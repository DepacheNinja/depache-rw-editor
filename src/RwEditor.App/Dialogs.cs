using RwEditor.Core.Browse;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// Every question and message of the editing screens. In unattended runs (self-test, screenshots) nothing is shown: the text is logged and kept in
/// <see cref="AppHost.UnattendedMessages"/>, and confirmations get <see cref="AppHost.AutoConfirm"/>.
/// </summary>
static class Ask
{
    public static bool Confirm(IWin32Window? owner, string title, string intro, IEnumerable<string> lines, string yes, string no = "Cancel", string? footer = null)
    {
        var list = lines.ToList();
        if (AppHost.Unattended)
        {
            AppHost.UnattendedMessages.Add($"CONFIRM {title}: {intro} | {string.Join(" | ", list)}{(footer != null ? " | " + footer : "")} -> {(AppHost.AutoConfirm ? yes : no)}");
            AppHost.Log.Info($"(unattended) confirm '{title}': {(AppHost.AutoConfirm ? yes : no)}");
            return AppHost.AutoConfirm;
        }
        using var d = new ListDialog(title, intro, list, footer, yes, no);
        return d.ShowDialog(owner) == DialogResult.OK;
    }

    public static void Info(IWin32Window? owner, string title, string text, IEnumerable<string>? lines = null, bool problem = false)
    {
        var list = lines?.ToList() ?? new List<string>();
        AppHost.Log.Info($"{(problem ? "problem" : "message")} '{title}': {text} {string.Join(" | ", list)}");
        if (AppHost.Unattended) { AppHost.UnattendedMessages.Add($"{(problem ? "PROBLEM" : "INFO")} {title}: {text} | {string.Join(" | ", list)}"); return; }
        TipFix.HideAll();
        if (list.Count == 0) { MessageBox.Show(owner, text, title, MessageBoxButtons.OK, problem ? MessageBoxIcon.Warning : MessageBoxIcon.Information); return; }
        using var d = new ListDialog(title, text, list, null, "OK", null);
        d.ShowDialog(owner);
    }
}

/// <summary>A plain dialog: a sentence, a scrollable list (one line per item), an optional footer, and one or two buttons.</summary>
sealed class ListDialog : Form
{
    public ListDialog(string title, string intro, IReadOnlyList<string> lines, string? footer, string yes, string? no)
    {
        Text = title;
        Icon = AppHost.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Size = new Size(760, lines.Count > 0 ? 520 : 260);
        MinimumSize = new Size(480, 220);
        Padding = new Padding(12);
        TipFix.Attach(this);

        var head = new Label { Text = intro, Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(720, 0), Padding = new Padding(0, 0, 0, 8) };
        var list = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window,
            Text = string.Join(Environment.NewLine, lines.Select(l => "- " + l)), Visible = lines.Count > 0,
        };
        var foot = new Label { Text = footer ?? "", Dock = DockStyle.Bottom, AutoSize = true, MaximumSize = new Size(720, 0), Padding = new Padding(0, 8, 0, 0), Visible = footer != null };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = yes, AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(10, 3, 10, 3) };
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        if (no != null)
        {
            var cancel = new Button { Text = no, AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 3, 10, 3) };
            buttons.Controls.Add(cancel);
            CancelButton = cancel;
        }
        Controls.Add(list);
        Controls.Add(head);
        Controls.Add(foot);
        Controls.Add(buttons);
    }
}

/// <summary>
/// "This value is shared": the two plain choices for a value that other units read too. Nothing is pre-selected (the shared change is never the default);
/// each choice says whether it is possible (the full check already ran) and the shared one lists every unit it changes.
/// </summary>
sealed class ShareChoiceDialog : Form
{
    readonly RadioButton _only = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    readonly RadioButton _all = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    readonly Button _ok = new() { Text = "Continue", AutoSize = true, Enabled = false, Padding = new Padding(10, 3, 10, 3) };

    /// <summary>true = every unit that shares it, false = only this unit (after OK).</summary>
    public bool ChangeAll => _all.Checked;

    public ShareChoiceDialog(UnitRowText unit, StageInfo info, StageCheck? onlyCheck, StageCheck? allCheck)
    {
        Text = "This value is shared";
        Icon = AppHost.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Size = new Size(780, 600);
        MinimumSize = new Size(560, 420);
        Padding = new Padding(14);
        TipFix.Attach(this);

        var e = info.Entry;
        var affected = allCheck?.Items.FirstOrDefault()?.Plan?.Affected.Select(a => a.Unit).ToList() ?? info.SharingUnits.ToList();
        string from = info.Origin == null ? "" : info.Origin.Kind == OriginKind.Own
            ? $"this unit's own file ({info.Origin.File}), which {Math.Max(0, info.SharingUnits.Count - 1)} other unit(s) copy"
            : $"a shared template ({info.Origin.File}, line {info.Origin.Line})";
        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(740, 0), Padding = new Padding(0, 0, 0, 10),
            Text = $"\"{StatText.StatName(e, info.Section)}\" of {unit.Title} comes from {from}. Changing that line changes {affected.Count} units. Choose what you want:",
        };

        string onlyProblem = onlyCheck == null ? "not checked" : onlyCheck.Ok ? "" : string.Join(" ", onlyCheck.Problems);
        _only.Text = "Change only this unit";
        _only.Enabled = onlyCheck?.Ok == true;
        var onlyNote = Note(_only.Enabled
            ? $"Adds the new value to {unit.Name}'s own file. The other units keep their value."
            : "Not possible here: " + onlyProblem);

        string allProblem = allCheck == null ? "the shared line is in a protected file" : allCheck.Ok ? "" : string.Join(" ", allCheck.Problems);
        _all.Text = $"Change every unit that shares it ({affected.Count} units)";
        _all.Enabled = allCheck?.Ok == true;
        var allNote = Note(_all.Enabled ? "Changes the shared line. These units all get the new value:" : "Not possible here: " + allProblem);
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var u in affected) list.Items.Add(unit.Describe(u));

        _only.CheckedChanged += (_, _) => _ok.Enabled = _only.Checked || _all.Checked;
        _all.CheckedChanged += (_, _) => _ok.Enabled = _only.Checked || _all.Checked;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 3, 10, 3) };
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        Ui.Stack(top, intro, Wrap(_only), onlyNote, Wrap(_all), allNote);
        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(buttons);
    }

    static Panel Wrap(Control c) { var p = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 4, 0, 0) }; p.Controls.Add(c); return p; }

    static Label Note(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(720, 0), Padding = new Padding(22, 0, 0, 8), ForeColor = Color.FromArgb(70, 70, 80) };

    /// <summary>Shows the choice. Returns true = every unit, false = only this unit, null = cancelled. Unattended: <see cref="AppHost.AutoShareChoice"/>.</summary>
    public static bool? Choose(IWin32Window? owner, UnitRowText unit, StageInfo info, StageCheck? onlyCheck, StageCheck? allCheck)
    {
        if (AppHost.Unattended)
        {
            var c = AppHost.AutoShareChoice(onlyCheck?.Ok == true, allCheck?.Ok == true);
            AppHost.UnattendedMessages.Add($"SHARED {unit.Name} {info.Entry.Id}: only={onlyCheck?.Ok} all={allCheck?.Ok} -> {(c == null ? "cancel" : c.Value ? "all" : "only")}");
            return c;
        }
        using var d = new ShareChoiceDialog(unit, info, onlyCheck, allCheck);
        return d.ShowDialog(owner) == DialogResult.OK ? d.ChangeAll : null;
    }
}

/// <summary>Unit names for dialogs: the in-game name and the internal name.</summary>
sealed record UnitRowText(string Name, string Title, Func<UnitType, string> Describe)
{
    public static UnitRowText For(ModSession s, UnitType u) => new(u.Name, s.RowOf(u).Title, x => { var r = s.RowOf(x); return r.Title == x.Name ? x.Name : $"{r.Title} ({x.Name})"; });
}

/// <summary>"Revert...": pick what to put back (this stat, this unit, a mod area, everything), see exactly what that does, then confirm.</summary>
sealed class RevertDialog : Form
{
    readonly RadioButton _stat = new() { AutoSize = true };
    readonly RadioButton _unit = new() { AutoSize = true };
    readonly RadioButton _area = new() { AutoSize = true, Text = "Every change in one mod area:" };
    readonly RadioButton _all = new() { AutoSize = true, Text = "Everything in this copy, back to the values it had when it was made" };
    readonly ComboBox _areas = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    readonly Button _ok = new() { Text = "Put back", AutoSize = true, Enabled = false, Padding = new Padding(10, 3, 10, 3) };
    readonly ModSession _session;
    readonly UnitType? _u;
    readonly StatRow? _row;
    int _version;

    public RevertRequest? Request { get; private set; }

    public RevertDialog(ModSession session, UnitType? unit, StatRow? row)
    {
        _session = session; _u = unit; _row = row;
        Text = "Put values back";
        Icon = AppHost.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Size = new Size(820, 600);
        MinimumSize = new Size(560, 420);
        Padding = new Padding(14);
        TipFix.Attach(this);

        string unitText = unit == null ? "" : UnitRowText.For(session, unit).Describe(unit);
        _stat.Text = row != null && unit != null ? $"This value: \"{StatText.StatName(row.Entry, row.Section)}\" of {unitText}" : "This value (select a value first)";
        _stat.Enabled = row != null && unit != null;
        _unit.Text = unit != null ? $"Every change made on this unit: {unitText}" : "This unit (select a unit first)";
        _unit.Enabled = unit != null;
        foreach (var a in session.Model.Areas.Areas) _areas.Items.Add(new AreaChoice(a.Id, a.Label));
        _areas.SelectedIndex = 0;

        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(780, 0), Padding = new Padding(0, 0, 0, 8),
            Text = "Choose what to put back. You see exactly what happens before anything is changed. A backup is made first, so this can be undone in History.",
        };
        var areaRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        areaRow.Controls.Add(_area);
        areaRow.Controls.Add(_areas);
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        Ui.Stack(top, intro, Line(_stat), Line(_unit), areaRow, Line(_all),
            new Label { Text = "What will happen:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 4), Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) });

        foreach (var rb in new[] { _stat, _unit, _area, _all }) rb.CheckedChanged += (_, _) => { if (rb.Checked) _ = AppHost.SafeRunAsync(this, "checking what to put back", RefreshPreviewAsync); };
        _areas.SelectedIndexChanged += (_, _) => { if (_area.Checked) _ = AppHost.SafeRunAsync(this, "checking what to put back", RefreshPreviewAsync); else _area.Checked = true; };
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 3, 10, 3) };
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);
        Controls.Add(_preview);
        Controls.Add(top);
        Controls.Add(buttons);
        _preview.Text = "Choose an option above.";
    }

    sealed record AreaChoice(string Id, string Label) { public override string ToString() => Label; }

    static Panel Line(Control c) { var p = new Panel { Dock = DockStyle.Top, Height = 28 }; p.Controls.Add(c); return p; }

    /// <summary>Selects a scope programmatically (screenshots / tests).</summary>
    public void Choose(RevertScope scope)
    {
        (scope switch { RevertScope.Stat => _stat, RevertScope.Unit => _unit, RevertScope.Area => _area, _ => _all }).Checked = true;
    }

    /// <summary>The narrowest choice that exists (this value, else this unit, else the first mod area), so the dialog opens with a preview and a working button.</summary>
    public void ChooseDefault() => Choose(_stat.Enabled ? RevertScope.Stat : _unit.Enabled ? RevertScope.Unit : RevertScope.Area);

    public Task PreviewDone { get; private set; } = Task.CompletedTask;

    /// <summary>The "Put back" button (tests click it the way a user does).</summary>
    internal Button PutBackButton => _ok;
    internal string PreviewText => _preview.Text;

    RevertRequest Current() =>
        _stat.Checked ? new RevertRequest(RevertScope.Stat, _u!.Name, _u.RelPath, _row!.Entry.Id, _row.Section)
        : _unit.Checked ? new RevertRequest(RevertScope.Unit, _u!.Name, _u.RelPath)
        : _area.Checked ? new RevertRequest(RevertScope.Area, Area: ((AreaChoice)_areas.SelectedItem!).Id)
        : new RevertRequest(RevertScope.All);

    async Task RefreshPreviewAsync()
    {
        int v = ++_version;
        var req = Current();
        _ok.Enabled = false;
        _preview.Text = "Checking...";
        var tcs = new TaskCompletionSource();
        PreviewDone = tcs.Task;
        try
        {
            RevertPlan plan;
            try { plan = await Task.Run(() => StatRevert.Plan(_session.Root, req, AppHost.WriteOptions())); }
            catch (RwEditor.Core.EditorRefusalException e)
            {
                if (v == _version) _preview.Text = "Not possible: " + e.Message;
                return;
            }
            if (v != _version) return;
            Request = req;
            var lines = new List<string>();
            if (!plan.Changes) lines.Add("Nothing to put back: there is no applied change for this choice. (Pending changes are not touched; remove them in the Pending changes list.)");
            else
            {
                lines.Add("How: " + plan.Method + ".");
                lines.AddRange(plan.Lines.Select(l => "- " + l));
                if (req.Scope == RevertScope.All && plan.NewBytes.Count > 0) lines.Add($"{plan.NewBytes.Count} file(s) are restored.");
                lines.AddRange(plan.Notes.Select(n => "Note: " + n));
                lines.Add("Pending (not yet applied) changes stay in the Pending changes list.");
            }
            _preview.Text = string.Join(Environment.NewLine, lines);
            _ok.Enabled = plan.Changes;
        }
        finally { tcs.TrySetResult(); }
    }
}
