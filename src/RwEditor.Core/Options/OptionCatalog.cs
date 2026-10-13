using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Options;

/// <summary>One place in a locked file where a catalog number lives: the line (file + section + key) and the pattern whose group "v" is the number.</summary>
public sealed record OptionTarget(string File, string Section, string Key, Regex Pattern);

/// <summary>One tunable number (knowledge/option-settings.json): the options it belongs to, its limits and where it is written. The FIRST target is the
/// one the current value is read from and must exist; further targets (the menu's own description text) follow the number when they are found.</summary>
public sealed record OptionSettingDef(string Id, string Label, IReadOnlyList<string> Options, string Description, string Unit, long Min, long Max, long? WarnMax,
    IReadOnlyList<OptionTarget> Targets);

/// <summary>
/// The whitelist of what the Menu options page may change in locked files (DESIGN_EDITOR 3, PH-101): the text lines of the options (name, description)
/// and a short list of numbers. Nothing outside it is ever written. Loaded strictly from the embedded knowledge/option-settings.json: any problem refuses
/// the whole catalog, so a typo can never widen what is editable.
/// </summary>
public sealed class OptionCatalog
{
    public const string ResourceName = "RwEditor.Data.option-settings.json";

    public string TextFile { get; }
    public string NameKey { get; }
    public string DescriptionKey { get; }
    public int MaxNameLength { get; }
    public int MaxDescriptionLines { get; }
    public int MaxDescriptionLineLength { get; }
    public IReadOnlyList<OptionSettingDef> Settings { get; }

    OptionCatalog(string textFile, string nameKey, string descKey, int maxName, int maxLines, int maxLineLen, IReadOnlyList<OptionSettingDef> settings)
    {
        TextFile = textFile; NameKey = nameKey; DescriptionKey = descKey; MaxNameLength = maxName; MaxDescriptionLines = maxLines; MaxDescriptionLineLength = maxLineLen;
        Settings = settings;
    }

    static OptionCatalog? _embedded;
    public static OptionCatalog Embedded => _embedded ??= LoadEmbedded();

    /// <summary>Every file the Menu options page may write (relative, forward slashes).</summary>
    public IReadOnlyCollection<string> TargetFiles =>
        Settings.SelectMany(s => s.Targets.Select(t => t.File)).Append(TextFile).Distinct(StringComparer.Ordinal).ToList();

    public OptionSettingDef? Find(string id) => Settings.FirstOrDefault(s => s.Id == id);

    static OptionCatalog LoadEmbedded()
    {
        using var s = typeof(OptionCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("embedded " + ResourceName + " is missing");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd(), ResourceName);
    }

    public static OptionCatalog Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad(source, "the top level must be an object");
            Only(root, source, "", "schema", "_about", "text", "settings");
            if (!root.TryGetProperty("schema", out var sc) || sc.ValueKind != JsonValueKind.Number || sc.GetInt32() != 1) throw Bad(source, "schema must be 1");
            var t = Obj(root, "text", source);
            Only(t, source, "text.", "file", "nameKey", "descriptionKey", "maxNameLength", "maxDescriptionLines", "maxDescriptionLineLength");
            string file = Rel(Str(t, "file", source, "text."), source);
            var list = new List<OptionSettingDef>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("settings", out var sa) || sa.ValueKind != JsonValueKind.Array) throw Bad(source, "settings must be an array");
            int i = 0;
            foreach (var s in sa.EnumerateArray())
            {
                string at = $"settings[{i++}].";
                if (s.ValueKind != JsonValueKind.Object) throw Bad(source, at + " must be an object");
                Only(s, source, at, "id", "label", "options", "desc", "unit", "kind", "min", "max", "warnMax", "targets");
                string id = Str(s, "id", source, at);
                if (!Regex.IsMatch(id, @"^[a-z][A-Za-z0-9]*(\.[a-z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant)) throw Bad(source, $"{at}id '{id}' is not a dotted name");
                if (!ids.Add(id)) throw Bad(source, $"{at}id '{id}' is used twice");
                if (Str(s, "kind", source, at) != "int") throw Bad(source, $"{at}kind must be \"int\" (the only kind this editor version writes)");
                long min = Long(s, "min", source, at), max = Long(s, "max", source, at);
                if (min < 0 || max <= min) throw Bad(source, $"{at}min/max must satisfy 0 <= min < max");
                long? warn = s.TryGetProperty("warnMax", out var w) ? Long(s, "warnMax", source, at) : null;
                if (warn is long wv && (wv <= min || wv > max)) throw Bad(source, $"{at}warnMax must be inside min..max");
                if (!s.TryGetProperty("options", out var oa) || oa.ValueKind != JsonValueKind.Array || oa.GetArrayLength() == 0) throw Bad(source, at + "options must be a non-empty array");
                var opts = oa.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString()! : throw Bad(source, at + "options must be strings")).ToList();
                if (!s.TryGetProperty("targets", out var ta) || ta.ValueKind != JsonValueKind.Array || ta.GetArrayLength() == 0) throw Bad(source, at + "targets must be a non-empty array");
                var targets = new List<OptionTarget>();
                int ti = 0;
                foreach (var tt in ta.EnumerateArray())
                {
                    string tat = $"{at}targets[{ti++}].";
                    if (tt.ValueKind != JsonValueKind.Object) throw Bad(source, tat + " must be an object");
                    Only(tt, source, tat, "file", "section", "key", "pattern");
                    string pat = Str(tt, "pattern", source, tat);
                    Regex rx;
                    try { rx = new Regex(pat, RegexOptions.CultureInvariant); }
                    catch (ArgumentException e) { throw Bad(source, $"{tat}pattern is not a valid expression: {e.Message}"); }
                    if (!rx.GetGroupNames().Contains("v")) throw Bad(source, tat + "pattern must have a group named v (the number)");
                    targets.Add(new OptionTarget(Rel(Str(tt, "file", source, tat), source), Str(tt, "section", source, tat), Str(tt, "key", source, tat), rx));
                }
                list.Add(new OptionSettingDef(id, Str(s, "label", source, at), opts, Str(s, "desc", source, at), Str(s, "unit", source, at), min, max, warn, targets));
            }
            return new OptionCatalog(file, Str(t, "nameKey", source, "text."), Str(t, "descriptionKey", source, "text."), Int(t, "maxNameLength", source, "text."),
                Int(t, "maxDescriptionLines", source, "text."), Int(t, "maxDescriptionLineLength", source, "text."), list);
        }
        catch (JsonException e) { throw Bad(source, "not valid JSON: " + e.Message); }
    }

    static string Rel(string path, string source)
    {
        if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path) || path.Contains('\\') || path.StartsWith('/'))
            throw Bad(source, $"'{path}' must be a relative path with forward slashes inside the mod");
        return path;
    }

    static EditorRefusalException Bad(string src, string msg) => new("OPTION_CATALOG_INVALID", $"{src}: {msg}");

    static void Only(JsonElement o, string src, string at, params string[] allowed)
    {
        foreach (var p in o.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw Bad(src, $"unknown member '{at}{p.Name}' (allowed: {string.Join(", ", allowed)})");
    }

    static JsonElement Obj(JsonElement o, string name, string src) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw Bad(src, $"'{name}' must be an object");

    static string Str(JsonElement o, string name, string src, string at) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString()! : throw Bad(src, $"'{at}{name}' must be a non-empty string");

    static long Long(JsonElement o, string name, string src, string at) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : throw Bad(src, $"'{at}{name}' must be a whole number");

    static int Int(JsonElement o, string name, string src, string at) => checked((int)Long(o, name, src, at));
}
