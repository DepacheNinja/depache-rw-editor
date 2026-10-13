using RwEditor.Core.Settings;

namespace RwEditor.Core.Stats;

/// <summary>
/// Plain-words text and input conversion for the stat editor (E3), shared by the app and the CLI. Users see times in SECONDS; the value written keeps the
/// line's own form where it can (frames stay frames, "Ns" stays "Ns"; build times are always written as "Ns", the form ab.d reads exactly).
/// </summary>
public static class StatText
{
    /// <summary>"Health", or "Reload time (turret_1)" for entries of numbered sections.</summary>
    public static string StatName(StatEntry e, string section) => e.FixedSection != null ? e.Label : $"{e.Label} ({section})";

    public static string StatName(PendingStatEdit p) =>
        StatDictionary.Embedded.ById(p.StatId) is StatEntry e ? StatName(e, p.Section) : $"{p.StatId} ({p.Section})";

    /// <summary>True when the user types and sees seconds for this entry.</summary>
    public static bool InSeconds(StatEntry e) => e.Type is StatType.Time or StatType.BuildTime;

    /// <summary>The unit of measure the user types in.</summary>
    public static string InputUnit(StatEntry e) => InSeconds(e) ? "seconds" : e.Unit;

    /// <summary>The canonical number (frames for Time, seconds for BuildTime, else the value) as the user sees it.</summary>
    public static double ToDisplay(StatEntry e, double canonical) => e.Type == StatType.Time ? canonical / 60.0 : canonical;

    /// <summary>A canonical number as plain text with its unit, e.g. "13000 hit points", "0.5 seconds".</summary>
    public static string Value(StatEntry e, double canonical) => $"{Num(ToDisplay(e, canonical))} {InputUnit(e)}";

    /// <summary>A number as shown (no unit): at most 4 decimals, no exponent (1 frame = 0.0167 seconds).</summary>
    public static string Num(double v) => StatValue.Format(Math.Round(v, 4));

    /// <summary>The allowed range in the user's unit, e.g. "1 .. 100000000 hit points".</summary>
    public static string Range(StatEntry e) => $"{Num(ToDisplay(e, e.HardMin))} .. {Num(ToDisplay(e, e.HardMax))} {InputUnit(e)}";

    /// <summary>
    /// Turns what the user typed (in <see cref="InputUnit"/>) into the value text for the stat patcher. <paramref name="currentRaw"/> is the line's text
    /// now (null when not set) so the line's form is kept. Returns null with a plain-words <paramref name="error"/> when the input is not usable; the
    /// range itself is checked by the patcher (and by <see cref="QuickCheck"/>).
    /// </summary>
    public static string? ToRequestValue(StatEntry e, string? currentRaw, string typed, out string? error)
    {
        error = null;
        string t = (typed ?? "").Trim().Replace(',', '.');
        if (InSeconds(e) && t.EndsWith('s')) t = t[..^1].Trim();
        if (t.StartsWith('+')) t = t[1..];
        if (t.Length == 0) { error = "Type a number."; return null; }
        if (!StatValue.TryFloat(t, out double v) || double.IsNaN(v) || double.IsInfinity(v))
        {
            error = $"'{typed}' is not a number. Use digits, and a dot for decimals (for example 2.5).";
            return null;
        }
        switch (e.Type)
        {
            case StatType.Int:
            case StatType.PartInt:
                if (v != Math.Floor(v)) { error = "This value must be a whole number (no decimals)."; return null; }
                if (v < int.MinValue || v > int.MaxValue) { error = "That number is far too large."; return null; }
                return StatValue.Format(v);
            case StatType.Time:
            {
                double frames = v * 60.0;
                bool frameForm = currentRaw != null && !currentRaw.Trim().EndsWith('s');
                if (frameForm && Math.Abs(frames - Math.Round(frames)) < 1e-9) return StatValue.Format(Math.Round(frames));
                return StatValue.Format(v) + "s";
            }
            case StatType.BuildTime:
                return StatValue.Format(v) + "s";
            default:
                return StatValue.Format(v);
        }
    }

    /// <summary>The fast checks done while typing (no file is read): the input converts, it is inside the hard limits. Null = fine.</summary>
    public static string? QuickCheck(StatEntry e, string? currentRaw, string typed, out string? requestValue)
    {
        requestValue = ToRequestValue(e, currentRaw, typed, out var err);
        if (requestValue == null) return err;
        if (!StatPatcher.ParseNew(e, requestValue, out double n, out _, out var perr)) return Plain(perr ?? "not a usable value");
        if (n < e.HardMin || n > e.HardMax) return $"Allowed: {Range(e)}. {(n < e.HardMin ? "Lower values" : "Higher values")} could break the game, so they are not possible.";
        return null;
    }

    /// <summary>
    /// Warnings in plain words, one line each, with the "computed from this value" notes of one unit folded into ONE line ("3 other values of this unit
    /// are calculated from this one and change with it: mass, ..."), so a user is not shown four formulas for one Health change.
    /// </summary>
    public static IReadOnlyList<string> PlainWarnings(IEnumerable<Finding> warnings)
    {
        var list = warnings.ToList();
        var rx = new System.Text.RegularExpressions.Regex(@"^(?<u>\S+) \[(?<s>[^\]]+)\] (?<k>[^:]+):");
        var deps = list.Where(f => f.Code == "EXPRESSION_DEPENDS").Select(f => rx.Match(f.Message)).Where(m => m.Success)
            .GroupBy(m => m.Groups["u"].Value).Select(g =>
            {
                var keys = g.Select(m => m.Groups["k"].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return $"{g.Count()} other value(s) of {g.Key} are calculated from this one and change with it ({string.Join(", ", keys.Take(5))}{(keys.Count > 5 ? ", ..." : "")}).";
            });
        return list.Where(f => f.Code != "EXPRESSION_DEPENDS").Select(Plain).Concat(deps).Distinct().ToList();
    }

    /// <summary>A finding in plain words (codes and internal terms removed or explained).</summary>
    public static string Plain(Finding f) => f.Code switch
    {
        "SOFT_LIMIT" or "SOFT_LIMIT_RELATIVE" => "Unusual value: " + Plain(f.Message) + ". It is allowed, but check it in a match.",
        "NOT_UNIT_ONLY" => "This value is shared with other units, so it cannot be changed for this unit alone. " + Plain(f.Message),
        "LINT_NEW" => "The game would report a problem when loading: " + Plain(f.Message),
        "EXPRESSION_DEPENDS" => "Another value of this unit is calculated from this one and changes with it: " + Plain(f.Message),
        _ => Plain(f.Message),
    };

    public static string Plain(string m) => m
        .Replace("; read-only", "", StringComparison.Ordinal)
        .Replace(" (internal check)", "", StringComparison.Ordinal)
        .Replace("Repeat with --template to change all", "Use \"change every unit that shares it\" to change all", StringComparison.Ordinal)
        .Replace("Use --template to change the shared line, or add the section by hand.", "Use \"change every unit that shares it\" instead.", StringComparison.Ordinal);
}
