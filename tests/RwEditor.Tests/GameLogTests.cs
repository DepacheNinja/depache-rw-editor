using System.Text.RegularExpressions;
using RwEditor.Core.GameLog;
using RwEditor.Core.Settings;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E6: the game-log analyser, fed with lines copied from the REAL game logs (91 recorded runs, lastrun.log, crashes.txt, hs_err_pid*.log).</summary>
public class GameLogTests
{
    const string Title = "[Depache Edit] MajorExperimentals 更多实验单位 V271.7";
    const string Folder = "MajorExperimentals_DepacheEdit";

    static string Mod(string title = Title, string folder = Folder) =>
        $"2026-10-08 17:10:30.001: Loading mod at:/SD/mods/units/{folder} (name:{title})\n";

    /// <summary>The game writes a load error four times (log line, alert, queue, post-alert): that is how the real log looks.</summary>
    static string LoadError(string unit, string body, string title = Title, string modShort = "[Depache Edit] MajorExper") =>
        $"2026-10-08 17:10:43.320: Error while loading unit:/SD/mods/units/{Folder}/{unit}\n" +
        $"2026-10-08 17:10:43.322: Adding error for mod: {modShort}: Error loading unit: {unit} (in mod {title}): \n{body}\n" +
        $"2026-10-08 17:10:43.322: alert:Error loading mod '{modShort}': Error loading unit: {unit} (in mod {title}): \n{body}\n" +
        $"2026-10-08 17:10:43.322: slick queuing-alert:Error loading mod '{modShort}': Error loading unit: {unit} (in mod {title}): \n{body}\n" +
        $"2026-10-08 17:10:43.323: Disabling mod due to error: {modShort} path:D:\\SteamLibrary\\steamapps\\common\\Rusted Warfare\\mods\\units\\{Folder}\n" +
        $"2026-10-08 17:10:45.123: slick post-alert:Error loading mod '{modShort}': Error loading unit: {unit} (in mod {title}): \n{body}\n";

    static LogReport Run(string text, LogContext? ctx = null, LogKind kind = LogKind.GameLog) => GameLogAnalyzer.Analyze(new[] { new LogSource("test.log", text, kind) }, ctx ?? new LogContext());

    // ---------------------------------------------------------------------------------------------------------------- parser
    [Fact]
    public void An_error_detail_line_belongs_to_the_entry_before_it()
    {
        var e = GameLogParser.ParseGameLog("2026-10-08 17:10:43.322: Adding error for mod: X: Error loading unit: a.ini (in mod M): \n[core]id cannot be longer than 15 characters\n2026-10-08 17:10:43.400: next\n");
        Assert.Equal(2, e.Count);
        Assert.Contains("[core]id cannot", e[0].Text, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 10, 8, 17, 10, 43, 322), e[0].Time);
        Assert.Equal("next", e[1].Text);
    }

    [Fact]
    public void Crash_blocks_are_split_at_their_headers()
    {
        var b = GameLogParser.ParseCrashes("\nfatal (at 2025-09-25 20:49:39.935 - 1.15)\njava.lang.OutOfMemoryError: Java heap space\n\tat a.b(SourceFile:1)\n\n\nMap Load Warning (at 2026-09-16 06:52:59.694 - 1.15)\nx.f: Could not find unit type of:hazardSpawner at x:2190.0, y:2290.0\n\tat c\n");
        Assert.Equal(new[] { "fatal", "Map Load Warning" }, b.Select(x => x.Kind));
        Assert.Equal("1.15", b[0].Version);
        Assert.StartsWith("java.lang.OutOfMemoryError", b[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/x/crashes.txt", LogKind.Crashes)]
    [InlineData("hs_err_pid18444.log", LogKind.JvmCrash)]
    [InlineData("lastrun.log", LogKind.GameLog)]
    public void The_file_name_tells_the_layout(string path, LogKind kind) => Assert.Equal(kind, GameLogParser.KindOf(path));

    // ---------------------------------------------------------------------------------------------------------------- load errors
    [Fact]
    public void The_id_too_long_error_of_the_real_incomeMeOff_log_is_pinpointed_to_file_section_and_key()
    {
        var r = Run(Mod() + LoadError("startingOptions/gameOptionSelector.ini", "[action_incomeMeOff]id cannot be longer than 15 characters"));
        var f = Assert.Single(r.Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogSeverity.Error, f.Severity);
        Assert.Equal("An id is too long in [action_incomeMeOff]", f.Title);
        Assert.Equal(("startingOptions/gameOptionSelector.ini", "action_incomeMeOff", "id"), (f.File, f.Section, f.Key));
        Assert.Equal(4, f.Count);   // the game writes it four times: one problem
        Assert.Contains("13", f.Fix, StringComparison.Ordinal);
        Assert.Contains(r.Mods, m => m.IsTribute && m.Disabled && m.DisabledWhy!.Contains("failed", StringComparison.Ordinal));
        Assert.Contains("1 problem", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_unit_in_a_generated_section_is_the_editors_group_and_names_the_group()
    {
        string body = "Could not find unit type:expHoverTankNOPE used on:spawnUnits in section:hiddenAction_ug01S";
        var ctx = new LogContext { HasChangeInfo = true, SpawnGroups = new[] { new SpawnGroup { Slot = 1, Name = "Hover strike" } } };
        var f = Assert.Single(Run(Mod() + LoadError("extraStartAnchor", body), ctx).Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogOrigin.Generated, f.Origin);
        Assert.Contains("Hover strike", f.OriginText, StringComparison.Ordinal);
        Assert.Equal("editorGenerated/anchorGroups.ini", f.File);
        Assert.Contains("expHoverTankNOPE", f.Explanation, StringComparison.Ordinal);
        Assert.Contains("Start units page", f.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_in_a_file_with_a_stat_edit_names_the_exact_line_the_user_changed()
    {
        var edit = new StatEdit { Unit = "heavyAATank", File = "aa_tanks/heavy_AA_tank.ini", FileSection = "projectile_1", Key = "life", StatId = "projectile.life", OldText = "60", NewText = "-5", WriteKind = StatWriteKind.Patch };
        var ctx = new LogContext { HasChangeInfo = true, StatEdits = new[] { edit } };
        var f = Assert.Single(Run(Mod() + LoadError("aa_tanks/heavy_AA_tank.ini", "[projectile_1]life cannot be < 0"), ctx).Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogOrigin.YourEdit, f.Origin);
        Assert.Contains("this exact line", f.OriginText, StringComparison.Ordinal);
        Assert.Contains("60 -> -5", f.OriginText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_in_a_file_with_other_stat_edits_says_how_many_edits_are_in_that_file()
    {
        var edit = new StatEdit { Unit = "heavyAATank", File = "aa_tanks/heavy_AA_tank.ini", FileSection = "core", Key = "maxHp", StatId = "core.maxHp", OldText = "11500", NewText = "13000", WriteKind = StatWriteKind.Patch };
        var f = Assert.Single(Run(Mod() + LoadError("aa_tanks/heavy_AA_tank.ini", "[core]strictLevel cannot be < 0"), new LogContext { HasChangeInfo = true, StatEdits = new[] { edit } }).Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogOrigin.YourEdit, f.Origin);
        Assert.Contains("1 value(s) in this file", f.OriginText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_in_a_menu_option_file_is_traced_to_the_option_edit()
    {
        var edit = new OptionEdit { File = "startingOptions/gameOptionSelector.ini", Line = 262, Section = "action_baseIncomeOn", Key = "description", OldLine = "a", NewLine = "b" };
        var f = Assert.Single(Run(Mod() + LoadError("startingOptions/gameOptionSelector.ini", "[action_baseIncomeOn]description something"), new LogContext { HasChangeInfo = true, OptionEdits = new[] { edit } }).Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogOrigin.YourEdit, f.Origin);
        Assert.Contains("line 262", f.OriginText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_that_is_none_of_the_users_changes_says_so_only_when_the_copy_is_known()
    {
        string log = Mod() + LoadError("units/x.ini", "[core]strictLevel cannot be < 0");
        Assert.Contains("not in your changes", Assert.Single(Run(log, new LogContext { HasChangeInfo = true }).Findings, x => x.Code == "LOAD_ERROR").OriginText, StringComparison.Ordinal);
        Assert.Contains("no copy was given", Assert.Single(Run(log).Findings, x => x.Code == "LOAD_ERROR").OriginText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_picture_is_explained_by_its_file_name()
    {
        var f = Assert.Single(Run(Mod() + LoadError("donors/vte/laser/laserShldT2.ini", "[graphics]image_wreak: IO Error: Failed to open: \\SD\\mods\\units\\MajorExperimentals_DepacheEdit\\donors\\vte\\laser/dead.png (in mod X)")).Findings);
        Assert.Equal("A picture or sound file is missing", f.Title);
        Assert.Contains("dead.png", f.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_in_another_mod_is_a_warning_about_that_mod_not_a_problem_of_the_copy()
    {
        var r = Run(Mod() + "2026-10-08 17:10:31.000: Loading mod at:/SD/mods/units/Other (name:Some Other Mod)\n"
            + LoadError("units/o.ini", "[core]id cannot be longer than 15 characters", title: "Some Other Mod", modShort: "Some Other Mod"));
        var f = Assert.Single(r.Findings, x => x.Code == "LOAD_ERROR");
        Assert.Equal(LogSeverity.Warning, f.Severity);
        Assert.Equal(LogOrigin.OtherMod, f.Origin);
        Assert.StartsWith("Another mod failed to load", f.Title, StringComparison.Ordinal);
        Assert.Empty(r.Problems);
    }

    // ---------------------------------------------------------------------------------------------------------------- warnings
    [Fact]
    public void A_repeated_key_warning_is_read_with_its_section_and_key_and_labelled_shipped_when_the_baseline_knows_it()
    {
        string line = "2026-10-08 15:31:56.923: Warning (on units/Epic Units/Uber/Type-U Gunship/type_u_gunship.ini (in mod Moo's Defence Agency v3.1)): Repeated key [turret_1]turnSpeed\n";
        var f = Assert.Single(Run(line).Findings);
        Assert.Equal(("A key is written twice", "turret_1", "turnSpeed"), (f.Title, f.Section, f.Key));
        Assert.Equal(LogOrigin.OtherMod, f.Origin);

        string ours = "2026-10-08 15:31:56.923: Warning (on units/a.ini (in mod Depache's Mod Tribute)): Repeated key [core]energyMax\n";
        Assert.Equal(LogOrigin.Unknown, Assert.Single(Run(ours, new LogContext { HasChangeInfo = true, Baseline = new ShippedLogBaseline(Array.Empty<string>()) }).Findings).Origin);
        var known = new ShippedLogBaseline(new[] { ShippedLogBaseline.Key("WARNING", "Warning (on units/a.ini (in mod Depache's Mod Tribute)): Repeated key [core]energyMax") });
        var r = Run(ours + "2026-10-08 15:32:10.000: --Now loading:init complete\n", new LogContext { HasChangeInfo = true, Baseline = known });
        Assert.Equal(LogOrigin.Shipped, Assert.Single(r.Findings).Origin);
        Assert.True(r.Clean);
    }

    [Fact]
    public void A_link_to_a_missing_unit_warning_is_explained()
    {
        string line = "2026-10-08 15:31:57.746: Warning (on units/Structures/wall/wall.ini (in mod Depache's Mod Tribute)): [core]builtFrom_1_name failed to find target:adv_builder\n";
        var f = Assert.Single(Run(line, new LogContext { HasChangeInfo = true }).Findings);
        Assert.Equal("A link points at a unit that does not exist", f.Title);
        Assert.Contains("adv_builder", f.Explanation, StringComparison.Ordinal);
        Assert.Equal("builtFrom_1_name", f.Key);
    }

    [Fact]
    public void An_unreadable_ini_line_is_a_harmless_note_and_is_shipped_when_the_baseline_knows_it()
    {
        string a = "2026-10-12 21:07:27.679: \\SD\\mods\\units\\DepacheTribute_Custom_My_copy/myAssets/baseTorpedo.ini: Unexpected format on line:1: 'template_torpedoTurret' in ini file\n";
        string b = "2026-10-12 21:07:32.855: /SD/mods/units/MajorExperimentals_DepacheEdit/myAssets/baseTorpedo.ini: Unexpected format on line:1: 'template_torpedoTurret' in ini file\n";
        var baseline = new ShippedLogBaseline(new[] { ShippedLogBaseline.Key("UNEXPECTED_FORMAT", a.Substring(25).TrimEnd()) });
        var r = Run(a + b, new LogContext { HasChangeInfo = true, Baseline = baseline });
        var f = Assert.Single(r.Findings);
        Assert.Equal(2, f.Count);   // both path styles of both copies are the same note
        Assert.Equal(LogOrigin.Shipped, f.Origin);
        Assert.Equal("myAssets/baseTorpedo.ini", f.File);
    }

    [Fact]
    public void A_workshop_items_unreadable_lines_are_another_mod()
    {
        string line = "2026-10-08 15:31:50.000: D:\\SteamLibrary\\steamapps\\workshop\\content\\647960\\1533844074/mod-info.txt: Unexpected format on line:4: ' (6p) ' in ini file\n";
        var f = Assert.Single(Run(line).Findings);
        Assert.Equal(LogOrigin.OtherMod, f.Origin);
        Assert.Contains("Workshop item 1533844074", f.OriginText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- copies and modes
    [Fact]
    public void Two_copies_switched_on_are_the_top_finding()
    {
        string log = Mod("[Custom My copy] Depache's Mod Tribute", "DepacheTribute_Custom_My_copy") + Mod("[Custom smoke] Depache's Mod Tribute", Folder)
            + LoadError("units/x.ini", "[core]strictLevel cannot be < 0");
        var r = Run(log);
        Assert.Equal("DOUBLE_COPY", r.Findings[0].Code);
        Assert.Equal(LogSeverity.Error, r.Findings[0].Severity);
        Assert.Contains("DepacheTribute_Custom_My_copy", r.Findings[0].Explanation, StringComparison.Ordinal);
        Assert.Contains("switch off", r.Findings[0].Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_second_copy_is_not_a_double_copy()
    {
        string log = Mod("[Custom My copy] Depache's Mod Tribute", "DepacheTribute_Custom_My_copy")
            + "2026-10-08 17:10:31.000: Disabled mod at:/SD/mods/units/MajorExperimentals_DepacheEdit (name:[Custom smoke] Depache's Mod Tribute)\n";
        Assert.DoesNotContain(Run(log).Findings, f => f.Code == "DOUBLE_COPY");
    }

    [Fact]
    public void Safe_mode_is_reported_with_the_games_own_reason_and_hides_the_no_copy_warning()
    {
        string log = "2026-10-08 17:31:51.109: starting game in safe mode, numIncompleteLoadAttempts:0 numLoadsSinceRunningGameOrNormalExit:4\n"
            + "2026-10-08 17:31:52.823: slick queuing-messageBox:Started game in safe mode due to multiple loads without starting a game or exiting. Mods have been disabled.\n"
            + "2026-10-08 17:31:52.900: Number of mods:1\n";
        var r = Run(log);
        var f = Assert.Single(r.Findings);
        Assert.Equal("SAFE_MODE", f.Code);
        Assert.Contains("multiple loads without starting a game or exiting", f.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_any_copy_says_it_tested_nothing()
    {
        var f = Assert.Single(Run("2026-10-11 18:22:26.000: Number of mods:1\n2026-10-11 18:22:27.000: Disabled mod at:builtin_mods/mega_builders (name:Mega Builders)\n2026-10-11 18:22:40.000: --Now loading:init complete\n").Findings);
        Assert.Equal("NO_COPY_LOADED", f.Code);
        Assert.Equal(LogSeverity.Warning, f.Severity);
    }

    [Fact]
    public void A_switched_off_copy_is_named_as_switched_off()
    {
        string log = "2026-10-11 18:22:26.000: Number of mods:2\n2026-10-11 18:22:27.000: Disabled mod at:/SD/mods/units/DepacheTribute_Custom_My_copy (name:[Custom My copy] Depache's Mod Tribute)\n2026-10-11 18:22:40.000: --Now loading:init complete\n";
        Assert.Contains("switched OFF", Assert.Single(Run(log).Findings).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_load_reports_nothing_wrong_and_the_honest_limit()
    {
        var r = Run(Mod() + "2026-10-12 21:07:42.982: Done loading custom units. image cacheHits:1 image cacheMisses:2 (in: 16104.029ms)\n2026-10-12 21:07:45.505: --Now loading:init complete\n");
        Assert.True(r.Clean);
        Assert.True(r.ReachedInit);
        Assert.Equal(16104.029, r.LoadMilliseconds);
        Assert.Contains("cannot be read from it", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_never_reaches_init_is_not_clean()
    {
        var r = Run(Mod());
        Assert.False(r.ReachedInit);
        Assert.False(r.Clean);
        Assert.Contains("did NOT reach", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Routine_engine_lines_are_counted_but_never_reported()
    {
        string log = "2026-10-08 17:10:36.364: slickToAndroidCodes: Could not find keycode for: NUMLOCK, RWIN\n2026-10-08 17:10:36.379: FileLoader: Could not find asset:assets/translations/Strings_en.properties\n"
            + "2026-10-08 17:10:37.405: Setting missingPlaceHolder\n2026-10-08 17:47:23.323: CommandController:Warning AI: 0 gave an order to unit with canNotBeGivenOrdersByPlayer: ultraCannon\n"
            + "2026-10-08 17:29:31.874: network:cannot send sendSystemMessage:AI - Impossible has been wiped out (Team: A), we are not a server\n";
        var r = Run(log);
        Assert.Empty(r.Findings);
        Assert.Equal(5, r.NoiseLines);
    }

    [Fact]
    public void Entries_before_the_since_time_are_history_and_left_out()
    {
        string log = LoadError("units/x.ini", "[core]strictLevel cannot be < 0");
        Assert.Contains(Run(log, new LogContext { Since = new DateTime(2026, 10, 8) }).Findings, f => f.Code == "LOAD_ERROR");
        var later = Run(log, new LogContext { Since = new DateTime(2026, 10, 9) });
        Assert.DoesNotContain(later.Findings, f => f.Code == "LOAD_ERROR");
        Assert.True(later.OlderSkipped >= 4, "older entries counted: " + later.OlderSkipped);
        Assert.Contains("Nothing newer than your copy", later.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_load_stays_finished_even_when_its_lines_are_older_than_the_copy()
    {
        var r = Run(Mod() + "2026-10-08 17:10:45.505: --Now loading:init complete\n", new LogContext { Since = new DateTime(2026, 10, 9) });
        Assert.True(r.ReachedInit);
        Assert.DoesNotContain("did NOT reach", r.Summary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- crash files
    const string Crashes = "\nfatal (at 2025-09-25 20:49:39.935 - 1.15)\njava.lang.OutOfMemoryError: Java heap space\n\tat com.corrodinggames.rts.gameFramework.k.i.a(SourceFile:121)\n\n\n" +
        "Map Load Warning (at 2026-09-16 06:52:59.694 - 1.15)\ncom.corrodinggames.rts.game.b.f: Could not find unit type of:hazardSpawner at x:2190.0, y:2290.0\n\tat com.corrodinggames.rts.game.b.a.<init>(SourceFile:329)\n\n\n" +
        "Map Load Warning (at 2026-09-16 06:53:41.625 - 1.15)\ncom.corrodinggames.rts.game.b.f: Could not find unit type of:hazardSpawner at x:100.0, y:200.0\n\tat com.corrodinggames.rts.game.b.a.<init>(SourceFile:329)\n";

    [Fact]
    public void Crashes_txt_gives_out_of_memory_and_one_finding_per_missing_map_unit()
    {
        var edit = new StatEdit { Unit = "bomber", File = "u.ini", FileSection = "projectile_1", Key = "life", StatId = "projectile.life", OldText = "60", NewText = "90000", WriteKind = StatWriteKind.Patch };
        var r = Run(Crashes, new LogContext { HasChangeInfo = true, StatEdits = new[] { edit } }, LogKind.Crashes);
        var oom = Assert.Single(r.Findings, f => f.Code == "OUT_OF_MEMORY");
        Assert.Contains("bomber: projectile.life 60 -> 90000", oom.Explanation, StringComparison.Ordinal);
        Assert.Contains("possible suspects, not proof", oom.Explanation, StringComparison.Ordinal);
        var map = Assert.Single(r.Findings, f => f.Code == "MAP_MISSING_UNIT");
        Assert.Equal(2, map.Count);
        Assert.Equal("hazardSpawner", map.Key);
    }

    [Fact]
    public void Out_of_memory_without_such_edits_says_so()
    {
        Assert.Contains("no edits of those values", Assert.Single(Run(Crashes, new LogContext { HasChangeInfo = true }, LogKind.Crashes).Findings, f => f.Code == "OUT_OF_MEMORY").Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_crash_blocks_are_left_out_by_the_since_time()
    {
        var r = Run(Crashes, new LogContext { Since = new DateTime(2026, 9, 16, 6, 53, 0) }, LogKind.Crashes);
        Assert.DoesNotContain(r.Findings, f => f.Code == "OUT_OF_MEMORY");
        Assert.Equal(1, Assert.Single(r.Findings).Count);
    }

    [Fact]
    public void A_jvm_crash_report_names_the_native_frame_and_does_not_blame_a_mod_file()
    {
        string hs = "#\n# A fatal error has been detected by the Java Runtime Environment:\n#\n#  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffbf91ab7c0, pid=18444, tid=25100\n#\n" +
            "# Problematic frame:\n# C  [msvcrt.dll+0x7b7c0]\n#\n# No core dump will be written.\n\n---------------  S U M M A R Y ------------\n\nCommand Line: -Xmx10000M ...\n\n" +
            "Host: Intel(R) Core(TM) i9-14900K, 32 cores, 127G,  Windows 10 , 64 bit Build 26100\nTime: Tue Sep 15 00:24:39 2026 Central Daylight Time elapsed time: 7 seconds (0d 0h 0m 7s)\n";
        var f = Assert.Single(Run(hs, kind: LogKind.JvmCrash).Findings);
        Assert.Equal("JVM_CRASH", f.Code);
        Assert.Contains("EXCEPTION_ACCESS_VIOLATION", f.Title, StringComparison.Ordinal);
        Assert.Contains("msvcrt.dll", f.Explanation, StringComparison.Ordinal);
        Assert.Contains("names no mod file", f.Explanation, StringComparison.Ordinal);
        Assert.Empty(Run(hs, new LogContext { Since = new DateTime(2026, 10, 1) }, LogKind.JvmCrash).Findings);
    }

    [Fact]
    public void A_text_that_is_not_a_jvm_crash_gives_nothing() => Assert.Empty(Run("hello\nworld\n", kind: LogKind.JvmCrash).Findings);

    // ---------------------------------------------------------------------------------------------------------------- baseline
    [Fact]
    public void Baseline_keys_do_not_depend_on_the_copys_folder_the_slash_style_or_line_numbers()
    {
        string a = ShippedLogBaseline.Key("UNEXPECTED_FORMAT", "\\SD\\mods\\units\\DepacheTribute_Custom_My_copy/myAssets/baseTorpedo.ini: Unexpected format on line:1: 'x' in ini file");
        string b = ShippedLogBaseline.Key("UNEXPECTED_FORMAT", "/SD/mods/units/MajorExperimentals_DepacheEdit/myAssets/baseTorpedo.ini: Unexpected format on line:7: 'x' in ini file");
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_baseline_made_from_a_clean_report_labels_the_same_notes_as_shipped_in_a_later_log()
    {
        string note = "2026-10-12 21:07:32.855: /SD/mods/units/MajorExperimentals_DepacheEdit/myAssets/baseTorpedo.ini: Unexpected format on line:1: 'template_torpedoTurret' in ini file\n";
        var first = Run(Mod() + note + "2026-10-12 21:07:45.505: --Now loading:init complete\n", new LogContext { Baseline = new ShippedLogBaseline(Array.Empty<string>()) });
        var baseline = ShippedLogBaseline.FromReport(first);
        Assert.Equal(1, baseline.Count);
        var text = baseline.ToText("test");
        var later = Run(Mod("[Custom a] Depache's Mod Tribute", "DepacheTribute_Custom_a") + note.Replace("MajorExperimentals_DepacheEdit", "DepacheTribute_Custom_a", StringComparison.Ordinal), new LogContext { HasChangeInfo = true, Baseline = ShippedLogBaseline.Parse(text) });
        Assert.Equal(LogOrigin.Shipped, Assert.Single(later.Findings).Origin);
    }

    [Fact]
    public void The_embedded_baseline_holds_the_notes_of_the_shipped_mod()
    {
        var b = ShippedLogBaseline.Embedded;
        Assert.True(b.Count >= 4, "embedded baseline keys: " + b.Count);
        Assert.Contains(b.Keys, k => k.Contains("baseTorpedo.ini", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------- the real recordings
    static IEnumerable<string> CorpusLogs() =>
        Repo.Root == null ? Array.Empty<string>() : Directory.EnumerateFiles(Path.Combine(Repo.Root, "smoke-logs"), "*.out.txt").OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>All recorded game runs of this repository: the analyser reads every one without failing, finds a problem exactly where the raw log has an
    /// "Error loading unit" of this mod, and finds the double copy exactly where two copies were loaded.</summary>
    [RepoFact]
    public void Every_recorded_game_run_is_read_and_the_findings_match_what_the_raw_log_says()
    {
        int n = 0, withErrors = 0, doubles = 0;
        foreach (var path in CorpusLogs())
        {
            n++;
            string text = File.ReadAllText(path);
            var r = GameLogAnalyzer.Analyze(new[] { new LogSource(Path.GetFileName(path), text) }, new LogContext { HasChangeInfo = true });
            bool rawError = Regex.IsMatch(text, @"Adding error for mod: [^\n]*Error loading unit: [^\n]*\(in mod [^)]*(Depache's Mod Tribute|MajorExperimentals)", RegexOptions.CultureInvariant);
            bool found = r.Findings.Any(f => f.Code == "LOAD_ERROR" && f.Severity == LogSeverity.Error);
            Assert.True(rawError == found, $"{Path.GetFileName(path)}: raw log has a load error of this mod = {rawError}, analyser found one = {found}");
            if (found) withErrors++;
            int tribute = Regex.Matches(text, @"^\d{4}-\d\d-\d\d [\d:.]+: Loading mod at:\S+ \(name:[^)\n]*(Depache's Mod Tribute|MajorExperimentals)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
                .Select(m => Regex.Match(m.Value, @"Loading mod at:(\S+)").Groups[1].Value).Distinct().Count();
            bool dbl = r.Findings.Any(f => f.Code == "DOUBLE_COPY");
            bool disabledByError = r.Mods.Count(m => m.IsTribute && !m.Disabled) >= 2;
            Assert.True(dbl == (tribute >= 2 && disabledByError), $"{Path.GetFileName(path)}: copies loaded {tribute}, double-copy finding {dbl}");
            if (dbl) doubles++;
            Assert.All(r.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Title) || string.IsNullOrWhiteSpace(f.Explanation), f.Code));
        }
        Assert.True(n >= 80, "recorded runs found: " + n);
        Assert.True(withErrors >= 10, "runs with a load error: " + withErrors);
        Assert.True(doubles >= 3, "runs with two copies: " + doubles);
    }

    [RepoFact]
    public void The_recorded_incomeMeOff_run_and_the_recorded_missing_unit_run_give_their_exact_findings()
    {
        string logs = Path.Combine(Repo.Root!, "smoke-logs");
        var a = GameLogAnalyzer.Analyze(new[] { new LogSource("a", File.ReadAllText(Path.Combine(logs, "smoke-20261008-171035.out.txt"))) });
        Assert.Contains(a.Findings, f => f.Title == "An id is too long in [action_incomeMeOff]" && f.File == "startingOptions/gameOptionSelector.ini");
        var b = GameLogAnalyzer.Analyze(new[] { new LogSource("b", File.ReadAllText(Path.Combine(logs, "smoke-20261012-015035.out.txt"))) },
            new LogContext { HasChangeInfo = true, SpawnGroups = new[] { new SpawnGroup { Slot = 1, Name = "Hover strike" } } });
        var f2 = Assert.Single(b.Findings, f => f.Code == "LOAD_ERROR");
        Assert.Equal(LogOrigin.Generated, f2.Origin);
        Assert.Contains("expHoverTankNOPE", f2.Explanation, StringComparison.Ordinal);
    }

    [RepoFact]
    public void A_recorded_clean_run_of_the_shipped_mod_is_clean_against_the_embedded_baseline()
    {
        string text = File.ReadAllText(Path.Combine(Repo.Root!, "smoke-logs", "smoke-20261012-072051.out.txt"));
        var r = GameLogAnalyzer.Analyze(new[] { new LogSource("clean", text) }, new LogContext { HasChangeInfo = true });
        Assert.True(r.Clean, string.Join("; ", r.Findings.Select(f => f.Title)));
        Assert.True(r.ReachedInit);
        Assert.Contains(r.Findings, f => f.Origin == LogOrigin.Shipped);
    }
}
