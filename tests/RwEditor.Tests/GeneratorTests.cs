using RwEditor.Core;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>One read-only synthetic mod shared by the tests of a class.</summary>
public sealed class SharedMod : IDisposable
{
    public TestMod Mod { get; } = new();
    public ModContext Ctx { get; }
    public SharedMod() { Ctx = Mod.Context(); }
    public void Dispose() => Mod.Dispose();
}

public class GeneratorTests(SharedMod shared) : IClassFixture<SharedMod>
{
    readonly ModContext _ctx = shared.Ctx;

    [Fact]
    public void Sample2_matches_the_golden_files_byte_for_byte()
    {
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), _ctx);
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), gen.AnchorGroups);
        Assert.Equal(TestMod.Golden("selectorGroups.sample2.ini"), gen.SelectorGroups);
    }

    [Fact]
    public void Output_is_LF_only_ascii_and_ends_with_one_newline()
    {
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), _ctx);
        foreach (var t in new[] { gen.AnchorGroups, gen.SelectorGroups })
        {
            Assert.DoesNotContain('\r', t);
            Assert.EndsWith("\n", t);
            Assert.False(t.EndsWith("\n\n", StringComparison.Ordinal));
            Assert.All(t, c => Assert.True(c < 128));
        }
    }

    [Fact]
    public void Generation_is_deterministic_and_independent_of_group_order_in_the_settings()
    {
        var a = SpawnGroupGenerator.Generate(TestMod.Sample2(), _ctx);
        var s = TestMod.Sample2();
        s.SpawnGroups.Reverse();
        var b = SpawnGroupGenerator.Generate(s, _ctx);
        var c = SpawnGroupGenerator.Generate(TestMod.Sample2(), _ctx);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Default_settings_give_the_empty_includes_and_pass_the_rules()
    {
        var gen = SpawnGroupGenerator.Generate(EditorSettings.Default(), _ctx);
        Assert.Equal("[core]\ndont_load: true\n" + SpawnGroupGenerator.AnchorHeader + "\n", gen.AnchorGroups);
        Assert.Equal("[core]\ndont_load: true\n" + SpawnGroupGenerator.SelectorHeader + "\n", gen.SelectorGroups);
        var rules = IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath));
        Assert.True(rules.AllPass, string.Join("\n", rules.Failures));
    }

    [Fact]
    public void Hidden_only_without_groups_writes_ugHide_and_no_anchor_sections()
    {
        var s = new EditorSettings();
        s.SetOptionState("extraSCB", OptionState.Deleted);
        s.SetOptionState("extraACV", OptionState.Hidden);
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        // selector file order: extraACV before extraSCB; Deleted and Hidden both hide in game
        Assert.Contains("[hiddenAction_ugHide]\nid:ugHide\nautoTriggerOnEvent: created\ntemporarilyAddTags: xhextraACV, xhextraSCB\n", gen.SelectorGroups);
        Assert.DoesNotContain("ugDef", gen.SelectorGroups);
        Assert.DoesNotContain("hiddenAction", gen.AnchorGroups);
        Assert.True(IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath)).AllPass);
    }

    [Fact]
    public void Disabled_group_is_not_generated()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[0].Enabled = false;
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        Assert.DoesNotContain("ug01", gen.AnchorGroups);
        Assert.DoesNotContain("ug01", gen.SelectorGroups);
        Assert.Contains("temporarilyAddTags: xGrpWait, ugW02\n", gen.AnchorGroups);
        Assert.True(IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath)).AllPass);
    }

    /// <summary>Largest legal configuration: 8 groups (all four hideable defaults hidden -> 1 + 8 = 9 buttons), 40 units, both modes.</summary>
    static EditorSettings MaxConfig()
    {
        var s = new EditorSettings();
        foreach (var t in new[] { "extraACV", "extraSCB", "extraSpdr", "extraDrop" }) s.SetOptionState(t, OptionState.Hidden);
        for (int k = 1; k <= 8; k++)
            s.SpawnGroups.Add(new SpawnGroup
            {
                Slot = k, Name = $"Group number {k}", Mode = k % 2 == 0 ? GroupMode.HumansOnly : GroupMode.FollowToggle, DefaultOn = k <= 3,
                Units = { new GroupUnit { Unit = "smallBot", Count = 5 } },
            });
        return s;
    }

    [Fact]
    public void Max_configuration_keeps_every_generator_invariant()
    {
        var s = MaxConfig();
        Assert.DoesNotContain(SettingsValidator.Validate(s, _ctx), f => f.Severity == Severity.Error);
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        foreach (var text in new[] { gen.AnchorGroups, gen.SelectorGroups })
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("autoTrigger:", StringComparison.Ordinal)) Assert.True(line.Length <= 705, $"{line.Length}: {line}");
                if (line.StartsWith("id:", StringComparison.Ordinal)) Assert.True(line.Length - 3 <= 13, line);
                if (line.StartsWith("@define actionTag:", StringComparison.Ordinal)) Assert.True(line.Substring(18).Trim().Length <= 10, line);
            }
        var planned = _ctx.Snapshot.WithFiles(gen.ByRelPath);
        var rules = IncludeRules.Check(planned);
        Assert.True(rules.AllPass, string.Join("\n", rules.Failures));
        Assert.Empty(RwLint.NewFindings(_ctx.Snapshot.WithFiles(SpawnGroupGenerator.GenerateDefaults(_ctx).ByRelPath), planned));
        // pure AND: no ' or ' in any spawn trigger
        foreach (var line in gen.AnchorGroups.Split('\n').Where(l => l.Contains("withTag='ug") && l.Contains("timeAlive")))
            Assert.DoesNotContain(" or ", line);
    }

    [Fact]
    public void Auto_layout_is_deterministic_and_inside_the_rings_and_the_sector()
    {
        // three generated groups (slots 1, 3, 5): slot 5 is the third -> sector centred on 240 degrees, +-60
        var g = new SpawnGroup { Slot = 5, Name = "Auto", Units = { new GroupUnit { Unit = "smallBot", Count = 10 }, new GroupUnit { Unit = "hoverBuilder", Count = 2 } } };
        var s = new EditorSettings { SpawnGroups = { new SpawnGroup { Slot = 1, Name = "A", Units = { new GroupUnit { Unit = "smallBot" } } }, new SpawnGroup { Slot = 3, Name = "B", Units = { new GroupUnit { Unit = "smallBot" } } }, g } };
        var a = SpawnGroupGenerator.PlannedOffsets(g, s, _ctx, out var r1);
        var b = SpawnGroupGenerator.PlannedOffsets(g, s, _ctx, out _);
        Assert.Null(r1);
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.Equal(12, a!.Count);
        foreach (var o in a)
        {
            double dist = Math.Sqrt(o.X * (double)o.X + o.Y * (double)o.Y);
            Assert.InRange(dist, Layout.InnerRing - 1, Layout.OuterRing + 1);
            double ang = Math.Atan2(o.Y, o.X) * 180 / Math.PI;
            double diff = ((ang - 240) % 360 + 540) % 360 - 180;
            Assert.InRange(diff, -60.5, 60.5);
        }
        // no two units closer than the sum of their radii (10 or 15) plus a margin
        for (int i = 0; i < a.Count; i++)
            for (int j = i + 1; j < a.Count; j++)
                Assert.True(Math.Sqrt(Math.Pow(a[i].X - a[j].X, 2) + Math.Pow(a[i].Y - a[j].Y, 2)) >= 30, $"{a[i]} vs {a[j]}");
        Assert.DoesNotContain(SettingsValidator.Validate(s, _ctx), f => f.Severity == Severity.Error);
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        Assert.True(IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath)).AllPass);
    }

    [Fact]
    public void Buildings_snap_to_the_grid_and_skip_when_overlapping()
    {
        var s = new EditorSettings { SpawnGroups = { new SpawnGroup { Slot = 1, Name = "Walls", Units = { new GroupUnit { Unit = "wallBlock", Count = 2 } } } } };
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        Assert.Contains("wallBlock(skipIfOverlapping=true, gridAlign=true, offsetX=", gen.AnchorGroups);
        Assert.DoesNotContain("wallBlock(skipIfOverlapping=false", gen.AnchorGroups);
    }

    [Fact]
    public void Description_uses_sanitised_display_names()
    {
        var s = new EditorSettings { SpawnGroups = { new SpawnGroup { Slot = 1, Name = "Walls", Units = { new GroupUnit { Unit = "wallBlock", Count = 2 } } } } };
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        Assert.Contains("description:- Every player starts with this group\\n- 2x Wall Mk II\n", gen.SelectorGroups);
    }

    [Fact]
    public void Generate_refuses_invalid_settings()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[0].Units[0].Unit = "noSuchUnit";
        var e = Assert.Throws<SettingsInvalidException>(() => SpawnGroupGenerator.Generate(s, _ctx));
        Assert.Contains(e.Findings, f => f.Code == "UNIT_UNKNOWN");
    }

    [Theory]
    [InlineData("Hover strike", "Hover strike")]
    [InlineData("  Tanks:  Mk  II  ", "Tanks Mk II")]
    [InlineData("a#b$c%d{e}[f]\\g\"h\nI", "abcdefgh I")]
    [InlineData("::::", "")]
    [InlineData("1234567890123456789012345678901234567890XYZ", "1234567890123456789012345678901234567890")]
    public void Name_sanitiser(string raw, string want)
    {
        Assert.Equal(want, NameRules.Sanitize(raw));
        if (want.Length > 0) Assert.True(NameRules.IsValidName(want));
    }
}
