using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RwEditor.Core.GameLog;

/// <summary>
/// Reads the game's logs and says, in plain words, what is wrong and whose edit it is (DESIGN_EDITOR 5.5). Every line format below was read from the
/// real files (91 recorded game runs, lastrun.log, crashes.txt, hs_err_pid*.log) and each rule has a test fed with those recordings. What a log cannot
/// show is not guessed: wrong behaviour in a match without an error line is outside what this can find (the report says so when it found nothing).
/// </summary>
public static class GameLogAnalyzer
{
    const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    static readonly Regex LoadingMod = new(@"^Loading mod at:(?<p>.+?) \(name:(?<n>.*)\)\s*$", Rx);
    static readonly Regex DisabledMod = new(@"^Disabled mod at:(?<p>.+?) \(name:(?<n>.*)\)\s*$", Rx);
    static readonly Regex DisablingDueToError = new(@"^Disabling mod due to error: (?<n>.*?) path:(?<p>.+)$", Rx);
    static readonly Regex AddingError = new(@"^Adding error for mod: (?<mod>.*?): Error loading unit: (?<unit>.+?) \(in mod (?<title>.*?)\): ?\n?(?<msg>.*)$", Rx);
    static readonly Regex AlertError = new(@"^(?:slick (?:queuing-|post-)?)?alert:Error loading mod '(?<mod>.*?)': Error loading unit: (?<unit>.+?) \(in mod (?<title>.*?)\): ?\n?(?<msg>.*)$", Rx);
    static readonly Regex GameWarning = new(@"^Warning \(on (?<rel>.+?) \(in mod (?<mod>.*?)\)\): (?<msg>.*)$", Rx);
    static readonly Regex Unexpected = new(@"^(?<path>.+?): Unexpected format on line:(?<n>\d+): '(?<text>.*)' in ini file\s*$", Rx);
    static readonly Regex SafeMode = new(@"^starting game in safe mode, numIncompleteLoadAttempts:(?<a>\d+) numLoadsSinceRunningGameOrNormalExit:(?<b>\d+)", Rx);
    static readonly Regex SafeModeWhy = new(@"Started game in safe mode due to (?<why>[^\n]+?)\. Mods have been disabled", Rx);
    static readonly Regex DoneLoading = new(@"^Done loading custom units\..* \(in: (?<ms>[0-9.]+)ms\)", Rx);
    static readonly Regex ModTable = new(@"^Mod: '(?<n>.*)' - Memory use:(?<mb>[0-9.]+) mb(?<rest>.*)$", Rx);
    static readonly Regex SectionKeyMsg = new(@"^\[(?<s>[^\]]+)\](?<k>[^\s:]+)(?<m>.*)$", Rx);
    static readonly Regex ModFolder = new(@"[\\/]SD[\\/]mods[\\/]units[\\/](?<f>[^\\/]+)[\\/](?<rel>.+)$", Rx);
    static readonly Regex UgSection = new(@"ug(?<n>\d\d)", Rx);
    /// <summary>Section names only the editor's generator writes (hiddenAction_ug01S, action_ug02On, hiddenAction_ugGate...).</summary>
    static readonly Regex GeneratedSection = new(@"^(?<k>hiddenAction|action)_ug(?:\d\d(?:On|Off|Msg|S|R)?|Init|Gate|Hide|Def)$", Rx);

    /// <summary>Lines the game writes in every run that mean nothing for a mod (each recognised exactly; counted, not reported).</summary>
    static readonly Regex[] Noise =
    {
        new(@"^FileLoader: Could not find asset:", Rx),
        new(@"^(?:slickToAndroidCodes|gdxToAndroidCodes|gdxToSlickCodes): Could not find keycode for:", Rx),
        new(@"^Setting missingPlaceHolder", Rx),
        new(@"^CommandController:Warning AI: \d+ gave an order to unit with canNotBeGivenOrdersByPlayer: ", Rx),
        new(@"^network:cannot send ", Rx),
        new(@"^convertTexturePath for: drawable:error_missingmap\.png", Rx),
    };

    public static bool IsOurs(string title) =>
        title.Contains("Depache's Mod Tribute", StringComparison.OrdinalIgnoreCase) || title.Contains("MajorExperimentals", StringComparison.OrdinalIgnoreCase);

    sealed class Acc
    {
        public string Code = "", Title = "", Explain = "", Source = "";
        public string? Fix, OriginText, File, Section, Key;
        public LogSeverity Severity;
        public LogOrigin Origin;
        public int Count;
        public DateTime? Time;
        public List<string> Sample = new();
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    public static LogReport Analyze(IEnumerable<LogSource> sources, LogContext? ctx = null)
    {
        ctx ??= new LogContext();
        var baseline = ctx.Baseline ?? ShippedLogBaseline.Embedded;
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var mods = new List<(string Name, string Path, bool Disabled, string? Why)>();
        var names = new List<string>();
        int noise = 0, total = 0, older = 0;
        bool? reached = null;
        double? loadMs = null;
        string? safeWhy = null;
        bool sawGameLog = false, sawModList = false, reachedAnywhere = false;

        foreach (var src in sources)
        {
            names.Add(src.Name);
            total += src.Text.Count(c => c == '\n') + 1;
            switch (src.Kind)
            {
                case LogKind.GameLog:
                    sawGameLog = true;
                    reached ??= false;
                    foreach (var e in GameLogParser.ParseGameLog(src.Text))
                    {
                        if (e.Text.StartsWith("--Now loading:init complete", StringComparison.Ordinal)) reachedAnywhere = true;
                        if (ctx.Since is DateTime since && e.Time is DateTime et && et < since) { older++; continue; }
                        Entry(e, src.Name);
                    }
                    break;
                case LogKind.Crashes:
                    foreach (var b in GameLogParser.ParseCrashes(src.Text))
                    {
                        if (ctx.Since is DateTime since && b.Time is DateTime bt && bt < since) { older++; continue; }
                        Crash(b, src.Name);
                    }
                    break;
                case LogKind.JvmCrash:
                    JvmCrash(src.Text, src.Name);
                    break;
            }
        }

        void Add(string code, string firstLine, Func<Acc> make, string rawSample, DateTime? time, bool keyByLine = true)
        {
            string k = code + "|" + (keyByLine ? ShippedLogBaseline.Key(code, firstLine) : "");
            if (!acc.TryGetValue(k, out var a)) { a = make(); a.Code = code; a.Time = time; acc[k] = a; }
            a.Count++;
            if (a.Sample.Count < 3) a.Sample.Add(rawSample.Length > 400 ? rawSample[..400] + "..." : rawSample);
        }

        void Entry(LogEntry e, string source)
        {
            string text = e.Text;
            string l1 = text.Split('\n')[0];

            if (Noise.Any(n => n.IsMatch(l1))) { noise++; return; }

            var m = LoadingMod.Match(l1);
            if (m.Success) { mods.Add((m.Groups["n"].Value, m.Groups["p"].Value, false, null)); return; }
            m = DisabledMod.Match(l1);
            if (m.Success) { mods.Add((m.Groups["n"].Value, m.Groups["p"].Value, true, "switched off (in the game's Mods menu, or a built-in mod that is off)")); return; }
            m = DisablingDueToError.Match(l1);
            if (m.Success)
            {
                string path = m.Groups["p"].Value.Trim();
                for (int i = 0; i < mods.Count; i++)
                    if (PathEquals(mods[i].Path, path) || mods[i].Name.StartsWith(m.Groups["n"].Value, StringComparison.Ordinal))
                        mods[i] = mods[i] with { Disabled = true, Why = "the game switched it off because it failed to load" };
                return;
            }
            m = ModTable.Match(l1);
            if (m.Success) { if (m.Groups["rest"].Value.Contains("(disabled)", StringComparison.Ordinal)) TableDisabled(m.Groups["n"].Value); return; }
            if (l1.StartsWith("Number of mods:", StringComparison.Ordinal)) sawModList = true;
            if (DoneLoading.Match(l1) is { Success: true } dm) { loadMs = double.Parse(dm.Groups["ms"].Value, CultureInfo.InvariantCulture); return; }
            if (l1.StartsWith("--Now loading:init complete", StringComparison.Ordinal)) { reached = true; return; }

            if (SafeModeWhy.Match(text) is { Success: true } sw) safeWhy = sw.Groups["why"].Value;
            m = SafeMode.Match(l1);
            if (m.Success)
            {
                Add("SAFE_MODE", "", () => new Acc
                {
                    Severity = LogSeverity.Error, Origin = LogOrigin.Engine, Source = source,
                    Title = "The game started in safe mode, so NO mods were loaded",
                    Explain = "After several start-ups that did not finish, the game turns every mod off for one start (it says so in a message box when it opens). Nothing in this run tells you anything about your copy.",
                    Fix = "Close the game normally (not with the task manager) and start it again. If it opens in safe mode again, the log above this run names the file that stopped the loading.",
                }, text, e.Time, keyByLine: false);
                return;
            }
            if (text.Contains("OutOfMemoryError", StringComparison.Ordinal) && !l1.StartsWith("Warning", StringComparison.Ordinal))
            {
                Add("OUT_OF_MEMORY", "", () => OutOfMemory(source, ctx), text, e.Time, keyByLine: false);
                return;
            }

            m = AddingError.Match(text);
            if (!m.Success) m = AlertError.Match(text);
            if (m.Success) { LoadError(m, e, source, l1, text, ctx, baseline, Add); return; }

            m = GameWarning.Match(l1);
            if (m.Success) { WarningLine(m, e, source, l1, ctx, baseline, Add); return; }
            m = Unexpected.Match(l1);
            if (m.Success) { UnexpectedLine(m, e, source, l1, ctx, baseline, Add); return; }
        }

        void TableDisabled(string title)
        {
            for (int i = 0; i < mods.Count; i++)
                if (mods[i].Name == title && !mods[i].Disabled) mods[i] = mods[i] with { Disabled = true, Why = "the game lists it as disabled" };
        }

        void Crash(CrashBlock b, string source)
        {
            string first = b.Body.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (b.Kind.Equals("Map Load Warning", StringComparison.OrdinalIgnoreCase))
            {
                var mm = Regex.Match(first, @"Could not find unit type of:(?<u>[^\s]+?)(?: at x:(?<x>[-0-9.]+), y:(?<y>[-0-9.]+))?$", Rx);
                if (mm.Success)
                {
                    string u = mm.Groups["u"].Value;
                    Add("MAP_MISSING_UNIT", u, () => new Acc
                    {
                        Severity = LogSeverity.Warning, Origin = LogOrigin.Unknown, OriginText = "a map or mission names it", Source = source,
                        Title = $"A map uses a unit that is not loaded: {u}",
                        Explain = $"A map placed a unit called '{u}' but no loaded mod defines it, so the game left it out. This happens when the mod that provides the unit is switched off or the unit was renamed.",
                        Fix = "Switch on the mod that defines that unit, or play a map that does not use it.", Key = u,
                    }, b.Kind + " (at " + b.Time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "): " + first, b.Time);
                    return;
                }
                Add("MAP_WARNING", first, () => new Acc
                {
                    Severity = LogSeverity.Warning, Origin = LogOrigin.Unknown, Source = source, Title = "The game warns about a map", Explain = first,
                }, first, b.Time);
                return;
            }
            if (first.Contains("OutOfMemoryError", StringComparison.Ordinal))
            {
                Add("OUT_OF_MEMORY", "", () => OutOfMemory(source, ctx), b.Kind + ": " + first, b.Time, keyByLine: false);
                return;
            }
            string cls = first.Split(':')[0].Trim();
            var frames = b.Body.Split('\n').Where(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal)).Take(3).Select(l => l.Trim());
            Add("GAME_CRASH", cls, () => new Acc
            {
                Severity = LogSeverity.Error, Origin = LogOrigin.Engine, Source = source, Title = $"The game crashed ({(cls.Length > 0 ? cls : b.Kind)})",
                Explain = $"crashes.txt records a '{b.Kind}' at {b.Time?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (game version {b.Version}): {first}\nFirst steps of the stack: {string.Join(" <- ", frames)}\nThe stack names the game's own classes, not a mod file, so it cannot point at one edit.",
                Fix = "If it started after a change, undo the last change (History > Undo) and test again.",
            }, b.Kind + ": " + first, b.Time);
        }

        void JvmCrash(string text, string source)
        {
            if (!text.Contains("A fatal error has been detected by the Java Runtime Environment", StringComparison.Ordinal)) return;
            var tm = Regex.Match(text, @"^Time: \w{3} (?<d>\w{3} +\d+ \d\d:\d\d:\d\d \d{4})", RegexOptions.Multiline | Rx);
            DateTime? when = tm.Success && DateTime.TryParseExact(Regex.Replace(tm.Groups["d"].Value, @" +", " "), "MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
            if (ctx.Since is DateTime since && when is DateTime w && w < since) { older++; return; }
            var ex = Regex.Match(text, @"^#\s+(?<n>[A-Z_]{4,})\s+\((?<c>0x[0-9a-fA-F]+)\)", RegexOptions.Multiline | Rx);
            var pf = Regex.Match(text, @"# Problematic frame:\s*\n#\s*(?<f>[^\n]+)", Rx);
            bool oom = text.Contains("# There is insufficient memory", StringComparison.Ordinal) || text.Contains("Out of Memory Error", StringComparison.Ordinal);
            string frame = pf.Success ? pf.Groups["f"].Value.Trim() : "unknown";
            string kind = ex.Success ? ex.Groups["n"].Value : oom ? "out of memory" : "unknown";
            Add("JVM_CRASH", kind + frame, () => new Acc
            {
                Severity = LogSeverity.Error, Origin = LogOrigin.Engine, Source = source, Title = $"The game's Java engine crashed ({kind})",
                Explain = $"The Java runtime wrote a crash report{(when != null ? " at " + when.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "")}. Problem frame: {frame}. "
                    + (oom ? "The report says the machine or the Java heap ran out of memory. " : "A frame in a native library (msvcrt, librocket...) means the crash was in the game's own engine code. ")
                    + "A crash report like this names no mod file, so it cannot point at one of your edits.",
                Fix = "Start the game again. If it crashes the same way every time, tell the editor's author which action causes it (with this report).",
            }, text.Split('\n').FirstOrDefault(l => l.Contains("EXCEPTION", StringComparison.Ordinal)) ?? "A fatal error has been detected", when);
        }

        // ---- the loaded mods and the double-copy rule
        var loaded = mods.Select(x => new LoadedMod(x.Name, x.Path, IsOurs(x.Name), x.Disabled, x.Why)).ToList();
        var copies = loaded.Where(x => x.IsTribute && !x.Disabled).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (copies.Count >= 2)
        {
            var a = new Acc
            {
                Code = "DOUBLE_COPY", Severity = LogSeverity.Error, Origin = LogOrigin.Engine, Source = names.FirstOrDefault() ?? "",
                Title = $"{copies.Count} copies of Depache's Mod Tribute are switched on at the same time",
                Explain = "Every unit is loaded once per copy, so units and buildings exist twice and the extra starting units are spawned once per copy. The copies the game loaded:\n  " + string.Join("\n  ", copies.Select(c => $"{c.Name}  ({c.Path})")),
                Fix = "Open the game's Mods menu and switch off every copy except the one you want (the Workshop one, your editor copy and any other local copy are all separate copies).",
                Count = copies.Count,
            };
            a.Sample.AddRange(copies.Select(c => "Loading mod at:" + c.Path + " (name:" + c.Name + ")"));
            acc["DOUBLE_COPY|"] = a;
        }
        if (sawModList && !loaded.Any(x => x.IsTribute) && !acc.ContainsKey("SAFE_MODE|"))
            acc["NO_COPY|"] = new Acc
            {
                Code = "NO_COPY_LOADED", Severity = LogSeverity.Warning, Origin = LogOrigin.Engine, Source = names.FirstOrDefault() ?? "", Count = 1,
                Title = "No copy of Depache's Mod Tribute was loaded in this run",
                Explain = "The game's log lists the mods it loaded and none of them is a copy of Depache's Mod Tribute, so this run says nothing about your edits.",
                Fix = "Open the game's Mods menu, switch your copy on, and run the test again.",
            };
        else if (sawModList && loaded.Any(x => x.IsTribute) && !loaded.Any(x => x.IsTribute && !x.Disabled) && !loaded.Any(x => x.IsTribute && x.DisabledWhy != null && x.DisabledWhy.Contains("failed", StringComparison.Ordinal)) && !acc.ContainsKey("SAFE_MODE|"))
            acc["NO_COPY|"] = new Acc
            {
                Code = "NO_COPY_LOADED", Severity = LogSeverity.Warning, Origin = LogOrigin.Engine, Source = names.FirstOrDefault() ?? "", Count = 1,
                Title = "Your copy of Depache's Mod Tribute is switched OFF in the game",
                Explain = "The game lists the copy but it is disabled, so it was not loaded and this run says nothing about your edits.",
                Fix = "Open the game's Mods menu, switch your copy on, and run the test again.",
            };
        if (safeWhy != null && acc.TryGetValue("SAFE_MODE|", out var sm)) sm.Explain += "\nThe game's own reason: " + safeWhy + ".";

        var findings = acc.Values.Select(a => new LogFinding(a.Code, a.Severity, a.Title, a.Explain, a.Fix, a.Origin, a.OriginText, a.File, a.Section, a.Key, a.Count, a.Time, a.Source, a.Sample))
            .OrderBy(f => Rank(f)).ThenBy(f => f.Time ?? DateTime.MinValue).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
        bool? init = sawGameLog ? (reached == true || reachedAnywhere) : null;
        return new LogReport
        {
            Sources = names, Mods = loaded, Findings = findings, NoiseLines = noise, TotalLines = total, ReachedInit = init, LoadMilliseconds = loadMs,
            OlderSkipped = older, Summary = Summary(findings, loaded, init, noise, older),
        };
    }

    static bool PathEquals(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    static int Rank(LogFinding f)
    {
        int sev = f.Severity switch { LogSeverity.Error => 0, LogSeverity.Warning => 20, _ => 40 };
        int org = f.Origin switch { LogOrigin.YourEdit => 0, LogOrigin.Generated => 1, LogOrigin.Unknown => 2, LogOrigin.Engine => 3, LogOrigin.OtherMod => 5, _ => 6 };
        int top = f.Code == "DOUBLE_COPY" ? -10 : f.Code == "SAFE_MODE" ? -5 : 0;
        return top + sev + org;
    }

    // ------------------------------------------------------------------------------------------------------------------------------ rules
    delegate void AddFn(string code, string firstLine, Func<Acc> make, string rawSample, DateTime? time, bool keyByLine = true);

    static void LoadError(Match m, LogEntry e, string source, string l1, string text, LogContext ctx, ShippedLogBaseline baseline, AddFn add)
    {
        string unit = m.Groups["unit"].Value.Trim(), title = m.Groups["title"].Value.Trim(), msg = m.Groups["msg"].Value.Trim();
        // the game repeats an error four times (log, alert, queue, post-alert): one problem. The first line used as the key is the message, not the prefix.
        string key = unit + "|" + msg.Split('\n')[0];
        bool ours = IsOurs(title);
        string rel = ResolveRel(unit, ctx);
        var sk = SectionKeyMsg.Match(msg.Split('\n')[0]);
        string? section = sk.Success ? sk.Groups["s"].Value : null, k2 = sk.Success ? sk.Groups["k"].Value : null;
        // the missing-unit message has no [section] prefix but names the section and the key it was found on
        if (section == null && Regex.Match(msg, @"used on:(?<k>\S+) in section:(?<s>\S+)", Rx) is { Success: true } um) { section = um.Groups["s"].Value; k2 = um.Groups["k"].Value; }
        var (t, ex, fix) = ExplainLoad(section, k2, sk.Success ? sk.Groups["m"].Value.Trim() : msg, msg, rel);
        var (origin, originText) = OriginOf(ours, title, rel, section, k2, "LOAD_ERROR|" + key, ctx, baseline, e);
        if (ours && origin == LogOrigin.Generated && section != null && !rel.StartsWith("editorGenerated/", StringComparison.OrdinalIgnoreCase))
            rel = GeneratedSection.Match(section).Groups["k"].Value == "hiddenAction" ? "editorGenerated/anchorGroups.ini" : "editorGenerated/selectorGroups.ini";
        add("LOAD_ERROR", key, () => new Acc
        {
            Severity = ours ? LogSeverity.Error : LogSeverity.Warning, Origin = origin, OriginText = originText, Source = source,
            Title = (ours ? "" : "Another mod failed to load: ") + t, Explain = ex + (ours ? "" : $"\nThis is in the mod \"{title}\", not in your copy. The game switches that mod off."),
            Fix = fix, File = rel, Section = section, Key = k2,
        }, text, e.Time);
    }

    static string ResolveRel(string unit, LogContext ctx)
    {
        string u = unit.Replace('\\', '/');
        if (u.Contains('/') || u.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return u;
        return ctx.UnitFiles != null && ctx.UnitFiles.TryGetValue(u, out var rel) ? rel : u;
    }

    static (string Title, string Explain, string? Fix) ExplainLoad(string? section, string? key, string rest, string raw, string rel)
    {
        string where = section != null ? $"[{section}]{key}" : rel;
        var m = Regex.Match(rest, @"cannot be longer than (?<n>\d+) characters", Rx);
        if (key == "id" && m.Success)
            return ($"An id is too long in [{section}]", $"The game only accepts ids up to {m.Groups["n"].Value} characters, and in practice 13: in this mod an id of exactly 15 characters was rejected although the message says 15. The file is {rel}.",
                $"Shorten the id of [{section}] in {rel} to 13 characters or fewer.");
        m = Regex.Match(raw, @"Could not find unit type:(?<u>.+?) used on:(?<k>.+?) in section:(?<s>.+)$", Rx);
        if (m.Success)
            return ("A unit that does not exist is used", $"{m.Groups["k"].Value.Trim()} in [{m.Groups["s"].Value.Trim()}] of {rel} names the unit '{m.Groups["u"].Value.Trim()}', and no loaded mod defines a unit with that name.",
                "Check the spelling of the unit name. For a start-unit group, remove or replace that unit on the Start units page. If the unit comes from another mod, that mod must be switched on.");
        m = Regex.Match(rest, @"IO Error: Failed to open: (?<p>.+?) \(in mod", Rx);
        if (m.Success)
            return ("A picture or sound file is missing", $"{where} in {rel} points at '{Path.GetFileName(m.Groups["p"].Value.Replace('\\', '/'))}', which the game could not open ({m.Groups["p"].Value.Trim()}).",
                "Put the file back where that line expects it, or correct the file name in the line.");
        return ($"The game rejected {where}", (section != null ? rest : raw).Trim() + $"\nThe file is {rel}.", null);
    }

    static void WarningLine(Match m, LogEntry e, string source, string l1, LogContext ctx, ShippedLogBaseline baseline, AddFn add)
    {
        string rel = m.Groups["rel"].Value.Trim(), mod = m.Groups["mod"].Value.Trim(), msg = m.Groups["msg"].Value.Trim();
        bool ours = IsOurs(mod);
        string title, ex; string? fix = null, section = null, key = null;
        var rk = Regex.Match(msg, @"^Repeated key \[(?<s>[^\]]+)\](?<k>.+)$", Rx);
        var ft = Regex.Match(msg, @"^(?<w>\[(?<s>[^\]]+)\](?<k>[^\s]+)) failed to find target:(?<t>.+)$", Rx);
        if (rk.Success)
        {
            section = rk.Groups["s"].Value; key = rk.Groups["k"].Value.Trim();
            title = "A key is written twice";
            ex = $"[{section}] in {rel} sets '{key}' more than once. The game warns and uses one of the values; if both lines are yours, check they say what you mean.";
            fix = "Delete the line you do not want (or change the stat once, on the unit's own page, which edits the one line).";
        }
        else if (ft.Success)
        {
            section = ft.Groups["s"].Value; key = ft.Groups["k"].Value;
            title = "A link points at a unit that does not exist";
            ex = $"{ft.Groups["w"].Value} in {rel} names '{ft.Groups["t"].Value.Trim()}', which is not a loaded unit.";
            fix = "Check the spelling, or switch on the mod that defines that unit.";
        }
        else { title = "The game warns about " + rel; ex = msg; }
        var (origin, originText) = OriginOf(ours, mod, rel, section, key, "WARNING|" + l1, ctx, baseline, e);
        add("WARNING", l1, () => new Acc
        {
            Severity = LogSeverity.Warning, Origin = origin, OriginText = originText, Source = source, Title = title, Explain = ex + (ours ? "" : $"\nThis is in the mod \"{mod}\", not in your copy."),
            Fix = fix, File = rel, Section = section, Key = key,
        }, l1, e.Time);
    }

    static void UnexpectedLine(Match m, LogEntry e, string source, string l1, LogContext ctx, ShippedLogBaseline baseline, AddFn add)
    {
        string path = m.Groups["path"].Value.Trim();
        var fm = ModFolder.Match(path);
        string rel = fm.Success ? fm.Groups["rel"].Value.Replace('\\', '/') : path;
        string folder = fm.Success ? fm.Groups["f"].Value : "";
        string text = m.Groups["text"].Value; if (text.Length > 70) text = text[..70] + "...";
        // the log names only the folder, not the mod title; a folder that is not a copy of this mod is "another mod"
        bool ours = folder.Length == 0
            ? baseline.Contains("UNEXPECTED_FORMAT", l1)   // a Workshop path: only a line the shipped mod is known to write counts as this mod
            : folder.Contains("Depache", StringComparison.OrdinalIgnoreCase) || folder.Contains("MajorExperimentals", StringComparison.OrdinalIgnoreCase);
        string who = folder.Length > 0 ? folder : Regex.Match(path, @"workshop[\\/]content[\\/]\d+[\\/](?<id>\d+)", Rx) is { Success: true } wm ? "Workshop item " + wm.Groups["id"].Value : "another mod";
        var (origin, originText) = OriginOf(ours, who, rel, null, null, "UNEXPECTED_FORMAT|" + l1, ctx, baseline, e);
        add("UNEXPECTED_FORMAT", l1, () => new Acc
        {
            Severity = LogSeverity.Warning, Origin = origin, OriginText = originText, Source = source, Title = "The game skipped a line it could not read",
            Explain = $"Line {m.Groups["n"].Value} of {rel} is not in the 'key: value' form ('{text}'), so the game ignores it. A line like this does no harm by itself.",
            Fix = "Only matters if you meant that line to do something: write it as 'key: value' or put # in front of it to make it a comment.", File = rel,
        }, l1, e.Time);
    }

    static Acc OutOfMemory(string source, LogContext ctx)
    {
        var risky = ctx.StatEdits.Where(x => x.Key is "life" or "speed" or "areaRadius" or "areaDamage").Select(x => $"{x.Unit}: {x.StatId} {x.OldText ?? "(none)"} -> {x.NewText}").ToList();
        return new Acc
        {
            Severity = LogSeverity.Error, Origin = LogOrigin.Engine, Source = source, Title = "The game ran out of memory",
            Explain = "The Java engine ran out of memory (OutOfMemoryError). The log cannot say which unit used it up. Values that make a projectile live longer, fly faster or hit a larger area can create very many objects."
                + (risky.Count > 0 ? "\nEdits of yours to such values (possible suspects, not proof):\n  " + string.Join("\n  ", risky.Take(12))
                    : ctx.HasChangeInfo ? "\nYou have no edits of those values in this copy." : ""),
            Fix = risky.Count > 0 ? "Undo the last change that raised a lifetime, speed or area value (History > Undo), then run the Test load again. Closing other programs also frees memory."
                : "Close other programs to free memory and start the game again. If it keeps happening, note what you did just before and tell the editor's author.",
        };
    }

    // ------------------------------------------------------------------------------------------------------------------------------ origin
    static (LogOrigin, string?) OriginOf(bool ours, string modTitle, string rel, string? section, string? key, string baselineCode, LogContext ctx, ShippedLogBaseline baseline, LogEntry e)
    {
        if (!ours) return (LogOrigin.OtherMod, $"mod \"{modTitle}\"");
        if (rel.StartsWith("editorGenerated/", StringComparison.OrdinalIgnoreCase) || (section != null && GeneratedSection.IsMatch(section)))
        {
            var ug = section != null ? UgSection.Match(section) : Match.Empty;
            string? grp = ug.Success && int.TryParse(ug.Groups["n"].Value, out int slot) ? ctx.SpawnGroups.FirstOrDefault(g => g.Slot == slot)?.Name : null;
            return (LogOrigin.Generated, "made by the editor from the Start units / Menu options pages" + (grp != null ? $" (group \"{grp}\")" : ""));
        }
        if (!ctx.HasChangeInfo && ctx.StatEdits.Count == 0 && ctx.OptionEdits.Count == 0) { /* fall through to the baseline check */ }
        var stat = ctx.StatEdits.Where(s => string.Equals(s.File, rel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stat.Count > 0)
        {
            var exact = section != null && key != null ? stat.Where(s => s.FileSection == section && s.Key == key).ToList() : new();
            if (exact.Count > 0) return (LogOrigin.YourEdit, "you changed this exact line: " + string.Join("; ", exact.Select(s => $"{s.Unit} {s.StatId} {s.OldText ?? "(none)"} -> {s.NewText}")));
            return (LogOrigin.YourEdit, $"you changed {stat.Count} value(s) in this file: " + string.Join("; ", stat.Take(4).Select(s => $"{s.Unit} {s.StatId} {s.OldText ?? "(none)"} -> {s.NewText}")) + (stat.Count > 4 ? "; ..." : ""));
        }
        var opt = ctx.OptionEdits.Where(o => string.Equals(o.File, rel, StringComparison.OrdinalIgnoreCase)).ToList();
        if (opt.Count > 0)
        {
            var exact = section != null && key != null ? opt.Where(o => o.Section == section && o.Key == key).ToList() : new();
            if (exact.Count > 0) return (LogOrigin.YourEdit, "you changed this exact line on the Menu options page (line " + exact[0].Line + ")");
            return (LogOrigin.YourEdit, $"you changed {opt.Count} line(s) of this file on the Menu options page");
        }
        int sep = baselineCode.IndexOf('|');
        string code = sep > 0 ? baselineCode[..sep] : baselineCode, first = sep > 0 ? baselineCode[(sep + 1)..] : "";
        if (code != "LOAD_ERROR" && baseline.Contains(code, first)) return (LogOrigin.Shipped, "also in the load log of the shipped mod, so not caused by an edit");
        if (!ctx.HasChangeInfo) return (LogOrigin.Unknown, "no copy was given, so your own changes could not be compared");
        return (LogOrigin.Unknown, code == "LOAD_ERROR" ? "this file is not in your changes; the shipped mod loads without this error" : "not in your changes and not in the shipped mod's own load log");
    }

    static string Summary(IReadOnlyList<LogFinding> f, IReadOnlyList<LoadedMod> mods, bool? init, int noise, int older)
    {
        var sb = new StringBuilder();
        int tribute = mods.Count(m => m.IsTribute && !m.Disabled), failed = mods.Count(m => m.Disabled && m.DisabledWhy != null && m.DisabledWhy.Contains("failed", StringComparison.Ordinal));
        if (mods.Count > 0)
        {
            sb.Append($"The game listed {mods.Count} mod(s): {mods.Count(m => !m.Disabled)} loaded");
            if (failed > 0) sb.Append($", {failed} failed to load and was switched off by the game");
            sb.Append(tribute > 0 ? $"; {tribute} loaded copy/copies of Depache's Mod Tribute. " : ". ");
        }
        if (init == true) sb.Append("The game finished loading. ");
        else if (init == false) sb.Append("The game did NOT reach the end of loading in this log. ");
        int errors = f.Count(x => x.Severity == LogSeverity.Error), warns = f.Count(x => x.Severity == LogSeverity.Warning && x.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod));
        if (errors == 0 && warns == 0 && older > 0 && mods.Count == 0)
            sb.Append($"Nothing newer than your copy was found: {older} older log entr{(older == 1 ? "y was" : "ies were")} left out. Tick \"Include entries from before my copy was made\" to read them anyway, or run a Test load for a fresh log.");
        else if (errors == 0 && warns == 0) sb.Append("Nothing wrong was found for this mod. A log shows only what the game complains about while loading; wrong behaviour in a match without an error line cannot be read from it.");
        else sb.Append($"{errors} problem(s) and {warns} warning(s) to look at.");
        return sb.ToString().Trim();
    }
}
