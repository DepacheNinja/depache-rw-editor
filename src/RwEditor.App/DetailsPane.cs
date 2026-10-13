using System.Drawing.Drawing2D;
using RwEditor.Core.Browse;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// The right-hand pane (E3): big thumbnail, names, family, and the stat EDITOR: Simple / Expand tabs with one row per whitelisted stat (label, now, a
/// "New value" box in the stat's unit, allowed range, the copy's original value with a Reset button, where the value comes from, a note), a help box
/// for the selected row (what it does, if you raise it / lower it, "apply this change to"), and an "All other values" tab that lists every other value
/// read-only with the reason. Locked / formula values are greyed and cannot be typed into.
/// </summary>
sealed class DetailsPane : UserControl
{
    readonly PixelPictureBox _pic = new() { Size = new Size(128, 128), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40, 44, 52) };
    readonly Label _title = new() { AutoSize = false, Height = 30, Dock = DockStyle.Top, Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoEllipsis = true };
    readonly Label _facts = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly Label _family = new()
    {
        Dock = DockStyle.Top, AutoSize = false, Height = 26, Padding = new Padding(6, 0, 6, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        BackColor = Color.FromArgb(232, 240, 252), ForeColor = Color.FromArgb(30, 60, 120),
    };
    readonly Panel _typing = new() { Dock = DockStyle.Top, Height = 36, Visible = false, BackColor = Color.FromArgb(255, 243, 196), Padding = new Padding(8, 3, 6, 3) };
    readonly Label _typingText = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        Text = "Press Enter or click \"Save value\" to keep this value. It is only written to your copy when you click \"Apply changes\".",
    };
    readonly Button _saveValue = new() { Text = "Save value", Dock = DockStyle.Right, Width = 110 };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly StatGrid _simple = new();
    readonly StatGrid _expand = new();
    readonly DataGridView _other = OtherGrid();
    readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };

    // help box for the selected row
    readonly Panel _help = new() { Dock = DockStyle.Bottom, Height = 168, Padding = new Padding(8, 6, 8, 6), BackColor = Color.FromArgb(248, 249, 252) };
    readonly Label _helpTitle = new() { Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoEllipsis = true };
    readonly Label _helpText = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly FlowLayoutPanel _helpButtons = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
    readonly Label _familyLabel = new() { Text = "Apply this change to:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) };
    readonly ComboBox _familyScope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    readonly Button _resetBtn = new() { Text = "Reset to original", AutoSize = true };
    readonly Button _revertBtn = new() { Text = "Revert...", AutoSize = true };
    readonly Button _bulkBtn = new() { Text = "Set for many units...", AutoSize = true, Enabled = false };
    readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 300 };

    ModSession? _session;
    EditController? _edit;
    UnitType? _unit;
    UnitDetails? _details;
    StatRow? _selected;
    StageInfo? _info;

    public TabControl Tabs => _tabs;
    public bool HasPicture => _pic.Image != null;
    public string FamilyText => _family.Text;
    public UnitType? Unit => _unit;
    public StatRow? SelectedStat => _selected;
    public StageInfo? SelectedInfo => _info;
    /// <summary>True while "this unit + its upgrade levels / versions" is chosen for the selected row.</summary>
    public bool FamilyChosen => _familyScope.Visible && _familyScope.SelectedIndex == 1;

    public DetailsPane()
    {
        var head = new Panel { Dock = DockStyle.Top, Height = 140, Padding = new Padding(6) };
        _pic.Dock = DockStyle.Left;
        var text = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 0) };
        text.Controls.Add(_facts);
        text.Controls.Add(_title);
        head.Controls.Add(text);
        head.Controls.Add(_pic);

        var tSimple = new TabPage("Simple") { ToolTipText = "The main numbers: price, health, speed, range, damage, reload. Type a new value in the \"New value\" column." };
        tSimple.Controls.Add(_simple);
        var tExpand = new TabPage("Expand") { ToolTipText = "More detailed numbers. Hover a row (or select it) to see what it does." };
        tExpand.Controls.Add(_expand);
        var tOther = new TabPage("All other values") { ToolTipText = "Every other value of this unit, read-only, with the reason it cannot be changed." };
        tOther.Controls.Add(_other);
        _tabs.TabPages.Add(tSimple);
        _tabs.TabPages.Add(tExpand);
        _tabs.TabPages.Add(tOther);
        _tabs.ShowToolTips = true;
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedIndex == 2) AppHost.SafeRun(FindForm(), "showing the other values", FillOtherNow); };

        _familyScope.Items.AddRange(new object[] { "this unit only", "this unit + its upgrade levels / versions" });
        _familyScope.SelectedIndex = 0;
        _helpButtons.Controls.Add(_familyLabel);
        _helpButtons.Controls.Add(_familyScope);
        _helpButtons.Controls.Add(_resetBtn);
        _helpButtons.Controls.Add(_revertBtn);
        _helpButtons.Controls.Add(_bulkBtn);
        _help.Controls.Add(_helpText);
        _help.Controls.Add(_helpButtons);
        _help.Controls.Add(_helpTitle);
        _tip.SetToolTip(_familyScope, "\"this unit only\" changes only the selected unit. The other choice also gives the same new value to the upgrade levels, water / land forms and nano builds of this unit that have the SAME value now (listed in the choice). Each one is checked before anything is staged.");
        _tip.SetToolTip(_resetBtn, "Puts this value back to what it was when your copy was made: a pending change is removed, an applied change is undone.");
        _tip.SetToolTip(_revertBtn, "Put values back: this value, this unit, a mod area or everything in your copy. Shows exactly what happens first.");
        _tip.SetToolTip(_bulkBtn, "Give this value to many units at once, for example \"Credits produced = 5\" for every building. You choose which ones; nothing is written until you click \"Apply changes\".");
        _bulkBtn.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "setting a value for many units", async () => { if (_edit != null && _selected != null) await _edit.StageBulkAsync(_selected); RefreshRows(); });
        _resetBtn.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "resetting a value", ResetSelectedAsync);
        _revertBtn.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "putting values back", () => _edit?.RevertAsync(_unit, _selected) ?? Task.CompletedTask);

        foreach (var g in new[] { _simple, _expand })
        {
            g.RowSelected += r => AppHost.SafeRun(FindForm(), "showing a value", () => ShowHelp(r));
            g.StageRequested += (r, text) => _ = AppHost.SafeRunAsync(FindForm(), "staging a change", () => StageAsync(r, text));
            g.RemoveRequested += r => AppHost.SafeRun(FindForm(), "removing a pending change", () =>
            {
                if (_session?.PendingFor(_unit!, r.Entry, r.Section) is PendingStatEdit p) _edit?.RemovePending(x => x.SameTarget(p), $"{StatText.StatName(r.Entry, r.Section)} of {_unit!.Name}");
            });
            g.ResetRequested += r => _ = AppHost.SafeRunAsync(FindForm(), "resetting a value", () => _edit != null && _unit != null ? _edit.ResetAsync(_unit, r) : Task.CompletedTask);
        }

        // the "keep it" hint: shown while a new value is typed in a box, so it is obvious how to save it
        _saveValue.Click += (_, _) => AppHost.SafeRun(FindForm(), "saving the value", () => { if (!_simple.CommitTyped()) _expand.CommitTyped(); });
        _typing.Controls.Add(_typingText);
        _typing.Controls.Add(_saveValue);
        foreach (var g in new[] { _simple, _expand }) g.TypingChanged += on => { _typing.Visible = on; };

        Controls.Add(_tabs);
        Controls.Add(_help);
        Controls.Add(_typing);
        Controls.Add(_family);
        Controls.Add(head);
        Controls.Add(_empty);
    }

    public void Bind(ModSession? session, EditController edit) { _session = session; _edit = edit; }

    static DataGridView OtherGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            ShowCellToolTips = true, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };
        g.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        g.Columns.Add("sec", "Section"); g.Columns[0].Width = 120;
        g.Columns.Add("key", "Key"); g.Columns[1].Width = 170;
        g.Columns.Add("val", "Value"); g.Columns[2].Width = 200;
        g.Columns.Add("why", "Why it cannot be changed"); g.Columns[3].Width = 300;
        g.Columns.Add("from", "Comes from"); g.Columns[4].Width = 240;
        return g;
    }

    public void ShowNothing(string text)
    {
        _empty.Text = text;
        _empty.Visible = true;
        _empty.BringToFront();
    }

    public void Show(UnitDetails d, ThumbImages thumbs, string familyLine)
    {
        _empty.Visible = false;
        _tabs.Visible = true;
        _details = d;
        var r = d.Row;
        bool sameUnit = _unit != null && _unit.RelPath.Equals(r.Unit.RelPath, StringComparison.OrdinalIgnoreCase);
        _unit = r.Unit;
        _title.Text = r.Title;
        _family.Text = familyLine;
        _pic.Image = thumbs.Get(r.Unit.RelPath);
        var info = thumbs.Info(r.Unit.RelPath);
        string picNote = info == null ? (AppHost.NoArt ? "picture: not shown in this view" : "picture: loading") : info.Source switch
        {
            RwEditor.Core.Imaging.ThumbSource.Image => "picture: the unit's own sprite",
            RwEditor.Core.Imaging.ThumbSource.MinimapIcon => "picture: the unit's minimap icon (no usable sprite)",
            _ => "picture: a stand-in shape (the unit has no usable sprite)",
        };
        _facts.Text =
            $"Internal name: {r.Name}{(r.DuplicateName ? "  (defined twice in the mod: not editable)" : "")}\n" +
            $"Mod: {r.AreaLabel}\n" +
            $"Kind: {r.Kind}    Folder: {r.Category}\n" +
            $"File: {r.Unit.RelPath}\n" +
            (r.Locked ? "LOCKED: part of the mod's machinery; never changed by the editor\n" : "") +
            picNote;
        var keep = sameUnit ? _selected : null;
        _simple.Fill(d.Simple, _session, r.Unit, _edit);
        _expand.Fill(d.Expand, _session, r.Unit, _edit);
        _otherFilledFor = null;
        if (_tabs.SelectedIndex == 2) FillOtherNow();   // filled only when that tab is looked at (a unit can have 2,000 other values)
        _tabs.TabPages[0].Text = $"Simple ({d.Simple.Count})";
        _tabs.TabPages[1].Text = $"Expand ({d.Expand.Count})";
        _tabs.TabPages[2].Text = $"All other values ({d.Other?.Count ?? 0})";
        var reselect = keep != null ? (_simple.Find(keep.Entry.Id, keep.Section) ?? _expand.Find(keep.Entry.Id, keep.Section)) : null;
        ShowHelp(reselect ?? d.Simple.FirstOrDefault());
    }

    /// <summary>The pending list or its checks changed: refresh the rows of the unit shown (no reload).</summary>
    public void RefreshRows()
    {
        if (_details == null || _unit == null) return;
        _simple.Fill(_details.Simple, _session, _unit, _edit);
        _expand.Fill(_details.Expand, _session, _unit, _edit);
        ShowHelp(_selected);
    }

    UnitDetails? _otherFilledFor;

    void FillOtherNow()
    {
        if (_details == null || ReferenceEquals(_otherFilledFor, _details)) return;
        _otherFilledFor = _details;
        FillOther(_details.Other ?? Array.Empty<OtherValue>());
    }

    void FillOther(IReadOnlyList<OtherValue> rows)
    {
        _other.SuspendLayout();
        _other.Rows.Clear();
        foreach (var o in rows)
        {
            int i = _other.Rows.Add(o.Section, o.Key, o.Value, o.Reason, o.OriginText);
            _other.Rows[i].Cells[3].ToolTipText = o.Reason == "Not editable in this version"
                ? "This value is not in the editor's list of safe numbers yet, so it is shown but cannot be changed here."
                : "Changing this could stop the unit or the mod from working, so the editor keeps it as it is.";
        }
        _other.ResumeLayout();
    }

    /// <summary>A part list row (Shared parts / Helper parts): what it holds, no stats of its own.</summary>
    public void ShowBucket(UnitFamily bucket, int shown)
    {
        _empty.Visible = false;
        _details = null;
        _unit = null;
        _title.Text = bucket.BucketTitle;
        _pic.Image = null;
        _family.Text = $"{shown:N0} of {bucket.Members.Count:N0} entries listed with the current filters (open the row with the arrow)";
        _facts.Text = bucket.Bucket == BucketKind.SharedParts
            ? "Parts and helpers (attached pieces, spawned effects, markers, timers) that units of two or more different families use, " +
              "so they do not belong under one unit. Select one to see its stats and which unit uses it."
            : "Entries the mod's files do not tie to any unit: nothing builds them, they have no in-game name with a picture, and no unit " +
              "attaches or spawns them. Usually templates, markers or leftovers. Nothing is hidden: every entry of the mod is listed somewhere.";
        _simple.Fill(Array.Empty<StatRow>(), _session, null, _edit);
        _expand.Fill(Array.Empty<StatRow>(), _session, null, _edit);
        _otherFilledFor = null;
        FillOther(Array.Empty<OtherValue>());
        _tabs.TabPages[0].Text = "Simple";
        _tabs.TabPages[1].Text = "Expand";
        _tabs.TabPages[2].Text = "All other values";
        ShowHelp(null);
    }

    void ShowHelp(StatRow? r)
    {
        _selected = r;
        _info = null;
        if (r == null || _unit == null || _session == null)
        {
            _helpTitle.Text = "";
            _helpText.Text = _session?.CanEdit == true ? "Select a value to see what it does. Type a new value in the \"New value\" column, then click \"Apply changes\" below." : "";
            _familyLabel.Visible = _familyScope.Visible = false;
            _resetBtn.Enabled = false;
            _revertBtn.Enabled = _session?.CanEdit == true && _unit != null;
            return;
        }
        var e = r.Entry;
        _helpTitle.Text = StatText.StatName(e, r.Section);
        var orig = _session.Original(_unit, e, r.Section);
        string origText = _session.OriginalModel == null ? (_session.IsEditorCopy ? "unknown (copy made by an older editor version)" : "-")
            : orig == null || !orig.IsSet ? "not set (the game's default)" : orig.Editable && orig.Number is double on ? StatText.Value(e, on) : orig.Raw!.Value.Trim();
        var lines = new List<string>
        {
            e.Desc,
            "If you raise it: " + e.Up,
            "If you lower it: " + e.Down,
            $"Now: {r.ValueText}    Original: {origText}    Allowed: {StatText.Range(e)}",
        };
        if (r.OriginText.Length > 0) lines.Add("Comes from: " + r.OriginText);
        if (!r.Editable) lines.Add(r.Note);
        else if (!_session.CanEdit) lines.Add("Changes are switched off for this folder (see the yellow bar at the top).");
        if (!e.Verified) lines.Add("The safe range of this value was not tested in a match yet.");
        _helpText.Text = string.Join("\n", lines);

        bool canFamily = false;
        if (r.Editable && _session.CanEdit)
        {
            _info = StatStaging.Info(_session.Model, _session.Dict, _session.Families, _unit, e, r.Section);
            canFamily = _info.FamilyCandidates.Count > 0;
            if (canFamily)
            {
                string names = string.Join(", ", _info.FamilyCandidates.Take(6).Select(c => c.Unit.Name)) + (_info.FamilyCandidates.Count > 6 ? ", ..." : "");
                _familyScope.Items[1] = $"this unit + {_info.FamilyCandidates.Count} upgrade level(s) / version(s) with the same value ({names})";
                string skipped = _info.FamilySkipped.Count == 0 ? "" : "\nNot included: " + string.Join("; ", _info.FamilySkipped.Take(8).Select(x => $"{x.Unit.Name} ({x.Reason})"));
                _tip.SetToolTip(_familyScope, "Also gives the new value to: " + string.Join(", ", _info.FamilyCandidates.Select(c => $"{c.Unit.Name} ({c.Relation})")) + "." + skipped +
                                              "\nEach one is checked first; if any of them cannot take the value, nothing is copied.");
            }
        }
        if (!canFamily) _familyScope.SelectedIndex = 0;
        _familyLabel.Visible = _familyScope.Visible = canFamily;
        _resetBtn.Enabled = _session.CanEdit && r.Editable && StatGrid.ResetApplies(_session, _unit, r);
        _revertBtn.Enabled = _session.CanEdit;
        _bulkBtn.Enabled = _session.CanEdit && r.Editable && r.Entry.FixedSection != null;
    }

    Task ResetSelectedAsync() => _edit != null && _unit != null && _selected != null ? _edit.ResetAsync(_unit, _selected) : Task.CompletedTask;

    async Task StageAsync(StatRow r, string text)
    {
        if (_edit == null || _unit == null) return;
        bool family = FamilyChosen && _selected != null && _selected.Entry.Id == r.Entry.Id && _selected.Section == r.Section;
        await _edit.StageAsync(_unit, r, text, family);
        RefreshRows();
    }

    // ------------------------------------------------------------------------------------------------------------ test / screenshot hooks
    /// <summary>The row of a stat in the Simple or Expand tab (selects the tab and the row).</summary>
    public StatRow? SelectStat(string statId, string section)
    {
        foreach (var (g, tab) in new[] { (_simple, 0), (_expand, 1) })
        {
            var r = g.Find(statId, section);
            if (r == null) continue;
            _tabs.SelectedIndex = tab;
            g.SelectRow(r);
            ShowHelp(r);
            return r;
        }
        return null;
    }

    /// <summary>Types a value into a stat's "New value" box exactly as a user would (same path as pressing Enter in the cell).</summary>
    public Task TypeValueAsync(string statId, string section, string text, bool family = false)
    {
        var r = SelectStat(statId, section) ?? throw new InvalidOperationException($"{statId} [{section}] is not shown for {_unit?.Name}");
        if (family) _familyScope.SelectedIndex = _familyScope.Visible ? 1 : 0;
        return StageAsync(r, text);
    }

    /// <summary>
    /// Types into the box of a "New value" cell like a user (cell selected, box opened, text typed) and keeps it the way Enter does (same method).
    /// Returns false when the row has no editable cell. The staging itself runs from the grid's own queued call, so callers wait for the pending list.
    /// </summary>
    public bool TypeAndPressEnter(string statId, string section, string text)
    {
        var r = SelectStat(statId, section);
        if (r == null) return false;
        var g = _tabs.SelectedIndex == 1 ? _expand : _simple;
        return g.TypeAndPressEnter(r, text);
    }

    public bool ResetEnabled => _resetBtn.Enabled;
    public string HelpText => _helpTitle.Text + "\n" + _helpText.Text;
    public int EditableRows => _simple.EditableCount + _expand.EditableCount;
    public int PendingRows => _simple.PendingCount + _expand.PendingCount;
}

/// <summary>One stat table (Simple or Expand) of the details pane: a DataGridView with an editable "New value" column for editable rows only.</summary>
sealed class StatGrid : DataGridView
{
    const int ColStat = 0, ColHelp = 1, ColNow = 2, ColNew = 3, ColUnit = 4, ColAllowed = 5, ColOrig = 6, ColReset = 7, ColFrom = 8, ColNote = 9;
    static readonly Color PendingBack = Color.FromArgb(255, 243, 196);
    static readonly Color ChangedFore = Color.FromArgb(20, 80, 170);
    Font? _bold;
    Font? _italic;
    bool _filling;

    public event Action<StatRow>? RowSelected;
    public event Action<StatRow, string>? StageRequested;
    public event Action<StatRow>? RemoveRequested;
    public event Action<StatRow>? ResetRequested;

    public int EditableCount { get; private set; }
    public int PendingCount { get; private set; }

    public StatGrid()
    {
        Dock = DockStyle.Fill;
        AllowUserToAddRows = false; AllowUserToDeleteRows = false; AllowUserToResizeRows = false; AllowUserToOrderColumns = false;
        RowHeadersVisible = false; MultiSelect = false; SelectionMode = DataGridViewSelectionMode.CellSelect;
        EditMode = DataGridViewEditMode.EditOnEnter;
        BackgroundColor = SystemColors.Window; BorderStyle = BorderStyle.None; ShowCellToolTips = true;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
        AddCol("Stat", 165, true);
        AddCol("?", 24, true);
        AddCol("Now", 140, true);
        AddCol("New value", 90, false);
        AddCol("Unit", 95, true);
        AddCol("Allowed", 150, true);
        AddCol("Original", 140, true);
        Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Width = 58, FlatStyle = FlatStyle.System });
        AddCol("Comes from", 300, true);
        AddCol("Note", 320, true);
        Columns[ColHelp].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        Columns[ColHelp].DefaultCellStyle.ForeColor = Color.FromArgb(30, 90, 170);
        Columns[ColNew].DefaultCellStyle.BackColor = Color.FromArgb(252, 252, 255);
        Columns[ColNew].HeaderCell.ToolTipText = "Type the new value here and press Enter. Nothing is written until you click \"Apply changes\".";
        Columns[ColOrig].HeaderCell.ToolTipText = "The value when your copy was made.";
        foreach (DataGridViewColumn c in Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

        CellEndEdit += OnEndEdit;
        CellContentClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == ColReset && Rows[e.RowIndex].Cells[ColReset] is DataGridViewButtonCell && Rows[e.RowIndex].Tag is StatRow r) ResetRequested?.Invoke(r); };
        CurrentCellChanged += (_, _) => { if (!_filling && CurrentRow?.Tag is StatRow r) RowSelected?.Invoke(r); };
        DataError += (_, e) => { e.ThrowException = false; AppHost.Log.Warn("stat grid: " + e.Exception?.Message); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && CurrentCell?.ColumnIndex == ColNew && CurrentRow?.Tag is StatRow r) { e.Handled = true; RemoveRequested?.Invoke(r); } };
    }

    void AddCol(string header, int width, bool readOnly)
    {
        var c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, ReadOnly = readOnly };
        Columns.Add(c);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold?.Dispose(); _italic?.Dispose(); }
        base.Dispose(disposing);
    }

    public StatRow? Find(string statId, string section)
    {
        foreach (DataGridViewRow row in Rows)
            if (row.Tag is StatRow r && r.Entry.Id.Equals(statId, StringComparison.OrdinalIgnoreCase) && r.Section.Equals(section, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    public void SelectRow(StatRow r)
    {
        foreach (DataGridViewRow row in Rows)
            if (ReferenceEquals(row.Tag, r) || (row.Tag is StatRow x && x.Entry.Id == r.Entry.Id && x.Section == r.Section))
            {
                _filling = true;
                try { CurrentCell = row.Cells[ColStat]; } finally { _filling = false; }
                return;
            }
    }

    /// <summary>Whether "Reset" does something for this row: a pending change, a recorded change, or a value that differs from the copy's original.</summary>
    public static bool ResetApplies(ModSession s, UnitType u, StatRow r)
    {
        if (s.PendingFor(u, r.Entry, r.Section) != null) return true;
        if (s.Settings.StatEdits.Any(e => e.Unit == u.Name && e.StatId.Equals(r.Entry.Id, StringComparison.OrdinalIgnoreCase) && e.Section.Equals(r.Section, StringComparison.OrdinalIgnoreCase))) return true;
        var o = s.Original(u, r.Entry, r.Section);
        return o != null && o.IsSet && o.Editable && !string.Equals(o.Raw?.Value.Trim(), r.Ref?.Raw?.Value.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The display text (user's unit) of a pending value.</summary>
    public static string PendingDisplay(StatEntry e, string value) =>
        StatPatcher.ParseNew(e, value, out double n, out _, out _) ? StatText.Num(StatText.ToDisplay(e, n)) : value;

    public void Fill(IReadOnlyList<StatRow> rows, ModSession? s, UnitType? u, EditController? edit)
    {
        _filling = true;
        SuspendLayout();
        try
        {
            if (IsCurrentCellInEditMode) CancelEdit();
            _bold ??= new Font(Font, FontStyle.Bold);
            _italic ??= new Font(Font, FontStyle.Italic);
            Rows.Clear();
            EditableCount = 0; PendingCount = 0;
            bool canEdit = s?.CanEdit == true;
            foreach (var r in rows)
            {
                var e = r.Entry;
                var pending = s != null && u != null ? s.PendingFor(u, e, r.Section) : null;
                var orig = s != null && u != null ? s.Original(u, e, r.Section) : null;
                string origText = s?.OriginalModel == null ? "-" : orig == null || !orig.IsSet ? "not set" : orig.Editable && orig.Number is double on ? StatText.Value(e, on) : orig.Raw!.Value.Trim();
                bool changed = orig != null && orig.IsSet && !string.Equals(orig.Raw?.Value.Trim(), r.Ref?.Raw?.Value.Trim(), StringComparison.Ordinal);
                bool editable = r.Editable && canEdit;
                string newText = pending != null ? PendingDisplay(e, pending.Value) : editable ? "" : r.Editable ? "-" : "locked";
                string note = pending != null
                    ? "Pending" + (pending.Mode == "template" ? " (every unit that shares it)" : "") + (pending.FromFamilyOf != null ? $" (copied from {pending.FromFamilyOf})" : "") +
                      (edit?.CheckText(pending) is string ct ? ": " + ct : ": not written yet")
                    : r.Note.Length > 0 ? r.Note
                    : changed ? "Changed from the original" : "";
                int i = Rows.Add(StatText.StatName(e, r.Section), "?", r.ValueText, newText, StatText.InputUnit(e), StatText.Range(e), origText, "", r.OriginText, note);
                var row = Rows[i];
                row.Tag = r;
                string tip = r.Tooltip + (r.OriginText.Length > 0 ? "\n\nComes from: " + r.OriginText : "") + (r.Editable ? "" : "\n\n" + r.Note);
                row.Cells[ColStat].ToolTipText = tip;
                row.Cells[ColHelp].ToolTipText = tip;
                row.Cells[ColNow].ToolTipText = tip;
                row.Cells[ColNew].ToolTipText = editable
                    ? $"Type the new value in {StatText.InputUnit(e)} ({StatText.Range(e)}) and press Enter. Delete removes a pending change."
                    : r.Editable ? "Changes are switched off for this folder (see the yellow bar at the top)." : r.Note;
                row.Cells[ColNew].ReadOnly = !editable;
                if (s != null && u != null && editable && ResetApplies(s, u, r))
                {
                    row.Cells[ColReset].Value = pending != null ? "Undo" : "Reset";
                    row.Cells[ColReset].ToolTipText = pending != null ? "Remove this pending change." : $"Back to the original value ({origText}).";
                }
                else row.Cells[ColReset] = new DataGridViewTextBoxCell { Value = "" };
                if (!r.Editable)
                {
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                    row.Cells[ColNew].Style.Font = _italic;
                }
                else EditableCount++;
                if (pending != null)
                {
                    PendingCount++;
                    row.DefaultCellStyle.BackColor = PendingBack;
                    row.Cells[ColNew].Style.BackColor = PendingBack;
                    row.Cells[ColNew].Style.Font = _bold;
                }
                if (changed) { row.Cells[ColNow].Style.ForeColor = ChangedFore; row.Cells[ColNow].Style.Font = _bold; }
                if (!r.IsSet) row.Cells[ColNow].Style.ForeColor = SystemColors.GrayText;
            }
        }
        finally
        {
            ResumeLayout();
            _filling = false;
        }
    }

    /// <summary>Raised when the box of a "New value" cell gets or loses typed text (the pane shows a "Save value" hint while there is text).</summary>
    public event Action<bool>? TypingChanged;

    TextBox? _editor;
    bool _enterHandled;
    string _lastTyped = "";

    void OnEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        TypingChanged?.Invoke(false);
        _editor = null;
        if (_enterHandled) return;   // Enter / "Save value" already handled this edit
        if (_filling || e.RowIndex < 0 || e.ColumnIndex != ColNew || Rows[e.RowIndex].Tag is not StatRow r) return;
        // the typed text: the cell's value, or (when the cell did not take it) what the box held last
        string typed = (Rows[e.RowIndex].Cells[ColNew].Value as string ?? "").Trim();
        if (typed.Length == 0 && _lastTyped.Length > 0) typed = _lastTyped.Trim();
        Dispatch(e.RowIndex, r, typed, (Rows[e.RowIndex].Cells[ColNew].Tag as string) ?? "");
    }

    /// <summary>Stages (or removes) what was typed, after the edit has fully ended (the cell is refilled afterwards).</summary>
    void Dispatch(int rowIndex, StatRow r, string typed, string shown)
    {
        BeginInvoke(new Action(() =>
        {
            if (typed.Length == 0) { if (Rows.Count > rowIndex && Rows[rowIndex].DefaultCellStyle.BackColor == PendingBack) RemoveRequested?.Invoke(r); return; }
            if (typed == shown) return;
            StageRequested?.Invoke(r, typed);
        }));
    }

    /// <summary>
    /// Keeps the value typed in the open box (Enter, or the "Save value" button): reads the box itself, ends the edit and stages the value. Returns false
    /// when no box is open. (Enter used to end the edit without staging anything: the edit-ended event does not always carry the typed text.)
    /// </summary>
    public bool CommitTyped()
    {
        if (!IsCurrentCellInEditMode || _editor == null || CurrentCell is not { ColumnIndex: ColNew } cell || cell.OwningRow?.Tag is not StatRow r) return false;
        string typed = _editor.Text.Trim();
        string shown = cell.Tag as string ?? "";
        int row = cell.RowIndex;
        _enterHandled = true;
        try { if (!EndEdit()) CancelEdit(); }
        finally { _enterHandled = false; }
        Dispatch(row, r, typed, shown);
        return true;
    }

    /// <summary>Test hook: opens the box of the row's "New value" cell, puts the text in it and presses Enter through <see cref="CommitTyped"/>.</summary>
    public bool TypeAndPressEnter(StatRow r, string text)
    {
        foreach (DataGridViewRow row in Rows)
        {
            if (row.Tag is not StatRow x || x.Entry.Id != r.Entry.Id || x.Section != r.Section) continue;
            if (row.Cells[ColNew].ReadOnly) return false;
            CurrentCell = row.Cells[ColNew];
            if (!BeginEdit(true) || _editor == null) return false;
            _editor.Text = text;
            return CommitTyped();
        }
        return false;
    }

    protected override void OnCellBeginEdit(DataGridViewCellCancelEventArgs e)
    {
        base.OnCellBeginEdit(e);
        _lastTyped = "";
        if (e.RowIndex >= 0 && e.ColumnIndex == ColNew) Rows[e.RowIndex].Cells[ColNew].Tag = Rows[e.RowIndex].Cells[ColNew].Value as string ?? "";
    }

    protected override void OnEditingControlShowing(DataGridViewEditingControlShowingEventArgs e)
    {
        base.OnEditingControlShowing(e);
        if (e.Control is not TextBox tb || CurrentCell?.ColumnIndex != ColNew) return;
        _editor = tb;
        tb.KeyDown -= EditorKeyDown;
        tb.KeyDown += EditorKeyDown;
        tb.TextChanged -= EditorTextChanged;
        tb.TextChanged += EditorTextChanged;
        TypingChanged?.Invoke(tb.Text.Trim().Length > 0);
    }

    void EditorTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBox tb) return;
        _lastTyped = tb.Text;
        TypingChanged?.Invoke(tb.Text.Trim().Length > 0);
    }

    void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = true; e.SuppressKeyPress = true;
        CommitTyped();
    }

    /// <summary>Enter in the "New value" box keeps the value (the box's own key handler does the work; this catches the key when the grid itself gets it).</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && IsCurrentCellInEditMode) { CommitTyped(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        // the "?" column: a small round badge
        if (e.RowIndex < 0 || e.ColumnIndex != ColHelp || e.Graphics == null) return;
        e.PaintBackground(e.CellBounds, true);
        int d = Math.Min(e.CellBounds.Height, e.CellBounds.Width) - 8;
        var circle = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - d) / 2, e.CellBounds.Y + (e.CellBounds.Height - d) / 2, d, d);
        var old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(225, 236, 250))) e.Graphics.FillEllipse(b, circle);
        using (var p = new Pen(Color.FromArgb(120, 150, 200))) e.Graphics.DrawEllipse(p, circle);
        e.Graphics.SmoothingMode = old;
        TextRenderer.DrawText(e.Graphics, "?", Font, circle, Color.FromArgb(30, 80, 160), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        e.Handled = true;
    }
}

/// <summary>A picture box that enlarges pixel art without blurring it.</summary>
sealed class PixelPictureBox : PictureBox
{
    protected override void OnPaint(PaintEventArgs pe)
    {
        pe.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        pe.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        base.OnPaint(pe);
    }
}
