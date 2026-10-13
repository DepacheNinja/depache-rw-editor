using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Apply;

/// <summary>One file inside a backup: its bytes before the change, or null when it did not exist.</summary>
public sealed record BackupFile(string RelPath, byte[]? Bytes);

/// <param name="Summary">E3: what the change did, in plain words (one line per change), for the History screen. Optional (older backups have none).</param>
/// <param name="Undoes">E3: for an "undo-safety" backup, the file name of the change backup that this undo restored (so that change counts as undone).</param>
public sealed record BackupInfo(string Path, string Kind, string ModRoot, DateTime CreatedUtc, string? SettingsHash, IReadOnlyList<BackupFile> Files,
    string? SettingsPath = null, byte[]? SettingsBytes = null, string? Summary = null, string? Undoes = null);

/// <summary>
/// Backups of the generated files, one zip per change, built in RAM and written ONCE (RAM-buffer rule) to
/// %LOCALAPPDATA%\DepacheRwEditor\backups\&lt;copyId&gt;\&lt;stamp&gt;-&lt;kind&gt;.zip (DESIGN_EDITOR 5.4 step 4). copyId = first 16 hex digits of the
/// SHA-256 of the mod folder's full path (lower case), so backups of different copies never mix.
/// </summary>
public static class BackupStore
{
    public const int Format = 1;
    const string ManifestName = "manifest.json";

    public static string DefaultRoot => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "backups");

    public static string CopyId(string modRoot)
    {
        string full = Path.GetFullPath(modRoot).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full))).Substring(0, 16);
    }

    public static string Folder(string backupRoot, string modRoot) => Path.Combine(backupRoot, CopyId(modRoot));

    /// <summary>Builds the zip in RAM and writes it once (temp name, then rename). Returns the zip path.</summary>
    /// <param name="settingsPath">E1b-2 stat edits: the settings file the change also updates; its bytes before the change (null = it did not exist)
    /// are kept in the zip so an undo can put the settings back in step with the files.</param>
    public static string Write(string backupRoot, string modRoot, string kind, IReadOnlyList<BackupFile> files, string? settingsHash, DateTime nowUtc,
        string? settingsPath = null, byte[]? settingsBytes = null, string? summary = null, string? undoes = null)
    {
        var manifest = new JsonObject
        {
            ["summary"] = summary,
            ["undoes"] = undoes,
            ["format"] = Format,
            ["kind"] = kind,
            ["modRoot"] = Path.GetFullPath(modRoot).TrimEnd('\\', '/'),
            ["createdUtc"] = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["settingsHash"] = settingsHash,
            ["settingsPath"] = settingsPath == null ? null : Path.GetFullPath(settingsPath),
            ["settingsExisted"] = settingsPath == null ? null : settingsBytes != null,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f.RelPath,
                ["existed"] = f.Bytes != null,
                ["sha256"] = f.Bytes == null ? null : Convert.ToHexStringLower(SHA256.HashData(f.Bytes)),
            }).ToArray()),
        };
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var me = za.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using (var s = me.Open()) s.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                foreach (var f in files.Where(f => f.Bytes != null))
                {
                    var e = za.CreateEntry("files/" + f.RelPath, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(f.Bytes!);
                }
                if (settingsPath != null && settingsBytes != null)
                {
                    var e = za.CreateEntry(SettingsEntry, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(settingsBytes);
                }
            }
            zip = ms.ToArray();
        }
        string dir = Folder(backupRoot, modRoot);
        Directory.CreateDirectory(dir);
        string stamp = nowUtc.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"{stamp}-{kind}.zip");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stamp}-{kind}-{n}.zip");
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, zip);
        File.Move(tmp, path);
        // read back once: a backup that cannot be read is no backup
        var check = Read(path);
        if (check.Files.Count != files.Count) throw new IOException("backup verification failed: " + path);
        return path;
    }

    public static BackupInfo Read(string zipPath)
    {
        using var za = ZipFile.OpenRead(zipPath);
        var me = za.GetEntry(ManifestName) ?? throw new InvalidDataException($"{zipPath}: no {ManifestName}");
        JsonNode m;
        using (var s = me.Open()) m = JsonNode.Parse(s) ?? throw new InvalidDataException($"{zipPath}: empty manifest");
        if ((int?)m["format"] != Format) throw new InvalidDataException($"{zipPath}: unknown backup format {m["format"]}");
        var files = new List<BackupFile>();
        foreach (var f in m["files"]!.AsArray())
        {
            string rel = (string)f!["path"]!;
            bool existed = (bool)f["existed"]!;
            byte[]? bytes = null;
            if (existed)
            {
                var e = za.GetEntry("files/" + rel) ?? throw new InvalidDataException($"{zipPath}: missing entry for {rel}");
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
                string want = (string)f["sha256"]!;
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != want) throw new InvalidDataException($"{zipPath}: {rel} does not match its recorded hash");
            }
            files.Add(new BackupFile(rel, bytes));
        }
        DateTime created = DateTime.Parse((string)m["createdUtc"]!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        string? settingsPath = (string?)m["settingsPath"];
        byte[]? settingsBytes = null;
        if (settingsPath != null && za.GetEntry(SettingsEntry) is ZipArchiveEntry se)
        {
            using var s = se.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            settingsBytes = ms.ToArray();
        }
        return new BackupInfo(zipPath, (string)m["kind"]!, (string)m["modRoot"]!, created, (string?)m["settingsHash"], files, settingsPath, settingsBytes,
            (string?)m["summary"], (string?)m["undoes"]);
    }

    /// <summary>The manifest only (no file bytes are read or hash-checked): for listing many backups quickly. Throws InvalidDataException / IOException.</summary>
    public static BackupInfo ReadManifest(string zipPath)
    {
        using var za = ZipFile.OpenRead(zipPath);
        var me = za.GetEntry(ManifestName) ?? throw new InvalidDataException($"{zipPath}: no {ManifestName}");
        JsonNode m;
        using (var s = me.Open()) m = JsonNode.Parse(s) ?? throw new InvalidDataException($"{zipPath}: empty manifest");
        if ((int?)m["format"] != Format) throw new InvalidDataException($"{zipPath}: unknown backup format {m["format"]}");
        var files = m["files"]!.AsArray().Select(f => new BackupFile((string)f!["path"]!, null)).ToList();
        DateTime created = DateTime.Parse((string)m["createdUtc"]!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        return new BackupInfo(zipPath, (string)m["kind"]!, (string)m["modRoot"]!, created, (string?)m["settingsHash"], files, (string?)m["settingsPath"], null,
            (string?)m["summary"], (string?)m["undoes"]);
    }

    const string SettingsEntry = "settings/before.json";

    /// <summary>Kinds of backup made by a change (an undo picks the newest of these); "undo-safety" backups are only restored when named explicitly.</summary>
    public static readonly IReadOnlyList<string> ChangeKinds = new[] { "apply", "revert-defaults", "stat-edit", "stat-revert", "stat-revert-all" };

    public static bool IsStatKind(string kind) => kind is "stat-edit" or "stat-revert" or "stat-revert-all";

    /// <summary>Backups of a copy, newest first (by file name, which starts with the UTC stamp).</summary>
    public static IReadOnlyList<string> List(string backupRoot, string modRoot)
    {
        string dir = Folder(backupRoot, modRoot);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.zip").OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
    }

    /// <summary>Sanity rule for anything restored from a backup: only the generated files may ever be written.</summary>
    public static bool IsRestorablePath(string rel) => EditorSupport.KnownGeneratedFiles.Contains(ModSnapshot.Norm(rel), StringComparer.Ordinal);
}
