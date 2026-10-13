using System.Diagnostics;
using System.Text;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.GameLog;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Setup;

namespace RwEditor.App;

/// <summary>
/// "Logs": two tabs. "Game check" reads what the game says about the copy (a Test load now, or the game's own log files) and names the file, the line and
/// whose change it is; "Editor log" is the editor's own session log and crash reports (E2, kept as it was, with the crash reports now readable here).
/// </summary>
sealed class LogsView : UserControl
{
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly GameCheckPage _game;
    readonly EditorLogPage _editor = new();

    public LogsView(MainForm form)
    {
        Padding = new Padding(8);
        _game = new GameCheckPage(form) { Dock = DockStyle.Fill };
        var t1 = new TabPage("Game check") { Padding = new Padding(8) };
        t1.Controls.Add(_game);
        var t2 = new TabPage("Editor log") { Padding = new Padding(8) };
        _editor.Dock = DockStyle.Fill;
        t2.Controls.Add(_editor);
        _tabs.TabPages.Add(t1);
        _tabs.TabPages.Add(t2);
        Controls.Add(_tabs);
        Controls.Add(new Label { Text = "Logs", Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Padding = new Padding(4, 8, 0, 0) });
    }

    public GameCheckPage Game => _game;
    public EditorLogPage Editor => _editor;
    public void Refresh2() { _game.RefreshHeader(); _editor.Refresh2(); }
}

/// <summary>Reads the game's side: a Test load, the game's own logs, or any log file, and shows the findings in plain words.</summary>
sealed class GameCheckPage : UserControl
{
    readonly MainForm _form;
    readonly Button _test = new() { Text = "Test load now", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly Button _cancel = new() { Text = "Cancel the test", AutoSize = true, Visible = false };
    readonly Button _read = new() { Text = "Read the game's own logs", AutoSize = true };
    readonly Button _open = new() { Text = "Open a log file...", AutoSize = true };
    readonly Button _copy = new() { Text = "Copy this report", AutoSize = true, Enabled = false };
    readonly CheckBox _older = new() { Text = "Include entries from before my copy was made", AutoSize = true };
    readonly CheckBox _normal = new() { Text = "Show normal notes (from the shipped mod and other mods)", AutoSize = true };
    readonly Label _intro = new() { AutoSize = true, MaximumSize = new Size(1000, 0), Padding = new Padding(0, 0, 0, 6) };
    readonly Label _last = new() { AutoSize = true, MaximumSize = new Size(1000, 0), ForeColor = Color.FromArgb(70, 80, 90) };
    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(1000, 0), Font = new Font("Segoe UI", 9.75f, FontStyle.Bold), Padding = new Padding(0, 6, 0, 4) };
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox _detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f), BackColor = Color.White };
    readonly Button _goto = new() { Text = "Show me where in the editor", AutoSize = true, Enabled = false };
    readonly ListBox _suspects = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Label _suspectsTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Text = "", Padding = new Padding(0, 6, 0, 2) };
    readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 300 };

    LogReport? _report;
    string _sourceNote = "";
    CancellationTokenSource? _cts;
    readonly List<LogFinding> _shown = new();

    public GameCheckPage(MainForm form)
    {
        _form = form;
        _intro.Text = "The game tells what it dislikes about a mod while it loads. \"Test load now\" starts the game once with the mods you have switched on, reads that, closes it gently and tells you in plain words " +
                      "what is wrong and which of your changes it is. It never changes your game's settings. The test runs without Steam, so a Workshop copy is not part of it (the game's own log of a normal start shows that).";
        _list.Columns.Add("", 70);
        _list.Columns.Add("What", 330);
        _list.Columns.Add("Whose", 190);
        _list.Columns.Add("Where", 260);
        _list.Columns.Add("Times", 50);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _test, _cancel, _read, _open, _copy });
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        options.Controls.AddRange(new Control[] { _older, _normal });
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var topStack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        topStack.Controls.AddRange(new Control[] { _intro, buttons, _last, options, _status });
        top.Controls.Add(topStack);

        var detailPanel = new Panel { Dock = DockStyle.Fill };
        var detailButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        detailButtons.Controls.Add(_goto);
        detailPanel.Controls.Add(_detail);
        detailPanel.Controls.Add(detailButtons);
        var suspectPanel = new Panel { Dock = DockStyle.Fill };
        suspectPanel.Controls.Add(_suspects);
        suspectPanel.Controls.Add(_suspectsTitle);
        var lower = new SplitContainer { Dock = DockStyle.Bottom, Height = 250, Orientation = Orientation.Vertical, SplitterDistance = 620 };
        lower.Panel1.Controls.Add(detailPanel);
        lower.Panel2.Controls.Add(suspectPanel);
        lower.SizeChanged += (_, _) => { try { lower.SplitterDistance = Math.Max(200, (int)(lower.Width * 0.6)); } catch (InvalidOperationException) { } };

        Controls.Add(_list);
        Controls.Add(lower);
        Controls.Add(top);

        _tip.SetToolTip(_older, "The game's log files keep old lines. Normally only what happened after your copy was made is read.");
        _tip.SetToolTip(_normal, "Notes the shipped mod and other mods always write are hidden, because they are not caused by your changes.");

        _test.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "testing the load", () => TestLoadAsync());
        _cancel.Click += (_, _) => { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } };
        _read.Click += (_, _) => AppHost.SafeRun(FindForm(), "reading the game's logs", ReadGameLogs);
        _open.Click += (_, _) => AppHost.SafeRun(FindForm(), "opening a log file", OpenLogFile);
        _copy.Click += (_, _) => AppHost.SafeRun(FindForm(), "copying the report", CopyReport);
        _older.CheckedChanged += (_, _) => AppHost.SafeRun(FindForm(), "re-reading", RereadLast);
        _normal.CheckedChanged += (_, _) => Fill();
        _list.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "showing the finding", ShowSelected);
        _goto.Click += (_, _) => AppHost.SafeRun(FindForm(), "going to the place", GoToSelected);
        Disposed += (_, _) => { _cts?.Dispose(); _tip.Dispose(); };
    }

    ModSession? S => _form.Session;
    sealed record Last(IReadOnlyList<LogSource> Sources, string Note);
    Last? _lastSources;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public LogReport? Report => _report;
    public int FindingCount => _shown.Count;
    public string SummaryText => _status.Text;
    public string DetailText => _detail.Text;
    public bool TestButtonEnabled => _test.Enabled;
    public int SuspectCount => _suspects.Items.Count;

    // ------------------------------------------------------------------------------------------------------------ header
    public void RefreshHeader()
    {
        var s = S;
        bool copy = s?.IsEditorCopy == true && s.Marker != null;
        _test.Enabled = copy && !_form.Editor.Busy;
        if (!copy) { _last.Text = "Open your copy first (Make my copy, or Open my existing copy in Settings)."; return; }
        var rec = TestLoadStore.Load(s!.Marker!.CopyId);
        _last.Text = rec == null ? "This copy has not been tested yet."
            : $"Last Test load: {rec.Utc.ToLocalTime():yyyy-MM-dd HH:mm} - {(rec.Clean ? "clean" : rec.ReachedInit ? $"{rec.Problems} problem(s), {rec.Warnings} warning(s)" : "the game did not finish loading")}.";
    }

    // ------------------------------------------------------------------------------------------------------------ test load
    /// <summary>Where the game folder is for this copy, or null with the reason.</summary>
    string? GameFolder(out string? reason)
    {
        reason = null;
        var s = S;
        if (s?.IsEditorCopy != true) { reason = "Open your copy first."; return null; }
        string? game = ModSession.GameDirFor(s.Root);
        if (game == null) { reason = "The game folder is not known yet. Choose it in Settings."; return null; }
        var units = Directory.GetParent(s.Root);
        if (!(units?.Name.Equals("units", StringComparison.OrdinalIgnoreCase) == true && units.Parent?.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) == true
              && string.Equals(units.Parent.Parent?.FullName.TrimEnd('\\', '/'), game.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Your copy is not in the game's mods folder, so the game cannot load it. Use \"Put a copy in the game\" in Settings first.";
            return null;
        }
        return game;
    }

    public Task<bool> TestLoadAsync(IGameLauncher? launcher = null, Func<bool>? gameRunning = null, bool skipConfirm = false, string? gameFolderForTest = null) => TestLoadCoreAsync(launcher, gameRunning, skipConfirm, gameFolderForTest);

    async Task<bool> TestLoadCoreAsync(IGameLauncher? launcher, Func<bool>? gameRunning, bool skipConfirm, string? gameFolderForTest)
    {
        var s = S;
        string? why = null;
        string? game = gameFolderForTest ?? GameFolder(out why);
        if (game == null) { Ask.Info(FindForm(), "Test load did not start", why!, problem: false); return false; }
        if (!skipConfirm && !Ask.Confirm(FindForm(), "Test load", "This starts the game for about a minute with the mods you have switched on. A game window opens and closes by itself. Nothing in your copy or in the game's settings is changed. Do not use the game during the test.",
                Array.Empty<string>(), "Start the test", "Cancel")) return false;
        var marker = s!.Marker!;
        var opts = new TestLoadOptions
        {
            GameFolder = game, CopyFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(s.Root)), SaveFolder = Path.Combine(TestLoadStore.DefaultFolder, "logs"),
            Launcher = launcher ?? new JavaGameLauncher(), IsGameRunning = gameRunning ?? (() => Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("Rusted Warfare", StringComparison.OrdinalIgnoreCase); } catch (InvalidOperationException) { return false; } })),
            TimeoutSeconds = AppHost.Unattended ? 20 : 240, WaitAfterInitSeconds = AppHost.Unattended ? 0 : 6,
            Poll = AppHost.Unattended ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(1), AfterErrorWait = AppHost.Unattended ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(3),
        };
        TestLoadResult? res = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(m => _status.Text = m);
        _test.Enabled = false; _read.Enabled = false; _open.Enabled = false; _cancel.Visible = true;
        _status.Text = "Starting the test...";
        try
        {
            bool ran = await _form.Editor.TryRunExclusiveAsync("test load", async () => { res = await Task.Run(() => GameTestLoad.Run(opts, progress, _cts.Token)); });
            if (!ran || res == null) return false;
        }
        finally { _cancel.Visible = false; _read.Enabled = true; _open.Enabled = true; _cts?.Dispose(); _cts = null; RefreshHeader(); }

        if (res.Outcome == TestLoadOutcome.Refused) { _status.Text = "The test did not start."; Ask.Info(FindForm(), "Test load did not start", res.Reason ?? "", problem: false); return false; }
        AppHost.Log.Info($"test load: {res.Outcome} in {res.Elapsed.TotalSeconds:0} s, init {res.ReachedInit}, log {res.SavedPath}");
        var src = new[] { new LogSource("Test load", res.LogText) };
        _lastSources = new Last(src, "Test load" + (res.SavedPath != null ? $" (log saved: {res.SavedPath})" : "") + (res.Reason != null ? ". " + res.Reason : ""));
        var report = Analyze(src, includeOlder: true);   // a Test load is a fresh log: everything in it counts
        _report = report;
        _sourceNote = _lastSources.Note;
        var before = TestLoadStore.Load(marker.CopyId);
        var rec = TestLoadStore.RecordOf(report, res.SavedPath, DateTime.UtcNow);
        try { TestLoadStore.Save(marker.CopyId, rec); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppHost.Log.Warn("test result not saved: " + e.Message); }
        Show(report, rec.Clean ? null : before is { Clean: true } ? before.Utc : null, res.PreferencesNote, rec.Clean);
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ read logs
    void ReadGameLogs()
    {
        string? game = S?.IsEditorCopy == true ? ModSession.GameDirFor(S.Root) : AppHost.Settings.GameFolder;
        if (game == null) { Ask.Info(FindForm(), "Game folder not known", "Choose the game folder in Settings first."); return; }
        var sources = new List<LogSource>();
        var notes = new List<string>();
        foreach (var name in new[] { "lastrun.log", "crashes.txt" })
        {
            string p = Path.Combine(game, name);
            if (!File.Exists(p)) { notes.Add($"{name}: not there"); continue; }
            sources.Add(new LogSource(name, File.ReadAllText(p, Encoding.UTF8), GameLogParser.KindOf(p)));
            notes.Add($"{name} (written {File.GetLastWriteTime(p):yyyy-MM-dd HH:mm})");
        }
        foreach (var p in Directory.EnumerateFiles(game, "hs_err_pid*.log").OrderByDescending(File.GetLastWriteTime).Take(3))
        {
            sources.Add(new LogSource(Path.GetFileName(p), File.ReadAllText(p, Encoding.UTF8), LogKind.JvmCrash));
            notes.Add($"{Path.GetFileName(p)} (written {File.GetLastWriteTime(p):yyyy-MM-dd HH:mm})");
        }
        if (sources.Count == 0) { Ask.Info(FindForm(), "No logs found", $"The game folder {game} has no lastrun.log, crashes.txt or hs_err file."); return; }
        _lastSources = new Last(sources, "The game's own files: " + string.Join("; ", notes));
        RereadLast();
    }

    void OpenLogFile()
    {
        using var d = new OpenFileDialog { Title = "Open a game log", Filter = "Game logs (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        _lastSources = new Last(new[] { new LogSource(Path.GetFileName(d.FileName), File.ReadAllText(d.FileName, Encoding.UTF8), GameLogParser.KindOf(d.FileName)) }, d.FileName);
        RereadLast();
    }

    public void AnalyzeTextForTest(string name, string text, LogKind kind = LogKind.GameLog, bool includeOlder = true)
    {
        _lastSources = new Last(new[] { new LogSource(name, text, kind) }, name);
        _older.Checked = includeOlder;
        RereadLast();
    }

    void RereadLast()
    {
        if (_lastSources == null) return;
        _report = Analyze(_lastSources.Sources, _older.Checked);
        _sourceNote = _lastSources.Note;
        var rec = S?.Marker != null ? TestLoadStore.Load(S.Marker.CopyId) : null;
        Show(_report, rec is { Clean: true } ? rec.Utc : null, null, _report.Clean);
    }

    LogReport Analyze(IReadOnlyList<LogSource> sources, bool includeOlder)
    {
        var s = S;
        LogContext ctx;
        if (s != null)
        {
            DateTime? since = !includeOlder && s.Marker != null ? s.Marker.CreatedUtc.ToLocalTime() : null;
            var units = s.Model.Units.GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.First().RelPath);
            ctx = LogContext.From(s.Settings, units, since);
        }
        else ctx = new LogContext();
        return GameLogAnalyzer.Analyze(sources, ctx);
    }

    // ------------------------------------------------------------------------------------------------------------ show
    void Show(LogReport r, DateTime? lastCleanUtc, string? note, bool clean)
    {
        _status.Text = r.Summary + (note != null ? " " + note : "");
        _status.ForeColor = r.Problems.Any() ? Color.FromArgb(170, 30, 30) : clean ? Color.FromArgb(20, 120, 50) : Color.FromArgb(120, 80, 0);
        _intro.Text = _intro.Text;   // unchanged; the source note goes into the list header below
        Fill();
        _copy.Enabled = true;
        FillSuspects(r, lastCleanUtc);
        RefreshHeader();
    }

    void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _shown.Clear();
        if (_report != null)
        {
            foreach (var f in _report.Findings.Where(f => _normal.Checked || f.Severity == LogSeverity.Error || f.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod)))
            {
                _shown.Add(f);
                var it = new ListViewItem(new[] { f.SeverityText, f.Title, WhoseText(f), WhereText(f), f.Count > 1 ? "x" + f.Count : "" }) { Tag = f };
                it.ForeColor = f.Severity == LogSeverity.Error ? Color.FromArgb(170, 30, 30) : f.Severity == LogSeverity.Warning ? Color.FromArgb(150, 90, 0) : Color.Gray;
                _list.Items.Add(it);
            }
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true; else ShowSelected();
        if (_list.Items.Count == 0 && _report != null)
            _detail.Text = (_report.Findings.Count > 0 ? "Only normal notes were found (hidden). Tick \"Show normal notes\" to see them.\r\n\r\n" : "Nothing to report.\r\n\r\n")
                + $"Read: {_sourceNote}\r\n{_report.TotalLines:N0} log lines, {_report.NoiseLines:N0} routine lines the game always writes (ignored).\r\n\r\n"
                + "A log shows only what the game complains about while loading. Wrong behaviour in a match without an error line cannot be read from it.";
    }

    static string WhoseText(LogFinding f) => f.Origin switch
    {
        LogOrigin.YourEdit => "your change", LogOrigin.Generated => "made by the editor", LogOrigin.Shipped => "shipped mod (normal)", LogOrigin.OtherMod => "another mod", LogOrigin.Engine => "the game", _ => "not known",
    };

    static string WhereText(LogFinding f) => f.File == null ? "" : f.File + (f.Section != null ? " [" + f.Section + "]" : "") + (f.Key != null ? " " + f.Key : "");

    void ShowSelected()
    {
        var f = _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as LogFinding : null;
        _goto.Enabled = f != null && Target(f) != null;
        if (f == null) { if (_list.Items.Count > 0) _detail.Text = ""; return; }
        var sb = new StringBuilder();
        sb.AppendLine(f.Title + (f.Count > 1 ? $"   (the game wrote it {f.Count} times)" : ""));
        sb.AppendLine();
        sb.AppendLine("Whose: " + WhoseText(f) + (f.OriginText != null ? " - " + f.OriginText : ""));
        if (f.File != null) sb.AppendLine("Where: " + WhereText(f));
        sb.AppendLine();
        sb.AppendLine(f.Explanation.Replace("\n", "\r\n"));
        if (f.Fix != null) { sb.AppendLine(); sb.AppendLine("What to do: " + f.Fix); }
        if (f.Sample.Count > 0) { sb.AppendLine(); sb.AppendLine("In the log (" + f.Source + "):"); foreach (var l in f.Sample) sb.AppendLine("  " + l.Replace("\n", "\r\n  ")); }
        _detail.Text = sb.ToString();
    }

    /// <summary>Where "Show me where" goes: a unit's stat the user changed, the Start units page, or the Menu options page.</summary>
    (string Kind, string? Unit, string? StatId, string? Section)? Target(LogFinding f)
    {
        var s = S;
        if (s == null || f.File == null) return null;
        if (f.Origin == LogOrigin.Generated)
            return f.File.Contains("selectorGroups", StringComparison.OrdinalIgnoreCase) ? ("menu", null, null, null) : ("groups", null, null, null);
        var stat = s.Settings.StatEdits.FirstOrDefault(e => string.Equals(e.File, f.File, StringComparison.OrdinalIgnoreCase) && (f.Section == null || e.FileSection == f.Section) && (f.Key == null || e.Key == f.Key))
                   ?? s.Settings.StatEdits.FirstOrDefault(e => string.Equals(e.File, f.File, StringComparison.OrdinalIgnoreCase));
        if (stat != null) return ("stat", stat.Unit, stat.StatId, stat.Section);
        if (s.Settings.OptionEdits.Any(e => string.Equals(e.File, f.File, StringComparison.OrdinalIgnoreCase))) return ("menu", null, null, null);
        return null;
    }

    void GoToSelected()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not LogFinding f || Target(f) is not { } t) return;
        switch (t.Kind)
        {
            case "stat": _form.ShowUnitStat(t.Unit!, t.StatId!, t.Section!); break;
            case "groups": _form.SelectNav("groups"); break;
            case "menu": _form.SelectNav("menu"); break;
        }
    }

    void FillSuspects(LogReport r, DateTime? lastCleanUtc)
    {
        _suspects.Items.Clear();
        var s = S;
        bool trouble = r.Problems.Any() || r.Findings.Any(f => f.Severity == LogSeverity.Warning && f.Origin is LogOrigin.YourEdit or LogOrigin.Generated or LogOrigin.Unknown) || r.ReachedInit == false;
        if (s?.Marker == null || !trouble) { _suspectsTitle.Text = ""; return; }
        IReadOnlyList<HistoryEntry> ch;
        try { ch = TestLoadStore.ChangesSince(History.List(AppHost.WriteOptions().EffectiveBackupRoot, s.Root), lastCleanUtc); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ch = Array.Empty<HistoryEntry>(); }
        _suspectsTitle.Text = lastCleanUtc != null ? "Changes since the last clean test (suspects when no cause is named above):" : "Changes made to this copy (the copy has no clean test yet):";
        foreach (var h in ch) _suspects.Items.Add($"{h.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {h.KindText}: {h.Summary}");
        if (ch.Count == 0) _suspects.Items.Add("(no changes were made to this copy)");
    }

    void CopyReport()
    {
        if (_report == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"{AppHost.Title} {AppHost.Version} - game check");
        sb.AppendLine("Source: " + _sourceNote);
        sb.AppendLine(_report.Summary);
        foreach (var f in _report.Findings.Where(f => _normal.Checked || f.Severity == LogSeverity.Error || f.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod)))
        {
            sb.AppendLine();
            sb.AppendLine($"[{f.SeverityText}] {f.Title}{(f.Count > 1 ? $" (x{f.Count})" : "")}");
            sb.AppendLine("  whose: " + WhoseText(f) + (f.OriginText != null ? " - " + f.OriginText : ""));
            if (f.File != null) sb.AppendLine("  where: " + WhereText(f));
            sb.AppendLine("  " + f.Explanation.Replace("\n", "\n  "));
            if (f.Fix != null) sb.AppendLine("  what to do: " + f.Fix);
        }
        try { Clipboard.SetText(sb.ToString()); } catch (System.Runtime.InteropServices.ExternalException) { }
    }
}

/// <summary>The editor's own log (this session's lines) and its crash reports (now readable here).</summary>
sealed class EditorLogPage : UserControl
{
    readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly ListBox _crashes = new() { Dock = DockStyle.Top, Height = 90 };
    readonly Label _what = new() { Text = "This session:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 8, 0, 2) };

    public EditorLogPage()
    {
        Controls.Add(_text);   // the fill control first: docking runs from the last added, so the top rows stack above it
        Ui.Stack(this,
            Ui.Para($"The editor keeps a log of what it does. It is saved when the editor closes (and right away if something goes wrong), in:\n{AppHost.Log.Folder}"),
            Ui.Row(Ui.Btn("Open log folder", () => AppHost.OpenFolder(AppHost.Log.Folder)),
                   Ui.Btn("Copy this session's log", () => { try { Clipboard.SetText(AppHost.Log.Text); } catch (System.Runtime.InteropServices.ExternalException) { } }, what: "copying the log"),
                   Ui.Btn("Copy the crash report shown", () => { try { if (_shownCrash != null) Clipboard.SetText(_text.Text); } catch (System.Runtime.InteropServices.ExternalException) { } }, what: "copying the crash report"),
                   Ui.Btn("Show this session again", ShowSession, what: "showing the session log"),
                   Ui.Btn("Refresh", Refresh2, what: "refreshing the log")),
            new Label { Text = "Crash reports saved so far (click one to read it):", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 8, 0, 2) },
            _crashes,
            _what);
        _crashes.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "showing the crash report", ShowCrash);
    }

    string? _shownCrash;
    public string ShownText => _text.Text;
    public int CrashCount => _crashes.Items.Cast<object>().Count(i => i is CrashItem);

    sealed record CrashItem(string Path) { public override string ToString() => System.IO.Path.GetFileName(Path); }

    void ShowSession()
    {
        _shownCrash = null;
        _what.Text = "This session:";
        _text.Text = AppHost.Log.Text.Replace("\n", Environment.NewLine);
        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
    }

    void ShowCrash()
    {
        if (_crashes.SelectedItem is not CrashItem c) return;
        try
        {
            _text.Text = File.ReadAllText(c.Path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _shownCrash = c.Path;
            _what.Text = "Crash report: " + System.IO.Path.GetFileName(c.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _text.Text = "The report cannot be read: " + e.Message; }
    }

    public void Refresh2()
    {
        ShowSession();
        _crashes.Items.Clear();
        try
        {
            if (Directory.Exists(AppHost.Log.Folder))
                foreach (var f in Directory.GetFiles(AppHost.Log.Folder, "crash-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Take(50))
                    _crashes.Items.Add(new CrashItem(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _crashes.Items.Add("(the log folder cannot be read: " + e.Message + ")"); }
        if (_crashes.Items.Count == 0) _crashes.Items.Add("(none - good)");
    }

    public void SelectCrashForTest(int index) { if (index < _crashes.Items.Count) _crashes.SelectedIndex = index; }
}
