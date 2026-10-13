using RwEditor.Core;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Every validation rule with a case that fails it, plus the sample that passes all of them.</summary>
public class ValidationTests(SharedMod shared) : IClassFixture<SharedMod>
{
    readonly ModContext _ctx = shared.Ctx;

    IReadOnlyList<Finding> V(Action<EditorSettings> mutate)
    {
        var s = TestMod.Sample2();
        mutate(s);
        return SettingsValidator.Validate(s, _ctx);
    }

    static SpawnGroup G(int slot, string name, string unit = "smallBot", int count = 1) =>
        new() { Slot = slot, Name = name, Units = { new GroupUnit { Unit = unit, Count = count } } };

    void AssertError(string code, Action<EditorSettings> mutate)
    {
        var f = V(mutate);
        Assert.Contains(f, x => x.Code == code && x.Severity == Severity.Error);
    }

    [Fact]
    public void Sample_is_valid() => Assert.DoesNotContain(V(_ => { }), f => f.Severity == Severity.Error);

    [Fact] public void Schema() => AssertError("SETTINGS_SCHEMA", s => s.Schema = 2);

    [Fact]
    public void More_than_8_groups() => AssertError("GROUP_COUNT", s =>
    {
        s.SpawnGroups.Clear();
        for (int k = 1; k <= 9; k++) s.SpawnGroups.Add(G(k, "G" + k));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void Slot_out_of_range(int slot) => AssertError("GROUP_SLOT", s => s.SpawnGroups[1].Slot = slot);

    [Fact] public void Slot_used_twice() => AssertError("GROUP_SLOT_DUP", s => s.SpawnGroups[1].Slot = 1);

    [Theory]
    [InlineData("Bad: name")]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("two  spaces")]
    [InlineData("dollar$")]
    [InlineData("12345678901234567890123456789012345678901")]
    [InlineData("quote\"")]
    public void Group_name_invalid(string name) => AssertError("GROUP_NAME_INVALID", s => s.SpawnGroups[0].Name = name);

    [Fact] public void Group_names_unique_ignoring_case() => AssertError("GROUP_NAME_DUP", s => s.SpawnGroups[1].Name = "HOVER STRIKE");

    [Fact] public void Group_name_equal_to_an_existing_option() => AssertError("GROUP_NAME_COLLIDES_OPTION", s => s.SpawnGroups[1].Name = "extra acv");

    [Fact] public void Group_without_units() => AssertError("GROUP_EMPTY", s => s.SpawnGroups[1].Units.Clear());

    [Fact] public void Unit_count_zero() => AssertError("UNIT_COUNT", s => { s.SpawnGroups[1].Units[0].Count = 0; s.SpawnGroups[1].Units[0].Offsets = new(); });

    [Fact]
    public void More_than_12_units_in_a_group() => AssertError("GROUP_TOO_MANY_UNITS", s =>
        s.SpawnGroups[1] = G(2, "Builders", "smallBot", 13));

    [Fact]
    public void Same_unit_twice_in_a_group() => AssertError("UNIT_DUP_ENTRY", s =>
        s.SpawnGroups[1] = new SpawnGroup { Slot = 2, Name = "B", Units = { new GroupUnit { Unit = "smallBot" }, new GroupUnit { Unit = "smallBot" } } });

    [Theory]
    [InlineData("bad name", "UNIT_NAME_SYNTAX")]
    [InlineData("x(1)", "UNIT_NAME_SYNTAX")]
    [InlineData("noSuchUnit", "UNIT_UNKNOWN")]
    [InlineData("SMALLBOT", "UNIT_CASE")]
    [InlineData("airTemplate", "UNIT_NOT_LOADABLE")]
    [InlineData("managerUnit", "UNIT_LOCKED")]
    public void Unit_must_be_a_real_loadable_unlocked_unit(string unit, string code) => AssertError(code, s =>
        s.SpawnGroups[1] = G(2, "Builders", unit));

    [Fact]
    public void Offsets_all_or_none() => AssertError("OFFSETS_PARTIAL", s => s.SpawnGroups[0].Units[1].Offsets = null);

    [Fact]
    public void Offsets_count_equals_unit_count() => AssertError("OFFSETS_COUNT", s => s.SpawnGroups[0].Units[0].Offsets!.RemoveAt(1));

    [Fact]
    public void Offsets_in_range() => AssertError("OFFSET_RANGE", s => s.SpawnGroups[0].Units[1].Offsets![0] = new Offset(1001, 0));

    [Fact]
    public void Auto_layout_must_fit() => AssertError("LAYOUT_NO_FIT", s => s.SpawnGroups[1] = G(2, "Big", "bigBase", 3));

    [Fact]
    public void Auto_layout_needs_a_numeric_radius() => AssertError("LAYOUT_NO_FIT", s => s.SpawnGroups[1] = G(2, "R", "noRadius", 1));

    [Fact]
    public void At_most_40_units_per_team() => AssertError("TEAM_TOO_MANY_UNITS", s =>
    {
        s.SpawnGroups.Clear();
        foreach (var t in new[] { "extraACV", "extraSCB", "extraSpdr", "extraDrop" }) s.SetOptionState(t, OptionState.Hidden);
        for (int k = 1; k <= 4; k++) s.SpawnGroups.Add(new SpawnGroup
        {
            Slot = k, Name = "G" + k,
            Units = { new GroupUnit { Unit = "smallBot", Count = 11, Offsets = Enumerable.Range(0, 11).Select(i => new Offset(300, i * 30)).ToList() } },
        });
    });

    [Fact]
    public void Disabled_groups_do_not_count_toward_the_team_limit_or_the_button_cap()
    {
        var f = V(s =>
        {
            s.SpawnGroups.Clear();
            s.StartOptions.Clear();
            for (int k = 1; k <= 8; k++) s.SpawnGroups.Add(new SpawnGroup { Slot = k, Name = "G" + k, Enabled = k <= 4, Units = { new GroupUnit { Unit = "smallBot", Count = 10 } } });
        });
        Assert.DoesNotContain(f, x => x.Severity == Severity.Error);
    }

    [Fact]
    public void Section_4_shows_at_most_9_buttons() => AssertError("SECTION4_CAP", s =>
    {
        s.StartOptions.Clear();   // 5 defaults shown (4 extras + extraHuman)
        s.SpawnGroups.Clear();
        for (int k = 1; k <= 5; k++) s.SpawnGroups.Add(G(k, "G" + k));
    });

    [Fact] public void Unknown_option() => AssertError("OPTION_UNKNOWN", s => s.SetOptionState("noSuchOption", OptionState.Hidden));

    [Theory]
    [InlineData("fairAI")]
    [InlineData("extraHuman")]
    public void Option_not_hideable(string tag) => AssertError("OPTION_NOT_HIDEABLE", s => s.SetOptionState(tag, OptionState.Deleted));

    [Fact]
    public void Base_mod_mismatch_is_a_warning_only()
    {
        var f = V(s => s.BaseMod = new BaseModRef("0.0.1", 1, 3, "deadbeef"));
        Assert.Contains(f, x => x.Code == "BASEMOD_MISMATCH" && x.Severity == Severity.Warning);
        Assert.DoesNotContain(f, x => x.Severity == Severity.Error);
    }

    [Fact]
    public void Matching_base_mod_gives_no_warning()
    {
        var f = V(s => s.BaseMod = new BaseModRef(_ctx.Support.ModVersion, 1, _ctx.Support.LockedFileCount, _ctx.Support.LockedManifestSha256));
        Assert.DoesNotContain(f, x => x.Code == "BASEMOD_MISMATCH");
    }

    [Fact]
    public void Default_on_while_disabled_is_a_warning() =>
        Assert.Contains(V(s => s.SpawnGroups[0].Enabled = false), x => x.Code == "DEFAULT_ON_DISABLED" && x.Severity == Severity.Warning);

    [Fact]
    public void Editor_tag_already_used_in_the_mod()
    {
        using var mod = new TestMod(extraFiles: new Dictionary<string, string> { ["units/clash.ini"] = "[core]\nname: clash\n[hiddenAction_x]\naddGlobalTeamTags: ug03Done\n" });
        var f = SettingsValidator.Validate(TestMod.Sample2(), mod.Context());
        Assert.Contains(f, x => x.Code == "TAG_COLLISION" && x.Message.Contains("units/clash.ini: ug03Done"));
    }

    [Fact]
    public void Editor_tag_in_a_comment_is_not_a_collision()
    {
        using var mod = new TestMod(extraFiles: new Dictionary<string, string> { ["units/ok.ini"] = "[core]\nname: okUnit\n# ug01 xGrpWait in a comment\n" });
        Assert.DoesNotContain(SettingsValidator.Validate(TestMod.Sample2(), mod.Context()), x => x.Code == "TAG_COLLISION");
    }
}
