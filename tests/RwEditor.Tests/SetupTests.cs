using System.Text;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;
using RwEditor.Core.Setup;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E2: the editor-copy marker (PH-071), the folder guard that requires it, the "Make my copy" path logic and copy, Steam library parsing,
/// preferences.ini reading, mod-info titles and the app's own settings file.</summary>
public sealed class SetupTests
{
    static readonly DateTime T0 = new(2026, 10, 12, 13, 14, 15, DateTimeKind.Utc);

    // ------------------------------------------------------------------------------------------------ marker
    [Fact]
    public void Marker_round_trips_canonically()
    {
        using var m = new TestMod(withMarker: false);
        var mk = EditorCopyMarker.Create("My copy", @"D:\x\installed", EditorSupport.Load(m.Root), "0.1.0", T0.AddTicks(1234));
        string json = mk.ToJson();
        Assert.EndsWith("}\n", json);
        Assert.DoesNotContain("\r", json);
        var back = EditorCopyMarker.Parse(json, "t");
        Assert.Equal(mk, back);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(T0, back.CreatedUtc);                       // stored to the second
        Assert.Equal("9.9.9", back.SourceModVersion);
        Assert.Equal(EditorSupport.Load(m.Root).LockedManifestSha256, back.SourceLockedManifestSha256);
        Assert.True(json.IndexOf("\"copyId\"", StringComparison.Ordinal) < json.IndexOf("\"source\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"something-else\"}")]
    public void Marker_garbage_is_refused(string text) =>
        Assert.Equal("COPY_MARKER_INVALID", Assert.Throws<EditorRefusalException>(() => EditorCopyMarker.Parse(text, "t")).Code);

    [Fact]
    public void Marker_with_unknown_member_wrong_format_or_relative_source_is_refused()
    {
        using var m = new TestMod(withMarker: false);
        string ok = EditorCopyMarker.Create("c", @"D:\x\installed", EditorSupport.Load(m.Root), "0.1.0", T0).ToJson();
        foreach (var bad in new[]
        {
            ok.Replace("\"format\": 1", "\"format\": 2"),
            ok.Replace("\"name\": \"c\"", "\"name\": \"c\", \"extra\": 1"),
            ok.Replace(@"D:\\x\\installed", @"x\\installed"),
            ok.Replace("\"createdUtc\": \"2026-10-12T13:14:15Z\"", "\"createdUtc\": \"yesterday\""),
            ok.Replace("\"kind\": \"depache-rw-editor-copy\"", "\"kind\": \"copy\""),
        })
        {
            Assert.NotEqual(ok, bad);
            Assert.Equal("COPY_MARKER_INVALID", Assert.Throws<EditorRefusalException>(() => EditorCopyMarker.Parse(bad, "t")).Code);
        }
    }

    // ------------------------------------------------------------------------------------------------ folder guard (PH-071)
    [Fact]
    public void Guard_requires_the_marker_for_every_write()
    {
        using var m = new TestMod(withMarker: false);
        Assert.Equal("NO_COPY_MARKER", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(m.Root, false)).Code);
        var before = Directory.EnumerateFiles(m.Root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        Assert.Equal("NO_COPY_MARKER", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Apply(TestMod.Sample2(), m.Root, new ApplyOptions { BackupRoot = m.BackupRoot })).Code);
        Assert.Equal("NO_COPY_MARKER", Assert.Throws<EditorRefusalException>(() =>
            RwEditor.Core.Stats.StatPipeline.Apply(m.Root, new RwEditor.Core.Stats.StatEditRequest("expHoverTank", "maxHp", "100"), Path.Combine(m.BaseFolder, "s.json"), new ApplyOptions { BackupRoot = m.BackupRoot })).Code);
        Assert.Equal("NO_COPY_MARKER", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, new ApplyOptions { BackupRoot = m.BackupRoot })).Code);
        foreach (var (f, b) in before) Assert.Equal(b, File.ReadAllBytes(f));
        Assert.False(Directory.Exists(m.BackupRoot));
        // the same folder with a marker passes
        File.WriteAllText(Path.Combine(m.Root, EditorCopyMarker.FileName), EditorCopyMarker.Create("c", Path.Combine(m.BaseFolder, "src"), EditorSupport.Load(m.Root), "0.1.0", T0).ToJson());
        Assert.Equal(Path.GetFullPath(m.Root), FolderGuard.Check(m.Root, false));
        // read-only checks (no write) do not need it
        Assert.Equal(Path.GetFullPath(m.Root), FolderGuard.Check(m.Root, false, requireCopyMarker: false));
    }

    [Fact]
    public void Guard_refuses_a_damaged_marker_and_a_marker_naming_the_folder_itself_as_source()
    {
        using var m = new TestMod();
        File.WriteAllText(Path.Combine(m.Root, EditorCopyMarker.FileName), "{ damaged");
        Assert.Equal("COPY_MARKER_INVALID", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(m.Root, false)).Code);
        File.WriteAllText(Path.Combine(m.Root, EditorCopyMarker.FileName), EditorCopyMarker.Create("c", m.Root, EditorSupport.Load(m.Root), "0.1.0", T0).ToJson());
        Assert.Equal("COPY_IS_SOURCE", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(m.Root, false)).Code);
    }

    [Fact]
    public void Guard_location_rules_still_come_first_even_with_a_marker()
    {
        using var ws = new TestMod(subPath: Path.Combine("steamapps", "workshop", "content", "647960", "3817316788"));
        Assert.Equal("FOLDER_WORKSHOP", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(ws.Root, false)).Code);
        using var pr = new TestMod(subPath: "baseline_V271.7_pristine");
        Assert.Equal("FOLDER_BASELINE", Assert.Throws<EditorRefusalException>(() => FolderGuard.Check(pr.Root, false)).Code);
    }

    // ------------------------------------------------------------------------------------------------ wizard path logic
    sealed class Installed : IDisposable
    {
        public readonly TestMod Mod = new(subPath: Path.Combine("lib", "steamapps", "workshop", "content", "647960", "3817316788"), withMarker: false);
        public Installed()
        {
            Mod.Put("mod-info.txt", "[mod]\r\ntitle: Depache's Mod Tribute\r\nminVersion: v1.15\r\n");
            File.WriteAllText(Path.Combine(Mod.Root, "steam.dat"), "[steam]\nid: 3817316788\n");
            Directory.CreateDirectory(Path.Combine(Mod.Root, "art"));
            File.WriteAllBytes(Path.Combine(Mod.Root, "art", "x.png"), new byte[] { 137, 80, 78, 71, 1, 2, 3 });
            Directory.CreateDirectory(Path.Combine(Mod.Root, "art", "empty"));   // empty folders are copied too
        }
        public string Dest(string name) => Path.Combine(Mod.BaseFolder, "user", name);
        public void Dispose() => Mod.Dispose();
    }

    [Fact]
    public void Source_check_accepts_the_installed_mod_and_refuses_copies_and_foreign_folders()
    {
        using var inst = new Installed();
        var s = CopyMaker.CheckSource(inst.Mod.Root);
        Assert.Equal("Depache's Mod Tribute", s.Title);
        Assert.Equal("9.9.9", s.ModVersion);
        Assert.Equal(Directory.EnumerateFiles(inst.Mod.Root, "*", SearchOption.AllDirectories).Count() - 1, s.FileCount);   // steam.dat is not copied
        using var copy = new TestMod();
        Assert.Equal("SOURCE_IS_COPY", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckSource(copy.Root)).Code);
        using var foreign = new TestMod(withSupport: false);
        Assert.Equal("SOURCE_NOT_SUPPORTED", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckSource(foreign.Root)).Code);
        Assert.Equal("SOURCE_MISSING", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckSource(Path.Combine(inst.Mod.BaseFolder, "nope"))).Code);
        Assert.Equal("SOURCE_MISSING", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckSource("  ")).Code);
    }

    [Fact]
    public void Destination_check_refuses_every_unsafe_place()
    {
        using var inst = new Installed();
        string src = inst.Mod.Root;
        string Code(string dest) => Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckDestination(src, dest)).Code;

        Assert.Equal(Path.GetFullPath(inst.Dest("mymod")), CopyMaker.CheckDestination(src, inst.Dest("mymod")));                 // new folder: fine
        Directory.CreateDirectory(inst.Dest("empty"));
        Assert.Equal(Path.GetFullPath(inst.Dest("empty")), CopyMaker.CheckDestination(src, inst.Dest("empty")));                 // empty folder: fine
        Assert.Equal("FOLDER_WORKSHOP", Code(src));                                                                              // the Workshop item itself
        Assert.Equal("FOLDER_WORKSHOP", Code(Path.Combine(src, "..", "999")));                                                  // another Workshop item
        Assert.Equal("FOLDER_WORKSHOP", Code(Path.Combine(src, "sub")));                                                         // inside the source (Workshop wins)
        Assert.Equal("FOLDER_BASELINE", Code(Path.Combine(inst.Mod.BaseFolder, "user", "baseline_V271.7_pristine")));
        Assert.Equal("FOLDER_BASELINE", Code(Path.Combine(inst.Mod.BaseFolder, "user", "x_pristine", "copy")));
        Assert.Equal("FOLDER_ROOT", Code(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.Equal("FOLDER_MISSING", Code(""));
        Directory.CreateDirectory(inst.Dest("full"));
        File.WriteAllText(Path.Combine(inst.Dest("full"), "keep.txt"), "user file");
        Assert.Equal("DEST_NOT_EMPTY", Code(inst.Dest("full")));
        File.WriteAllText(inst.Dest("afile"), "x");
        Assert.Equal("DEST_IS_FILE", Code(inst.Dest("afile")));
        Directory.CreateDirectory(Path.Combine(inst.Mod.BaseFolder, "repo", ".git"));
        Assert.Equal("FOLDER_GIT_TREE", Code(Path.Combine(inst.Mod.BaseFolder, "repo", "copy")));

        // a source outside the Workshop: inside it, or a folder holding it, are refused
        using var local = new TestMod(subPath: Path.Combine("game", "mods", "units", "Tribute"), withMarker: false);
        Assert.Equal("DEST_IS_SOURCE", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckDestination(local.Root, local.Root)).Code);
        Assert.Equal("DEST_INSIDE_SOURCE", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckDestination(local.Root, Path.Combine(local.Root, "copy"))).Code);
        Assert.Equal("DEST_HOLDS_SOURCE", Assert.Throws<EditorRefusalException>(() => CopyMaker.CheckDestination(local.Root, Path.Combine(local.BaseFolder, "game"))).Code);
    }

    [Theory]
    [InlineData("My copy", null)]
    [InlineData("Hover-test_2 (v1.0)", null)]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("bad:name", "letters")]
    [InlineData("toolong-toolong-toolong-toolong-toolong-x", "letters")]
    [InlineData("new\nline", "letters")]
    public void Copy_name_rule(string name, string? problemContains)
    {
        var p = CopyMaker.NameProblem(name);
        if (problemContains == null) Assert.Null(p); else Assert.Contains(problemContains, p);
    }

    [Fact]
    public void Game_mods_destination_is_under_mods_units_with_a_safe_folder_name()
    {
        Assert.Equal(Path.Combine(@"D:\Games\Rusted Warfare", "mods", "units", "DepacheTribute_Custom_My_copy__1_"), CopyMaker.GameModsFolderFor(@"D:\Games\Rusted Warfare", "My copy (1)"));
        Assert.EndsWith(Path.Combine("DepacheRwEditor", "mymod"), CopyMaker.DefaultFolder);
    }

    [Fact]
    public void Make_copy_copies_byte_for_byte_renames_the_title_marks_the_copy_and_never_overwrites()
    {
        using var inst = new Installed();
        string dest = inst.Dest("mymod");
        var progress = new List<CopyProgress>();
        var r = CopyMaker.MakeCopy(inst.Mod.Root, dest, "My copy", "0.1.0", new SyncProgress<CopyProgress>(progress.Add), utcNow: () => T0);
        Assert.Equal(Path.GetFullPath(dest), r.Folder);
        Assert.Equal("[Custom My copy] Depache's Mod Tribute", r.NewTitle);
        Assert.Equal("Done", progress[^1].Step);
        // every file equal except mod-info.txt (title) and the Workshop's steam.dat (not copied); the marker is new
        foreach (var f in Directory.EnumerateFiles(inst.Mod.Root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(inst.Mod.Root, f);
            string target = Path.Combine(dest, rel);
            if (rel == "steam.dat") { Assert.False(File.Exists(target)); continue; }
            if (rel == "mod-info.txt") { Assert.Equal("[mod]\r\ntitle: [Custom My copy] Depache's Mod Tribute\r\nminVersion: v1.15\r\n", File.ReadAllText(target)); continue; }
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(target));
        }
        Assert.True(Directory.Exists(Path.Combine(dest, "art", "empty")));
        Assert.Equal(Directory.EnumerateDirectories(inst.Mod.Root, "*", SearchOption.AllDirectories).Count(),
                     Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories).Count());
        var mk = EditorCopyMarker.Load(dest);
        Assert.Equal("My copy", mk.Name);
        Assert.Equal(Path.GetFullPath(inst.Mod.Root), mk.SourcePath);
        Assert.Equal(r.Marker, mk);
        // E3: the copy's original values are saved next to the marker: every stat-writable file, none locked / generated, all equal to the copy now
        var bl = CopyBaseline.Load(dest)!;
        Assert.Equal(mk.CopyId, bl.CopyId);
        Assert.Equal(CopyBaseline.StatWritableFiles(dest, EditorSupport.Load(dest).LockedGlobs), bl.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("units/wall.ini", bl.Files.Keys);
        Assert.DoesNotContain(bl.Files.Keys, k => k.StartsWith("myAssets/", StringComparison.Ordinal) || k.StartsWith("editorGenerated/", StringComparison.Ordinal));
        Assert.Empty(bl.Differences(dest));
        Assert.Equal(Directory.EnumerateFiles(inst.Mod.Root, "*", SearchOption.AllDirectories).Count(r2 => !r2.EndsWith("steam.dat", StringComparison.Ordinal)) + 2,
                     Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count());   // + marker + baseline, nothing else
        // the new copy passes the write guard and its locked files are intact; no partial folder is left
        Assert.Equal(Path.GetFullPath(dest), CopyMaker.CheckExistingCopy(dest));
        Assert.True(EditorSupport.Load(dest).VerifyLockedFiles(dest).Ok);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(dest)!, "*" + CopyMaker.PartialSuffix + "*"));
        // and it really is editable end to end
        var applied = ApplyPipeline.Apply(TestMod.Sample2(), dest, new ApplyOptions { BackupRoot = Path.Combine(inst.Mod.BaseFolder, "bk") });
        Assert.True(applied.Written);
        // a second copy into the same folder is refused and changes nothing
        var bytes = File.ReadAllBytes(Path.Combine(dest, EditorCopyMarker.FileName));
        Assert.Equal("DEST_HAS_COPY", Assert.Throws<EditorRefusalException>(() => CopyMaker.MakeCopy(inst.Mod.Root, dest, "Again", "0.1.0")).Code);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dest, EditorCopyMarker.FileName)));
        // copying the copy is refused
        Assert.Equal("SOURCE_IS_COPY", Assert.Throws<EditorRefusalException>(() => CopyMaker.MakeCopy(dest, inst.Dest("copy2"), "c", "0.1.0")).Code);
    }

    [Fact]
    public void Make_copy_with_a_tampered_source_is_refused_and_writes_nothing()
    {
        using var inst = new Installed();
        inst.Mod.Put("myAssets/managerUnit.ini", "[core]\nname: managerUnit\nradius: 11\n");   // a locked file changed after editor-support.json was made
        string dest = inst.Dest("mymod");
        Assert.Equal("SOURCE_CHANGED", Assert.Throws<EditorRefusalException>(() => CopyMaker.MakeCopy(inst.Mod.Root, dest, "c", "0.1.0")).Code);
        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(Path.GetDirectoryName(dest)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(dest)!).Any());
    }

    [Fact]
    public void Cancelled_copy_removes_the_partial_folder()
    {
        using var inst = new Installed();
        string dest = inst.Dest("mymod");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var e = Assert.Throws<ApplyFailedException>(() => CopyMaker.MakeCopy(inst.Mod.Root, dest, "c", "0.1.0", cancel: cts.Token));
        Assert.True(e.Restored);
        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(Path.GetDirectoryName(dest)));   // the parent it created is removed again
    }

    sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T value) => a(value); }

    // ------------------------------------------------------------------------------------------------ Steam
    const string LibraryVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"177444829"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"647960"		"472979819"
        		}
        	}
        }
        """;

    [Fact]
    public void Library_folders_vdf_is_parsed()
    {
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLocator.LibrariesFromVdf(LibraryVdf));
        // the old flat format and comments
        Assert.Equal(new[] { @"E:\Games\Steam" }, SteamLocator.LibrariesFromVdf("// comment\n\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\"1\"\n\t\"1\"\t\"E:\\\\Games\\\\Steam\"\n}\n"));
        // damaged text gives what could be read, never throws
        Assert.Empty(SteamLocator.LibrariesFromVdf("\"libraryfolders\" { \"0\" { \"path\" "));
        Assert.Empty(SteamLocator.LibrariesFromVdf(""));
        Vdf.Parse("\"a\" { \"b\" \"c\" ", out var problem);
        Assert.NotNull(problem);
    }

    [Fact]
    public void App_manifest_installdir_is_read()
    {
        const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"647960\"\n\t\"name\"\t\t\"Rusted Warfare - RTS\"\n\t\"installdir\"\t\t\"Rusted Warfare\"\n}\n";
        Assert.Equal("Rusted Warfare", SteamLocator.InstallDirFromManifest(acf));
        Assert.Null(SteamLocator.InstallDirFromManifest("\"AppState\" { }"));
    }

    [Fact]
    public void Game_folders_and_mods_are_found_in_a_fake_library()
    {
        string root = Path.Combine(Path.GetTempPath(), "rwe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string lib = Path.Combine(root, "SteamLibrary");
            string game = Path.Combine(lib, "steamapps", "common", "RW Custom");
            Directory.CreateDirectory(Path.Combine(game, "assets", "units"));
            File.WriteAllBytes(Path.Combine(game, "game-lib.jar"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(lib, "steamapps", "appmanifest_647960.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"RW Custom\"\n}\n");
            Assert.Equal(new[] { game }, SteamLocator.GameFolders(new[] { lib }));
            Assert.Equal(Path.GetFullPath(lib), Path.GetFullPath(SteamLocator.LibraryOfGame(game)!));
            Assert.Null(SteamLocator.LibraryOfGame(Path.Combine(root, "elsewhere", "game")));
            Assert.False(SteamLocator.IsGameFolder(lib));

            // one Workshop item with editor support, one old item without, one local mod copy
            using var _ = new TempModAt(Path.Combine(lib, "steamapps", "workshop", "content", "647960", "3817316788"), "Depache's Mod Tribute", support: true, marker: false);
            using var __ = new TempModAt(Path.Combine(lib, "steamapps", "workshop", "content", "647960", "1111"), "Other mod", support: false, marker: false);
            using var ___ = new TempModAt(Path.Combine(game, "mods", "units", "DepacheTribute_Custom_A"), "[Custom A] Depache's Mod Tribute", support: true, marker: true);
            var mods = SteamLocator.FindMods(game, new[] { lib });
            Assert.Equal(3, mods.Count);
            var ws = mods.Single(x => x.WorkshopId == "3817316788");
            Assert.True(ws.CanBeSource);
            Assert.Equal(ModLocation.Workshop, ws.Location);
            Assert.Equal("Depache's Mod Tribute", ws.Title);
            var old = mods.Single(x => x.WorkshopId == "1111");
            Assert.False(old.CanBeSource);
            Assert.Contains("editor support", old.EditorSupportProblem);
            var copy = mods.Single(x => x.Location == ModLocation.GameModsFolder);
            Assert.True(copy.IsEditorCopy);
            Assert.False(copy.CanBeSource);
            Assert.Equal("A", copy.CopyName);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    /// <summary>A minimal mod folder at an exact path (for the locator test).</summary>
    sealed class TempModAt : IDisposable
    {
        public TempModAt(string dir, string title, bool support, bool marker)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mod-info.txt"), $"[mod]\ntitle: {title}\n");
            if (support)
            {
                using var t = new TestMod(withMarker: false);
                foreach (var f in Directory.EnumerateFiles(t.Root, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(dir, Path.GetRelativePath(t.Root, f));
                    if (Path.GetFileName(f) == "mod-info.txt") continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target);
                }
                if (marker) File.WriteAllText(Path.Combine(dir, EditorCopyMarker.FileName), EditorCopyMarker.Create("A", @"D:\src", EditorSupport.Load(dir), "0.1.0", T0).ToJson());
            }
        }
        public void Dispose() { }
    }

    [Fact]
    public void Workshop_item_state_is_read_from_preferences()
    {
        const string prefs = "x:1\nmodSettings:sw1878960341|sw1878960341|enabled,sw3817316788|sw3817316788|disabled,entals_DepacheEdit...|9769E7AD|enabled\nmodSettingsVersion:1\n";
        Assert.Equal(ModEnabledState.Disabled, GamePreferences.WorkshopItemState(prefs, "3817316788"));
        Assert.Equal(ModEnabledState.Enabled, GamePreferences.WorkshopItemState(prefs, "1878960341"));
        Assert.Equal(ModEnabledState.NotListed, GamePreferences.WorkshopItemState(prefs, "42"));
        Assert.Equal(ModEnabledState.Unknown, GamePreferences.WorkshopItemState("nothing here", "42"));
        Assert.Equal(ModEnabledState.Unknown, GamePreferences.WorkshopItemState(null, "42"));
        Assert.Equal(ModEnabledState.Enabled, GamePreferences.WorkshopItemState(prefs.Replace("\n", "\r\n").Replace("disabled", "enabled"), "3817316788"));
    }

    // ------------------------------------------------------------------------------------------------ mod-info
    [Fact]
    public void Mod_info_title_is_read_and_replaced_keeping_everything_else()
    {
        const string text = "[mod]\r\ntitle: Depache's Mod Tribute\r\ndescription: title: not this one\r\n\r\n[music]\r\ntitle: nor this\r\n";
        Assert.Equal("Depache's Mod Tribute", ModInfo.Title(text));
        Assert.Equal(text.Replace("title: Depache's Mod Tribute", "title: [Custom A] Depache's Mod Tribute"), ModInfo.WithTitle(text, "[Custom A] Depache's Mod Tribute"));
        Assert.Null(ModInfo.Title("[music]\ntitle: x\n"));
        Assert.Null(ModInfo.WithTitle("[mod]\ndescription: x\n", "t"));
        Assert.Throws<ArgumentException>(() => ModInfo.WithTitle(text, "a\nb"));
        Assert.Equal("x", ModInfo.Title("\uFEFF[mod]\nTitle : x\n"));
    }

    [RepoFact]
    public void Real_mod_info_title_reads()
    {
        Assert.Equal("Depache's Mod Tribute", ModInfo.Title(File.ReadAllText(Path.Combine(Repo.Src, "mod-info.txt"), Encoding.UTF8)));
        var c = SteamLocator.Describe(Repo.Src);
        Assert.True(c.HasEditorSupport);
        Assert.False(c.IsEditorCopy);
    }

    // ------------------------------------------------------------------------------------------------ app settings
    [Fact]
    public void App_settings_round_trip_and_a_damaged_file_gives_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rwe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string p = Path.Combine(dir, "app-settings.json");
            var s = AppSettings.Load(p);
            Assert.Null(s.CopyFolder);
            Assert.Null(s.LoadProblem);
            s.CopyFolder = @"C:\Users\x\AppData\Local\DepacheRwEditor\mymod";
            s.GameFolder = @"D:\SteamLibrary\steamapps\common\Rusted Warfare";
            s.Save(p);
            s.Save(p);   // second save replaces
            var back = AppSettings.Load(p);
            Assert.Equal(s.CopyFolder, back.CopyFolder);
            Assert.Equal(s.GameFolder, back.GameFolder);
            Assert.Equal(s.ToJson(), File.ReadAllText(p));
            File.WriteAllText(p, "{ broken");
            var bad = AppSettings.Load(p);
            Assert.NotNull(bad.LoadProblem);
            Assert.Null(bad.CopyFolder);
            File.WriteAllText(p, "{\"format\":1,\"copyFolder\":\"relative\\\\path\"}");
            Assert.Null(AppSettings.Load(p).CopyFolder);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
