using RwEditor.Core.Model;

namespace RwEditor.Core.Stats;

/// <summary>
/// One whitelisted stat of one unit, resolved (DESIGN_EDITOR 5.3 StatRef): the raw ini text, the number it means (canonical unit of the entry), where
/// it comes from, and why it is read-only when it is.
/// </summary>
public sealed record StatRef(
    UnitType Unit, StatEntry Entry, string Section, MergedValue? Raw, double? Number, string? ReadOnlyReason)
{
    public bool IsSet => Raw != null;
    public bool Editable => ReadOnlyReason == null;
    /// <summary>The field text (price parts) or the whole value text.</summary>
    public string? Text => Raw == null ? null : Entry.Field == null ? Raw.Value : Editable ? StatValue.PriceField(Raw.Value, Entry.Field) ?? "0" : Raw.Value;
}

public static class StatReader
{
    /// <summary>Resolves one entry in one section of a merged unit. A key that is not set at all reads as "not set" (the engine default applies; editable by adding it).</summary>
    public static StatRef Read(MergedUnit m, StatEntry e, string section)
    {
        var raw = m.Get(section, e.Key);
        if (raw == null) return new StatRef(m.Unit, e, section, null, null, null);
        string v = raw.Value.Trim();
        if (StatValue.IsExpression(v))
            return new StatRef(m.Unit, e, section, raw, null, "the value is an expression or uses a defined name (${...}); read-only");
        switch (e.Type)
        {
            case StatType.Int:
                return StatValue.TryInt(v, out var i) ? new StatRef(m.Unit, e, section, raw, i, null)
                    : new StatRef(m.Unit, e, section, raw, null, $"'{v}' is not a whole number literal; read-only");
            case StatType.Float:
                return StatValue.TryFloat(v, out var f) ? new StatRef(m.Unit, e, section, raw, f, null)
                    : new StatRef(m.Unit, e, section, raw, null, $"'{v}' is not a number literal; read-only");
            case StatType.Time:
                return StatValue.TryTime(v, out var fr, out _) ? new StatRef(m.Unit, e, section, raw, fr, null)
                    : new StatRef(m.Unit, e, section, raw, null, $"'{v}' is not a time literal (frames or Ns); read-only");
            case StatType.BuildTime:
                if (!StatValue.TryBuildSeconds(v, out var sec, out _))
                    return new StatRef(m.Unit, e, section, raw, null, $"'{v}' is not a build time literal (Ns or a per-frame number); read-only");
                // a plain 0 (or less) is used by many actions; whether the engine treats it as "instant" or "never" was not traced (PH-077)
                return double.IsInfinity(sec) ? new StatRef(m.Unit, e, section, raw, null, $"'{v}': a per-frame build speed of 0 or less; its meaning was not traced; read-only")
                    : new StatRef(m.Unit, e, section, raw, sec, null);
            default:
            {
                var parts = StatValue.ParsePrice(v);
                if (parts == null) return new StatRef(m.Unit, e, section, raw, null, $"'{v}' is not a literal price; read-only");
                if (CoupledReason(m, e, section) is string coupled) return new StatRef(m.Unit, e, section, raw, null, coupled);
                var hit = parts.LastOrDefault(p => p.Name == e.Field);
                if (hit == null) return new StatRef(m.Unit, e, section, raw, 0, null);   // the price has no such part: 0 of it
                bool ok = e.Type == StatType.PartInt ? StatValue.TryInt(hit.Number, out var c) && (c >= int.MinValue && c <= int.MaxValue) : StatValue.TryFloat(hit.Number, out _);
                if (!ok) return new StatRef(m.Unit, e, section, raw, null, $"'{hit.Number}' is not a {(e.Type == StatType.PartInt ? "whole" : "")} number; read-only");
                StatValue.TryFloat(hit.Number, out var n);
                // a "negate" entry is stored as the opposite sign: borrowResourcesWhileBuilt: techLevel = -5 PROVIDES 5 tech (the building borrows a negative amount)
                return new StatRef(m.Unit, e, section, raw, e.Negate ? (n == 0 ? 0 : -n) : n, null);
            }
        }
    }

    static readonly System.Text.RegularExpressions.Regex HasResourcesRx = new(@"hasResources\s*\(([^)]*)\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A literal action price or refund that is written a second time elsewhere as fixed numbers, so changing it alone would leave the copies out of step
    /// (mod 1.1.0, CHANGELOG C-043f): an EK factory entry with a two-currency price (price: credits=16500,experimental=1000) cannot use the shared
    /// template's ${section.price}, so its queue condition repeats the numbers (alsoTriggerOrQueueActionConditional: ... hasResources(credits=16500,
    /// experimental=1000)) and its requeue helper refunds them (addResources: credits=-16500,experimental=-1000). The editor edits one line at a time,
    /// so both are read-only with the reason. Plain prices whose condition / refund are computed (${...}) stay editable.
    /// </summary>
    internal static string? CoupledReason(MergedUnit m, StatEntry e, string section)
    {
        if (!IsActionSection(section)) return null;
        var sec = m.Section(section);
        if (sec == null) return null;
        if (e.Key.Equals("price", StringComparison.OrdinalIgnoreCase))
            return RepeatedPrice(sec) is string where
                ? $"this price is written a second time as the same fixed numbers in {where} and refunded by a re-queue helper; the editor changes one line " +
                  "at a time, so it is read-only here (change the price, that condition and the refund together by hand)"
                : null;
        if (!e.Key.Equals("addResources", StringComparison.OrdinalIgnoreCase)) return null;
        var refund = LiteralParts(sec.Get("addResources")?.Value);
        if (refund == null) return null;
        // the refund of a re-queued build = exactly minus the price of an action of this unit whose price is repeated in its queue condition
        foreach (var other in m.Sections)
        {
            if (ReferenceEquals(other, sec) || !IsActionSection(other.Name) || RepeatedPrice(other) == null) continue;
            var price = LiteralParts(other.Get("price")?.Value);
            if (price != null && SameParts(price, refund, negate: true))
                return $"[{section}] gives back exactly the price of [{other.Name}] (the refund of a re-queued build): it must stay equal to minus that price, so it " +
                       "is read-only here (change the price, its queue condition and this refund together by hand)";
        }
        return null;
    }

    static bool IsActionSection(string s) => s.StartsWith("action_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("hiddenAction_", StringComparison.OrdinalIgnoreCase);

    /// <summary>"[s] key (hasResources(...))" when another value of the section checks hasResources with exactly the section's literal price; else null.</summary>
    static string? RepeatedPrice(MergedSection sec)
    {
        var price = LiteralParts(sec.Get("price")?.Value);
        if (price == null) return null;
        foreach (var v in sec.Values)
        {
            if (v.Key.Equals("price", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (System.Text.RegularExpressions.Match hit in HasResourcesRx.Matches(v.Value))
                if (LiteralParts(hit.Groups[1].Value) is { } q && SameParts(price, q, negate: false))
                    return $"[{sec.Name}] {v.Key} ({hit.Value.Trim()})";
        }
        return null;
    }

    /// <summary>A literal price-format value as (resource, number) parts; null for an expression, an unknown format or a non-number part.</summary>
    static Dictionary<string, double>? LiteralParts(string? value)
    {
        if (value == null || StatValue.ParsePrice(value.Trim()) is not { Count: > 0 } parts) return null;
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts) { if (!StatValue.TryFloat(p.Number, out var n)) return null; d[p.Name] = n; }
        return d;
    }

    static bool SameParts(Dictionary<string, double> a, Dictionary<string, double> b, bool negate) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == (negate ? -kv.Value : kv.Value));

    /// <summary>Every section of a merged unit an entry applies to, in merged order.</summary>
    public static IEnumerable<string> SectionsFor(MergedUnit m, StatEntry e) => m.Sections.Select(s => s.Name).Where(e.MatchesSection);
}
