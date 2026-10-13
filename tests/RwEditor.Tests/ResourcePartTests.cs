using RwEditor.Core;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>C-033: credits / rust produced (generation_resources) and tech level provided (borrowResourcesWhileBuilt, stored negative).</summary>
public sealed class ResourcePartTests
{
    static StatFixture Fx() => new(new Dictionary<string, string>
    {
        ["units/hq.ini"] = "[core]\nname: hq\nisBuilding: true\nmaxHp: 900\ngeneration_resources: credits=60, rust=15\n[movement]\nmoveSpeed: 0\n",
        ["units/plain.ini"] = "[core]\nname: plainTank\nmaxHp: 400\n",
        ["units/rustonly.ini"] = "[core]\nname: rustOnly\ngeneration_resources: energy = -0.003\n",
        ["units/formula.ini"] = "[core]\nname: formulaUnit\ngeneration_resources: credits=${creditsProvided1}\n",
        ["units/techc.ini"] = "[core]\nname: techC\nisBuilding: true\nmaxHp: 500\nborrowResourcesWhileBuilt: techLevel = -5\n[movement]\nmoveSpeed: 0\n",
        ["units/techmix.ini"] = "[core]\nname: techMix\nisBuilding: true\nborrowResourcesWhileBuilt: moduleSlot = -1, techLevel = -300\n[movement]\nmoveSpeed: 0\n",
        ["units/techexpr.ini"] = "[core]\nname: techExpr\nisBuilding: true\nborrowResourcesWhileBuilt: techLevel = ${-1*techT1ProvideTechLevel}\n[movement]\nmoveSpeed: 0\n",
    });

    static string Text(StatFixture f, StatEditPlan p, string file) => StatFixture.Decode(p.NewBytes[file]);

    [Fact]
    public void Credits_produced_changes_only_its_part_and_keeps_the_rust_part()
    {
        var f = Fx();
        var p = f.Plan("hq", "core.generation_resources.credits", "90");
        Assert.Equal(60, p.Before.Number);
        Assert.Equal("[core]\nname: hq\nisBuilding: true\nmaxHp: 900\ngeneration_resources: credits=90, rust=15\n[movement]\nmoveSpeed: 0\n", Text(f, p, "units/hq.ini"));
        f.Commit(p);
        Assert.Equal(90, StatReader.Read(f.Model.Merge(f.Model.Find("hq")!), StatDictionary.Embedded.ById("core.generation_resources.credits")!, "core").Number);
        Assert.Equal(15, StatReader.Read(f.Model.Merge(f.Model.Find("hq")!), StatDictionary.Embedded.ById("core.generation_resources.rust")!, "core").Number);
    }

    [Fact]
    public void Rust_produced_accepts_decimals_credits_must_be_whole()
    {
        var f = Fx();
        var p = f.Plan("hq", "core.generation_resources.rust", "22.5");
        Assert.Contains("rust=22.5", Text(f, p, "units/hq.ini"));
        var e = Assert.Throws<StatEditRefusedException>(() => f.Plan("hq", "core.generation_resources.credits", "60.5"));
        Assert.Contains(e.Findings, x => x.Code == "VALUE_INVALID");
    }

    [Fact]
    public void A_unit_with_no_income_line_gets_one_added_to_its_own_core()
    {
        var f = Fx();
        var p = f.Plan("plainTank", "core.generation_resources.credits", "5");
        Assert.False(p.Before.IsSet);
        Assert.Equal("[core]\ngeneration_resources: credits=5\nname: plainTank\nmaxHp: 400\n", Text(f, p, "units/plain.ini"));
        f.Commit(p);
        Assert.Equal(5, StatReader.Read(f.Model.Merge(f.Model.Find("plainTank")!), StatDictionary.Embedded.ById("core.generation_resources.credits")!, "core").Number);
    }

    [Fact]
    public void A_line_with_other_parts_gets_the_new_part_appended_without_losing_them()
    {
        var f = Fx();
        var p = f.Plan("rustOnly", "core.generation_resources.credits", "3");
        Assert.Equal("[core]\nname: rustOnly\ngeneration_resources: energy = -0.003, credits = 3\n", Text(f, p, "units/rustonly.ini"));
    }

    [Fact]
    public void A_formula_line_stays_read_only()
    {
        var f = Fx();
        var e = Assert.Throws<StatEditRefusedException>(() => f.Plan("formulaUnit", "core.generation_resources.credits", "4"));
        Assert.Contains(e.Findings, x => x.Code == "VALUE_READ_ONLY");
    }

    [Fact]
    public void Tech_provided_is_shown_positive_and_written_negative()
    {
        var f = Fx();
        var d = StatDictionary.Embedded.ById("core.borrowResourcesWhileBuilt.techLevel")!;
        Assert.True(d.Negate);
        Assert.Equal(5, StatReader.Read(f.Model.Merge(f.Model.Find("techC")!), d, "core").Number);   // "-5" in the file = provides 5
        var p = f.Plan("techC", "core.borrowResourcesWhileBuilt.techLevel", "12");
        Assert.Equal(5, p.Before.Number);
        Assert.Equal(12, p.NewNumber);
        Assert.Contains("borrowResourcesWhileBuilt: techLevel = -12", Text(f, p, "units/techc.ini"));
        f.Commit(p);
        Assert.Equal(12, StatReader.Read(f.Model.Merge(f.Model.Find("techC")!), d, "core").Number);
    }

    [Fact]
    public void Tech_provided_keeps_the_other_resource_parts_and_zero_is_written_as_zero()
    {
        var f = Fx();
        var p = f.Plan("techMix", "core.borrowResourcesWhileBuilt.techLevel", "70");
        Assert.Equal(300, p.Before.Number);
        Assert.Contains("moduleSlot = -1, techLevel = -70", Text(f, p, "units/techmix.ini"));
        var z = f.Plan("techMix", "core.borrowResourcesWhileBuilt.techLevel", "0");
        Assert.Contains("techLevel = 0", Text(f, z, "units/techmix.ini"));
        Assert.DoesNotContain("-0", Text(f, z, "units/techmix.ini").Replace("-0.", "x"));
    }

    [Fact]
    public void Tech_provided_cannot_go_negative_and_a_formula_stays_read_only()
    {
        var f = Fx();
        var e = Assert.Throws<StatEditRefusedException>(() => f.Plan("techC", "core.borrowResourcesWhileBuilt.techLevel", "-3"));
        Assert.Contains(e.Findings, x => x.Code == "OUT_OF_RANGE");
        var r = Assert.Throws<StatEditRefusedException>(() => f.Plan("techExpr", "core.borrowResourcesWhileBuilt.techLevel", "3"));
        Assert.Contains(r.Findings, x => x.Code == "VALUE_READ_ONLY");
    }

    [Fact]
    public void The_unit_class_techLevel_key_is_still_never_editable()
    {
        var f = Fx();
        var e = Assert.Throws<StatEditRefusedException>(() => f.Plan("techC", "techLevel", "2"));
        Assert.Contains(e.Findings, x => x.Code is "NOT_IN_DICTIONARY" or "KEY_LOCKED");
    }

    [Fact]
    public void Quick_check_while_typing_uses_the_displayed_sign()
    {
        var d = StatDictionary.Embedded.ById("core.borrowResourcesWhileBuilt.techLevel")!;
        Assert.Null(StatText.QuickCheck(d, "-5", "8", out var v));
        Assert.Equal("8", v);
        Assert.NotNull(StatText.QuickCheck(d, "-5", "-8", out _));
    }
}
