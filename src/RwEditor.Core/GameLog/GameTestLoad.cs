using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RwEditor.Core.GameLog;

public enum ModSwitch { Unknown, Enabled, Disabled }

/// <summary>Reads the game's preferences.ini (read-only here: the editor never writes it).</summary>
public static class GamePreferences
{
    /// <summary>
    /// Whether the game has a local mod folder switched on, from the "modSettings:" line (entries "name|id|enabled" or "...|disabled"). A local mod's name is
    /// stored cut to its last characters followed by "..." (VERIFIED on the real file: "te_Custom_My_copy...|HASH|enabled" for the folder
    /// DepacheTribute_Custom_My_copy), so a local entry matches when the folder name ends with the text before the dots.
    /// </summary>
    public static ModSwitch StateOf(string preferencesText, string folderName)
    {
        var m = Regex.Match(preferencesText, @"^modSettings:(?<v>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!m.Success) return ModSwitch.Unknown;
        foreach (var raw in m.Groups["v"].Value.TrimEnd('\r').Split(','))
        {
            var parts = raw.Split('|');
            if (parts.Length < 3) continue;
            string name = parts[0];
            bool match = name.EndsWith("...", StringComparison.Ordinal) ? folderName.EndsWith(name[..^3], StringComparison.OrdinalIgnoreCase) && name.Length > 3
                                                                           : string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            return parts[2].Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase) ? ModSwitch.Enabled
                 : parts[2].Trim().Equals("disabled", StringComparison.OrdinalIgnoreCase) ? ModSwitch.Disabled : ModSwitch.Unknown;
        }
        return ModSwitch.Unknown;
    }
}

/// <summary>The running game (or a stand-in in tests).</summary>
public interface IRunningGame
{
    bool HasExited { get; }
    /// <summary>Ask the game to close like the window's close button (the way that is not counted as an incomplete load).</summary>
    void CloseGracefully();
    void Kill();
}

/// <summary>Starts the game and feeds every output line to <paramref name="onLine"/>.</summary>
public interface IGameLauncher
{
    IRunningGame Start(string gameFolder, string heap, Action<string> onLine);
}

/// <summary>The real thing: the same java command as the game's own fallback64.bat, minus the Steam flag.</summary>
public sealed class JavaGameLauncher : IGameLauncher
{
    sealed class Running(Process p) : IRunningGame
    {
        public bool HasExited => p.HasExited;
        public void CloseGracefully() { try { p.CloseMainWindow(); } catch (InvalidOperationException) { } }
        public void Kill() { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    public IRunningGame Start(string gameFolder, string heap, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(Path.Combine(gameFolder, "jvm64", "bin", "java.exe"))
        {
            WorkingDirectory = gameFolder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-Xmx" + heap, "-Dfile.encoding=UTF-8", "-Djava.library.path=.", "-cp", "game-lib.jar;libs/*", "com.corrodinggames.rts.java.Main", "-width", "800", "-height", "600" })
            psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new Running(p);
    }
}

public enum TestLoadOutcome { Refused, Finished, Failed, TimedOut, GameExited, Cancelled }

public sealed record TestLoadResult(TestLoadOutcome Outcome, string? Reason, bool ReachedInit, string LogText, string? SavedPath, TimeSpan Elapsed, string? PreferencesNote);

public sealed class TestLoadOptions
{
    public required string GameFolder { get; init; }
    /// <summary>The folder name of the copy under mods\units (checked against the game's mod switches).</summary>
    public string? CopyFolderName { get; init; }
    public int WaitAfterInitSeconds { get; init; } = 6;
    public int TimeoutSeconds { get; init; } = 240;
    public string Heap { get; init; } = "6000M";
    /// <summary>Where the log is saved (written once at the end); null = not saved.</summary>
    public string? SaveFolder { get; init; }
    public IGameLauncher Launcher { get; init; } = new JavaGameLauncher();
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Func<bool> IsGameRunning { get; init; } = DefaultIsGameRunning;
    /// <summary>How often the output is looked at (tests make it small).</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long to wait after a load error for the rest of its text.</summary>
    public TimeSpan AfterErrorWait { get; init; } = TimeSpan.FromSeconds(3);

    static bool DefaultIsGameRunning() => Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("Rusted Warfare", StringComparison.OrdinalIgnoreCase); } catch (InvalidOperationException) { return false; } });
}

/// <summary>
/// The "Test load" (DESIGN_EDITOR 5.5): starts the game once with the mods the player has switched on, reads its live log until it finishes loading or
/// reports a load error, closes it the gentle way, and hands back the log. It ports tools/smoke-test.ps1, which has been run over a hundred times.
/// Rules it keeps: the game must not already be running; the editor never writes preferences.ini (the game itself rewrites it at every start, including
/// the switches of mods it cannot see without Steam, so the file is put back to its exact earlier bytes afterwards); a copy that is switched off in the
/// game is refused instead of tested (a run that does not load the copy tests nothing); a forced kill is avoided because the game counts it as an
/// incomplete load and the next start is then in safe mode.
/// </summary>
public static class GameTestLoad
{
    /// <summary>Why a test cannot start, or null.</summary>
    public static string? Preflight(TestLoadOptions o, out string? prefsNote)
    {
        prefsNote = null;
        string g = o.GameFolder;
        if (!File.Exists(Path.Combine(g, "jvm64", "bin", "java.exe")) || !File.Exists(Path.Combine(g, "game-lib.jar")))
            return $"{g} does not look like the Rusted Warfare folder (jvm64\\bin\\java.exe or game-lib.jar is missing). Choose the game folder in Settings.";
        if (o.IsGameRunning()) return "The game is already running. Close it first (the test starts its own copy of the game and would clash with it).";
        string prefs = Path.Combine(g, "preferences.ini");
        if (o.CopyFolderName != null && File.Exists(prefs))
        {
            var st = GamePreferences.StateOf(File.ReadAllText(prefs, Encoding.UTF8), o.CopyFolderName);
            if (st == ModSwitch.Disabled)
                return "Your copy is switched OFF in the game's Mods menu, so a test would not load it and would tell you nothing. Switch it on there (the editor never changes the game's settings), then test again.";
            if (st == ModSwitch.Unknown) prefsNote = "The game has no switch for this copy yet; it is loaded as the game decides for a new mod.";
        }
        return null;
    }

    public static TestLoadResult Run(TestLoadOptions o, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        string? problem = Preflight(o, out var note);
        if (problem != null) return new TestLoadResult(TestLoadOutcome.Refused, problem, false, "", null, sw.Elapsed, note);

        string prefs = Path.Combine(o.GameFolder, "preferences.ini");
        byte[]? prefsBefore = File.Exists(prefs) ? File.ReadAllBytes(prefs) : null;
        var lines = new ConcurrentQueue<string>();
        var all = new List<string>();
        bool reached = false, errorSeen = false, exited = false, timedOut = false, cancelled = false;
        DateTime? errorAt = null, initAt = null;
        IRunningGame? game = null;
        try
        {
            progress?.Report("Starting the game (a window opens for a moment; it closes itself)...");
            game = o.Launcher.Start(o.GameFolder, o.Heap, l => lines.Enqueue(l));
            var deadline = DateTime.UtcNow.AddSeconds(o.TimeoutSeconds);
            while (true)
            {
                while (lines.TryDequeue(out var l))
                {
                    all.Add(l);
                    if (!reached && l.Contains("--Now loading:init complete", StringComparison.Ordinal)) { reached = true; initAt = DateTime.UtcNow; progress?.Report("The game finished loading; reading the last lines..."); }
                    if (!errorSeen && l.Contains("Error loading", StringComparison.Ordinal)) { errorSeen = true; errorAt = DateTime.UtcNow; progress?.Report("The game reported a load error; collecting it..."); }
                }
                if (cancel.IsCancellationRequested) { cancelled = true; break; }
                if (game.HasExited) { exited = true; break; }
                if (reached && DateTime.UtcNow - initAt!.Value >= TimeSpan.FromSeconds(o.WaitAfterInitSeconds)) break;
                if (errorSeen && DateTime.UtcNow - errorAt!.Value >= o.AfterErrorWait) break;
                if (DateTime.UtcNow >= deadline) { timedOut = true; break; }
                Thread.Sleep(o.Poll);
            }
        }
        finally
        {
            if (game != null)
            {
                progress?.Report("Closing the game...");
                if (!game.HasExited) game.CloseGracefully();
                for (int i = 0; i < 100 && !game.HasExited; i++) Thread.Sleep(Math.Max(1, (int)(o.Poll.TotalMilliseconds / 2)));
                if (!game.HasExited) game.Kill();
                for (int i = 0; i < 20 && !game.HasExited; i++) Thread.Sleep(Math.Max(1, (int)(o.Poll.TotalMilliseconds / 2)));
                // the rest of the output that arrived while closing
                Thread.Sleep(Math.Max(1, (int)(o.Poll.TotalMilliseconds / 4)));
                while (lines.TryDequeue(out var l)) all.Add(l);
            }
            // the game rewrites preferences.ini at every start: put the player's file back exactly (only when it changed)
            try
            {
                if (prefsBefore != null && (!File.Exists(prefs) || !File.ReadAllBytes(prefs).AsSpan().SequenceEqual(prefsBefore))) File.WriteAllBytes(prefs, prefsBefore);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note = (note != null ? note + " " : "") + $"preferences.ini could not be put back ({e.Message})."; }
        }

        string text = string.Join("\n", all) + "\n";
        string? saved = null;
        if (o.SaveFolder != null)
        {
            try
            {
                Directory.CreateDirectory(o.SaveFolder);
                saved = Path.Combine(o.SaveFolder, "testload-" + o.Now().ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(saved, text, new UTF8Encoding(false));   // one write at the end (RAM-buffer rule)
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { saved = null; note = (note != null ? note + " " : "") + $"The log could not be saved ({e.Message})."; }
        }
        var outcome = cancelled ? TestLoadOutcome.Cancelled : timedOut ? TestLoadOutcome.TimedOut : reached || errorSeen ? TestLoadOutcome.Finished : exited ? TestLoadOutcome.GameExited : TestLoadOutcome.Failed;
        string? reason = outcome switch
        {
            TestLoadOutcome.TimedOut => $"The game did not finish loading within {o.TimeoutSeconds} s.",
            TestLoadOutcome.GameExited => "The game closed by itself before it finished loading.",
            TestLoadOutcome.Cancelled => "Cancelled.",
            TestLoadOutcome.Failed => "The game stopped without finishing.",
            _ => null,
        };
        return new TestLoadResult(outcome, reason, reached, text, saved, sw.Elapsed, note);
    }
}
