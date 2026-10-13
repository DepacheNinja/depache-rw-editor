using System.Text;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>The disk side of stat edits on throw-away copies: guards, backup + write once, undo, revert to defaults, settings, no partial writes.</summary>
public class StatPipelineTests
{
    static readonly Dictionary<string, string> Extra = new()
    {
        ["templates/base.ini"] = "[core]\ndont_load: true\nname: base\nmaxHp: 100\nprice: credits = 10, rust = 2\n",
        ["units/a.ini"] = "[core]\nname: unitA\ncopyFrom: ROOT:/templates/base.ini\nmaxHp: 150\n",
        ["units/b.ini"] = "[core]\nname: unitB\ncopyFrom: ROOT:/templates/base.ini\n",
    };

    static TestMod Mod(string? sub = null, bool support = true) => new(sub, support, Extra);
    static ApplyOptions Opt(TestMod m, Action<int, string>? hook = null) => new() { BackupRoot = m.BackupRoot, BeforeWriteFile = hook };
    static string Settings(TestMod m) => Path.Combine(m.BackupRoot, "..", "settings.json");

    [Fact]
    public void Apply_writes_one_file_backs_it_up_records_the_edit_and_undo_restores_everything()
    {
        using var m = Mod();
        var before = m.Bytes("units/a.ini");
        var r = StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), Settings(m), Opt(m));
        Assert.Equal(new[] { "units/a.ini" }, r.WrittenFiles);
        Assert.Contains("maxHp: 175", m.Read("units/a.ini"));
        Assert.True(File.Exists(r.BackupPath));
        var info = BackupStore.Read(r.BackupPath);
        Assert.Equal("stat-edit", info.Kind);
        Assert.Equal(before, info.Files.Single().Bytes);
        Assert.Null(info.SettingsBytes);                           // the settings file did not exist before
        var s = SettingsJson.Load(Settings(m));
        Assert.Equal(HashStatus.Match, s.Hash);
        Assert.Single(s.Settings.StatEdits);
        Assert.NotNull(s.Settings.BaseMod);
        Assert.Empty(Directory.EnumerateFiles(m.Root, "*.rwe-tmp", SearchOption.AllDirectories));

        var u = ApplyPipeline.Undo(m.Root, Opt(m));
        Assert.Equal("stat-edit", u.Kind);
        Assert.Equal(before, m.Bytes("units/a.ini"));
        Assert.Equal(Path.GetFullPath(Settings(m)), u.SettingsPath);
    }

    [Fact]
    public void Revert_to_defaults_per_unit_key_area_and_all_gives_the_original_bytes()
    {
        using var m = Mod();
        var origA = m.Bytes("units/a.ini");
        var origB = m.Bytes("units/b.ini");
        string sp = Settings(m);
        StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), sp, Opt(m));
        StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "maxHp", "120"), sp, Opt(m));
        StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "core.price.rust", "5"), sp, Opt(m));
        Assert.Equal(3, SettingsJson.Load(sp).Settings.StatEdits.Count);

        var dry = StatPipeline.Revert(m.Root, sp, StatPipeline.Select("unitB", "core.price.rust", null), Opt(m), dryRun: true);
        Assert.False(dry.Written);
        Assert.Equal(3, SettingsJson.Load(sp).Settings.StatEdits.Count);

        var r1 = StatPipeline.Revert(m.Root, sp, StatPipeline.Select("unitB", "core.price.rust", null), Opt(m));   // per key
        Assert.Single(r1.Reverted);
        Assert.DoesNotContain("price:", m.Read("units/b.ini"));
        var r2 = StatPipeline.Revert(m.Root, sp, StatPipeline.Select(null, null, "main"), Opt(m));               // per mod area
        Assert.Equal(2, r2.Reverted.Count);
        Assert.Equal(origA, m.Bytes("units/a.ini"));
        Assert.Equal(origB, m.Bytes("units/b.ini"));
        Assert.Empty(SettingsJson.Load(sp).Settings.StatEdits);

        StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "160"), sp, Opt(m));
        StatPipeline.Revert(m.Root, sp, StatPipeline.Select(null, null, null), Opt(m));                          // all
        Assert.Equal(origA, m.Bytes("units/a.ini"));
    }

    [Fact]
    public void Revert_refuses_everything_when_a_line_was_changed_by_hand()
    {
        using var m = Mod();
        string sp = Settings(m);
        StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), sp, Opt(m));
        StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "maxHp", "120"), sp, Opt(m));
        m.Put("units/a.ini", m.Read("units/a.ini").Replace("maxHp: 175", "maxHp: 176"));
        var bBefore = m.Bytes("units/b.ini");
        var e = Assert.Throws<EditorRefusalException>(() => StatPipeline.Revert(m.Root, sp, StatPipeline.Select(null, null, null), Opt(m)));
        Assert.Equal("REVERT_CONFLICT", e.Code);
        Assert.Equal(bBefore, m.Bytes("units/b.ini"));               // nothing reverted
    }

    [Fact]
    public void Template_edit_is_written_only_with_the_flag()
    {
        using var m = Mod();
        var template = m.Bytes("templates/base.ini");
        // without the flag the value is written into the unit's own file; the shared template is never touched
        var r0 = StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "maxHp", "130"), Settings(m), Opt(m));
        Assert.Equal(new[] { "units/b.ini" }, r0.WrittenFiles);
        Assert.Equal(template, m.Bytes("templates/base.ini"));
        StatPipeline.Revert(m.Root, Settings(m), StatPipeline.Select(null, null, null), Opt(m));

        var r = StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "maxHp", "130", StatEditMode.Template), Settings(m), Opt(m));
        Assert.Equal(new[] { "templates/base.ini" }, r.WrittenFiles);
        Assert.Equal(new[] { "unitB" }, r.Plan.Affected.Select(a => a.Unit.Name));   // unitA has its own maxHp
        var r2 = StatPipeline.Apply(m.Root, new StatEditRequest("unitB", "core.price.credits", "12", StatEditMode.Template), Settings(m), Opt(m));
        Assert.Equal(new[] { "unitA", "unitB" }, r2.Plan.Affected.Select(a => a.Unit.Name).OrderBy(x => x));
    }

    [Theory]
    [InlineData(0)]
    public void A_failure_during_the_write_leaves_the_file_and_the_settings_untouched(int failAt)
    {
        using var m = Mod();
        var before = m.Bytes("units/a.ini");
        Assert.Throws<ApplyFailedException>(() => StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), Settings(m),
            Opt(m, (i, rel) => { if (i == failAt) throw new IOException("simulated"); })));
        Assert.Equal(before, m.Bytes("units/a.ini"));
        Assert.False(File.Exists(Settings(m)));
    }

    [Fact]
    public void Refused_edits_write_nothing_and_make_no_backup()
    {
        using var m = Mod();
        var before = m.Bytes("units/a.ini");
        foreach (var req in new[]
        {
            new StatEditRequest("unitA", "maxHp", "0"),             // out of range
            new StatEditRequest("unitA", "techLevel", "2"),         // not whitelisted (locked key)
            new StatEditRequest("managerUnit", "maxHp", "5"),       // unit in myAssets/** (locked)
        })
            Assert.ThrowsAny<EditorRefusalException>(() => StatPipeline.Apply(m.Root, req, Settings(m), Opt(m)));
        Assert.Equal(before, m.Bytes("units/a.ini"));
        Assert.False(Directory.Exists(m.BackupRoot) && Directory.EnumerateFiles(m.BackupRoot, "*.zip", SearchOption.AllDirectories).Any());
        Assert.False(File.Exists(Settings(m)));
    }

    [Theory]
    [InlineData(@"steamapps\workshop\content\333600\3817316788", true, "FOLDER_WORKSHOP")]
    [InlineData(@"baseline_V271.7_pristine", true, "FOLDER_BASELINE")]
    [InlineData(@"copy_pristine", true, "FOLDER_BASELINE")]
    [InlineData(null, false, "NO_EDITOR_SUPPORT")]
    public void Forbidden_folders_are_refused(string? sub, bool support, string code)
    {
        using var m = Mod(sub, support);
        var e = Assert.ThrowsAny<EditorRefusalException>(() => StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), Settings(m), Opt(m)));
        Assert.Equal(code, e.Code);
        Assert.DoesNotContain("175", m.Read("units/a.ini"));
    }

    [Fact]
    public void A_folder_inside_a_git_work_tree_is_refused()
    {
        using var m = Mod();
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(m.Root)!, ".git"));
        var e = Assert.ThrowsAny<EditorRefusalException>(() => StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), Settings(m), Opt(m)));
        Assert.Equal("FOLDER_GIT_TREE", e.Code);
    }

    [Fact]
    public void A_copy_with_a_changed_locked_file_is_read_only()
    {
        using var m = Mod();
        m.Put("myAssets/managerUnit.ini", "[core]\nname: managerUnit\nradius: 11\n");
        var e = Assert.ThrowsAny<EditorRefusalException>(() => StatPipeline.Apply(m.Root, new StatEditRequest("unitA", "maxHp", "175"), Settings(m), Opt(m)));
        Assert.Equal("LOCKED_FILES_CHANGED", e.Code);
    }

    [Fact]
    public void Undo_never_restores_a_locked_file_from_a_stat_backup()
    {
        using var m = Mod();
        string zip = BackupStore.Write(m.BackupRoot, m.Root, "stat-edit", new[] { new BackupFile("myAssets/managerUnit.ini", Encoding.UTF8.GetBytes("x")) }, null, DateTime.UtcNow);
        var e = Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, Opt(m), zip));
        Assert.Equal("BACKUP_FORBIDDEN_PATH", e.Code);
    }
}
