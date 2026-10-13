using System.Text;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Generation;

/// <summary>
/// Text rules for anything the generator substitutes into the selector (DESIGN_EDITOR 2.3). A group name is substituted into text: / description: /
/// message values through @define actionName, so ':' '#' '$' '%' braces brackets '\' quotes and newlines are not allowed.
/// The allowed set is exactly the one tools/check-option-sections.ps1 (f) asserts: ^[A-Za-z0-9 \-_.!?+'&amp;()]{1,40}$.
/// </summary>
public static class NameRules
{
    public const int MaxNameLength = 40;
    public static readonly Regex Allowed = new(@"^[A-Za-z0-9 \-_.!?+'&()]{1,40}$", RegexOptions.CultureInvariant);

    static bool IsAllowedChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '-' or '_' or '.' or '!' or '?' or '+' or '\'' or '&' or '(' or ')';

    public static bool IsValidName(string name) => Allowed.IsMatch(name) && name == name.Trim() && !name.Contains("  ", StringComparison.Ordinal);

    /// <summary>
    /// The stored form of a typed name: disallowed characters dropped, runs of spaces collapsed, trimmed, cut to 40 characters (and trimmed again).
    /// May return "" (then the name is refused). The UI shows this result before saving (DESIGN_EDITOR 2.3).
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            char ch = c is '\t' or '\r' or '\n' ? ' ' : c;
            if (!IsAllowedChar(ch)) continue;
            if (ch == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(ch);
        }
        string s = sb.ToString().Trim();
        if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength).TrimEnd();
        return s;
    }

    /// <summary>A unit's display text made safe for a description line; falls back to the unit's code name when nothing usable is left.</summary>
    public static string DisplayForDescription(string? displayText, string unitName)
    {
        var s = Sanitize(displayText);
        return s.Length > 0 ? s : unitName;
    }
}
