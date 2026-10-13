using System.Globalization;
using System.Text;

namespace RwEditor.Core.Diagnostics;

/// <summary>
/// The editor's own log (DESIGN_EDITOR 5.5 "Editor crash log"). Lines are kept in RAM only (RAM-buffer rule) and written ONCE: the session log on exit
/// (<see cref="WriteSessionLog"/>, at most once per session), and one crash log per crash (<see cref="WriteCrashLog"/>, the whole session so far plus the
/// error, written in one go). Files go to %LOCALAPPDATA%\DepacheRwEditor\logs as editor-&lt;stamp&gt;.log / crash-&lt;stamp&gt;.log. Writing never throws:
/// a log that cannot be written must not crash the editor. Thread-safe.
/// </summary>
public sealed class SessionLog
{
    public static string DefaultFolder => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "logs");

    readonly object _gate = new();
    readonly List<string> _lines = new();
    readonly Func<DateTime> _now;
    int _dropped;
    string? _sessionPath;
    int _crashCount;

    public SessionLog(string? folder = null, int maxLines = 20000, Func<DateTime>? utcNow = null)
    {
        Folder = folder ?? DefaultFolder;
        MaxLines = Math.Max(100, maxLines);
        _now = utcNow ?? (() => DateTime.UtcNow);
        Started = _now();
    }

    public string Folder { get; }
    public int MaxLines { get; }
    public DateTime Started { get; }
    public int CrashCount { get { lock (_gate) return _crashCount; } }

    public void Info(string message) => Add("INFO ", message);
    public void Warn(string message) => Add("WARN ", message);
    public void Error(string message, Exception? e = null) => Add("ERROR", e == null ? message : message + Environment.NewLine + e);

    void Add(string level, string message)
    {
        string stamp = _now().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            // keep the first quarter (how the session started) and drop from the middle when the buffer is full
            if (_lines.Count >= MaxLines) { _lines.RemoveAt(MaxLines / 4); _dropped++; }
            _lines.Add($"{stamp} {level} {message}");
        }
    }

    /// <summary>The whole log so far as text (header + lines).</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                sb.Append("Depache's Mod Tribute Editor log, session started ").Append(Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC").Append('\n');
                if (_dropped > 0) sb.Append($"({_dropped} older lines were dropped to keep memory small)\n");
                foreach (var l in _lines) sb.Append(l).Append('\n');
                return sb.ToString();
            }
        }
    }

    public IReadOnlyList<string> Lines { get { lock (_gate) return _lines.ToList(); } }

    string Stamp() => _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Writes the session log once (later calls return the same path and write nothing). Null when it could not be written.</summary>
    public string? WriteSessionLog()
    {
        lock (_gate)
        {
            if (_sessionPath != null) return _sessionPath;
            _sessionPath = WriteOnce($"editor-{Stamp()}.log", Text);
            return _sessionPath;
        }
    }

    /// <summary>Writes one crash log (session so far + what happened + the error), in one write. Null when it could not be written. Never throws.</summary>
    public string? WriteCrashLog(Exception e, string context)
    {
        try
        {
            Error("CRASH in " + context, e);
            string text;
            lock (_gate) { _crashCount++; text = Text; }
            var sb = new StringBuilder();
            sb.Append("What happened: ").Append(context).Append('\n');
            sb.Append("Error: ").Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n');
            sb.Append("Editor version: ").Append(typeof(SessionLog).Assembly.GetName().Version).Append('\n');
            sb.Append("Windows: ").Append(Environment.OSVersion.VersionString).Append(", .NET ").Append(Environment.Version).Append('\n');
            sb.Append('\n').Append(e).Append("\n\n----- session log -----\n").Append(text);
            return WriteOnce($"crash-{Stamp()}-{CrashCount}.log", sb.ToString());
        }
        catch (Exception) { return null; }   // a crash log must never cause a second crash
    }

    string? WriteOnce(string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, fileName);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
