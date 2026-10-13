using System.Text;
using RwEditor.Core.Apply;
using RwEditor.Core.GameLog;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E6: the Test load runner (with a stand-in game), the preferences reader and the store of the last result.</summary>
public sealed class TestLoadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "rwe-testload-" + Guid.NewGuid().ToString("N"));
    string Game => Path.Combine(_dir, "game");

    public TestLoadTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "jvm64", "bin"));
        File.WriteAllText(Path.Combine(Game, "jvm64", "bin", "java.exe"), "x");
        File.WriteAllText(Path.Combine(Game, "game-lib.jar"), "x");
        File.WriteAllText(Path.Combine(Game, "preferences.ini"), PrefsText(enabled: true), new UTF8Encoding(false));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    const string Folder = "DepacheTribute_Custom_My_copy";

    static string PrefsText(bool enabled) =>
        "[settings]\nlanguage:en\nmodSettings:sw1878960341|sw1878960341|enabled,entals_DepacheEdit...|9769E7ADA4265A94E0605E7B0DE80BCB267A7FC6CEBB80DDC41E57CDE659B348|disabled,"
        + $"te_Custom_My_copy...|F75E08DA62FE07776A84AEDD411CE72E1E8B7C5DCBDC20D9E6188B32ADE8F6E7|{(enabled ? "enabled" : "disabled")},sw162|sw162|enabled\nmodSettingsVersion:1\n";

    sealed class FakeGame(IReadOnlyList<string> lines, Action<string> onLine, bool exitWhenDone, bool ignoreClose) : IRunningGame
    {
        volatile bool _exited;
        public bool Closed, Killed;
        public bool HasExited => _exited;
        public void CloseGracefully() { Closed = true; if (!ignoreClose) _exited = true; }
        public void Kill() { Killed = true; _exited = true; }
        public void Begin() => Task.Run(() =>
        {
            foreach (var l in lines) { onLine(l); Thread.Sleep(2); }
            if (exitWhenDone) _exited = true;
        });
    }

    sealed class FakeLauncher(IReadOnlyList<string> lines, bool exitWhenDone = false, bool ignoreClose = false, Action? whileRunning = null) : IGameLauncher
    {
        public FakeGame? Game;
        public IRunningGame Start(string gameFolder, string heap, Action<string> onLine)
        {
            Game = new FakeGame(lines, onLine, exitWhenDone, ignoreClose);
            whileRunning?.Invoke();
            Game.Begin();
            return Game;
        }
    }

    TestLoadOptions Opt(IGameLauncher l, int timeout = 5, bool running = false, string? copy = Folder, string? save = null) => new()
    {
        GameFolder = Game, CopyFolderName = copy, Launcher = l, TimeoutSeconds = timeout, WaitAfterInitSeconds = 0, Poll = TimeSpan.FromMilliseconds(20),
        AfterErrorWait = TimeSpan.FromMilliseconds(80), IsGameRunning = () => running, SaveFolder = save, Now = () => new DateTime(2026, 10, 12, 21, 7, 0),
    };

    static readonly string[] CleanLines =
    {
        "2026-10-12 21:07:27.156: Loading mod at:/SD/mods/units/DepacheTribute_Custom_My_copy (name:[Custom My copy] Depache's Mod Tribute)",
        "2026-10-12 21:07:42.982: Done loading custom units. image cacheHits:1 image cacheMisses:2 (in: 16104.029ms)",
        "2026-10-12 21:07:45.505: --Now loading:init complete",
    };

    // ---------------------------------------------------------------------------------------------------------------- the run
    [Fact]
    public void A_clean_load_finishes_at_init_complete_closes_the_game_gently_and_saves_the_log_once()
    {
        var l = new FakeLauncher(CleanLines);
        var r = GameTestLoad.Run(Opt(l, save: Path.Combine(_dir, "logs")));
        Assert.Equal(TestLoadOutcome.Finished, r.Outcome);
        Assert.True(r.ReachedInit);
        Assert.True(l.Game!.Closed);
        Assert.False(l.Game.Killed);
        Assert.Contains("init complete", r.LogText, StringComparison.Ordinal);
        Assert.Equal(CleanLines.Length, r.LogText.TrimEnd().Split('\n').Length);
        Assert.NotNull(r.SavedPath);
        Assert.Equal(r.LogText, File.ReadAllText(r.SavedPath!));
        Assert.EndsWith("testload-20261012-210700.log", r.SavedPath, StringComparison.Ordinal);
        var rep = GameLogAnalyzer.Analyze(new[] { new LogSource("t", r.LogText) });
        Assert.True(rep.Clean);
    }

    [Fact]
    public void A_load_error_ends_the_test_after_the_rest_of_its_text_arrived()
    {
        var lines = new[]
        {
            "2026-10-08 17:10:43.322: Adding error for mod: [Depache Edit] MajorExper: Error loading unit: startingOptions/gameOptionSelector.ini (in mod [Depache Edit] MajorExperimentals V271.7): ",
            "[action_incomeMeOff]id cannot be longer than 15 characters",
            "2026-10-08 17:10:43.323: Disabling mod due to error: [Depache Edit] MajorExper path:D:\\x\\MajorExperimentals_DepacheEdit",
        };
        var l = new FakeLauncher(lines);
        var r = GameTestLoad.Run(Opt(l));
        Assert.Equal(TestLoadOutcome.Finished, r.Outcome);
        Assert.False(r.ReachedInit);
        Assert.Contains("[action_incomeMeOff]id cannot", r.LogText, StringComparison.Ordinal);
        Assert.Contains("Disabling mod due to error", r.LogText, StringComparison.Ordinal);
        Assert.Contains(GameLogAnalyzer.Analyze(new[] { new LogSource("t", r.LogText) }).Findings, f => f.Code == "LOAD_ERROR");
    }

    [Fact]
    public void A_game_that_never_finishes_times_out_and_is_closed_gently_first()
    {
        var l = new FakeLauncher(new[] { "2026-10-12 21:07:27.156: Loading mod at:/SD/mods/units/x (name:y)" });
        var r = GameTestLoad.Run(Opt(l, timeout: 1));
        Assert.Equal(TestLoadOutcome.TimedOut, r.Outcome);
        Assert.Contains("within 1 s", r.Reason, StringComparison.Ordinal);
        Assert.True(l.Game!.Closed);
        Assert.False(l.Game.Killed);
    }

    [Fact]
    public void A_game_that_ignores_the_close_request_is_killed_as_the_last_resort()
    {
        var l = new FakeLauncher(CleanLines, ignoreClose: true);
        var r = GameTestLoad.Run(Opt(l));
        Assert.True(l.Game!.Closed);
        Assert.True(l.Game.Killed);
        Assert.True(r.ReachedInit);
    }

    [Fact]
    public void A_game_that_closes_by_itself_before_finishing_is_reported()
    {
        var r = GameTestLoad.Run(Opt(new FakeLauncher(new[] { "2026-10-12 21:07:27.156: starting" }, exitWhenDone: true)));
        Assert.Equal(TestLoadOutcome.GameExited, r.Outcome);
        Assert.False(r.ReachedInit);
        Assert.Contains("closed by itself", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_stops_the_test_and_closes_the_game()
    {
        using var cts = new CancellationTokenSource();
        var l = new FakeLauncher(new[] { "2026-10-12 21:07:27.156: starting" });
        cts.CancelAfter(100);
        var r = GameTestLoad.Run(Opt(l, timeout: 30), cancel: cts.Token);
        Assert.Equal(TestLoadOutcome.Cancelled, r.Outcome);
        Assert.True(l.Game!.Closed);
    }

    [Fact]
    public void The_game_rewriting_preferences_ini_is_undone_byte_for_byte()
    {
        string prefs = Path.Combine(Game, "preferences.ini");
        byte[] before = File.ReadAllBytes(prefs);
        var l = new FakeLauncher(CleanLines, whileRunning: () => File.WriteAllText(prefs, "[settings]\nmodSettings:sw1|sw1|disabled\n"));
        var r = GameTestLoad.Run(Opt(l));
        Assert.True(r.ReachedInit);
        Assert.Equal(before, File.ReadAllBytes(prefs));
    }

    // ---------------------------------------------------------------------------------------------------------------- refusals
    [Fact]
    public void A_folder_that_is_not_the_game_is_refused()
    {
        var o = new TestLoadOptions { GameFolder = _dir, Launcher = new FakeLauncher(CleanLines), IsGameRunning = () => false };
        var r = GameTestLoad.Run(o);
        Assert.Equal(TestLoadOutcome.Refused, r.Outcome);
        Assert.Contains("does not look like the Rusted Warfare folder", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_game_is_never_touched()
    {
        var l = new FakeLauncher(CleanLines);
        var r = GameTestLoad.Run(Opt(l, running: true));
        Assert.Equal(TestLoadOutcome.Refused, r.Outcome);
        Assert.Contains("already running", r.Reason, StringComparison.Ordinal);
        Assert.Null(l.Game);
    }

    [Fact]
    public void A_copy_that_is_switched_off_is_refused_and_the_game_settings_are_not_changed()
    {
        string prefs = Path.Combine(Game, "preferences.ini");
        File.WriteAllText(prefs, PrefsText(enabled: false), new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(prefs);
        var l = new FakeLauncher(CleanLines);
        var r = GameTestLoad.Run(Opt(l));
        Assert.Equal(TestLoadOutcome.Refused, r.Outcome);
        Assert.Contains("switched OFF", r.Reason, StringComparison.Ordinal);
        Assert.Contains("never changes the game's settings", r.Reason, StringComparison.Ordinal);
        Assert.Null(l.Game);
        Assert.Equal(before, File.ReadAllBytes(prefs));
    }

    [Fact]
    public void A_copy_the_game_has_not_seen_yet_is_tested_with_a_note()
    {
        var r = GameTestLoad.Run(Opt(new FakeLauncher(CleanLines), copy: "DepacheTribute_Brand_new"));
        Assert.Equal(TestLoadOutcome.Finished, r.Outcome);
        Assert.Contains("no switch for this copy yet", r.PreferencesNote, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- preferences
    [Theory]
    [InlineData("DepacheTribute_Custom_My_copy", ModSwitch.Enabled)]
    [InlineData("MajorExperimentals_DepacheEdit", ModSwitch.Disabled)]
    [InlineData("sw1878960341", ModSwitch.Enabled)]
    [InlineData("Something_Else", ModSwitch.Unknown)]
    public void A_mods_switch_is_found_by_its_folder_name_as_the_game_stores_it(string folder, ModSwitch expected) =>
        Assert.Equal(expected, GamePreferences.StateOf(PrefsText(true), folder));

    [Fact]
    public void Preferences_without_a_mod_list_say_unknown() => Assert.Equal(ModSwitch.Unknown, GamePreferences.StateOf("[settings]\nlanguage:en\n", Folder));

    /// <summary>Reads the real preferences.ini of a real game folder named by the environment variable RW_GAME_DIR (skipped when it is not set).</summary>
    [Fact]
    public void The_real_preferences_file_format_is_understood_when_a_game_folder_is_given()
    {
        string? game = Environment.GetEnvironmentVariable("RW_GAME_DIR");
        string real = game == null ? "" : Path.Combine(game, "preferences.ini");
        if (!File.Exists(real)) return;
        string text = File.ReadAllText(real);
        Assert.Contains("modSettings:", text, StringComparison.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(game!, "mods", "units")))
            Assert.True(Enum.IsDefined(GamePreferences.StateOf(text, Path.GetFileName(dir))));
    }

    // ---------------------------------------------------------------------------------------------------------------- the store
    [Fact]
    public void The_last_result_is_remembered_per_copy_and_a_damaged_file_means_never_tested()
    {
        string dir = Path.Combine(_dir, "store");
        Assert.Null(TestLoadStore.Load("abc", dir));
        var rec = new TestLoadRecord(new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc), true, true, 0, 0, "Nothing wrong was found", @"C:\logs\x.log");
        TestLoadStore.Save("abc", rec, dir);
        Assert.Equal(rec, TestLoadStore.Load("abc", dir));
        Assert.Null(TestLoadStore.Load("other", dir));
        File.WriteAllText(Path.Combine(dir, "abc.json"), "{ not json");
        Assert.Null(TestLoadStore.Load("abc", dir));
    }

    [Fact]
    public void A_record_is_clean_only_when_the_game_finished_and_nothing_touches_the_mod()
    {
        var clean = GameLogAnalyzer.Analyze(new[] { new LogSource("t", string.Join("\n", CleanLines)) });
        Assert.True(TestLoadStore.RecordOf(clean, null, DateTime.UtcNow).Clean);
        var notFinished = GameLogAnalyzer.Analyze(new[] { new LogSource("t", CleanLines[0]) });
        Assert.False(TestLoadStore.RecordOf(notFinished, null, DateTime.UtcNow).Clean);
        var bad = GameLogAnalyzer.Analyze(new[] { new LogSource("t", CleanLines[0] + "\n2026-10-08 17:10:43.322: Adding error for mod: X: Error loading unit: a.ini (in mod [Custom My copy] Depache's Mod Tribute): \n[core]id cannot be longer than 15 characters\n" + CleanLines[2]) });
        var rb = TestLoadStore.RecordOf(bad, null, DateTime.UtcNow);
        Assert.False(rb.Clean);
        Assert.Equal(1, rb.Problems);
    }

    [Fact]
    public void Changes_since_the_last_clean_test_are_the_suspects_and_undone_ones_are_left_out()
    {
        HistoryEntry E(string kind, int minute, bool change = true, bool undone = false) =>
            new("p" + minute, kind, new DateTime(2026, 10, 12, 20, minute, 0, DateTimeKind.Utc), "summary " + minute, Array.Empty<string>(), change, undone, null);
        var h = new[] { E("apply", 1), E("stat-edit", 10), E("stat-edit", 20, undone: true), E("undo-safety", 25, change: false), E("apply", 30) };
        Assert.Equal(new[] { "summary 10", "summary 30" }, TestLoadStore.ChangesSince(h, new DateTime(2026, 10, 12, 20, 5, 0, DateTimeKind.Utc)).Select(x => x.Summary));
        Assert.Equal(3, TestLoadStore.ChangesSince(h, null).Count);
    }
}
