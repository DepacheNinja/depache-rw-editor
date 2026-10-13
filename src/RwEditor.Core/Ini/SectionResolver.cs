namespace RwEditor.Core.Ini;

/// <summary>
/// Port of Resolve-Sec / Copies of tools/check-option-sections.ps1: the effective key map of a section after @copyFromSection, sources in order
/// (a later source overrides an earlier one), then the section's own keys. Section and key lookups are case-insensitive like the PowerShell hashtables.
/// </summary>
public sealed class SectionResolver
{
    readonly Dictionary<string, IniSection> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<string, string>> _memo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sections in merged order; for a duplicated name the LAST one is used (the scripts' $byName assignment loop).</summary>
    public SectionResolver(IEnumerable<IniSection> sections)
    {
        foreach (var s in sections) if (s.Name.Length > 0) _byName[s.Name] = s;
    }

    public bool Has(string name) => _byName.ContainsKey(name);

    public IniSection? Section(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    public IReadOnlyDictionary<string, string> Resolve(string name) => Resolve(name, 0);

    Dictionary<string, string> Resolve(string name, int depth)
    {
        if (_memo.TryGetValue(name, out var cached)) return cached;
        if (depth > 20) throw new InvalidOperationException($"copyFromSection loop at [{name}]");
        if (!_byName.TryGetValue(name, out var sec)) throw new InvalidOperationException($"copyFromSection source [{name}] not found");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sec.Pairs)
        {
            if (!p.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var src in p.Value.Split(','))
            {
                string sn = src.Trim();
                if (sn.Length == 0) continue;
                var m2 = Resolve(sn, depth + 1);
                foreach (var kv in m2) if (!kv.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase)) map[kv.Key] = kv.Value;
            }
        }
        foreach (var p in sec.Pairs) if (!p.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase)) map[p.Key] = p.Value;
        _memo[name] = map;
        return map;
    }

    public string? Get(string section, string key) => Resolve(section).TryGetValue(key, out var v) ? v : null;

    /// <summary>Copies of the scripts: does [name] list 'what' in one of its @copyFromSection lines (case-insensitive -contains)?</summary>
    public bool Copies(string name, string what)
    {
        if (!_byName.TryGetValue(name, out var sec)) return false;
        foreach (var p in sec.Pairs)
            if (p.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase)
                && p.Value.Split(',').Select(x => x.Trim()).Contains(what, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }
}
