using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Mod;

/// <summary>One locked file -> the generated include it copies (editor-support.json "includes").</summary>
public sealed record IncludeRef(string LockedFile, string GeneratedFile, string CopyFromEntry);

/// <summary>Result of re-hashing a copy's locked files against editor-support.json.</summary>
public sealed record LockVerifyResult(int LockedOnDisk, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, IReadOnlyList<string> Changed)
{
    public bool Ok => Missing.Count == 0 && Extra.Count == 0 && Changed.Count == 0;

    public string Describe()
    {
        if (Ok) return $"all {LockedOnDisk} locked files hash-equal editor-support.json";
        var sb = new StringBuilder();
        sb.Append($"locked files differ from editor-support.json: {Changed.Count} changed, {Missing.Count} listed but missing/unlocked, {Extra.Count} locked but not listed");
        foreach (var x in Changed.Take(10)) sb.Append("\n  hash differs: ").Append(x);
        foreach (var x in Missing.Take(10)) sb.Append("\n  in json but no longer locked/present: ").Append(x);
        foreach (var x in Extra.Take(10)) sb.Append("\n  not in json: ").Append(x);
        return sb.ToString();
    }
}

/// <summary>
/// The contract file the shipped mod carries at its root (C-026, written by tools/editor-support.ps1). The editor understands exactly one layout: schema 1
/// with the two generated include files below. Anything else is refused (the copy is then read-only for this editor version).
/// </summary>
public sealed class EditorSupport
{
    public const string FileName = "editor-support.json";
    public const int SupportedSchema = 1;
    public const string AnchorGenerated = "editorGenerated/anchorGroups.ini";
    public const string SelectorGenerated = "editorGenerated/selectorGroups.ini";
    public const string AnchorLocked = "myAssets/extraStartAnchor.ini";
    public const string SelectorLocked = "startingOptions/gameOptionSelector.ini";

    /// <summary>The schema-1 layout, exactly as tools/editor-support.ps1 writes it.</summary>
    public static readonly IReadOnlyList<string> KnownGeneratedFiles = new[] { AnchorGenerated, SelectorGenerated };
    public static readonly IReadOnlyList<IncludeRef> KnownIncludes = new[]
    {
        new IncludeRef(AnchorLocked, AnchorGenerated, "ROOT:/" + AnchorGenerated),
        new IncludeRef(SelectorLocked, SelectorGenerated, "ROOT:/" + SelectorGenerated),
    };

    public int Schema { get; private init; }
    public string ModVersion { get; private init; } = "";
    public IReadOnlyList<string> GeneratedFiles { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<IncludeRef> Includes { get; private init; } = Array.Empty<IncludeRef>();
    public IReadOnlyList<string> LockedGlobs { get; private init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> LockedFiles { get; private init; } = new Dictionary<string, string>();
    public int LockedFileCount { get; private init; }

    /// <summary>Loads and checks the layout. Throws EditorRefusalException when the file is missing, malformed or describes a layout this editor does not know.</summary>
    public static EditorSupport Load(string modRoot)
    {
        string path = Path.Combine(modRoot, FileName);
        if (!File.Exists(path)) throw new EditorRefusalException("NO_EDITOR_SUPPORT", $"{FileName} not found in {modRoot}: this folder is not a mod made for the Depache RW Editor (or not its root folder).");
        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    public static EditorSupport Parse(string json, string sourceName)
    {
        EditorSupport es;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var locked = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in r.GetProperty("lockedFiles").EnumerateObject()) locked[p.Name] = p.Value.GetString() ?? "";
            es = new EditorSupport
            {
                Schema = r.GetProperty("schema").GetInt32(),
                ModVersion = r.GetProperty("modVersion").GetString() ?? "",
                GeneratedFiles = r.GetProperty("generatedFiles").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                Includes = r.GetProperty("includes").EnumerateArray().Select(x => new IncludeRef(
                    x.GetProperty("lockedFile").GetString() ?? "", x.GetProperty("generatedFile").GetString() ?? "", x.GetProperty("copyFromEntry").GetString() ?? "")).ToList(),
                LockedGlobs = r.GetProperty("lockedGlobs").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                LockedFiles = locked,
                LockedFileCount = r.GetProperty("lockedFileCount").GetInt32(),
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new EditorRefusalException("EDITOR_SUPPORT_MALFORMED", $"{sourceName} cannot be read: {e.Message}");
        }
        if (es.Schema != SupportedSchema)
            throw new EditorRefusalException("SCHEMA_UNKNOWN", $"{sourceName} has schema {es.Schema}; this editor knows schema {SupportedSchema} only. Update the editor (the copy stays read-only).");
        if (!es.GeneratedFiles.SequenceEqual(KnownGeneratedFiles, StringComparer.Ordinal))
            throw new EditorRefusalException("LAYOUT_UNKNOWN", $"{sourceName} lists generated files [{string.Join(", ", es.GeneratedFiles)}]; schema 1 expects [{string.Join(", ", KnownGeneratedFiles)}].");
        if (!es.Includes.SequenceEqual(KnownIncludes))
            throw new EditorRefusalException("LAYOUT_UNKNOWN", $"{sourceName} lists includes that differ from the schema-1 layout.");
        if (es.LockedFileCount != es.LockedFiles.Count)
            throw new EditorRefusalException("EDITOR_SUPPORT_MALFORMED", $"{sourceName}: lockedFileCount {es.LockedFileCount} but {es.LockedFiles.Count} rows.");
        foreach (var g in es.GeneratedFiles)
            if (IsLocked(g, es.LockedGlobs)) throw new EditorRefusalException("LAYOUT_UNKNOWN", $"{sourceName}: generated file {g} is under a lock glob.");
        return es;
    }

    /// <summary>Glob -> regex exactly as tools/editor-support.ps1 (Glob-Regex): '**' = any characters, '*' = any characters except '/', case-insensitive.</summary>
    public static Regex GlobRegex(string glob)
    {
        string r = Regex.Escape(glob).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*");
        return new Regex("^" + r + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsLocked(string rel, IEnumerable<string> globs)
    {
        rel = ModSnapshot.Norm(rel);
        foreach (var g in globs) if (GlobRegex(g).IsMatch(rel)) return true;
        return false;
    }

    public bool IsLocked(string rel) => IsLocked(rel, LockedGlobs);

    /// <summary>
    /// SHA-256 over the locked-file manifest: one "path\thash\n" line per locked file, sorted ordinally. This is the "locked-file manifest reference"
    /// a settings file records (DESIGN_SPEC 6c) so a later profile compare can tell whether two copies share the same locked base.
    /// </summary>
    public string LockedManifestSha256 => ManifestHash(LockedFiles);

    public static string ManifestHash(IReadOnlyDictionary<string, string> lockedFiles)
    {
        var sb = new StringBuilder();
        foreach (var k in lockedFiles.Keys.OrderBy(k => k, StringComparer.Ordinal)) sb.Append(k).Append('\t').Append(lockedFiles[k]).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Hashes every file under a lock glob (raw bytes, lowercase hex), exactly the set tools/editor-support.ps1 computes. Reads each file once.</summary>
    public static SortedDictionary<string, string> ComputeLockedHashes(string modRoot, IReadOnlyList<string> globs)
    {
        var rx = globs.Select(GlobRegex).ToList();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Get-ChildItem -Recurse -File (no -Force) skips hidden and system items; mirror that.
        var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = false };
        foreach (var f in Directory.EnumerateFiles(modRoot, "*", opts))
        {
            string rel = ModSnapshot.ToRel(modRoot, f);
            if (!rx.Any(r => r.IsMatch(rel))) continue;
            result[rel] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)));
        }
        return result;
    }

    public LockVerifyResult VerifyLockedFiles(string modRoot)
    {
        var now = ComputeLockedHashes(modRoot, LockedGlobs);
        var missing = LockedFiles.Keys.Where(k => !now.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = now.Keys.Where(k => !LockedFiles.ContainsKey(k)).ToList();
        var changed = now.Keys.Where(k => LockedFiles.TryGetValue(k, out var h) && !string.Equals(h, now[k], StringComparison.Ordinal)).ToList();
        if (changed.Count > 0) changed = changed.Where(k => !IsShippedPlusRecordedEdits(modRoot, k)).ToList();
        return new LockVerifyResult(now.Count, missing, extra, changed);
    }

    /// <summary>
    /// E5b: a locked file the editor rewrote on purpose (the Menu options page) is still the shipped file when the line edits recorded in the copy's
    /// settings are undone: the file with those lines put back must hash-equal the manifest. Anything else (a line changed by hand, an edit that is not
    /// recorded, a file the editor never changes) stays "changed". Settings that cannot be read record nothing, so nothing is excused.
    /// </summary>
    bool IsShippedPlusRecordedEdits(string modRoot, string rel)
    {
        if (!Options.OptionCatalog.Embedded.TargetFiles.Contains(rel, StringComparer.Ordinal)) return false;
        string settingsPath = CopyFiles.SettingsPath(modRoot);
        if (!File.Exists(settingsPath)) return false;
        List<Settings.OptionEdit> edits;
        try { edits = Settings.SettingsJson.Load(settingsPath).Settings.OptionEdits; }
        catch (Exception e) when (e is EditorRefusalException or IOException or UnauthorizedAccessException) { return false; }
        if (edits.Count == 0 || !edits.Any(e => e.File == rel)) return false;
        var undone = Options.OptionModel.TryUndo(rel, File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar))), edits);
        return undone != null && LockedFiles.TryGetValue(rel, out var want) && string.Equals(Convert.ToHexStringLower(SHA256.HashData(undone)), want, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON text tools/editor-support.ps1 -Write produces for a folder, byte for byte (LF, no BOM). Used to build test fixtures and, in a test against
    /// the real repo, to prove the glob and hash port agrees with the PowerShell tool.
    /// </summary>
    public static string BuildJson(string modRoot, string modVersion, IReadOnlyList<string>? globs = null)
    {
        globs ??= DefaultLockedGlobs;
        var hashes = ComputeLockedHashes(modRoot, globs);
        const string nl = "\n";
        var sb = new StringBuilder();
        sb.Append('{').Append(nl);
        sb.Append("  \"schema\": ").Append(SupportedSchema).Append(',').Append(nl);
        sb.Append("  \"modVersion\": ").Append(JStr(modVersion)).Append(',').Append(nl);
        sb.Append("  \"generatedBy\": \"tools/editor-support.ps1 (C-026)\",").Append(nl);
        sb.Append("  \"generatedFiles\": [").Append(string.Join(", ", KnownGeneratedFiles.Select(JStr))).Append("],").Append(nl);
        sb.Append("  \"includes\": [").Append(nl);
        sb.Append(string.Join("," + nl, KnownIncludes.Select(i =>
            "    { \"lockedFile\": " + JStr(i.LockedFile) + ", \"generatedFile\": " + JStr(i.GeneratedFile) + ", \"copyFromEntry\": " + JStr(i.CopyFromEntry) + " }")));
        sb.Append(nl).Append("  ],").Append(nl);
        sb.Append("  \"lockedGlobs\": [").Append(string.Join(", ", globs.Select(JStr))).Append("],").Append(nl);
        sb.Append("  \"hashAlgorithm\": \"SHA-256 of the raw file bytes, lowercase hex\",").Append(nl);
        sb.Append("  \"lockedFileCount\": ").Append(hashes.Count).Append(',').Append(nl);
        sb.Append("  \"lockedFiles\": {").Append(nl);
        sb.Append(string.Join("," + nl, hashes.Select(kv => "    " + JStr(kv.Key) + ": " + JStr(kv.Value))));
        sb.Append(nl).Append("  }").Append(nl).Append('}').Append(nl);
        return sb.ToString();
    }

    /// <summary>The lock globs of tools/editor-support.ps1 (DESIGN_EDITOR 1.4).</summary>
    public static readonly IReadOnlyList<string> DefaultLockedGlobs = new[]
    {
        "myAssets/**", "startingOptions/**", "myActionDummyUnits/**", "explosion_*spawner/**", "bitSpawner/**", "map_special/**",
        "all-units.template", "normalUnitTemplate.ini", "normalUnitIsParadeTemplate.ini",
    };

    /// <summary>JStr of tools/editor-support.ps1: escapes '"', '\' and control characters (\u00xx); everything else verbatim.</summary>
    static string JStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"') sb.Append("\\\"");
            else if (ch == '\\') sb.Append("\\\\");
            else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
