using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RwEditor.Core.Stats;

namespace RwEditor.Core.Mod;

/// <summary>The editor's own files at the root of a copy. The game reads *.ini / *.template / mod-info.txt and pictures / sounds; it ignores these.</summary>
public static class CopyFiles
{
    /// <summary>The copy's settings (schema 1, canonical JSON): applied stat edits, pending (staged) changes, spawn groups, start options.</summary>
    public const string SettingsName = "rweditor-settings.json";

    public static string SettingsPath(string copyRoot) => Path.Combine(copyRoot, SettingsName);

    /// <summary>Every editor-owned file name at a copy's root (never copied into a new copy, never written by a stat edit).</summary>
    public static readonly IReadOnlyList<string> EditorOwned = new[] { EditorCopyMarker.FileName, SettingsName, CopyBaseline.FileName };
}

/// <summary>
/// The copy's ORIGINAL values (E3, "revert everything"): a snapshot of every file the stat editor may ever write (an .ini / .template that is not locked
/// and not a generated include, <see cref="LockPolicy.IsStatWritableFile(string, IReadOnlyList{string})"/>), taken by "Make my copy" right after copying
/// and before the marker is written. Stored at the copy's root as <c>rweditor-baseline.bin</c>: a zip (built in RAM, written once) with
/// <c>manifest.json</c> (format, copyId, createdUtc, every file's path / size / SHA-256) and <c>files/&lt;path&gt;</c> (the bytes). The marker stays format 1
/// (no change for older copies). Copies made before E3 have no baseline: "revert everything" then falls back to the backups (<c>StatRevert</c>).
/// </summary>
public sealed class CopyBaseline
{
    public const string FileName = "rweditor-baseline.bin";
    public const int Format = 1;

    CopyBaseline(string copyId, DateTime createdUtc, IReadOnlyDictionary<string, byte[]> files)
    {
        CopyId = copyId; CreatedUtc = createdUtc; Files = files;
    }

    public string CopyId { get; }
    public DateTime CreatedUtc { get; }
    /// <summary>Relative path (forward slashes, as ModSnapshot) -> the bytes at copy time.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    public static string PathOf(string copyRoot) => Path.Combine(copyRoot, FileName);

    public static bool Exists(string copyRoot) => File.Exists(PathOf(copyRoot));

    /// <summary>The stat-writable files of a mod folder (relative paths, ordinal order).</summary>
    public static IReadOnlyList<string> StatWritableFiles(string root, IReadOnlyList<string> lockGlobs)
    {
        var list = new List<string>();
        foreach (var full in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            string rel = ModSnapshot.ToRel(root, full);
            if (LockPolicy.IsStatWritableFile(rel, lockGlobs)) list.Add(rel);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Builds the snapshot of a folder in RAM (one read per file). Nothing is written.</summary>
    public static byte[] Build(string root, IReadOnlyList<string> lockGlobs, string copyId, DateTime nowUtc)
    {
        var files = StatWritableFiles(root, lockGlobs).Select(rel => (rel, bytes: File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))).ToList();
        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["kind"] = "depache-rw-editor-baseline",
            ["copyId"] = copyId,
            ["createdUtc"] = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.rel,
                ["size"] = f.bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.bytes)),
            }).ToArray()),
        };
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var me = za.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var s = me.Open()) s.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            foreach (var (rel, bytes) in files)
            {
                var e = za.CreateEntry("files/" + rel, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Builds and writes the snapshot of <paramref name="root"/> once (temp name, then move) and reads it back. Returns the file count.</summary>
    public static int Write(string root, IReadOnlyList<string> lockGlobs, string copyId, DateTime nowUtc)
    {
        byte[] zip = Build(root, lockGlobs, copyId, nowUtc);
        string path = PathOf(root), tmp = path + ".tmp";
        File.WriteAllBytes(tmp, zip);
        File.Move(tmp, path, overwrite: true);
        return Load(root)!.Files.Count;   // a snapshot that cannot be read back is no snapshot
    }

    /// <summary>The copy's snapshot, or null when the copy has none (made before E3). Throws BASELINE_INVALID when it exists but is damaged: every file
    /// must match its recorded SHA-256 and size.</summary>
    public static CopyBaseline? Load(string copyRoot)
    {
        string path = PathOf(copyRoot);
        if (!File.Exists(path)) return null;
        try
        {
            using var za = ZipFile.OpenRead(path);
            var me = za.GetEntry("manifest.json") ?? throw new InvalidDataException("no manifest.json");
            JsonNode m;
            using (var s = me.Open()) m = JsonNode.Parse(s) ?? throw new InvalidDataException("empty manifest");
            if ((int?)m["format"] != Format || (string?)m["kind"] != "depache-rw-editor-baseline") throw new InvalidDataException("unknown format");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in m["files"]!.AsArray())
            {
                string rel = ModSnapshot.Norm((string)f!["path"]!);
                if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) throw new InvalidDataException("bad path " + rel);
                var e = za.GetEntry("files/" + rel) ?? throw new InvalidDataException("missing " + rel);
                using var s = e.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                var bytes = buf.ToArray();
                if (bytes.Length != (int)f["size"]! || Convert.ToHexStringLower(SHA256.HashData(bytes)) != (string)f["sha256"]!)
                    throw new InvalidDataException(rel + " does not match its recorded hash");
                files[rel] = bytes;
            }
            DateTime created = DateTime.Parse((string)m["createdUtc"]!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            return new CopyBaseline((string)m["copyId"]!, created, files);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or InvalidOperationException or NullReferenceException or FormatException
                                      or UnauthorizedAccessException)
        {
            throw new EditorRefusalException("BASELINE_INVALID", $"The saved original values of this copy ({FileName}) cannot be read ({e.Message}). " +
                "\"Put everything back\" will use the backups instead.");
        }
    }

    /// <summary>Files whose bytes on disk now differ from the snapshot (or that are missing now): relative path -> (snapshot bytes, current bytes or null).</summary>
    public IReadOnlyDictionary<string, (byte[] Original, byte[]? Now)> Differences(string copyRoot)
    {
        var d = new SortedDictionary<string, (byte[], byte[]?)>(StringComparer.Ordinal);
        foreach (var (rel, orig) in Files)
        {
            string full = Path.Combine(copyRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            byte[]? now = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (now == null || !now.AsSpan().SequenceEqual(orig)) d[rel] = (orig, now);
        }
        return d;
    }

    /// <summary>The snapshot's texts (decoded like ModSnapshot) for files that differ from <paramref name="current"/>: used to show each value's
    /// ORIGINAL (shipped) value next to the current one.</summary>
    public IReadOnlyDictionary<string, string> ChangedTexts(ModSnapshot current)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, bytes) in Files)
        {
            if (!ModSnapshot.IsIniLike(rel)) continue;
            string text = Encoding.UTF8.GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
            if (!string.Equals(current.GetText(rel), text, StringComparison.Ordinal)) d[rel] = text;
        }
        return d;
    }
}
