using RwEditor.Core.Apply;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// "Pending changes" (E3), at the bottom of the unit browser: every staged change (unit, value, now -> new, how, check result). Nothing is written until
/// "Apply changes". Hidden for folders that cannot be changed.
/// </summary>
sealed class PendingPanel : UserControl
{
    readonly Label _head = new() { Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = true,
        HideSelection = false,
    };
    readonly Button _apply = new() { Text = "Apply changes", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Padding = new Padding(10, 3, 10, 3), BackColor = Color.FromArgb(220, 240, 220) };
    readonly Button _remove = new() { Text = "Remove selected", AutoSize = true };
    readonly Button _clear = new() { Text = "Remove all", AutoSize = true };
    readonly Button _check = new() { Text = "Check again", AutoSize = true };
    readonly ToolTip _tip = new();
    EditController? _edit;
    ModSession? _session;

    public ListView List => _list;

    public PendingPanel()
    {
        Height = 190;
        BackColor = Color.FromArgb(255, 251, 235);
        Padding = new Padding(4);
        _list.Columns.Add("Unit", 230);
        _list.Columns.Add("Value", 200);
        _list.Columns.Add("Now", 140);
        _list.Columns.Add("New", 140);
        _list.Columns.Add("Applies to", 240);
        _list.Columns.Add("Check", 600);
        // fixed cells: "Apply changes" on the first line, then "Remove selected" | "Remove all", then "Check again"
        var buttons = new TableLayoutPanel { Dock = DockStyle.Right, AutoSize = true, ColumnCount = 2, RowCount = 3, Padding = new Padding(6, 0, 0, 0) };
        buttons.Controls.Add(_apply, 0, 0);
        buttons.SetColumnSpan(_apply, 2);
        buttons.Controls.Add(_remove, 0, 1);
        buttons.Controls.Add(_clear, 1, 1);
        buttons.Controls.Add(_check, 0, 2);
        _tip.SetToolTip(_apply, "Writes every pending change into your copy in one go, after checking them all together. A backup is made first (History > Undo).");
        _tip.SetToolTip(_remove, "Takes the selected changes off the list. Nothing in your copy changes.");
        _tip.SetToolTip(_clear, "Takes every change off the list. Nothing in your copy changes.");
        _tip.SetToolTip(_check, "Checks every pending change again, together, as \"Apply changes\" will.");
        _apply.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "applying the changes", () => _edit?.ApplyAsync() ?? Task.CompletedTask);
        _remove.Click += (_, _) => AppHost.SafeRun(FindForm(), "removing pending changes", RemoveSelected);
        _clear.Click += (_, _) => AppHost.SafeRun(FindForm(), "removing pending changes", () =>
        {
            if (_session == null || _session.Settings.PendingStatEdits.Count == 0) return;
            if (Ask.Confirm(FindForm(), "Remove all pending changes?", $"All {_session.Settings.PendingStatEdits.Count} pending change(s) are taken off the list. Your copy does not change.",
                    Array.Empty<string>(), "Remove all", "Cancel"))
                _edit?.RemovePending(_ => true, "all pending changes");
        });
        _check.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "checking the pending changes", () => _edit?.RefreshChecksAsync() ?? Task.CompletedTask);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; AppHost.SafeRun(FindForm(), "removing pending changes", RemoveSelected); } };
        _list.DoubleClick += (_, _) => AppHost.SafeRun(FindForm(), "showing a unit", () =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is PendingStatEdit p && FindForm() is MainForm f) f.ShowUnitStat(p.Unit, p.StatId, p.Section);
        });
        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(_head);
    }

    public void SetEditor(EditController edit) => _edit = edit;

    void RemoveSelected()
    {
        var sel = _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<PendingStatEdit>().ToList();
        if (sel.Count == 0) return;
        _edit?.RemovePending(p => sel.Any(x => x.SameTarget(p)), $"{sel.Count} change(s)");
    }

    public void ShowSession(ModSession? s)
    {
        _session = s;
        Visible = s != null && s.CanEdit;
        if (!Visible) return;
        var pend = s!.Settings.PendingStatEdits;
        // small while empty (one hint line), room for the list once something is staged: the unit table above keeps as much height as possible
        Height = LogicalToDeviceUnits(pend.Count == 0 ? 40 : Math.Min(230, 96 + 20 * pend.Count));
        _list.Visible = pend.Count > 0;
        _head.Text = pend.Count == 0
            ? "Pending changes: none. Type a new value in the \"New value\" column of a unit, press Enter, then click \"Apply changes\"."
            : $"Pending changes ({pend.Count}) - nothing is written into your copy until you click \"Apply changes\"";
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in pend)
        {
            var e = s.Dict.ById(p.StatId);
            var u = s.Model.UnitsInFile(p.UnitFile).FirstOrDefault(x => x.Name == p.Unit);
            string unitText = u == null ? p.Unit + " (not found)" : UnitRowText.For(s, u).Describe(u);
            string now = "?";
            if (u != null && e != null)
            {
                var r = StatReader.Read(s.Model.Merge(u), e, p.Section);
                now = !r.IsSet ? "not set" : r.Number is double n ? StatText.Value(e, n) : r.Raw!.Value.Trim();
            }
            string nw = e == null ? p.Value : StatGrid.PendingDisplay(e, p.Value) + " " + StatText.InputUnit(e);
            string how = p.Mode == "template" ? "every unit that shares it" : p.FromFamilyOf != null ? $"this unit (copied from {p.FromFamilyOf})" : "this unit only";
            string check = _edit?.CheckText(p) ?? "not checked yet (checked again before applying)";
            var it = new ListViewItem(new[] { unitText, e == null ? p.StatId : StatText.StatName(e, p.Section), now, nw, how, check }) { Tag = p, ToolTipText = check };
            if (check.StartsWith("Problem", StringComparison.Ordinal) || check.Contains("Together:", StringComparison.Ordinal)) it.ForeColor = Color.FromArgb(170, 20, 20);
            else if (check.StartsWith("OK, but", StringComparison.Ordinal)) it.ForeColor = Color.FromArgb(150, 90, 0);
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _apply.Enabled = _remove.Enabled = _clear.Enabled = _check.Enabled = pend.Count > 0;
        _remove.Visible = _clear.Visible = _check.Visible = pend.Count > 0;
    }
}

/// <summary>History and undo (E3): every backup of the copy (newest first) with what it did, "Undo last change", "Revert...", the backup folder.</summary>
sealed class HistoryView : UserControl
{
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false,
    };
    readonly Label _where = Ui.Para("");
    readonly Button _undo = new() { Text = "Undo last change", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(6) };
    readonly Button _revert = new() { Text = "Revert...", AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(6) };
    readonly Func<ModSession?> _session;
    readonly Func<EditController> _edit;

    public ListView List => _list;

    public HistoryView(Func<ModSession?> session, Func<EditController> edit)
    {
        _session = session; _edit = edit;
        Padding = new Padding(12);
        _list.Columns.Add("When", 150);
        _list.Columns.Add("What", 220);
        _list.Columns.Add("Details", 760);
        _list.Columns.Add("State", 130);
        _undo.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "undoing the last change", async () => { await _edit().UndoLastAsync(); Refresh2(); });
        _revert.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "putting values back", async () =>
        {
            var f = FindForm() as MainForm;
            await _edit().RevertAsync(f?.Browser.Details.Unit, f?.Browser.Details.SelectedStat);
            Refresh2();
        });
        Controls.Add(_list);
        Ui.Stack(this,
            Ui.Heading("History and undo"),
            Ui.Para("Every change to your copy is listed here, newest first. Before each change the editor saves a backup of the files it changes, so every change can be undone. " +
                    "\"Undo last change\" puts the files back as they were before the newest change that is not undone yet; press it again to go further back. " +
                    "\"Revert...\" puts back one value, one unit, one mod area, or everything in your copy (to the values it had when it was made)."),
            _where,
            Ui.Row(_undo, _revert,
                Ui.Btn("Open backup folder", () => { if (_session() is ModSession s) AppHost.OpenFolder(BackupStore.Folder(AppHost.WriteOptions().EffectiveBackupRoot, s.Root)); }, what: "opening the backup folder"),
                Ui.Btn("Refresh", Refresh2, what: "refreshing the history")));
        VisibleChanged += (_, _) => { if (Visible) AppHost.SafeRun(FindForm(), "reading the history", Refresh2); };
    }

    public void Refresh2()
    {
        var s = _session();
        _list.BeginUpdate();
        _list.Items.Clear();
        try
        {
            if (s == null) { _where.Text = "No copy is open."; _undo.Enabled = _revert.Enabled = false; return; }
            string root = AppHost.WriteOptions().EffectiveBackupRoot;
            _where.Text = (s.CanEdit ? "" : "Changes are switched off for this folder: " + s.EditBlockedReason + "\n") +
                          $"Backups of this copy are kept in {BackupStore.Folder(root, s.Root)} (never deleted by the editor)." +
                          (s.BaselineNote != null ? "\n" + s.BaselineNote : "");
            IReadOnlyList<HistoryEntry> list;
            try { list = History.List(root, s.Root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _where.Text += "\nThe backup folder cannot be read: " + e.Message; list = Array.Empty<HistoryEntry>(); }
            foreach (var h in list)
            {
                string state = h.Problem != null ? "cannot be read" : h.IsChange ? (h.Undone ? "undone" : "") : h.Kind == "undo-safety" ? "saved before an undo" : "";
                var it = new ListViewItem(new[] { h.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), h.KindText, h.Problem ?? h.Summary, state })
                    { ToolTipText = (h.Problem ?? h.Summary).Replace("; ", "\n") + "\n\n" + Path.GetFileName(h.Path) };
                if (h.Undone || !h.IsChange) it.ForeColor = SystemColors.GrayText;
                _list.Items.Add(it);
            }
            if (list.Count == 0) _list.Items.Add(new ListViewItem(new[] { "", "No changes yet", "Nothing was changed in this copy so far.", "" }));
            _undo.Enabled = s.CanEdit && list.Any(h => h.IsChange && !h.Undone && h.Problem == null);
            _revert.Enabled = s.CanEdit;
        }
        finally { _list.EndUpdate(); }
    }
}
