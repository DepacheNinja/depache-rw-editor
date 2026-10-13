namespace RwEditor.Core.Settings;

/// <summary>Who receives a spawn group (DESIGN_EDITOR 2.3). There is deliberately no "everyone, ignore the toggle" mode.</summary>
public enum GroupMode
{
    /// <summary>Follows the match's "Extra units human players only" toggle, like the four default extras.</summary>
    FollowToggle,
    /// <summary>Human players only, always (AI teams never get it).</summary>
    HumansOnly,
}

/// <summary>State of one EXISTING (shipped) start option in the selector. Shown = default (not stored).</summary>
public enum OptionState
{
    Shown,
    /// <summary>Toggled off in the editor: its On/Off buttons are hidden (generated ugHide tag xh&lt;tag&gt;).</summary>
    Hidden,
    /// <summary>Deleted by the user. The option lives in a LOCKED file, so in the game this is the same xh&lt;tag&gt; hide as Hidden; the difference is
    /// kept in the settings only (the owner asked for both "toggle" and "delete", DESIGN_SPEC 6c).</summary>
    Deleted,
}

/// <summary>One map offset relative to the team's anchor, in game units.</summary>
public readonly record struct Offset(int X, int Y);

/// <summary>One unit type in a group with how many to spawn. Offsets are either given for every copy or not at all (then the auto-layout places them).</summary>
public sealed class GroupUnit
{
    public string Unit { get; set; } = "";
    public int Count { get; set; } = 1;
    public List<Offset>? Offsets { get; set; }

    public GroupUnit Clone() => new() { Unit = Unit, Count = Count, Offsets = Offsets?.ToList() };
}

/// <summary>One user spawn group. Slot 1..8 fixes its tags (ug01..ug08), so a group keeps its identity when others are added or removed.</summary>
public sealed class SpawnGroup
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public GroupMode Mode { get; set; } = GroupMode.FollowToggle;
    /// <summary>On by default when a match starts (the generated ugDef action adds the tag at creation).</summary>
    public bool DefaultOn { get; set; }
    /// <summary>False = toggled off in the editor: kept in the settings, not generated into the mod. Deleting a group removes it from the list.</summary>
    public bool Enabled { get; set; } = true;
    public List<GroupUnit> Units { get; set; } = new();

    public string Tag => "ug" + Slot.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    public SpawnGroup Clone() => new() { Slot = Slot, Name = Name, Mode = Mode, DefaultOn = DefaultOn, Enabled = Enabled, Units = Units.Select(u => u.Clone()).ToList() };
}

/// <summary>How a stat edit was written into the mod copy (phase E1b-2).</summary>
public enum StatWriteKind
{
    /// <summary>The value text of an existing line was replaced.</summary>
    Patch,
    /// <summary>A "key: value" line was added to an existing section of the file.</summary>
    Insert,
    /// <summary>A new section with one "key: value" line was added at the end of the file.</summary>
    InsertSection,
}

/// <summary>
/// One stat edit applied to the copy (DESIGN_EDITOR 1.2 "every patch is recorded ... (file, section, key, old, new, mode)"). Enough to revert it exactly
/// (revert-defaults per key / unit / mod area / all) and, later, to re-apply it to a newer mod version: an edit applies only where <see cref="OldText"/>
/// is still found.
/// </summary>
public sealed class StatEdit
{
    /// <summary>Order in which edits were applied (reverts run newest first).</summary>
    public int Seq { get; set; }
    public string Unit { get; set; } = "";
    public string UnitFile { get; set; } = "";
    /// <summary>Mod area of the unit (main, fe, moo, vte, depache).</summary>
    public string Area { get; set; } = "";
    /// <summary>Dictionary entry id, e.g. core.maxHp or core.price.credits.</summary>
    public string StatId { get; set; } = "";
    /// <summary>The merged section of the unit that was edited.</summary>
    public string Section { get; set; } = "";
    /// <summary>"unit" (this unit only) or "template" (a shared line; see AffectedUnits).</summary>
    public string Mode { get; set; } = "unit";
    public StatWriteKind WriteKind { get; set; }
    /// <summary>The file that was written (the unit's own file, or the template's file).</summary>
    public string File { get; set; } = "";
    /// <summary>The section header in <see cref="File"/> the line belongs to.</summary>
    public string FileSection { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>The whole value text before the edit (null for an inserted line).</summary>
    public string? OldText { get; set; }
    /// <summary>The whole value text written.</summary>
    public string NewText { get; set; } = "";
    /// <summary>An insert at the very end of a file without a final line break had to give the previous last line one; a revert takes it away again.</summary>
    public bool EolAdded { get; set; }
    /// <summary>Loadable units whose value changed with this edit (1 for a unit-only edit).</summary>
    public int AffectedUnits { get; set; } = 1;

    public StatEdit Clone() => (StatEdit)MemberwiseClone();
}

/// <summary>
/// One STAGED stat change (phase E3): typed by the user, checked, listed in "Pending changes", and NOT yet written into the mod. "Apply changes" turns
/// every pending change into <see cref="StatEdit"/> records through one plan / backup / write (Core <c>StatBatch</c>). Kept in the settings file so a
/// reopened copy still shows what was staged.
/// </summary>
public sealed class PendingStatEdit
{
    public string Unit { get; set; } = "";
    public string UnitFile { get; set; } = "";
    /// <summary>Dictionary entry id, e.g. core.maxHp.</summary>
    public string StatId { get; set; } = "";
    /// <summary>The merged section of the unit (core, turret_1, projectile_2...).</summary>
    public string Section { get; set; } = "";
    /// <summary>"unit" (this unit only) or "template" (the shared line, after the user confirmed the list of units it changes).</summary>
    public string Mode { get; set; } = "unit";
    /// <summary>The value text handed to the stat patcher (e.g. "13000", "40s", "30").</summary>
    public string Value { get; set; } = "";
    /// <summary>Null for a change the user made on this unit; the unit's name when the change was copied to it by "this unit + its upgrade levels /
    /// versions" from that unit.</summary>
    public string? FromFamilyOf { get; set; }

    public PendingStatEdit Clone() => (PendingStatEdit)MemberwiseClone();

    /// <summary>Same target (unit file + stat + section): a newer pending change of the same target replaces the older one.</summary>
    public bool SameTarget(PendingStatEdit o) =>
        UnitFile.Equals(o.UnitFile, StringComparison.OrdinalIgnoreCase) && Unit == o.Unit && StatId.Equals(o.StatId, StringComparison.OrdinalIgnoreCase)
        && Section.Equals(o.Section, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The base mod a settings file was made against (DESIGN_SPEC 6c: base mod version + locked-file manifest reference). Profile-ready.</summary>
public sealed record BaseModRef(string ModVersion, int EditorSupportSchema, int LockedFileCount, string LockedManifestSha256);

/// <summary>
/// The editor's settings for the start options (schema 1). Serialised canonically by <see cref="SettingsJson"/>. Pure data plus the revert operations;
/// validation lives in <see cref="Validation.SettingsValidator"/>.
/// </summary>
public sealed class EditorSettings
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public BaseModRef? BaseMod { get; set; }
    public List<SpawnGroup> SpawnGroups { get; set; } = new();
    /// <summary>Non-default states of existing start options, by option tag. A tag that is absent is Shown.</summary>
    public SortedDictionary<string, OptionState> StartOptions { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Stat edits applied to the copy, in the order they were applied (E1b-2). Optional member of schema 1: absent = none, so every E1b-1
    /// settings file reads and hashes exactly as before.</summary>
    public List<StatEdit> StatEdits { get; set; } = new();
    /// <summary>Staged stat changes not written yet (E3), in the order they were staged. Optional member of schema 1: absent = none, so older files read
    /// and hash exactly as before.</summary>
    public List<PendingStatEdit> PendingStatEdits { get; set; } = new();
    /// <summary>E5b: what the user wants changed in the locked option files (numbers and option text). Optional member of schema 1: absent = nothing.</summary>
    public OptionTuning Tuning { get; set; } = new();
    /// <summary>E5b: the lines of locked files the editor rewrote for <see cref="Tuning"/>, in file / line order. Derived at apply time, never typed; the
    /// locked-file check and the "put back" read it. Optional member of schema 1.</summary>
    public List<OptionEdit> OptionEdits { get; set; } = new();

    /// <summary>Stages a change: an older pending change of the same target is replaced (the newest value wins), then the change is added at the end.</summary>
    public void Stage(PendingStatEdit p)
    {
        PendingStatEdits.RemoveAll(x => x.SameTarget(p));
        PendingStatEdits.Add(p);
    }

    /// <summary>The shipped behaviour: no groups, nothing hidden. Generating from it gives the "empty" include files.</summary>
    public static EditorSettings Default() => new();

    public EditorSettings Clone() => new()
    {
        Schema = Schema,
        BaseMod = BaseMod,
        SpawnGroups = SpawnGroups.Select(g => g.Clone()).ToList(),
        StartOptions = new SortedDictionary<string, OptionState>(StartOptions, StringComparer.Ordinal),
        StatEdits = StatEdits.Select(e => e.Clone()).ToList(),
        PendingStatEdits = PendingStatEdits.Select(e => e.Clone()).ToList(),
        Tuning = Tuning.Clone(),
        OptionEdits = OptionEdits.Select(e => e.Clone()).ToList(),
    };

    public int NextStatSeq => StatEdits.Count == 0 ? 1 : StatEdits.Max(e => e.Seq) + 1;

    public OptionState GetOptionState(string tag) => StartOptions.TryGetValue(tag, out var s) ? s : OptionState.Shown;

    /// <summary>Sets an option's state; Shown removes the entry (default = not stored, so equal settings serialise equally).</summary>
    public void SetOptionState(string tag, OptionState state)
    {
        if (state == OptionState.Shown) StartOptions.Remove(tag);
        else StartOptions[tag] = state;
    }

    // ---- revert to defaults (DESIGN_SPEC 6c: all, and per item / section) ----

    /// <summary>Start options back to the shipped behaviour (groups and option states). BaseMod is kept (it describes the copy, not a choice).
    /// Stat edits are NOT touched here: they live in the mod files and are reverted with the files (rwedit revert-stats).</summary>
    public void RevertAll() { SpawnGroups.Clear(); StartOptions.Clear(); Tuning = new OptionTuning(); }

    /// <summary>Section 4 ("start unit options") back to the shipped behaviour: no user groups, every default option shown.</summary>
    public void RevertSection4(IEnumerable<string> section4Tags)
    {
        SpawnGroups.Clear();
        foreach (var t in section4Tags) StartOptions.Remove(t);
    }

    /// <summary>Several existing options back to Shown (one menu section, or all); groups are not touched.</summary>
    public void RevertOptions(IEnumerable<string> tags)
    {
        foreach (var t in tags) StartOptions.Remove(t);
    }

    /// <summary>One existing option back to Shown.</summary>
    public void RevertOption(string tag) => StartOptions.Remove(tag);

    /// <summary>One user group back to the default, i.e. removed (the shipped mod has no user groups). Returns false if no group has that slot.</summary>
    public bool RevertGroup(int slot) => SpawnGroups.RemoveAll(g => g.Slot == slot) > 0;
}
