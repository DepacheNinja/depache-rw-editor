using RwEditor.Core.Model;
using RwEditor.Core.Stats;

namespace RwEditor.Core.Browse;

/// <summary>One whitelisted stat of one unit as the browser shows it (READ-ONLY in E2): label, value in plain words, where it comes from, and the
/// dictionary's tooltip.</summary>
/// <param name="Ref">E3: the resolved value (raw text, number, origin, read-only reason) the editor converts from.</param>
/// <param name="Editable">E3: the stat may be changed (whitelisted, the unit / key is not locked, the value is a literal). Whether "this unit only" is
/// possible is decided by the full check when a change is staged.</param>
public sealed record StatRow(StatEntry Entry, string Section, string Label, string ValueText, string OriginText, string Note, string Tooltip, bool IsSet,
    OriginKind? Kind, int SharedBy, string? OriginFile, int? OriginLine, StatRef? Ref = null, bool Editable = false);

/// <summary>E3: a value of the unit that is NOT in the stat dictionary, shown read-only with the reason in plain words.</summary>
public sealed record OtherValue(string Section, string Key, string Value, string Reason, string OriginText);

/// <summary>Everything the details pane shows for one unit.</summary>
public sealed record UnitDetails(UnitRow Row, IReadOnlyList<StatRow> Simple, IReadOnlyList<StatRow> Expand, IReadOnlyList<string> Chain, IReadOnlyList<string> Problems,
    IReadOnlyList<OtherValue>? Other = null);

/// <summary>
/// Builds the read-only stat view of a unit from the unit model and the stat dictionary (like `rwedit stats`: every Simple entry of a fixed section,
/// shown even when not set; every other entry only where it is set; one row per matching section, e.g. each turret). UI-free.
/// </summary>
public static class StatView
{
    public static UnitDetails Describe(UnitModel model, StatDictionary dict, UnitRow row)
    {
        var u = row.Unit;
        var m = model.Merge(u);
        var simple = new List<StatRow>();
        var expand = new List<StatRow>();
        foreach (var e in dict.Entries)
        {
            var sections = StatReader.SectionsFor(m, e).ToList();
            if (sections.Count == 0 && e.FixedSection != null && e.Tier == StatTier.Simple) sections.Add(e.FixedSection);
            foreach (var sec in sections)
            {
                var r = StatReader.Read(m, e, sec);
                // "not set" is shown only for the basics of a fixed section (e.g. Armour); per-turret / per-projectile "not set" rows would be noise
                if (!r.IsSet && (e.Tier == StatTier.Expand || e.FixedSection == null)) continue;
                var target = e.Tier == StatTier.Simple ? simple : expand;
                target.Add(Row(model, dict, u, row, e, sec, r));
            }
        }
        // E3: every other resolved key, read-only, with the reason in plain words (keys of the dictionary are in Simple / Expand already)
        var shown = simple.Concat(expand).Select(r => (r.Section.ToLowerInvariant(), r.Entry.Key.ToLowerInvariant())).ToHashSet();
        var other = new List<OtherValue>();
        foreach (var s in m.Sections)
            foreach (var v in s.Values)
            {
                if (shown.Contains((s.Name.ToLowerInvariant(), v.Key.ToLowerInvariant()))) continue;
                bool locked = row.Locked || v.Key.StartsWith('@') || dict.IsNeverEditable(v.Key);
                string reason = row.Locked ? "Locked to keep the game stable (part of the mod's machinery)"
                    : locked ? "Locked to keep the game stable (game logic, names, pictures or links)"
                    : "Not editable in this version";
                other.Add(new OtherValue(s.Name, v.Key, v.Value.Trim(), reason, OriginWords(v.Origin)));
            }
        return new UnitDetails(row, simple, expand, m.ChainFiles.ToList(), m.Problems.Concat(m.UnresolvedParents.Select(p => "not readable by the editor: " + p)).ToList(), other);
    }

    static string OriginWords(Origin o) => o.Kind switch
    {
        OriginKind.Own => $"own file, line {o.Line}",
        OriginKind.Template => $"{o.File}, line {o.Line}",
        _ => $"all-units.template, line {o.Line}",
    };

    static StatRow Row(UnitModel model, StatDictionary dict, UnitType u, UnitRow row, StatEntry e, string sec, StatRef r)
    {
        string label = e.Label + SectionSuffix(e, sec);
        string value = !r.IsSet ? "not set (the game's default)"
            : r.Editable ? ValueText(e, r)
            : r.Raw!.Value.Trim();
        string origin = "";
        int shared = 0;
        if (r.Raw != null)
        {
            var o = r.Raw.Origin;
            shared = SharedBy(model, r);
            origin = o.Kind switch
            {
                OriginKind.Own => $"this unit's own file ({o.File}, line {o.Line})" + (shared > 1 ? $" - also read by {shared - 1} unit(s) that copy this file" : ""),
                OriginKind.Template => $"shared template {o.File}, line {o.Line}" + (shared > 1 ? $" - used by {shared} units" : ""),
                _ => $"base settings for every unit (all-units.template, line {o.Line})",
            };
            if (o.ViaSection != null) origin += $", copied from section [{o.ViaSection}]";
        }
        string note;
        bool editable = false;
        if (row.Locked) note = "Locked to keep the game stable (part of the mod's machinery)";
        else if (!r.Editable) note = "Read-only: " + ReadOnlyWords(r.ReadOnlyReason!);
        else if (LockPolicy.Approve(dict, e, e.Id, model, u, sec) is Finding f) note = "Locked to keep the game stable: " + f.Message;
        else
        {
            editable = true;
            note = r.Raw != null && !LockPolicy.IsStatWritableFile(r.Raw.Origin.File, model.LockGlobs)
                ? "Comes from a locked file: a change is added to this unit's own file" : "";
        }
        string tip = $"{e.Label}: {e.Desc}\nIf you raise it: {e.Up}\nIf you lower it: {e.Down}\nMeasured in: {StatText.InputUnit(e)}. Allowed: {StatText.Range(e)}." +
                     (e.Verified ? "" : "\n(The safe range of this value was not tested in a match yet.)");
        return new StatRow(e, sec, label, value, origin, note, tip, r.IsSet, r.Raw?.Origin.Kind, shared, r.Raw?.Origin.File, r.Raw?.Origin.Line, r, editable);
    }

    /// <summary>Why a dictionary value is read-only, in plain words.</summary>
    static string ReadOnlyWords(string reason) =>
        reason.Contains("${", StringComparison.Ordinal) || reason.Contains("expression", StringComparison.Ordinal)
            ? "calculated from other values (a formula), so it is not editable in this version"
            : Plain(reason) + " - not editable in this version";

    static string SectionSuffix(StatEntry e, string sec) => e.FixedSection != null ? "" : $" [{sec}]";

    /// <summary>E3: the value in the unit the user types in (times in seconds), with that unit, e.g. "11500 hit points", "0.0667 seconds".</summary>
    static string ValueText(StatEntry e, StatRef r) => StatText.Value(e, r.Number!.Value);

    static string Plain(string reason) => reason.Replace("; read-only", "", StringComparison.Ordinal);

    /// <summary>How many loadable units read this value from the same file line (one line holds one key, so file + line identify the value).</summary>
    public static int SharedBy(UnitModel model, StatRef r)
    {
        if (r.Raw == null) return 0;
        var o = r.Raw.Origin;
        if (o.Kind == OriginKind.Own) return Math.Max(1, UnitsReadingLine(model, o.File, o.Line).Count);
        return UnitsReadingLine(model, o.File, o.Line).Count;
    }

    /// <summary>The loadable units (path order) whose merged value of some key comes from this file line (one line holds one key). E3: the list the
    /// "change every unit that shares it" choice shows. An own-file line counts the unit itself plus every unit that copies its file.</summary>
    public static IReadOnlyList<UnitType> UnitsReadingLine(UnitModel model, string file, int line) =>
        LineUse(model).TryGetValue((file, line), out var list) ? list : Array.Empty<UnitType>();

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UnitModel, Dictionary<(string File, int Line), IReadOnlyList<UnitType>>> _lineUse = new();

    /// <summary>
    /// For every (file, line) that some unit's merged value comes from: the number of distinct loadable units using it. Built once per model in one pass
    /// (E2 performance fix: counting per value by re-scanning every unit made the stat view of all units take minutes on the real mod).
    /// </summary>
    static Dictionary<(string File, int Line), IReadOnlyList<UnitType>> LineUse(UnitModel model) => _lineUse.GetValue(model, m =>
    {
        var sets = new Dictionary<(string, int), List<UnitType>>();
        foreach (var u in m.Units)   // path order, so every list is in path order
            foreach (var s in m.Merge(u).Sections)
                foreach (var v in s.Values)
                {
                    var k = (v.Origin.File.ToLowerInvariant(), v.Origin.Line);
                    if (!sets.TryGetValue(k, out var list)) sets[k] = list = new List<UnitType>();
                    if (list.Count == 0 || !ReferenceEquals(list[^1], u)) list.Add(u);   // a unit reads a line at most once per pass over its sections
                }
        var map = new Dictionary<(string File, int Line), IReadOnlyList<UnitType>>(new FileLineComparer());
        foreach (var (k, list) in sets) map[k] = list.Distinct().ToList();
        return map;
    });

    sealed class FileLineComparer : IEqualityComparer<(string File, int Line)>
    {
        public bool Equals((string File, int Line) a, (string File, int Line) b) => a.Line == b.Line && string.Equals(a.File, b.File, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string File, int Line) x) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(x.File), x.Line);
    }
}
