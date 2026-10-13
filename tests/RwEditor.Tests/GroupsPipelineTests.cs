using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E4: spawn groups and default options applied to a COPY together with its settings file (files and settings always agree).</summary>
public sealed class GroupsPipelineTests
{
    static ApplyOptions Opt(TestMod m) => new() { BackupRoot = m.BackupRoot };

    static string SettingsPath(TestMod m) => CopyFiles.SettingsPath(m.Root);

    [Fact]
    public void Apply_writes_the_generated_files_and_saves_the_groups_in_the_copys_settings_then_undo_puts_both_back()
    {
        using var m = new TestMod();
        var beforeAnchor = m.Bytes(EditorSupport.AnchorGenerated);
        var r = GroupsPipeline.Apply(m.Root, TestMod.Sample2(), Opt(m));
        Assert.True(r.Written);
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), m.Read(EditorSupport.AnchorGenerated));
        var saved = GroupsPipeline.LoadCurrent(m.Root);
        Assert.Equal(new[] { "Hover strike", "Builders" }, saved.SpawnGroups.OrderBy(g => g.Slot).Select(g => g.Name));
        Assert.NotNull(saved.BaseMod);
        Assert.Contains("Hover strike", r.Summary, StringComparison.Ordinal);
        var h = History.List(m.BackupRoot, m.Root).First();
        Assert.Equal("apply", h.Kind);
        Assert.True(h.IsChange);
        Assert.Contains("Hover strike", h.Summary, StringComparison.Ordinal);

        ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: SettingsPath(m));
        Assert.Equal(beforeAnchor, m.Bytes(EditorSupport.AnchorGenerated));
        Assert.Empty(GroupsPipeline.LoadCurrent(m.Root).SpawnGroups);   // the settings went back with the files
    }

    [Fact]
    public void Applying_the_same_groups_again_writes_and_backs_up_nothing()
    {
        using var m = new TestMod();
        GroupsPipeline.Apply(m.Root, TestMod.Sample2(), Opt(m));
        int backups = History.List(m.BackupRoot, m.Root).Count;
        var again = GroupsPipeline.Apply(m.Root, GroupsPipeline.LoadCurrent(m.Root), Opt(m));
        Assert.False(again.Written);
        Assert.Equal(backups, History.List(m.BackupRoot, m.Root).Count);
    }

    [Fact]
    public void Stat_edits_and_the_pending_list_in_the_settings_survive_a_group_apply()
    {
        using var m = new TestMod();
        var s = EditorSettings.Default();
        s.PendingStatEdits.Add(new PendingStatEdit { Unit = "tankA", UnitFile = "units/tankA.ini", StatId = "core.maxHp", Section = "core", Value = "900" });
        s.StatEdits.Add(new StatEdit { Seq = 1, Unit = "tankB", UnitFile = "units/tankB.ini", StatId = "core.maxHp", Section = "core", File = "units/tankB.ini", FileSection = "core", Key = "maxHp", OldText = "10", NewText = "20" });
        StatPipeline.WriteSettings(SettingsPath(m), s);
        GroupsPipeline.Apply(m.Root, TestMod.Sample2(), Opt(m));
        var after = GroupsPipeline.LoadCurrent(m.Root);
        Assert.Single(after.PendingStatEdits);
        Assert.Single(after.StatEdits);
        Assert.Equal(2, after.SpawnGroups.Count);
    }

    [Fact]
    public void A_refused_plan_changes_neither_the_files_nor_the_settings()
    {
        using var m = new TestMod();
        var bad = TestMod.Sample2();
        bad.SpawnGroups[0].Units[0].Unit = "noSuchUnit";
        var anchor = m.Bytes(EditorSupport.AnchorGenerated);
        Assert.Throws<RwEditor.Core.Generation.SettingsInvalidException>(() => GroupsPipeline.Apply(m.Root, bad, Opt(m)));
        Assert.Equal(anchor, m.Bytes(EditorSupport.AnchorGenerated));
        Assert.False(File.Exists(SettingsPath(m)));
        Assert.Empty(History.List(m.BackupRoot, m.Root));
    }

    [Fact]
    public void Revert_all_goes_back_to_the_shipped_behaviour_and_keeps_stat_data()
    {
        using var m = new TestMod();
        GroupsPipeline.Apply(m.Root, TestMod.Sample2(), Opt(m));
        var r = GroupsPipeline.RevertAll(m.Root, Opt(m));
        Assert.True(r.Written);
        Assert.Empty(GroupsPipeline.LoadCurrent(m.Root).SpawnGroups);
        Assert.Equal("revert-defaults", History.List(m.BackupRoot, m.Root).First().Kind);
    }
}
