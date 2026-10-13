using System.Globalization;
using System.Text;

namespace RwEditor.Core.Model;

/// <summary>
/// A deliberately small evaluator for the <c>${...}</c> parts of unit-NAME values (convertTo, canBuild names, attachment units, spawned units), so links
/// such as <c>convertTo: ACV${level}</c> or <c>convertTo: ${core.name}_water</c> can be followed from the data instead of guessed from names.
/// Supported (the forms that occur in link values of the shipped mod, survey 2026-10-12): <c>${name}</c> = the section's own <c>@define name</c>, else a
/// <c>@global name</c> of the unit (any section; [core] first); <c>${section.key}</c> / <c>${core.key}</c> / <c>${otherSection.key}</c> = that merged
/// value (resolved again in its own section); numbers; + - * / and parentheses on numbers; <c>int(x)</c>. Whole numbers print without a decimal point.
/// Anything else (unknown names, functions, runtime values like <c>self.x</c>, text arithmetic) makes the whole value UNRESOLVED (null): the caller then
/// records the link as unresolved instead of guessing. Values are resolved on the MERGED unit (own file, copyFrom parents, @copyFromSection), so a
/// section that copies a template keeps its own @define (the put-if-absent rule of <see cref="UnitModel"/>).
/// </summary>
public static class LinkExpr
{
    const int MaxDepth = 8;

    /// <summary>The value with every <c>${...}</c> replaced, or null when any part cannot be resolved.</summary>
    public static string? Resolve(string value, MergedUnit unit, string sectionName) => Resolve(value, unit, sectionName, 0);

    static string? Resolve(string value, MergedUnit unit, string sectionName, int depth)
    {
        if (depth > MaxDepth) return null;
        if (!value.Contains("${", StringComparison.Ordinal)) return value.Contains('$') ? null : value;
        var sb = new StringBuilder();
        int i = 0;
        while (i < value.Length)
        {
            int start = value.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(value, i, value.Length - i); break; }
            sb.Append(value, i, start - i);
            int end = value.IndexOf('}', start + 2);
            if (end < 0) return null;
            var r = Evaluate(value.Substring(start + 2, end - start - 2), unit, sectionName, depth);
            if (r == null) return null;
            sb.Append(r);
            i = end + 1;
        }
        string s = sb.ToString();
        return s.Contains('$') ? null : s;
    }

    static string? Evaluate(string expr, MergedUnit unit, string sectionName, int depth)
    {
        var p = new Parser(expr, unit, sectionName, depth);
        try
        {
            var v = p.ParseExpr();
            if (!p.AtEnd) return null;
            return v switch { double d => FormatNumber(d), string s => s, _ => null };
        }
        catch (FormatException) { return null; }
    }

    public static string FormatNumber(double d) =>
        Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15 ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Looks up a bare name: the section's own @define, then a @global anywhere in the unit ([core] first, then file order).</summary>
    static (string Value, string Section)? Lookup(string name, MergedUnit unit, string sectionName)
    {
        var own = unit.Section(sectionName)?.Get("@define " + name);
        if (own != null) return (own.Value, sectionName);
        var core = unit.Section("core")?.Get("@global " + name);
        if (core != null) return (core.Value, "core");
        foreach (var s in unit.Sections)
        {
            var g = s.Get("@global " + name);
            if (g != null) return (g.Value, s.Name);
        }
        return null;
    }

    sealed class Parser
    {
        readonly string _s; readonly MergedUnit _u; readonly string _sec; readonly int _depth;
        int _i;
        public Parser(string s, MergedUnit u, string sec, int depth) { _s = s; _u = u; _sec = sec; _depth = depth; }
        public bool AtEnd { get { Skip(); return _i >= _s.Length; } }

        void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        bool Eat(char c) { Skip(); if (_i < _s.Length && _s[_i] == c) { _i++; return true; } return false; }

        public object ParseExpr()
        {
            var left = ParseTerm();
            while (true)
            {
                if (Eat('+')) left = Num(left) + Num(ParseTerm());
                else if (Eat('-')) left = Num(left) - Num(ParseTerm());
                else return left;
            }
        }

        object ParseTerm()
        {
            var left = ParseFactor();
            while (true)
            {
                if (Eat('*')) left = Num(left) * Num(ParseFactor());
                else if (Eat('/'))
                {
                    double d = Num(ParseFactor());
                    if (d == 0) throw new FormatException("division by zero");
                    left = Num(left) / d;
                }
                else return left;
            }
        }

        object ParseFactor()
        {
            Skip();
            if (_i >= _s.Length) throw new FormatException("unexpected end");
            if (Eat('(')) { var v = ParseExpr(); if (!Eat(')')) throw new FormatException("missing )"); return v; }
            if (Eat('-')) return -Num(ParseFactor());
            char c = _s[_i];
            if (char.IsDigit(c) || c == '.')
            {
                int st = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                return double.Parse(_s.AsSpan(st, _i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(c) || c == '_')
            {
                int st = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                string id = _s.Substring(st, _i - st);
                if (Eat('('))
                {
                    if (!id.Equals("int", StringComparison.Ordinal)) throw new FormatException("function " + id);
                    var a = ParseExpr();
                    if (!Eat(')')) throw new FormatException("missing )");
                    return Math.Truncate(Num(a));
                }
                return Ident(id);
            }
            throw new FormatException("unexpected " + c);
        }

        object Ident(string id)
        {
            string? raw; string ctx;
            int dot = id.IndexOf('.');
            if (dot > 0)
            {
                string sec = id[..dot], key = id[(dot + 1)..];
                if (sec.Equals("section", StringComparison.Ordinal)) sec = _sec;
                if (sec.Equals("self", StringComparison.Ordinal) || sec.Equals("parent", StringComparison.Ordinal) || key.Contains('.')) throw new FormatException("runtime value " + id);
                raw = _u.Section(sec)?.Get(key)?.Value; ctx = sec;
            }
            else
            {
                var hit = Lookup(id, _u, _sec);
                raw = hit?.Value; ctx = hit?.Section ?? _sec;
            }
            if (raw == null) throw new FormatException("unknown " + id);
            var resolved = Resolve(raw.Trim(), _u, ctx, _depth + 1) ?? throw new FormatException("unresolved " + id);
            return double.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : resolved;
        }

        static double Num(object o) => o is double d ? d : throw new FormatException("text in arithmetic");
    }

    /// <summary>Splits a unit list at top-level commas (commas inside <c>(...)</c> or <c>${...}</c> stay) and returns, for each entry, the unit-name part:
    /// the text before a top-level <c>*count</c> or <c>(arguments)</c>, trimmed. Empty entries are dropped.</summary>
    public static IReadOnlyList<string> NameParts(string list)
    {
        var parts = new List<string>();
        int depthParen = 0, depthBrace = 0, st = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            char c = i < list.Length ? list[i] : ',';
            if (c == '$' && i + 1 < list.Length && list[i + 1] == '{') { depthBrace++; i++; continue; }
            if (c == '}' && depthBrace > 0) { depthBrace--; continue; }
            if (depthBrace > 0) continue;
            if (c == '(') depthParen++;
            else if (c == ')') depthParen = Math.Max(0, depthParen - 1);
            else if (c == ',' && depthParen == 0)
            {
                string e = NamePart(list.Substring(st, i - st));
                if (e.Length > 0) parts.Add(e);
                st = i + 1;
            }
        }
        return parts;
    }

    /// <summary>The unit-name part of one spawn entry: the text before a top-level <c>*count</c> or <c>(arguments)</c>, trimmed.</summary>
    public static string NamePart(string entry)
    {
        int depthBrace = 0;
        for (int i = 0; i < entry.Length; i++)
        {
            char c = entry[i];
            if (c == '$' && i + 1 < entry.Length && entry[i + 1] == '{') { depthBrace++; i++; continue; }
            if (c == '}' && depthBrace > 0) { depthBrace--; continue; }
            if (depthBrace == 0 && (c == '*' || c == '(')) return entry[..i].Trim();
        }
        return entry.Trim();
    }
}
