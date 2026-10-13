using System.Text.RegularExpressions;
using RwEditor.Core;
using RwEditor.Core.Generation;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E5a: hiding, showing and deleting the existing options of ALL four selector sections, and putting them back.</summary>
public class MenuOptionsTests(SharedMod shared) : IClassFixture<SharedMod>
{
    readonly ModContext _ctx = shared.Ctx;

    [Fact]
    public void An_option_of_section_1_can_be_hidden_and_deleted_and_the_generated_files_pass_every_rule()
    {
        var s = TestMod.Sample2();
        s.SetOptionState("richStart", OptionState.Hidden);
        s.SetOptionState("extraACV", OptionState.Deleted);
        Assert.DoesNotContain(SettingsValidator.Validate(s, _ctx), f => f.Severity == Severity.Error);
        var gen = SpawnGroupGenerator.Generate(s, _ctx);
        Assert.Contains("temporarilyAddTags: xhrichStart, xhextraACV", gen.SelectorGroups, StringComparison.Ordinal);   // the selector's own file order
        Assert.True(IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath)).AllPass);
    }

    [Fact]
    public void Hidden_and_deleted_options_are_hidden_the_same_way_in_the_game()
    {
        var a = TestMod.Sample2(); a.SetOptionState("richStart", OptionState.Hidden);
        var b = TestMod.Sample2(); b.SetOptionState("richStart", OptionState.Deleted);
        Assert.Equal(SpawnGroupGenerator.Generate(a, _ctx).SelectorGroups, SpawnGroupGenerator.Generate(b, _ctx).SelectorGroups);
    }

    [Fact]
    public void Hideable_options_are_every_option_except_the_two_that_always_stay()
    {
        Assert.Equal(_ctx.Options.Count - 2, _ctx.HideableOptions.Count);
        Assert.DoesNotContain(_ctx.HideableOptions, o => ModContext.NeverHide.Contains(o.Tag));
        Assert.All(_ctx.HideableSection4Options, o => Assert.Contains(o, _ctx.HideableOptions));
    }

    [Fact]
    public void Putting_options_back_leaves_the_groups_alone()
    {
        var s = TestMod.Sample2();
        s.SetOptionState("richStart", OptionState.Hidden);
        s.SetOptionState("extraACV", OptionState.Hidden);
        int groups = s.SpawnGroups.Count;
        s.RevertOptions(new[] { "richStart" });
        Assert.Equal(OptionState.Shown, s.GetOptionState("richStart"));
        Assert.Equal(OptionState.Hidden, s.GetOptionState("extraACV"));
        s.RevertOptions(_ctx.Options.Select(o => o.Tag));
        Assert.Empty(s.StartOptions);
        Assert.Equal(groups, s.SpawnGroups.Count);
    }

    /// <summary>The real selector: four named sections, every option has a description, and hiding any hideable option passes the include rules.</summary>
    [RepoFact]
    public void Real_selector_has_four_named_sections_and_hiding_any_option_passes_the_rules()
    {
        var ctx = ModContext.Load(Repo.Src);
        foreach (var id in new[] { "1", "2", "3", "4" }) Assert.True(ctx.SectionNames.ContainsKey(id), "section " + id);
        Assert.Equal("start unit options", ctx.SectionNames["4"]);
        Assert.All(ctx.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description), o.Tag + " has no description"));
        Assert.All(ctx.Options, o => Assert.Contains(o.TypeId, new[] { "1", "2", "3", "4" }));

        var s = EditorSettings.Default();
        foreach (var o in ctx.HideableOptions) s.SetOptionState(o.Tag, OptionState.Hidden);
        var findings = SettingsValidator.Validate(s, ctx);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
        var gen = SpawnGroupGenerator.Generate(s, ctx);
        foreach (var o in ctx.HideableOptions) Assert.Contains("xh" + o.Tag, gen.SelectorGroups, StringComparison.Ordinal);
        Assert.DoesNotContain("xhfairAI", gen.SelectorGroups, StringComparison.Ordinal);
        Assert.DoesNotContain("xhextraHuman", gen.SelectorGroups, StringComparison.Ordinal);
        Assert.True(IncludeRules.Check(ctx.Snapshot.WithFiles(gen.ByRelPath)).AllPass);
    }

    /// <summary>An option the mod turns on by itself when the match starts must never be hidable (hiding it would lock it on). Read from the real selector.</summary>
    [RepoFact]
    public void Every_option_the_selector_turns_on_by_default_is_in_the_never_hide_list()
    {
        var ctx = ModContext.Load(Repo.Src);
        var doc = IniDocument.Parse(ctx.Snapshot.GetText(EditorSupport.SelectorLocked)!, EditorSupport.SelectorLocked, IniDialect.Checker);
        var tags = ctx.Options.Select(o => o.Tag).ToHashSet(StringComparer.Ordinal);
        var onByDefault = new List<string>();
        foreach (var sec in doc.Named.Where(x => x.Name.StartsWith("hiddenAction_", StringComparison.Ordinal)))
        {
            string? ev = doc.Get(sec.Name, "autoTriggerOnEvent");
            string? add = doc.Get(sec.Name, "temporarilyAddTags");
            if (ev == null || add == null || !ev.Trim().Equals("created", StringComparison.OrdinalIgnoreCase)) continue;
            onByDefault.AddRange(add.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(tags.Contains));
        }
        Assert.NotEmpty(onByDefault);
        Assert.All(onByDefault, t => Assert.Contains(t, ModContext.NeverHide));
    }
}
