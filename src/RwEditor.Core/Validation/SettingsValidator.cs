using System.Text.RegularExpressions;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;

namespace RwEditor.Core.Validation;

/// <summary>
/// Every rule a settings file must pass before anything is generated (DESIGN_EDITOR 2.1, 2.3, 5.4 step 3). Pure: reads the settings and the in-RAM mod.
/// Limits: 8 groups, 12 units per group, 40 units per team, at most 9 visible option buttons in section 4 (O5 / PH-067), ids at most 13 characters and
/// toggle tags at most 10 (RwCatalog lint R1 / R1b, MaxIdSafe = 13), generated conditions at most 705 characters (E12).
/// </summary>
public static class SettingsValidator
{
    public const int MaxGroups = 8;
    public const int MaxUnitsPerGroup = 12;
    public const int MaxUnitsPerTeam = 40;
    public const int MaxSection4Buttons = 9;
    /// <summary>tools/RwCatalog/Lint.cs MaxIdSafe: the jar rejects ids longer than 15 and a 15-character id failed in practice, so ids stay at 13 or less.</summary>
    public const int MaxIdLength = 13;
    /// <summary>Lint R1b: the option templates build ids &lt;tag&gt;on / &lt;tag&gt;Off / &lt;tag&gt;Msg, so a toggle tag is at most 13 - 3 = 10 characters.</summary>
    public const int MaxTagLength = MaxIdLength - 3;
    /// <summary>Longest condition verified in owner matches (DESIGN_START_UNIT_CATALOG E12); every generated "autoTrigger: ..." line stays within it.</summary>
    public const int MaxConditionLine = 705;

    static readonly Regex UnitNameSyntax = new(@"^[A-Za-z0-9_\.]+$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<Finding> Validate(EditorSettings s, ModContext mod)
    {
        var f = new List<Finding>();
        void Err(string code, string msg) => f.Add(new Finding(Severity.Error, code, msg));
        void Warn(string code, string msg) => f.Add(new Finding(Severity.Warning, code, msg));

        if (s.Schema != EditorSettings.CurrentSchema) Err("SETTINGS_SCHEMA", $"settings schema {s.Schema} is not supported (expected {EditorSettings.CurrentSchema})");

        if (s.BaseMod != null)
        {
            if (s.BaseMod.ModVersion != mod.Support.ModVersion)
                Warn("BASEMOD_MISMATCH", $"the settings were made for mod version {s.BaseMod.ModVersion}; this copy is {mod.Support.ModVersion}");
            if (s.BaseMod.LockedManifestSha256 != mod.Support.LockedManifestSha256)
                Warn("BASEMOD_MISMATCH", "the settings were made against a different set of locked files (manifest hash differs)");
        }

        // ---- groups ----
        if (s.SpawnGroups.Count > MaxGroups) Err("GROUP_COUNT", $"{s.SpawnGroups.Count} groups; at most {MaxGroups}");
        foreach (var g in s.SpawnGroups)
            if (g.Slot < 1 || g.Slot > MaxGroups) Err("GROUP_SLOT", $"group '{g.Name}' has slot {g.Slot}; slots are 1..{MaxGroups}");
        foreach (var dup in s.SpawnGroups.GroupBy(g => g.Slot).Where(x => x.Count() > 1))
            Err("GROUP_SLOT_DUP", $"slot {dup.Key} is used by {dup.Count()} groups");

        foreach (var dup in s.SpawnGroups.GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            Err("GROUP_NAME_DUP", $"the group name '{dup.Key}' is used by {dup.Count()} groups (names must differ, ignoring case)");

        foreach (var g in s.SpawnGroups)
        {
            string label = $"group {g.Slot} '{g.Name}'";
            if (!NameRules.IsValidName(g.Name))
            {
                string sug = NameRules.Sanitize(g.Name);
                Err("GROUP_NAME_INVALID", $"{label}: the name must be 1-{NameRules.MaxNameLength} characters of letters, digits, space and -_.!?+'&() (no leading/trailing or double spaces)"
                    + (sug.Length > 0 && sug != g.Name ? $"; stored form would be '{sug}'" : ""));
            }
            var opt = mod.Options.FirstOrDefault(o => o.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
            if (opt != null) Err("GROUP_NAME_COLLIDES_OPTION", $"{label}: the name equals the existing option '{opt.Name}' ({opt.Tag})");

            if (g.Units.Count == 0) Err("GROUP_EMPTY", $"{label} has no units");
            foreach (var u in g.Units)
                if (u.Count < 1) Err("UNIT_COUNT", $"{label}: '{u.Unit}' count {u.Count}; at least 1");
            int entries = g.Units.Sum(u => Math.Max(0, u.Count));
            if (entries > MaxUnitsPerGroup) Err("GROUP_TOO_MANY_UNITS", $"{label} spawns {entries} units; at most {MaxUnitsPerGroup} per group");
            foreach (var dup in g.Units.GroupBy(u => u.Unit, StringComparer.Ordinal).Where(x => x.Count() > 1))
                Err("UNIT_DUP_ENTRY", $"{label} lists '{dup.Key}' {dup.Count()} times; use one entry with a count");

            foreach (var u in g.Units)
            {
                if (!UnitNameSyntax.IsMatch(u.Unit)) { Err("UNIT_NAME_SYNTAX", $"{label}: '{u.Unit}' is not a valid unit name (letters, digits, '_' and '.')"); continue; }
                var info = mod.FindUnit(u.Unit);
                if (info == null)
                {
                    var near = mod.FindUnitIgnoreCase(u.Unit);
                    if (near != null) Err("UNIT_CASE", $"{label}: no unit '{u.Unit}'; did you mean '{near.Name}' ({near.RelPath})? Names are case-sensitive.");
                    else Err("UNIT_UNKNOWN", $"{label}: no unit named '{u.Unit}' exists in this mod");
                    continue;
                }
                if (!info.Loadable) Err("UNIT_NOT_LOADABLE", $"{label}: '{u.Unit}' ({info.RelPath}) is a template ([core] dont_load: true), not a unit the game loads");
                if (info.Locked) Err("UNIT_LOCKED", $"{label}: '{u.Unit}' ({info.RelPath}) is in a locked area (mod machinery), it cannot be spawned by a group");
            }

            bool any = g.Units.Any(u => u.Offsets != null), all = g.Units.All(u => u.Offsets != null);
            if (any && !all) Err("OFFSETS_PARTIAL", $"{label}: either every unit has offsets or none has (then they are placed automatically)");
            foreach (var u in g.Units.Where(u => u.Offsets != null))
            {
                if (u.Offsets!.Count != u.Count) Err("OFFSETS_COUNT", $"{label}: '{u.Unit}' has count {u.Count} but {u.Offsets.Count} offsets");
                foreach (var o in u.Offsets)
                    if (Math.Abs(o.X) > Layout.MaxAbsOffset || Math.Abs(o.Y) > Layout.MaxAbsOffset)
                        Err("OFFSET_RANGE", $"{label}: '{u.Unit}' offset ({o.X}, {o.Y}) is outside +-{Layout.MaxAbsOffset}");
            }
            if (!any && g.Units.Count > 0 && g.Units.All(u => u.Count >= 1 && mod.FindUnit(u.Unit) != null)
                && g.Slot is >= 1 and <= MaxGroups
                && SpawnGroupGenerator.PlannedOffsets(g, s, mod, out var reason) == null)
                Err("LAYOUT_NO_FIT", $"{label}: automatic placement failed: {reason}. Place the units by hand (drag them in the picture) or use fewer units.");

            if (!g.Enabled && g.DefaultOn) Warn("DEFAULT_ON_DISABLED", $"{label} is on by default but toggled off in the editor, so it is not generated");
        }

        var active = SpawnGroupGenerator.ActiveGroups(s);
        int teamTotal = active.Sum(g => g.Units.Sum(u => Math.Max(0, u.Count)));
        if (teamTotal > MaxUnitsPerTeam) Err("TEAM_TOO_MANY_UNITS", $"the enabled groups spawn {teamTotal} units per team; at most {MaxUnitsPerTeam}");

        // units placed by hand may be put on top of each other: say so (a warning, the user decides)
        foreach (var hit in HandPlacementOverlaps(s, mod))
            Warn("OFFSETS_OVERLAP", hit);

        // ---- existing options (hide / delete) ----
        var hideable = mod.HideableOptions.Select(o => o.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var (tag, state) in s.StartOptions)
        {
            if (state == OptionState.Shown) continue;
            var o = mod.FindOption(tag);
            if (o == null) Err("OPTION_UNKNOWN", $"start option '{tag}' does not exist in this mod's selector");
            else if (!hideable.Contains(tag))
                Err("OPTION_NOT_HIDEABLE", ModContext.NeverHide.Contains(tag)
                    ? $"'{tag}' can never be hidden or deleted (it would be locked on or break the human-only toggle)"
                    : $"'{tag}' cannot be hidden or deleted in this editor version");
        }
        int shownDefaults4 = mod.Options.Count(o => o.TypeId == ModContext.Section4TypeId && s.GetOptionState(o.Tag) == OptionState.Shown);
        if (shownDefaults4 + active.Count > MaxSection4Buttons)
            Err("SECTION4_CAP", $"section 4 would show {shownDefaults4} default options + {active.Count} groups = {shownDefaults4 + active.Count} buttons; at most {MaxSection4Buttons} (hide default options or disable groups)");

        // ---- E5b: numbers and text of the shipped options (the locked option files, only through the catalog) ----
        try { f.AddRange(mod.LoadOptionModel(s.OptionEdits).Validate(s.Tuning, mod.OnSections)); }
        catch (EditorRefusalException e) { Err(e.Code, e.Message); }

        // ---- the editor's tags must be free in the rest of the mod ----
        foreach (var hit in IncludeRules.TagCollisions(mod.Snapshot))
            Err("TAG_COLLISION", $"the mod already uses an editor tag outside the generated files: {hit}");

        // ---- generator invariants (always true for the schema-1 naming; asserted so a future change cannot break them silently) ----
        foreach (var g in active.Where(g => g.Slot is >= 1 and <= MaxGroups))
        {
            string tag = SpawnGroupGenerator.Tag(g.Slot);
            foreach (var id in new[] { tag + "S", tag + "R", tag + "on", tag + "Off", tag + "Msg", "ugInit", "ugGate", "ugHide", "ugDef" })
                if (id.Length > MaxIdLength) Err("ID_TOO_LONG", $"generated id '{id}' is {id.Length} characters; at most {MaxIdLength}");
            if (tag.Length > MaxTagLength) Err("ID_TOO_LONG", $"generated tag '{tag}' is {tag.Length} characters; at most {MaxTagLength}");
            foreach (var cond in new[] { SpawnGroupGenerator.SpawnTrigger(g.Slot, g.Mode), SpawnGroupGenerator.ReleaseTrigger(g.Slot, g.Mode) })
                if (("autoTrigger: " + cond).Length > MaxConditionLine) Err("CONDITION_TOO_LONG", $"{tag}: a generated condition line is {("autoTrigger: " + cond).Length} characters; at most {MaxConditionLine}");
        }
        if (active.Count > 0)
        {
            int gate = ("autoTrigger: " + SpawnGroupGenerator.GateTrigger(active.Select(g => g.Slot))).Length;
            if (gate > MaxConditionLine) Err("CONDITION_TOO_LONG", $"the ugGate condition line is {gate} characters; at most {MaxConditionLine}");
        }
        return f;
    }

    static IEnumerable<string> HandPlacementOverlaps(EditorSettings s, ModContext mod)
    {
        foreach (var grp in HandPlacement.Overlaps(s, mod).GroupBy(o => o.Slot))
        {
            var g = s.SpawnGroups.FirstOrDefault(x => x.Slot == grp.Key);
            if (g == null) continue;
            var pairs = grp.Select(o => $"{o.Unit} on {o.Other}").Distinct().ToList();
            yield return $"group {g.Slot} '{g.Name}': {pairs.Count} placed unit(s) overlap another ({string.Join(", ", pairs.Take(3))}{(pairs.Count > 3 ? ", ..." : "")}). " +
                         "Mobile units are placed anyway; a building that overlaps another one is skipped by the game.";
        }
    }
}
