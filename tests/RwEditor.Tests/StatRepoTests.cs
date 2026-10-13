using RwEditor.Core.Imaging;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>
/// Read-only checks of the unit model, dictionary and thumbnails against the REAL mod source (skipped, visibly, outside the mod repository). Nothing is
/// written anywhere. Expected values were read by hand from the ini lines named in each case (2026-10-12).
/// </summary>
public class StatRepoTests
{
    static UnitModel? _model;
    static UnitModel Model => _model ??= UnitModel.Load(Repo.Src);

    /// <summary>E3: the three gate changes planned together on the real mod, in RAM only (nothing written): a unit-only Health, a price, and the FE
    /// generators' shared build time (10 units listed), each with the full checks, one lint of the result, and the family / sharing info the app shows.</summary>
    [RepoFact]
    public void E3_batch_of_the_gate_changes_plans_on_the_real_mod_in_ram()
    {
        var d = StatDictionary.Embedded;
        var pend = new[]
        {
            new RwEditor.Core.Settings.PendingStatEdit { Unit = "heavyAATank", UnitFile = "aa_tanks/heavy_AA_tank.ini", StatId = "core.maxHp", Section = "core", Value = "13000" },
            new RwEditor.Core.Settings.PendingStatEdit { Unit = "heavyAATank", UnitFile = "aa_tanks/heavy_AA_tank.ini", StatId = "core.price.credits", Section = "core", Value = "6000" },
            new RwEditor.Core.Settings.PendingStatEdit { Unit = "GenT1", UnitFile = "donors/fe/generators/GenT1.ini", StatId = "core.buildSpeed", Section = "core", Value = "40s", Mode = "template" },
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plan = StatBatch.Plan(Model, d, pend, rel => File.ReadAllBytes(Path.Combine(Repo.Src, rel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(plan.CanApply, string.Join("\n", plan.Problems));
        Assert.Empty(plan.NewLintFindings);
        Assert.Equal(new[] { "aa_tanks/heavy_AA_tank.ini", "donors/fe/generators/gen_common.ini" }, plan.NewBytes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => "GenT" + i).OrderBy(x => x), plan.Items[2].Plan!.Affected.Select(a => a.Unit.Name).OrderBy(x => x));
        var info = StatStaging.Info(Model, d, null, Model.Find("GenT1")!, d.ById("core.buildSpeed")!, "core");
        Assert.True(info.Shared);
        Assert.Equal(10, info.SharingUnits.Count);
        Console.WriteLine($"E3 batch plan of 3 changes on the real mod: {sw.ElapsedMilliseconds} ms");
    }

    [RepoFact]
    public void Loadable_unit_count_and_areas_match_the_design_survey()
    {
        Assert.Equal(1699, Model.Units.Count);                                     // DESIGN_EDITOR 1.1: 1,699
        Assert.Equal(1613, Model.Units.Count(u => u.Area == "main"));
        Assert.Equal(2, Model.Units.Count(u => u.Area == "depache"));               // main mod 1,615 = main + depache
        Assert.Equal(20, Model.Units.Count(u => u.Area == "fe"));
        Assert.Equal(21, Model.Units.Count(u => u.Area == "moo"));
        Assert.Equal(43, Model.Units.Count(u => u.Area == "vte"));
    }

    [RepoFact]
    public void Depache_area_is_exactly_the_ini_files_added_outside_donors()
    {
        if (!Directory.Exists(Repo.Baseline)) return;   // the baseline is not part of every checkout
        var added = Directory.EnumerateFiles(Repo.Src, "*.ini", SearchOption.AllDirectories)
            .Select(f => ModSnapshot.ToRel(Repo.Src, f))
            .Where(r => !r.StartsWith("donors/", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(Repo.Baseline, r.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(added);
        foreach (var r in added) Assert.Equal("depache", ModAreas.Embedded.AreaOf(r));
        // and nothing else in the main tree is classified as depache
        var depache = Directory.EnumerateFiles(Repo.Src, "*.ini", SearchOption.AllDirectories).Select(f => ModSnapshot.ToRel(Repo.Src, f))
            .Where(r => ModAreas.Embedded.AreaOf(r) == "depache").OrderBy(r => r, StringComparer.Ordinal);
        Assert.Equal(added, depache);
    }

    /// <summary>Ten hand-read values: (unit, section, key, expected value text, origin file, origin line).</summary>
    public static IEnumerable<object[]> SpotChecks() => new[]
    {
        new object[] { "heavyAATank", "core", "price", "5200", "aa_tanks/heavy_AA_tank.ini", 6 },
        new object[] { "heavyAATank", "core", "maxHp", "11500", "aa_tanks/heavy_AA_tank.ini", 7 },
        new object[] { "heavyAATank", "core", "buildSpeed", "0.00055", "aa_tanks/heavy_AA_tank.ini", 8 },
        new object[] { "heavyAATank", "attack", "maxAttackRange", "550", "aa_tanks/heavy_AA_tank.ini", 48 },
        new object[] { "heavyAATank", "movement", "moveSpeed", "1.15", "aa_tanks/heavy_AA_tank.ini", 114 },
        new object[] { "heavyAATank", "turret_gunMuzzle3", "delay", "4", "aa_tanks/heavy_AA_tank.ini", 76 },    // via @copyFromSection turret_gunMuzzle1
        new object[] { "t3arty", "projectile_1", "areaDamage", "1300", "donors/vte/artillery/t3arty.ini", 92 },
        new object[] { "mdLightCan", "turret_1", "delay", "150", "donors/moo/bldg/mdCannon/mdLightCan.ini", 58 },
        new object[] { "GenT1", "core", "buildSpeed", "0.0005", "donors/fe/generators/gen_common.ini", 18 },     // template
        new object[] { "GenT1", "attack", "maxAttackRange", "0", "myAssets/typeBuildingCannotAttack.ini", 14 },  // template
        new object[] { "t3arty", "core", "maxHp", "42000", "donors/vte/artillery/t3arty.ini", 12 },
    };

    [RepoTheory, MemberData(nameof(SpotChecks))]
    public void Resolved_values_match_the_hand_read_ini_lines(string unit, string section, string key, string value, string file, int line)
    {
        var v = Model.Merge(Model.Find(unit)!).Get(section, key);
        Assert.NotNull(v);
        Assert.Equal(value, v!.Value);
        Assert.Equal(file, v.Origin.File);
        Assert.Equal(line, v.Origin.Line);
        // the origin really is that line
        var lines = File.ReadAllLines(Path.Combine(Repo.Src, file.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(value, lines[line - 1].Substring(lines[line - 1].IndexOf(':') + 1).Trim());
    }

    [RepoFact]
    public void Every_editable_shipped_value_is_inside_the_dictionary_hard_limits()
    {
        var rows = StatSurvey.Run(Model, StatDictionary.Embedded);
        Assert.All(rows, r => Assert.True(r.Clean, $"{r.Entry.Id}: {string.Join("; ", r.OutsideHard.Take(3))}"));
    }

    [RepoFact]
    public void Template_affected_count_for_the_fabricator_generator_build_time_is_ten()
    {
        // read-only plan: nothing is written (readBytes reads the real file into RAM only)
        var p = StatPatcher.Plan(Model, StatDictionary.Embedded, new StatEditRequest("GenT1", "core.buildSpeed", "40s", StatEditMode.Template),
            rel => File.ReadAllBytes(Path.Combine(Repo.Src, rel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(10, p.Affected.Count);
        Assert.Equal("donors/fe/generators/gen_common.ini", p.Record.File);
    }

    [RepoFact]
    public void Every_image_path_resolves_or_has_a_reason_other_than_a_missing_file()
    {
        // the game folder (for SHARED: images): RW_GAME_DIR, else the detected Steam install; without it SHARED: images fall back (allowed below)
        string? gameDir = Environment.GetEnvironmentVariable("RW_GAME_DIR") ?? RwEditor.Core.Setup.SteamLocator.DetectGameFolders().FirstOrDefault();
        if (gameDir != null && !Directory.Exists(Path.Combine(gameDir, "assets", "units"))) gameDir = null;
        var rep = Thumbnails.Build(Model, gameDir, 64);
        Assert.Equal(Model.Units.Count, rep.Thumbs.Count);
        Assert.DoesNotContain(rep.Thumbs, t => t.Note != null && t.Note.Contains("file not found", StringComparison.Ordinal) && !t.Note.Contains("SHARED", StringComparison.Ordinal));
        Assert.True(rep.Count(ThumbSource.Image) > Model.Units.Count / 2);
    }
}

public sealed class RepoTheoryAttribute : TheoryAttribute
{
    public RepoTheoryAttribute()
    {
        if (Repo.Root == null) Skip = "the mod repository (VERSION + src/editor-support.json + maps/DESIGN_EDITOR.md) is not above the test folder";
    }
}
