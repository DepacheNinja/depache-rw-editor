using System.Text.Json.Nodes;
using RwEditor.Core;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>The stat dictionary: the shipped file loads, has the design's shape, and every consistency rule refuses a broken dictionary.</summary>
public class DictionaryTests
{
    static readonly StatDictionary D = StatDictionary.Embedded;

    [Fact]
    public void Embedded_dictionary_loads_with_the_designed_shape()
    {
        Assert.Equal(15, D.Entries.Count(e => e.Tier == StatTier.Simple));
        Assert.InRange(D.Entries.Count, 40, 50);
        Assert.Empty(D.Check());
        // the design's 12 Simple stats (DESIGN_EDITOR 1.3) plus credits / rust produced and tech level provided (C-033)
        Assert.Equal(new[] { "attack.maxAttackRange", "core.armour", "core.borrowResourcesWhileBuilt.techLevel", "core.buildSpeed", "core.generation_resources.credits", "core.generation_resources.rust", "core.maxHp", "core.maxShield", "core.price.credits", "core.price.rust",
                "core.selfRegenRate", "movement.moveSpeed", "projectile.areaDamage", "projectile.directDamage", "turret.delay" },
            D.Entries.Where(e => e.Tier == StatTier.Simple).Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("techLevel")]
    [InlineData("movementType")]
    [InlineData("id")]
    [InlineData("tags")]
    [InlineData("image")]
    [InlineData("image_turret")]
    [InlineData("@copyFromSection")]
    [InlineData("@define x")]
    [InlineData("copyFrom")]
    [InlineData("autoTrigger")]
    [InlineData("spawnUnits")]
    [InlineData("radius")]
    [InlineData("isBuilding")]
    [InlineData("ai_upgradePriority")]
    public void Logic_ids_tags_images_movementType_techLevel_are_never_editable(string key)
    {
        Assert.True(D.IsNeverEditable(key));
        Assert.Null(D.Resolve(key, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Resolve_accepts_ids_and_unique_plain_keys_only()
    {
        Assert.Equal("core.maxHp", D.Resolve("maxHp", out _)!.Id);
        Assert.Equal("core.maxHp", D.Resolve("core.maxHp", out _)!.Id);
        Assert.Null(D.Resolve("aimOffsetSpread", out var amb));      // attack and turret both have it
        Assert.Contains("ambiguous", amb);
        Assert.Null(D.Resolve("price", out _));                      // a price needs .credits / .rust
    }

    static JsonObject BaseJson() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "stat-dictionary.json")))!.AsObject();

    static string Refusal(Action<JsonObject> mutate)
    {
        var j = BaseJson();
        mutate(j);
        var e = Assert.Throws<EditorRefusalException>(() => StatDictionary.Parse(j.ToJsonString(), "test"));
        Assert.Equal("DICTIONARY_INVALID", e.Code);
        return e.Message;
    }

    static JsonObject Entry(JsonObject j, string id) => j["entries"]!.AsArray().Select(x => x!.AsObject()).First(x => (string?)x["id"] == id);

    [Fact] public void Refuses_a_duplicate_id() => Assert.Contains("duplicate id", Refusal(j => j["entries"]!.AsArray().Add(Entry(j, "core.maxHp").DeepClone())));
    [Fact] public void Refuses_a_bad_id() => Assert.Contains("id must be", Refusal(j => Entry(j, "core.maxHp")["id"] = "maxHp"));
    [Fact] public void Refuses_a_never_editable_key() => Assert.Contains("never-editable", Refusal(j => Entry(j, "core.maxHp")["key"] = "techLevel"));
    [Fact] public void Refuses_no_sections() => Assert.Contains("no sections", Refusal(j => Entry(j, "core.maxHp")["sections"] = new JsonArray()));
    [Fact] public void Refuses_missing_texts() => Assert.Contains("required", Refusal(j => Entry(j, "core.maxHp")["up"] = ""));
    [Fact] public void Refuses_missing_evidence() => Assert.Contains("evidence", Refusal(j => Entry(j, "core.maxHp")["evidence"] = ""));
    [Fact] public void Refuses_inverted_hard_limits() => Assert.Contains("must be below hardMax", Refusal(j => Entry(j, "core.maxHp")["hardMin"] = 1e9));
    [Fact] public void Refuses_warn_limits_outside_the_hard_range() => Assert.Contains("warnMax outside", Refusal(j => Entry(j, "core.maxHp")["warnMax"] = 1e12));
    [Fact] public void Refuses_crossed_warn_limits() => Assert.Contains("warnMin above warnMax", Refusal(j => { Entry(j, "core.maxHp")["warnMin"] = 50; Entry(j, "core.maxHp")["warnMax"] = 10; }));
    [Fact] public void Refuses_bad_relative_limits() => Assert.Contains("relWarnMax", Refusal(j => Entry(j, "core.maxHp")["relWarnMax"] = 0.5));
    [Fact] public void Refuses_bad_relative_min() => Assert.Contains("relWarnMin", Refusal(j => Entry(j, "core.maxHp")["relWarnMin"] = 2));
    [Fact] public void Refuses_a_field_on_a_plain_type() => Assert.Contains("field", Refusal(j => Entry(j, "core.maxHp")["field"] = "credits"));
    [Fact] public void Refuses_a_price_without_field() => Assert.Contains("field", Refusal(j => Entry(j, "core.price.credits")["field"] = null));
    [Fact] public void Refuses_a_credits_type_with_the_rust_field() => Assert.Contains("partInt is only for the credits part", Refusal(j => Entry(j, "core.price.credits")["field"] = "rust"));
    [Fact] public void Refuses_fractional_integer_limits() => Assert.Contains("whole-number", Refusal(j => Entry(j, "core.maxHp")["hardMin"] = 0.5));
    [Fact] public void Refuses_limits_beyond_java_int() => Assert.Contains("Java int", Refusal(j => Entry(j, "core.maxHp")["hardMax"] = 3e9));
    [Fact] public void Refuses_a_negative_build_time() => Assert.Contains("build time cannot be negative", Refusal(j => Entry(j, "core.buildSpeed")["hardMin"] = -1));
    [Fact] public void Refuses_a_negative_time() => Assert.Contains("time cannot be negative", Refusal(j => Entry(j, "turret.delay")["hardMin"] = -1));
    [Fact] public void Refuses_an_unknown_rule() => Assert.Contains("unknown rule", Refusal(j => Entry(j, "core.maxHp")["rules"] = new JsonArray("noSuchRule")));
    [Fact] public void Refuses_a_family_mismatch() => Assert.Contains("must start with", Refusal(j => Entry(j, "core.maxHp")["sections"] = new JsonArray("movement")));
    [Fact] public void Refuses_an_unused_rule() => Assert.Contains("not used", Refusal(j => j["rules"]!.AsArray().Add(new JsonObject { ["id"] = "spare", ["severity"] = "warning", ["verified"] = false, ["desc"] = "d", ["evidence"] = "e" })));
    [Fact] public void Refuses_unknown_members() => Assert.Contains("unknown member", Refusal(j => Entry(j, "core.maxHp")["hardmax"] = 5));

    [Fact]
    public void Refuses_unknown_type_and_schema()
    {
        Assert.Contains("type", Refusal(j => Entry(j, "core.maxHp")["type"] = "double"));
        var j2 = BaseJson();
        j2["schema"] = 2;
        Assert.Equal("DICTIONARY_SCHEMA", Assert.Throws<EditorRefusalException>(() => StatDictionary.Parse(j2.ToJsonString(), "t")).Code);
    }

    [Fact]
    public void Section_patterns_match_like_the_readers()
    {
        var delay = D.ById("turret.delay")!;
        Assert.True(delay.MatchesSection("turret_1"));
        Assert.True(delay.MatchesSection("TURRET_gun"));
        Assert.False(delay.MatchesSection("projectile_1"));
        Assert.Null(delay.FixedSection);
        Assert.Equal("core", D.ById("core.maxHp")!.FixedSection);
    }

    [Fact]
    public void Embedded_mod_areas_load() => Assert.Equal(new[] { "main", "fe", "moo", "vte", "depache" }, ModAreas.Embedded.Areas.Select(a => a.Id));
}
