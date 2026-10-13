using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RwEditor.Core.Settings;

public enum HashStatus { Absent, Match, Mismatch }

/// <summary>Result of reading a settings file.</summary>
public sealed record SettingsLoadResult(EditorSettings Settings, HashStatus Hash, string? StoredHash, string ComputedHash);

/// <summary>
/// Canonical, deterministic JSON for <see cref="EditorSettings"/> (DESIGN_SPEC 6c, profile-ready):
/// - object keys sorted ordinally at every level; groups sorted by slot; units keep their order (it is the spawn order); defaults are written explicitly
///   except option states, where Shown is the absence of an entry;
/// - UTF-8 without BOM, two-space indent, LF line ends, one trailing LF;
/// - "contentHash" = "sha256:" + SHA-256 of the canonical text of the same object WITHOUT the contentHash member.
/// Reading is strict: unknown members, wrong types and unknown enum values are errors, so a typo can never be silently ignored.
/// </summary>
public static class SettingsJson
{
    public const string HashPrefix = "sha256:";

    static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ write
    public static string Serialize(EditorSettings s)
    {
        var body = ToNode(s);
        string hash = HashOfNode(body);
        body["contentHash"] = hash;
        return Write(Sort(body));
    }

    public static byte[] SerializeBytes(EditorSettings s) => new UTF8Encoding(false).GetBytes(Serialize(s));

    /// <summary>The content hash of a settings object ("sha256:..."), independent of member order or formatting in any file it came from.</summary>
    public static string ContentHash(EditorSettings s) => HashOfNode(ToNode(s));

    static string HashOfNode(JsonObject bodyWithoutHash) =>
        HashPrefix + Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(Write(Sort(bodyWithoutHash)))));

    static JsonObject ToNode(EditorSettings s)
    {
        var o = new JsonObject { ["schema"] = s.Schema };
        if (s.BaseMod != null)
            o["baseMod"] = new JsonObject
            {
                ["modVersion"] = s.BaseMod.ModVersion,
                ["editorSupportSchema"] = s.BaseMod.EditorSupportSchema,
                ["lockedFileCount"] = s.BaseMod.LockedFileCount,
                ["lockedManifestSha256"] = s.BaseMod.LockedManifestSha256,
            };
        var groups = new JsonArray();
        foreach (var g in s.SpawnGroups.OrderBy(g => g.Slot))
        {
            var units = new JsonArray();
            foreach (var u in g.Units)
            {
                var uo = new JsonObject { ["unit"] = u.Unit, ["count"] = u.Count };
                if (u.Offsets != null)
                {
                    var offs = new JsonArray();
                    foreach (var p in u.Offsets) offs.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
                    uo["offsets"] = offs;
                }
                units.Add(uo);
            }
            groups.Add(new JsonObject
            {
                ["slot"] = g.Slot,
                ["name"] = g.Name,
                ["mode"] = ModeToString(g.Mode),
                ["defaultOn"] = g.DefaultOn,
                ["enabled"] = g.Enabled,
                ["units"] = units,
            });
        }
        o["spawnGroups"] = groups;
        var opts = new JsonObject();
        foreach (var kv in s.StartOptions.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (kv.Value != OptionState.Shown) opts[kv.Key] = StateToString(kv.Value);
        o["startOptions"] = opts;
        // E1b-2: optional; written only when there are stat edits so every E1b-1 file keeps its exact text and contentHash
        if (s.StatEdits.Count > 0)
        {
            var edits = new JsonArray();
            foreach (var e in s.StatEdits.OrderBy(e => e.Seq))
                edits.Add(new JsonObject
                {
                    ["seq"] = e.Seq,
                    ["unit"] = e.Unit,
                    ["unitFile"] = e.UnitFile,
                    ["area"] = e.Area,
                    ["statId"] = e.StatId,
                    ["section"] = e.Section,
                    ["mode"] = e.Mode,
                    ["writeKind"] = WriteKindToString(e.WriteKind),
                    ["file"] = e.File,
                    ["fileSection"] = e.FileSection,
                    ["key"] = e.Key,
                    ["oldText"] = e.OldText,
                    ["newText"] = e.NewText,
                    ["eolAdded"] = e.EolAdded,
                    ["affectedUnits"] = e.AffectedUnits,
                });
            o["statEdits"] = edits;
        }
        // E3: optional; written only when changes are staged (list order = staging order, which is deterministic)
        if (s.PendingStatEdits.Count > 0)
        {
            var pend = new JsonArray();
            foreach (var p in s.PendingStatEdits)
            {
                var po = new JsonObject
                {
                    ["unit"] = p.Unit,
                    ["unitFile"] = p.UnitFile,
                    ["statId"] = p.StatId,
                    ["section"] = p.Section,
                    ["mode"] = p.Mode,
                    ["value"] = p.Value,
                };
                if (p.FromFamilyOf != null) po["fromFamilyOf"] = p.FromFamilyOf;
                pend.Add(po);
            }
            o["pendingStatEdits"] = pend;
        }
        // E5b: optional; written only when something is changed in the locked option files
        if (!s.Tuning.IsEmpty)
        {
            var t = new JsonObject();
            if (s.Tuning.Values.Count > 0)
            {
                var vals = new JsonObject();
                foreach (var kv in s.Tuning.Values) vals[kv.Key] = kv.Value;
                t["values"] = vals;
            }
            if (s.Tuning.Texts.Count > 0)
            {
                var texts = new JsonObject();
                foreach (var kv in s.Tuning.Texts)
                {
                    var to = new JsonObject();
                    if (kv.Value.Name != null) to["name"] = kv.Value.Name;
                    if (kv.Value.Description != null) to["description"] = kv.Value.Description;
                    texts[kv.Key] = to;
                }
                t["texts"] = texts;
            }
            o["optionTuning"] = t;
        }
        if (s.OptionEdits.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var e in s.OptionEdits.OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line))
                arr.Add(new JsonObject { ["file"] = e.File, ["line"] = e.Line, ["section"] = e.Section, ["key"] = e.Key, ["oldLine"] = e.OldLine, ["newLine"] = e.NewLine });
            o["optionEdits"] = arr;
        }
        return o;
    }

    public static string WriteKindToString(StatWriteKind k) => k switch
    {
        StatWriteKind.Patch => "patch", StatWriteKind.Insert => "insert", StatWriteKind.InsertSection => "insertSection",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>Deep copy with every object's members sorted ordinally (arrays keep their order).</summary>
    static JsonNode? Sort(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        null => null,
        _ => JsonNode.Parse(n.ToJsonString()),
    };

    static string Write(JsonNode? n)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) n!.WriteTo(w);
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static string ModeToString(GroupMode m) => m switch { GroupMode.FollowToggle => "followToggle", GroupMode.HumansOnly => "humansOnly", _ => throw new ArgumentOutOfRangeException(nameof(m)) };
    public static string StateToString(OptionState s) => s switch { OptionState.Shown => "shown", OptionState.Hidden => "hidden", OptionState.Deleted => "deleted", _ => throw new ArgumentOutOfRangeException(nameof(s)) };

    // ------------------------------------------------------------------ read
    public static SettingsLoadResult Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8), path);

    public static SettingsLoadResult Parse(string json, string sourceName = "settings")
    {
        try { return ParseCore(json, sourceName); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            // e.g. a member name given twice, or a value of an unexpected JSON kind
            throw Bad(sourceName, e.Message);
        }
    }

    static SettingsLoadResult ParseCore(string json, string sourceName)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false }); }
        catch (JsonException e) { throw Bad(sourceName, "not valid JSON: " + e.Message); }
        if (root is not JsonObject o) throw Bad(sourceName, "the top level must be an object");
        Only(o, sourceName, "", "schema", "baseMod", "spawnGroups", "startOptions", "statEdits", "pendingStatEdits", "optionTuning", "optionEdits", "contentHash");

        var s = new EditorSettings { Schema = Int(o, "schema", sourceName, "", required: true)!.Value };
        if (s.Schema != EditorSettings.CurrentSchema)
            throw new EditorRefusalException("SETTINGS_SCHEMA", $"{sourceName}: settings schema {s.Schema} is not supported (this editor reads schema {EditorSettings.CurrentSchema}).");

        if (o["baseMod"] is JsonNode bm)
        {
            if (bm is not JsonObject b) throw Bad(sourceName, "baseMod must be an object");
            Only(b, sourceName, "baseMod.", "modVersion", "editorSupportSchema", "lockedFileCount", "lockedManifestSha256");
            s.BaseMod = new BaseModRef(Str(b, "modVersion", sourceName, "baseMod.")!, Int(b, "editorSupportSchema", sourceName, "baseMod.", true)!.Value,
                Int(b, "lockedFileCount", sourceName, "baseMod.", true)!.Value, Str(b, "lockedManifestSha256", sourceName, "baseMod.")!);
        }

        if (o["spawnGroups"] is JsonNode gn)
        {
            if (gn is not JsonArray ga) throw Bad(sourceName, "spawnGroups must be an array");
            int gi = 0;
            foreach (var item in ga)
            {
                string at = $"spawnGroups[{gi++}].";
                if (item is not JsonObject g) throw Bad(sourceName, at + " must be an object");
                Only(g, sourceName, at, "slot", "name", "mode", "defaultOn", "enabled", "units");
                var grp = new SpawnGroup
                {
                    Slot = Int(g, "slot", sourceName, at, true)!.Value,
                    Name = Str(g, "name", sourceName, at)!,
                    Mode = Str(g, "mode", sourceName, at, required: false) switch
                    {
                        null or "followToggle" => GroupMode.FollowToggle,
                        "humansOnly" => GroupMode.HumansOnly,
                        var x => throw Bad(sourceName, $"{at}mode '{x}' is not one of followToggle, humansOnly"),
                    },
                    DefaultOn = Bool(g, "defaultOn", sourceName, at) ?? false,
                    Enabled = Bool(g, "enabled", sourceName, at) ?? true,
                };
                if (g["units"] is not JsonArray ua) throw Bad(sourceName, at + "units must be an array");
                int ui = 0;
                foreach (var un in ua)
                {
                    string uat = $"{at}units[{ui++}].";
                    if (un is not JsonObject u) throw Bad(sourceName, uat + " must be an object");
                    Only(u, sourceName, uat, "unit", "count", "offsets");
                    var gu = new GroupUnit { Unit = Str(u, "unit", sourceName, uat)!, Count = Int(u, "count", sourceName, uat, required: false) ?? 1 };
                    if (u["offsets"] is JsonNode on)
                    {
                        if (on is not JsonArray oa) throw Bad(sourceName, uat + "offsets must be an array");
                        gu.Offsets = new List<Offset>();
                        int oi = 0;
                        foreach (var p in oa)
                        {
                            string oat = $"{uat}offsets[{oi++}].";
                            if (p is not JsonObject po) throw Bad(sourceName, oat + " must be an object {\"x\": .., \"y\": ..}");
                            Only(po, sourceName, oat, "x", "y");
                            gu.Offsets.Add(new Offset(Int(po, "x", sourceName, oat, true)!.Value, Int(po, "y", sourceName, oat, true)!.Value));
                        }
                    }
                    grp.Units.Add(gu);
                }
                s.SpawnGroups.Add(grp);
            }
        }

        if (o["startOptions"] is JsonNode sn)
        {
            if (sn is not JsonObject so) throw Bad(sourceName, "startOptions must be an object of tag -> \"hidden\" | \"deleted\" | \"shown\"");
            foreach (var kv in so)
            {
                if (kv.Value is not JsonValue v || !v.TryGetValue<string>(out var st)) throw Bad(sourceName, $"startOptions.{kv.Key} must be a string");
                var state = st switch
                {
                    "shown" => OptionState.Shown,
                    "hidden" => OptionState.Hidden,
                    "deleted" => OptionState.Deleted,
                    _ => throw Bad(sourceName, $"startOptions.{kv.Key}: '{st}' is not one of shown, hidden, deleted"),
                };
                s.SetOptionState(kv.Key, state);
            }
        }

        if (o["statEdits"] is JsonNode en)
        {
            if (en is not JsonArray ea) throw Bad(sourceName, "statEdits must be an array");
            int ei = 0;
            foreach (var item in ea)
            {
                string at = $"statEdits[{ei++}].";
                if (item is not JsonObject e) throw Bad(sourceName, at + " must be an object");
                Only(e, sourceName, at, "seq", "unit", "unitFile", "area", "statId", "section", "mode", "writeKind", "file", "fileSection", "key", "oldText", "newText", "eolAdded", "affectedUnits");
                var se = new StatEdit
                {
                    Seq = Int(e, "seq", sourceName, at, true)!.Value,
                    Unit = Str(e, "unit", sourceName, at)!,
                    UnitFile = Str(e, "unitFile", sourceName, at)!,
                    Area = Str(e, "area", sourceName, at)!,
                    StatId = Str(e, "statId", sourceName, at)!,
                    Section = Str(e, "section", sourceName, at)!,
                    Mode = Str(e, "mode", sourceName, at) switch { "unit" => "unit", "template" => "template", var x => throw Bad(sourceName, $"{at}mode '{x}' is not one of unit, template") },
                    WriteKind = Str(e, "writeKind", sourceName, at) switch
                    {
                        "patch" => StatWriteKind.Patch, "insert" => StatWriteKind.Insert, "insertSection" => StatWriteKind.InsertSection,
                        var x => throw Bad(sourceName, $"{at}writeKind '{x}' is not one of patch, insert, insertSection"),
                    },
                    File = Str(e, "file", sourceName, at)!,
                    FileSection = Str(e, "fileSection", sourceName, at)!,
                    Key = Str(e, "key", sourceName, at)!,
                    OldText = e["oldText"] is null ? null : Str(e, "oldText", sourceName, at),
                    NewText = Str(e, "newText", sourceName, at)!,
                    EolAdded = Bool(e, "eolAdded", sourceName, at) ?? false,
                    AffectedUnits = Int(e, "affectedUnits", sourceName, at, true)!.Value,
                };
                if ((se.WriteKind == StatWriteKind.Patch) != (se.OldText != null))
                    throw Bad(sourceName, $"{at}oldText must be set exactly for writeKind patch");
                if (s.StatEdits.Any(x => x.Seq == se.Seq)) throw Bad(sourceName, $"{at}seq {se.Seq} is used twice");
                s.StatEdits.Add(se);
            }
            s.StatEdits.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        }

        if (o["pendingStatEdits"] is JsonNode pn)
        {
            if (pn is not JsonArray pa) throw Bad(sourceName, "pendingStatEdits must be an array");
            int pi = 0;
            foreach (var item in pa)
            {
                string at = $"pendingStatEdits[{pi++}].";
                if (item is not JsonObject e) throw Bad(sourceName, at + " must be an object");
                Only(e, sourceName, at, "unit", "unitFile", "statId", "section", "mode", "value", "fromFamilyOf");
                var p = new PendingStatEdit
                {
                    Unit = Str(e, "unit", sourceName, at)!,
                    UnitFile = Str(e, "unitFile", sourceName, at)!,
                    StatId = Str(e, "statId", sourceName, at)!,
                    Section = Str(e, "section", sourceName, at)!,
                    Mode = Str(e, "mode", sourceName, at) switch { "unit" => "unit", "template" => "template", var x => throw Bad(sourceName, $"{at}mode '{x}' is not one of unit, template") },
                    Value = Str(e, "value", sourceName, at)!,
                    FromFamilyOf = Str(e, "fromFamilyOf", sourceName, at, required: false),
                };
                if (s.PendingStatEdits.Any(x => x.SameTarget(p))) throw Bad(sourceName, $"{at}{p.Unit} {p.StatId} [{p.Section}] is staged twice");
                s.PendingStatEdits.Add(p);
            }
        }

        if (o["optionTuning"] is JsonNode tn)
        {
            if (tn is not JsonObject to) throw Bad(sourceName, "optionTuning must be an object");
            Only(to, sourceName, "optionTuning.", "values", "texts");
            if (to["values"] is JsonNode vn)
            {
                if (vn is not JsonObject vo) throw Bad(sourceName, "optionTuning.values must be an object of setting id -> value text");
                foreach (var kv in vo)
                {
                    if (kv.Value is not JsonValue v || !v.TryGetValue<string>(out var vs)) throw Bad(sourceName, $"optionTuning.values.{kv.Key} must be a string");
                    s.Tuning.Values[kv.Key] = vs;
                }
            }
            if (to["texts"] is JsonNode xn)
            {
                if (xn is not JsonObject xo) throw Bad(sourceName, "optionTuning.texts must be an object of option tag -> {name, description}");
                foreach (var kv in xo)
                {
                    string at = $"optionTuning.texts.{kv.Key}.";
                    if (kv.Value is not JsonObject t) throw Bad(sourceName, at + " must be an object");
                    Only(t, sourceName, at, "name", "description");
                    var ot = new OptionText { Name = Str(t, "name", sourceName, at, required: false), Description = Str(t, "description", sourceName, at, required: false) };
                    if (!ot.IsEmpty) s.Tuning.Texts[kv.Key] = ot;
                }
            }
        }

        if (o["optionEdits"] is JsonNode on2)
        {
            if (on2 is not JsonArray oa2) throw Bad(sourceName, "optionEdits must be an array");
            int oi2 = 0;
            foreach (var item in oa2)
            {
                string at = $"optionEdits[{oi2++}].";
                if (item is not JsonObject e) throw Bad(sourceName, at + " must be an object");
                Only(e, sourceName, at, "file", "line", "section", "key", "oldLine", "newLine");
                var oe = new OptionEdit
                {
                    File = Str(e, "file", sourceName, at)!,
                    Line = Int(e, "line", sourceName, at, true)!.Value,
                    Section = Str(e, "section", sourceName, at)!,
                    Key = Str(e, "key", sourceName, at)!,
                    OldLine = Str(e, "oldLine", sourceName, at)!,
                    NewLine = Str(e, "newLine", sourceName, at)!,
                };
                if (oe.Line < 1) throw Bad(sourceName, $"{at}line must be 1 or more");
                if (s.OptionEdits.Any(x => x.File == oe.File && x.Line == oe.Line)) throw Bad(sourceName, $"{at}{oe.File} line {oe.Line} is recorded twice");
                s.OptionEdits.Add(oe);
            }
        }

        string computed = ContentHash(s);
        string? stored = null;
        if (o["contentHash"] is JsonNode hn)
        {
            if (hn is not JsonValue hv || !hv.TryGetValue<string>(out stored)) throw Bad(sourceName, "contentHash must be a string");
        }
        var status = stored == null ? HashStatus.Absent : string.Equals(stored, computed, StringComparison.Ordinal) ? HashStatus.Match : HashStatus.Mismatch;
        return new SettingsLoadResult(s, status, stored, computed);
    }

    static EditorRefusalException Bad(string src, string msg) => new("SETTINGS_INVALID", $"{src}: {msg}");

    static void Only(JsonObject o, string src, string at, params string[] allowed)
    {
        foreach (var kv in o)
            if (!allowed.Contains(kv.Key, StringComparer.Ordinal))
                throw Bad(src, $"unknown member '{at}{kv.Key}' (allowed: {string.Join(", ", allowed)})");
    }

    static string? Str(JsonObject o, string key, string src, string at, bool required = true)
    {
        var n = o[key];
        if (n == null) { if (required) throw Bad(src, $"'{at}{key}' is required"); return null; }
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        throw Bad(src, $"'{at}{key}' must be a string");
    }

    static int? Int(JsonObject o, string key, string src, string at, bool required)
    {
        var n = o[key];
        if (n == null) { if (required) throw Bad(src, $"'{at}{key}' is required"); return null; }
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            var el = v.GetValue<JsonElement>();
            if (el.TryGetInt32(out var i)) return i;
        }
        throw Bad(src, $"'{at}{key}' must be a whole number");
    }

    static bool? Bool(JsonObject o, string key, string src, string at)
    {
        var n = o[key];
        if (n == null) return null;
        if (n is JsonValue v && (v.GetValueKind() == JsonValueKind.True || v.GetValueKind() == JsonValueKind.False)) return v.GetValue<bool>();
        throw Bad(src, $"'{at}{key}' must be true or false");
    }
}
