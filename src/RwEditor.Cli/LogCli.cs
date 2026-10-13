using System.Globalization;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.GameLog;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;

/// <summary>E6 commands: read the game's logs with the same analyser the app uses.</summary>
static class LogCli
{
    static List<LogSource> Sources(string list) =>
        list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new LogSource(p, File.ReadAllText(p, System.Text.Encoding.UTF8), GameLogParser.KindOf(p))).ToList();

    static LogContext ContextOf(string root, DateTime? since)
    {
        var settings = GroupsPipeline.LoadCurrent(root);
        var mc = ModContext.Build(ModSnapshot.Load(root), EditorSupport.Load(root));
        return LogContext.From(settings, mc.Units.Where(u => u.Loadable).GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.First().RelPath), since);
    }

    public static int TestLoad(Opts o)
    {
        o.Only("mod", "game", "timeout", "backup-dir", "allow-git-tree");
        string root = FolderGuard.Check(o.Req("mod"), o.Flag("allow-git-tree"));
        var marker = EditorCopyMarker.Load(root);
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        string? game = o.Get("game");
        if (game == null)
        {
            var units = Directory.GetParent(root); var mods = units?.Parent;
            if (units?.Name == "units" && mods?.Name == "mods" && mods.Parent != null) game = mods.Parent.FullName;
            else throw new UsageException("the copy is not inside a game's mods/units folder, so --game <Rusted Warfare folder> is required");
        }
        var opts = new TestLoadOptions
        {
            GameFolder = game, CopyFolderName = folder, SaveFolder = Path.Combine(TestLoadStore.DefaultFolder, "logs"),
            TimeoutSeconds = o.Get("timeout") is string t ? int.Parse(t, CultureInfo.InvariantCulture) : 240,
        };
        Console.WriteLine("Test load: starting the game with your switched-on mods (this takes about a minute)...");
        var res = GameTestLoad.Run(opts, new Progress<string>(m => Console.WriteLine("  " + m)));
        if (res.Outcome == TestLoadOutcome.Refused) { Console.WriteLine("NOT RUN: " + res.Reason); return 3; }
        if (res.PreferencesNote != null) Console.WriteLine("note: " + res.PreferencesNote);
        Console.WriteLine($"finished: {res.Outcome} in {res.Elapsed.TotalSeconds:0} s; log {res.SavedPath ?? "(not saved)"}");
        if (res.Reason != null) Console.WriteLine(res.Reason);
        var report = GameLogAnalyzer.Analyze(new[] { new LogSource("Test load", res.LogText) }, ContextOf(root, marker.CreatedUtc.ToLocalTime()));
        Print(report, all: false);
        var rec = TestLoadStore.RecordOf(report, res.SavedPath, DateTime.UtcNow);
        var before = TestLoadStore.Load(marker.CopyId);
        TestLoadStore.Save(marker.CopyId, rec);
        if (!rec.Clean)
        {
            var since = before is { Clean: true } ? before.Utc : (DateTime?)null;
            var sus = TestLoadStore.ChangesSince(History.List(new ApplyOptions { BackupRoot = o.Get("backup-dir") }.EffectiveBackupRoot, root), since);
            if (sus.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Changes made {(since != null ? "since the last clean test" : "to this copy")} (suspects when the cause is not named above):");
                foreach (var h in sus) Console.WriteLine($"  {h.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {h.KindText}: {h.Summary}");
            }
        }
        Console.WriteLine();
        Console.WriteLine(rec.Clean ? "TEST LOAD CLEAN" : "TEST LOAD: PROBLEMS FOUND");
        return rec.Clean ? 0 : 2;
    }

    public static int Analyze(Opts o)
    {
        o.Only("log", "mod", "since", "all", "baseline");
        var sources = Sources(o.Req("log"));
        DateTime? since = o.Get("since") is string s ? DateTime.ParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None) : null;
        LogContext ctx = new() { Since = since };
        if (o.Get("mod") is string mod)
        {
            string root = FolderGuard.Check(mod, allowGitTree: true);
            var settings = GroupsPipeline.LoadCurrent(root);
            var support = EditorSupport.Load(root);
            var mc = ModContext.Build(ModSnapshot.Load(root), support);
            ctx = LogContext.From(settings, mc.Units.Where(u => u.Loadable).GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.First().RelPath), since);
        }
        if (o.Get("baseline") is string bp) ctx = new LogContext { StatEdits = ctx.StatEdits, OptionEdits = ctx.OptionEdits, SpawnGroups = ctx.SpawnGroups, UnitFiles = ctx.UnitFiles, Since = ctx.Since, Baseline = ShippedLogBaseline.Parse(File.ReadAllText(bp)) };
        var r = GameLogAnalyzer.Analyze(sources, ctx);
        Print(r, o.Flag("all"));
        return r.Problems.Any() ? 2 : 0;
    }

    public static void Print(LogReport r, bool all)
    {
        Console.WriteLine(r.Summary);
        Console.WriteLine($"sources: {string.Join(", ", r.Sources)}; {r.TotalLines} lines, {r.NoiseLines} routine lines ignored");
        foreach (var m in r.Mods) Console.WriteLine($"  mod: {m.Name}{(m.IsTribute ? "  [Tribute copy]" : "")}{(m.Disabled ? "  (" + m.DisabledWhy + ")" : "")}  {m.Path}");
        foreach (var f in r.Findings.Where(f => all || f.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod) || f.Severity == LogSeverity.Error))
        {
            Console.WriteLine();
            Console.WriteLine($"[{f.SeverityText}] {f.Title}{(f.Count > 1 ? $" (x{f.Count})" : "")}");
            if (f.OriginText != null) Console.WriteLine($"  whose: {f.Origin} - {f.OriginText}");
            Console.WriteLine("  " + f.Explanation.Replace("\n", "\n  "));
            if (f.Fix != null) Console.WriteLine("  what to do: " + f.Fix);
            if (f.File != null) Console.WriteLine($"  where: {f.File}{(f.Section != null ? " [" + f.Section + "]" : "")}{(f.Key != null ? " " + f.Key : "")}");
        }
        int hidden = r.Findings.Count(f => !(all || f.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod) || f.Severity == LogSeverity.Error));
        if (hidden > 0) Console.WriteLine($"\n({hidden} normal note(s) from the shipped mod or other mods hidden; use --all)");
    }

    public static int Baseline(Opts o)
    {
        o.Only("log", "out");
        var r = GameLogAnalyzer.Analyze(Sources(o.Req("log")), new LogContext { Baseline = new ShippedLogBaseline(Array.Empty<string>()) });
        if (r.ReachedInit != true || r.Problems.Any() || r.Mods.Count(m => m.IsTribute && !m.Disabled) != 1)
            throw new UsageException("the log is not a clean load of the shipped mod (it must reach 'init complete', load exactly one copy of the mod and show no problem); a baseline must come from such a run");
        var b = ShippedLogBaseline.FromReport(r);
        File.WriteAllText(o.Req("out"), b.ToText("Notes the game writes for the SHIPPED mod in a clean load (made by 'rwedit log-baseline').\nKeys are code|first line with the mod folder, numbers and slashes made neutral. Regenerate when the mod changes."), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{b.Count} key(s) written to {o.Req("out")}");
        return 0;
    }
}
