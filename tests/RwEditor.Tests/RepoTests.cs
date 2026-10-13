using System.Text;
using System.Text.RegularExpressions;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Read-only tests against the REAL shipped mod source and design doc of the repository (skipped outside it). Nothing here writes.</summary>
public class RepoTests
{
    static ModContext? _ctx;
    static ModContext Ctx => _ctx ??= ModContext.Load(Repo.Src);

    [RepoFact]
    public void Real_mod_sample2_matches_the_golden_files()
    {
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), Ctx);
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), gen.AnchorGroups);
        Assert.Equal(TestMod.Golden("selectorGroups.sample2.ini"), gen.SelectorGroups);
    }

    [RepoFact]
    public void Sample_settings_file_in_the_repo_parses_to_the_same_settings()
    {
        var r = SettingsJson.Load(Path.Combine(Repo.Root!, "tools", "RwEditor", "samples", "sample-2groups.json"));
        Assert.Equal(SettingsJson.ContentHash(TestMod.Sample2()), r.ComputedHash);
    }

    /// <summary>The golden files ARE the canonical text of maps/DESIGN_EDITOR.md section 2.4 (the two ```ini blocks), byte for byte.</summary>
    [RepoFact]
    public void Golden_files_equal_the_design_doc_section_2_4()
    {
        string md = File.ReadAllText(Path.Combine(Repo.Root!, "maps", "DESIGN_EDITOR.md"), Encoding.UTF8).Replace("\r\n", "\n");
        int start = md.IndexOf("### 2.4", StringComparison.Ordinal);
        int end = md.IndexOf("## 3.", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var blocks = Regex.Matches(md.Substring(start, end - start), "```ini\n(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, blocks.Count);
        Assert.Equal(blocks[0], TestMod.Golden("selectorGroups.sample2.ini"));
        Assert.Equal(blocks[1], TestMod.Golden("anchorGroups.sample2.ini"));
    }

    [RepoFact]
    public void Real_mod_shipped_includes_and_the_sample_pass_the_ported_rules()
    {
        var shipped = IncludeRules.Check(Ctx.Snapshot);
        Assert.True(shipped.AllPass, string.Join("\n", shipped.Failures));
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), Ctx);
        var planned = IncludeRules.Check(Ctx.Snapshot.WithFiles(gen.ByRelPath));
        Assert.True(planned.AllPass, string.Join("\n", planned.Failures));
    }

    [RepoFact]
    public void Real_mod_locked_files_match_editor_support_json()
    {
        var v = Ctx.Support.VerifyLockedFiles(Repo.Src);
        Assert.True(v.Ok, v.Describe());
        Assert.Equal(Ctx.Support.LockedFileCount, v.LockedOnDisk);
    }

    /// <summary>The C# glob/hash port produces tools/editor-support.ps1's file byte for byte (same globs, same file set, same hashes, same text).</summary>
    [RepoFact]
    public void Editor_support_json_builder_matches_the_PowerShell_tool()
    {
        string version = File.ReadAllText(Path.Combine(Repo.Root!, "VERSION")).Trim();
        string shipped = File.ReadAllText(Path.Combine(Repo.Src, EditorSupport.FileName), Encoding.UTF8);
        Assert.Equal(shipped, EditorSupport.BuildJson(Repo.Src, version));
    }

    [RepoFact]
    public void Real_mod_section_4_options_are_the_expected_five()
    {
        var four = Ctx.Options.Where(o => o.TypeId == "4").Select(o => o.Tag).ToList();
        Assert.Equal(new[] { "extraACV", "extraSCB", "extraSpdr", "extraDrop", "extraHuman" }, four);
        Assert.Equal(new[] { "extraACV", "extraSCB", "extraSpdr", "extraDrop" }, Ctx.HideableSection4Options.Select(o => o.Tag));
        Assert.Equal(26, Ctx.Options.Count);
    }

    /// <summary>Lint port parity: the shipped src has no finding that the pristine baseline lacks (RwCatalog's "NEW (ours): 0" gate), and the sample adds none.</summary>
    [RepoFact]
    public void Lint_port_src_vs_baseline_and_sample()
    {
        Assert.True(Directory.Exists(Repo.Baseline), "baseline folder missing: " + Repo.Baseline);
        var src = RwLint.Run(Ctx.Snapshot);
        var baseline = RwLint.Run(ModSnapshot.Load(Repo.Baseline));
        var fresh = src.Except(baseline).ToList();
        Assert.True(fresh.Count == 0, string.Join("\n", fresh));
        Assert.True(src.Count > 0);   // the shipped mod carries the baseline's known findings (87 at C-026)
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), Ctx);
        Assert.Empty(RwLint.NewFindings(Ctx.Snapshot, Ctx.Snapshot.WithFiles(gen.ByRelPath)));
    }

    [RepoFact]
    public void Real_mod_units_of_the_sample_are_mobile_and_named_as_in_the_design()
    {
        foreach (var (unit, disp) in new[] { ("expHoverTank", "Experimental Hovertank"), ("Valkyrie", "Valkyrie Frigate"), ("hoverBuilder", "Hovercraft Engineer"), ("heliBase", "Heli Battlebase") })
        {
            var u = Ctx.FindUnit(unit);
            Assert.NotNull(u);
            Assert.True(u!.Loadable);
            Assert.False(u.Locked);
            Assert.Equal(disp, u.DisplayText);
            Assert.False(u.IsBuilding);
        }
    }
}
