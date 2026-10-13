using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// "Set for many units" (C-033): one value of one stat for every building / unit that matches a filter. The preview (Core <see cref="StatBulk"/>, nothing
/// written) says how many units change, how many are skipped and why, and the "Stage" button puts the checked changes into the pending list.
/// </summary>
sealed class BulkDialog : Form
{
    readonly ModSession _session;
    readonly StatEntry _entry;
    readonly TextBox _value = new() { Width = 120 };
    readonly RadioButton _bld = new() { Text = "All buildings", AutoSize = true, Checked = true };
    readonly RadioButton _unit = new() { Text = "All mobile units", AutoSize = true };
    readonly RadioButton _both = new() { Text = "Buildings and units", AutoSize = true };
    readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly RadioButton _any = new() { Text = "Every one of them", AutoSize = true, Checked = true };
    readonly RadioButton _has = new() { Text = "Only those that already have this value (change it)", AutoSize = true };
    readonly RadioButton _without = new() { Text = "Only those that do not have it yet (add it)", AutoSize = true };
    readonly CheckBox _copies = new() { Text = "Also change their upgrade levels and versions (the units that copy their file)", AutoSize = true, Checked = true, Dock = DockStyle.Top, Padding = new Padding(4, 6, 0, 2) };
    readonly TextBox _preview = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    readonly Button _stage = new() { Text = "Stage", AutoSize = true, Enabled = false, Padding = new Padding(10, 3, 10, 3) };
    readonly System.Windows.Forms.Timer _delay = new() { Interval = 350 };
    int _version;

    public BulkPlan? Plan { get; private set; }
    public Task PreviewDone { get; private set; } = Task.CompletedTask;
    internal Button StageButton => _stage;
    internal string PreviewText => _preview.Text;

    sealed record AreaChoice(string? Id, string Label) { public override string ToString() => Label; }

    public BulkDialog(ModSession session, StatEntry entry, string? prefill)
    {
        _session = session; _entry = entry;
        Text = $"Set \"{entry.Label}\" for many units";
        Icon = AppHost.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Size = new Size(840, 780);
        MinimumSize = new Size(640, 560);
        Padding = new Padding(14);
        TipFix.Attach(this);

        _area.Items.Add(new AreaChoice(null, "All mods"));
        foreach (var a in session.Model.Areas.Areas) _area.Items.Add(new AreaChoice(a.Id, a.Label));
        _area.SelectedIndex = 0;
        _value.Text = prefill ?? "";

        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(780, 0), Padding = new Padding(0, 0, 0, 8),
            Text = $"{entry.Desc}\n\nThe same value is given to every complete unit or building that matches the choices below. Attached parts and helper units are never included, " +
                   "locked mod machinery is never included, and nothing is written into your copy yet: the changes go into the pending list, where you can look at them and remove any before you click \"Apply changes\".",
        };
        var valueRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        valueRow.Controls.Add(new Label { Text = "New value:", AutoSize = true, Padding = new Padding(0, 6, 4, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) });
        valueRow.Controls.Add(_value);
        valueRow.Controls.Add(new Label { Text = $"{StatText.InputUnit(entry)}   (allowed {StatText.Range(entry)})", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });

        var who = Group("Which ones", new Control[] { _bld, _unit, _both, Row(new Label { Text = "In:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) }, _area) });
        var have = Group("Only", new Control[] { _any, _has, _without });
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        Ui.Stack(top, intro, valueRow, who, have, _copies,
            new Label { Text = "What will happen:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 4), Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) });

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 3, 10, 3) };
        CancelButton = cancel;
        _stage.Click += (_, _) => { if (Plan?.CanStage == true) { DialogResult = DialogResult.OK; Close(); } };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(_stage);
        buttons.Controls.Add(cancel);
        Controls.Add(_preview);
        Controls.Add(top);
        Controls.Add(buttons);
        _preview.Text = "Type a value above.";

        foreach (var rb in new[] { _bld, _unit, _both, _any, _has, _without }) rb.CheckedChanged += (_, _) => Changed();
        _copies.CheckedChanged += (_, _) => Changed();
        _area.SelectedIndexChanged += (_, _) => Changed();
        _value.TextChanged += (_, _) => Changed();
        _delay.Tick += (_, _) => { _delay.Stop(); _ = AppHost.SafeRunAsync(this, "checking the change", RefreshAsync); };
        FormClosed += (_, _) => _delay.Dispose();
        Shown += (_, _) => { _value.Focus(); if (_value.Text.Trim().Length > 0) Changed(); };
    }

    static GroupBox Group(string title, Control[] items)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 6) };
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        f.Controls.AddRange(items);
        g.Controls.Add(f);
        return g;
    }

    static FlowLayoutPanel Row(params Control[] c)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        f.Controls.AddRange(c);
        return f;
    }

    void Changed()
    {
        _stage.Enabled = false;
        Plan = null;
        _delay.Stop();
        _delay.Start();   // wait until the typing pauses
    }

    BulkFilter Filter() =>
        new(_bld.Checked ? BulkWho.Buildings : _unit.Checked ? BulkWho.Units : BulkWho.UnitsAndBuildings,
            (_area.SelectedItem as AreaChoice)?.Id,
            _has.Checked ? BulkHave.OnlyThoseThatHaveIt : _without.Checked ? BulkHave.OnlyThoseWithout : BulkHave.Any, _copies.Checked);

    /// <summary>Types the value like a user (tests).</summary>
    internal void TypeForTest(string text) => _value.Text = text;

    /// <summary>Runs the preview now (tests); normally it runs when the typing pauses.</summary>
    public Task RefreshNow() { _delay.Stop(); return RefreshAsync(); }

    async Task RefreshAsync()
    {
        int v = ++_version;
        string typed = _value.Text.Trim();
        var tcs = new TaskCompletionSource();
        PreviewDone = tcs.Task;
        try
        {
            if (typed.Length == 0) { _preview.Text = "Type a value above."; return; }
            _preview.Text = "Checking...";
            var filter = Filter();
            BulkPlan plan;
            try { plan = await Task.Run(() => StatBulk.Plan(_session.Model, _session.Dict, _session.Families, _entry, typed, filter, _session.ReadDisk)); }
            catch (RwEditor.Core.EditorRefusalException e) { if (v == _version) _preview.Text = "Not possible: " + StatText.Plain(e.Message); return; }
            if (v != _version) return;
            Plan = plan;
            var lines = new List<string>();
            if (plan.Ok.Count == 0) lines.Add($"Nothing can be changed: {plan.Considered} unit(s) match, all skipped.");
            else
            {
                lines.Add($"{plan.Ok.Count} unit(s) / building(s) get {StatText.StatName(_entry, plan.Section)} = {typed} {StatText.InputUnit(_entry)}." +
                          (plan.AffectedUnits.Count > plan.Ok.Count ? $" With their upgrade levels and versions {plan.AffectedUnits.Count} units change in all." : ""));
                lines.Add($"{plan.Considered} matched, {plan.Skipped.Count} skipped.");
            }
            foreach (var (reason, count) in plan.SkipSummary) lines.Add($"- skipped {count}: {reason}");
            foreach (var p in plan.BatchProblems) lines.Add("PROBLEM: " + p);
            if (plan.Ok.Count > 0)
            {
                lines.Add("");
                lines.Add("Changed: " + string.Join(", ", plan.Ok.Take(40).Select(p => p.Unit)) + (plan.Ok.Count > 40 ? $", ... and {plan.Ok.Count - 40} more" : ""));
            }
            _preview.Text = string.Join(Environment.NewLine, lines);
            _stage.Text = plan.CanStage ? $"Stage {plan.Ok.Count} change(s)" : "Stage";
            _stage.Enabled = plan.CanStage;
        }
        finally { tcs.TrySetResult(); }
    }
}
