using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RwEditor.Core.Setup;

/// <summary>
/// The editor app's own remembered choices (not mod settings): the game folder and the copy the user works on. Stored as
/// %LOCALAPPDATA%\DepacheRwEditor\app-settings.json, read once at start and written once per change (temp file + replace). A missing or damaged file
/// gives the defaults (and the problem in <see cref="LoadProblem"/>); it never stops the editor.
/// </summary>
public sealed class AppSettings
{
    public const int Format = 1;
    public static string DefaultPath => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "app-settings.json");

    public string? GameFolder { get; set; }
    /// <summary>The editor copy (folder with rweditor-copy.json) the user works on.</summary>
    public string? CopyFolder { get; set; }
    /// <summary>Set when the file existed but could not be read (the defaults are used).</summary>
    public string? LoadProblem { get; private set; }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var s = new AppSettings();
        try
        {
            if (!File.Exists(path)) return s;
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? throw new JsonException("the root is not an object");
            if ((int?)root["format"] != Format) throw new JsonException("unknown format");
            s.GameFolder = Clean((string?)root["gameFolder"]);
            s.CopyFolder = Clean((string?)root["copyFolder"]);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            s.LoadProblem = $"{path} could not be read ({e.Message}); starting with no remembered folders";
            s.GameFolder = null; s.CopyFolder = null;
        }
        return s;
    }

    static string? Clean(string? p) => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) ? null : p;

    public string ToJson() => new JsonObject
    {
        ["copyFolder"] = CopyFolder,
        ["format"] = Format,
        ["gameFolder"] = GameFolder,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";

    /// <summary>One write (temp + replace). Throws IOException-family errors to the caller (the app shows them as a friendly message).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}
