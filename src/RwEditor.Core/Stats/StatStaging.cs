using RwEditor.Core.Browse;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;

namespace RwEditor.Core.Stats;

/// <summary>A family member the change could be copied to (same family, same section, same value now).</summary>
public sealed record FamilyMember(UnitType Unit, string Relation);

/// <summary>A family member that is NOT included, with the reason in plain words.</summary>
public sealed record FamilySkip(UnitType Unit, string Relation, string Reason);

/// <summary>
/// What the editor knows about one stat of one unit before a change is staged (E3): where the value comes from, whether that line is shared and with
/// which units, and which family members hold the same value now.
/// </summary>
public sealed record StageInfo(UnitType Unit, StatEntry Entry, string Section, StatRef Current, Origin? Origin, IReadOnlyList<UnitType> SharingUnits,
    IReadOnlyList<FamilyMember> FamilyCandidates, IReadOnlyList<FamilySkip> FamilySkipped)
{
    /// <summary>The value comes from a line that other units read too (a shared template, or this unit's file copied by others): the user must choose
    /// between "only this unit" and "every unit that shares it".</summary>
    public bool Shared => SharingUnits.Count > 1;
}

/// <summary>The result of checking a change before it is staged: the pending entries it becomes (1, or 1 + family members) and each one's full check.</summary>
public sealed record StageCheck(IReadOnlyList<StatBatchItem> Items)
{
    public bool Ok => Items.Count > 0 && Items.All(i => i.Ok);
    public IReadOnlyList<PendingStatEdit> Pendings => Items.Select(i => i.Pending).ToList();
    public IEnumerable<Finding> Warnings => Items.SelectMany(i => i.Warnings);
    public IEnumerable<string> Problems => Items.Where(i => !i.Ok).SelectMany(i => i.Errors.Select(e => (Items.Count > 1 ? i.Pending.Unit + ": " : "") + StatText.Plain(e)));
}

/// <summary>
/// Staging logic shared by the app and the CLI (E3). A change is checked with the FULL single-edit plan (<see cref="StatPatcher.Plan"/>, nothing written)
/// against the copy as it is on disk, so the user learns at once whether "this unit only" is possible, which units a shared change reaches, and every
/// limit / rule problem. "Apply changes" checks all staged changes together again (<see cref="StatBatch"/>).
/// Family scope ("this unit + its upgrade levels / versions"): only members of the unit's family (never the shared / helper part lists) related as an
/// upgrade, water / land form, nano build, based-on / base-of or switch (NOT attached parts or spawned helpers), not locked, having the same section with
/// the SAME editable value now; each becomes its own "this unit only" change and EVERY one must pass the full check, otherwise the family choice is refused.
/// </summary>
public static class StatStaging
{
    static readonly FamilyRelation[] VersionRelations =
    {
        FamilyRelation.Root, FamilyRelation.WaterVersion, FamilyRelation.LandVersion, FamilyRelation.NanoBuild, FamilyRelation.Upgrade,
        FamilyRelation.UpgradedFrom, FamilyRelation.BasedOn, FamilyRelation.Converts, FamilyRelation.UpgradesInto, FamilyRelation.BaseOf,
    };

    public static StageInfo Info(UnitModel model, StatDictionary dict, UnitFamilies? families, UnitType unit, StatEntry entry, string section)
    {
        var m = model.Merge(unit);
        var cur = StatReader.Read(m, entry, section);
        var origin = cur.Raw?.Origin;
        IReadOnlyList<UnitType> sharing = origin == null ? new[] { unit } : StatView.UnitsReadingLine(model, origin.File, origin.Line);
        if (sharing.Count == 0) sharing = new[] { unit };
        var cands = new List<FamilyMember>();
        var skipped = new List<FamilySkip>();
        if (families != null && cur.Editable && cur.Number is double mine)
        {
            var fam = families.FamilyOf(unit);
            if (!fam.IsBucket && fam.Root != null)
            {
                var all = new List<(UnitType U, FamilyRelation R, string Label)> { (fam.Root, FamilyRelation.Root, "the family's main unit") };
                all.AddRange(fam.Members.Select(e => (e.Unit, e.Relation, e.Label)));
                foreach (var (u, rel, label) in all)
                {
                    if (u.RelPath.Equals(unit.RelPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!VersionRelations.Contains(rel)) continue;   // attached parts / spawned helpers are different things, never included
                    string? why = null;
                    var mm = model.Merge(u);
                    if (u.Locked) why = "locked (mod machinery)";
                    else if (model.UnitsNamed(u.Name).Count > 1) why = "its name is used by two files";
                    else if (mm.Section(section) == null) why = $"has no {section} section";
                    else if (LockPolicy.Approve(dict, entry, entry.Id, model, u, mm.Section(section)!.Name) is Finding f) why = StatText.Plain(f);
                    else
                    {
                        var r = StatReader.Read(mm, entry, mm.Section(section)!.Name);
                        if (!r.Editable) why = "its value is read-only (" + StatText.Plain(r.ReadOnlyReason!) + ")";
                        else if (r.Number is not double n) why = "the value is not set";
                        else if (Math.Abs(n - mine) > 1e-9 * Math.Max(1, Math.Abs(mine))) why = $"different value now ({StatText.Value(entry, n)})";
                    }
                    if (why == null) cands.Add(new FamilyMember(u, label));
                    else skipped.Add(new FamilySkip(u, label, why));
                }
            }
        }
        return new StageInfo(unit, entry, section, cur, origin, sharing, cands, skipped);
    }

    /// <summary>
    /// Updates the copy's pending list in its settings file (one write; the folder guard and marker are required like for any write): removes the entries
    /// matching <paramref name="remove"/>, then stages <paramref name="add"/> (a newer change of the same target replaces the older one). Returns the saved
    /// settings. Nothing in the mod files changes.
    /// </summary>
    public static EditorSettings SaveStaged(string copyRoot, IEnumerable<PendingStatEdit>? add, Func<PendingStatEdit, bool>? remove = null, bool allowGitTree = false)
    {
        string root = Apply.FolderGuard.Check(copyRoot, allowGitTree);
        string path = Mod.CopyFiles.SettingsPath(root);
        var (settings, _) = StatPipeline.LoadSettings(path);
        if (remove != null) settings.PendingStatEdits.RemoveAll(p => remove(p));
        if (add != null) foreach (var p in add) settings.Stage(p.Clone());
        StatPipeline.WriteSettings(path, settings);
        return settings;
    }

    /// <summary>
    /// Checks one change (and, with <paramref name="family"/>, the same change on every family candidate as "this unit only") with the full plan against
    /// the model as it is now. Nothing is written. A family change is only usable when every item passes.
    /// </summary>
    public static StageCheck Check(UnitModel model, StatDictionary dict, PendingStatEdit change, Func<string, byte[]> readDisk, IReadOnlyList<FamilyMember>? family = null)
    {
        var list = new List<PendingStatEdit> { change };
        if (family != null)
            foreach (var f in family)
                list.Add(new PendingStatEdit
                {
                    Unit = f.Unit.Name, UnitFile = f.Unit.RelPath, StatId = change.StatId, Section = model.Merge(f.Unit).Section(change.Section)?.Name ?? change.Section,
                    Mode = "unit", Value = change.Value, FromFamilyOf = change.Unit,
                });
        // checked TOGETHER (one batch plan): the family members' edits must also hold next to each other
        var plan = StatBatch.Plan(model, dict, list, readDisk);
        var items = plan.Items.ToList();
        if (plan.BatchFindings.Any(f => f.Severity == Severity.Error))
            items[0] = items[0] with { Findings = items[0].Findings.Concat(plan.BatchFindings.Where(f => f.Severity == Severity.Error)).ToList() };
        return new StageCheck(items);
    }
}
