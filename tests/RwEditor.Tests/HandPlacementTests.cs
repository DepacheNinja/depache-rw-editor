using RwEditor.Core;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>PH-099 / PH-100: placing the units of a group by hand (explicit offsets) next to the automatic layout.</summary>
public class HandPlacementTests(SharedMod shared) : IClassFixture<SharedMod>
{
    readonly ModContext _ctx = shared.Ctx;

    static SpawnGroup Auto(int slot, string name, string unit, int count) =>
        new() { Slot = slot, Name = name, Units = { new GroupUnit { Unit = unit, Count = count } } };

    /// <summary>Slot 1 as in the sample (explicit offsets), slot 2 automatic: three big bases do not fit its half of the circle.</summary>
    static EditorSettings TwoGroupsWithAnAutoGroupThatDoesNotFit()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[1] = Auto(2, "Bases", "bigBase", 3);
        return s;
    }

    static double Dist(Offset a, Offset b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    [Fact]
    public void A_group_the_automatic_layout_refuses_can_be_placed_by_hand_and_then_passes_every_rule()
    {
        var s = TwoGroupsWithAnAutoGroupThatDoesNotFit();
        Assert.Contains(SettingsValidator.Validate(s, _ctx), f => f.Code == "LAYOUT_NO_FIT");
        var g = s.SpawnGroups[1];
        Assert.Null(SpawnGroupGenerator.PlannedOffsets(g, s, _ctx, out _));

        HandPlacement.MakeManual(g, s, _ctx);

        Assert.True(HandPlacement.IsManual(g));
        var findings = SettingsValidator.Validate(s, _ctx);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
        Assert.DoesNotContain(findings, f => f.Code == "OFFSETS_OVERLAP");
        var offs = g.Units[0].Offsets!;
        Assert.Equal(3, offs.Count);
        Assert.All(offs, o => Assert.True(Math.Abs(o.X) <= Layout.MaxAbsOffset && Math.Abs(o.Y) <= Layout.MaxAbsOffset));
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++) Assert.True(Dist(offs[i], offs[j]) >= 400, "big bases (radius 200) must not overlap");
        // the generator accepts it: the generated file carries the offsets
        Assert.Contains("offsetX=", SpawnGroupGenerator.Generate(s, _ctx).AnchorGroups, StringComparison.Ordinal);
    }

    [Fact]
    public void Placing_by_hand_keeps_every_unit_where_the_automatic_layout_put_it()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[1] = Auto(2, "Smalls", "smallBot", 5);
        var g = s.SpawnGroups[1];
        var planned = SpawnGroupGenerator.PlannedOffsets(g, s, _ctx, out var why);
        Assert.True(planned != null, why);

        HandPlacement.MakeManual(g, s, _ctx);

        Assert.Equal(planned!, g.Units[0].Offsets!);
        HandPlacement.BackToAuto(g);
        Assert.False(HandPlacement.IsManual(g));
        Assert.Equal(planned!, SpawnGroupGenerator.PlannedOffsets(g, s, _ctx, out _)!);
    }

    [Fact]
    public void More_copies_get_free_spots_fewer_copies_cut_from_the_end_and_the_rest_stay_put()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[1] = Auto(2, "Smalls", "smallBot", 3);
        var g = s.SpawnGroups[1];
        HandPlacement.MakeManual(g, s, _ctx);
        g.Units[0].Offsets![0] = new Offset(-500, 500);   // the user dragged the first one
        var kept = g.Units[0].Offsets!.ToList();

        g.Units[0].Count = 6;
        HandPlacement.Sync(g, s, _ctx);
        var more = g.Units[0].Offsets!;
        Assert.Equal(6, more.Count);
        Assert.Equal(kept, more.Take(3).ToList());
        for (int i = 0; i < 6; i++)
            for (int j = i + 1; j < 6; j++) Assert.True(Dist(more[i], more[j]) >= 20, "new copies must not land on another one");
        Assert.DoesNotContain(SettingsValidator.Validate(s, _ctx), f => f.Severity == Severity.Error || f.Code == "OFFSETS_OVERLAP");

        g.Units[0].Count = 2;
        HandPlacement.Sync(g, s, _ctx);
        Assert.Equal(kept.Take(2).ToList(), g.Units[0].Offsets!);
    }

    [Fact]
    public void A_new_unit_added_to_a_group_placed_by_hand_gets_spots_and_the_group_stays_valid()
    {
        var s = TestMod.Sample2();
        var g = s.SpawnGroups[0];   // explicit offsets
        g.Units.Add(new GroupUnit { Unit = "smallBot", Count = 2 });
        Assert.Contains(SettingsValidator.Validate(s, _ctx), f => f.Code == "OFFSETS_PARTIAL");
        HandPlacement.Sync(g, s, _ctx);
        Assert.Equal(2, g.Units[2].Offsets!.Count);
        Assert.DoesNotContain(SettingsValidator.Validate(s, _ctx), f => f.Severity == Severity.Error);
    }

    [Fact]
    public void Units_dragged_onto_each_other_give_a_warning_not_an_error()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[0].Units[0].Offsets![1] = s.SpawnGroups[0].Units[0].Offsets![0];   // two hover tanks on the same spot
        var f = SettingsValidator.Validate(s, _ctx);
        Assert.DoesNotContain(f, x => x.Severity == Severity.Error);
        var w = Assert.Single(f, x => x.Code == "OFFSETS_OVERLAP");
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("Hover strike", w.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_has_no_overlap_warning()
    {
        Assert.DoesNotContain(SettingsValidator.Validate(TestMod.Sample2(), _ctx), f => f.Code == "OFFSETS_OVERLAP");
    }

    [Fact]
    public void A_group_next_to_an_automatic_group_is_warned_when_it_is_dragged_onto_it()
    {
        var s = TestMod.Sample2();
        s.SpawnGroups[1] = Auto(2, "Smalls", "smallBot", 2);
        var auto = SpawnGroupGenerator.PlannedOffsets(s.SpawnGroups[1], s, _ctx, out _)!;
        s.SpawnGroups[0].Units[0].Offsets![0] = auto[0];   // a hand-placed tank on an automatic unit
        Assert.Contains(SettingsValidator.Validate(s, _ctx), f => f.Code == "OFFSETS_OVERLAP");
    }

    [Fact]
    public void Free_spot_is_deterministic_and_clear_of_what_is_taken()
    {
        var taken = new List<(Offset, double)>();
        var first = Layout.FreeSpot(0, 2, 20, taken);
        taken.Add((first, 20));
        var second = Layout.FreeSpot(0, 2, 20, taken);
        Assert.NotEqual(first, second);
        Assert.True(Dist(first, second) >= 40);
        Assert.Equal(second, Layout.FreeSpot(0, 2, 20, taken));
    }

    [Fact]
    public void Relaxed_layout_always_places_twelve_big_units_inside_the_bound()
    {
        var copies = Enumerable.Range(0, 12).Select(_ => ("bigBase", (double?)200)).ToList();
        var offs = Layout.Relaxed(1, 8, copies);
        Assert.Equal(12, offs.Count);
        Assert.All(offs, o => Assert.True(Math.Abs(o.X) <= Layout.MaxAbsOffset && Math.Abs(o.Y) <= Layout.MaxAbsOffset));
    }
}
