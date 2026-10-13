using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using RwEditor.Core.Browse;
using RwEditor.Core.Model;

namespace RwEditor.App;

/// <summary>
/// The unit browser (E2, families since E2b): search box, filters, and a virtual owner-drawn list of FAMILIES: every complete unit / building is one
/// top-level row with an expand arrow; expanding shows its upgrade levels, water / land forms, nano builds, attached parts and spawned helpers with their
/// relation, and two part lists hold the helpers that belong to no single unit (Core <see cref="UnitFamilies"/> / <see cref="FamilyView"/>). Only the
/// visible rows are ever drawn; thumbnails are decoded on first draw. The details pane (E3) shows and EDITS the Simple / Expand stats of the selected
/// entry; the "Pending changes" panel at the bottom lists what is staged and applies it.
/// </summary>
sealed class BrowserView : UserControl
{
    const int ThumbLogical = 64;
    const int IndentLogical = 28;   // per tree level inside a family
    const int ArrowLogical = 20;    // the expand arrow at the left of the first cell

    readonly TextBox _search = new() { PlaceholderText = "Type a unit name, e.g. hover tank", Width = 260 };
    readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Label _count = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly CheckBox _showLocked = new() { Text = "Show locked mod machinery", AutoSize = true, Padding = new Padding(8, 3, 0, 0) };
    readonly ListView _list = new()
    {
        View = View.Details, VirtualMode = true, FullRowSelect = true, MultiSelect = false, HideSelection = false, OwnerDraw = true,
        HeaderStyle = ColumnHeaderStyle.Clickable, Dock = DockStyle.Fill,
    };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
    readonly DetailsPane _details = new() { Dock = DockStyle.Fill };
    readonly PendingPanel _pending = new() { Dock = DockStyle.Bottom };
    readonly ToolTip _tip = new();
    EditController? _edit;
    readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 200 };

    ModSession? _session;
    ThumbImages? _thumbs;
    FamilyListing? _listing;
    IReadOnlyList<DisplayRow> _view = Array.Empty<DisplayRow>();
    readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);   // families the search opened by itself and the user closed
    string _lastSearch = "";
    SortKey _sort = SortKey.Title;
    bool _desc;
    bool _syncingArea;
    int _rowHeight;
    ImageList? _rowSizer;
    Font? _boldFont;

    /// <summary>Process-start-to-first-drawn-row time, set once (for the timing log).</summary>
    public double? FirstRowPaintMs { get; private set; }
    public event Action<string?>? AreaChanged;
    public ListView List => _list;
    public DetailsPane Details => _details;
    public PendingPanel Pending => _pending;

    /// <summary>The editing flows (E3); set once by the main window.</summary>
    public void SetEditor(EditController edit)
    {
        _edit = edit;
        _pending.SetEditor(edit);
        _details.Bind(_session, edit);
    }

    /// <summary>The pending list or its checks changed (no file of the mod changed): refresh the rows shown and the pending panel.</summary>
    public void RefreshEditing()
    {
        _details.RefreshRows();
        _pending.ShowSession(_session);
    }
    /// <summary>Visible rows (top-level rows plus the rows of expanded families).</summary>
    public int ViewCount => _view.Count;
    public IReadOnlyList<DisplayRow> VisibleRows => _view;
    public FamilyListing? Listing => _listing;
    public string CountText => _count.Text;

    public BrowserView()
    {
        _type.Items.AddRange(new object[] { "Units and buildings", "Units only", "Buildings only" });
        _type.SelectedIndex = 0;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 2), WrapContents = true };
        bar.Controls.Add(new Label { Text = "Find:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        bar.Controls.Add(_search);
        bar.Controls.Add(new Label { Text = "Show:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        bar.Controls.Add(_type);
        bar.Controls.Add(new Label { Text = "Mod:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        bar.Controls.Add(_area);
        bar.Controls.Add(_showLocked);
        bar.Controls.Add(_count);
        _tip.SetToolTip(_showLocked, "Also list the mod's own machinery (markers, menus, spawners). They are locked: the editor never changes them.");
        _showLocked.CheckedChanged += (_, _) => AppHost.SafeRun(FindForm(), "filtering", Requery);
        _tip.SetToolTip(_search, "Every word you type must appear in the unit's name (in-game or internal). Not case-sensitive. Finds units inside a family too: their family opens.");
        _tip.SetToolTip(_type, "Mobile units, buildings, or both.");
        _tip.SetToolTip(_area, "Which of the mods in the collection the unit comes from.");

        _list.Columns.Add("Unit", 360);
        _list.Columns.Add("Internal name", 150);
        _list.Columns.Add("Mod", 170);
        _list.Columns.Add("Kind", 85);
        _list.Columns.Add("Folder", 115);
        _list.RetrieveVirtualItem += OnRetrieve;
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, e) => { };
        _list.DrawSubItem += OnDrawSubItem;
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "showing a unit", ShowSelected);
        _list.MouseDown += (_, e) => AppHost.SafeRun(FindForm(), "opening a family", () => ListMouseDown(e));
        _list.DoubleClick += (_, _) => AppHost.SafeRun(FindForm(), "opening a family", () => { if (SelectedIndex is int i && _view[i].HasChildren) Toggle(i); });
        _list.KeyDown += (_, e) => AppHost.SafeRun(FindForm(), "opening a family", () => ListKeyDown(e));

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_details);
        Controls.Add(_split);
        Controls.Add(_pending);
        Controls.Add(bar);

        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); AppHost.SafeRun(FindForm(), "searching", Requery); };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _searchDelay.Stop(); AppHost.SafeRun(FindForm(), "searching", Requery); } };
        _type.SelectedIndexChanged += (_, _) => AppHost.SafeRun(FindForm(), "filtering", Requery);
        _area.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingArea) return;
            AppHost.SafeRun(FindForm(), "filtering", Requery);
            AreaChanged?.Invoke(SelectedArea);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _rowHeight = LogicalToDeviceUnits(ThumbLogical + 6);
        _rowSizer?.Dispose();
        _rowSizer = new ImageList { ImageSize = new Size(1, Math.Min(256, _rowHeight)) };   // the only way to set a details-view row height
        _list.SmallImageList = _rowSizer;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _boldFont?.Dispose(); _rowSizer?.Dispose(); _searchDelay.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    bool _splitPlaced;

    /// <summary>The details pane gets a fixed, readable width the first time the view has its real size (at handle creation it is still tiny).</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pane = LogicalToDeviceUnits(800);   // E3: the editing table needs room (it scrolls sideways when narrower)
        if (_splitPlaced || _split.Width < LogicalToDeviceUnits(360 + 520)) return;
        pane = Math.Min(pane, _split.Width - LogicalToDeviceUnits(360));
        try
        {
            _split.Panel1MinSize = LogicalToDeviceUnits(360);
            _split.Panel2MinSize = LogicalToDeviceUnits(520);
            _split.SplitterDistance = _split.Width - pane;
            _splitPlaced = true;
        }
        catch (InvalidOperationException) { }
    }

    string? SelectedArea => _area.SelectedItem is AreaItem a ? a.Id : null;

    sealed record AreaItem(string? Id, string Label) { public override string ToString() => Label; }

    public void Bind(ModSession session, ThumbImages thumbs)
    {
        _session = session;
        _thumbs = thumbs;
        _shown = null;
        if (_edit != null) _details.Bind(session, _edit);
        _pending.ShowSession(session);
        _expanded.Clear();
        _collapsed.Clear();
        _syncingArea = true;
        _area.Items.Clear();
        _area.Items.Add(new AreaItem(null, "All mods"));
        foreach (var a in session.Model.Areas.Areas) _area.Items.Add(new AreaItem(a.Id, $"{a.Label} ({session.Rows.Count(r => r.Area == a.Id):N0})"));
        _area.SelectedIndex = 0;
        _syncingArea = false;
        Requery();
        if (_view.Count > 0) Select(0);
    }

    /// <summary>Called by the navigation tree.</summary>
    public void SetArea(string? areaId)
    {
        if (_session == null) return;
        _syncingArea = true;
        for (int i = 0; i < _area.Items.Count; i++)
            if (_area.Items[i] is AreaItem a && a.Id == areaId) { _area.SelectedIndex = i; break; }
        _syncingArea = false;
        Requery();
    }

    public void SetSearch(string text) { _search.Text = text; _searchDelay.Stop(); Requery(); }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowLocked { get => _showLocked.Checked; set => _showLocked.Checked = value; }

    /// <summary>The selection survives a re-query by this key: an entry by its file, a family / bucket row by its family key.</summary>
    static string SelKey(DisplayRow r) => r.Kind == DisplayKind.Child ? "entry:" + r.Row!.Unit.RelPath : r.Key;

    public void Requery() => Requery(null);

    void Requery(string? keepKey)
    {
        if (_session == null) { _view = Array.Empty<DisplayRow>(); _list.VirtualListSize = 0; return; }
        var sw = Stopwatch.StartNew();
        var sel = SelectedRow;
        keepKey ??= sel == null ? null : SelKey(sel);
        string? keepFamily = sel?.Key;
        if (_search.Text != _lastSearch) { _collapsed.Clear(); _lastSearch = _search.Text; }
        var type = _type.SelectedIndex switch { 1 => TypeFilter.Units, 2 => TypeFilter.Buildings, _ => TypeFilter.All };
        var q = new UnitQuery(_search.Text, SelectedArea, type, _sort, _desc, IncludeLocked: _showLocked.Checked);
        _listing = FamilyView.Build(_session.Families, _session.Rows, q, _expanded, _collapsed);
        _view = _listing.Rows;
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = _view.Count;
        _list.Invalidate();
        _count.Text = _listing.StatusText;
        int idx = keepKey == null ? -1 : IndexOf(r => SelKey(r) == keepKey);
        if (idx < 0 && keepFamily != null) idx = IndexOf(r => r.Kind != DisplayKind.Child && r.Key == keepFamily);   // its family was closed: the family row
        if (idx >= 0) Select(idx);
        else if (_view.Count > 0) Select(0);      // the details pane is never left empty while units are listed
        else _details.ShowNothing(_search.Text.Length > 0 ? "No unit matches your search." : "No units here.");
        AppHost.Log.Info($"query '{_search.Text}' area={SelectedArea ?? "all"} type={type} sort={_sort}{(_desc ? " desc" : "")}: {_listing.Units} units, " +
            $"{_listing.Inside} inside, {_view.Count} rows in {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    int IndexOf(Func<DisplayRow, bool> match)
    {
        for (int i = 0; i < _view.Count; i++) if (match(_view[i])) return i;
        return -1;
    }

    /// <summary>Opens or closes the family / part list on row <paramref name="index"/>; the selection stays on that row.</summary>
    public void Toggle(int index)
    {
        if (_listing == null || index < 0 || index >= _view.Count) return;
        var r = _view[index];
        if (!r.HasChildren) return;
        if (r.Expanded) { _expanded.Remove(r.Key); if (_listing.AutoExpanded.Contains(r.Key)) _collapsed.Add(r.Key); }
        else { _expanded.Add(r.Key); _collapsed.Remove(r.Key); }
        Requery(r.Kind == DisplayKind.Child ? SelKey(r) : r.Key);
    }

    /// <summary>Opens every family and part list (true) or closes them all (false).</summary>
    public void ExpandAll(bool open)
    {
        if (_session == null) return;
        _expanded.Clear();
        _collapsed.Clear();
        if (open) foreach (var f in _session.Families.Items) _expanded.Add(FamilyView.KeyOf(f));
        Requery();
    }

    /// <summary>Selects the row of a unit (opening its family when needed). Returns false when the unit is filtered out.</summary>
    public bool SelectUnit(string name)
    {
        if (_session == null) return false;
        var u = _session.Model.Find(name);
        if (u == null) return false;
        var fam = _session.Families.FamilyOf(u);
        string key = FamilyView.KeyOf(fam);
        if (!ReferenceEquals(fam.Root, u)) { _expanded.Add(key); _collapsed.Remove(key); }
        Requery(ReferenceEquals(fam.Root, u) ? key : "entry:" + u.RelPath);
        int i = IndexOf(r => r.Row?.Unit == u);
        if (i < 0) return false;
        Select(i);
        return true;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _view.Count) return;
        _list.SelectedIndices.Clear();
        _list.SelectedIndices.Add(index);
        if (_list.IsHandleCreated) _list.EnsureVisible(index);
        ShowRow(_view[index]);   // directly: before the list has a window handle its selection is not reported back yet
    }

    int? SelectedIndex => _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _view.Count ? _list.SelectedIndices[0] : null;

    public DisplayRow? SelectedRow => SelectedIndex is int i ? _view[i] : null;

    void ShowSelected()
    {
        if (_session == null || _thumbs == null) return;
        var row = SelectedRow;
        if (row != null) ShowRow(row);
    }

    DisplayRow? _shown;

    void ShowRow(DisplayRow row)
    {
        if (_session == null || _thumbs == null) return;
        if (_shown != null && SelKey(_shown) == SelKey(row) && _shown.Expanded == row.Expanded && (row.Row == null || _details.HasPicture == _thumbs.Ready)) return;
        _shown = row;
        if (row.Row == null) { _details.ShowBucket(row.Family, row.ChildCount); return; }
        var d = StatView.Describe(_session.Model, _session.Dict, row.Row);
        _details.Show(d, _thumbs, FamilyLine(row));
    }

    /// <summary>The "where does this entry belong" line of the details pane.</summary>
    string FamilyLine(DisplayRow row)
    {
        var f = row.Family;
        if (row.Kind == DisplayKind.Root)
            return f.Members.Count == 0 ? "Family: nothing else belongs to this unit" : $"Family: {f.Members.Count:N0} entries inside (open the row with the arrow)";
        var e = row.Entry!;
        if (f.IsBucket) return $"In \"{f.BucketTitle}\": {e.Label}";
        var rootRow = _session!.RowOf(f.Root!);
        return $"In the family of {rootRow.Title} ({rootRow.Name}): {e.Label}";
    }

    void OnRetrieve(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count) { e.Item = new ListViewItem(""); return; }
        var r = _view[e.ItemIndex];
        if (r.Row == null)
        {
            var b = new ListViewItem(r.Title) { ToolTipText = r.Title };
            for (int i = 0; i < 4; i++) b.SubItems.Add("");
            e.Item = b;
            return;
        }
        var u = r.Row;
        var it = new ListViewItem(u.Title) { ToolTipText = r.Entry != null ? $"{u.Title} ({u.Name}) - {r.Entry.Label}" : $"{u.Title} ({u.Name})" };
        it.SubItems.Add(u.Name);
        it.SubItems.Add(u.AreaLabel);
        it.SubItems.Add(u.Kind);
        it.SubItems.Add(u.Category);
        e.Item = it;
    }

    void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var key = e.Column switch { 1 => SortKey.Name, 2 => SortKey.Area, 3 => SortKey.Kind, 4 => SortKey.Category, _ => SortKey.Title };
        _desc = key == _sort && !_desc;
        _sort = key;
        AppHost.SafeRun(FindForm(), "sorting", Requery);
    }

    Rectangle ArrowRect(Rectangle rowBounds, DisplayRow r)
    {
        int indent = LogicalToDeviceUnits(IndentLogical) * r.Depth;
        return new Rectangle(rowBounds.X + 2 + indent, rowBounds.Y, LogicalToDeviceUnits(ArrowLogical), rowBounds.Height);
    }

    void ListMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item == null) return;
        int i = hit.Item.Index;
        if (i < 0 || i >= _view.Count || !_view[i].HasChildren) return;
        var arrow = ArrowRect(hit.Item.Bounds, _view[i]);
        arrow.Inflate(LogicalToDeviceUnits(4), 0);
        if (arrow.Contains(e.Location)) Toggle(i);
    }

    void ListKeyDown(KeyEventArgs e)
    {
        if (SelectedIndex is not int i) return;
        var r = _view[i];
        bool open = e.KeyCode is Keys.Right or Keys.Add, close = e.KeyCode is Keys.Left or Keys.Subtract;
        if (!open && !close) return;
        e.Handled = true;
        if (r.HasChildren && open != r.Expanded) { Toggle(i); return; }
        if (close && r.Kind == DisplayKind.Child)
        {
            int parent = IndexOf(x => x.Kind != DisplayKind.Child && x.Key == r.Key);
            if (parent >= 0) Select(parent);
        }
    }

    void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _view.Count || e.Item == null || e.SubItem == null) return;
        try
        {
            var r = _view[e.ItemIndex];
            bool selected = _list.SelectedIndices.Contains(e.ItemIndex);
            var g = e.Graphics;
            var back = selected ? SystemColors.Highlight
                : r.Kind == DisplayKind.Bucket ? Color.FromArgb(236, 238, 244)
                : r.Kind == DisplayKind.Child ? Color.FromArgb(250, 250, 252)
                : (e.ItemIndex % 2 == 0 ? SystemColors.Window : Color.FromArgb(246, 246, 248));
            var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
            var dim = selected ? fore : SystemColors.GrayText;
            using (var bb = new SolidBrush(back)) g.FillRectangle(bb, e.Bounds);
            var bounds = e.Bounds;
            if (e.ColumnIndex != 0)
            {
                TextRenderer.DrawText(g, e.SubItem.Text, _list.Font, new Rectangle(bounds.X + 4, bounds.Y, bounds.Width - 6, bounds.Height), r.Context ? dim : fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }

            // tree guide for entries inside a family: a thin line at the family's left edge
            if (r.Kind == DisplayKind.Child)
            {
                using var guide = new Pen(selected ? fore : Color.FromArgb(200, 204, 214), Math.Max(1, LogicalToDeviceUnits(2)));
                int gx = bounds.X + 2 + LogicalToDeviceUnits(ArrowLogical) / 2;
                g.DrawLine(guide, gx, bounds.Y, gx, bounds.Bottom);
            }
            var arrow = ArrowRect(bounds, r);
            if (r.HasChildren) DrawArrow(g, arrow, r.Expanded, selected ? fore : Color.FromArgb(70, 76, 90));

            int ts = LogicalToDeviceUnits(ThumbLogical);
            var cell = new Rectangle(arrow.Right + 4, bounds.Y + (bounds.Height - ts) / 2, ts, ts);
            if (r.Row == null) DrawBucketGlyph(g, cell, selected ? fore : Color.FromArgb(110, 118, 136));
            else
            {
                var bmp = _thumbs?.Get(r.Row.Unit.RelPath);
                if (bmp != null)
                {
                    var oldMode = g.InterpolationMode; var oldPix = g.PixelOffsetMode;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(bmp, cell);
                    g.InterpolationMode = oldMode; g.PixelOffsetMode = oldPix;
                }
                else
                {
                    using var pb = new SolidBrush(Color.FromArgb(225, 225, 228));
                    g.FillRectangle(pb, cell);
                }
            }

            var textRect = new Rectangle(cell.Right + 8, bounds.Y, Math.Max(0, bounds.Right - cell.Right - 10), bounds.Height);
            _boldFont ??= new Font(_list.Font, FontStyle.Bold);
            var titleFont = r.Kind == DisplayKind.Bucket ? _boldFont : _list.Font;
            TextRenderer.DrawText(g, r.Title, titleFont, new Rectangle(textRect.X, textRect.Y + 4, textRect.Width, textRect.Height / 2), r.Context ? dim : fore,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var (sub, subColor) = Subtitle(r, selected, fore);
            TextRenderer.DrawText(g, sub, _list.Font, new Rectangle(textRect.X, textRect.Y + textRect.Height / 2 + 2, textRect.Width, textRect.Height / 2 - 4),
                subColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (FirstRowPaintMs == null && _thumbs?.Ready == true && r.Row != null)
            {
                FirstRowPaintMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                AppHost.Log.Info($"first unit row with thumbnail drawn {FirstRowPaintMs:0} ms after process start");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
        {
            AppHost.Log.Warn($"row {e.ItemIndex} could not be drawn: {ex.Message}");
        }
    }

    static (string Text, Color Color) Subtitle(DisplayRow r, bool selected, Color fore)
    {
        var gray = selected ? fore : SystemColors.GrayText;
        if (r.Row == null) return ($"{r.ChildCount:N0} {(r.ChildCount == 1 ? "entry" : "entries")}", gray);
        var u = r.Row;
        string what = u.Locked ? "locked (mod machinery)" : u.IsBuilding ? "building" : "unit";
        var whatColor = selected ? fore : (u.Locked ? Color.FromArgb(160, 60, 0) : SystemColors.GrayText);
        if (r.Kind == DisplayKind.Child)
            return ($"{r.Entry!.Label} · {what}", selected ? fore : (u.Locked ? Color.FromArgb(160, 60, 0) : Color.FromArgb(40, 90, 160)));
        string inside = r.ChildCount > 0 ? $" · {r.ChildCount:N0} inside" : "";
        string context = r.Context ? " · listed for what is inside" : "";
        return (what + inside + context, whatColor);
    }

    static void DrawArrow(Graphics g, Rectangle r, bool expanded, Color color)
    {
        int s = Math.Max(4, Math.Min(r.Width, r.Height) / 3);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        Point[] pts = expanded
            ? new[] { new Point(cx - s, cy - s / 2), new Point(cx + s, cy - s / 2), new Point(cx, cy + s / 2 + 1) }
            : new[] { new Point(cx - s / 2, cy - s), new Point(cx - s / 2, cy + s), new Point(cx + s / 2 + 1, cy) };
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(color)) g.FillPolygon(b, pts);
        g.SmoothingMode = old;
    }

    /// <summary>A neutral "stack of parts" glyph for the two part lists (no unit picture of their own).</summary>
    static void DrawBucketGlyph(Graphics g, Rectangle cell, Color color)
    {
        int w = cell.Width * 5 / 8, h = cell.Height * 3 / 16, gap = cell.Height / 16;
        int top = cell.Y + (cell.Height - (3 * h + 2 * gap)) / 2;
        using var pen = new Pen(color, Math.Max(1, cell.Width / 32f));
        using var fill = new SolidBrush(Color.FromArgb(40, color));
        for (int i = 0; i < 3; i++)
        {
            var box = new Rectangle(cell.X + (cell.Width - w) / 2 + (i - 1) * cell.Width / 12, top + i * (h + gap), w, h);
            g.FillRectangle(fill, box);
            g.DrawRectangle(pen, box);
        }
    }

    /// <summary>Automated scroll test: pages through every visible row, painting each page synchronously. Returns (pages, total ms, worst page ms, rows).</summary>
    public (int Pages, double TotalMs, double WorstMs, int Rows) ScrollTest()
    {
        int perPage = Math.Max(1, _list.ClientSize.Height / Math.Max(1, _rowHeight));
        int pages = 0; double worst = 0;
        var total = Stopwatch.StartNew();
        for (int i = 0; i < _view.Count; i += perPage)
        {
            var sw = Stopwatch.StartNew();
            _list.EnsureVisible(Math.Min(_view.Count - 1, i + perPage - 1));
            _list.Update();   // paint now
            pages++;
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
        }
        if (_view.Count > 0) { _list.EnsureVisible(0); _list.Update(); }
        return (pages, total.Elapsed.TotalMilliseconds, worst, _view.Count);
    }
}

