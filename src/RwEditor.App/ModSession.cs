using System.Diagnostics;
using System.Drawing.Imaging;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Browse;
using RwEditor.Core.Imaging;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Setup;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>One opened mod folder, loaded ONCE into RAM on a background thread: the unit model, the browser rows and the stat dictionary.
/// <see cref="IsEditorCopy"/> = it carries a valid editor-copy marker (rweditor-copy.json) and passes the folder guard; anything else is shown read-only.</summary>
sealed class ModSession
{
    ModSession(string root, UnitModel model, IReadOnlyList<UnitRow> rows, UnitFamilies families, string? gameDir, EditorCopyMarker? marker, string? notCopyReason,
        string title, IReadOnlyList<string> warnings, TimeSpan loadTime, TimeSpan familyTime)
    {
        Root = root; Model = model; Rows = rows; Families = families; GameDir = gameDir; Marker = marker; NotCopyReason = notCopyReason; Title = title;
        Warnings = warnings; LoadTime = loadTime; FamilyTime = familyTime;
    }

    // ------------------------------------------------------------------------------------------------------------ E3: editing state
    /// <summary>Why values cannot be changed in this folder (null = they can): not a copy made by the editor, protected files changed, or the copy's
    /// list of changes cannot be read.</summary>
    public string? EditBlockedReason { get; private set; }
    /// <summary>True when the reason is a damaged settings file (the banner then offers to set it aside).</summary>
    public bool SettingsDamaged { get; private set; }
    public bool CanEdit => IsEditorCopy && EditBlockedReason == null;
    public string SettingsPath => CopyFiles.SettingsPath(Root);
    /// <summary>The copy's settings (applied changes + pending list); defaults when the file does not exist yet.</summary>
    public EditorSettings Settings { get; private set; } = EditorSettings.Default();
    /// <summary>The copy as it was when it was made (saved original values), for the "Original" column; null when the copy has none.</summary>
    public UnitModel? OriginalModel { get; private set; }
    /// <summary>Plain words about the saved original values (none / damaged), or null when they are fine.</summary>
    public string? BaselineNote { get; private set; }

    public byte[] ReadDisk(string rel) => File.ReadAllBytes(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Reads the copy's settings file again (after a stage / apply / undo).</summary>
    public void ReloadSettings()
    {
        if (!File.Exists(SettingsPath)) { Settings = EditorSettings.Default(); return; }
        Settings = StatPipeline.LoadSettings(SettingsPath).Settings;
    }

    /// <summary>The value the stat had when the copy was made (null when unknown: no saved original, or the unit / section did not exist then).</summary>
    public StatRef? Original(UnitType u, StatEntry e, string section)
    {
        var om = OriginalModel;
        if (om == null) return null;
        var ou = om.UnitsInFile(u.RelPath).FirstOrDefault(x => x.Name == u.Name);
        if (ou == null) return null;
        var m = om.Merge(ou);
        return m.Section(section) == null && e.FixedSection == null ? null : StatReader.Read(m, e, section);
    }

    /// <summary>The pending change of one stat of one unit, or null.</summary>
    public PendingStatEdit? PendingFor(UnitType u, StatEntry e, string section) =>
        Settings.PendingStatEdits.FirstOrDefault(p => p.UnitFile.Equals(u.RelPath, StringComparison.OrdinalIgnoreCase) && p.Unit == u.Name
            && p.StatId.Equals(e.Id, StringComparison.OrdinalIgnoreCase) && p.Section.Equals(section, StringComparison.OrdinalIgnoreCase));

    /// <summary>E3 part of loading (any thread): settings, protected-file check, saved original values. Never throws: problems become plain reasons.</summary>
    void LoadEditing()
    {
        if (!IsEditorCopy) { EditBlockedReason = "This folder is not a copy made by this editor, so nothing in it can be changed here. " + NotCopyReason; return; }
        try
        {
            var support = EditorSupport.Load(Root);
            var locks = support.VerifyLockedFiles(Root);
            if (!locks.Ok) { EditBlockedReason = "Some protected files of this copy were changed outside the editor, so the editor will not change it: " + locks.Describe(); return; }
        }
        catch (EditorRefusalException e) { EditBlockedReason = "This copy's editor-support.json cannot be used: " + e.Message; return; }
        try { ReloadSettings(); }
        catch (EditorRefusalException e)
        {
            SettingsDamaged = true;
            EditBlockedReason = $"The list of your changes ({CopyFiles.SettingsName}) cannot be read: {e.Message}";
            return;
        }
        try
        {
            var bl = CopyBaseline.Load(Root);
            if (bl == null) BaselineNote = "This copy was made by an older editor version: its original values were not saved, so the \"Original\" column is empty. Everything you change can still be undone and put back.";
            else
            {
                var changed = bl.ChangedTexts(Model.Snapshot);
                OriginalModel = changed.Count == 0 ? Model : Model.WithFiles(changed);
            }
        }
        catch (EditorRefusalException e) { BaselineNote = e.Message; }
    }

    /// <summary>The copy's damaged settings file is moved aside (renamed, never deleted) so a new list can start. Returns the new name.</summary>
    public string SetDamagedSettingsAside()
    {
        string aside = SettingsPath + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(SettingsPath, aside);
        return aside;
    }

    public string Root { get; }
    public UnitModel Model { get; }
    public IReadOnlyList<UnitRow> Rows { get; }
    /// <summary>The units grouped into families (complete units at the top, their variants and parts inside, two part buckets), from the data links.</summary>
    public UnitFamilies Families { get; }
    /// <summary>Time spent reading the links and building the families (part of <see cref="LoadTime"/>).</summary>
    public TimeSpan FamilyTime { get; }

    Dictionary<UnitType, UnitRow>? _rowOf;
    /// <summary>The browser row of a unit (rows are in the model's unit order).</summary>
    public UnitRow RowOf(UnitType u)
    {
        _rowOf ??= Rows.ToDictionary(r => r.Unit);
        return _rowOf[u];
    }
    public StatDictionary Dict => StatDictionary.Embedded;
    public string? GameDir { get; }
    public EditorCopyMarker? Marker { get; }
    /// <summary>Why the folder is not an editable copy (null when it is one).</summary>
    public string? NotCopyReason { get; }
    public bool IsEditorCopy => NotCopyReason == null;
    public string Title { get; }
    public IReadOnlyList<string> Warnings { get; }
    public TimeSpan LoadTime { get; }

    /// <summary>Loads a mod folder (any thread). Throws EditorRefusalException with a plain message when the folder cannot be shown at all.</summary>
    public static ModSession Load(string folder, string? gameDir)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(folder)) throw new EditorRefusalException("FOLDER_MISSING", "No folder was chosen.");
        string root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (!Directory.Exists(root)) throw new EditorRefusalException("FOLDER_MISSING", $"The folder {root} does not exist any more (moved or deleted?). Make a new copy or open another folder.");
        var warnings = new List<string>();
        UnitModel model;
        try { model = UnitModel.Load(root); }
        catch (EditorRefusalException e)
        {
            // a damaged editor-support.json must not stop the browser: show the units with the standard lock list, read-only
            warnings.Add("This folder's editor-support.json cannot be used (" + e.Message + "). Units are shown read-only with the standard locks.");
            model = UnitModel.Build(ModSnapshot.Load(root), ModAreas.Embedded, EditorSupport.DefaultLockedGlobs);
        }
        if (model.Units.Count == 0)
            throw new EditorRefusalException("NO_UNITS", $"No units were found in {root}. Pick the mod's own folder (the one that holds mod-info.txt and the unit folders).");
        var rows = UnitCatalog.Build(model);          // merges every unit once (the model memo is complete afterwards)
        StatView.Describe(model, StatDictionary.Embedded, rows[0]);   // builds the shared-line index here, off the UI thread
        var fsw = Stopwatch.StartNew();
        var families = UnitFamilies.Build(model);                     // links + families from the merged units (about 0.2 s on the real mod)
        var familyTime = fsw.Elapsed;

        string? reason = null;
        EditorCopyMarker? marker = null;
        try { FolderGuard.Check(root, allowGitTree: false); marker = EditorCopyMarker.Load(root); }
        catch (EditorRefusalException e) { reason = e.Message; }
        if (marker == null) marker = EditorCopyMarker.TryLoad(root, out _);

        gameDir ??= GameDirFor(root);
        string title = SteamLocator.ReadModTitle(root) ?? Path.GetFileName(root);
        var s = new ModSession(root, model, rows, families, gameDir, marker, reason, title, warnings, sw.Elapsed, familyTime);
        s.LoadEditing();
        return s;
    }

    /// <summary>The game folder for SHARED: / CORE: images: the remembered one, a folder above the mod (&lt;game&gt;\mods\units\x), or auto-detected.</summary>
    public static string? GameDirFor(string modRoot)
    {
        if (SteamLocator.IsGameFolder(AppHost.Settings.GameFolder)) return AppHost.Settings.GameFolder;
        var units = Directory.GetParent(modRoot);
        var mods = units?.Parent;
        if (units?.Name.Equals("units", StringComparison.OrdinalIgnoreCase) == true && mods?.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) == true
            && SteamLocator.IsGameFolder(mods.Parent?.FullName)) return mods.Parent!.FullName;
        try { return SteamLocator.DetectGameFolders().FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// The thumbnails of the opened copy as small bitmaps, created lazily on the UI thread the first time a row is drawn (decoding a 64 x 64 PNG takes
/// microseconds), then kept. Until the background load finishes, rows draw a grey placeholder.
/// </summary>
sealed class ThumbImages : IDisposable
{
    readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyDictionary<string, Thumbnails.CachedThumb>? _thumbs;

    public bool Ready => _thumbs != null;
    public int Count => _thumbs?.Count ?? 0;
    public int Decoded => _bitmaps.Count;
    public double DecodeMs { get; private set; }

    public void SetThumbs(IReadOnlyDictionary<string, Thumbnails.CachedThumb> thumbs) => _thumbs = thumbs;

    public Thumbnails.CachedThumb? Info(string relPath) => _thumbs != null && _thumbs.TryGetValue(relPath, out var t) ? t : null;

    public Bitmap? Get(string relPath)
    {
        if (_bitmaps.TryGetValue(relPath, out var b)) return b;
        if (_thumbs == null || !_thumbs.TryGetValue(relPath, out var t)) return null;
        long t0 = Stopwatch.GetTimestamp();
        try { b = ToBitmap(Png.Decode(t.Png)); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IOException)
        {
            AppHost.Log.Warn($"thumbnail of {relPath} cannot be decoded: {e.Message}");
            b = ToBitmap(Thumbnails.Silhouette("", Thumbnails.DefaultSize));
        }
        DecodeMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _bitmaps[relPath] = b;
        return b;
    }

    public static Bitmap ToBitmap(RgbaImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[img.Width * 4];
            for (int y = 0; y < img.Height; y++)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int i = img.Index(x, y), o = x * 4;
                    row[o] = img.Pixels[i + 2]; row[o + 1] = img.Pixels[i + 1]; row[o + 2] = img.Pixels[i]; row[o + 3] = img.Pixels[i + 3];
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public void Dispose()
    {
        foreach (var b in _bitmaps.Values) b.Dispose();
        _bitmaps.Clear();
    }
}
