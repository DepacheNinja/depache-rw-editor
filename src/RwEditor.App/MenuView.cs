using System.Globalization;
using RwEditor.Core;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Options;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.App;

/// <summary>
/// "Menu options" (E5): every option of the match-start menu in all four sections. Each one can be shown, hidden or deleted (E5a), its name and description
/// can be changed, and the few options that have numbers (base income, rich start) show them to change (E5b). Hide and delete both take the option's buttons
/// out of the game's menu; text and numbers are written into the protected option files only on the lines of the whitelist catalog (Core
/// <see cref="OptionModel"/>), recorded so they can be undone and so the protected-file check can tell them from tampering. Options that are plain switches
/// (Super AI, Fair AI, the bans...) have no numbers and only a text. Everything is checked by the same rules as the apply (<see cref="SettingsValidator"/>)
/// and written through the same backed-up, undoable <see cref="RwEditor.Core.Apply.GroupsPipeline"/> as the Start units page.
/// </summary>
sealed class MenuView : UserControl
{
    readonly Func<EditController> _edit;
    readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowGroups = true, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle, ShowItemToolTips = true,
    };
    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), MaximumSize = new Size(380, 0) };
    readonly Label _where = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90), MaximumSize = new Size(380, 0) };
    readonly Label _what = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 8, 0, 0) };
    readonly Label _stateHelp = new() { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.FromArgb(70, 80, 90), Margin = new Padding(0, 10, 0, 0) };
    readonly Button _toggle = new() { Text = "Hide", AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    readonly Button _delete = new() { Text = "Delete", AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    readonly GroupBox _textBox = new() { Text = "Text in the game's menu", Width = 390, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Padding = new Padding(8, 4, 8, 8) };
    readonly TextBox _nameBox = new() { Width = 360, MaxLength = 80 };
    readonly Label _nameHint = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(150, 80, 0) };
    readonly TextBox _descBox = new() { Width = 360, Height = 96, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, MaxLength = 1200 };
    readonly Label _descHint = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(150, 80, 0) };
    readonly Button _textBack = new() { Text = "Put the text back", AutoSize = true };
    readonly GroupBox _numBox = new() { Text = "Numbers", Width = 390, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Padding = new Padding(8, 4, 8, 8) };
    readonly FlowLayoutPanel _numRows = new() { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly Button _revertOption = new() { Text = "Put this option back (text and numbers too)", AutoSize = true, Enabled = false };
    readonly Button _revertSection = new() { Text = "Put this section back to the defaults", AutoSize = true, Enabled = false };
    readonly Button _revertAll = new() { Text = "Put every option back to the defaults", AutoSize = true, Enabled = false };
    readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(8, 0, 0, 0) };
    readonly Button _apply = new() { Text = "Apply to my copy", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = false };
    readonly Button _discard = new() { Text = "Discard my changes here", AutoSize = true, Enabled = false };
    readonly Label _banner = new() { Dock = DockStyle.Top, Height = 38, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), BackColor = Color.FromArgb(255, 236, 160), Font = new Font("Segoe UI", 9.75f, FontStyle.Bold) };
    readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 300 };
    readonly System.Windows.Forms.Timer _check = new() { Interval = 300 };
    readonly Font _struck;
    readonly Font _italic;

    ModSession? _session;
    ModContext? _ctx;
    OptionModel? _opt;
    string? _optProblem;
    EditorSettings _work = EditorSettings.Default();
    string _savedSig = "";
    bool _loading;
    readonly Dictionary<string, NumericUpDown> _numBoxes = new(StringComparer.Ordinal);

    /// <summary>True when the Start units page has changes that are not applied yet: an apply here would reload both pages and lose them (set by the window).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? OtherPageDirty { get; set; }

    /// <summary>The settings being edited (tests).</summary>
    public EditorSettings Working => _work;
    public bool CanApply => _apply.Enabled;
    public string StatusText => _status.Text;
    public bool Dirty => Signature(_work) != _savedSig;
    public int OptionRows => _list.Items.Cast<ListViewItem>().Count(i => i.Tag is OptionInfo);
    public int GroupRows => _list.Items.Cast<ListViewItem>().Count(i => i.Tag is SpawnGroup);
    public int SectionCount => _list.Groups.Count;

    public MenuView(Func<EditController> edit)
    {
        _edit = edit;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        _struck = new Font(_list.Font, FontStyle.Strikeout);
        _italic = new Font(_list.Font, FontStyle.Italic);
        _list.Columns.Add("Option", 230);
        _list.Columns.Add("State", 70);
        _list.Columns.Add("Note", 300);

        var head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        var heading = new Label { Text = "Menu options", Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Padding = new Padding(0, 4, 0, 0) };
        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(1000, 0), Padding = new Padding(0, 2, 0, 2),
            Text = "These are the options players tick in the match-start menu, in all four sections. Hide or delete the ones you do not want to see (an option that is not ticked stays off), " +
                   "change the words players read, and set the numbers of the few options that have them. Options that are plain on/off switches have no numbers. " +
                   "Your own groups (section 4) are made on the Start units page.",
        };
        head.Controls.Add(intro);
        head.Controls.Add(heading);

        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 430, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 0, 0, 0), AutoScroll = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _toggle, _delete });
        var textStack = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        textStack.Controls.AddRange(new Control[]
        {
            new Label { Text = "Name (the button's words)", AutoSize = true }, _nameBox, _nameHint,
            new Label { Text = "Description (the tooltip)", AutoSize = true, Margin = new Padding(3, 8, 3, 0) }, _descBox, _descHint, _textBack,
        });
        _textBox.Controls.Add(textStack);
        _numBox.Controls.Add(_numRows);
        side.Controls.AddRange(new Control[] { _title, _where, _what, _stateHelp, buttons, _textBox, _numBox, new Label { Height = 8, AutoSize = false }, _revertOption, _revertSection, _revertAll });

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(240, 242, 246) };
        var barButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 8, 0) };
        barButtons.Controls.Add(_apply);
        barButtons.Controls.Add(_discard);
        bar.Controls.Add(_status);
        bar.Controls.Add(barButtons);

        Controls.Add(_list);
        Controls.Add(side);
        Controls.Add(head);
        Controls.Add(bar);
        Controls.Add(_banner);

        _tip.SetToolTip(_toggle, "Hide: the option's buttons are not shown in the game; it stays in this list as switched off. Show: it is back.");
        _tip.SetToolTip(_delete, "Delete: the option's buttons are gone from the game's menu. The game files keep it, so \"Show\" or the revert buttons bring it back.");
        _tip.SetToolTip(_nameBox, "The words on the option's button. Letters, digits, spaces and - _ . ! ? + ' & ( ). The Chinese text of the game is not changed.");
        _tip.SetToolTip(_descBox, "The tooltip players read. One line per row; letters, digits, spaces and - _ . , ! ? + ' & ( ) : ; / %. The Chinese text of the game is not changed.");
        _tip.SetToolTip(_apply, "Writes this into your copy (a backup is made first; History > Undo puts everything back).");
        _tip.SetToolTip(_discard, "Throws away what you changed here since the last apply.");

        _list.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "showing the option", ShowSelected);
        _toggle.Click += (_, _) => AppHost.SafeRun(FindForm(), "changing the option", ToggleSelected);
        _delete.Click += (_, _) => AppHost.SafeRun(FindForm(), "deleting the option", DeleteSelected);
        _nameBox.TextChanged += (_, _) => { if (!_loading) AppHost.SafeRun(FindForm(), "changing the name", () => NameTyped(_nameBox.Text)); };
        _descBox.TextChanged += (_, _) => { if (!_loading) AppHost.SafeRun(FindForm(), "changing the description", () => DescriptionTyped(_descBox.Text)); };
        _textBack.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the text back", RevertText);
        _revertOption.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the option back", RevertOption);
        _revertSection.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the section back", RevertSection);
        _revertAll.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the options back", RevertAll);
        _discard.Click += (_, _) => AppHost.SafeRun(FindForm(), "discarding changes", Discard);
        _apply.Click += (_, _) => _ = AppHost.SafeRunAsync(FindForm(), "applying the menu", ApplyAsync);
        _check.Tick += (_, _) => { _check.Stop(); AppHost.SafeRun(FindForm(), "checking the menu", Recheck); };
        Disposed += (_, _) => { _struck.Dispose(); _italic.Dispose(); _tip.Dispose(); _check.Dispose(); };
    }

    static string Signature(EditorSettings s)
    {
        var c = s.Clone();
        c.StatEdits.Clear(); c.PendingStatEdits.Clear(); c.OptionEdits.Clear(); c.BaseMod = null;
        return SettingsJson.ContentHash(c);
    }

    // ------------------------------------------------------------------------------------------------------------ session
    /// <summary>Loads the copy's saved option choices (called when a copy is opened or re-read after a write). The page is small, so it is filled at once.</summary>
    public void Bind(ModSession? session)
    {
        _session = session;
        _ctx = null;
        _opt = null;
        _optProblem = null;
        _loading = true;
        try
        {
            _list.Items.Clear();
            _list.Groups.Clear();
            if (session == null) { _banner.Visible = false; _status.Text = "No copy is open."; SetEnabledState(false); return; }
            _work = session.Settings.Clone();
            _savedSig = Signature(_work);
            _banner.Visible = !session.CanEdit;
            _banner.Text = session.CanEdit ? "" : "Looking only: " + (session.EditBlockedReason ?? "this folder cannot be changed.");
            try { _ctx = ModContext.Build(session.Model.Snapshot, EditorSupport.Load(session.Root)); }
            catch (EditorRefusalException e)
            {
                _status.Text = "The menu cannot be shown here: " + e.Message;
                SetEnabledState(false);
                return;
            }
            try { _opt = _ctx.LoadOptionModel(_work.OptionEdits); }
            catch (EditorRefusalException e) { _optProblem = e.Message; }
            Fill();
            SetEnabledState(session.CanEdit);
        }
        finally { _loading = false; }
        ShowSelected();
        Recheck();
    }

    void SetEnabledState(bool on)
    {
        _list.Enabled = _ctx != null;
        _revertAll.Enabled = on && _ctx != null;
        if (!on) { _toggle.Enabled = false; _delete.Enabled = false; _revertSection.Enabled = false; _revertOption.Enabled = false; _apply.Enabled = false; _discard.Enabled = false; _textBox.Visible = false; _numBox.Visible = false; }
    }

    bool CanChange => _session is { CanEdit: true } && _ctx != null;

    // ------------------------------------------------------------------------------------------------------------ list
    void Fill()
    {
        var ctx = _ctx!;
        foreach (var id in ctx.Options.Select(o => o.TypeId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            _list.Groups.Add(new ListViewGroup(id, SectionHeader(id)) { Tag = id });
        foreach (var o in ctx.Options)
        {
            var item = new ListViewItem(new[] { o.Name, "", "" }) { Tag = o, Group = GroupOf(o.TypeId) };
            _list.Items.Add(item);
            PaintRow(item);
        }
        foreach (var g in _work.SpawnGroups.OrderBy(g => g.Slot))
        {
            var item = new ListViewItem(new[] { g.Name, "Your group", "Made on the Start units page" + (g.Enabled ? "" : " (switched off)") }) { Tag = g, Group = GroupOf(ModContext.Section4TypeId), ForeColor = Color.FromArgb(0, 110, 60) };
            _list.Items.Add(item);
        }
        UpdateHeaders();
    }

    ListViewGroup? GroupOf(string typeId) => _list.Groups.Cast<ListViewGroup>().FirstOrDefault(g => (string?)g.Tag == typeId);

    string SectionHeader(string id)
    {
        string name = _ctx!.SectionNames.TryGetValue(id, out var n) ? n : "options";
        int hidden = _ctx.Options.Count(o => o.TypeId == id && _work.GetOptionState(o.Tag) != OptionState.Shown);
        int edited = _ctx.Options.Count(o => o.TypeId == id && IsEdited(o.Tag));
        string extra = string.Join(", ", new[] { hidden > 0 ? $"{hidden} hidden or deleted" : null, edited > 0 ? $"{edited} changed" : null }.Where(x => x != null));
        return $"Section {id}: {name}" + (extra.Length > 0 ? $"   ({extra})" : "");
    }

    void UpdateHeaders()
    {
        foreach (ListViewGroup g in _list.Groups) g.Header = SectionHeader((string)g.Tag!);
    }

    /// <summary>Why an option can never be hidden, or null.</summary>
    static string? AlwaysShownReason(string tag) => tag switch
    {
        "fairAI" => "Always shown: it is on by default, so hiding it would lock it on.",
        "extraHuman" => "Always shown: the extra-unit entries depend on this switch.",
        _ => null,
    };

    static IReadOnlyList<OptionSettingDef> SettingsOf(string tag) => OptionCatalog.Embedded.Settings.Where(s => s.Options.Contains(tag)).ToList();

    bool IsEdited(string tag) => _work.Tuning.Texts.ContainsKey(tag) || SettingsOf(tag).Any(s => _work.Tuning.Values.ContainsKey(s.Id));

    void PaintRow(ListViewItem item)
    {
        var o = (OptionInfo)item.Tag!;
        var st = _work.GetOptionState(o.Tag);
        string? never = AlwaysShownReason(o.Tag);
        item.Text = _work.Tuning.Texts.TryGetValue(o.Tag, out var tt) && tt.Name != null ? tt.Name : o.Name;
        item.SubItems[1].Text = never != null ? "Shown" : st switch { OptionState.Hidden => "Hidden", OptionState.Deleted => "Deleted", _ => "Shown" };
        string note = never ?? (st == OptionState.Shown ? "" : st == OptionState.Hidden ? "Switched off here; its buttons are not shown in the game." : "Gone from the game's menu until put back.");
        if (IsEdited(o.Tag))
            note = (note.Length > 0 ? note + " " : "") + "Changed: "
                + string.Join(", ", new[] { _work.Tuning.Texts.ContainsKey(o.Tag) ? "text" : null, SettingsOf(o.Tag).Any(s => _work.Tuning.Values.ContainsKey(s.Id)) ? "numbers" : null }.Where(x => x != null)) + ".";
        item.SubItems[2].Text = note;
        item.ForeColor = st == OptionState.Shown ? SystemColors.WindowText : Color.Gray;
        item.Font = st == OptionState.Deleted ? _struck : st == OptionState.Hidden ? _italic : _list.Font;
        item.ToolTipText = (EffectiveDescription(o) ?? "").Replace("\\n", "  ", StringComparison.Ordinal);
    }

    ListViewItem? ItemOf(string tag) => _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is OptionInfo o && o.Tag == tag);

    OptionInfo? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as OptionInfo : null;

    /// <summary>The description as it will read: the user's, else the file's current one (stored form, literal \n).</summary>
    string? EffectiveDescription(OptionInfo o) =>
        _work.Tuning.Texts.TryGetValue(o.Tag, out var t) && t.Description != null ? t.Description
        : _opt?.Text(o.Tag, o.OnSection)?.CurrentDescription ?? o.Description.Replace("\n", "\\n", StringComparison.Ordinal);

    void ShowSelected()
    {
        if (_loading) return;   // the list is being rebuilt (clearing it raises a selection change while the copy's data is not there yet)
        var o = _ctx == null ? null : Selected;
        if (_ctx != null && _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is SpawnGroup g)
        {
            Plain(g.Name, "Your group in section 4", "Made and changed on the Start units page (units, who gets it, where they stand, on or off).");
            return;
        }
        if (o == null) { Plain(_ctx == null ? "No copy is open" : "Pick an option", "", _ctx == null ? "" : "Click an option in the list to see what it does, to hide or delete it, and to change its words and numbers."); return; }

        string sec = _ctx!.SectionNames.TryGetValue(o.TypeId, out var n) ? n : "options";
        _title.Text = _work.Tuning.Texts.TryGetValue(o.Tag, out var tt) && tt.Name != null ? tt.Name : o.Name;
        _where.Text = $"Section {o.TypeId}: {sec}";
        _what.Text = "In the game it says:\n" + (EffectiveDescription(o) is { Length: > 0 } d ? d.Replace("\\n", "\n", StringComparison.Ordinal) : "(no description)");
        string? never = AlwaysShownReason(o.Tag);
        var st = _work.GetOptionState(o.Tag);
        _stateHelp.Text = never ?? (st switch
        {
            OptionState.Hidden => "Hidden: switched off here, still in this list. In the game its buttons are not shown.",
            OptionState.Deleted => "Deleted: gone from the game's menu. Click Show to put it back (the game files still hold it).",
            _ => "Shown: as the mod ships it.",
        });
        bool can = CanChange && never == null;
        _toggle.Text = st == OptionState.Shown ? "Hide" : "Show";
        _toggle.Enabled = can;
        _delete.Enabled = can && st != OptionState.Deleted;
        _revertOption.Enabled = CanChange;
        _revertSection.Text = $"Put section {o.TypeId} back to the defaults";
        _revertSection.Enabled = CanChange;
        FillTextEditors(o);
        FillNumbers(o);
    }

    void Plain(string title, string where, string what)
    {
        _title.Text = title; _where.Text = where; _what.Text = what; _stateHelp.Text = "";
        _toggle.Enabled = false; _delete.Enabled = false; _revertSection.Enabled = false; _revertOption.Enabled = false;
        _revertSection.Text = "Put this section back to the defaults";
        _textBox.Visible = false; _numBox.Visible = false;
    }

    void FillTextEditors(OptionInfo o)
    {
        var ts = _opt?.Text(o.Tag, o.OnSection);
        bool has = ts is { ShippedName: not null } && _optProblem == null;
        _textBox.Visible = has;
        if (!has)
        {
            if (_optProblem != null) _status.Text = _optProblem;
            return;
        }
        _loading = true;
        try
        {
            _work.Tuning.Texts.TryGetValue(o.Tag, out var t);
            _nameBox.Text = t?.Name ?? ts!.CurrentName ?? o.Name;
            _descBox.Text = DescriptionRules.ForEditing(t?.Description ?? ts!.CurrentDescription ?? "");
            _nameBox.Enabled = _descBox.Enabled = CanChange;
            _nameHint.Text = ""; _descHint.Text = "";
            _textBack.Enabled = CanChange && t != null;
        }
        finally { _loading = false; }
        _tip.SetToolTip(_textBack, "Shipped name: " + ts!.ShippedName + "\nShipped description: " + (ts.ShippedDescription ?? "").Replace("\\n", "\n", StringComparison.Ordinal));
    }

    void FillNumbers(OptionInfo o)
    {
        _loading = true;
        try
        {
            foreach (Control c in _numRows.Controls.Cast<Control>().ToList()) { _numRows.Controls.Remove(c); c.Dispose(); }
            _numBoxes.Clear();
            var defs = SettingsOf(o.Tag);
            var states = _opt?.Settings.ToDictionary(s => s.Def.Id) ?? new Dictionary<string, SettingState>();
            if (defs.Count == 0 || _optProblem != null) { _numBox.Visible = false; return; }
            _numBox.Visible = true;
            foreach (var d in defs)
            {
                states.TryGetValue(d.Id, out var st);
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 6) };
                row.Controls.Add(new Label { Text = d.Label, AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
                if (st == null || !st.Available)
                {
                    row.Controls.Add(new Label { Text = "Not available in this copy: " + (st?.Reason ?? "the file is missing"), AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(150, 80, 0) });
                    _numRows.Controls.Add(row);
                    continue;
                }
                long cur = _work.Tuning.Values.TryGetValue(d.Id, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var pv) ? pv
                    : long.TryParse(st.Current ?? st.Shipped, NumberStyles.None, CultureInfo.InvariantCulture, out var cv) ? cv : d.Min;
                var box = new NumericUpDown { Minimum = d.Min, Maximum = d.Max, Value = Math.Clamp(cur, d.Min, d.Max), Width = 120, Enabled = CanChange, ThousandsSeparator = false };
                string id = d.Id;
                box.ValueChanged += (_, _) => { if (!_loading) AppHost.SafeRun(FindForm(), "changing the number", () => NumberTyped(id, (long)box.Value)); };
                _numBoxes[d.Id] = box;
                var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                var back = new Button { Text = "Put back", AutoSize = true, Enabled = CanChange };
                back.Click += (_, _) => AppHost.SafeRun(FindForm(), "putting the number back", () => PutNumberBack(id));
                line.Controls.AddRange(new Control[] { box, new Label { Text = d.Unit, AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, back });
                row.Controls.Add(line);
                row.Controls.Add(new Label { Text = $"As shipped: {st.Shipped}.  Allowed: {d.Min} to {d.Max}.", AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90) });
                row.Controls.Add(new Label
                {
                    Text = d.Description + (d.Options.Count > 1 ? $"\n(Shared by: {string.Join(", ", d.Options.Select(t => _ctx!.FindOption(t)?.Name ?? t))}.)" : ""),
                    AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.FromArgb(70, 80, 90),
                });
                _numRows.Controls.Add(row);
            }
        }
        finally { _loading = false; }
    }

    // ------------------------------------------------------------------------------------------------------------ actions
    void SetState(OptionInfo o, OptionState state)
    {
        if (!CanChange || AlwaysShownReason(o.Tag) != null) return;
        _work.SetOptionState(o.Tag, state);
        Refresh2(o.Tag);
    }

    void Refresh2(string? tag)
    {
        if (tag != null && ItemOf(tag) is { } it) PaintRow(it); else foreach (var i in _list.Items.Cast<ListViewItem>().Where(i => i.Tag is OptionInfo)) PaintRow(i);
        UpdateHeaders();
        ShowSelected();
        _check.Stop();
        Recheck();
    }

    void ToggleSelected() { if (Selected is { } o) SetState(o, _work.GetOptionState(o.Tag) == OptionState.Shown ? OptionState.Hidden : OptionState.Shown); }

    void DeleteSelected() { if (Selected is { } o) SetState(o, OptionState.Deleted); }

    /// <summary>The user typed a name: it is cleaned like a group name and kept as the choice (a name equal to the shipped one is no choice at all).</summary>
    void NameTyped(string typed)
    {
        if (Selected is not { } o || !CanChange || _opt == null) return;
        string clean = NameRules.Sanitize(typed);
        var shipped = _opt.Text(o.Tag, o.OnSection)?.ShippedName;
        _nameHint.Text = clean.Length == 0 ? "The name cannot be empty (the shipped name is kept)." : clean != typed ? "It will be saved as: " + clean : "";
        _work.Tuning.SetName(o.Tag, clean.Length == 0 || clean == shipped ? null : clean);
        TextChangedAfter(o);
    }

    void DescriptionTyped(string typed)
    {
        if (Selected is not { } o || !CanChange || _opt == null) return;
        string clean = DescriptionRules.Sanitize(typed, OptionCatalog.Embedded);
        var shipped = _opt.Text(o.Tag, o.OnSection)?.ShippedDescription;
        string asTyped = typed.Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        _descHint.Text = clean.Length == 0 ? "The description cannot be empty (the shipped text is kept)." : clean != asTyped.Trim() ? "It will be saved as: " + clean.Replace("\\n", " / ", StringComparison.Ordinal) : "";
        _work.Tuning.SetDescription(o.Tag, clean.Length == 0 || clean == shipped ? null : clean);
        TextChangedAfter(o);
    }

    void TextChangedAfter(OptionInfo o)
    {
        if (ItemOf(o.Tag) is { } it) PaintRow(it);
        UpdateHeaders();
        _what.Text = "In the game it says:\n" + (EffectiveDescription(o) is { Length: > 0 } d ? d.Replace("\\n", "\n", StringComparison.Ordinal) : "(no description)");
        _title.Text = _work.Tuning.Texts.TryGetValue(o.Tag, out var tt) && tt.Name != null ? tt.Name : o.Name;
        _textBack.Enabled = _work.Tuning.Texts.ContainsKey(o.Tag);
        _discard.Enabled = Dirty;
        _check.Stop(); _check.Start();   // the full check runs when typing pauses
    }

    void RevertText()
    {
        if (Selected is not { } o || !CanChange) return;
        _work.Tuning.Texts.Remove(o.Tag);
        Refresh2(o.Tag);
    }

    void NumberTyped(string id, long value)
    {
        if (!CanChange || _opt == null) return;
        var shipped = _opt.Settings.FirstOrDefault(s => s.Def.Id == id)?.Shipped;
        string text = value.ToString(CultureInfo.InvariantCulture);
        _work.Tuning.SetValue(id, shipped == text ? null : text);
        RepaintRowsOf(id);
        _check.Stop(); _check.Start();
    }

    void PutNumberBack(string id)
    {
        if (!CanChange) return;
        _work.Tuning.Values.Remove(id);
        RepaintRowsOf(id);
        ShowSelected();
        _check.Stop();
        Recheck();
    }

    void RepaintRowsOf(string id)
    {
        var def = OptionCatalog.Embedded.Find(id);
        if (def != null) foreach (var tag in def.Options) if (ItemOf(tag) is { } it) PaintRow(it);
        UpdateHeaders();
        _discard.Enabled = Dirty;
    }

    void RevertOption()
    {
        if (!CanChange || Selected is not { } o) return;
        _work.RevertOptions(new[] { o.Tag });
        _work.Tuning.Revert(new[] { o.Tag }, SettingsOf(o.Tag).Select(s => s.Id));
        Refresh2(null);
    }

    void RevertSection()
    {
        if (!CanChange || Selected is not { } o) return;
        var tags = _ctx!.Options.Where(x => x.TypeId == o.TypeId).Select(x => x.Tag).ToList();
        _work.RevertOptions(tags);
        _work.Tuning.Revert(tags, tags.SelectMany(t => SettingsOf(t).Select(s => s.Id)));
        Refresh2(null);
    }

    void RevertAll()
    {
        if (!CanChange) return;
        if (_work.StartOptions.Count == 0 && _work.Tuning.IsEmpty) { _status.Text = "Every option is already as the mod ships it."; return; }
        if (!Ask.Confirm(FindForm(), "Put every option back?", "Every hidden or deleted option is shown again and every changed name, description and number goes back to what the mod ships (here; click Apply to write it into your copy). Your groups are not touched.",
                Array.Empty<string>(), "Put them back", "Keep them as they are")) return;
        RevertAllNow();
    }

    void RevertAllNow()
    {
        _work.RevertOptions(_ctx!.Options.Select(x => x.Tag));
        _work.Tuning = new OptionTuning();
        Refresh2(null);
    }

    void Discard()
    {
        if (_session == null) return;
        if (Dirty && !Ask.Confirm(FindForm(), "Discard your changes here?", "The options go back to what is applied to your copy now.", Array.Empty<string>(), "Discard", "Keep working")) return;
        Bind(_session);
    }

    /// <summary>Checks the working settings with the same rules as the apply and fills the status line and the Apply button.</summary>
    void Recheck()
    {
        if (_loading) return;
        _discard.Enabled = CanChange && Dirty;
        if (_ctx == null || _session == null) { _apply.Enabled = false; return; }
        var findings = SettingsValidator.Validate(_work, _ctx);
        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        _apply.Enabled = CanChange && Dirty && errors.Count == 0;
        int hidden = _work.StartOptions.Count;
        int changed = _work.Tuning.Texts.Count + _work.Tuning.Values.Count;
        string counts = $"{hidden} option(s) hidden or deleted, {changed} text or number change(s) in all.";
        var warn = findings.FirstOrDefault(f => f.Severity == Severity.Warning && f.Code.StartsWith("OPTION_", StringComparison.Ordinal));
        if (errors.Count > 0) _status.Text = "Cannot apply: " + errors[0].Message;
        else if (Dirty) _status.Text = "Ready: click \"Apply to my copy\". " + counts + (warn != null ? " Note: " + warn.Message : "");
        else _status.Text = hidden == 0 && changed == 0 ? "Everything is as the mod ships it." : $"Everything shown here is already applied to your copy ({counts})";
    }

    async Task ApplyAsync()
    {
        if (_session == null || _ctx == null) return;
        _check.Stop();
        Recheck();
        if (!_apply.Enabled) return;
        if (OtherPageDirty?.Invoke() == true)
        {
            Ask.Info(FindForm(), "Finish the Start units page first", "The Start units page has changes that are not applied yet. Applying here re-reads your copy and would lose them. Apply or discard them there first, then come back.");
            return;
        }
        await _edit().ApplyGroupsAsync(_work, tag => _ctx?.FindOption(tag)?.Name ?? tag);
    }

    // ------------------------------------------------------------------------------------------------------------ test hooks
    public string StateOf(string tag) => ItemOf(tag)?.SubItems[1].Text ?? "";
    public string NameOf(string tag) => ItemOf(tag)?.Text ?? "";
    public string NoteOf(string tag) => ItemOf(tag)?.SubItems[2].Text ?? "";
    public bool SelectOptionForTest(string tag) { var it = ItemOf(tag); if (it == null) return false; it.Selected = true; ShowSelected(); return true; }
    public string ToggleButtonText => _toggle.Text;
    public bool ToggleEnabled => _toggle.Enabled;
    public bool DeleteEnabled => _delete.Enabled;
    public bool TextVisible => _textBox.Visible;
    public bool NumbersVisible => _numBox.Visible;
    public IReadOnlyCollection<string> NumberIds => _numBoxes.Keys;
    public string NameBoxText => _nameBox.Text;
    public string DescriptionBoxText => _descBox.Text;
    public string NameHint => _nameHint.Text;
    public decimal NumberValue(string id) => _numBoxes[id].Value;
    public void ToggleForTest() => ToggleSelected();
    public void DeleteForTest() => DeleteSelected();
    /// <summary>Types into the boxes like the user (the TextChanged / ValueChanged path).</summary>
    public void TypeNameForTest(string text) => _nameBox.Text = text;
    public void TypeDescriptionForTest(string text) => _descBox.Text = text;
    public void SetNumberForTest(string id, long value) => _numBoxes[id].Value = value;
    public void RevertTextForTest() => RevertText();
    public void RevertOptionForTest() => RevertOption();
    public void RevertSectionForTest() => RevertSection();
    public void RevertAllForTest() => RevertAllNow();
    public Task<bool> ApplyForTest() { _check.Stop(); Recheck(); return _apply.Enabled ? _edit().ApplyGroupsAsync(_work, tag => _ctx?.FindOption(tag)?.Name ?? tag) : Task.FromResult(false); }
}
