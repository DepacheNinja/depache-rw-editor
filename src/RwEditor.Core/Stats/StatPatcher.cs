using RwEditor.Core.Ini;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.Core.Stats;

public enum StatEditMode
{
    /// <summary>Change only this unit: patch its own line when nothing else uses that line, otherwise add the key to the unit's own section.</summary>
    UnitOnly,
    /// <summary>Patch the line the value comes from, wherever it is (a shared template), after listing every unit it changes. Needs an explicit flag.
    /// A key that is not set anywhere is added to the unit's own file instead (the units that copy that file inherit it).</summary>
    Template,
}

/// <param name="Unit">Unit name (exact, case-sensitive like the game).</param>
/// <param name="Key">Dictionary id (core.maxHp, turret.delay, core.price.credits) or a key that names one entry.</param>
/// <param name="Value">New value: a number; for time keys frames or "Ns"; for build time "Ns" or a per-frame number; for price fields a number.</param>
/// <param name="Section">Section for turret_ / projectile_ / action_ keys (core / movement / attack are implied).</param>
public sealed record StatEditRequest(string Unit, string Key, string Value, StatEditMode Mode = StatEditMode.UnitOnly, string? Section = null);

/// <summary>A loadable unit whose value of the edited key changes, with the sections that change and the value text before / after (first section).</summary>
public sealed record AffectedUnit(UnitType Unit, IReadOnlyList<string> Sections, string? Before, string? After);

/// <summary>Everything decided in RAM for one stat edit; nothing is written by planning.</summary>
public sealed record StatEditPlan(
    StatEditRequest Request, StatEntry Entry, UnitType Unit, string Section, StatRef Before, double NewNumber, string NewRawValue,
    StatEdit Record, IReadOnlyList<AffectedUnit> Affected, IReadOnlyList<Finding> Warnings,
    IReadOnlyDictionary<string, byte[]> NewBytes, IReadOnlyDictionary<string, byte[]?> CurrentBytes, IReadOnlyList<string> NewLintFindings)
{
    public bool Changes => NewBytes.Any(kv => CurrentBytes[kv.Key] is not byte[] cur || !cur.AsSpan().SequenceEqual(kv.Value));
}

/// <summary>A refused stat edit with every reason found (all of them, so a user sees the whole picture at once).</summary>
public sealed class StatEditRefusedException : EditorRefusalException
{
    public StatEditRefusedException(IReadOnlyList<Finding> findings)
        : base(findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Code ?? "STAT_EDIT_REFUSED",
            "the stat edit is refused:\n  " + string.Join("\n  ", findings.Where(f => f.Severity == Severity.Error)))
    { Findings = findings; }

    public IReadOnlyList<Finding> Findings { get; }
}

/// <summary>
/// Plans one stat edit on an in-RAM model (DESIGN_EDITOR 1.2): approve through <see cref="LockPolicy"/>, parse and range-check the new value against the
/// dictionary, choose the write (patch the own line / add the key to the own section / patch the template line), build the new bytes of the ONE file
/// that changes (lossless <see cref="IniText"/>), re-resolve every unit that uses that file to list exactly which units change, check the cross-key
/// rules on each of them (no new violation) and lint the planned mod (0 new findings). Pure: reads only through <paramref name="readBytes"/>.
/// </summary>
public static class StatPatcher
{
    /// <summary>Sections a "this unit only" edit may create when the unit's own file lacks them (one per unit, so their order does not matter).
    /// turret_ / projectile_ / action_ sections are never created: a new own section changes the order in which the engine appends inherited
    /// sections (C-026: own sections first), which may change turret numbering (not verified, PH-075).</summary>
    public static readonly IReadOnlyList<string> CreatableSections = new[] { "core", "movement", "attack" };

    /// <param name="lint">False only for a batch that lints its final result once (<see cref="StatBatch"/>); single edits always lint.</param>
    /// <param name="chainIndex">The model whose reverse index (which units read which file) is used to find the affected units. Stat edits never change a
    /// copyFrom chain (copyFrom / name / dont_load are never editable), so a batch passes its ORIGINAL model here instead of re-merging every unit of each
    /// intermediate model. Default: <paramref name="model"/>.</param>
    public static StatEditPlan Plan(UnitModel model, StatDictionary dict, StatEditRequest req, Func<string, byte[]> readBytes, bool lint = true, UnitModel? chainIndex = null)
    {
        var errors = new List<Finding>();
        var warnings = new List<Finding>();
        void Err(string c, string m) => errors.Add(new Finding(Severity.Error, c, m));
        void Warn(string c, string m) => warnings.Add(new Finding(Severity.Warning, c, m));
        StatEditRefusedException Refused() => new(errors.Concat(warnings).ToList());

        // ---- unit
        var named = model.UnitsNamed(req.Unit);
        if (named.Count == 0)
        {
            var near = model.FindIgnoreCase(req.Unit);
            Err("UNIT_UNKNOWN", near != null ? $"no loadable unit '{req.Unit}'; did you mean '{near.Name}' ({near.RelPath})? Names are case-sensitive." : $"no loadable unit named '{req.Unit}'");
            throw Refused();
        }
        if (named.Count > 1)
        {
            Err("UNIT_AMBIGUOUS", $"'{req.Unit}' is defined by {named.Count} loadable files ({string.Join(", ", named.Select(u => u.RelPath))}); the editor does not guess which one the game uses");
            throw Refused();
        }
        var unit = named[0];

        // ---- entry + section
        var entry = dict.Resolve(req.Key, out var keyError);
        if (entry == null) { Err("NOT_IN_DICTIONARY", keyError!); throw Refused(); }
        string? section = req.Section ?? entry.FixedSection;
        if (section == null) { Err("SECTION_REQUIRED", $"{entry.Id} needs --section (one of the unit's [{string.Join("], [", entry.Sections)}] sections)"); throw Refused(); }
        var merged = model.Merge(unit);
        var mergedSection = merged.Section(section);
        if (mergedSection != null) section = mergedSection.Name;   // the spelling the unit uses
        else if (!CreatableSections.Contains(section, StringComparer.OrdinalIgnoreCase))
        {
            Err("SECTION_NOT_IN_UNIT", $"{unit.Name} has no [{section}] section");
            throw Refused();
        }

        var approval = LockPolicy.Approve(dict, entry, req.Key, model, unit, section);
        if (approval != null) { errors.Add(approval); throw Refused(); }
        if (merged.UnresolvedParents.Count > 0)
            Warn("CHAIN_UNRESOLVED", $"{unit.Name} copies from files the editor cannot read ({string.Join("; ", merged.UnresolvedParents)}); values from them are unknown here");

        // ---- current value
        var before = StatReader.Read(merged, entry, section);
        if (!before.Editable) { Err("VALUE_READ_ONLY", $"{unit.Name} [{section}] {entry.Key}: {before.ReadOnlyReason}"); throw Refused(); }

        // ---- new value
        if (!ParseNew(entry, req.Value, out double newNumber, out string fieldText, out string? parseError)) { Err("VALUE_INVALID", parseError!); throw Refused(); }
        if (newNumber < entry.HardMin || newNumber > entry.HardMax || double.IsNaN(newNumber))
            Err("OUT_OF_RANGE", $"{entry.Id} = {req.Value} ({StatDictionary.Describe(entry, newNumber)}) is outside the allowed range {StatDictionary.Describe(entry, entry.HardMin)} .. {StatDictionary.Describe(entry, entry.HardMax)}");
        if (entry.WarnMin is double wn && newNumber < wn) Warn("SOFT_LIMIT", $"{entry.Id} below {StatDictionary.Describe(entry, wn)}");
        if (entry.WarnMax is double wx && newNumber > wx) Warn("SOFT_LIMIT", $"{entry.Id} above {StatDictionary.Describe(entry, wx)}");
        if (before.Number is double b && b > 0)
        {
            if (entry.RelWarnMin is double rn && newNumber < b * rn) Warn("SOFT_LIMIT_RELATIVE", $"{entry.Id} goes below {rn} x the current value {StatDictionary.Describe(entry, b)}");
            if (entry.RelWarnMax is double rx && newNumber > b * rx) Warn("SOFT_LIMIT_RELATIVE", $"{entry.Id} goes above {rx} x the current value {StatDictionary.Describe(entry, b)}");
        }
        if (errors.Count > 0) throw Refused();

        string newRaw = entry.Field == null ? fieldText : StatValue.WithPriceField(before.Raw?.Value.Trim() ?? "", entry.Field, fieldText)!;
        if (before.Raw != null && before.Raw.Value.Trim() == newRaw)
        {
            Err("NO_CHANGE", $"{unit.Name} [{section}] {entry.Key} is already '{newRaw}'");
            throw Refused();
        }

        // ---- choose the write
        string keyText = before.Raw?.Key ?? entry.Key;
        var origin = before.Raw?.Origin;
        string file;
        StatWriteKind kind;
        int? patchLine = null;
        string fileSection;
        if (req.Mode == StatEditMode.Template && origin != null)
        {
            file = origin.File; kind = StatWriteKind.Patch; patchLine = origin.Line; fileSection = origin.FileSection;
        }
        // (a key that is not set anywhere has no shared line: in Template mode it is added to the unit's own file like a unit-only change, and the
        //  units that copy that file inherit it; they are listed in Affected and the caller must have confirmed them)
        else if (origin != null && origin.Kind == OriginKind.Own && origin.ViaSection == null && origin.FileSection.Equals(section, StringComparison.OrdinalIgnoreCase))
        {
            file = origin.File; kind = StatWriteKind.Patch; patchLine = origin.Line; fileSection = origin.FileSection;
        }
        else
        {
            file = unit.RelPath;
            var ownDoc = model.Doc(unit.RelPath)!;
            var ownSec = ownDoc.Sections.LastOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
            if (ownSec != null) { kind = StatWriteKind.Insert; fileSection = ownSec.Name; }
            else if (CreatableSections.Contains(section, StringComparer.OrdinalIgnoreCase)) { kind = StatWriteKind.InsertSection; fileSection = section; }
            else
            {
                Err("SECTION_NOT_OWN", $"[{section}] of {unit.Name} is inherited as a whole ({origin}); a unit-only edit would have to create a new [{section}] in {unit.RelPath}, which may reorder its turrets/projectiles (PH-075). Use --template to change the shared line, or add the section by hand.");
                throw Refused();
            }
        }
        if (!LockPolicy.IsStatWritableFile(file, model.LockGlobs))
        {
            Err("FILE_LOCKED", $"the value comes from {file}, which is locked (mod machinery) or not an editable file");
            throw Refused();
        }

        // ---- new bytes of the one file
        var cur = readBytes(file);
        var text = IniText.Parse(cur, file);
        if (!string.Equals(text.Text, model.Snapshot.GetText(file), StringComparison.Ordinal))
        {
            Err("FILE_CHANGED", $"{file} changed on disk after the mod was loaded; reload and try again");
            throw Refused();
        }
        bool eolAdded = false;
        IniText next;
        int writtenLine;
        switch (kind)
        {
            case StatWriteKind.Patch:
            {
                int idx = patchLine!.Value - 1;
                var span = text.GetValueSpan(idx);
                if (span == null || !span.Key.Equals(keyText, StringComparison.OrdinalIgnoreCase) || span.Value != (before.Raw?.Value ?? ""))
                {
                    Err("ORIGIN_MISMATCH", $"{file}:{patchLine} is not the line '{keyText}: {before.Raw?.Value}' (internal check)");
                    throw Refused();
                }
                if (span.Value.StartsWith("\"\"\"", StringComparison.Ordinal)) { Err("VALUE_MULTILINE", $"{file}:{patchLine} holds a multi-line value; read-only"); throw Refused(); }
                next = text.WithValue(idx, newRaw);
                writtenLine = patchLine.Value;
                break;
            }
            case StatWriteKind.Insert:
            {
                var ownDoc = IniDocument.Parse(text.Text, file, IniDialect.Catalog);
                var sec = ownDoc.Sections.Last(s => s.Name.Equals(fileSection, StringComparison.OrdinalIgnoreCase));
                // right after the section header: always a single physical line (a multi-line value can never be split this way)
                (next, eolAdded) = text.WithLineInserted(sec.Line - 1, keyText + ": " + newRaw);
                writtenLine = sec.Line + 1;
                break;
            }
            default:
            {
                int last = text.LastContentLine;
                (var withHeader, eolAdded) = text.WithLineInserted(last, "[" + fileSection + "]");
                (next, _) = withHeader.WithLineInserted(last + 1, keyText + ": " + newRaw);
                writtenLine = last + 3;
                break;
            }
        }
        var newBytes = next.ToBytes();
        var model2 = model.WithFiles(new Dictionary<string, string> { [file] = next.Text });

        // ---- the edit must take effect on the target unit
        var merged2 = model2.Merge(model2.UnitsNamed(unit.Name).Single(u => u.RelPath.Equals(unit.RelPath, StringComparison.OrdinalIgnoreCase)));
        var after = StatReader.Read(merged2, entry, section);
        if (!after.Editable || after.Number is not double an || Math.Abs(an - newNumber) > 1e-9 * Math.Max(1, Math.Abs(newNumber)))
        {
            Err("EDIT_NOT_EFFECTIVE", $"after the planned write {unit.Name} [{section}] {entry.Key} would read '{after.Raw?.Value}', not the new value (internal check)");
            throw Refused();
        }

        // ---- who else changes
        var affected = Affected(model, model2, file, entry.Key, chainIndex);
        if (!affected.Any(a => a.Unit.RelPath.Equals(unit.RelPath, StringComparison.OrdinalIgnoreCase)))
        {
            Err("EDIT_NOT_EFFECTIVE", "the planned write does not change the unit itself (internal check)");
            throw Refused();
        }
        var others = affected.Where(a => !a.Unit.RelPath.Equals(unit.RelPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (req.Mode == StatEditMode.UnitOnly && others.Count > 0)
            Err("NOT_UNIT_ONLY", $"{file}:{writtenLine} is shared: changing it also changes {others.Count} other unit(s): {string.Join(", ", others.Take(20).Select(a => a.Unit.Name))}{(others.Count > 20 ? ", ..." : "")}. Repeat with --template to change all {affected.Count}, or edit a unit whose own line it is.");
        var lockedHit = affected.Where(a => a.Unit.Locked).ToList();
        if (lockedHit.Count > 0)
            Err("TEMPLATE_REACHES_LOCKED", $"the change would also change locked unit(s) {string.Join(", ", lockedHit.Take(10).Select(a => $"{a.Unit.Name} ({a.Unit.RelPath})"))}");

        // ---- cross-key rules on every affected unit: no new violation
        foreach (var a in affected)
        {
            var m1 = model.Merge(a.Unit);
            var m2 = model2.Merge(model2.UnitsNamed(a.Unit.Name).First(u => u.RelPath.Equals(a.Unit.RelPath, StringComparison.OrdinalIgnoreCase)));
            foreach (var rid in entry.Rules)
            {
                var rule = dict.Rules.First(r => r.Id == rid);
                var was = StatRules.Evaluate(m1, rid).ToHashSet(StringComparer.Ordinal);
                foreach (var v in StatRules.Evaluate(m2, rid).Where(v => !was.Contains(v)))
                {
                    if (rule.Severity == Severity.Error) Err("RULE_" + rid, $"{a.Unit.Name}: {v}");
                    else Warn("RULE_" + rid, $"{a.Unit.Name}: {v}");
                }
            }
        }
        // values elsewhere in the unit computed from this key (${...}) change with it
        foreach (var s in merged.Sections)
            foreach (var v in s.Values)
                if (StatValue.IsExpression(v.Value) && v.Value.Contains(entry.Key, StringComparison.OrdinalIgnoreCase) && !v.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
                    Warn("EXPRESSION_DEPENDS", $"{unit.Name} [{s.Name}] {v.Key}: {v.Value} may be computed from {entry.Key}");

        var lintNew = lint ? RwLint.NewFindings(model.Snapshot, model2.Snapshot) : new List<string>();
        if (lintNew.Count > 0) Err("LINT_NEW", "the edit would add lint findings: " + string.Join("; ", lintNew));
        if (errors.Count > 0) throw Refused();

        var record = new StatEdit
        {
            Unit = unit.Name, UnitFile = unit.RelPath, Area = unit.Area, StatId = entry.Id, Section = section,
            Mode = req.Mode == StatEditMode.Template ? "template" : "unit", WriteKind = kind, File = file, FileSection = fileSection, Key = keyText,
            OldText = kind == StatWriteKind.Patch ? before.Raw!.Value : null, NewText = newRaw, EolAdded = eolAdded, AffectedUnits = affected.Count,
        };
        return new StatEditPlan(req, entry, unit, section, before, newNumber, newRaw, record, affected, warnings,
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { [file] = newBytes },
            new Dictionary<string, byte[]?>(StringComparer.Ordinal) { [file] = cur }, lintNew);
    }

    /// <summary>Units (of either model) whose value text of <paramref name="key"/> differs in any section between the two models.</summary>
    public static IReadOnlyList<AffectedUnit> Affected(UnitModel before, UnitModel after, string changedFile, string key, UnitModel? chainIndex = null)
    {
        // a stat edit never changes which files a unit reads (copyFrom / name / dont_load are never editable), so the candidates are the units whose
        // chain holds the changed file; with a chain index given (a batch) its reverse index is reused instead of merging every unit of 'after'
        var cand = (chainIndex != null ? chainIndex.UnitsUsingFile(changedFile) : before.UnitsUsingFile(changedFile).Concat(after.UnitsUsingFile(changedFile)))
            .GroupBy(u => u.RelPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(u => u.RelPath, StringComparer.OrdinalIgnoreCase);
        var list = new List<AffectedUnit>();
        foreach (var u in cand)
        {
            var ub = before.UnitsInFile(u.RelPath).FirstOrDefault(x => x.Name == u.Name) ?? before.UnitsInFile(u.RelPath).FirstOrDefault();
            var ua = after.UnitsInFile(u.RelPath).FirstOrDefault(x => x.Name == u.Name) ?? after.UnitsInFile(u.RelPath).FirstOrDefault();
            var m1 = ub == null ? null : before.Merge(ub);
            var m2 = ua == null ? null : after.Merge(ua);
            var names = (m1?.Sections.Select(s => s.Name) ?? Enumerable.Empty<string>()).Concat(m2?.Sections.Select(s => s.Name) ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var changed = new List<string>();
            string? b0 = null, a0 = null;
            foreach (var n in names)
            {
                string? vb = m1?.Get(n, key)?.Value, va = m2?.Get(n, key)?.Value;
                if (string.Equals(vb, va, StringComparison.Ordinal)) continue;
                if (changed.Count == 0) { b0 = vb; a0 = va; }
                changed.Add(n);
            }
            if (changed.Count > 0) list.Add(new AffectedUnit(ua ?? u, changed, b0, a0));
        }
        return list;
    }

    /// <summary>Parses the typed value for an entry: the canonical number (limits unit) and the text to write for the field / value.</summary>
    public static bool ParseNew(StatEntry e, string typed, out double number, out string text, out string? error)
    {
        number = 0; error = null;
        text = typed.Trim();
        if (text.StartsWith('+')) text = text[1..];
        if (text.Length == 0 || text.IndexOfAny(new[] { ',', '|', '=', ':', '$', '{', '}', '#', '"', '\r', '\n' }) >= 0)
        {
            error = $"'{typed}' is not a plain number";
            return false;
        }
        switch (e.Type)
        {
            case StatType.PartInt:
                if (!StatValue.TryInt(text, out var l) || l < int.MinValue || l > int.MaxValue) { error = $"{e.Id} needs a whole number (the game reads it with Integer.parseInt); got '{typed}'"; return false; }
                number = l; text = (e.Negate ? -l : l).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case StatType.Int:
                if (!StatValue.TryInt(text, out var li) || li < int.MinValue || li > int.MaxValue) { error = $"{e.Id} needs a whole number (the game reads it with Integer.parseInt); got '{typed}'"; return false; }
                number = li; text = li.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case StatType.Float:
            case StatType.PartFloat:
                if (!StatValue.TryFloat(text, out var f)) { error = $"{e.Id} needs a number; got '{typed}'"; return false; }
                number = f;
                // "negate" entries are written with the opposite sign (tech provided 5 -> "techLevel = -5")
                if (e.Negate) text = StatValue.Format(f == 0 ? 0 : -f);
                return true;
            case StatType.Time:
                if (!StatValue.TryTime(text, out var fr, out _)) { error = $"{e.Id} needs frames (e.g. 30) or seconds (e.g. 0.5s); got '{typed}'"; return false; }
                number = fr; return true;
            default:
                if (!StatValue.TryBuildSeconds(text, out var sec, out _)) { error = $"{e.Id} needs seconds (e.g. 20s) or a per-frame build progress number; got '{typed}'"; return false; }
                number = sec; return true;
        }
    }
}
