using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RwEditor.Core.Model;

namespace RwEditor.Core.Stats;

public enum StatTier { Simple, Expand }

public enum AppliesTo { Both, Units, Buildings }

/// <summary>One whitelisted stat (knowledge/stat-dictionary.json). Limits are in the entry's canonical unit: frames for Time, seconds for BuildTime.</summary>
public sealed record StatEntry(
    string Id, string Key, string? Field, IReadOnlyList<string> Sections, StatType Type, StatTier Tier, AppliesTo AppliesTo,
    string Label, string Desc, string Up, string Down, string Unit,
    double HardMin, double HardMax, double? WarnMin, double? WarnMax, double? RelWarnMin, double? RelWarnMax,
    IReadOnlyList<string> Rules, bool Verified, string Evidence, string? ShippedOutsideHard = null, bool Negate = false)
{
    readonly IReadOnlyList<Regex> _sectionRx = Sections.Select(SectionGlob).ToList();

    public bool MatchesSection(string section) => _sectionRx.Any(r => r.IsMatch(section));

    /// <summary>Section patterns: exact name, or a trailing '*' (e.g. turret_*); case-insensitive like the readers.</summary>
    static Regex SectionGlob(string p) => new("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the entry has exactly one section and no wildcard (core, movement, attack): --section can be left out.</summary>
    public string? FixedSection => Sections.Count == 1 && !Sections[0].Contains('*') ? Sections[0] : null;
}

public sealed record StatRule(string Id, Severity Severity, bool Verified, string Desc, string Evidence);

/// <summary>
/// The stat dictionary: a WHITELIST. A key that is not in it is never shown as editable (DESIGN_EDITOR 1.3 / 1.4). Loaded from the embedded copy of
/// knowledge/stat-dictionary.json (or a given file for checks) and validated strictly; any problem refuses the whole dictionary.
/// </summary>
public sealed class StatDictionary
{
    public const int SupportedSchema = 1;
    public const string ResourceName = "RwEditor.Data.stat-dictionary.json";

    StatDictionary(IReadOnlyList<StatEntry> entries, IReadOnlyList<StatRule> rules, IReadOnlyList<string> neverEditable)
    {
        Entries = entries; Rules = rules; NeverEditable = neverEditable;
        _neverRx = neverEditable.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    readonly List<Regex> _neverRx;

    public IReadOnlyList<StatEntry> Entries { get; }
    public IReadOnlyList<StatRule> Rules { get; }
    /// <summary>Key patterns that may never be editable, whatever the entries say (logic, ids, tags, images, movementType, techLevel...).</summary>
    public IReadOnlyList<string> NeverEditable { get; }

    public bool IsNeverEditable(string key) => _neverRx.Any(r => r.IsMatch(key.Trim()));

    public StatEntry? ById(string id) => Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Entries for an ini key in a given section (several when a key has fields, e.g. price credits / rust).</summary>
    public IEnumerable<StatEntry> For(string section, string key) =>
        Entries.Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.MatchesSection(section));

    /// <summary>Resolves what a user typed: an entry id ("core.maxHp", "turret.delay", "core.price.credits") or a plain key that names exactly one entry.</summary>
    public StatEntry? Resolve(string typed, out string? error)
    {
        error = null;
        var byId = ById(typed);
        if (byId != null) return byId;
        var byKey = Entries.Where(e => e.Key.Equals(typed, StringComparison.OrdinalIgnoreCase) && e.Field == null).ToList();
        if (byKey.Count == 1) return byKey[0];
        error = byKey.Count == 0
            ? $"'{typed}' is not in the stat dictionary (only whitelisted keys can be edited; see rwedit dict-check)"
            : $"'{typed}' is ambiguous; use one of: {string.Join(", ", byKey.Select(e => e.Id))}";
        return null;
    }

    static StatDictionary? _embedded;
    public static StatDictionary Embedded => _embedded ??= Parse(EmbeddedText.Read(ResourceName), ResourceName);

    public static StatDictionary Load(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.UTF8), path);

    public static StatDictionary Parse(string json, string source)
    {
        var problems = new List<string>();
        StatDictionary d;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var r = doc.RootElement;
            Only(r, "", problems, "schema", "about", "sources", "neverEditable", "rules", "entries");
            int schema = r.GetProperty("schema").GetInt32();
            if (schema != SupportedSchema) throw new EditorRefusalException("DICTIONARY_SCHEMA", $"{source}: schema {schema}; this editor reads schema {SupportedSchema}");
            var never = r.GetProperty("neverEditable").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var rules = new List<StatRule>();
            foreach (var x in r.GetProperty("rules").EnumerateArray())
            {
                Only(x, "rules[].", problems, "id", "severity", "verified", "desc", "evidence");
                rules.Add(new StatRule(Req(x, "id"), Req(x, "severity") switch { "error" => Severity.Error, "warning" => Severity.Warning, var s => throw Bad($"rule severity '{s}'") },
                    x.GetProperty("verified").GetBoolean(), Req(x, "desc"), Req(x, "evidence")));
            }
            var entries = new List<StatEntry>();
            foreach (var x in r.GetProperty("entries").EnumerateArray())
            {
                Only(x, "entries[].", problems, "id", "key", "field", "sections", "type", "tier", "appliesTo", "label", "desc", "up", "down", "unit",
                    "hardMin", "hardMax", "warnMin", "warnMax", "relWarnMin", "relWarnMax", "rules", "verified", "evidence", "shippedOutsideHard", "negate");
                string id = Req(x, "id");
                entries.Add(new StatEntry(
                    id, Req(x, "key"), Opt(x, "field"),
                    x.GetProperty("sections").EnumerateArray().Select(s => s.GetString() ?? "").ToList(),
                    Req(x, "type") switch
                    {
                        "int" => StatType.Int, "float" => StatType.Float, "time" => StatType.Time, "buildTime" => StatType.BuildTime,
                        "partInt" => StatType.PartInt, "partFloat" => StatType.PartFloat, var t => throw Bad($"{id}: type '{t}'"),
                    },
                    Req(x, "tier") switch { "simple" => StatTier.Simple, "expand" => StatTier.Expand, var t => throw Bad($"{id}: tier '{t}'") },
                    Req(x, "appliesTo") switch { "both" => AppliesTo.Both, "units" => AppliesTo.Units, "buildings" => AppliesTo.Buildings, var t => throw Bad($"{id}: appliesTo '{t}'") },
                    Req(x, "label"), Req(x, "desc"), Req(x, "up"), Req(x, "down"), Req(x, "unit"),
                    Num(x, "hardMin")!.Value, Num(x, "hardMax")!.Value, Num(x, "warnMin"), Num(x, "warnMax"), Num(x, "relWarnMin"), Num(x, "relWarnMax"),
                    x.GetProperty("rules").EnumerateArray().Select(s => s.GetString() ?? "").ToList(),
                    x.GetProperty("verified").GetBoolean(), Req(x, "evidence"), Opt(x, "shippedOutsideHard"),
                    x.TryGetProperty("negate", out var neg) && neg.ValueKind == JsonValueKind.True));
            }
            d = new StatDictionary(entries, rules, never);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new EditorRefusalException("DICTIONARY_INVALID", $"{source} cannot be read: {e.Message}");
        }
        problems.AddRange(d.Check());
        if (problems.Count > 0) throw new EditorRefusalException("DICTIONARY_INVALID", $"{source}:\n  " + string.Join("\n  ", problems));
        return d;

        static FormatException Bad(string m) => new(m);
        static string Req(JsonElement x, string k) => x.GetProperty(k).GetString() ?? throw new FormatException($"'{k}' must be a string");
        static string? Opt(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
        static double? Num(JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetDouble() : null;
        static void Only(JsonElement o, string at, List<string> problems, params string[] allowed)
        {
            foreach (var p in o.EnumerateObject())
                if (!allowed.Contains(p.Name, StringComparer.Ordinal)) problems.Add($"unknown member '{at}{p.Name}'");
        }
    }

    /// <summary>Internal consistency rules of the dictionary itself (every one has a test).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        foreach (var dup in Entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) p.Add($"duplicate id '{dup.Key}'");
        var ruleIds = Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var dup in Rules.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)) p.Add($"duplicate rule id '{dup.Key}'");
        foreach (var e in Entries)
        {
            string at = e.Id;
            if (!Regex.IsMatch(e.Id, @"^[a-z]+\.[A-Za-z_]+(\.[A-Za-z]+)?$")) p.Add($"{at}: id must be '<family>.<key>[.<resource part>]'");
            if (IsNeverEditable(e.Key)) p.Add($"{at}: key '{e.Key}' is on the never-editable list");
            if (e.Sections.Count == 0) { p.Add($"{at}: no sections"); continue; }
            if (e.Label.Length == 0 || e.Desc.Length == 0 || e.Up.Length == 0 || e.Down.Length == 0 || e.Unit.Length == 0) p.Add($"{at}: label, desc, up, down and unit are required");
            if (e.Evidence.Length == 0) p.Add($"{at}: evidence is required");
            if (!(e.HardMin < e.HardMax)) p.Add($"{at}: hardMin {e.HardMin} must be below hardMax {e.HardMax}");
            if (e.WarnMin is double wn && (wn < e.HardMin || wn > e.HardMax)) p.Add($"{at}: warnMin outside the hard range");
            if (e.WarnMax is double wx && (wx < e.HardMin || wx > e.HardMax)) p.Add($"{at}: warnMax outside the hard range");
            if (e.WarnMin is double a && e.WarnMax is double b && a > b) p.Add($"{at}: warnMin above warnMax");
            if (e.RelWarnMin is double rn && (rn <= 0 || rn >= 1)) p.Add($"{at}: relWarnMin must be between 0 and 1");
            if (e.RelWarnMax is double rx && rx <= 1) p.Add($"{at}: relWarnMax must be above 1");
            bool partType = e.Type is StatType.PartInt or StatType.PartFloat;
            if (partType != (e.Field != null)) p.Add($"{at}: a field (the resource part, e.g. credits or rust) is required exactly for the part types (partInt, partFloat)");
            if (e.Negate && !partType) p.Add($"{at}: negate is only for part types");
            if (e.Field != null && !e.Id.EndsWith("." + e.Field, StringComparison.Ordinal)) p.Add($"{at}: the id of a part entry must end with .{e.Field}");
            // the game reads the credits part with Integer.parseInt and every other resource part with Float.parseFloat (units/custom/d/b.b)
            if (e.Type == StatType.PartInt && e.Field != "credits") p.Add($"{at}: partInt is only for the credits part (Integer.parseInt); other resources are partFloat");
            if (e.Type == StatType.PartFloat && e.Field == "credits") p.Add($"{at}: the credits part is read as a whole number: use partInt");
            if (e.Type is StatType.Int or StatType.PartInt && (e.HardMin != Math.Floor(e.HardMin) || e.HardMax != Math.Floor(e.HardMax))) p.Add($"{at}: whole-number limits are required for integer keys");
            if (e.Type is StatType.Int or StatType.PartInt && e.HardMax > int.MaxValue) p.Add($"{at}: hardMax above the Java int range");
            if (e.Type == StatType.BuildTime && e.HardMin < 0) p.Add($"{at}: a build time cannot be negative");
            if (e.Type == StatType.Time && e.HardMin < 0) p.Add($"{at}: a time cannot be negative");
            foreach (var rid in e.Rules) if (!ruleIds.Contains(rid)) p.Add($"{at}: unknown rule '{rid}'");
            string expectedFamily = e.Sections[0] switch
            {
                var s when s.StartsWith("turret_", StringComparison.OrdinalIgnoreCase) => "turret",
                var s when s.StartsWith("projectile_", StringComparison.OrdinalIgnoreCase) => "projectile",
                var s when s.StartsWith("action_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("hiddenAction_", StringComparison.OrdinalIgnoreCase) => "action",
                var s => s.ToLowerInvariant(),
            };
            if (!e.Id.StartsWith(expectedFamily + ".", StringComparison.Ordinal)) p.Add($"{at}: id must start with '{expectedFamily}.' for its first section '{e.Sections[0]}'");
        }
        foreach (var r in Rules) if (!Entries.Any(e => e.Rules.Contains(r.Id))) p.Add($"rule '{r.Id}' is not used by any entry");
        return p;
    }

    public static string Describe(StatEntry e, double v) => e.Type switch
    {
        StatType.Time => $"{StatValue.Format(v)} frames ({StatValue.Format(Math.Round(v / 60.0, 3))} s)",
        StatType.BuildTime => $"{StatValue.Format(Math.Round(v, 3))} s",
        _ => StatValue.Format(v),
    };

    internal static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}
