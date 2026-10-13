using System.Diagnostics;
using RwEditor.Core;
using RwEditor.Core.Diagnostics;
using RwEditor.Core.Setup;

namespace RwEditor.App;

/// <summary>
/// App-wide services: the session log (RAM only, written once on exit / once per crash), the remembered settings, and the crash safety net
/// (DESIGN_EDITOR 5.5): every UI command runs inside <see cref="SafeRun"/>, and the global handlers catch anything that slips through.
/// </summary>
static class AppHost
{
    public const string Title = "Depache's Mod Tribute Editor";
    public const string MultiplayerWarning =
        "Edited copies cannot join normal multiplayer games - all players need byte-identical mod files. Multiplayer is for the default mod only. " +
        "Profile import/export will change this later.";

    public static SessionLog Log { get; private set; } = new();
    public static AppSettings Settings { get; private set; } = new();
    public static string Version => typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static long StartTicks { get; } = Stopwatch.GetTimestamp();
    /// <summary>When true (self-test / screenshot runs), no dialog is ever shown: problems are logged and the run fails.</summary>
    public static bool Unattended { get; set; }
    public static int UnattendedFailures;

    public static double MsSinceStart => Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;

    /// <summary>Self-test only: backups go here instead of %LOCALAPPDATA%\DepacheRwEditor\backups (null = the normal place).</summary>
    public static string? BackupRootOverride { get; set; }
    /// <summary>--no-art: unit pictures stay grey boxes (the README screenshots must not show other authors' sprites).</summary>
    public static bool NoArt { get; set; }

    /// <summary>The options every write of the app uses (folder guard without the git-tree exception; backups in the normal place).</summary>
    public static RwEditor.Core.Apply.ApplyOptions WriteOptions() => new() { BackupRoot = BackupRootOverride };

    /// <summary>Unattended runs: the answer every confirmation gets (the self-test confirms; a test can set false to check a cancel).</summary>
    public static bool AutoConfirm { get; set; } = true;
    /// <summary>Unattended runs: the choice made in the "this value is shared" dialog (true = every unit that shares it, false = only this unit,
    /// null = cancel). Default: only this unit when possible.</summary>
    public static Func<bool, bool, bool?> AutoShareChoice { get; set; } = (unitOk, templateOk) => unitOk ? false : templateOk ? true : null;
    /// <summary>Unattended runs: the texts of every message / confirmation shown (the self-test checks them).</summary>
    /// <summary>Unattended answer of "Set for many units": the value and the filter for a row, or null to cancel.</summary>
    public static Func<RwEditor.Core.Browse.StatRow, (string Value, RwEditor.Core.Stats.BulkFilter Filter)?>? AutoBulk { get; set; }

    public static List<string> UnattendedMessages { get; } = new();

    public static void Init(string? logFolder = null)
    {
        Log = new SessionLog(logFolder);
        Log.Info($"{Title} {Version} starting; Windows {Environment.OSVersion.VersionString}; .NET {Environment.Version}; args: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        Settings = AppSettings.Load();
        if (Settings.LoadProblem != null) Log.Warn(Settings.LoadProblem);
    }

    public static void SaveSettings()
    {
        try { Settings.Save(); Log.Info($"settings saved (copy: {Settings.CopyFolder ?? "-"}, game: {Settings.GameFolder ?? "-"})"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("settings could not be saved: " + e.Message); }
    }

    /// <summary>Runs a UI command; any error is logged and shown in plain words. Returns false when it failed. The editor keeps running.</summary>
    public static bool SafeRun(IWin32Window? owner, string what, Action action)
    {
        try { action(); return true; }
        catch (Exception e) { Report(owner, what, e); return false; }
    }

    /// <summary>Like <see cref="SafeRun"/> for async work started from the UI.</summary>
    public static async Task<bool> SafeRunAsync(IWin32Window? owner, string what, Func<Task> action)
    {
        try { await action(); return true; }
        catch (Exception e) { Report(owner, what, e); return false; }
    }

    /// <summary>Expected refusals (a folder that is not allowed, a missing file...) get a friendly message; anything else is a crash report.</summary>
    public static void Report(IWin32Window? owner, string what, Exception e)
    {
        if (e is EditorRefusalException or ApplyFailedException)
        {
            Log.Warn($"{what}: {e.Message}");
            if (Unattended) { Interlocked.Increment(ref UnattendedFailures); return; }
            MessageBox.Show(owner, e.Message, "Not possible: " + what, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{what}: file problem: {e.Message}");
            if (Unattended) { Interlocked.Increment(ref UnattendedFailures); return; }
            MessageBox.Show(owner, $"A file could not be read or written while {what}.\n\n{e.Message}\n\nNothing else was changed. Check that the folder still exists and is not open in another program, then try again.",
                "File problem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Crash(what, e, canContinue: true);
    }

    /// <summary>Writes the crash log at once (one write) and shows the crash dialog (unless unattended).</summary>
    public static void Crash(string what, Exception e, bool canContinue)
    {
        string? path = Log.WriteCrashLog(e, what);
        if (Unattended) { Interlocked.Increment(ref UnattendedFailures); return; }
        try
        {
            using var d = new CrashDialog(what, e, path, canContinue);
            d.ShowDialog();
        }
        catch (Exception) { /* the dialog itself failed: the crash log is already on disk */ }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("could not open " + folder + ": " + e.Message);
            if (!Unattended) MessageBox.Show($"The folder could not be opened:\n{folder}\n\n{e.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    static Icon? _icon;
    /// <summary>The app icon (embedded copy of assets\depache-rw-editor.ico), or the default when it is missing.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                using var s = typeof(AppHost).Assembly.GetManifestResourceStream("RwEditor.App.depache-rw-editor.ico");
                if (s != null) _icon = new Icon(s);
            }
            catch (Exception e) when (e is ArgumentException or IOException) { Log.Warn("app icon could not be loaded: " + e.Message); }
            return _icon;
        }
    }
}
