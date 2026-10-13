using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using Xunit;

namespace RwEditor.Tests;

/// <summary>Apply / undo / revert on throw-away synthetic copies: folder guard, locked-file guard, backups, undo round trip and "no partial write".</summary>
public class ApplyTests
{
    static readonly string[] Gen = { EditorSupport.AnchorGenerated, EditorSupport.SelectorGenerated };

    static ApplyOptions Opt(TestMod m, Action<int, string>? hook = null) => new() { BackupRoot = m.BackupRoot, BeforeWriteFile = hook };

    static Dictionary<string, byte[]> Snapshot(TestMod m) => Gen.ToDictionary(g => g, m.Bytes);

    static void AssertUnchanged(TestMod m, Dictionary<string, byte[]> before)
    {
        foreach (var g in Gen) Assert.Equal(before[g], m.Bytes(g));
        Assert.Empty(Directory.EnumerateFiles(m.Root, "*.rwe-tmp", SearchOption.AllDirectories));
    }

    static int BackupCount(TestMod m) => Directory.Exists(m.BackupRoot) ? Directory.EnumerateFiles(m.BackupRoot, "*.zip", SearchOption.AllDirectories).Count() : 0;

    [Fact]
    public void Apply_writes_the_generated_files_and_a_backup_then_undo_restores_the_original_bytes()
    {
        using var m = new TestMod();
        var before = Snapshot(m);
        var r = ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m));
        Assert.True(r.Written);
        Assert.NotNull(r.BackupPath);
        Assert.True(File.Exists(r.BackupPath));
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), m.Read(EditorSupport.AnchorGenerated));
        Assert.Equal(TestMod.Golden("selectorGroups.sample2.ini"), m.Read(EditorSupport.SelectorGenerated));
        Assert.True(r.Plan.Rules.AllPass);
        Assert.Empty(r.Plan.NewLintFindings);
        Assert.Empty(Directory.EnumerateFiles(m.Root, "*.rwe-tmp", SearchOption.AllDirectories));

        var backup = BackupStore.Read(r.BackupPath!);
        Assert.Equal("apply", backup.Kind);
        foreach (var f in backup.Files) Assert.Equal(before[f.RelPath], f.Bytes);

        var u = ApplyPipeline.Undo(m.Root, Opt(m));
        Assert.Equal(r.BackupPath, u.RestoredFrom);
        AssertUnchanged(m, before);
        Assert.True(File.Exists(u.SafetyBackupPath));
        Assert.True(u.RulesAfter.AllPass);

        // the safety backup holds the applied state, so the undo itself can be undone
        ApplyPipeline.Undo(m.Root, Opt(m), u.SafetyBackupPath);
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), m.Read(EditorSupport.AnchorGenerated));
    }

    [Fact]
    public void Applying_the_same_settings_twice_writes_nothing_the_second_time()
    {
        using var m = new TestMod();
        ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m));
        int n = BackupCount(m);
        var r2 = ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m));
        Assert.False(r2.Written);
        Assert.Equal(n, BackupCount(m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_failure_during_the_write_leaves_no_partial_change(int failAt)
    {
        using var m = new TestMod();
        var before = Snapshot(m);
        var e = Assert.Throws<ApplyFailedException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root,
            Opt(m, (i, rel) => { if (i == failAt) throw new IOException("simulated disk failure before " + rel); })));
        Assert.True(e.Restored);
        AssertUnchanged(m, before);
    }

    [Fact]
    public void Invalid_settings_write_nothing_and_make_no_backup()
    {
        using var m = new TestMod();
        var before = Snapshot(m);
        var s = TestMod.Sample2();
        s.SpawnGroups[1].Units[0].Unit = "noSuchUnit";
        var e = Assert.Throws<SettingsInvalidException>(() => ApplyPipeline.Apply(s, m.Root, Opt(m)));
        Assert.Contains(e.Findings, f => f.Code == "UNIT_UNKNOWN");
        AssertUnchanged(m, before);
        Assert.Equal(0, BackupCount(m));
    }

    [Fact]
    public void A_changed_locked_file_makes_the_copy_read_only()
    {
        using var m = new TestMod();
        var before = Snapshot(m);
        m.Put("myAssets/extraStartAnchor.ini", m.Read("myAssets/extraStartAnchor.ini") + "\n#one more line\n");
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m)));
        Assert.Equal("LOCKED_FILES_CHANGED", e.Code);
        Assert.Contains("hash differs: myAssets/extraStartAnchor.ini", e.Message);
        AssertUnchanged(m, before);
    }

    [Fact]
    public void A_new_file_under_a_lock_glob_is_also_refused()
    {
        using var m = new TestMod();
        m.Put("myAssets/sneaky.ini", "[core]\nname: sneaky\n");
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m)));
        Assert.Equal("LOCKED_FILES_CHANGED", e.Code);
        Assert.Contains("not in json: myAssets/sneaky.ini", e.Message);
    }

    [Fact]
    public void Revert_defaults_writes_the_empty_includes_with_a_backup()
    {
        using var m = new TestMod();
        ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m));
        var r = ApplyPipeline.RevertDefaults(m.Root, Opt(m));
        Assert.True(r.Written);
        Assert.Equal("revert-defaults", BackupStore.Read(r.BackupPath!).Kind);
        var empty = SpawnGroupGenerator.GenerateDefaults(m.Context());
        Assert.Equal(empty.AnchorGroups, m.Read(EditorSupport.AnchorGenerated));
        Assert.Equal(empty.SelectorGroups, m.Read(EditorSupport.SelectorGenerated));
        // undo of the revert brings the groups back
        ApplyPipeline.Undo(m.Root, Opt(m));
        Assert.Equal(TestMod.Golden("anchorGroups.sample2.ini"), m.Read(EditorSupport.AnchorGenerated));
    }

    [Fact]
    public void Undo_without_any_backup_is_refused()
    {
        using var m = new TestMod();
        Assert.Equal("NO_BACKUP", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, Opt(m))).Code);
    }

    [Fact]
    public void Undo_refuses_a_backup_of_another_copy()
    {
        using var a = new TestMod();
        using var b = new TestMod();
        var r = ApplyPipeline.Apply(TestMod.Sample2(), a.Root, Opt(a));
        var before = Snapshot(b);
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(b.Root, Opt(b), r.BackupPath));
        Assert.Equal("BACKUP_OTHER_COPY", e.Code);
        AssertUnchanged(b, before);
    }

    // ------------------------------------------------------------------------------------------------ folder guard
    [Fact]
    public void Steam_workshop_content_folder_is_refused()
    {
        using var m = new TestMod(subPath: Path.Combine("steamapps", "workshop", "content", "647960", "3817316788"));
        var before = Snapshot(m);
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m)));
        Assert.Equal("FOLDER_WORKSHOP", e.Code);
        AssertUnchanged(m, before);
        Assert.Equal("FOLDER_WORKSHOP", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, Opt(m))).Code);
    }

    [Theory]
    [InlineData("baseline_V271.7_pristine")]
    [InlineData("anything_pristine")]
    [InlineData("baseline_copy")]
    public void Pristine_baseline_is_refused(string name)
    {
        using var m = new TestMod(subPath: name);
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m)));
        Assert.Equal("FOLDER_BASELINE", e.Code);
    }

    [Fact]
    public void Folder_without_editor_support_is_refused()
    {
        using var m = new TestMod(withSupport: false);
        var before = Snapshot(m);
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m)));
        Assert.Equal("NO_EDITOR_SUPPORT", e.Code);
        AssertUnchanged(m, before);
        Assert.Equal("NO_EDITOR_SUPPORT", Assert.Throws<EditorRefusalException>(() => ModContext.Load(m.Root)).Code);
    }

    [Fact]
    public void Folder_inside_a_git_tree_is_refused_unless_allowed()
    {
        using var m = new TestMod(subPath: Path.Combine("repo", "src"));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(m.Root)!, ".git"));
        Assert.Equal("FOLDER_GIT_TREE", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m))).Code);
        var r = ApplyPipeline.Apply(TestMod.Sample2(), m.Root, new ApplyOptions { BackupRoot = m.BackupRoot, AllowGitTree = true });
        Assert.True(r.Written);
    }

    [Fact]
    public void Missing_folder_is_refused() =>
        Assert.Equal("FOLDER_MISSING", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(Path.Combine(Path.GetTempPath(), "rwe-no-such-" + Guid.NewGuid()), false)).Code);

    [Fact]
    public void Unknown_editor_support_schema_is_refused()
    {
        using var m = new TestMod();
        m.Put(EditorSupport.FileName, m.Read(EditorSupport.FileName).Replace("\"schema\": 1,", "\"schema\": 2,"));
        Assert.Equal("SCHEMA_UNKNOWN", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m))).Code);
    }

    [Fact]
    public void Editor_support_listing_other_generated_files_is_refused()
    {
        using var m = new TestMod();
        m.Put(EditorSupport.FileName, m.Read(EditorSupport.FileName).Replace("\"editorGenerated/selectorGroups.ini\"]", "\"myAssets/isCommand.ini\"]"));
        Assert.Equal("LAYOUT_UNKNOWN", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, Opt(m))).Code);
    }

    [Fact]
    public void Backup_zip_with_a_foreign_path_is_never_restored()
    {
        using var m = new TestMod();
        string zip = BackupStore.Write(m.BackupRoot, m.Root, "apply", new[] { new BackupFile("myAssets/extraStartAnchor.ini", new byte[] { 1, 2, 3 }) }, null, DateTime.UtcNow);
        var before = m.Bytes("myAssets/extraStartAnchor.ini");
        Assert.Equal("BACKUP_FORBIDDEN_PATH", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, Opt(m), zip)).Code);
        Assert.Equal(before, m.Bytes("myAssets/extraStartAnchor.ini"));
    }
}
