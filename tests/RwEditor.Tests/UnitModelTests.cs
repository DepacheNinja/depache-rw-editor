using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Unit enumeration, mod areas and the resolver with origin tracking, on the synthetic fixture.</summary>
public class UnitModelTests
{
    static readonly StatFixture F = new();

    [Fact]
    public void Loadable_units_follow_the_RwCatalog_rule()
    {
        var names = F.Model.Units.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("tankA", names);
        Assert.DoesNotContain("tankBase", names);          // dont_load
        Assert.DoesNotContain("notLoaded", names);         // dont_load
        Assert.DoesNotContain("notAUnitTemplate", names);  // a .template file is never a unit
        Assert.DoesNotContain(F.Model.Units, u => u.RelPath == "units/noname.ini");   // no [core] name
        Assert.Equal(2, F.Model.UnitsNamed("dupName").Count);
        Assert.Equal(F.Model.Units.OrderBy(u => u.RelPath, StringComparer.OrdinalIgnoreCase).Select(u => u.RelPath), F.Model.Units.Select(u => u.RelPath));
    }

    [Theory]
    [InlineData("feGen", "fe")]
    [InlineData("mooA", "moo")]
    [InlineData("vteB", "vte")]
    [InlineData("acvMainBldg", "depache")]
    [InlineData("tankA", "main")]
    public void Units_get_their_mod_area(string unit, string area) => Assert.Equal(area, F.Model.Find(unit)!.Area);

    [Fact]
    public void Locked_units_are_flagged()
    {
        Assert.True(F.Model.Find("lockedUnit")!.Locked);
        Assert.False(F.Model.Find("tankA")!.Locked);
    }

    [Fact]
    public void Own_value_wins_over_the_template_and_has_its_own_origin()
    {
        var m = F.Model.Merge(F.Model.Find("tankA")!);
        var hp = m.Get("core", "maxHp")!;
        Assert.Equal("1200", hp.Value);
        Assert.Equal(new Origin("units/tankA.ini", "core", 4, OriginKind.Own, null), hp.Origin);
        var price = m.Get("core", "price")!;
        Assert.Equal(new Origin("templates/tank_base.ini", "core", 5, OriginKind.Template, null), price.Origin);
        var shield = m.Get("core", "maxShield")!;
        Assert.Equal(OriginKind.AllUnits, shield.Origin.Kind);
        Assert.Equal(new[] { "units/tankA.ini", "templates/tank_base.ini", "all-units.template" }, m.ChainFiles);
    }

    [Fact]
    public void A_unit_copying_another_unit_file_sees_that_file_as_a_template()
    {
        var m = F.Model.Merge(F.Model.Find("tankC")!);
        Assert.Equal("1200", m.Get("core", "maxHp")!.Value);
        Assert.Equal(OriginKind.Template, m.Get("core", "maxHp")!.Origin.Kind);
        Assert.Equal("units/tankA.ini", m.Get("core", "maxHp")!.Origin.File);
    }

    [Fact]
    public void The_last_listed_copyFrom_parent_wins()
    {
        var m = F.Model.Merge(F.Model.Find("cfsUnit")!);
        Assert.Equal("222", m.Get("core", "maxHp")!.Value);              // parent2 is listed last
        Assert.Equal("units/parent2.ini", m.Get("core", "maxHp")!.Origin.File);
        Assert.Equal("1", m.Get("core", "mass")!.Value);                 // only parent1 sets it
    }

    [Fact]
    public void CopyFromSection_keeps_own_keys_and_the_last_listed_source_wins()
    {
        var m = F.Model.Merge(F.Model.Find("cfsUnit")!);
        var d1 = m.Get("turret_1", "delay")!;
        Assert.Equal("20", d1.Value);                                     // template_b listed last
        Assert.Equal("template_b", d1.Origin.ViaSection);
        Assert.Equal("template_b", d1.Origin.FileSection);
        Assert.Equal("a", m.Get("turret_1", "x")!.Value);                 // keys only in the earlier source still arrive
        Assert.Equal("b", m.Get("turret_1", "y")!.Value);
        var d2 = m.Get("turret_2", "delay")!;
        Assert.Equal("5", d2.Value);                                      // own key wins
        Assert.Null(d2.Origin.ViaSection);
    }

    [Fact]
    public void UnitsUsingFile_is_the_reverse_index_of_the_chains()
    {
        var users = F.Model.UnitsUsingFile("templates/tank_base.ini").Select(u => u.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "tankA", "tankB", "tankC" }, users);
        Assert.Equal(new[] { "tankA", "tankC" }, F.Model.UnitsUsingFile("units/tankA.ini").Select(u => u.Name).OrderBy(x => x));
    }

    [Fact]
    public void Expressions_are_read_only_and_literals_are_parsed_per_type()
    {
        var dict = StatDictionary.Embedded;
        var e = F.Model.Merge(F.Model.Find("exprUnit")!);
        Assert.False(StatReader.Read(e, dict.ById("core.maxHp")!, "core").Editable);
        Assert.False(StatReader.Read(e, dict.ById("core.price.credits")!, "core").Editable);
        Assert.False(StatReader.Read(e, dict.ById("core.buildSpeed")!, "core").Editable);   // plain 0: meaning not traced
        var a = F.Model.Merge(F.Model.Find("tankA")!);
        Assert.Equal(1200, StatReader.Read(a, dict.ById("core.maxHp")!, "core").Number);
        Assert.Equal(500, StatReader.Read(a, dict.ById("core.price.credits")!, "core").Number);
        Assert.Equal(100, StatReader.Read(a, dict.ById("core.price.rust")!, "core").Number);
        Assert.Equal(20, StatReader.Read(a, dict.ById("core.buildSpeed")!, "core").Number);      // 20s
        Assert.Equal(30, StatReader.Read(a, dict.ById("turret.delay")!, "turret_1").Number);       // frames
    }

    [Fact]
    public void Model_with_planned_files_only_changes_what_was_replaced()
    {
        var m2 = F.Model.WithFiles(new Dictionary<string, string> { ["templates/tank_base.ini"] = StatFixture.Files["templates/tank_base.ini"].Replace("maxHp: 1000", "maxHp: 999") });
        Assert.Equal("999", m2.Merge(m2.Find("tankB")!).Get("core", "maxHp")!.Value);
        Assert.Equal("1000", F.Model.Merge(F.Model.Find("tankB")!).Get("core", "maxHp")!.Value);
    }
}

/// <summary>Value parsing: prices, times, build times, numbers.</summary>
public class StatValueTests
{
    [Theory]
    [InlineData("500", "credits", "500")]
    [InlineData("credits = 300000, rust = 150000", "rust", "150000")]
    [InlineData("credits=1|rust:2", "rust", "2")]
    [InlineData("credits = 3, energy=4", "energy", "4")]
    public void Price_fields_are_read_like_the_game(string price, string field, string expected) => Assert.Equal(expected, StatValue.PriceField(price, field));

    [Theory]
    [InlineData("credits = 300000, rust = 150000", "credits", "1", "credits = 1, rust = 150000")]
    [InlineData("credits = 300000, rust = 150000", "rust", "7", "credits = 300000, rust = 7")]
    [InlineData("500", "credits", "600", "600")]
    [InlineData("500", "rust", "9", "500, rust=9")]
    [InlineData("credits = 5", "rust", "9", "credits = 5, rust = 9")]
    [InlineData("", "credits", "5", "credits=5")]
    public void Price_fields_are_changed_in_place(string price, string field, string number, string expected) =>
        Assert.Equal(expected, StatValue.WithPriceField(price, field, number));

    [Theory]
    [InlineData("${x}")]
    [InlineData("credits=1=2")]
    [InlineData("credits=")]
    public void Unusual_prices_are_not_editable(string price) => Assert.Null(StatValue.ParsePrice(price));

    [Theory]
    [InlineData("30", 30, false)]
    [InlineData("2s", 120, true)]
    [InlineData("0.5s", 30, true)]
    public void Times_are_frames_or_seconds(string s, double frames, bool seconds)
    {
        Assert.True(StatValue.TryTime(s, out var f, out var sec));
        Assert.Equal(frames, f, 6);
        Assert.Equal(seconds, sec);
    }

    [Fact]
    public void Build_speed_is_inverted_time()
    {
        Assert.True(StatValue.TryBuildSeconds("20s", out var a, out var sa));
        Assert.Equal(20, a); Assert.True(sa);
        Assert.True(StatValue.TryBuildSeconds("0.0005", out var b, out var sb));
        Assert.Equal(1.0 / 0.03, b, 6); Assert.False(sb);
        Assert.True(StatValue.TryBuildSeconds("0", out var c, out _));
        Assert.True(double.IsPositiveInfinity(c));
    }

    [Theory]
    [InlineData("12", true)]
    [InlineData("-3", true)]
    [InlineData("1.5", false)]
    [InlineData("1e3", false)]
    [InlineData("", false)]
    public void Integers_are_whole_numbers_only(string s, bool ok) => Assert.Equal(ok, StatValue.TryInt(s, out _));

    [Fact]
    public void Format_writes_plain_invariant_numbers()
    {
        Assert.Equal("1500", StatValue.Format(1500));
        Assert.Equal("0.0005", StatValue.Format(0.0005));
        Assert.Equal("-2.5", StatValue.Format(-2.5));
    }
}
