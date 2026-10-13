using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RwEditor.Core.Mod;

/// <summary>
/// The "made by the editor" marker (DESIGN_EDITOR 5.4, PLACEHOLDERS PH-071): <c>rweditor-copy.json</c> at the root of a mod copy that the editor's
/// "Make my copy" step created. The folder guard requires it before the editor writes anything into a folder, so the editor never edits a folder it did
/// not copy itself (the Workshop item, the installed mod, a development source, the pristine baseline).
/// Format 1 (canonical JSON: members sorted, 2-space indent, LF, trailing LF):
/// <code>
/// { "copyId": "&lt;32 hex&gt;", "createdUtc": "yyyy-MM-ddTHH:mm:ssZ", "editorVersion": "0.1.0", "format": 1, "kind": "depache-rw-editor-copy",
///   "name": "&lt;user's copy name&gt;", "source": { "lockedManifestSha256": "&lt;64 hex&gt;", "modVersion": "1.0.6", "path": "&lt;full path copied from&gt;" } }
/// </code>
/// The game ignores the file (it reads *.ini / *.template / mod-info.txt; editor-support.json already sits next to it in the shipped mod).
/// </summary>
public sealed record EditorCopyMarker(string CopyId, DateTime CreatedUtc, string EditorVersion, string Name, string SourcePath, string SourceModVersion,
    string SourceLockedManifestSha256)
{
    public const string FileName = "rweditor-copy.json";
    public const string KindValue = "depache-rw-editor-copy";
    public const int CurrentFormat = 1;

    public static EditorCopyMarker Create(string name, string sourcePath, EditorSupport sourceSupport, string editorVersion, DateTime nowUtc) =>
        new(Guid.NewGuid().ToString("N"), TruncateToSeconds(nowUtc), editorVersion, name, Path.GetFullPath(sourcePath).TrimEnd('\\', '/'),
            sourceSupport.ModVersion, sourceSupport.LockedManifestSha256);

    static DateTime TruncateToSeconds(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>Canonical text (members sorted ordinally at every level, LF, trailing LF), so the same marker always gives the same bytes.</summary>
    public string ToJson()
    {
        var src = new JsonObject
        {
            ["lockedManifestSha256"] = SourceLockedManifestSha256,
            ["modVersion"] = SourceModVersion,
            ["path"] = SourcePath,
        };
        var root = new JsonObject
        {
            ["copyId"] = CopyId,
            ["createdUtc"] = CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["editorVersion"] = EditorVersion,
            ["format"] = CurrentFormat,
            ["kind"] = KindValue,
            ["name"] = Name,
            ["source"] = src,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\r\n", "\n") + "\n";
    }

    public byte[] ToBytes() => new UTF8Encoding(false).GetBytes(ToJson());

    /// <summary>Strict reader: unknown or missing members, a wrong kind / format or malformed values refuse the marker (COPY_MARKER_INVALID).</summary>
    public static EditorCopyMarker Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("the root is not an object");
            Only(r, "copyId", "createdUtc", "editorVersion", "format", "kind", "name", "source");
            if (Str(r, "kind") != KindValue) throw new FormatException($"kind is not '{KindValue}'");
            int format = r.GetProperty("format").GetInt32();
            if (format != CurrentFormat) throw new FormatException($"format {format}; this editor reads format {CurrentFormat}");
            string id = Str(r, "copyId");
            if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new FormatException("copyId must be 32 hex digits");
            if (!DateTime.TryParseExact(Str(r, "createdUtc"), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                throw new FormatException("createdUtc must be yyyy-MM-ddTHH:mm:ssZ");
            var s = r.GetProperty("source");
            if (s.ValueKind != JsonValueKind.Object) throw new FormatException("source must be an object");
            Only(s, "lockedManifestSha256", "modVersion", "path");
            string path = Str(s, "path");
            if (path.Length == 0 || !Path.IsPathFullyQualified(path)) throw new FormatException("source.path must be a full path");
            return new EditorCopyMarker(id, DateTime.SpecifyKind(created, DateTimeKind.Utc), Str(r, "editorVersion"), Str(r, "name"), path,
                Str(s, "modVersion"), Str(s, "lockedManifestSha256"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new EditorRefusalException("COPY_MARKER_INVALID", $"{source} cannot be read ({e.Message}). The editor only changes copies it made itself; make a new copy.");
        }

        static string Str(JsonElement o, string k) => o.GetProperty(k).GetString() ?? throw new FormatException($"'{k}' must be a string");
        static void Only(JsonElement o, params string[] allowed)
        {
            foreach (var p in o.EnumerateObject())
                if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new FormatException($"unknown member '{p.Name}'");
        }
    }

    /// <summary>The marker of a folder. Throws NO_COPY_MARKER when the file is missing, COPY_MARKER_INVALID when it cannot be read.</summary>
    public static EditorCopyMarker Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            throw new EditorRefusalException("NO_COPY_MARKER", $"{FileName} not found in {folder}: the editor only changes a copy it made itself (use \"Make my copy\" first).");
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new EditorRefusalException("COPY_MARKER_INVALID", $"{path} cannot be read: {e.Message}"); }
        return Parse(text, path);
    }

    /// <summary>Like <see cref="Load"/> but never throws: null plus the reason.</summary>
    public static EditorCopyMarker? TryLoad(string folder, out string? problem)
    {
        problem = null;
        try { return Load(folder); }
        catch (EditorRefusalException e) { problem = e.Message; return null; }
    }

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, FileName));
}
