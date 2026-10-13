using RwEditor.Core.Ini;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Model;

/// <summary>Where a resolved value comes from, relative to the unit it is resolved for.</summary>
public enum OriginKind
{
    /// <summary>A line in the unit's own file.</summary>
    Own,
    /// <summary>A line in a [core] copyFrom parent (a shared template or another unit's file).</summary>
    Template,
    /// <summary>A line in the mod's all-units.template (merged into every unit last).</summary>
    AllUnits,
}

/// <summary>The exact source line of a resolved value: file, section header in that file, 1-based line, and (when it arrived through
/// @copyFromSection) the section it was copied from in the merged unit.</summary>
public sealed record Origin(string File, string FileSection, int Line, OriginKind Kind, string? ViaSection)
{
    public override string ToString() => $"{File}:{Line} [{FileSection}] ({Kind switch { OriginKind.Own => "own file", OriginKind.Template => "template", _ => "all-units.template" }}{(ViaSection != null ? ", via @copyFromSection " + ViaSection : "")})";
}

/// <summary>One resolved key of a merged section.</summary>
public sealed record MergedValue(string Key, string Value, Origin Origin);

/// <summary>One section of a merged unit: its keys in first-seen order (case-insensitive names, as the existing readers).</summary>
public sealed class MergedSection
{
    readonly Dictionary<string, MergedValue> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _order = new();

    public MergedSection(string name) { Name = name; }
    public string Name { get; }
    public IEnumerable<MergedValue> Values => _order.Select(k => _map[k]);
    public MergedValue? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => _map.ContainsKey(key);

    /// <summary>Engine rule (gameFramework/utility/ab.d, javap): put only if absent.</summary>
    internal void PutIfAbsent(MergedValue v)
    {
        if (_map.ContainsKey(v.Key)) return;
        _map[v.Key] = v;
        _order.Add(v.Key);
    }
}

/// <summary>A unit after the engine's merge: own file, copyFrom parents, all-units.template, then @copyFromSection. Problems found while merging are listed.</summary>
public sealed class MergedUnit
{
    readonly Dictionary<string, MergedSection> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly List<MergedSection> _order = new();

    public MergedUnit(UnitType unit) { Unit = unit; }
    public UnitType Unit { get; }
    public IReadOnlyList<MergedSection> Sections => _order;
    /// <summary>Every file that contributed (own file first, then the copyFrom chain in merge order, then all-units.template).</summary>
    public List<string> ChainFiles { get; } = new();
    /// <summary>copyFrom entries that point outside the mod (CORE:) or to a missing file: their keys are unknown to the editor.</summary>
    public List<string> UnresolvedParents { get; } = new();
    public List<string> Problems { get; } = new();

    public MergedSection? Section(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    internal MergedSection GetOrAdd(string name)
    {
        if (!_byName.TryGetValue(name, out var s)) { s = new MergedSection(name); _byName[name] = s; _order.Add(s); }
        return s;
    }

    public MergedValue? Get(string section, string key) => Section(section)?.Get(key);
}

/// <summary>One loadable unit type of a mod (a *.ini file with [core] name and without [core] dont_load: true, as tools/RwCatalog decides).</summary>
public sealed record UnitType(string Name, string RelPath, string Area, bool Locked);

/// <summary>
/// The read-only unit model of a mod folder (phase E1b-2): every loadable unit, its area, and its resolved stats with the exact origin of every value.
/// Merge order = the engine's (CHANGELOG C-026 + this phase's javap of units/custom/ag and gameFramework/utility/ab):
/// 1. the unit's own file (within one file the last duplicate key wins, the parser's rule);
/// 2. [core] copyFrom parents, the LAST listed first, each followed by its own chain, a key added only where none exists yet (ab.a / ab.d put-if-absent);
/// 3. all-units.template, same rule;
/// 4. @copyFromSection on the merged unit (ag.a(l, ab, String, String, int)): the target keeps every key it has; its sources are taken LAST listed first,
///    each source's keys put-if-absent, then that source's own @copyFromSection recursively (at most 10 levels).
/// Paths: "ROOT:/x" = mod root; "CORE:" = engine built-in (not resolvable here, recorded as unresolved); anything else relative to the copying file's
/// folder with the mod root as fallback (as tools/RwCatalog and ModContext).
/// </summary>
public sealed class UnitModel
{
    public const string AllUnitsTemplate = "all-units.template";

    readonly Dictionary<string, IniDocument> _docs;
    readonly Dictionary<string, MergedUnit> _memo = new(StringComparer.OrdinalIgnoreCase);
    readonly IReadOnlyList<string> _lockGlobs;

    UnitModel(ModSnapshot snap, Dictionary<string, IniDocument> docs, IReadOnlyList<UnitType> units, ModAreas areas, IReadOnlyList<string> lockGlobs)
    {
        Snapshot = snap; _docs = docs; Units = units; Areas = areas; _lockGlobs = lockGlobs;
        _byName = new Dictionary<string, UnitType>(StringComparer.Ordinal);
        foreach (var u in units) _byName.TryAdd(u.Name, u);
    }

    readonly Dictionary<string, UnitType> _byName;

    public ModSnapshot Snapshot { get; }
    public ModAreas Areas { get; }
    /// <summary>Loadable units, ordered by file path (OrdinalIgnoreCase, the RwCatalog order).</summary>
    public IReadOnlyList<UnitType> Units { get; }
    public IReadOnlyList<string> LockGlobs => _lockGlobs;

    public static UnitModel Load(string root, ModAreas? areas = null)
    {
        root = Path.GetFullPath(root);
        var snap = ModSnapshot.Load(root);
        IReadOnlyList<string> globs = File.Exists(Path.Combine(root, EditorSupport.FileName)) ? EditorSupport.Load(root).LockedGlobs : EditorSupport.DefaultLockedGlobs;
        return Build(snap, areas ?? ModAreas.Embedded, globs);
    }

    public static UnitModel Build(ModSnapshot snap, ModAreas areas, IReadOnlyList<string> lockGlobs)
    {
        var docs = new Dictionary<string, IniDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in snap.Files) docs[rel] = IniDocument.Parse(snap.GetText(rel)!, rel, IniDialect.Catalog);
        return new UnitModel(snap, docs, FindUnits(docs, areas, lockGlobs), areas, lockGlobs);
    }

    /// <summary>A model of the same mod with some file texts replaced (planned bytes); only the changed files are re-parsed.</summary>
    public UnitModel WithFiles(IReadOnlyDictionary<string, string> overrides)
    {
        var docs = new Dictionary<string, IniDocument>(_docs, StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, text) in overrides) docs[ModSnapshot.Norm(rel)] = IniDocument.Parse(text, ModSnapshot.Norm(rel), IniDialect.Catalog);
        return new UnitModel(Snapshot.WithFiles(overrides), docs, FindUnits(docs, Areas, _lockGlobs), Areas, _lockGlobs);
    }

    static IReadOnlyList<UnitType> FindUnits(Dictionary<string, IniDocument> docs, ModAreas areas, IReadOnlyList<string> lockGlobs)
    {
        var list = new List<UnitType>();
        foreach (var (rel, doc) in docs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;   // the game and RwCatalog load *.ini only
            var name = doc.Get("core", "name");
            if (name == null) continue;
            if ((doc.Get("core", "dont_load") ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new UnitType(name.Trim(), rel, areas.AreaOf(rel), EditorSupport.IsLocked(rel, lockGlobs)));
        }
        return list;
    }

    public IniDocument? Doc(string rel) => _docs.TryGetValue(ModSnapshot.Norm(rel), out var d) ? d : null;

    public bool IsLocked(string rel) => EditorSupport.IsLocked(rel, _lockGlobs);

    /// <summary>Exact (ordinal) name lookup; when two loadable files share a name the first in path order is returned.</summary>
    public UnitType? Find(string name) => _byName.TryGetValue(name, out var u) ? u : null;

    public UnitType? FindIgnoreCase(string name) => Units.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<UnitType> UnitsNamed(string name) => Units.Where(u => u.Name == name).ToList();

    // ------------------------------------------------------------------------------------------------------------------------- merge
    public MergedUnit Merge(UnitType unit)
    {
        // E2: the app reads one model from the UI thread and from a background thread (thumbnails), so the memo is locked; the merge itself only reads
        // the immutable parsed files, and two threads merging the same unit at once simply produce equal results (the first stored one wins).
        lock (_memo) { if (_memo.TryGetValue(unit.RelPath, out var hit)) return hit; }
        var m = new MergedUnit(unit);
        var own = _docs[unit.RelPath];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MergeChain(m, own, unit.RelPath, seen, 0);
        if (_docs.TryGetValue(AllUnitsTemplate, out var all) && !seen.Contains(AllUnitsTemplate))
        {
            m.ChainFiles.Add(AllUnitsTemplate);
            MergeFile(m, all, OriginKind.AllUnits);
        }
        ApplyCopyFromSection(m);
        lock (_memo)
        {
            if (_memo.TryGetValue(unit.RelPath, out var other)) return other;
            _memo[unit.RelPath] = m;
        }
        return m;
    }

    void MergeChain(MergedUnit m, IniDocument doc, string unitRel, HashSet<string> seen, int depth)
    {
        if (!seen.Add(doc.RelPath)) return;
        if (depth > 10) { m.Problems.Add($"copyFrom deeper than 10 levels at {doc.RelPath} (the game refuses this)"); return; }
        m.ChainFiles.Add(doc.RelPath);
        MergeFile(m, doc, string.Equals(doc.RelPath, unitRel, StringComparison.OrdinalIgnoreCase) ? OriginKind.Own : OriginKind.Template);
        var cf = doc.Get("core", "copyFrom");
        if (cf == null) return;
        var entries = cf.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            var parent = FindParent(doc.RelPath, entries[i]);
            if (parent == null) { m.UnresolvedParents.Add($"{doc.RelPath}: copyFrom '{entries[i]}'"); continue; }
            MergeChain(m, parent, unitRel, seen, depth + 1);
        }
    }

    static void MergeFile(MergedUnit m, IniDocument doc, OriginKind kind)
    {
        // within one file the last duplicate wins: collect per section first, then put-if-absent into the merged unit
        var perSection = new List<(string Name, Dictionary<string, (IniPair Pair, string FileSection)> Keys, List<string> Order)>();
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in doc.Sections)
        {
            if (!idx.TryGetValue(s.Name, out int i)) { i = perSection.Count; idx[s.Name] = i; perSection.Add((s.Name, new(StringComparer.OrdinalIgnoreCase), new())); }
            var bucket = perSection[i];
            foreach (var p in s.Pairs)
            {
                if (!bucket.Keys.ContainsKey(p.Key)) bucket.Order.Add(p.Key);
                bucket.Keys[p.Key] = (p, s.Name);
            }
        }
        foreach (var (name, keys, order) in perSection)
        {
            var ms = m.GetOrAdd(name);
            foreach (var k in order)
            {
                var (p, fs) = keys[k];
                ms.PutIfAbsent(new MergedValue(p.Key, p.Value, new Origin(doc.RelPath, fs, p.Line, kind, null)));
            }
        }
    }

    static void ApplyCopyFromSection(MergedUnit m)
    {
        // raw (pre-copy) key lists of every section; the result does not depend on processing order (put-if-absent, see class summary)
        var raw = m.Sections.ToDictionary(s => s.Name, s => s.Values.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var target in m.Sections.ToList())
        {
            if (!target.Has("@copyFromSection")) continue;
            Copy(m, raw, target, target.Name, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    static void Copy(MergedUnit m, Dictionary<string, List<MergedValue>> raw, MergedSection target, string current, int depth, HashSet<string> path)
    {
        if (depth > 10) { m.Problems.Add($"[{target.Name}] @copyFromSection deeper than 10 levels (the game refuses this)"); return; }
        string? list = raw.TryGetValue(current, out var cur) ? cur.LastOrDefault(v => v.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase))?.Value : null;
        if (string.IsNullOrWhiteSpace(list)) return;
        var sources = list.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Reverse().ToList();
        foreach (var src in sources)
        {
            if (src.Contains("${", StringComparison.Ordinal)) { m.Problems.Add($"[{current}] @copyFromSection '{src}' is an expression; not followed"); continue; }
            if (!raw.TryGetValue(src, out var srcKeys)) { m.Problems.Add($"[{current}] @copyFromSection '{src}' not found"); continue; }
            if (!path.Add(src)) { m.Problems.Add($"[{target.Name}] @copyFromSection loop through '{src}'"); continue; }
            foreach (var v in srcKeys)
            {
                if (v.Key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase)) continue;
                target.PutIfAbsent(v with { Origin = v.Origin with { ViaSection = v.Origin.ViaSection ?? src } });
            }
            Copy(m, raw, target, src, depth + 1, path);
            path.Remove(src);
        }
    }

    IniDocument? FindParent(string fromRel, string entry)
    {
        var rel = ResolveCopyFromFile(fromRel, entry);
        return rel != null && _docs.TryGetValue(rel, out var d) ? d : null;
    }

    /// <summary>The mod file a copyFrom entry of <paramref name="fromRel"/> names (same rules as the merge: relative to the copying file's folder, the mod
    /// root as fallback, ROOT:/ = mod root); null for CORE: or a file that is not in the mod.</summary>
    public string? ResolveCopyFromFile(string fromRel, string entry)
    {
        var rel = ModSnapshot.ResolveCopyFrom(fromRel, entry);
        if (rel != null && _docs.TryGetValue(rel, out var d)) return d.RelPath;
        string e = entry.Trim();
        if (!e.StartsWith("ROOT:", StringComparison.OrdinalIgnoreCase) && !e.StartsWith("CORE:", StringComparison.OrdinalIgnoreCase))
        {
            var rootRel = ModSnapshot.ResolveCopyFrom("x.ini", e);
            if (rootRel != null && _docs.TryGetValue(rootRel, out var d2)) return d2.RelPath;
        }
        return null;
    }

    Dictionary<string, List<UnitType>>? _byFile;

    /// <summary>The loadable unit(s) defined by one file (normally 0 or 1). The index is built once; a race only builds an equal index twice.</summary>
    public IReadOnlyList<UnitType> UnitsInFile(string rel)
    {
        var index = _byFile;
        if (index == null)
        {
            index = new Dictionary<string, List<UnitType>>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in Units) { if (!index.TryGetValue(u.RelPath, out var l)) index[u.RelPath] = l = new(); l.Add(u); }
            _byFile = index;
        }
        return index.TryGetValue(ModSnapshot.Norm(rel), out var list) ? list : Array.Empty<UnitType>();
    }

    // ------------------------------------------------------------------------------------------------------------------------- reverse index
    Dictionary<string, List<UnitType>>? _byChainFile;

    /// <summary>Loadable units whose merge chain contains <paramref name="rel"/> (their own file, a copyFrom parent, or all-units.template).</summary>
    public IReadOnlyList<UnitType> UnitsUsingFile(string rel)
    {
        if (_byChainFile == null)
        {
            var d = new Dictionary<string, List<UnitType>>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in Units)
                foreach (var f in Merge(u).ChainFiles)
                {
                    if (!d.TryGetValue(f, out var l)) d[f] = l = new List<UnitType>();
                    l.Add(u);
                }
            _byChainFile = d;
        }
        return _byChainFile.TryGetValue(ModSnapshot.Norm(rel), out var list) ? list : Array.Empty<UnitType>();
    }

    /// <summary>The effective [core] isBuilding (missing = not a building, standard RW default; see PH-072).</summary>
    public bool IsBuilding(UnitType u) => (Merge(u).Get("core", "isBuilding")?.Value.Trim() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
}
