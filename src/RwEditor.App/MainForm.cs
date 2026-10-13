using System.Diagnostics;
using RwEditor.Core.Imaging;
using RwEditor.Core.Model;
using RwEditor.Core.Setup;

namespace RwEditor.App;

/// <summary>
/// The main window: the always-visible red multiplayer warning, the copy status bar, the navigation tree (per-mod areas + the other sections) and the
/// page on the right. Loading runs in the background; the window paints at once and fills in when the units and then the pictures are ready.
/// </summary>
sealed class MainForm : Form
{
    readonly Label _warning = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 46, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0),
        BackColor = Color.FromArgb(178, 24, 24), ForeColor = Color.White, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        Text = AppHost.MultiplayerWarning,
    };
    readonly Label _copyLabel = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    readonly TreeView _nav = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ShowLines = false, ItemHeight = 26, Font = new Font("Segoe UI", 10f) };
    readonly Panel _content = new() { Dock = DockStyle.Fill };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _timing = new() { TextAlign = ContentAlignment.MiddleRight };

    readonly BrowserView _browser = new() { Dock = DockStyle.Fill };
    readonly LogsView _logs;
    readonly SettingsView _settings;
    readonly HistoryView _history;
    readonly EditController _edit;
    // E3: shown when the open folder cannot be changed (not an editor copy, protected files changed, damaged list of changes)
    readonly Panel _banner = new() { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(255, 236, 160), Visible = false, Padding = new Padding(10, 0, 6, 0) };
    readonly Label _bannerText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
    readonly Button _bannerAside = new() { Text = "Start a new list of changes", AutoSize = true, Visible = false };
    readonly ToolTip _bannerTip = new() { AutoPopDelay = 30000 };
    // game link: says whether the game loads the open copy (it only reads <game>\mods\units), with the fix when it does not
    readonly Panel _link = new() { Dock = DockStyle.Top, Height = 48, Visible = false, Padding = new Padding(10, 0, 6, 0) };
    readonly Label _linkText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
    readonly Button _linkFix = new() { Text = "Put a copy in the game", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
    readonly Button _linkSteps = new() { Text = "Steps in the game", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
    readonly ToolTip _linkTip = new() { AutoPopDelay = 30000 };
    readonly WelcomeView _welcome;
    readonly GroupsView _groups;
    readonly MenuView _menu;

    ModSession? _session;
    ThumbImages? _thumbs;
    bool _syncingNav;
    string? _initialFolder;
    bool _initialIsExplicit;

    /// <summary>Set when the background loads finish (self-test / screenshots wait on it).</summary>
    public TaskCompletionSource<bool> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BrowserView Browser => _browser;
    public ModSession? Session => _session;
    public ThumbImages? Thumbs => _thumbs;
    public string? ThumbReport { get; private set; }

    public MainForm(string? folder, bool explicitFolder)
    {
        _initialFolder = folder;
        _initialIsExplicit = explicitFolder;
        Text = AppHost.Title;
        Icon = AppHost.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterScreen;
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Clamp(work.Width * 9 / 10, 1000, 1700), Math.Clamp(work.Height * 9 / 10, 650, 1000));
        MinimumSize = new Size(900, 600);

        _settings = new SettingsView(MakeCopy, OpenExistingCopy, OpenFolderReadOnly, ChangeGameFolder, RebuildThumbs) { Dock = DockStyle.Fill };
        _welcome = new WelcomeView(MakeCopy, OpenFolderReadOnly) { Dock = DockStyle.Fill };
        _edit = new EditController(this);
        _history = new HistoryView(() => _session, () => _edit) { Dock = DockStyle.Fill };
        _logs = new LogsView(this) { Dock = DockStyle.Fill };
        _groups = new GroupsView(() => _edit) { Dock = DockStyle.Fill };
        _menu = new MenuView(() => _edit) { Dock = DockStyle.Fill };
        // both pages write the same menu files and an apply re-reads both: an apply on one must not throw away unapplied work on the other
        _menu.OtherPageDirty = () => _groups.Dirty;
        _groups.OtherPageDirty = () => _menu.Dirty;
        _browser.SetEditor(_edit);
        _edit.Changed += () => _browser.RefreshEditing();

        var bannerButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        bannerButtons.Controls.Add(_bannerAside);
        bannerButtons.Controls.Add(Ui.Btn("Make my copy...", MakeCopy, this, "making a copy"));
        _bannerAside.Click += (_, _) => AppHost.SafeRun(this, "setting the damaged list aside", SetSettingsAside);
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(bannerButtons);

        var copyBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(240, 242, 246) };
        var copyButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 6, 0) };
        copyButtons.Controls.Add(Ui.Btn("Make my copy...", MakeCopy, this, "making a copy"));
        copyButtons.Controls.Add(Ui.Btn("Open...", OpenExistingCopy, this, "opening a copy"));
        copyBar.Controls.Add(_copyLabel);
        copyBar.Controls.Add(copyButtons);

        var linkButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        linkButtons.Controls.Add(_linkFix);
        linkButtons.Controls.Add(_linkSteps);
        _link.Controls.Add(_linkText);
        _link.Controls.Add(linkButtons);
        _linkFix.Click += (_, _) => _ = AppHost.SafeRunAsync(this, "putting a copy in the game", LinkIntoGameAsync);
        _linkSteps.Click += (_, _) => AppHost.SafeRun(this, "showing the steps", ShowLinkSteps);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(_nav);
        split.Panel2.Controls.Add(_content);
        Load += (_, _) => { try { split.SplitterDistance = LogicalToDeviceUnits(250); } catch (InvalidOperationException) { } };

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_timing);

        Controls.Add(split);
        Controls.Add(_banner);
        Controls.Add(_link);
        Controls.Add(copyBar);
        Controls.Add(_warning);   // added last = docked first = always at the very top
        Controls.Add(statusStrip);

        BuildNav();
        _nav.AfterSelect += (_, e) => AppHost.SafeRun(this, "opening a section", () => OnNav(e.Node));
        _browser.AreaChanged += area => SelectNavArea(area);
        ShowPage(_welcome);
        UpdateCopyLabel();
        Shown += async (_, _) => await AppHost.SafeRunAsync(this, "starting up", StartupAsync);
        FormClosed += (_, _) => { _thumbs?.Dispose(); };
    }

    // ------------------------------------------------------------------------------------------------------------ navigation
    void BuildNav()
    {
        _nav.BeginUpdate();
        _nav.Nodes.Clear();
        var units = _nav.Nodes.Add("units", "All units") ;
        units.Tag = "area:";
        // every mod area the same way: its label from knowledge/mod-areas.json, in file order (no special "main mod" entry)
        foreach (var a in ModAreas.Embedded.Areas)
            units.Nodes.Add("area:" + a.Id, a.Label).Tag = "area:" + a.Id;
        _nav.Nodes.Add("groups", "Start units / spawn groups").Tag = "groups";
        _nav.Nodes.Add("menu", "Menu options").Tag = "menu";
        _nav.Nodes.Add("logs", "Logs").Tag = "logs";
        var settings = _nav.Nodes.Add("settings", "Settings");
        settings.Tag = "settings";
        settings.Nodes.Add("history", "History and undo").Tag = "history";
        units.Expand();
        settings.Expand();
        _nav.EndUpdate();
    }

    void OnNav(TreeNode? node)
    {
        if (_syncingNav || node?.Tag is not string tag) return;
        if (tag.StartsWith("area:", StringComparison.Ordinal))
        {
            if (_session == null) { ShowPage(_welcome); return; }
            ShowPage(_browser);
            string id = tag[5..];
            _browser.SetArea(id.Length == 0 ? null : id);
            return;
        }
        switch (tag)
        {
            case "groups": ShowPage(_groups); break;
            case "menu": ShowPage(_menu); break;
            case "logs": ShowPage(_logs); _logs.Refresh2(); break;
            case "settings": _settings.ShowSession(_session); ShowPage(_settings); break;
            case "history": ShowPage(_history); _history.Refresh2(); break;
        }
    }

    public HistoryView History => _history;
    public GroupsView Groups => _groups;
    public LogsView LogsPage => _logs;
    public MenuView MenuPage => _menu;
    public EditController Editor => _edit;
    public void SetStatus(string text) { _status.Text = text; AppHost.Log.Info("status: " + text); }

    /// <summary>Opens the units page on one unit and one of its values (from the pending list).</summary>
    public void ShowUnitStat(string unit, string statId, string section)
    {
        SelectNav("area:");
        if (_browser.SelectUnit(unit)) _browser.Details.SelectStat(statId, section);
    }

    /// <summary>Re-reads the open copy after a change to its files (apply / undo / revert) and goes back to the same unit and page.</summary>
    public async Task ReloadAsync(string? selectUnit)
    {
        if (_session == null) return;
        bool onHistory = _content.Controls.Count == 1 && _content.Controls[0] == _history;
        bool onGroups = _content.Controls.Count == 1 && _content.Controls[0] == _groups;
        bool onMenu = _content.Controls.Count == 1 && _content.Controls[0] == _menu;
        if (!await OpenAsync(_session.Root, remember: false)) return;
        if (selectUnit != null) _browser.SelectUnit(selectUnit);
        if (onHistory) { ShowPage(_history); SelectNav("history"); _history.Refresh2(); }
        if (onGroups) { ShowPage(_groups); SelectNav("groups"); }
        if (onMenu) { ShowPage(_menu); SelectNav("menu"); }
    }

    void SetSettingsAside()
    {
        if (_session == null || !_session.SettingsDamaged) return;
        if (!Ask.Confirm(this, "Start a new list of changes?", $"The damaged file is renamed (kept, not deleted) and a new, empty list is started. The values already in your copy stay as they are.",
                Array.Empty<string>(), "Rename it and start a new list", "Cancel")) return;
        string aside = _session.SetDamagedSettingsAside();
        AppHost.Log.Warn("damaged settings set aside: " + aside);
        _ = AppHost.SafeRunAsync(this, "opening your copy", () => ReloadAsync(null));
    }

    void SelectNavArea(string? area)
    {
        _syncingNav = true;
        try
        {
            var units = _nav.Nodes["units"]!;
            _nav.SelectedNode = area == null ? units : units.Nodes["area:" + area] ?? units;
        }
        finally { _syncingNav = false; }
    }

    public void SelectNav(string tag)
    {
        foreach (TreeNode n in _nav.Nodes)
        {
            if ((string?)n.Tag == tag) { _nav.SelectedNode = n; return; }
            foreach (TreeNode c in n.Nodes) if ((string?)c.Tag == tag) { _nav.SelectedNode = c; return; }
        }
    }

    void ShowPage(Control page)
    {
        if (_content.Controls.Count == 1 && _content.Controls[0] == page) return;
        _content.SuspendLayout();
        _content.Controls.Clear();
        BusyCursor.Sync(page);
        _content.Controls.Add(page);
        _content.ResumeLayout();
    }

    void UpdateCopyLabel()
    {
        if (_session == null) { _copyLabel.Text = "No copy open yet. Click \"Make my copy\" to start."; return; }
        _copyLabel.Text = _session.IsEditorCopy
            ? $"Your copy \"{_session.Marker?.Name}\": {_session.Root}"
            : $"Looking only (read-only): {_session.Root}   - not a copy made by this editor";
        _copyLabel.ForeColor = _session.IsEditorCopy ? Color.DarkGreen : Color.FromArgb(150, 80, 0);
        // E3: the read-only banner says why nothing can be changed here, with the way out
        _banner.Visible = !_session.CanEdit;
        _bannerAside.Visible = _session.SettingsDamaged;
        _bannerText.Text = _session.CanEdit ? "" : !_session.IsEditorCopy
            ? "Looking only: this folder is not your own copy, so its values cannot be changed here (the editor never changes the original mod). To change values, click \"Make my copy...\"."
            : "Changes are switched off: " + _session.EditBlockedReason;
        _bannerTip.SetToolTip(_bannerText, _session.EditBlockedReason ?? "");
        UpdateLink();
    }

    GameLinkStatus? _linkStatus;

    /// <summary>The Workshop item id when the copy was made from a Workshop folder (…\workshop\content\647960\&lt;id&gt;), else null.</summary>
    static string? WorkshopIdOf(string? sourcePath)
    {
        if (sourcePath == null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(sourcePath, @"workshop[\\/]content[\\/]647960[\\/](\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    void UpdateLink()
    {
        _linkStatus = null;
        if (_session == null || !_session.IsEditorCopy) { _link.Visible = false; return; }
        var st = GameLink.Check(_session.Root, _session.GameDir, WorkshopIdOf(_session.Marker?.SourcePath));
        _linkStatus = st;
        _link.Visible = true;
        _link.BackColor = st.Linked ? Color.FromArgb(214, 240, 214) : Color.FromArgb(255, 205, 205);
        _linkText.ForeColor = st.Linked ? Color.FromArgb(0, 80, 0) : Color.FromArgb(140, 0, 0);
        _linkText.Text = st.Linked
            ? $"Linked to the game: the game loads this copy as \"{st.Title}\". In the game's Mods menu switch it ON and every other Depache's Mod Tribute OFF."
            : "NOT LINKED TO THE GAME: this copy is not in the game's mods folder, so the game cannot load it and your changes will NOT show up in the game.";
        _linkTip.SetToolTip(_linkText, string.Join("\n", st.Linked ? st.Steps : st.Problems));
        _linkFix.Visible = !st.Linked;
        _linkSteps.Visible = st.Linked;
    }

    void ShowLinkSteps()
    {
        if (_linkStatus == null) return;
        Ask.Info(this, "Steps in the game", _linkStatus.Linked ? "Your copy is in the game's mods folder. To play with it:" : "This copy is not linked to the game yet.",
            _linkStatus.Linked ? _linkStatus.Steps : _linkStatus.Problems);
    }

    /// <summary>Puts a copy of the open editor copy (every change so far included) into the game's mods folder and opens that one.</summary>
    async Task LinkIntoGameAsync()
    {
        var s = _session;
        if (s == null || !s.IsEditorCopy) return;
        if (s.GameDir == null) { ChangeGameFolder(); s = _session; if (s?.GameDir == null && AppHost.Settings.GameFolder == null) return; }
        string game = AppHost.Settings.GameFolder ?? s!.GameDir!;
        string dest = GameLink.DestinationFor(game, s!.Marker?.Name ?? "My copy");
        if (!Ask.Confirm(this, "Put a copy in the game",
                $"The game only loads mods from its own mods folder. A copy of your copy (with every change applied so far) will be made in:\n\n{dest}\n\nYour current copy stays where it is, unchanged. From then on the editor works on the copy in the game's folder.",
                Array.Empty<string>(), "Make the copy in the game", "Cancel")) return;
        SetStatus("Copying into the game's mods folder...");
        string made;
        using (BusyCursor.Begin(this))
        {
            try { made = await Task.Run(() => GameLink.CopyIntoGame(s.Root, game)); }
            catch (Exception e) when (e is RwEditor.Core.EditorRefusalException or RwEditor.Core.ApplyFailedException)
            { Ask.Info(this, "Nothing was changed", e.Message, problem: true); return; }
        }
        AppHost.Settings.CopyFolder = made;
        AppHost.SaveSettings();
        if (await OpenAsync(made, remember: true))
            Ask.Info(this, "Linked to the game", "Done. The editor now works on the copy in the game's mods folder.", _linkStatus?.Steps ?? new List<string>());
    }

    // ------------------------------------------------------------------------------------------------------------ loading
    async Task StartupAsync()
    {
        AppHost.Log.Info($"main window shown {AppHost.MsSinceStart:0} ms after Main started ({(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after process start)");
        string? folder = _initialFolder ?? AppHost.Settings.CopyFolder;
        if (folder != null)
        {
            bool ok = await OpenAsync(folder, remember: false);
            if (!ok && !_initialIsExplicit && !AppHost.Unattended)
                MessageBox.Show(this, "Your copy could not be opened (see above). You can make a new copy or open another folder.", AppHost.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!ok) Loaded.TrySetResult(false);
            return;
        }
        Loaded.TrySetResult(false);
        if (!AppHost.Unattended) MakeCopy();   // first run: start the wizard
    }

    /// <summary>Opens a folder: units first (the list appears), pictures in the background. Returns false when it could not be opened (message shown).</summary>
    public async Task<bool> OpenAsync(string folder, bool remember)
    {
        _status.Text = "Reading the mod's files...";
        using var busy = BusyCursor.Begin(this);
        {
            ModSession s;
            try { s = await Task.Run(() => ModSession.Load(folder, null)); }
            catch (Exception e)
            {
                AppHost.Report(this, "opening " + folder, e);
                _status.Text = "Could not open " + folder;
                return false;
            }
            _thumbs?.Dispose();
            bool hadSession = _session != null;
            _session = s;
            _thumbs = new ThumbImages();
            foreach (var w in s.Warnings) AppHost.Log.Warn(w);
            AppHost.Log.Info($"opened {s.Root}: {s.Rows.Count} entries in {s.LoadTime.TotalMilliseconds:0} ms ({s.Families.Stats.Roots} units with {s.Families.Stats.InsideFamilies} entries inside, {s.Families.Stats.SharedParts + s.Families.Stats.HelperParts} shared / helper parts; families {s.FamilyTime.TotalMilliseconds:0} ms); editor copy: {s.IsEditorCopy}{(s.NotCopyReason != null ? " (" + s.NotCopyReason + ")" : "")}; game: {s.GameDir ?? "-"}");
            if (remember && s.IsEditorCopy)
            {
                AppHost.Settings.CopyFolder = s.Root;
                if (s.GameDir != null) AppHost.Settings.GameFolder ??= s.GameDir;
                AppHost.SaveSettings();
            }
            UpdateCopyLabel();
            _settings.ShowSession(s);
            _edit.ResetChecks();
            _browser.Bind(s, _thumbs);
            _groups.Bind(s, _thumbs);
            _menu.Bind(s);
            // the old session (a whole parsed mod, hundreds of MB) is garbage now: collect it in the background instead of letting reloads pile up
            if (hadSession) GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            if (s.CanEdit && s.Settings.PendingStatEdits.Count > 0) _ = AppHost.SafeRunAsync(this, "checking the pending changes", _edit.RefreshChecksAsync);
            ShowPage(_browser);
            SelectNavArea(null);
            _status.Text = $"{s.Families.Stats.Roots:N0} units ({s.Rows.Count:N0} entries) loaded in {s.LoadTime.TotalSeconds:0.0} s. Loading pictures...";
            if (s.Warnings.Count > 0 && !AppHost.Unattended)
                MessageBox.Show(this, string.Join("\n\n", s.Warnings), AppHost.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

            var thumbs = _thumbs;
            Thumbnails.ThumbSet set;
            try { set = await Task.Run(() => Thumbnails.LoadOrBuild(s.Model, s.GameDir)); }
            catch (Exception e)
            {
                AppHost.Log.Error("pictures could not be made", e);
                _status.Text = $"{s.Families.Stats.Roots:N0} units ({s.Rows.Count:N0} entries). The pictures could not be made (see Logs); grey boxes are shown instead.";
                Loaded.TrySetResult(true);
                return true;
            }
            if (!ReferenceEquals(thumbs, _thumbs)) return true;   // another folder was opened meanwhile
            if (!AppHost.NoArt) thumbs.SetThumbs(set.Thumbs);
            if (set.CacheProblem != null) AppHost.Log.Warn(set.CacheProblem);
            ThumbReport = $"{set.Thumbs.Count} thumbnails {(set.FromCache ? "from cache" : "built and cached")} in {set.LoadOrBuildTime.TotalMilliseconds:0} ms (+ {set.SignatureTime.TotalMilliseconds:0} ms input check); cache {set.CachePath}";
            AppHost.Log.Info(ThumbReport);
            _browser.List.Invalidate();
            _groups.ThumbsReady();
            _browser.Requery();
            _status.Text = $"{s.Families.Stats.Roots:N0} units ({s.Rows.Count:N0} entries), {set.Thumbs.Count:N0} pictures ({(set.FromCache ? "from the saved pictures" : "made now and saved")}).";
            _timing.Text = $"ready {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds:0.0} s after start";
            Loaded.TrySetResult(true);
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ actions
    void MakeCopy()
    {
        using var w = new CopyWizard();
        if (w.ShowDialog(this) != DialogResult.OK || w.ResultFolder == null) return;
        if (w.GameFolder != null) AppHost.Settings.GameFolder = w.GameFolder;
        AppHost.Settings.CopyFolder = w.ResultFolder;
        AppHost.SaveSettings();
        _ = AppHost.SafeRunAsync(this, "opening your copy", () => OpenAsync(w.ResultFolder, remember: true));
    }

    void OpenExistingCopy() => MakeCopyOpenMode();

    void MakeCopyOpenMode()
    {
        using var d = new FolderBrowserDialog { Description = "Pick the folder of your copy (made by \"Make my copy\")", UseDescriptionForTitle = true };
        if (AppHost.Settings.CopyFolder != null) d.InitialDirectory = AppHost.Settings.CopyFolder;
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string full = CopyMaker.CheckExistingCopy(d.SelectedPath);
        _ = AppHost.SafeRunAsync(this, "opening your copy", () => OpenAsync(full, remember: true));
    }

    void OpenFolderReadOnly()
    {
        using var d = new FolderBrowserDialog { Description = "Pick any mod folder to look at (nothing in it will be changed)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _ = AppHost.SafeRunAsync(this, "opening a folder", () => OpenAsync(d.SelectedPath, remember: false));
    }

    void ChangeGameFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Pick the Rusted Warfare folder (it holds \"Rusted Warfare.exe\")", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!SteamLocator.IsGameFolder(d.SelectedPath))
        {
            MessageBox.Show(this, "That folder is not a Rusted Warfare game folder (game-lib.jar and assets\\units were not found in it).", AppHost.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        AppHost.Settings.GameFolder = d.SelectedPath;
        AppHost.SaveSettings();
        _settings.ShowSession(_session);
    }

    void RebuildThumbs()
    {
        if (_session == null) return;
        string cache = Thumbnails.CachePath(_session.Root);
        if (File.Exists(cache)) File.Delete(cache);
        AppHost.Log.Info("thumbnail cache deleted: " + cache);
        _ = AppHost.SafeRunAsync(this, "rebuilding the pictures", () => OpenAsync(_session.Root, remember: false));
    }
}
