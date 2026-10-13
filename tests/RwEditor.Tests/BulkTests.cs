using RwEditor.Core;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>C-033: "Set for many units" (Core StatBulk): filters, upgrade levels / versions, skips with reasons, nothing written.</summary>
public sealed class BulkTests
{
    static StatFixture Fx() => new(new Dictionary<string, string>
    {
        ["bld/baseOne.ini"] = "[core]\nname: baseOne\ndisplayText: Base One\nisBuilding: true\nmaxHp: 800\ngeneration_resources: credits=4\n[movement]\nmoveSpeed: 0\n",
        ["bld/kidOne.ini"] = "[core]\nname: kidOne\ncopyFrom: baseOne.ini\nmaxHp: 1600\n[movement]\nmoveSpeed: 0\n",
        ["bld/solo.ini"] = "[core]\nname: soloBld\ndisplayText: Solo\nisBuilding: true\nmaxHp: 300\n[movement]\nmoveSpeed: 0\n",
        ["bld/formula.ini"] = "[core]\nname: formulaBld\ndisplayText: Formula\nisBuilding: true\ngeneration_resources: credits=${x}\n[movement]\nmoveSpeed: 0\n",
        ["units/walker.ini"] = "[core]\nname: walker\ndisplayText: Walker\nmaxHp: 500\n[movement]\nmoveSpeed: 1\n",
    });

    static BulkPlan Run(StatFixture f, string statId, string value, BulkFilter filter) =>
        StatBulk.Plan(f.Model, StatDictionary.Embedded, UnitFamilies.Build(f.Model), StatDictionary.Embedded.ById(statId)!, value, filter, f.Read);

    const string Credits = "core.generation_resources.credits";

    [Fact]
    public void Buildings_filter_takes_only_buildings_and_units_filter_only_units()
    {
        var f = Fx();
        var b = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings));
        Assert.NotEmpty(b.Ok);
        Assert.All(b.Ok, p => Assert.True(f.Model.IsBuilding(f.Model.UnitsNamed(p.Unit).Single())));
        Assert.Contains(b.Ok, p => p.Unit == "soloBld");
        var u = Run(f, Credits, "9", new BulkFilter(BulkWho.Units));
        Assert.All(u.Ok, p => Assert.False(f.Model.IsBuilding(f.Model.UnitsNamed(p.Unit).Single())));
        Assert.Contains(u.Ok, p => p.Unit == "walker");
    }

    [Fact]
    public void Have_filter_picks_those_with_or_without_the_value()
    {
        var f = Fx();
        var without = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings, null, BulkHave.OnlyThoseWithout));
        Assert.Contains(without.Ok, p => p.Unit == "soloBld");
        Assert.DoesNotContain(without.Ok, p => p.Unit == "baseOne");
        Assert.Contains(without.Skipped, s => s.Unit.Name == "baseOne" && s.Reason.Contains("already has a value", StringComparison.Ordinal));
        var with = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings, null, BulkHave.OnlyThoseThatHaveIt, IncludeCopies: true));
        Assert.Contains(with.Ok, p => p.Unit == "baseOne");
        Assert.DoesNotContain(with.Ok, p => p.Unit == "soloBld");
    }

    [Fact]
    public void A_unit_whose_file_others_copy_is_skipped_unless_copies_are_included()
    {
        var f = Fx();
        var off = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings));
        Assert.DoesNotContain(off.Ok, p => p.Unit == "baseOne");
        Assert.Contains(off.Skipped, s => s.Unit.Name == "baseOne" && s.Reason.Contains("also used by other units", StringComparison.Ordinal));
        var on = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings, null, BulkHave.Any, IncludeCopies: true));
        Assert.Contains(on.Ok, p => p.Unit == "baseOne" && p.Mode == "template");
        Assert.Contains("kidOne", on.AffectedUnits);          // the upgrade level / version inherits it
        Assert.True(on.AffectedUnits.Count > on.Ok.Count);
        Assert.True(on.CanStage);
    }

    [Fact]
    public void Formula_lines_locked_units_and_same_values_are_skipped_with_reasons()
    {
        var f = Fx();
        var p = Run(f, Credits, "4", new BulkFilter(BulkWho.Buildings, null, BulkHave.Any, IncludeCopies: true));
        Assert.Contains(p.Skipped, s => s.Unit.Name == "formulaBld" && s.Reason.Contains("calculated", StringComparison.Ordinal));
        Assert.Contains(p.Skipped, s => s.Unit.Name == "baseOne" && s.Reason.Contains("already has this value", StringComparison.Ordinal));
        Assert.NotEmpty(p.SkipSummary);
    }

    [Fact]
    public void The_changes_really_apply_and_every_unit_reads_the_new_value()
    {
        var f = Fx();
        var plan = Run(f, Credits, "9", new BulkFilter(BulkWho.Buildings, null, BulkHave.Any, IncludeCopies: true));
        var batch = StatBatch.Plan(f.Model, StatDictionary.Embedded, plan.Ok, f.Read);
        Assert.True(batch.CanApply, string.Join("; ", batch.Problems));
        var d = StatDictionary.Embedded.ById(Credits)!;
        foreach (var name in new[] { "baseOne", "kidOne", "soloBld" })
        {
            var u = batch.FinalModel.UnitsNamed(name).Single();
            Assert.Equal(9, StatReader.Read(batch.FinalModel.Merge(u), d, "core").Number);
        }
    }

    [Fact]
    public void Planning_writes_nothing_and_numbered_sections_are_refused()
    {
        var f = Fx();
        var before = f.Bytes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        Run(f, Credits, "9", new BulkFilter(BulkWho.UnitsAndBuildings, null, BulkHave.Any, true));
        foreach (var (k, v) in before) Assert.Equal(v, f.Bytes[k]);
        var e = Assert.Throws<EditorRefusalException>(() => Run(f, "turret.delay", "5", new BulkFilter(BulkWho.Units)));
        Assert.Equal("BULK_SECTION", e.Code);
        var bad = Assert.Throws<EditorRefusalException>(() => Run(f, Credits, "abc", new BulkFilter(BulkWho.Units)));
        Assert.Equal("VALUE_INVALID", bad.Code);
    }
}
