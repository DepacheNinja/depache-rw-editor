using RwEditor.Core.Settings;

namespace RwEditor.Core.GameLog;

/// <summary>Which kind of game file a log text came from (each has its own layout).</summary>
public enum LogKind
{
    /// <summary>The game's log (lastrun.log, or the output of a Test load): lines that start with a timestamp.</summary>
    GameLog,
    /// <summary>crashes.txt: blocks "fatal (at ...)" / "Map Load Warning (at ...)" followed by a Java stack trace.</summary>
    Crashes,
    /// <summary>hs_err_pid*.log: the Java runtime's crash report.</summary>
    JvmCrash,
}

/// <summary>One log text to read, with the name it is shown under.</summary>
public sealed record LogSource(string Name, string Text, LogKind Kind = LogKind.GameLog);

public enum LogSeverity { Error, Warning, Info }

/// <summary>Whose fault a finding most likely is, as far as the files can show (never more than that).</summary>
public enum LogOrigin
{
    /// <summary>The line it names was changed by the user (a stat edit, a menu option edit).</summary>
    YourEdit,
    /// <summary>A file the editor generated (start-unit groups, hidden options).</summary>
    Generated,
    /// <summary>Also present in the load log of the shipped mod, so not caused by an edit.</summary>
    Shipped,
    /// <summary>Another mod, not this one.</summary>
    OtherMod,
    /// <summary>This mod, but not in the user's changes and not in the shipped baseline.</summary>
    Unknown,
    /// <summary>The game itself (engine, Java runtime).</summary>
    Engine,
}

/// <summary>One mod the game said it loaded (or disabled).</summary>
public sealed record LoadedMod(string Name, string Path, bool IsTribute, bool Disabled, string? DisabledWhy);

/// <summary>
/// One problem found in the logs, in plain words. <see cref="Count"/> is how many times the game wrote it; <see cref="Sample"/> keeps the first raw lines
/// (so the user can search the real log). File / Section / Key name the exact place in the mod when the log gives it.
/// </summary>
public sealed record LogFinding(string Code, LogSeverity Severity, string Title, string Explanation, string? Fix, LogOrigin Origin, string? OriginText,
    string? File, string? Section, string? Key, int Count, DateTime? Time, string Source, IReadOnlyList<string> Sample)
{
    public string SeverityText => Severity switch { LogSeverity.Error => "Problem", LogSeverity.Warning => "Warning", _ => "Note" };
}

/// <summary>What the analyser found in one or more logs.</summary>
public sealed class LogReport
{
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LoadedMod> Mods { get; init; } = Array.Empty<LoadedMod>();
    public IReadOnlyList<LogFinding> Findings { get; init; } = Array.Empty<LogFinding>();
    /// <summary>Lines the game writes in every run and that mean nothing for a mod (keycodes, missing translations, AI orders...), counted, not listed.</summary>
    public int NoiseLines { get; init; }
    public int TotalLines { get; init; }
    /// <summary>Entries left out because they are older than the copy (see <see cref="LogContext.Since"/>); they are history, not about this copy.</summary>
    public int OlderSkipped { get; init; }
    /// <summary>The log reached "init complete" (the game finished loading); null when the log is not a start-up log.</summary>
    public bool? ReachedInit { get; init; }
    public double? LoadMilliseconds { get; init; }
    public string Summary { get; init; } = "";

    /// <summary>No problem and no warning that touches this mod: the game loaded it and found nothing wrong.</summary>
    public bool Clean => Findings.All(f => f.Severity == LogSeverity.Info || f.Origin is LogOrigin.Shipped or LogOrigin.OtherMod or LogOrigin.Engine) && ReachedInit != false;

    public IEnumerable<LogFinding> Problems => Findings.Where(f => f.Severity == LogSeverity.Error);
}

/// <summary>What the analyser may know about the copy, to say whose edit a problem is. All parts are optional.</summary>
public sealed class LogContext
{
    public IReadOnlyList<StatEdit> StatEdits { get; init; } = Array.Empty<StatEdit>();
    public IReadOnlyList<OptionEdit> OptionEdits { get; init; } = Array.Empty<OptionEdit>();
    public IReadOnlyList<SpawnGroup> SpawnGroups { get; init; } = Array.Empty<SpawnGroup>();
    /// <summary>Unit name -> the file it is defined in (relative, forward slashes), for errors that name only a unit.</summary>
    public IReadOnlyDictionary<string, string>? UnitFiles { get; init; }
    /// <summary>Only entries at or after this local time are reported (older ones are history). Null = all.</summary>
    public DateTime? Since { get; init; }
    /// <summary>What the baseline of the shipped mod's own load log says is normal. Default: the embedded one.</summary>
    public ShippedLogBaseline? Baseline { get; init; }
    /// <summary>True when the lists above come from a real copy (then "not in your changes" is a statement); false = no copy was given.</summary>
    public bool HasChangeInfo { get; init; }

    public static LogContext From(EditorSettings s, IReadOnlyDictionary<string, string>? unitFiles = null, DateTime? since = null) => new()
    {
        StatEdits = s.StatEdits, OptionEdits = s.OptionEdits, SpawnGroups = s.SpawnGroups, UnitFiles = unitFiles, Since = since, HasChangeInfo = true,
    };
}
