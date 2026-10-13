using System.Drawing.Drawing2D;
using RwEditor.Core;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;
using RwEditor.Core.Validation;
using CoreLayout = RwEditor.Core.Generation.Layout;

namespace RwEditor.App;

/// <summary>
/// "Start units / spawn groups" (E4): builds the groups of the match-start menu's 4th section. Pick units from a grid of pictures, name the group, choose who
/// gets it and whether it is on by default, see where the units will stand, hide or delete the default entries, and apply it all to the copy (Core
/// <see cref="RwEditor.Core.Apply.GroupsPipeline"/>: validated, backed up, one write, undoable). Everything shown here is checked live by the same rules the
/// apply uses (<see cref="SettingsValidator"/>), so what can be clicked can be applied.
/// </summary>
sealed class GroupsView : UserControl
{
    readonly Func<EditController> _edit;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, IntegralHeight = false };
    readonly Button _new = new() { Text = "New group", AutoSize = true };
    readonly Button _delete = new() { Text = "Delete group", AutoSize = true };
    readonly FlowLayoutPanel _defaults = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly Button _revertMenu = new() { Text = "Put this menu back to the defaults", AutoSize = true };

    readonly Panel _right = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 4, 6, 4) };
    readonly TextBox _name = new() { Width = 300, MaxLength = 80 };
    readonly Label _nameHint = new() { AutoSize = true, ForeColor = Color.FromArgb(150, 80, 0), MaximumSize = new Size(520, 0) };
    readonly CheckBox _enabled = new() { Text = "Switched on (shows in the game's menu)", AutoSize = true };
    readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    readonly CheckBox _defaultOn = new() { Text = "Already switched on when a match starts", AutoSize = true };
    readonly Label _unitsTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    readonly FlowLayoutPanel _tiles = new() { Height = 150, Width = 540, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
    readonly RingPreview _ring = new() { Width = 300, Height = 300 };
    readonly Label _handHint = new() { AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Color.FromArgb(70, 80, 90) };
    readonly Button _handBtn = new() { AutoSize = true, Text = "Place by hand" };
    readonly Label _gameText = new() { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Color.FromArgb(60, 60, 60) };

    readonly TextBox _search = new() { Width = 200, PlaceholderText = "Find a unit..." };
    readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly ListView _pick = new() { View = View.LargeIcon, Width = 780, Height = 340, MultiSelect = true, HideSelection = false, ShowItemToolTips = true };
    readonly ImageList _images = new() { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth32Bit };
    readonly Button _add = new() { Text = "Add selected to the group", AutoSize = true };

    readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(8, 0, 0, 0) };
    readonly Button _apply = new() { Text = "Apply to my copy", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = false };
    readonly Button _discard = new() { Text = "Discard my changes here", AutoSize = true, Enabled = false };
    readonly Label _banner = new() { Dock = DockStyle.Top, Height = 38, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), BackColor = Color.FromArgb(255, 236, 160), Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
    readonly System.Windows.Forms.Timer _check = new() { Interval = 300 };
    readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 300 };

    ModSession? _session;
    ThumbImages? _thumbs;
    ModContext? _ctx;
    EditorSettings _work = EditorSettings.Default();
    string _savedSig = "";
    List<UnitPick> _units = new();
    SpawnGroup? _current;
    bool _loading;
    readonly Dictionary<string, ComboBox> _optionBoxes = new(StringComparer.Ordinal);

    sealed record UnitPick(string Name, string RelPath, string Title, string Area, string AreaLabel, bool IsBuilding, string Kind, string Description);
    sealed record AreaChoice(string? Id, string Label) { public override string ToString() => Label; }
    sealed record KindChoice(int Id, string Label) { public override string ToString() => Label; }
    sealed record ModeChoice(GroupMode Mode, string Label) { public override string ToString() => Label; }
    sealed record StateChoice(OptionState State, string Label) { public override string ToString() => Label; }

    /// <summary>The settings being edited (tests).</summary>
    public EditorSettings Working => _work;
    public IReadOnlyList<SpawnGroup> Groups => _work.SpawnGroups;
    public bool CanApply { get { EnsureBuilt(); return _apply.Enabled; } }
    public string StatusText { get { EnsureBuilt(); return _status.Text; } }
    internal ListView Picker { get { EnsureBuilt(); return _pick; } }
    public int PickerCount { get { EnsureBuilt(); return _pick.Items.Count; } }
    public bool Dirty => Signature(_work) != _savedSig;

    /// <summary>True when the Menu options page has changes that are not applied yet: an apply here would reload both pages and lose them (set by the window).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? OtherPageDirty { get; set; }

    public GroupsView(Func<EditController> edit)
    {
        _edit = edit;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        _pick.LargeImageList = _images;
        _mode.Items.Add(new ModeChoice(GroupMode.FollowToggle, "Like the default extras: follows the match's \"Extra units: human players only\" switch"));
        _mode.Items.Add(new ModeChoice(GroupMode.HumansOnly, "Human players only, always (the computer players never get it)"));
        _kind.Items.AddRange(new object[] { new KindChoice(0, "Buildings and units"), new KindChoice(1, "Buildings only"), new KindChoice(2, "Mobile units only") });
        _kind.SelectedIndex = 0;

        // ---- left column
        var left = new Panel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(0, 0, 8, 0) };
        var groupButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        groupButtons.Controls.AddRange(new Control[] { _new, _delete });
        var defaultsBox = new GroupBox { Text = "Default entries of this menu", Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        defaultsBox.Controls.Add(_defaults);
        var defaultsNote = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = true, MaximumSize = new Size(320, 0), Padding = new Padding(0, 4, 0, 4),
            Text = "Hidden = switched off here but still listed in the editor. Deleted = gone from the menu for good (the game files stay, so you can put it back with the revert button).",
        };
        var leftTitle = new Label { Text = "Your groups", Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        left.Controls.Add(_list);
        left.Controls.Add(groupButtons);
        left.Controls.Add(_revertMenu);
        _revertMenu.Dock = DockStyle.Bottom;
        left.Controls.Add(defaultsNote);
        left.Controls.Add(defaultsBox);
        left.Controls.Add(leftTitle);

        // ---- right column (top to bottom)
        var nameRow = Row(Lbl("Name of the group:"), _name);
        var modeRow = Row(Lbl("Who gets it:"), _mode);
        var unitsRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        unitsRow.Controls.Add(_tiles);
        var ringBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(10, 0, 0, 0) };
        ringBox.Controls.Add(new Label { Text = "Where they will stand", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
        ringBox.Controls.Add(_ring);
        ringBox.Controls.Add(_handHint);
        ringBox.Controls.Add(_handBtn);
        unitsRow.Controls.Add(ringBox);
        var filterRow = Row(Lbl("Find:"), _search, _kind, _area);
        var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stack.Controls.AddRange(new Control[]
        {
            nameRow, _nameHint, _enabled, modeRow, _defaultOn,
            _unitsTitle, unitsRow, _gameText,
            new Label { Text = "Add units", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) },
            new Label { Text = "Double-click a picture (or select several and click the button). Hover for the name and what it does. Adding a unit that is already in the group adds one more of it.", AutoSize = true, MaximumSize = new Size(780, 0) },
            filterRow, _pick, _add,
        });
        _right.Controls.Add(stack);

        // ---- bottom bar
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(240, 242, 246) };
        var barButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 8, 0) };
        barButtons.Controls.Add(_apply);
        barButtons.Controls.Add(_discard);
        bar.Controls.Add(_status);
        bar.Controls.Add(barButtons);

        Controls.Add(_right);
        Controls.Add(left);
        Controls.Add(bar);
        Controls.Add(_banner);

        _tip.SetToolTip(_name, "The name shown on the group's button in the match-start menu (letters, digits, space and - _ . ! ? + ' & ( )).");
        _tip.SetToolTip(_defaultOn, "When on, the group is already ticked when the match menu opens; players can still switch it off.");
        _tip.SetToolTip(_enabled, "Off = the group stays in the editor but is not put into the game.");
        _tip.SetToolTip(_apply, "Writes the groups into your copy (a backup is made first; History > Undo puts everything back).");
        _tip.SetToolTip(_discard, "Throws away what you changed here since the last apply.");

        _list.DrawItem += DrawGroupItem;
        _list.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "showing the group", () => SelectGroup(_list.SelectedItem as SpawnGroup));
        _new.Click += (_, _) => AppHost.SafeRun(FindForm(), "adding a group", NewGroup);
        _delete.Click += (_, _) => AppHost.SafeRun(FindForm(), "deleting a group", DeleteGroup);
        _name.TextChanged += (_, _) => { if (!_loading && _current != null) { SetName(_name.Text); } };
        _enabled.CheckedChanged += (_, _) => Edit(g => g.Enabled = _enabled.Checked);
        _defaultOn.CheckedChanged += (_, _) => Edit(g => g.DefaultOn = _defaultOn.Checked);
        _mode.SelectedIndexChanged += (_, _) => Edit(g => g.Mode = (_mode.SelectedItem as ModeChoice)?.Mode ?? g.Mode);
        _search.TextChanged += (_, _) => FillPicker();
        _kind.SelectedIndexChanged += (_, _) => FillPicker();
        _area.SelectedIndexChanged += (_, _) => FillPicker();
        _pick.DoubleClick += (_, _) => AppHost.SafeRun(FindForm(), "adding units", AddSelected);
        _add.Click += (_, _) => AppHost.SafeRun(FindForm(), "adding units", AddSelected);
        _ring.DotMoved += (d, x, y, final) => AppHost.SafeRun(FindForm(), "moving a unit", () => OnDotMoved(d, x, y, final));
        _handBtn.Click += (_, _) => AppHost.SafeRun(FindForm(), "changing the placement", ToggleHandPlacement);
        _tip.SetToolTip(_handBtn, "By hand: the units stay exactly where you drag them. Automatic: the editor lines them up on the ring for you.");
        _revertMenu.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the menu back", RevertMenu);
        _discard.Click += (_, _) => AppHost.SafeRun(FindForm(), "discarding changes", Discard);
        _apply.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "applying the groups", ApplyAsync);
        _check.Tick += (_, _) => { _check.Stop(); AppHost.SafeRun(FindForm(), "checking the groups", () => CheckNow()); };
        Disposed += (_, _) => { _check.Dispose(); _images.Dispose(); };
        SetEnabledState(false);
    }

    static Label Lbl(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(0, 5, 4, 0) };

    static FlowLayoutPanel Row(params Control[] c)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        f.Controls.AddRange(c);
        return f;
    }

    static string Signature(EditorSettings s)
    {
        var c = s.Clone();
        c.StatEdits.Clear(); c.PendingStatEdits.Clear(); c.BaseMod = null;
        return SettingsJson.ContentHash(c);
    }

    // ------------------------------------------------------------------------------------------------------------ session
    /// <summary>Loads the copy's groups and the picker. Called when a copy is opened or reloaded (and when the page is shown).</summary>
    public void Bind(ModSession? session, ThumbImages? thumbs)
    {
        _session = session;
        _thumbs = thumbs;
        _built = false;
        if (session != null) { _work = session.Settings.Clone(); _savedSig = Signature(_work); }
        // the page itself (346 pictures, the tiles, the checks) is built when it is shown: a reload after every save must not freeze the window
        if (session == null) EnsureBuilt();
    }

    bool _built;

    /// <summary>The page has been built for the open copy (it is built when the page is shown, not before).</summary>
    public bool IsBuilt => _built;

    // A control without a parent already reports Visible = true, so "shown" cannot be told from VisibleChanged alone: the page is shown by adding it to the
    // window's content area (MainForm.ShowPage), which is what builds it.
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null) EnsureBuilt();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && Parent != null) EnsureBuilt();
    }

    /// <summary>Builds the page for the bound copy once (no-op when built).</summary>
    void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        var session = _session;
        _ctx = null;
        _current = null;
        _loading = true;
        _list.Items.Clear();
        _tiles.Controls.Clear();
        _defaults.Controls.Clear();
        _optionBoxes.Clear();
        _pick.Items.Clear();
        _images.Images.Clear();
        _loading = false;
        if (session == null) { _banner.Visible = false; SetEnabledState(false); _status.Text = "No copy is open."; return; }

        _banner.Visible = !session.CanEdit;
        _banner.Text = session.CanEdit ? "" : "Looking only: " + (session.EditBlockedReason ?? "this folder cannot be changed.");
        try
        {
            var support = EditorSupport.Load(session.Root);
            _ctx = ModContext.Build(session.Model.Snapshot, support);
        }
        catch (EditorRefusalException e)
        {
            _status.Text = "The groups cannot be built here: " + e.Message;
            SetEnabledState(false);
            return;
        }
        BuildUnits(session);
        BuildDefaults();
        FillList(null);
        SetEnabledState(session.CanEdit);
        FillPicker();
        CheckNow();
    }

    /// <summary>The pictures finished loading (they load after the page is built): redraw the picker and the unit tiles with them.</summary>
    public void ThumbsReady()
    {
        if (_session == null || !_built) return;
        _images.Images.Clear();
        FillPicker();
        if (_current != null) RefreshGroupPanel();
    }

    void BuildUnits(ModSession s)
    {
        var roots = s.Families.Items.Where(f => !f.IsBucket && f.Root != null).Select(f => f.Root!).Where(u => !u.Locked).ToList();
        _units = roots.Select(u =>
        {
            var row = s.RowOf(u);
            string desc = "";
            var raw = s.Model.Merge(u).Get("core", "displayDescription")?.Value;
            if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("${", StringComparison.Ordinal))
                desc = raw.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
            return new UnitPick(u.Name, u.RelPath, row.Title, u.Area, row.AreaLabel, row.IsBuilding, row.Kind, desc);
        }).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
        _area.Items.Clear();
        _area.Items.Add(new AreaChoice(null, "All mods"));
        foreach (var a in s.Model.Areas.Areas) _area.Items.Add(new AreaChoice(a.Id, a.Label));
        _area.SelectedIndex = 0;
    }

    void BuildDefaults()
    {
        foreach (var o in _ctx!.HideableSection4Options)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Tag = o.Tag };
            box.Items.AddRange(new object[] { new StateChoice(OptionState.Shown, "Shown"), new StateChoice(OptionState.Hidden, "Hidden"), new StateChoice(OptionState.Deleted, "Deleted") });
            box.SelectedIndex = _work.GetOptionState(o.Tag) switch { OptionState.Hidden => 1, OptionState.Deleted => 2, _ => 0 };
            box.SelectedIndexChanged += (_, _) =>
            {
                if (_loading) return;
                _work.SetOptionState(o.Tag, (box.SelectedItem as StateChoice)?.State ?? OptionState.Shown);
                Changed();
            };
            _optionBoxes[o.Tag] = box;
            row.Controls.Add(new Label { Text = o.Name, Width = 170, AutoEllipsis = true, Padding = new Padding(0, 5, 0, 0) });
            row.Controls.Add(box);
            _defaults.Controls.Add(row);
            _tip.SetToolTip(box, $"\"{o.Name}\" (menu 4): Shown = as shipped. Hidden = its buttons are not shown. Deleted = removed from the menu (kept in the game files).");
        }
        var never = new Label
        {
            AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Color.Gray,
            Text = "\"Fair AI\" and \"Extra units: human players only\" always stay: hiding them would lock them on or break the other entries.",
        };
        _defaults.Controls.Add(never);
    }

    void SetEnabledState(bool on)
    {
        _new.Enabled = on; _delete.Enabled = on && _current != null; _revertMenu.Enabled = on; _defaults.Enabled = on;
        _right.Enabled = on && _current != null;
        _pick.Enabled = on;
        if (!on) { _apply.Enabled = false; _discard.Enabled = false; }
    }

    // ------------------------------------------------------------------------------------------------------------ groups
    void FillList(SpawnGroup? select)
    {
        _loading = true;
        try
        {
            _list.Items.Clear();
            foreach (var g in _work.SpawnGroups.OrderBy(g => g.Slot)) _list.Items.Add(g);
            if (select != null && _list.Items.Contains(select)) _list.SelectedItem = select;
            else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }
        finally { _loading = false; }
        SelectGroup(_list.SelectedItem as SpawnGroup);
    }

    void DrawGroupItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var g = (SpawnGroup)_list.Items[e.Index];
        e.DrawBackground();
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var fore = sel ? SystemColors.HighlightText : g.Enabled ? SystemColors.WindowText : SystemColors.GrayText;
        using var bold = new Font(e.Font ?? Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(g.Name) ? "(no name)" : g.Name, bold, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 10, 20), fore, TextFormatFlags.EndEllipsis);
        int n = g.Units.Sum(u => u.Count);
        string sub = $"{n} unit(s)" + (g.Mode == GroupMode.HumansOnly ? " - human players only" : "") + (g.DefaultOn ? " - on by default" : "") + (g.Enabled ? "" : " - switched off");
        TextRenderer.DrawText(e.Graphics, sub, e.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 22, e.Bounds.Width - 10, 18), sel ? SystemColors.HighlightText : Color.Gray, TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    void NewGroup()
    {
        if (_ctx == null) return;
        if (_work.SpawnGroups.Count >= SettingsValidator.MaxGroups) { Ask.Info(FindForm(), "No more groups", $"At most {SettingsValidator.MaxGroups} groups fit in the menu."); return; }
        int slot = Enumerable.Range(1, SettingsValidator.MaxGroups).First(i => _work.SpawnGroups.All(g => g.Slot != i));
        string name = "New group";
        for (int i = 2; _work.SpawnGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || _ctx.Options.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = "New group " + i;
        var g = new SpawnGroup { Slot = slot, Name = name };
        _work.SpawnGroups.Add(g);
        FillList(g);
        Changed();
        _name.Focus(); _name.SelectAll();
    }

    void DeleteGroup()
    {
        if (_current == null) return;
        if (!Ask.Confirm(FindForm(), "Delete this group?", $"\"{_current.Name}\" is removed from your list. It leaves the game's menu when you apply (History > Undo can bring it back).",
                Array.Empty<string>(), "Delete the group", "Keep it")) return;
        _work.SpawnGroups.Remove(_current);
        _current = null;
        FillList(null);
        Changed();
    }

    void SelectGroup(SpawnGroup? g)
    {
        if (_loading) return;
        _current = g;
        _loading = true;
        try
        {
            _right.Enabled = g != null && _session?.CanEdit == true;
            _delete.Enabled = g != null && _session?.CanEdit == true;
            if (g == null) { _name.Text = ""; _nameHint.Text = ""; _tiles.Controls.Clear(); _unitsTitle.Text = "Select or create a group on the left."; _gameText.Text = ""; _ring.Clear(); UpdateHandState(); return; }
            _name.Text = g.Name;
            _enabled.Checked = g.Enabled;
            _defaultOn.Checked = g.DefaultOn;
            _mode.SelectedIndex = g.Mode == GroupMode.HumansOnly ? 1 : 0;
            _nameHint.Text = "";
        }
        finally { _loading = false; }
        RefreshGroupPanel();
    }

    void SetName(string typed)
    {
        if (_current == null) return;
        string stored = NameRules.Sanitize(typed);
        _current.Name = stored;
        _nameHint.Text = stored == typed ? "" : stored.Length == 0 ? "This name cannot be used: use letters, digits, spaces and - _ . ! ? + ' & ( )." : $"Saved as \"{stored}\": characters the menu cannot show were removed.";
        _list.Invalidate();
        Changed();
    }

    void Edit(Action<SpawnGroup> change)
    {
        if (_loading || _current == null) return;
        change(_current);
        _list.Invalidate();
        Changed();
    }

    // ------------------------------------------------------------------------------------------------------------ units of the group
    void RefreshGroupPanel()
    {
        _tiles.SuspendLayout();
        _tiles.Controls.Clear();
        var g = _current;
        if (g != null)
        {
            foreach (var gu in g.Units) _tiles.Controls.Add(Tile(g, gu));
            int n = g.Units.Sum(u => u.Count);
            int team = SpawnGroupGenerator.ActiveGroups(_work).Sum(x => x.Units.Sum(u => u.Count));
            _unitsTitle.Text = $"Units in this group: {n} of {SettingsValidator.MaxUnitsPerGroup} (all switched-on groups together: {team} of {SettingsValidator.MaxUnitsPerTeam} per team)";
            _gameText.Text = _ctx != null && g.Units.Count > 0 ? "In the game the button's tooltip says:\n" + SpawnGroupGenerator.Description(g, _ctx).Replace("\\n", "\n", StringComparison.Ordinal) : "";
        }
        _tiles.ResumeLayout();
        UpdateRing();
    }

    Control Tile(SpawnGroup g, GroupUnit gu)
    {
        var pick = _units.FirstOrDefault(u => u.Name == gu.Unit);
        var p = new Panel { Width = 96, Height = 132, Margin = new Padding(3), BackColor = SystemColors.Control };
        var pic = new PixelPictureBox { Left = 16, Top = 2, Width = 64, Height = 64, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 44, 52) };
        if (pick != null) pic.Image = _thumbs?.Get(pick.RelPath);
        var name = new Label { Left = 0, Top = 68, Width = 96, Height = 18, Text = pick?.Title ?? gu.Unit, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
        var count = new NumericUpDown { Left = 8, Top = 88, Width = 48, Minimum = 1, Maximum = SettingsValidator.MaxUnitsPerGroup, Value = Math.Clamp(gu.Count, 1, SettingsValidator.MaxUnitsPerGroup) };
        var remove = new Button { Left = 58, Top = 86, Width = 32, Height = 26, Text = "X" };
        var note = new Label { Left = 0, Top = 114, Width = 96, Height = 16, Text = "how many", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5f) };
        p.Controls.AddRange(new Control[] { pic, name, count, remove, note });
        _tip.SetToolTip(pic, TipText(pick, gu.Unit));
        _tip.SetToolTip(name, TipText(pick, gu.Unit));
        _tip.SetToolTip(remove, "Take this unit out of the group.");
        count.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            bool byHand = HandPlacement.IsManual(g);
            gu.Count = (int)count.Value;
            if (byHand && _ctx != null) HandPlacement.Sync(g, _work, _ctx); else gu.Offsets = null;   // units placed by hand keep their spots
            AfterUnitsChanged();
        };
        remove.Click += (_, _) => AppHost.SafeRun(FindForm(), "removing a unit", () => { g.Units.Remove(gu); AfterUnitsChanged(); RefreshGroupPanel(); });
        return p;
    }

    static string TipText(UnitPick? p, string name) =>
        p == null ? name + "\n(not in the list of units this editor can pick)"
        : $"{p.Title}  [{name}]\n{(p.IsBuilding ? "Building" : "Unit")} - {p.Kind} - {p.AreaLabel}" + (p.Description.Length > 0 ? "\n\n" + p.Description : "");

    void AfterUnitsChanged()
    {
        _list.Invalidate();
        if (_current != null)
        {
            int n = _current.Units.Sum(u => u.Count);
            int team = SpawnGroupGenerator.ActiveGroups(_work).Sum(x => x.Units.Sum(u => u.Count));
            _unitsTitle.Text = $"Units in this group: {n} of {SettingsValidator.MaxUnitsPerGroup} (all switched-on groups together: {team} of {SettingsValidator.MaxUnitsPerTeam} per team)";
            _gameText.Text = _ctx != null && _current.Units.Count > 0 ? "In the game the button's tooltip says:\n" + SpawnGroupGenerator.Description(_current, _ctx).Replace("\\n", "\n", StringComparison.Ordinal) : "";
        }
        Changed();
        UpdateRing();
    }

    // ------------------------------------------------------------------------------------------------------------ picker
    void FillPicker()
    {
        if (_loading || _session == null) return;
        string q = _search.Text.Trim();
        string? area = (_area.SelectedItem as AreaChoice)?.Id;
        int kind = (_kind.SelectedItem as KindChoice)?.Id ?? 0;
        _pick.BeginUpdate();
        _pick.Items.Clear();
        foreach (var u in _units)
        {
            if (kind == 1 && !u.IsBuilding) continue;
            if (kind == 2 && u.IsBuilding) continue;
            if (area != null && !u.Area.Equals(area, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.Length > 0 && !u.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && !u.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_images.Images.ContainsKey(u.RelPath))
            {
                var bmp = _thumbs?.Get(u.RelPath);
                _images.Images.Add(u.RelPath, bmp != null ? (Image)bmp.Clone() : GreyBox());
            }
            _pick.Items.Add(new ListViewItem(u.Title) { Tag = u, ImageKey = u.RelPath, ToolTipText = TipText(u, u.Name) });
        }
        _pick.EndUpdate();
    }

    static Bitmap GreyBox()
    {
        var b = new Bitmap(64, 64);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.FromArgb(70, 74, 82));
        return b;
    }

    void AddSelected()
    {
        if (_current == null) { Ask.Info(FindForm(), "No group selected", "Create or select a group first (left side)."); return; }
        var picks = _pick.SelectedItems.Cast<ListViewItem>().Select(i => (UnitPick)i.Tag!).ToList();
        if (picks.Count == 0) { Ask.Info(FindForm(), "Nothing selected", "Click a picture first (hold Ctrl to select several)."); return; }
        int room = SettingsValidator.MaxUnitsPerGroup - _current.Units.Sum(u => u.Count);
        int added = 0;
        bool byHand = HandPlacement.IsManual(_current);
        foreach (var p in picks)
        {
            if (room <= 0) break;
            var gu = _current.Units.FirstOrDefault(u => u.Unit == p.Name);
            if (gu == null) _current.Units.Add(new GroupUnit { Unit = p.Name, Count = 1 });
            else gu.Count++;
            room--; added++;
        }
        // a group placed by hand keeps the spots of its units and gives each new copy a free spot; an automatic group is laid out again
        if (byHand && _ctx != null) HandPlacement.Sync(_current, _work, _ctx);
        else foreach (var gu in _current.Units) gu.Offsets = null;
        if (added < picks.Count) Ask.Info(FindForm(), "The group is full", $"A group holds at most {SettingsValidator.MaxUnitsPerGroup} units; {picks.Count - added} were not added.");
        RefreshGroupPanel();
        AfterUnitsChanged();
    }

    // ------------------------------------------------------------------------------------------------------------ test hooks
    /// <summary>Makes a group like the buttons do (New group, name, units from the picker) without dialogs. Returns the group.</summary>
    public SpawnGroup AddGroupForTest(string name, GroupMode mode, bool defaultOn, params (string Unit, int Count)[] units)
    {
        EnsureBuilt();
        if (_ctx == null) throw new InvalidOperationException("no copy is open");
        int slot = Enumerable.Range(1, SettingsValidator.MaxGroups).First(i => _work.SpawnGroups.All(g => g.Slot != i));
        var g = new SpawnGroup { Slot = slot, Name = NameRules.Sanitize(name), Mode = mode, DefaultOn = defaultOn };
        foreach (var (u, c) in units) g.Units.Add(new GroupUnit { Unit = u, Count = c });
        _work.SpawnGroups.Add(g);
        FillList(g);
        Changed();
        CheckNow();
        return g;
    }

    /// <summary>Sets a default entry's state like its drop-down does.</summary>
    public void SetOptionForTest(string tag, OptionState state)
    {
        EnsureBuilt();
        _optionBoxes[tag].SelectedIndex = state switch { OptionState.Hidden => 1, OptionState.Deleted => 2, _ => 0 };
        CheckNow();
    }

    public int DefaultEntryCount { get { EnsureBuilt(); return _optionBoxes.Count; } }
    public int RingDots { get { EnsureBuilt(); return _ring.DotCount; } }
    public int TileCount { get { EnsureBuilt(); return _tiles.Controls.Count; } }
    public bool NewButtonEnabled { get { EnsureBuilt(); return _new.Enabled; } }

    /// <summary>The list item the picker shows for a unit name, or null (tests).</summary>
    public bool PickerHas(string unit) { EnsureBuilt(); return _pick.Items.Cast<ListViewItem>().Any(i => ((UnitPick)i.Tag!).Name == unit); }

    // ------------------------------------------------------------------------------------------------------------ placing by hand
    /// <summary>A unit of the open group was dragged in the picture (live while dragging, final on release). The first drag turns an automatic group into a by-hand one.</summary>
    void OnDotMoved(RingPreview.Dot d, int x, int y, bool final)
    {
        if (_current == null || _ctx == null || _session?.CanEdit != true || d.UnitIdx < 0 || d.UnitIdx >= _current.Units.Count) return;
        if (!HandPlacement.IsManual(_current)) HandPlacement.MakeManual(_current, _work, _ctx);
        var gu = _current.Units[d.UnitIdx];
        if (gu.Offsets == null || d.Copy < 0 || d.Copy >= gu.Offsets.Count) return;
        gu.Offsets[d.Copy] = new Offset(Math.Clamp(x, -CoreLayout.MaxAbsOffset, CoreLayout.MaxAbsOffset), Math.Clamp(y, -CoreLayout.MaxAbsOffset, CoreLayout.MaxAbsOffset));
        if (final) Changed();
    }

    void ToggleHandPlacement()
    {
        if (_current == null || _ctx == null || _session?.CanEdit != true) return;
        if (HandPlacement.IsManual(_current)) HandPlacement.BackToAuto(_current);
        else HandPlacement.MakeManual(_current, _work, _ctx);
        Changed();
        UpdateRing();
    }

    /// <summary>Moves one copy of a unit of the open group like a drag does (tests). Returns false when it cannot be moved.</summary>
    public bool DragForTest(int unitIndex, int copy, int x, int y)
    {
        EnsureBuilt();
        if (_current == null || unitIndex >= _current.Units.Count) return false;
        OnDotMoved(new RingPreview.Dot(0, 0, false, true, "", 0, unitIndex, copy), x, y, true);
        CheckNow();
        return true;
    }

    public bool PlacedByHand => _current != null && HandPlacement.IsManual(_current);
    public string HandButtonText { get { EnsureBuilt(); return _handBtn.Text; } }
    public void ToggleHandPlacementForTest() { EnsureBuilt(); ToggleHandPlacement(); CheckNow(); }
    public void SelectGroupForTest(SpawnGroup g) { EnsureBuilt(); FillList(g); }
    internal RingPreview Ring => _ring;

    // ------------------------------------------------------------------------------------------------------------ checks, ring, apply
    void Changed()
    {
        if (_loading) return;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Checking...";
        _check.Stop();
        _check.Start();
        _discard.Enabled = Dirty && _session?.CanEdit == true;
        _apply.Enabled = false;
    }

    /// <summary>Runs the same rules as the apply (Core SettingsValidator) and shows the result; tests call it directly.</summary>
    public IReadOnlyList<Finding> CheckNow()
    {
        _check.Stop();
        if (_session == null || _ctx == null) return Array.Empty<Finding>();
        var findings = SettingsValidator.Validate(_work, _ctx);
        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        var warns = findings.Where(f => f.Severity == Severity.Warning).ToList();
        bool dirty = Dirty;
        _discard.Enabled = dirty && _session.CanEdit;
        if (errors.Count > 0)
        {
            _status.ForeColor = Color.FromArgb(160, 20, 20);
            _status.Text = "Cannot apply yet: " + Plain(errors[0]) + (errors.Count > 1 ? $"  (+{errors.Count - 1} more)" : "");
            _tip.SetToolTip(_status, string.Join("\n", errors.Select(Plain)));
            _apply.Enabled = false;
        }
        else
        {
            _status.ForeColor = dirty ? Color.FromArgb(0, 90, 0) : SystemColors.GrayText;
            _status.Text = dirty ? "Ready: click \"Apply to my copy\" to put this into the game's menu." + (warns.Count > 0 ? "  Note: " + Plain(warns[0]) : "")
                                 : _work.SpawnGroups.Count == 0 && _work.StartOptions.Count == 0 ? "Nothing changed. Create a group with \"New group\"." : "Everything shown here is already applied to your copy.";
            _tip.SetToolTip(_status, string.Join("\n", warns.Select(Plain)));
            _apply.Enabled = dirty && _session.CanEdit;
        }
        UpdateRing();
        return findings;
    }

    static string Plain(Finding f) => StatText.Plain(f.Message);

    void UpdateRing()
    {
        if (_ctx == null || _current == null) { _ring.Clear(); UpdateHandState(); return; }
        var shapes = new List<RingPreview.Dot>();
        string? why = null;
        foreach (var g in SpawnGroupGenerator.ActiveGroups(_work).Concat(_current.Enabled ? Array.Empty<SpawnGroup>() : new[] { _current }))
        {
            if (g.Units.Count == 0 || g.Units.Any(u => _ctx.FindUnit(u.Unit) == null)) continue;
            var offs = SpawnGroupGenerator.PlannedOffsets(g, _work, _ctx, out var r);
            if (offs == null) { if (ReferenceEquals(g, _current)) why = r; continue; }
            bool mine = ReferenceEquals(g, _current);
            int k = 0;
            for (int ui = 0; ui < g.Units.Count; ui++)
            {
                var u = g.Units[ui];
                for (int c = 0; c < u.Count && k < offs.Count; c++, k++)
                    shapes.Add(new RingPreview.Dot(offs[k].X, offs[k].Y, _ctx.FindUnit(u.Unit)?.IsBuilding == true, mine, _units.FirstOrDefault(x => x.Name == u.Unit)?.Title ?? u.Unit,
                        HandPlacement.RadiusOf(u.Unit, _ctx), mine ? ui : -1, mine ? c : -1));
            }
        }
        _ring.Editable = _session?.CanEdit == true;
        _ring.SetDots(shapes, why);
        UpdateHandState();
    }

    void UpdateHandState()
    {
        bool has = _current != null && _current.Units.Count > 0 && _ctx != null;
        bool can = has && _session?.CanEdit == true;
        bool byHand = _current != null && HandPlacement.IsManual(_current);
        _handBtn.Visible = _handHint.Visible = has;
        _handBtn.Enabled = can;
        _handBtn.Text = byHand ? "Back to automatic placement" : "Place by hand";
        _handHint.Text = byHand ? "Placed by hand: drag a green dot to move that unit. Circles show each unit's size."
                                : "Placed automatically. Drag a green dot to place the units by hand instead.";
    }

    void RevertMenu()
    {
        if (!Ask.Confirm(FindForm(), "Put this menu back to the defaults?", "All your groups are removed from the list here and every default entry is shown again. Nothing changes in your copy until you click \"Apply to my copy\".",
                Array.Empty<string>(), "Put it back", "Cancel")) return;
        _work.RevertAll();
        _loading = true;
        foreach (var b in _optionBoxes.Values) b.SelectedIndex = 0;
        _loading = false;
        FillList(null);
        Changed();
    }

    void Discard()
    {
        if (_session == null) return;
        if (Dirty && !Ask.Confirm(FindForm(), "Discard your changes here?", "The groups and default entries go back to what is applied to your copy now.", Array.Empty<string>(), "Discard", "Keep working")) return;
        Bind(_session, _thumbs);
        EnsureBuilt();
    }

    async Task ApplyAsync()
    {
        if (_session == null) return;
        CheckNow();
        if (!_apply.Enabled) return;
        if (OtherPageDirty?.Invoke() == true)
        {
            Ask.Info(FindForm(), "Finish the Menu options page first", "The Menu options page has changes that are not applied yet. Applying here re-reads your copy and would lose them. Apply or discard them there first, then come back.");
            return;
        }
        await _edit().ApplyGroupsAsync(_work, tag => _ctx?.FindOption(tag)?.Name ?? tag);
    }
}

/// <summary>
/// A top-down picture of where the units of the groups will stand around the team's start point (the cross). The units of the open group (green) can be
/// dragged with the mouse: <see cref="DotMoved"/> reports the new place in game units while dragging and once more when the button is released. The scale is
/// kept fixed while dragging so the picture does not jump under the mouse; a unit cannot be dragged out of the picture.
/// </summary>
sealed class RingPreview : Control
{
    /// <summary>One unit: place in game units, size (its [core] radius), and for the open group which unit entry and which copy of it it is (-1 for the other groups).</summary>
    public sealed record Dot(int X, int Y, bool Building, bool Current, string Label, double Radius = 0, int UnitIdx = -1, int Copy = -1);

    List<Dot> _dots = new();
    string? _why;
    int _drag = -1;
    int _hover = -1;
    double _frozenMax;
    readonly ToolTip _tip = new() { InitialDelay = 150, ReshowDelay = 50, AutoPopDelay = 8000 };

    /// <summary>Reports a move: the dot as it was, the new place, and whether the mouse button was released (the place is final).</summary>
    public event Action<Dot, int, int, bool>? DotMoved;

    /// <summary>False when the copy cannot be changed: nothing can be dragged.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Editable { get; set; } = true;

    public RingPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(28, 32, 38); ResizeRedraw = true; }

    public void Clear() { _dots = new(); _why = null; _drag = -1; _hover = -1; Invalidate(); }

    public void SetDots(List<Dot> dots, string? why)
    {
        if (_drag >= 0) return;   // never rebuild under the mouse while a unit is being dragged
        _dots = dots; _why = why; _hover = -1; Invalidate();
    }

    public int DotCount => _dots.Count;
    public IReadOnlyList<Dot> Dots => _dots;

    double Extent() => Math.Max(450, _dots.Count == 0 ? 450 : _dots.Max(d => Math.Max(Math.Abs(d.X), Math.Abs(d.Y)))) * 1.1;
    float Scale(double max) => (float)(Math.Min(Width, Height) / 2.0 / max);
    double CurrentMax => _drag >= 0 ? _frozenMax : Extent();

    /// <summary>Where a dot is on the control, and the size of its circle in pixels (at least 4).</summary>
    public (float X, float Y, float R) Screen(Dot d)
    {
        float k = Scale(CurrentMax);
        return (Width / 2f + d.X * k, Height / 2f + d.Y * k, Math.Max(4f, (float)d.Radius * k));
    }

    int HitTest(Point p)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _dots.Count; i++)
        {
            if (!_dots[i].Current || _dots[i].UnitIdx < 0) continue;
            var (x, y, r) = Screen(_dots[i]);
            double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            if (d <= Math.Max(r, 8f) && d < bestD) { best = i; bestD = d; }
        }
        return best;
    }

    (int X, int Y) ToGame(Point p)
    {
        float k = Scale(CurrentMax);
        double visible = Math.Min(Width, Height) / 2.0 / k * 0.97;   // stay inside the picture
        int x = (int)Math.Round(Math.Clamp((p.X - Width / 2f) / k, -visible, visible));
        int y = (int)Math.Round(Math.Clamp((p.Y - Height / 2f) / k, -visible, visible));
        return (Math.Clamp(x, -CoreLayout.MaxAbsOffset, CoreLayout.MaxAbsOffset),
                Math.Clamp(y, -CoreLayout.MaxAbsOffset, CoreLayout.MaxAbsOffset));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Editable) return;
        int i = HitTest(e.Location);
        if (i < 0) return;
        _drag = i;
        _frozenMax = Extent();
        _tip.Hide(this);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0)
        {
            var (x, y) = ToGame(e.Location);
            var old = _dots[_drag];
            if (old.X != x || old.Y != y)
            {
                _dots[_drag] = old with { X = x, Y = y };
                Invalidate();
                DotMoved?.Invoke(old, x, y, false);
            }
            return;
        }
        int h = Editable ? HitTest(e.Location) : -1;
        Cursor = h >= 0 ? Cursors.SizeAll : Cursors.Default;
        if (h != _hover)
        {
            _hover = h;
            if (h >= 0) _tip.Show($"{_dots[h].Label}\nx {_dots[h].X}, y {_dots[h].Y}\nDrag to move", this, e.X + 14, e.Y + 18);
            else _tip.Hide(this);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0) return;
        var d = _dots[_drag];
        _drag = -1;
        Capture = false;
        DotMoved?.Invoke(d, d.X, d.Y, true);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag < 0) { _hover = -1; _tip.Hide(this); Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = Width / 2f, cy = Height / 2f;
        float k = Scale(CurrentMax);
        using (var ring = new Pen(Color.FromArgb(60, 70, 80)))
            foreach (int r in new[] { 150, 300, 450 }) g.DrawEllipse(ring, cx - r * k, cy - r * k, 2 * r * k, 2 * r * k);
        using (var cross = new Pen(Color.FromArgb(220, 190, 40), 2)) { g.DrawLine(cross, cx - 7, cy, cx + 7, cy); g.DrawLine(cross, cx, cy - 7, cx, cy + 7); }
        for (int i = 0; i < _dots.Count; i++)
        {
            var d = _dots[i];
            if (d.Current) continue;
            DrawDot(g, d, Color.FromArgb(120, 130, 140), false);
        }
        for (int i = 0; i < _dots.Count; i++)
        {
            var d = _dots[i];
            if (!d.Current) continue;
            DrawDot(g, d, Color.FromArgb(60, 200, 110), i == _hover || i == _drag);
        }
        if (_why != null) TextRenderer.DrawText(g, "No room: " + _why, Font, new Rectangle(4, Height - 34, Width - 8, 32), Color.FromArgb(255, 150, 120), TextFormatFlags.WordBreak);
        else TextRenderer.DrawText(g, "cross = start point, round = unit, square = building", new Font(Font.FontFamily, 7f), new Rectangle(4, Height - 14, Width - 8, 12), Color.FromArgb(150, 160, 170), TextFormatFlags.Left);
    }

    void DrawDot(Graphics g, Dot d, Color color, bool lit)
    {
        var (x, y, r) = Screen(d);
        // the circle is the unit's real size; the solid dot in the middle is what you grab
        using (var area = new SolidBrush(Color.FromArgb(lit ? 90 : 50, color)))
        using (var edge = new Pen(Color.FromArgb(lit ? 255 : 170, color), lit ? 2 : 1))
        {
            if (d.Building) { g.FillRectangle(area, x - r, y - r, 2 * r, 2 * r); g.DrawRectangle(edge, x - r, y - r, 2 * r, 2 * r); }
            else { g.FillEllipse(area, x - r, y - r, 2 * r, 2 * r); g.DrawEllipse(edge, x - r, y - r, 2 * r, 2 * r); }
        }
        using var b = new SolidBrush(color);
        if (d.Building) g.FillRectangle(b, x - 3, y - 3, 6, 6); else g.FillEllipse(b, x - 3, y - 3, 6, 6);
    }
}
