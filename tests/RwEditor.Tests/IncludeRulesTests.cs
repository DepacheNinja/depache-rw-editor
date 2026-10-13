using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Negative controls for the C# port of the include rules: each break of the generated text is reported by name (the same breaks C-026 used on the PowerShell checks).</summary>
public class IncludeRulesTests(SharedMod shared) : IClassFixture<SharedMod>
{
    readonly ModContext _ctx = shared.Ctx;

    RuleReport Check(Func<string, string>? anchor = null, Func<string, string>? selector = null)
    {
        var gen = SpawnGroupGenerator.Generate(TestMod.Sample2(), _ctx);
        var files = new Dictionary<string, string>
        {
            [EditorSupport.AnchorGenerated] = anchor == null ? gen.AnchorGroups : anchor(gen.AnchorGroups),
            [EditorSupport.SelectorGenerated] = selector == null ? gen.SelectorGroups : selector(gen.SelectorGroups),
        };
        return IncludeRules.Check(_ctx.Snapshot.WithFiles(files));
    }

    static string Rep(string s, string a, string b)
    {
        Assert.Contains(a, s);
        return s.Replace(a, b);
    }

    [Fact]
    public void Generated_sample_passes_every_rule()
    {
        var r = Check();
        Assert.True(r.AllPass, string.Join("\n", r.Failures));
        Assert.True(r.Passed > 40);
    }

    [Fact]
    public void Shipped_empty_includes_pass() => Assert.True(IncludeRules.Check(_ctx.Snapshot).AllPass);

    [Theory]
    [InlineData("or-branch", "(ii-m) [hiddenAction_ug01S] trigger is exactly the canonical")]
    [InlineData("wrong-done", "sets its Done tag ug01Done")]
    [InlineData("count-star", "spawns 1..12 single entries")]
    [InlineData("unknown-unit", "spawns 1..12 single entries")]
    [InlineData("random-offset", "spawns 1..12 single entries")]
    [InlineData("core-copyFrom", "generated [core] holds only 'dont_load: true'")]
    [InlineData("locked-section", "re-uses a section name")]
    [InlineData("deleteSelf", "[hiddenAction_ug01S] is exactly id:ug01S")]
    [InlineData("init-missing-w02", "ugInit adds exactly xGrpWait")]
    [InlineData("gate-missing-w02", "ugGate removes xGrpWait exactly")]
    [InlineData("release-mode", "[hiddenAction_ug02R] exists, matches the humans mode")]
    [InlineData("orphan-release", "no release action without its spawn action")]
    [InlineData("bad-name", "every generated section has a generated name")]
    public void Anchor_breaks_are_reported(string kind, string failing)
    {
        Func<string, string> mut = kind switch
        {
            "or-branch" => a => Rep(a, "> 0 and not self.globalTeamTags(includes=\"ug01Done\")", "> 0 or not self.globalTeamTags(includes=\"ug01Done\")"),
            "wrong-done" => a => Rep(a, "addGlobalTeamTags: ug01Done", "addGlobalTeamTags: ug02Done"),
            "count-star" => a => Rep(a, "heliBase(skipIfOverlapping", "heliBase*2(skipIfOverlapping"),
            "unknown-unit" => a => Rep(a, "Valkyrie(", "ValkyrieNOPE("),
            "random-offset" => a => Rep(a, "offsetX=370, offsetY=-25", "offsetX=370, offsetY=-25, offsetRandomX=20"),
            "core-copyFrom" => a => Rep(a, "dont_load: true\n", "dont_load: true\ncopyFrom: ROOT:/x.ini\n"),
            "locked-section" => a => a + "\n[hiddenAction_cleanup]\nid:cleanup\n",
            "deleteSelf" => a => Rep(a, "addGlobalTeamTags: ug01Done\n", "addGlobalTeamTags: ug01Done\ndeleteSelf: true\n"),
            "init-missing-w02" => a => Rep(a, "temporarilyAddTags: xGrpWait, ugW01, ugW02", "temporarilyAddTags: xGrpWait, ugW01"),
            "gate-missing-w02" => a => Rep(a, " and not self.tags(includes=\"ugW02\")\n", "\n"),
            "release-mode" => a => Rep(a, "numberOfUnitsInNeutralTeam(withTag='ug02') < 1 or self.isControlledByAI)", "numberOfUnitsInNeutralTeam(withTag='ug02') < 1)"),
            "orphan-release" => a => a + "\n[hiddenAction_ug05R]\nid:ug05R\nautoTrigger: if true\ntemporarilyRemoveTags: ugW05\n",
            "bad-name" => a => a + "\n[hiddenAction_extra]\nid:extra\n",
            _ => throw new ArgumentException(kind),
        };
        var r = Check(anchor: mut);
        Assert.False(r.AllPass);
        Assert.Contains(r.Failures, f => f.Text.Contains(failing, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hide-fairAI", "ugHide adds only xh<tag>")]
    [InlineData("hide-unknown", "ugHide adds only xh<tag>")]
    [InlineData("def-hidden", "ugDef turns on only known option or group tags")]
    [InlineData("pos-8001", "typeId 4 and pos 8002")]
    [InlineData("typeId-1", "typeId 4 and pos 8002")]
    [InlineData("colon-name", "group name is 1-40 allowed characters")]
    [InlineData("off-extra-key", "[action_ug01Off] exists and is exactly")]
    [InlineData("msg-missing", "[action_ug02Msg] exists and is exactly")]
    [InlineData("template-section", "re-uses a section name")]
    [InlineData("core-global", "generated [core] holds only 'dont_load: true'")]
    [InlineData("group-only-in-selector", "(iv-m) selector group tags")]
    public void Selector_breaks_are_reported(string kind, string failing)
    {
        Func<string, string> mut = kind switch
        {
            "hide-fairAI" => s => Rep(s, "temporarilyAddTags: xhextraACV", "temporarilyAddTags: xhextraACV, xhfairAI"),
            "hide-unknown" => s => Rep(s, "temporarilyAddTags: xhextraACV", "temporarilyAddTags: xhnope"),
            "def-hidden" => s => Rep(s, "temporarilyAddTags: ug01\n", "temporarilyAddTags: ug01, extraACV\n"),
            "pos-8001" => s => Rep(s, "@define actionTag: ug01\n@define actionName: Hover strike\n@define actionNameZh: Hover strike\n@define typeId: 4\npos: 8002",
                                      "@define actionTag: ug01\n@define actionName: Hover strike\n@define actionNameZh: Hover strike\n@define typeId: 4\npos: 8001"),
            "typeId-1" => s => Rep(s, "@define actionNameZh: Builders\n@define typeId: 4", "@define actionNameZh: Builders\n@define typeId: 1"),
            "colon-name" => s => Rep(s, "@define actionName: Builders", "@define actionName: Builders: v2"),
            "off-extra-key" => s => Rep(s, "@copyFromSection: action_ug01On, template_turnOffOption\n", "@copyFromSection: action_ug01On, template_turnOffOption\npos: 1\n"),
            "msg-missing" => s => Rep(s, "[action_ug02Msg]\n@copyFromSection: action_ug02On, template_notificationAction\n", ""),
            "template-section" => s => s + "\n[template_turnOnOption]\npos: 1\n",
            "core-global" => s => Rep(s, "dont_load: true\n", "dont_load: true\n@global x: 1\n"),
            "group-only-in-selector" => s => s + "\n[action_ug03On]\n@copyFromSection: template_turnOnOption\n@define actionTag: ug03\n@define actionName: Three\n@define actionNameZh: Three\n@define typeId: 4\npos: 8002\ndescription:- x\n[action_ug03Off]\n@copyFromSection: action_ug03On, template_turnOffOption\n[action_ug03Msg]\n@copyFromSection: action_ug03On, template_notificationAction\n",
            _ => throw new ArgumentException(kind),
        };
        var r = Check(selector: mut);
        Assert.False(r.AllPass);
        Assert.Contains(r.Failures, f => f.Text.Contains(failing, StringComparison.Ordinal));
    }

    [Fact]
    public void Ten_visible_section_4_buttons_fail_the_cap()
    {
        // unchecked generation lets the rule itself be exercised: 5 shown defaults + 5 groups
        var s = new RwEditor.Core.Settings.EditorSettings();
        for (int k = 1; k <= 5; k++)
            s.SpawnGroups.Add(new RwEditor.Core.Settings.SpawnGroup { Slot = k, Name = "G" + k, Units = { new RwEditor.Core.Settings.GroupUnit { Unit = "smallBot" } } });
        var gen = SpawnGroupGenerator.GenerateUnchecked(s, _ctx);
        var r = IncludeRules.Check(_ctx.Snapshot.WithFiles(gen.ByRelPath));
        Assert.Contains(r.Failures, f => f.Text.Contains("section 4 shows at most 9 option buttons (10;", StringComparison.Ordinal));
    }

    [Fact]
    public void Include_removed_from_the_locked_copyFrom_is_reported()
    {
        var anchorText = _ctx.Snapshot.GetText(EditorSupport.AnchorLocked)!.Replace(", ROOT:/editorGenerated/anchorGroups.ini", "");
        var r = IncludeRules.Check(_ctx.Snapshot.WithFiles(new Dictionary<string, string> { [EditorSupport.AnchorLocked] = anchorText }));
        Assert.Contains(r.Failures, f => f.Text.Contains("anchor copyFrom includes ROOT:/editorGenerated/anchorGroups.ini exactly once", StringComparison.Ordinal));
    }
}
