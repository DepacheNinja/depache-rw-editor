using RwEditor.Core;
using RwEditor.Core.Settings;
using Xunit;

namespace RwEditor.Tests;

public class SettingsJsonTests
{
    [Fact]
    public void Round_trip_is_byte_identical_and_hash_matches()
    {
        string a = SettingsJson.Serialize(TestMod.Sample2());
        var r = SettingsJson.Parse(a);
        Assert.Equal(HashStatus.Match, r.Hash);
        Assert.Equal(a, SettingsJson.Serialize(r.Settings));
        Assert.Equal(SettingsJson.ContentHash(TestMod.Sample2()), r.StoredHash);
    }

    [Fact]
    public void Canonical_form_sorts_members_uses_LF_and_two_space_indent()
    {
        string a = SettingsJson.Serialize(TestMod.Sample2());
        Assert.DoesNotContain('\r', a);
        Assert.EndsWith("}\n", a);
        Assert.StartsWith("{\n  \"contentHash\": \"sha256:", a);
        int iHash = a.IndexOf("\"contentHash\"", StringComparison.Ordinal), iSchema = a.IndexOf("\"schema\"", StringComparison.Ordinal);
        int iGroups = a.IndexOf("\"spawnGroups\"", StringComparison.Ordinal), iOpts = a.IndexOf("\"startOptions\"", StringComparison.Ordinal);
        Assert.True(iHash < iSchema && iSchema < iGroups && iGroups < iOpts);
        Assert.Contains("\"name\": \"Hover strike\"", a);
    }

    [Fact]
    public void Member_order_formatting_and_group_order_do_not_change_the_hash_or_the_canonical_text()
    {
        const string messy = """
            { "startOptions": { "extraACV": "hidden", "extraSCB": "shown" },
              "spawnGroups": [
                { "units": [ { "offsets": [ { "y": 40, "x": -300 } ], "unit": "hoverBuilder" }, { "count": 1, "unit": "heliBase", "offsets": [ { "x": -380, "y": -30 } ] } ],
                  "enabled": true, "name": "Builders", "slot": 2, "mode": "humansOnly" },
                { "slot": 1, "name": "Hover strike", "defaultOn": true, "units": [
                    { "unit": "expHoverTank", "count": 2, "offsets": [ { "x": 300, "y": -60 }, { "x": 300, "y": 10 } ] },
                    { "unit": "Valkyrie", "offsets": [ { "x": 370, "y": -25 } ] } ] }
              ],
              "schema": 1 }
            """;
        var r = SettingsJson.Parse(messy);
        Assert.Equal(HashStatus.Absent, r.Hash);
        Assert.Equal(SettingsJson.ContentHash(TestMod.Sample2()), r.ComputedHash);
        Assert.Equal(SettingsJson.Serialize(TestMod.Sample2()), SettingsJson.Serialize(r.Settings));
    }

    [Fact]
    public void Edited_file_is_reported_as_hash_mismatch()
    {
        string a = SettingsJson.Serialize(TestMod.Sample2()).Replace("Hover strike", "Hover strikes");
        Assert.Equal(HashStatus.Mismatch, SettingsJson.Parse(a).Hash);
    }

    [Fact]
    public void Base_mod_reference_round_trips()
    {
        var s = TestMod.Sample2();
        s.BaseMod = new BaseModRef("1.0.6", 1, 583, new string('a', 64));
        var r = SettingsJson.Parse(SettingsJson.Serialize(s));
        Assert.Equal(s.BaseMod, r.Settings.BaseMod);
        Assert.Equal(HashStatus.Match, r.Hash);
        Assert.NotEqual(SettingsJson.ContentHash(TestMod.Sample2()), SettingsJson.ContentHash(s));
    }

    [Theory]
    [InlineData("""{ "schema": 1, "spawnGroups": [], "colour": "red" }""", "unknown member 'colour'")]
    [InlineData("""{ "schema": 1, "spawnGroups": [ { "slot": 1, "name": "A", "units": [], "mood": 1 } ] }""", "unknown member 'spawnGroups[0].mood'")]
    [InlineData("""{ "schema": 1, "spawnGroups": [ { "slot": 1, "name": "A", "mode": "everyone", "units": [] } ] }""", "not one of followToggle, humansOnly")]
    [InlineData("""{ "schema": 1, "startOptions": { "extraACV": "gone" } }""", "not one of shown, hidden, deleted")]
    [InlineData("""{ "schema": 1, "spawnGroups": [ { "slot": "1", "name": "A", "units": [] } ] }""", "must be a whole number")]
    [InlineData("""{ "schema": 1, "spawnGroups": [ { "slot": 1.5, "name": "A", "units": [] } ] }""", "must be a whole number")]
    [InlineData("""{ "schema": 1, "spawnGroups": [ { "slot": 1, "name": "A", "units": [ { "unit": "x", "offsets": [ [1, 2] ] } ] } ] }""", "must be an object")]
    [InlineData("""{ "schema": 1, "schema": 1 }""", "")]
    [InlineData("""{ "spawnGroups": [] }""", "'schema' is required")]
    [InlineData("""[1, 2]""", "top level must be an object")]
    [InlineData("""{ "schema": 1, """, "not valid JSON")]
    public void Strict_reader_refuses_bad_input(string json, string expect)
    {
        var e = Assert.ThrowsAny<EditorRefusalException>(() => SettingsJson.Parse(json, "t.json"));
        Assert.Contains(expect, e.Message);
    }

    [Fact]
    public void Unsupported_schema_is_refused()
    {
        var e = Assert.Throws<EditorRefusalException>(() => SettingsJson.Parse("""{ "schema": 2 }"""));
        Assert.Equal("SETTINGS_SCHEMA", e.Code);
    }

    [Fact]
    public void Revert_operations()
    {
        var s = TestMod.Sample2();
        s.SetOptionState("extraSCB", OptionState.Deleted);
        Assert.True(s.RevertGroup(2));
        Assert.False(s.RevertGroup(7));
        Assert.Single(s.SpawnGroups);
        s.RevertOption("extraACV");
        Assert.Equal(OptionState.Shown, s.GetOptionState("extraACV"));
        Assert.Equal(OptionState.Deleted, s.GetOptionState("extraSCB"));
        s.RevertSection4(new[] { "extraSCB" });
        Assert.Empty(s.SpawnGroups);
        Assert.Empty(s.StartOptions);
        var t = TestMod.Sample2();
        t.BaseMod = new BaseModRef("1", 1, 1, "x");
        t.RevertAll();
        Assert.Equal(SettingsJson.ContentHash(new EditorSettings { BaseMod = t.BaseMod }), SettingsJson.ContentHash(t));
    }
}
