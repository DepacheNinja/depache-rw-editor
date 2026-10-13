using System.Text.Json;
using System.Text.RegularExpressions;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Model;

public sealed record ModArea(string Id, string Label, IReadOnlyList<string> Globs);

/// <summary>
/// Maps a unit file to its source mod area (main, fe, moo, vte, depache) from knowledge/mod-areas.json (embedded). First matching glob wins; no match = the
/// default area ("main"). Glob rules are the editor-support.json ones (<see cref="EditorSupport.GlobRegex"/>).
/// </summary>
public sealed class ModAreas
{
    public const string ResourceName = "RwEditor.Data.mod-areas.json";

    ModAreas(string defaultArea, IReadOnlyList<ModArea> areas)
    {
        DefaultArea = defaultArea; Areas = areas;
        _rx = areas.SelectMany(a => a.Globs.Select(g => (a.Id, EditorSupport.GlobRegex(g)))).ToList();
    }

    readonly List<(string Id, Regex Rx)> _rx;

    public string DefaultArea { get; }
    public IReadOnlyList<ModArea> Areas { get; }

    public string AreaOf(string relPath)
    {
        string rel = ModSnapshot.Norm(relPath);
        foreach (var (id, rx) in _rx) if (rx.IsMatch(rel)) return id;
        return DefaultArea;
    }

    public bool IsKnown(string id) => Areas.Any(a => a.Id == id);

    static ModAreas? _embedded;
    public static ModAreas Embedded => _embedded ??= Parse(EmbeddedText.Read(ResourceName), ResourceName);

    public static ModAreas Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int schema = r.GetProperty("schema").GetInt32();
            if (schema != 1) throw new EditorRefusalException("AREAS_SCHEMA", $"{source}: schema {schema} is not supported");
            string def = r.GetProperty("defaultArea").GetString() ?? "main";
            var areas = r.GetProperty("areas").EnumerateArray().Select(a => new ModArea(
                a.GetProperty("id").GetString() ?? "", a.GetProperty("label").GetString() ?? "",
                a.GetProperty("globs").EnumerateArray().Select(g => g.GetString() ?? "").ToList())).ToList();
            if (areas.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != areas.Count) throw new EditorRefusalException("AREAS_INVALID", $"{source}: duplicate area id");
            if (!areas.Any(a => a.Id == def)) throw new EditorRefusalException("AREAS_INVALID", $"{source}: defaultArea '{def}' is not an area");
            return new ModAreas(def, areas);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new EditorRefusalException("AREAS_INVALID", $"{source} cannot be read: {e.Message}");
        }
    }
}

/// <summary>Reads an embedded text resource of RwEditor.Core.</summary>
internal static class EmbeddedText
{
    public static string Read(string logicalName)
    {
        using var s = typeof(EmbeddedText).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException("embedded resource missing: " + logicalName);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }
}
