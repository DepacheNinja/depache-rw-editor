using System.Globalization;
using System.Text.RegularExpressions;

namespace RwEditor.Core.GameLog;

/// <summary>One logical entry of the game's log: the line with its timestamp plus the unstamped lines the game wrote right after it (error details).</summary>
public sealed record LogEntry(DateTime? Time, string Text, int FirstLine);

/// <summary>One block of crashes.txt: its kind ("fatal", "Map Load Warning"), time, game version and the text after the header.</summary>
public sealed record CrashBlock(string Kind, DateTime? Time, string Version, string Body, int FirstLine);

/// <summary>
/// Reads the layouts of the game's log files (VERIFIED against the real files: 91 recorded runs, lastrun.log, crashes.txt, hs_err_pid*.log). Nothing here
/// interprets a line; that is <see cref="GameLogAnalyzer"/>.
/// </summary>
public static class GameLogParser
{
    /// <summary>The layout of a game log file by its name: crashes.txt, hs_err_pid*.log, anything else is the game's normal log.</summary>
    public static LogKind KindOf(string path)
    {
        string n = Path.GetFileName(path);
        if (n.Equals("crashes.txt", StringComparison.OrdinalIgnoreCase)) return LogKind.Crashes;
        if (n.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase)) return LogKind.JvmCrash;
        return LogKind.GameLog;
    }

    static readonly Regex Stamp = new(@"^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+): ", RegexOptions.CultureInvariant);

    /// <summary>The game's log: a line starting "yyyy-MM-dd HH:mm:ss.fff: " begins an entry; lines without a stamp belong to the entry before them.</summary>
    public static List<LogEntry> ParseGameLog(string text)
    {
        var list = new List<LogEntry>();
        string? cur = null; DateTime? curTime = null; int first = 0, n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r');
            var m = Stamp.Match(line);
            if (m.Success)
            {
                if (cur != null) list.Add(new LogEntry(curTime, cur, first));
                cur = line[m.Length..];
                curTime = DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
                first = n;
            }
            else if (cur != null) cur += "\n" + line;
        }
        if (cur != null) list.Add(new LogEntry(curTime, cur.TrimEnd('\n', '\r'), first));
        return list;
    }

    static readonly Regex CrashHeader = new(@"^(?<kind>[A-Za-z][A-Za-z ]*?) \(at (?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) - (?<v>[0-9.]+)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>crashes.txt: "kind (at time - version)" then the exception and its stack, up to the next header.</summary>
    public static List<CrashBlock> ParseCrashes(string text)
    {
        var list = new List<CrashBlock>();
        string? kind = null, ver = null; DateTime? time = null; int first = 0, n = 0;
        var body = new List<string>();
        void Flush() { if (kind != null) list.Add(new CrashBlock(kind, time, ver ?? "", string.Join("\n", body).Trim(), first)); body.Clear(); }
        foreach (var raw in text.Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r').TrimStart('\uFEFF');
            var m = CrashHeader.Match(line);
            if (m.Success)
            {
                Flush();
                kind = m.Groups["kind"].Value; ver = m.Groups["v"].Value; first = n;
                time = DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
            }
            else if (kind != null) body.Add(line);
        }
        Flush();
        return list;
    }
}
