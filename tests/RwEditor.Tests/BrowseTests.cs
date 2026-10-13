using RwEditor.Core.Browse;
using RwEditor.Core.Diagnostics;
using RwEditor.Core.Imaging;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E2: the unit browser's rows / search / filter / sort, the read-only stat view, the icon builder and the session log.</summary>
public sealed class BrowseTests
{
    static readonly IReadOnlyList<UnitRow> Rows = UnitCatalog.Build(new StatFixture().Model);

    [Fact]
    public void Rows_carry_area_kind_category_and_flags()
    {
        var model = new StatFixture().Model;
        Assert.Equal(model.Units.Count, Rows.Count);
        var tower = Rows.Single(r => r.Name == "tower");
        Assert.True(tower.IsBuilding);
        Assert.Equal("Building", tower.Kind);
        Assert.Equal("units", tower.Category);
        var fe = Rows.Single(r => r.Name == "feGen");
        Assert.Equal("fe", fe.Area);
        Assert.Equal("Fabricator Extreme", fe.AreaLabel);
        Assert.Equal("(mod top folder)", fe.Category);    // donors/fe/gen.ini sits directly in the donor's top folder
        Assert.Equal("depache", Rows.Single(r => r.Name == "acvMainBldg").Area);
        Assert.True(Rows.Single(r => r.Name == "lockedUnit").Locked);
        Assert.True(Rows.Where(r => r.Name == "dupName").All(r => r.DuplicateName));
        Assert.All(Rows, r => Assert.Equal(r, Rows[r.Index]));
    }

    [Theory]
    [InlineData(true, null, "Building")]
    [InlineData(false, "LAND", "Land")]
    [InlineData(false, "air", "Air")]
    [InlineData(false, "OVER_CLIFF_WATER", "Over cliffs and water")]
    [InlineData(false, "NONE", "Does not move")]
    [InlineData(false, null, "Unit (no movement type)")]
    [InlineData(false, "${x}", "Unit (computed movement)")]
    [InlineData(false, "SPACE_THING", "Space thing")]
    public void Kind_reads_the_movement_type_literally(bool building, string? movement, string expected) =>
        Assert.Equal(expected, UnitCatalog.KindOf(building, movement));

    [Theory]
    [InlineData("units/a.ini", "units")]
    [InlineData("a.ini", "(mod top folder)")]
    [InlineData("donors/fe/generators/gen.ini", "generators")]
    [InlineData("donors/vte/laser/sub/x.ini", "laser")]
    [InlineData("donors/moo/a.ini", "(mod top folder)")]
    public void Category_is_the_authors_folder(string rel, string expected) => Assert.Equal(expected, UnitCatalog.CategoryOf(rel));

    [Fact]
    public void Search_needs_every_word_in_the_name_or_the_ingame_name()
    {
        var withText = UnitCatalog.Build(new StatFixture(new Dictionary<string, string>
        {
            ["units/hov.ini"] = "[core]\nname: expHoverTank\ndisplayText: Experimental Hovertank\n",
            ["units/hov2.ini"] = "[core]\nname: hoverBuilder\ndisplayText: Hovercraft Engineer\n",
        }).Model);
        Assert.Equal(new[] { "expHoverTank", "hoverBuilder" }, UnitCatalog.Query(withText, new UnitQuery("hover")).Select(r => r.Name).OrderBy(x => x));
        Assert.Equal(new[] { "expHoverTank" }, UnitCatalog.Query(withText, new UnitQuery("experimental  HOVER")).Select(r => r.Name));
        Assert.Equal(new[] { "hoverBuilder" }, UnitCatalog.Query(withText, new UnitQuery("engineer")).Select(r => r.Name));   // display text only
        Assert.Empty(UnitCatalog.Query(withText, new UnitQuery("hover zebra")));
        Assert.Equal(withText.Count, UnitCatalog.Query(withText, new UnitQuery("  ")).Count);
    }

    [Fact]
    public void Filters_and_sorts_are_stable()
    {
        Assert.All(UnitCatalog.Query(Rows, new UnitQuery(Type: TypeFilter.Buildings)), r => Assert.True(r.IsBuilding));
        Assert.All(UnitCatalog.Query(Rows, new UnitQuery(Type: TypeFilter.Units)), r => Assert.False(r.IsBuilding));
        Assert.Equal(Rows.Count, UnitCatalog.Query(Rows, new UnitQuery(Type: TypeFilter.Units)).Count + UnitCatalog.Query(Rows, new UnitQuery(Type: TypeFilter.Buildings)).Count);
        Assert.Equal(new[] { "mooA" }, UnitCatalog.Query(Rows, new UnitQuery(Area: "moo")).Select(r => r.Name));
        var noLocked = UnitCatalog.Query(Rows, new UnitQuery(IncludeLocked: false));
        Assert.DoesNotContain(noLocked, r => r.Locked);
        Assert.Equal(Rows.Count(r => !r.Locked), noLocked.Count);
        Assert.Contains(UnitCatalog.Query(Rows, new UnitQuery()), r => r.Locked);
        var byName = UnitCatalog.Query(Rows, new UnitQuery(Sort: SortKey.Name)).Select(r => r.Name).ToList();
        Assert.Equal(byName.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), byName);
        var desc = UnitCatalog.Query(Rows, new UnitQuery(Sort: SortKey.Name, Descending: true)).Select(r => r.Name).ToList();
        Assert.Equal(byName.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList(), desc);
        // same query twice = same order (ties broken by internal name and file)
        Assert.Equal(UnitCatalog.Query(Rows, new UnitQuery(Sort: SortKey.Area)), UnitCatalog.Query(Rows, new UnitQuery(Sort: SortKey.Area)));
    }

    [Fact]
    public void Stat_view_shows_values_with_their_origin_in_plain_words()
    {
        var f = new StatFixture();
        var d = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "tankA"));
        StatRow S(string id) => d.Simple.Concat(d.Expand).First(r => r.Entry.Id == id);
        var hp = S("core.maxHp");
        Assert.Equal("1200 hit points", hp.ValueText);                         // E3: in the unit the user types in
        Assert.Equal(OriginKind.Own, hp.Kind);
        Assert.StartsWith("this unit's own file (units/tankA.ini, line 4)", hp.OriginText);
        Assert.Contains("also read by 1 unit(s) that copy this file", hp.OriginText);   // tankC copies tankA.ini
        Assert.Contains("If you raise it:", hp.Tooltip);
        Assert.Contains("If you lower it:", hp.Tooltip);
        Assert.True(hp.Editable);
        Assert.Equal(1200, hp.Ref!.Number);
        var speed = S("movement.moveSpeed");
        Assert.Equal(OriginKind.Template, speed.Kind);
        Assert.Contains("templates/tank_base.ini", speed.OriginText);
        Assert.Equal(3, speed.SharedBy);                    // tankA, tankB and tankC (via tankA) read it from the template line
        Assert.Contains("used by 3 units", speed.OriginText);
        Assert.Equal("20 seconds", S("core.buildSpeed").ValueText);
        Assert.Equal("0.5 seconds", d.Simple.First(r => r.Entry.Id == "turret.delay").ValueText);   // 30 frames
        Assert.Equal("500 credits", S("core.price.credits").ValueText);
        Assert.Contains(d.Simple, r => r.Entry.Id == "turret.delay" && r.Section == "turret_1" && r.Label.EndsWith("[turret_1]"));
        Assert.Equal(OriginKind.AllUnits, S("core.maxShield").Kind);
        Assert.Contains("all-units.template", S("core.maxShield").OriginText);
        Assert.Contains("units/tankA.ini", d.Chain);
        // a value that is not set shows the game default; expressions are read-only with the reason
        var e = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "exprUnit"));
        Assert.StartsWith("Read-only", e.Simple.First(r => r.Entry.Id == "core.maxHp").Note);
        Assert.Equal("${hpGlobal}", e.Simple.First(r => r.Entry.Id == "core.maxHp").ValueText);
        Assert.Contains("calculated from other values (a formula)", e.Simple.First(r => r.Entry.Id == "core.maxHp").Note);
        Assert.False(e.Simple.First(r => r.Entry.Id == "core.maxHp").Editable);
        Assert.Contains(e.Simple, r => !r.IsSet && r.ValueText.StartsWith("not set"));
        // a locked unit says so, in plain words, and nothing of it is editable
        var l = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "lockedUnit"));
        Assert.All(l.Simple, r => Assert.StartsWith("Locked to keep the game stable", r.Note));
        Assert.All(l.Simple, r => Assert.False(r.Editable));
        // E3: every other value is listed read-only with the reason
        Assert.Contains(d.Other!, o => o.Section == "core" && o.Key == "copyFrom" && o.Reason.StartsWith("Locked to keep the game stable"));
        var cfs = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "cfsUnit"));
        Assert.Contains(cfs.Other!, o => o.Section == "template_a" && o.Key == "x" && o.Value == "a" && o.Reason == "Not editable in this version");
        Assert.DoesNotContain(d.Other!, o => o.Section == "core" && o.Key == "maxHp");   // shown in Simple already
    }

    [RepoFact]
    public void Real_src_rows_match_the_unit_model()
    {
        var model = UnitModel.Load(Repo.Src);
        var rows = UnitCatalog.Build(model);
        Assert.Equal(1699, rows.Count);
        Assert.Equal(rows.Count, UnitCatalog.Query(rows, new UnitQuery()).Count);
        Assert.Contains(UnitCatalog.Query(rows, new UnitQuery("hover")), r => r.Name == "hoverBuilder");
        var d = StatView.Describe(model, StatDictionary.Embedded, rows.Single(r => r.Name == "heavyAATank"));
        Assert.Equal("11500 hit points", d.Simple.Single(r => r.Entry.Id == "core.maxHp").ValueText);
    }

    // ------------------------------------------------------------------------------------------------ icon
    [Fact]
    public void Icon_has_every_standard_size_and_is_deterministic()
    {
        var src = new RgbaImage(100, 80);
        for (int y = 0; y < 80; y++) for (int x = 0; x < 100; x++) src.Set(x, y, (byte)(x * 2), (byte)(y * 3), 50, 255);
        var ico = IconBuilder.Build(src);
        Assert.Equal(ico, IconBuilder.Build(src));
        var dir = IconBuilder.ReadDirectory(ico);
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, dir.Select(d => d.Width));
        Assert.All(dir, d => { Assert.Equal(d.Width, d.Height); Assert.Equal(32, d.Bits); });
        Assert.All(dir, d => Assert.False(d.IsPng));            // all DIB by default (a PNG entry broke the compiler's ApplicationIcon step)
        foreach (var d in dir)
            Assert.Equal(40 + d.Width * d.Width * 4 + ((d.Width + 31) / 32) * 4 * d.Width, d.Length);   // header + BGRA + 1-bit mask rows padded to 4 bytes
        var withPng = IconBuilder.ReadDirectory(IconBuilder.Build(src, pngFromSize: 256));
        Assert.Equal(new[] { false, false, false, false, false, false, true }, withPng.Select(d => d.IsPng));
        Assert.Throws<ArgumentException>(() => IconBuilder.Build(src, new[] { 16, 16 }));
        Assert.Throws<ArgumentException>(() => IconBuilder.Build(src, new[] { 512 }));
        Assert.Throws<InvalidDataException>(() => IconBuilder.ReadDirectory(new byte[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Fact]
    public void Resize_averages_areas_and_keeps_transparency_from_darkening_edges()
    {
        var img = new RgbaImage(4, 4);
        for (int y = 0; y < 4; y++) for (int x = 0; x < 2; x++) img.Set(x, y, 255, 0, 0, 255);   // left half red, right half transparent black
        var r = IconBuilder.Resize(img, 1);
        int i = r.Index(0, 0);
        Assert.Equal(255, r.Pixels[i]);       // colour stays pure red (alpha-weighted)
        Assert.InRange(r.Pixels[i + 3], 127, 128);   // half covered
        var sq = IconBuilder.CropSquare(new RgbaImage(10, 4));
        Assert.Equal((4, 4), (sq.Width, sq.Height));
    }

    [RepoFact]
    public void Committed_app_icon_has_every_standard_size()
    {
        string ico = Path.Combine(Repo.Root!, "tools", "RwEditor", "assets", "depache-rw-editor.ico");
        var dir = IconBuilder.ReadDirectory(File.ReadAllBytes(ico));
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, dir.Select(d => d.Width));
    }

    // ------------------------------------------------------------------------------------------------ session log
    [Fact]
    public void Session_log_is_written_once_and_crash_logs_carry_the_session()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var t = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc);
            var log = new SessionLog(dir, utcNow: () => t);
            log.Info("started");
            log.Warn("careful");
            Assert.False(Directory.Exists(dir));                       // nothing on disk until a write is asked for
            string? crash = log.WriteCrashLog(new InvalidOperationException("boom"), "opening a unit");
            Assert.NotNull(crash);
            string ct = File.ReadAllText(crash!);
            Assert.Contains("opening a unit", ct);
            Assert.Contains("boom", ct);
            Assert.Contains("started", ct);
            string? s1 = log.WriteSessionLog();
            log.Info("after");
            string? s2 = log.WriteSessionLog();
            Assert.Equal(s1, s2);
            Assert.DoesNotContain("after", File.ReadAllText(s1!));       // written once only
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            Assert.Equal(1, log.CrashCount);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Session_log_keeps_memory_bounded_and_never_throws_on_a_bad_folder()
    {
        var log = new SessionLog(Path.Combine(Path.GetTempPath(), "rwe-tests", "bad\0name"), maxLines: 100);
        for (int i = 0; i < 1000; i++) log.Info("line " + i);
        Assert.Equal(100, log.Lines.Count);
        Assert.Contains("line 0", log.Lines[0]);                       // the start of the session is kept
        Assert.Contains("line 999", log.Lines[^1]);
        Assert.Contains("dropped", log.Text);
        Assert.Null(log.WriteSessionLog());
        Assert.Null(log.WriteCrashLog(new Exception("x"), "y"));
    }
}
