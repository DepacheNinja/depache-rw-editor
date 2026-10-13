using System.Text;

namespace RwEditor.Core.Mod;

/// <summary>
/// The text of every *.ini / *.template file of a mod folder, read ONCE into RAM (RAM-buffer rule). Paths are mod-relative with '/' separators.
/// A snapshot can be overlaid with planned file texts (WithFiles) so every check runs on the exact bytes that would be written, before anything is written.
/// </summary>
public sealed class ModSnapshot
{
    readonly Dictionary<string, string> _texts;

    ModSnapshot(string root, Dictionary<string, string> texts) { Root = root; _texts = texts; }

    /// <summary>Full path of the mod folder (the folder holding editor-support.json).</summary>
    public string Root { get; }

    /// <summary>Relative paths, sorted the way tools/RwCatalog enumerates (OrdinalIgnoreCase).</summary>
    public IReadOnlyList<string> Files => _texts.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsIniLike(string path) =>
        path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".template", StringComparison.OrdinalIgnoreCase);

    public static ModSnapshot Load(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("mod folder not found: " + root);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!IsIniLike(f)) continue;
            // UTF-8, BOM stripped: the same decoding as tools/RwCatalog (File.ReadAllText(path, Encoding.UTF8)).
            texts[ToRel(root, f)] = File.ReadAllText(f, Encoding.UTF8);
        }
        return new ModSnapshot(root, texts);
    }

    /// <summary>For tests: a snapshot built from in-memory texts.</summary>
    public static ModSnapshot FromTexts(string root, IReadOnlyDictionary<string, string> texts) =>
        new(Path.GetFullPath(root), new Dictionary<string, string>(texts, StringComparer.OrdinalIgnoreCase));

    public string? GetText(string rel) => _texts.TryGetValue(Norm(rel), out var t) ? t : null;

    public bool Contains(string rel) => _texts.ContainsKey(Norm(rel));

    /// <summary>A copy with some files replaced or added (planned content). The receiver is not changed.</summary>
    public ModSnapshot WithFiles(IReadOnlyDictionary<string, string> overrides)
    {
        var d = new Dictionary<string, string>(_texts, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides) d[Norm(kv.Key)] = kv.Value;
        return new ModSnapshot(Root, d);
    }

    public static string ToRel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    public static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/');

    /// <summary>Full path of a mod-relative path.</summary>
    public string FullPath(string rel) => Path.GetFullPath(Path.Combine(Root, Norm(rel).Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Resolves one copyFrom entry the way the check scripts do (Resolve-RwPath): "CORE:" = engine built-in (null), "ROOT:/x" = mod root, anything else
    /// relative to the copying file's folder. A "(...)" suffix is dropped and quotes trimmed, as tools/RwCatalog does. Returns a mod-relative path
    /// (which may not exist) or null.
    /// </summary>
    public static string? ResolveCopyFrom(string fromRel, string entry)
    {
        string e = entry.Trim();
        int paren = e.IndexOf('(');
        if (paren > 0) e = e[..paren];
        e = e.Trim().Trim('"');
        if (e.Length == 0 || e.StartsWith("CORE:", StringComparison.OrdinalIgnoreCase)) return null;
        string rel;
        if (e.StartsWith("ROOT:", StringComparison.OrdinalIgnoreCase))
            rel = e.Substring(5).TrimStart('/', '\\');
        else
        {
            string dir = Path.GetDirectoryName(Norm(fromRel).Replace('/', Path.DirectorySeparatorChar)) ?? "";
            rel = Path.Combine(dir, e.Replace('/', Path.DirectorySeparatorChar));
        }
        // Collapse "..": resolve against a fake root and make it relative again.
        string fakeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rwe-root"));
        string full = Path.GetFullPath(Path.Combine(fakeRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        return Path.GetRelativePath(fakeRoot, full).Replace('\\', '/');
    }
}
