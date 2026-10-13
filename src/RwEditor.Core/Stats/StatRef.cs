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

    /// <summary>Every section of a merged unit an entry applies to, in merged order.</summary>
    public static IEnumerable<string> SectionsFor(MergedUnit m, StatEntry e) => m.Sections.Select(s => s.Name).Where(e.MatchesSection);
}
