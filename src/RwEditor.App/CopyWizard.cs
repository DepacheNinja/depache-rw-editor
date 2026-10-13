using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;
using RwEditor.Core.Setup;

namespace RwEditor.App;

/// <summary>
/// "Make my copy" (E2): finds the game, lets the user pick the installed mod, chooses where the copy goes, copies it (Core <see cref="CopyMaker"/>) and
/// explains how to switch it on in the game. Can also pick a copy made before. Every step says in plain words what happens; nothing is overwritten and
/// nothing in the game folder changes unless the user chose the game's mods folder as the place for the copy.
/// </summary>
sealed class CopyWizard : Form
{
    enum Page { Welcome, Game, Source, Existing, Destination, Copying, Done }

    readonly Label _step = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Padding = new Padding(12, 10, 0, 0) };
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4), AutoScroll = true };
    readonly Button _back = new() { Text = "< Back", AutoSize = true };
    readonly Button _next = new() { Text = "Next >", AutoSize = true };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    readonly Label _progressText = new() { AutoSize = true };

    // welcome
    readonly RadioButton _modeNew = new() { Text = "Make a new copy (choose this the first time)", AutoSize = true, Checked = true };
    readonly RadioButton _modeOpen = new() { Text = "Open a copy I made before", AutoSize = true };
    // game
    readonly ListBox _games = new() { Height = 90, Width = 640 };
    readonly Label _gameStatus = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    // source / existing
    readonly ListView _mods = ModList();
    readonly CheckBox _showAll = new() { AutoSize = true };
    readonly Label _modStatus = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    readonly ListView _copies = ModList();
    readonly Label _copyStatus = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    // destination
    readonly TextBox _name = new() { Text = CopyMaker.DefaultName, Width = 260 };
    readonly RadioButton _destDefault = new() { AutoSize = true, Checked = true };
    readonly RadioButton _destGame = new() { AutoSize = true };
    readonly RadioButton _destOther = new() { Text = "Somewhere else:", AutoSize = true };
    readonly TextBox _otherPath = new() { Width = 470 };
    readonly Label _destStatus = new() { AutoSize = true, MaximumSize = new Size(660, 0) };
    // copying / done
    readonly ProgressBar _bar = new() { Width = 640, Height = 22, Minimum = 0, Maximum = 1000 };
    readonly Label _doneText = new() { AutoSize = true, MaximumSize = new Size(680, 0) };

    readonly Dictionary<Page, Control> _pages = new();
    readonly List<Page> _history = new();
    Page _page = Page.Welcome;
    CancellationTokenSource? _cts;
    bool _copying;
    bool _destChosen;
    CopyResult? _result;
    IReadOnlyList<string> _libraries = Array.Empty<string>();

    /// <summary>The copy to open when the wizard closes with OK.</summary>
    public string? ResultFolder { get; private set; }
    public string? GameFolder { get; private set; }

    public CopyWizard()
    {
        Text = "Make my copy - " + AppHost.Title;
        Icon = AppHost.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(760, 560);
        TipFix.Attach(this);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(8) };
        footer.Controls.AddRange(new Control[] { _cancel, _next, _back });
        Controls.Add(_host);
        Controls.Add(footer);
        Controls.Add(_step);

        _back.Click += (_, _) => AppHost.SafeRun(this, "going back", GoBack);
        _next.Click += (_, _) => AppHost.SafeRun(this, "going to the next step", GoNext);
        _cancel.Click += (_, _) => { if (_copying) _cts?.Cancel(); else { DialogResult = DialogResult.Cancel; Close(); } };
        FormClosing += (_, e) => { if (_copying) { e.Cancel = true; _cts?.Cancel(); } };

        BuildPages();
        _name.TextChanged += (_, _) => UpdateDestination();
        _destDefault.CheckedChanged += (_, _) => UpdateDestination();
        _destGame.CheckedChanged += (_, _) => UpdateDestination();
        _destOther.CheckedChanged += (_, _) => UpdateDestination();
        _otherPath.TextChanged += (_, _) => { if (_otherPath.Focused) _destOther.Checked = true; UpdateDestination(); };
        _games.SelectedIndexChanged += (_, _) => UpdateGame();
        _mods.SelectedIndexChanged += (_, _) => UpdateSource();
        _copies.SelectedIndexChanged += (_, _) => UpdateExisting();
        Show(Page.Welcome);
    }

    static ListView ModList()
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Width = 680, Height = 200, ShowItemToolTips = true };
        lv.Columns.Add("Mod", 250);
        lv.Columns.Add("Where", 210);
        lv.Columns.Add("Version", 60);
        lv.Columns.Add("Status", 150);
        return lv;
    }

    static Label P(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 4, 0, 6) };

    static FlowLayoutPanel Column(params Control[] c)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        f.Controls.AddRange(c);
        return f;
    }

    void BuildPages()
    {
        _pages[Page.Welcome] = Column(
            P("The editor never changes the mod you downloaded from the Steam Workshop, and never changes the game itself."),
            P("Instead it makes YOUR OWN COPY of the mod in a folder you choose. Only that copy is ever changed. " +
              "If something goes wrong, the original is untouched and you can simply make a new copy."),
            P("What do you want to do?"),
            _modeNew, _modeOpen);

        var browseGame = new Button { Text = "Browse...", AutoSize = true };
        browseGame.Click += (_, _) => AppHost.SafeRun(this, "choosing the game folder", BrowseGame);
        _pages[Page.Game] = Column(
            P("First the editor needs the Rusted Warfare game folder (the one that holds \"Rusted Warfare.exe\"). It looked in your Steam libraries:"),
            _games, browseGame, _gameStatus);

        var browseSrc = new Button { Text = "The mod is somewhere else: browse...", AutoSize = true };
        browseSrc.Click += (_, _) => AppHost.SafeRun(this, "choosing the mod folder", BrowseSource);
        _pages[Page.Source] = Column(
            P("Which mod should be copied? Pick \"Depache's Mod Tribute\". Only versions made for this editor (v1.0.6 or newer) can be copied."),
            _mods, _showAll, browseSrc, _modStatus);
        _showAll.CheckedChanged += (_, _) => AppHost.SafeRun(this, "listing the mods", FillMods);

        var browseCopy = new Button { Text = "My copy is somewhere else: browse...", AutoSize = true };
        browseCopy.Click += (_, _) => AppHost.SafeRun(this, "choosing your copy", BrowseExisting);
        _pages[Page.Existing] = Column(
            P("Pick the copy you made before (copies made by this editor carry a small file named rweditor-copy.json):"),
            _copies, browseCopy, _copyStatus);

        var browseDest = new Button { Text = "Browse...", AutoSize = true };
        browseDest.Click += (_, _) => AppHost.SafeRun(this, "choosing the folder for the copy", BrowseDestination);
        var otherRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        otherRow.Controls.AddRange(new Control[] { _destOther, _otherPath, browseDest });
        var nameRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        nameRow.Controls.AddRange(new Control[] { new Label { Text = "Name of your copy:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _name });
        _pages[Page.Destination] = Column(
            nameRow,
            P("The name is added to the mod's title, so you can tell your copy from the original in the game's Mods menu."),
            P("Where should your copy go? Nothing is ever overwritten: the folder must be new or empty."),
            _destDefault, _destGame, otherRow, _destStatus);

        _pages[Page.Copying] = Column(P("Copying the mod into your folder. This reads every file once and checks the protected files afterwards."), _bar, _progressText);
        _pages[Page.Done] = Column(_doneText);
    }

    // ------------------------------------------------------------------------------------------------------------ navigation
    void Show(Page p)
    {
        _page = p;
        _host.Controls.Clear();
        _host.Controls.Add(_pages[p]);
        bool open = _modeOpen.Checked;
        _step.Text = p switch
        {
            Page.Welcome => "Make my copy",
            Page.Game => "Step 1: find the game",
            Page.Source => "Step 2: choose the mod to copy",
            Page.Existing => "Step 2: choose your copy",
            Page.Destination => "Step 3: where your copy goes",
            Page.Copying => "Step 4: copying",
            _ => open ? "Your copy is ready to open" : "Done: your copy is ready",
        };
        _back.Enabled = _history.Count > 0 && p is not (Page.Copying or Page.Done);
        _next.Text = p == Page.Done ? "Finish" : p == Page.Destination ? "Make the copy" : "Next >";
        _next.Enabled = p != Page.Copying;
        _cancel.Text = p == Page.Copying ? "Stop copying" : "Cancel";
        _cancel.Enabled = p != Page.Done;
        if (p == Page.Game && _games.Items.Count == 0) DetectGames();
        if (p == Page.Source) FillMods();
        if (p == Page.Existing) FillCopies();
        if (p == Page.Destination) UpdateDestination();
        AppHost.Log.Info("wizard page: " + p);
    }

    void GoBack()
    {
        if (_history.Count == 0) return;
        var prev = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Show(prev);
    }

    void Push(Page next) { _history.Add(_page); Show(next); }

    void GoNext()
    {
        switch (_page)
        {
            case Page.Welcome: Push(Page.Game); break;
            case Page.Game:
                if (SelectedGame == null && _games.Items.Count > 0) { Warn("Pick the game folder in the list, or click Browse."); return; }
                GameFolder = SelectedGame;
                Push(_modeOpen.Checked ? Page.Existing : Page.Source);
                break;
            case Page.Source:
                if (SelectedMod is not ModCandidate src) { Warn("Pick the mod to copy in the list (or browse to it)."); return; }
                if (!src.CanBeSource) { Warn(SourceProblem(src)); return; }
                CopyMaker.CheckSource(src.Path);   // throws a plain-words refusal
                Push(Page.Destination);
                break;
            case Page.Existing:
                if (SelectedCopy is not ModCandidate c) { Warn("Pick your copy in the list (or browse to it)."); return; }
                ResultFolder = CopyMaker.CheckExistingCopy(c.Path);
                Push(Page.Done);
                FillDone(null, c.Path);
                break;
            case Page.Destination:
                if (CopyMaker.NameProblem(_name.Text) is string np) { Warn(np); return; }
                string dest = CopyMaker.CheckDestination(SelectedMod!.Path, DestinationPath());
                var answer = AppHost.Unattended ? DialogResult.OK : MessageBox.Show(this,
                    $"The mod will be copied to:\n\n{dest}\n\nThe original stays exactly as it is. Continue?", "Make my copy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
                Push(Page.Copying);
                _ = RunCopyAsync(SelectedMod.Path, dest, _name.Text.Trim());
                break;
            case Page.Done:
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }

    void Warn(string text) => MessageBox.Show(this, text, "Make my copy", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ------------------------------------------------------------------------------------------------------------ game
    sealed record GameItem(string Path, string Note) { public override string ToString() => $"{Path}   ({Note})"; }

    string? SelectedGame => (_games.SelectedItem as GameItem)?.Path;

    void DetectGames()
    {
        _games.Items.Clear();
        IReadOnlyList<string> found;
        try
        {
            _libraries = SteamLocator.Libraries(SteamLocator.SteamRoots());
            found = SteamLocator.GameFolders(_libraries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppHost.Log.Warn("Steam detection failed: " + e.Message); found = Array.Empty<string>(); }
        if (SteamLocator.IsGameFolder(AppHost.Settings.GameFolder) && !found.Contains(AppHost.Settings.GameFolder!, StringComparer.OrdinalIgnoreCase))
            _games.Items.Add(new GameItem(AppHost.Settings.GameFolder!, "used last time"));
        foreach (var g in found) _games.Items.Add(new GameItem(g, "found in your Steam library"));
        if (_games.Items.Count > 0) _games.SelectedIndex = 0;
        AppHost.Log.Info($"game folders found: {_games.Items.Count}; Steam libraries: {string.Join(" | ", _libraries)}");
        UpdateGame();
    }

    void UpdateGame()
    {
        _gameStatus.ForeColor = SelectedGame != null ? Color.DarkGreen : Color.DarkRed;
        _gameStatus.Text = SelectedGame != null
            ? "Good: this is a Rusted Warfare folder. Click Next."
            : _games.Items.Count == 0
                ? "The game was not found automatically. Click Browse and pick the folder that holds \"Rusted Warfare.exe\" " +
                  "(in Steam: right-click Rusted Warfare > Manage > Browse local files). You can also click Next and pick the mod folder yourself."
                : "Pick one of the folders above.";
    }

    void BrowseGame()
    {
        using var d = new FolderBrowserDialog { Description = "Pick the Rusted Warfare folder (it holds \"Rusted Warfare.exe\")", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!SteamLocator.IsGameFolder(d.SelectedPath)) { Warn("That folder is not a Rusted Warfare game folder (game-lib.jar and assets\\units were not found in it)."); return; }
        _games.Items.Insert(0, new GameItem(d.SelectedPath, "chosen by you"));
        _games.SelectedIndex = 0;
        var lib = SteamLocator.LibraryOfGame(d.SelectedPath);
        if (lib != null && !_libraries.Contains(lib, StringComparer.OrdinalIgnoreCase)) _libraries = _libraries.Append(lib).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------ source / existing
    ModCandidate? SelectedMod => _mods.SelectedItems.Count > 0 ? _mods.SelectedItems[0].Tag as ModCandidate : null;
    ModCandidate? SelectedCopy => _copies.SelectedItems.Count > 0 ? _copies.SelectedItems[0].Tag as ModCandidate : null;

    static string Where(ModCandidate c) => c.Location switch
    {
        ModLocation.Workshop => $"Workshop item {c.WorkshopId}",
        ModLocation.GameModsFolder => "mods folder: " + Path.GetFileName(c.Path),
        _ => c.Path,
    };

    static string SourceProblem(ModCandidate c) => c.IsEditorCopy
        ? "That is already a copy made by this editor. Go back and choose \"Open a copy I made before\"."
        : "That mod cannot be copied: " + (c.EditorSupportProblem ?? "it is not made for this editor") + ".";

    IEnumerable<ModCandidate> Found()
    {
        try { return SteamLocator.FindMods(GameFolder, _libraries.Count > 0 ? _libraries : SteamLocator.Libraries(SteamLocator.SteamRoots())); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppHost.Log.Warn("mod search failed: " + e.Message); return Array.Empty<ModCandidate>(); }
    }

    static ListViewItem Item(ModCandidate c, string status)
    {
        var it = new ListViewItem(c.Title) { Tag = c, ToolTipText = c.Path };
        it.SubItems.Add(Where(c));
        it.SubItems.Add(c.ModVersion ?? "-");
        it.SubItems.Add(status);
        return it;
    }

    List<ModCandidate>? _found;

    void FillMods()
    {
        _found ??= Found().Where(c => !c.IsEditorCopy).OrderByDescending(c => c.CanBeSource).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var keep = SelectedMod?.Path;
        _mods.BeginUpdate();
        _mods.Items.Clear();
        // mods made for this editor first; the others (other mods, or versions older than v1.0.6) only when asked for, greyed, with the reason
        foreach (var c in _found.Where(c => c.CanBeSource || _showAll.Checked))
        {
            var it = Item(c, c.CanBeSource ? "can be copied" : "cannot be copied");
            if (!c.CanBeSource) it.ForeColor = SystemColors.GrayText;
            _mods.Items.Add(it);
            if (keep != null && FolderGuard.SamePath(keep, c.Path)) it.Selected = true;
        }
        _mods.EndUpdate();
        int hidden = _found.Count(c => !c.CanBeSource);
        _showAll.Text = $"Also show the other {hidden} mods found (they cannot be copied)";
        _showAll.Visible = hidden > 0;
        if (_mods.SelectedItems.Count == 0 && _mods.Items.Count > 0 && ((ModCandidate)_mods.Items[0].Tag!).CanBeSource) _mods.Items[0].Selected = true;
        UpdateSource();
    }

    void UpdateSource()
    {
        var c = SelectedMod;
        if (c == null) { _modStatus.ForeColor = Color.DarkRed; _modStatus.Text = _mods.Items.Count == 0 ? "No mods were found. Use the browse button." : "Pick the mod to copy."; return; }
        _modStatus.ForeColor = c.CanBeSource ? Color.DarkGreen : Color.DarkRed;
        _modStatus.Text = c.CanBeSource ? $"Good: \"{c.Title}\" version {c.ModVersion} can be copied.\n{c.Path}" : SourceProblem(c);
    }

    void BrowseSource()
    {
        using var d = new FolderBrowserDialog { Description = "Pick the mod's folder (the one that holds mod-info.txt)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var c = SteamLocator.Describe(d.SelectedPath);
        FillMods();
        _found!.RemoveAll(x => FolderGuard.SamePath(x.Path, c.Path));
        _found.Insert(0, c);
        if (!c.CanBeSource) _showAll.Checked = true;   // refills
        FillMods();
        foreach (ListViewItem it in _mods.Items) it.Selected = FolderGuard.SamePath(((ModCandidate)it.Tag!).Path, c.Path);
        UpdateSource();
    }

    void FillCopies()
    {
        _copies.Items.Clear();
        var list = Found().Where(c => c.IsEditorCopy).ToList();
        foreach (var extra in new[] { AppHost.Settings.CopyFolder, CopyMaker.DefaultFolder })
            if (extra != null && Directory.Exists(extra) && !list.Any(c => FolderGuard.SamePath(c.Path, extra)) && EditorCopyMarker.Exists(extra)) list.Insert(0, SteamLocator.Describe(extra));
        foreach (var c in list) _copies.Items.Add(Item(c, "your copy: " + c.CopyName));
        if (_copies.Items.Count > 0) _copies.Items[0].Selected = true;
        UpdateExisting();
    }

    void UpdateExisting()
    {
        var c = SelectedCopy;
        _copyStatus.ForeColor = c != null ? Color.DarkGreen : Color.DarkRed;
        _copyStatus.Text = c != null ? $"Your copy \"{c.CopyName}\":\n{c.Path}" : _copies.Items.Count == 0 ? "No copies were found. Use the browse button, or go back and make a new copy." : "Pick your copy.";
    }

    void BrowseExisting()
    {
        using var d = new FolderBrowserDialog { Description = "Pick the folder of your copy", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string full = CopyMaker.CheckExistingCopy(d.SelectedPath);   // plain-words refusal when it is not a copy
        var c = SteamLocator.Describe(full);
        var it = Item(c, "your copy: " + c.CopyName);
        _copies.Items.Insert(0, it);
        it.Selected = true;
        UpdateExisting();
    }

    // ------------------------------------------------------------------------------------------------------------ destination
    string DestinationPath() =>
        _destGame.Checked && GameFolder != null ? CopyMaker.GameModsFolderFor(GameFolder, _name.Text)
        : _destOther.Checked ? _otherPath.Text.Trim()
        : CopyMaker.DefaultFolder;

    void UpdateDestination()
    {
        _destDefault.Text = $"In the editor's own folder (practice only: the game will NOT load it):\n{CopyMaker.DefaultFolder}";
        _destGame.Enabled = GameFolder != null;
        _destGame.Text = GameFolder != null
            ? $"In the game's mods folder (recommended: the game loads it, so your changes show up in the game):\n{CopyMaker.GameModsFolderFor(GameFolder, _name.Text)}"
            : "In the game's mods folder (the game folder was not found, so this is not possible yet)";
        if (_page == Page.Destination && !_destChosen)
        {
            _destChosen = true;
            if (GameFolder != null) _destGame.Checked = true;   // the link to the game is the default: a copy the game cannot see changes nothing in the game
        }
        if (_page != Page.Destination || SelectedMod == null) return;
        try
        {
            if (CopyMaker.NameProblem(_name.Text) is string np) throw new EditorRefusalException("COPY_NAME", np);
            string dest = CopyMaker.CheckDestination(SelectedMod.Path, DestinationPath());
            var src = CopyMaker.CheckSource(SelectedMod.Path);
            long? free = CopyMaker.FreeBytes(dest);
            _destStatus.ForeColor = _destGame.Checked ? Color.DarkGreen : Color.FromArgb(170, 70, 0);
            _destStatus.Text = $"Good: the copy will be made in\n{dest}\nIt needs about {src.TotalBytes / (1024 * 1024) + 1} MB" +
                (free is long f ? $"; {f / (1024 * 1024 * 1024.0):0.0} GB are free there." : ".") +
                (_destGame.Checked ? "" : "\nWARNING: the game only loads mods from its own mods folder, so it will NOT see a copy here and your changes will not show up in the game. Choose the game's mods folder above to play with your changes.");
        }
        catch (EditorRefusalException e)
        {
            _destStatus.ForeColor = Color.DarkRed;
            _destStatus.Text = e.Message;
        }
    }

    void BrowseDestination()
    {
        using var d = new FolderBrowserDialog { Description = "Pick an EMPTY folder for your copy (or make a new one)", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _otherPath.Text = d.SelectedPath;
        _destOther.Checked = true;
        UpdateDestination();
    }

    // ------------------------------------------------------------------------------------------------------------ copy
    async Task RunCopyAsync(string source, string dest, string name)
    {
        _copying = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<CopyProgress>(p =>
        {
            _bar.Value = p.BytesTotal > 0 ? (int)Math.Clamp(p.BytesDone * 1000 / p.BytesTotal, 0, 1000) : 0;
            _progressText.Text = $"{p.Step}: {p.FilesDone:N0} of {p.FilesTotal:N0} files ({p.BytesDone / (1024 * 1024):N0} of {p.BytesTotal / (1024 * 1024):N0} MB)";
        });
        AppHost.Log.Info($"copy: {source} -> {dest} (name '{name}')");
        try
        {
            var token = _cts.Token;
            _result = await Task.Run(() => CopyMaker.MakeCopy(source, dest, name, AppHost.Version, progress, token));
            AppHost.Log.Info($"copy done: {_result.Files} files, {_result.Bytes} bytes, {_result.Duration.TotalSeconds:0.0} s; title '{_result.NewTitle}'");
            ResultFolder = _result.Folder;
            _copying = false;
            _history.Clear();
            Show(Page.Done);
            FillDone(_result, _result.Folder);
        }
        catch (Exception e)
        {
            _copying = false;
            AppHost.Report(this, "copying the mod", e);
            _history.RemoveAt(_history.Count - 1);
            Show(Page.Destination);
        }
        finally { _cts.Dispose(); _cts = null; }
    }

    void FillDone(CopyResult? made, string folder, string? titleOverride = null)
    {
        string? game = GameFolder;
        bool inGame = game != null && FolderGuard.IsSameOrInside(folder, SteamLocator.ModsUnitsFolder(game));
        string title = made?.NewTitle ?? titleOverride ?? SteamLocator.ReadModTitle(folder) ?? Path.GetFileName(folder);
        var sb = new System.Text.StringBuilder();
        sb.Append(made != null ? $"Your copy was made ({made.Files:N0} files) in:\n{folder}\n\n" : $"Your copy:\n{folder}\n\n");
        sb.Append("The editor will only ever change this copy. The mod from the Steam Workshop stays as it is.\n\n");
        if (inGame)
        {
            sb.Append("This copy is linked to the game (it is in the game's mods folder). To play with it:\n");
            var link = GameLink.Check(folder, game, SelectedMod?.WorkshopId);
            int n = 0;
            foreach (var step in link.Steps) sb.Append(step.StartsWith("   ", StringComparison.Ordinal) ? step + "\n" : $"  {++n}. {step}\n");
            if (link.Steps.Count == 0) foreach (var pr in link.Problems) sb.Append("  " + pr + "\n");
        }
        else
        {
            sb.Append("This copy is in the editor's own folder, so the game does not see it: Rusted Warfare only loads mods from its own mods folder");
            sb.Append(game != null ? $"\n({SteamLocator.ModsUnitsFolder(game)}).\n" : ".\n");
            sb.Append("That is fine for looking around. To play with your changes later, make a copy in the game's mods folder (Make my copy > Step 3).\n");
        }
        sb.Append("\nOnline games: edited copies cannot join normal multiplayer games (every player needs the same files). Use the Workshop mod for those.");
        _doneText.Text = sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------ screenshots (unattended)
    /// <summary>Puts the wizard on a page with real detected data, for the --screenshot mode. Never copies anything.</summary>
    internal void PrepareForScreenshot(string page)
    {
        switch (page)
        {
            case "welcome": Show(Page.Welcome); break;
            case "game": _history.Add(Page.Welcome); Show(Page.Game); break;
            case "source": DetectGames(); GameFolder = SelectedGame; _history.Add(Page.Game); Show(Page.Source); break;
            case "destination":
                DetectGames(); GameFolder = SelectedGame; Show(Page.Source); _history.Add(Page.Source); Show(Page.Destination); break;
            case "done":
                DetectGames(); GameFolder = SelectedGame; Show(Page.Source);
                Show(Page.Done);
                if (GameFolder != null)
                    FillDone(null, CopyMaker.GameModsFolderFor(GameFolder, _name.Text),
                        SelectedMod != null ? CopyMaker.TitleFor(_name.Text, SelectedMod.Title) : null);
                break;
        }
    }
}
