using System.Text;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Ini;

/// <summary>Which reader rules to follow. Two existing readers in the repo disagree in small ways, and each consumer must match the tool it ports.</summary>
public enum IniDialect
{
    /// <summary>tools/RwCatalog/Ini.cs: a header is a trimmed line starting with '[' and ending with ']'; pairs before the first header are dropped;
    /// a value starting with """ continues until a line ending with """. Used by the lint port and the unit index.</summary>
    Catalog,

    /// <summary>Parse-Ini of tools/check-start-spawner.ps1 / check-option-sections.ps1: a header matches ^\[(.+)\]$ on the trimmed line; pairs before the
    /// first header go to an unnamed section (Name ""); no multi-line values. Used by the include-rule port so it decides exactly like the scripts.</summary>
    Checker,
}

public sealed class IniPair
{
    public IniPair(string key, string value, int line) { Key = key; Value = value; Line = line; }
    public string Key { get; }
    public string Value { get; }
    /// <summary>1-based line number of the key in the source text.</summary>
    public int Line { get; }
}

public sealed class IniSection
{
    public IniSection(string name, int index, int line) { Name = name; Index = index; Line = line; }
    public string Name { get; }
    /// <summary>Position in the file (0 = the unnamed leading section in the Checker dialect).</summary>
    public int Index { get; }
    public int Line { get; }
    public List<IniPair> Pairs { get; } = new();

    /// <summary>Value of a key in THIS section; the last duplicate wins (the game's rule, and both existing readers'). Key match is case-insensitive,
    /// as in RwCatalog (OrdinalIgnoreCase) and the PowerShell readers (-eq).</summary>
    public string? Get(string key)
    {
        string? v = null;
        foreach (var p in Pairs) if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) v = p.Value;
        return v;
    }

    public IEnumerable<string> Keys => Pairs.Select(p => p.Key);
}

/// <summary>One parsed Rusted Warfare ini text. Read-only model; it never writes files.</summary>
public sealed class IniDocument
{
    static readonly Regex CheckerHeader = new(@"^\[(.+)\]$", RegexOptions.CultureInvariant);

    IniDocument(string relPath, IniDialect dialect) { RelPath = relPath; Dialect = dialect; }

    public string RelPath { get; }
    public IniDialect Dialect { get; }
    public List<IniSection> Sections { get; } = new();

    public static IniDocument Parse(string text, string relPath, IniDialect dialect)
    {
        var doc = new IniDocument(relPath, dialect);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        IniSection? cur = null;
        if (dialect == IniDialect.Checker) { cur = new IniSection("", 0, 0); doc.Sections.Add(cur); }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (dialect == IniDialect.Catalog)
            {
                if (line[0] == '[' && line.EndsWith(']'))
                {
                    cur = new IniSection(line.Substring(1, line.Length - 2).Trim(), doc.Sections.Count, i + 1);
                    doc.Sections.Add(cur);
                    continue;
                }
            }
            else
            {
                var m = CheckerHeader.Match(line);
                if (m.Success)
                {
                    cur = new IniSection(m.Groups[1].Value.Trim(), doc.Sections.Count, i + 1);
                    doc.Sections.Add(cur);
                    continue;
                }
            }
            if (cur == null) continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string key = line.Substring(0, c).Trim();
            string val = line.Substring(c + 1).Trim();
            int keyLine = i + 1;
            if (dialect == IniDialect.Catalog && val.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                // Same rule as tools/RwCatalog/Ini.cs.
                var sb = new StringBuilder(val.Substring(3));
                bool closed = sb.ToString().EndsWith("\"\"\"", StringComparison.Ordinal) && sb.Length >= 3;
                while (!closed && i + 1 < lines.Length)
                {
                    i++;
                    sb.Append('\n').Append(lines[i]);
                    closed = lines[i].TrimEnd().EndsWith("\"\"\"", StringComparison.Ordinal);
                }
                val = sb.ToString().TrimEnd();
                if (val.EndsWith("\"\"\"", StringComparison.Ordinal)) val = val.Substring(0, val.Length - 3);
            }
            cur.Pairs.Add(new IniPair(key, val, keyLine));
        }
        return doc;
    }

    /// <summary>RwCatalog semantics: value of (section, key) over ALL sections of that name, last one wins; names and keys case-insensitive.</summary>
    public string? Get(string section, string key)
    {
        string? found = null;
        foreach (var s in Sections)
            if (s.Name.Equals(section, StringComparison.OrdinalIgnoreCase))
                foreach (var p in s.Pairs)
                    if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) found = p.Value;
        return found;
    }

    /// <summary>PowerShell semantics: the FIRST section with this name (case-insensitive), as in 'Where-Object { $_.Name -eq "core" } | Select-Object -First 1'.</summary>
    public IniSection? First(string name) => Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Named sections only (drops the unnamed leading section of the Checker dialect).</summary>
    public IEnumerable<IniSection> Named => Sections.Where(s => s.Name.Length > 0);
}
