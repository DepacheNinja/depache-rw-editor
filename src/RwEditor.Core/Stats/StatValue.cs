using System.Globalization;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Stats;

/// <summary>How the game reads a key (verified with javap on game-lib.jar, gameFramework/utility/ab and units/custom/d/b; see knowledge/stat-dictionary.json).</summary>
public enum StatType
{
    /// <summary>Integer.parseInt: a whole number, no decimals ("Not a static integer" otherwise).</summary>
    Int,
    /// <summary>Float.parseFloat.</summary>
    Float,
    /// <summary>ab.b(section, key, Float) "time": a number of frames, or "Ns" = N seconds = N x 60 frames.</summary>
    Time,
    /// <summary>ab.d(section, key, Float) inverted time (buildSpeed): "Ns" = N seconds to build; a plain number = build progress per frame.</summary>
    BuildTime,
    /// <summary>The credits part of a price-format value (units/custom/d/b.b): parts split on ',' or '|', each "name=value" / "name:value" or a bare number
    /// (= credits). Credits are Integer.parseInt.</summary>
    PartInt,
    /// <summary>The "rust" part of a price-format value: a mod-defined resource, Float.parseFloat.</summary>
    PartFloat,
}

/// <summary>Literal-value parsing and formatting. Anything that is not a plain literal of the expected form (${...}, @define names, text) is read-only.</summary>
public static class StatValue
{
    static readonly Regex IntRx = new(@"^[+-]?\d+$", RegexOptions.CultureInvariant);
    static readonly Regex FloatRx = new(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$", RegexOptions.CultureInvariant);

    public static bool IsExpression(string v) => v.Contains("${", StringComparison.Ordinal) || v.Contains('$');

    public static bool TryInt(string s, out long v) { v = 0; s = s.Trim(); return IntRx.IsMatch(s) && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v); }

    public static bool TryFloat(string s, out double v) { v = 0; s = s.Trim(); return FloatRx.IsMatch(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

    /// <summary>Formats a number the way a person writes it in an ini: no exponent, no trailing zeros, invariant culture.</summary>
    public static string Format(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.############", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------------------------------------------------------- time
    /// <summary>A time literal: "20s" (seconds) or "300" (frames). <paramref name="frames"/> = the engine's value in frames (60 per second).</summary>
    public static bool TryTime(string s, out double frames, out bool seconds)
    {
        s = s.Trim(); frames = 0; seconds = s.EndsWith('s');
        string n = seconds ? s[..^1] : s;
        if (!TryFloat(n, out var v)) return false;
        frames = seconds ? v * 60 : v;
        return true;
    }

    /// <summary>A buildSpeed literal: "20s" = 20 seconds, or a plain per-frame progress p (build time = 1 / (60 p) seconds; p &lt;= 0 never finishes).</summary>
    public static bool TryBuildSeconds(string s, out double secondsToBuild, out bool secondsForm)
    {
        s = s.Trim(); secondsToBuild = 0; secondsForm = s.EndsWith('s');
        string n = secondsForm ? s[..^1] : s;
        if (!TryFloat(n, out var v)) return false;
        if (secondsForm) secondsToBuild = v;
        else secondsToBuild = v > 0 ? 1.0 / (60.0 * v) : double.PositiveInfinity;
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------------- price
    /// <summary>One part of a price value with the exact character span of its number inside the whole value text.</summary>
    public sealed record PricePart(string Name, string Number, int NumberStart, bool Bare);

    /// <summary>Splits a literal price exactly like units/custom/d/b.b (',' or '|' between parts, '=' or ':' inside a part, a bare number = credits).
    /// Returns null for a value the editor cannot edit (an expression, an unknown format).</summary>
    public static IReadOnlyList<PricePart>? ParsePrice(string value)
    {
        if (IsExpression(value)) return null;
        var parts = new List<PricePart>();
        int i = 0;
        while (i <= value.Length)
        {
            int end = value.IndexOfAny(new[] { ',', '|' }, i);
            if (end < 0) end = value.Length;
            string piece = value.Substring(i, end - i);
            if (piece.Trim().Length > 0)
            {
                int eq = piece.IndexOfAny(new[] { '=', ':' });
                if (eq >= 0 && piece.IndexOfAny(new[] { '=', ':' }, eq + 1) >= 0) return null;   // "Unknown price format" in the game
                string name, numRaw; int numOffset;
                if (eq < 0) { name = "credits"; numRaw = piece; numOffset = 0; }
                else { name = piece.Substring(0, eq).Trim(); numRaw = piece.Substring(eq + 1); numOffset = eq + 1; }
                int lead = numRaw.Length - numRaw.TrimStart().Length;
                string num = numRaw.Trim();
                if (num.Length == 0) return null;
                parts.Add(new PricePart(name, num, i + numOffset + lead, eq < 0));
            }
            i = end + 1;
        }
        return parts;
    }

    public static string? PriceField(string value, string field) => ParsePrice(value)?.LastOrDefault(p => p.Name == field)?.Number;

    /// <summary>
    /// The price text with one field changed: the number is replaced in place (everything else kept); a field that is missing is appended as
    /// ", name = N" (" = " spacing when the value already uses it, the mod's usual style, else "name=N"). Null when the value is not an editable price.
    /// </summary>
    public static string? WithPriceField(string value, string field, string number)
    {
        var parts = ParsePrice(value);
        if (parts == null) return null;
        var hit = parts.LastOrDefault(p => p.Name == field);
        if (hit != null) return value.Substring(0, hit.NumberStart) + number + value.Substring(hit.NumberStart + hit.Number.Length);
        bool spaced = value.Contains(" = ", StringComparison.Ordinal);
        string sep = value.Trim().Length == 0 ? "" : ", ";
        return value.TrimEnd() + sep + field + (spaced ? " = " : "=") + number;
    }
}
