using System.Text;
using RwEditor.Core;
using RwEditor.Core.Ini;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Planning stat edits on the in-RAM fixture: write modes, affected units, every refusal, cross-key rules, determinism.</summary>
public class StatPatcherTests
{
    static StatEditRefusedException Refused(Func<StatEditPlan> plan, string code)
    {
        var e = Assert.Throws<StatEditRefusedException>(() => plan());
        Assert.Contains(e.Findings, f => f.Code == code && f.Severity == Severity.Error);
        return e;
    }

    static string Text(StatFixture f, StatEditPlan p, string rel) => StatFixture.Decode(p.NewBytes[rel]);

    [Fact]
    public void Own_line_is_patched_in_place_for_a_unit_only_edit()
    {
        var f = new StatFixture();
        var p = f.Plan("tankB", "maxHp", "1500");   // tankB has no own maxHp -> insert
        Assert.Equal(StatWriteKind.Insert, p.Record.WriteKind);
        f.Commit(p);
        var p2 = f.Plan("tankB", "maxHp", "1600");  // now its own line -> patch
        Assert.Equal(StatWriteKind.Patch, p2.Record.WriteKind);
        Assert.Equal("1500", p2.Record.OldText);
        Assert.Equal("[core]\nmaxHp: 1600\nname: tankB\ncopyFrom: ROOT:/templates/tank_base.ini\n", Text(f, p2, "units/tankB.ini"));
        Assert.Single(p2.Affected);
    }

    [Fact]
    public void Inherited_value_is_added_to_the_units_own_section_right_after_its_header()
    {
        var f = new StatFixture();
        var p = f.Plan("tankB", "core.price.credits", "650");
        Assert.Equal("units/tankB.ini", p.Record.File);
        Assert.Equal("credits = 650, rust = 100", p.NewRawValue);   // the inherited text with only the field changed
        Assert.Equal("[core]\nprice: credits = 650, rust = 100\nname: tankB\ncopyFrom: ROOT:/templates/tank_base.ini\n", Text(f, p, "units/tankB.ini"));
        Assert.Equal(new[] { "tankB" }, p.Affected.Select(a => a.Unit.Name));
        Assert.Equal(650, p.NewNumber);
    }

    [Fact]
    public void Missing_core_like_section_is_created_at_the_end_of_the_file()
    {
        var f = new StatFixture();
        var p = f.Plan("tankB", "movement.moveSpeed", "2.25");
        Assert.Equal(StatWriteKind.InsertSection, p.Record.WriteKind);
        Assert.EndsWith("[movement]\nmoveSpeed: 2.25\n", Text(f, p, "units/tankB.ini"));
        Assert.False(p.Record.EolAdded);
    }

    [Fact]
    public void Missing_turret_section_is_never_created()
    {
        var f = new StatFixture();
        Refused(() => f.Plan("tankB", "turret.delay", "40", section: "turret_1"), "SECTION_NOT_OWN");
    }

    [Fact]
    public void A_shared_own_line_needs_the_template_flag_and_then_lists_every_unit()
    {
        var f = new StatFixture();
        var e = Refused(() => f.Plan("tankA", "maxHp", "1300"), "NOT_UNIT_ONLY");   // tankC copies tankA's file
        Assert.Contains("tankC", e.Message);
        var p = f.Plan("tankA", "maxHp", "1300", StatEditMode.Template);
        Assert.Equal(new[] { "tankA", "tankC" }, p.Affected.Select(a => a.Unit.Name).OrderBy(x => x));
        Assert.Equal("template", p.Record.Mode);
        Assert.Equal(2, p.Record.AffectedUnits);
    }

    [Fact]
    public void Template_edit_patches_the_template_line_and_reports_every_affected_unit()
    {
        var f = new StatFixture();
        var p = f.Plan("tankB", "turret.delay", "45", StatEditMode.Template, "turret_1");
        Assert.Equal("templates/tank_base.ini", p.Record.File);
        Assert.Equal(StatWriteKind.Patch, p.Record.WriteKind);
        Assert.Equal(new[] { "tankA", "tankB", "tankC" }, p.Affected.Select(a => a.Unit.Name).OrderBy(x => x));
        Assert.Contains("delay: 45\n", Text(f, p, "templates/tank_base.ini"));
    }

    [Fact]
    public void Template_edit_that_reaches_a_locked_unit_is_refused()
    {
        var f = new StatFixture();
        var e = Refused(() => f.Plan("tankD", "maxHp", "800", StatEditMode.Template), "TEMPLATE_REACHES_LOCKED");
        Assert.Contains("lockedUnit", e.Message);
        // unit-only is fine: it does not touch the shared file
        Assert.Single(f.Plan("tankD", "maxHp", "800").Affected);
    }

    [Fact]
    public void Template_edit_of_a_locked_file_is_refused()
    {
        var f = new StatFixture();
        Refused(() => f.Plan("tankB", "maxShield", "50", StatEditMode.Template), "FILE_LOCKED");   // all-units.template
        Assert.Equal(StatWriteKind.Insert, f.Plan("tankB", "maxShield", "50").Record.WriteKind);
    }

    [Fact]
    public void Adding_a_key_to_a_file_other_units_copy_is_not_unit_only()
    {
        // tankC copies tankA's file, so a key added to tankA.ini reaches tankC as well: the model finds that by re-resolving
        var e = Refused(() => new StatFixture().Plan("tankA", "maxShield", "50"), "NOT_UNIT_ONLY");
        Assert.Contains("tankC", e.Message);
    }

    [Fact] public void Locked_unit_is_refused() => Refused(() => new StatFixture().Plan("lockedUnit", "maxHp", "9"), "UNIT_LOCKED");
    [Fact] public void Key_outside_the_dictionary_is_refused() => Refused(() => new StatFixture().Plan("tankA", "techLevel", "2"), "NOT_IN_DICTIONARY");
    [Fact] public void Radius_is_refused() => Refused(() => new StatFixture().Plan("tankA", "radius", "40"), "NOT_IN_DICTIONARY");
    [Fact] public void Out_of_range_is_refused() => Refused(() => new StatFixture().Plan("tankA", "maxHp", "0"), "OUT_OF_RANGE");
    [Fact] public void Turret_delay_zero_is_refused() => Refused(() => new StatFixture().Plan("tankA", "turret.delay", "0", StatEditMode.Template, "turret_1"), "OUT_OF_RANGE");
    [Fact] public void Decimal_in_an_integer_key_is_refused() => Refused(() => new StatFixture().Plan("tankA", "maxHp", "12.5"), "VALUE_INVALID");
    [Fact] public void Text_value_is_refused() => Refused(() => new StatFixture().Plan("tankA", "maxHp", "${x}"), "VALUE_INVALID");
    [Fact] public void Expression_value_is_read_only() => Refused(() => new StatFixture().Plan("exprUnit", "maxHp", "50"), "VALUE_READ_ONLY");
    [Fact] public void Unknown_unit_is_refused() => Refused(() => new StatFixture().Plan("tanka", "maxHp", "50"), "UNIT_UNKNOWN");
    [Fact] public void Duplicate_unit_name_is_refused() => Refused(() => new StatFixture().Plan("dupName", "maxHp", "50"), "UNIT_AMBIGUOUS");
    [Fact] public void Mobile_key_on_a_building_is_refused() => Refused(() => new StatFixture().Plan("tower", "moveSpeed", "1"), "KEY_NOT_FOR_BUILDINGS");
    [Fact] public void Wildcard_key_without_section_is_refused() => Refused(() => new StatFixture().Plan("tankA", "turret.delay", "10"), "SECTION_REQUIRED");
    [Fact] public void Section_not_allowed_for_the_key_is_refused() => Refused(() => new StatFixture().Plan("tankA", "turret.delay", "10", section: "core"), "SECTION_NOT_ALLOWED");
    [Fact] public void Same_value_is_refused() => Refused(() => new StatFixture().Plan("tankA", "maxHp", "1200"), "NO_CHANGE");

    [Fact]
    public void Build_time_plain_zero_is_refused_and_seconds_are_written_as_given()
    {
        var f = new StatFixture();
        Refused(() => f.Plan("tankB", "core.buildSpeed", "0"), "OUT_OF_RANGE");
        Refused(() => f.Plan("tankB", "buildSpeed", "25s"), "NOT_IN_DICTIONARY");   // ambiguous: core and action build speed
        var p = f.Plan("tankB", "core.buildSpeed", "25s");
        Assert.Equal("25s", p.NewRawValue);
        Assert.Equal(25, p.NewNumber);
    }

    [Fact]
    public void Cross_key_rule_limitingRange_refuses_a_new_violation()
    {
        var f = new StatFixture();
        // ranged: maxAttackRange 200; every turret sets limitingRange (250, 210): raising the range above 250 makes the game refuse the unit
        var e = Refused(() => f.Plan("ranged", "maxAttackRange", "300"), "RULE_limitingRangeVsMaxAttackRange");
        Assert.Contains("below maxAttackRange", e.Message);
        Assert.NotNull(f.Plan("ranged", "maxAttackRange", "240"));
        Refused(() => f.Plan("ranged", "turret.limitingRange", "150", section: "turret_1"), "RULE_limitingRangeVsMaxAttackRange");
    }

    [Fact]
    public void Warning_rules_and_soft_limits_warn_but_do_not_refuse()
    {
        var f = new StatFixture();
        var p = f.Plan("ranged", "turret.limitingMinRange", "260", section: "turret_1");
        Assert.Contains(p.Warnings, w => w.Code == "RULE_limitingMinRangeBelowLimitingRange");
        var p2 = f.Plan("ranged", "turret.energyUsage", "2", section: "turret_1");
        Assert.Contains(p2.Warnings, w => w.Code == "RULE_energyUsageNeedsEnergyMax");
        var p3 = f.Plan("tankB", "maxHp", "50000");
        Assert.Contains(p3.Warnings, w => w.Code == "SOFT_LIMIT_RELATIVE");
    }

    [Fact]
    public void Every_dictionary_rule_is_evaluated()
    {
        var f = new StatFixture(new Dictionary<string, string> { ["units/noDamage.ini"] = "[core]\nname: noDmg\nmaxHp: 5\n[projectile_x]\nspeed: 2\n" });
        var m = f.Model.Merge(f.Model.Find("noDmg")!);
        Assert.Single(StatRules.Evaluate(m, "projectileNeedsDamage"));
        foreach (var r in StatDictionary.Embedded.Rules) Assert.NotNull(StatRules.Evaluate(m, r.Id));
        Assert.Throws<ArgumentException>(() => StatRules.Evaluate(m, "nope"));
    }

    [Fact]
    public void Planning_is_deterministic()
    {
        var a = new StatFixture().Plan("tankB", "core.price.rust", "120");
        var b = new StatFixture().Plan("tankB", "core.price.rust", "120");
        Assert.Equal(a.NewBytes.Keys, b.NewBytes.Keys);
        foreach (var k in a.NewBytes.Keys) Assert.Equal(a.NewBytes[k], b.NewBytes[k]);
        Assert.Equal(SettingsJson.Serialize(new EditorSettings { StatEdits = { a.Record } }), SettingsJson.Serialize(new EditorSettings { StatEdits = { b.Record } }));
    }

    [Fact]
    public void Patch_preserves_BOM_CRLF_and_missing_final_line_break_exactly()
    {
        var raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[core]\r\nname: crlfUnit\r\nmaxHp: 77\r\n# keep me\r\n[movement]\r\nmoveSpeed: 1")).ToArray();
        var f = new StatFixture(rawExtra: new Dictionary<string, byte[]> { ["units/crlf.ini"] = raw });
        var p = f.Plan("crlfUnit", "maxHp", "78");
        var expect = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[core]\r\nname: crlfUnit\r\nmaxHp: 78\r\n# keep me\r\n[movement]\r\nmoveSpeed: 1")).ToArray();
        Assert.Equal(expect, p.NewBytes["units/crlf.ini"]);
        // a second patch back to the old value gives the original bytes
        f.Commit(p);
        Assert.Equal(raw, f.Plan("crlfUnit", "maxHp", "77").NewBytes["units/crlf.ini"]);
    }

    [Fact]
    public void Every_write_kind_reverts_to_the_exact_original_bytes()
    {
        var crlfNoEol = Encoding.UTF8.GetBytes("[core]\r\nname: e1\r\ncopyFrom: ROOT:/templates/tank_base.ini");
        var lf = Encoding.UTF8.GetBytes("[core]\nname: e2\ncopyFrom: ROOT:/templates/tank_base.ini\n");
        var f = new StatFixture(rawExtra: new Dictionary<string, byte[]> { ["units/e1.ini"] = crlfNoEol, ["units/e2.ini"] = lf });
        foreach (var (unit, key, value, rel, orig) in new[]
        {
            ("e1", "maxHp", "900", "units/e1.ini", crlfNoEol),            // insert
            ("e1", "moveSpeed", "3", "units/e1.ini", crlfNoEol),          // insert section at the end of a file without a final line break
            ("e2", "moveSpeed", "3", "units/e2.ini", lf),                  // insert section
            ("e2", "core.price.rust", "5", "units/e2.ini", lf),            // insert (price with a field)
        })
        {
            var p = f.Plan(unit, key, value);
            var after = IniText.Parse(p.NewBytes[rel], rel);
            var reverted = StatPipeline.RevertOne(after, p.Record);
            Assert.NotNull(reverted);
            Assert.Equal(orig, reverted!.ToBytes());
        }
        // patch kind
        var f2 = new StatFixture();
        var pp = f2.Plan("tankA", "maxHp", "1250", StatEditMode.Template);
        Assert.Equal(f2.Read("units/tankA.ini"), StatPipeline.RevertOne(IniText.Parse(pp.NewBytes["units/tankA.ini"], "units/tankA.ini"), pp.Record)!.ToBytes());
    }

    [Fact]
    public void Revert_finds_nothing_when_the_line_was_changed_by_hand()
    {
        var f = new StatFixture();
        var p = f.Plan("tankB", "maxHp", "1500");
        var handEdited = IniText.Parse(Encoding.UTF8.GetBytes(StatFixture.Decode(p.NewBytes["units/tankB.ini"]).Replace("maxHp: 1500", "maxHp: 1501")), "units/tankB.ini");
        Assert.Null(StatPipeline.RevertOne(handEdited, p.Record));
    }

    [Fact]
    public void Repeated_edits_of_one_stat_fold_into_one_record_and_back_to_default_removes_it()
    {
        var f = new StatFixture();
        var s = new EditorSettings();
        var p1 = f.Plan("tankB", "maxHp", "1500"); StatPipeline.Merge(s, p1.Record); f.Commit(p1);
        var p2 = f.Plan("tankB", "maxHp", "1600"); StatPipeline.Merge(s, p2.Record); f.Commit(p2);
        var only = Assert.Single(s.StatEdits);
        Assert.Equal(StatWriteKind.Insert, only.WriteKind);
        Assert.Equal("1600", only.NewText);
        var p3 = f.Plan("tankA", "maxHp", "1300", StatEditMode.Template); StatPipeline.Merge(s, p3.Record); f.Commit(p3);
        var p4 = f.Plan("tankA", "maxHp", "1200", StatEditMode.Template); StatPipeline.Merge(s, p4.Record);
        Assert.Single(s.StatEdits);                                  // the patch went back to the shipped text: record gone
        Assert.Equal(new[] { 1 }, s.StatEdits.Select(e => e.Seq));
    }

    [Fact]
    public void Settings_with_stat_edits_round_trip_canonically()
    {
        var f = new StatFixture();
        var s = new EditorSettings();
        StatPipeline.Merge(s, f.Plan("tankB", "core.price.credits", "650").Record);
        StatPipeline.Merge(s, f.Plan("tankA", "maxHp", "1300", StatEditMode.Template).Record);
        string json = SettingsJson.Serialize(s);
        var back = SettingsJson.Parse(json);
        Assert.Equal(HashStatus.Match, back.Hash);
        Assert.Equal(json, SettingsJson.Serialize(back.Settings));
        Assert.Equal(2, back.Settings.StatEdits.Count);
        // an E1b-1 settings file (no statEdits) still serialises without the member
        Assert.DoesNotContain("statEdits", SettingsJson.Serialize(new EditorSettings()));
    }

    [Theory]
    [InlineData("\"writeKind\": \"insert\"", "\"writeKind\": \"move\"")]
    [InlineData("\"writeKind\": \"insert\"", "\"writeKind\": \"patch\"")]   // a patch needs oldText
    [InlineData("\"mode\": \"unit\"", "\"mode\": \"all\"")]
    [InlineData("\"oldText\": null", "\"oldText\": \"x\"")]                 // an insert has no oldText
    [InlineData("\"seq\": 1", "\"seq\": 1, \"extra\": 1")]
    [InlineData("\"seq\": 1", "\"seq\": \"1\"")]
    public void Settings_reader_is_strict_about_stat_edits(string from, string to)
    {
        var f = new StatFixture();
        var s = new EditorSettings();
        StatPipeline.Merge(s, f.Plan("tankB", "maxHp", "1500").Record);   // an insert: oldText null, writeKind insert
        string json = SettingsJson.Serialize(s);
        Assert.Contains(from, json);
        Assert.Equal("SETTINGS_INVALID", Assert.Throws<EditorRefusalException>(() => SettingsJson.Parse(json.Replace(from, to))).Code);
    }
}
