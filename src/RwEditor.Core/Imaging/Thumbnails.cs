using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;

namespace RwEditor.Core.Imaging;

public enum ThumbSource
{
    /// <summary>Frame 0 of [graphics] image.</summary>
    Image,
    /// <summary>[graphics] icon_zoomed_out (the minimap icon), used whole.</summary>
    MinimapIcon,
    /// <summary>A generated silhouette for the movement type, with a "?" badge.</summary>
    Silhouette,
}

/// <summary>One unit's thumbnail: the PNG bytes (Size x Size, RGBA) and where it came from; <see cref="Note"/> says why a fallback was used.</summary>
public sealed record Thumb(UnitType Unit, ThumbSource Source, string? ImagePath, byte[] Png, string? Note);

public sealed record ThumbReport(IReadOnlyList<Thumb> Thumbs, int Size)
{
    public int Count(ThumbSource s) => Thumbs.Count(t => t.Source == s);
}

/// <summary>
/// Unit thumbnails (DESIGN_EDITOR 4), built in RAM. Rules (javap, units/custom/ag offsets 1609-2341 and 13622+):
/// - the image is [graphics] image of the merged unit; "NONE" or empty = no image;
/// - paths: "ROOT:x" = mod root; "SHARED:x" = &lt;game&gt;/assets/units/shared/x; "CORE:x" = &lt;game&gt;/assets/units/x; anything else is relative to the
///   UNIT's own file folder (l.F = the unit's path, also for an image key inherited from a template; [core] overrideResourceLoadPath, unused in this mod,
///   would change that base and is not supported: fallback);
/// - frames run horizontally: frame width = image width / total_frames (integer division, at least 1), or frame_width when it is above 0; frame height =
///   the image height, or frame_height when above 0 (and below the image height); frame 0 is the top-left cell;
/// - fallbacks: frame 0 of the image, then icon_zoomed_out (whole image), then a generated silhouette by movement type with a "?" badge.
/// The frame is trimmed of its transparent border and drawn centred into a Size x Size cell with nearest-neighbour scaling. Nothing of the mod's art is
/// stored anywhere except the user's own cache / output (never shipped with the editor).
/// </summary>
public static class Thumbnails
{
    public const int DefaultSize = 64;

    public static ThumbReport Build(UnitModel model, string? gameDir, int size = DefaultSize)
    {
        var decoded = new Dictionary<string, RgbaImage?>(StringComparer.OrdinalIgnoreCase);   // each image file read and decoded once
        var list = new List<Thumb>(model.Units.Count);
        foreach (var u in model.Units)
        {
            var m = model.Merge(u);
            string? note = null;
            var img = m.Get("graphics", "image")?.Value.Trim();
            var path = ResolvePath(model.Snapshot.Root, gameDir, u.RelPath, img, m, out var why);
            var src = path == null ? null : Load(decoded, path, out why);
            if (src != null)
            {
                int tf = Int(m, "total_frames") ?? 1, fw = Int(m, "frame_width") ?? -1, fh = Int(m, "frame_height") ?? -1;
                var frame = Frame0(src, tf, fw, fh);
                var fitted = Fit(frame, size);
                if (fitted != null) { list.Add(new Thumb(u, ThumbSource.Image, path, Png.Encode(fitted), null)); continue; }
                why = "frame 0 is fully transparent (" + Path.GetFileName(path) + ")";
            }
            note = "image: " + why;
            var icon = m.Get("graphics", "icon_zoomed_out")?.Value.Trim();
            var ipath = ResolvePath(model.Snapshot.Root, gameDir, u.RelPath, icon, m, out var iwhy);
            var isrc = ipath == null ? null : Load(decoded, ipath, out iwhy);
            if (isrc != null)
            {
                if (Fit(isrc, size) is RgbaImage ifit) { list.Add(new Thumb(u, ThumbSource.MinimapIcon, ipath, Png.Encode(ifit), note)); continue; }
                iwhy = "fully transparent (" + Path.GetFileName(ipath) + ")";
            }
            note += "; minimap icon: " + iwhy;
            string kind = model.IsBuilding(u) ? "building" : (m.Get("movement", "movementType")?.Value.Trim().ToUpperInvariant() ?? "");
            list.Add(new Thumb(u, ThumbSource.Silhouette, null, Png.Encode(Silhouette(kind, size)), note));
        }
        return new ThumbReport(list, size);
    }

    static int? Int(MergedUnit m, string key) => m.Get("graphics", key) is MergedValue v && int.TryParse(v.Value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>Full path of an image value, or null with the reason.</summary>
    public static string? ResolvePath(string modRoot, string? gameDir, string unitRel, string? value, MergedUnit m, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(value) || value.Equals("NONE", StringComparison.OrdinalIgnoreCase)) { why = "not set"; return null; }
        if (value.Contains("${", StringComparison.Ordinal)) { why = "an expression (" + value + ")"; return null; }
        if (m.Get("core", "overrideResourceLoadPath") != null) { why = "overrideResourceLoadPath is not supported by the editor"; return null; }
        string v = value.Trim().Trim('"');
        string full;
        if (v.StartsWith("ROOT:", StringComparison.OrdinalIgnoreCase)) full = Path.Combine(modRoot, v.Substring(5).TrimStart('/', '\\'));
        else if (v.StartsWith("SHARED:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("CORE:", StringComparison.OrdinalIgnoreCase))
        {
            if (gameDir == null) { why = "a game asset (" + v + "); the game folder is not known (pass --game)"; return null; }
            bool shared = v.StartsWith("SHARED:", StringComparison.OrdinalIgnoreCase);
            string rest = v.Substring(shared ? 7 : 5).TrimStart('/', '\\');
            full = shared ? Path.Combine(gameDir, "assets", "units", "shared", rest) : Path.Combine(gameDir, "assets", "units", rest);
        }
        else full = Path.Combine(modRoot, Path.GetDirectoryName(unitRel.Replace('/', Path.DirectorySeparatorChar)) ?? "", v);
        full = Path.GetFullPath(full.Replace('/', Path.DirectorySeparatorChar));
        if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { why = "not a .png (" + v + ")"; return null; }
        return full;
    }

    static RgbaImage? Load(Dictionary<string, RgbaImage?> cache, string path, out string why)
    {
        why = "";
        if (cache.TryGetValue(path, out var c)) { if (c == null) why = "cannot be read (" + path + ")"; return c; }
        RgbaImage? img = null;
        if (!File.Exists(path)) why = "file not found (" + path + ")";
        else
        {
            try { img = Png.Decode(File.ReadAllBytes(path)); }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentOutOfRangeException) { why = $"cannot be decoded ({e.Message})"; }
        }
        cache[path] = img;
        if (img == null && why.Length == 0) why = "cannot be read (" + path + ")";
        return img;
    }

    /// <summary>The top-left frame of a horizontal sprite sheet (engine rule, see class summary).</summary>
    public static RgbaImage Frame0(RgbaImage src, int totalFrames, int frameWidth, int frameHeight)
    {
        int fw = Math.Max(1, src.Width / Math.Max(1, totalFrames));
        if (frameWidth > 0) fw = frameWidth;
        int fh = src.Height;
        if (frameHeight > 0 && frameHeight < src.Height) fh = frameHeight;
        fw = Math.Min(fw, src.Width);
        var f = new RgbaImage(fw, fh);
        for (int y = 0; y < fh; y++) Array.Copy(src.Pixels, src.Index(0, y), f.Pixels, f.Index(0, y), fw * 4);
        return f;
    }

    /// <summary>Trims the transparent border and scales (nearest neighbour, aspect kept) into a centred size x size cell; null when nothing is visible.</summary>
    public static RgbaImage? Fit(RgbaImage src, int size)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (src.Alpha(x, y) != 0) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        if (maxX < 0) return null;
        int w = maxX - minX + 1, h = maxY - minY + 1;
        double scale = Math.Min((double)size / w, (double)size / h);
        if (scale >= 1) scale = Math.Floor(scale);          // small sprites: whole-number enlargement keeps pixels crisp
        int dw = Math.Max(1, (int)Math.Round(w * scale)), dh = Math.Max(1, (int)Math.Round(h * scale));
        int ox = (size - dw) / 2, oy = (size - dh) / 2;
        var dst = new RgbaImage(size, size);
        for (int y = 0; y < dh; y++)
        {
            int sy = minY + Math.Min(h - 1, (int)(y / scale));
            for (int x = 0; x < dw; x++)
            {
                int sx = minX + Math.Min(w - 1, (int)(x / scale));
                Array.Copy(src.Pixels, src.Index(sx, sy), dst.Pixels, dst.Index(ox + x, oy + y), 4);
            }
        }
        return dst;
    }

    /// <summary>A plain grey shape per movement type (building = square, AIR = triangle, WATER / OVER_CLIFF_WATER = boat, HOVER = rounded, else land =
    /// circle), with a small "?" badge. Generated, so no art is involved.</summary>
    public static RgbaImage Silhouette(string kind, int size)
    {
        var img = new RgbaImage(size, size);
        double c = (size - 1) / 2.0, r = size * 0.36;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - c, dy = y - c;
                bool inside = kind switch
                {
                    "building" => Math.Abs(dx) <= r && Math.Abs(dy) <= r,
                    "AIR" => dy <= r && dy >= -r && Math.Abs(dx) <= (dy + r) / 2,
                    "WATER" or "OVER_CLIFF_WATER" => Math.Abs(dy) <= r * 0.45 && Math.Abs(dx) <= r * (1 - Math.Max(0, dx / r) * 0.5),
                    "HOVER" => Math.Pow(Math.Abs(dx) / r, 4) + Math.Pow(Math.Abs(dy) / (r * 0.75), 4) <= 1,
                    _ => dx * dx + dy * dy <= r * r,
                };
                if (inside) img.Set(x, y, 128, 128, 128, 255);
            }
        // "?" badge, 3x5 glyph scaled, bottom-right
        string[] q = { "111", "001", "011", "000", "010" };
        int s = Math.Max(1, size / 16), bx = size - 4 * s - 1, by = size - 6 * s - 1;
        for (int gy = 0; gy < 5; gy++)
            for (int gx = 0; gx < 3; gx++)
                if (q[gy][gx] == '1')
                    for (int yy = 0; yy < s; yy++)
                        for (int xx = 0; xx < s; xx++) img.Set(bx + gx * s + xx, by + gy * s + yy, 255, 200, 0, 255);
        return img;
    }

    // ------------------------------------------------------------------------------------------------------------------ packing / cache
    /// <summary>
    /// One zip with every thumbnail ("&lt;unit file path&gt;.png") and an index.json, built in RAM with fixed entry times, so the same thumbnails give
    /// byte-identical zips. This is the only form the editor writes thumbnails in (RAM-buffer rule: one write).
    /// </summary>
    public static byte[] Pack(ThumbReport report, string signature)
    {
        var index = new JsonArray();
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var t in report.Thumbs.OrderBy(t => t.Unit.RelPath, StringComparer.Ordinal))
            {
                string entry = "thumbs/" + t.Unit.RelPath + ".png";
                var e = za.CreateEntry(entry, CompressionLevel.NoCompression);
                e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using (var s = e.Open()) s.Write(t.Png);
                index.Add(new JsonObject
                {
                    ["unit"] = t.Unit.Name, ["file"] = t.Unit.RelPath, ["area"] = t.Unit.Area, ["entry"] = entry,
                    ["source"] = t.Source.ToString(), ["note"] = t.Note,
                });
            }
            var root = new JsonObject
            {
                ["format"] = 1, ["size"] = report.Size, ["signature"] = signature, ["count"] = report.Thumbs.Count,
                ["image"] = report.Count(ThumbSource.Image), ["minimapIcon"] = report.Count(ThumbSource.MinimapIcon), ["silhouette"] = report.Count(ThumbSource.Silhouette),
                ["units"] = index,
            };
            var ie = za.CreateEntry("index.json", CompressionLevel.Optimal);
            ie.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var s2 = ie.Open();
            s2.Write(Encoding.UTF8.GetBytes(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n"));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// What the thumbnails depend on, computed WITHOUT decoding any image (E2: so a cache can be trusted before building anything): the text hash of every
    /// ini / template of the copy (an image key can come from any template), the game folder, and for every unit the resolved image and minimap-icon paths
    /// with each file's size and write time ("missing" when absent). A cache whose signature differs is rebuilt.
    /// </summary>
    public static string Signature(UnitModel model, string? gameDir, int size)
    {
        var sb = new StringBuilder().Append("thumbs-format=2\nsize=").Append(size).Append("\ngame=").Append(gameDir ?? "").Append('\n');
        foreach (var rel in model.Snapshot.Files)
            sb.Append(rel).Append('=').Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model.Snapshot.GetText(rel) ?? "")))).Append('\n');
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in model.Units)
        {
            var m = model.Merge(u);
            foreach (var key in new[] { "image", "icon_zoomed_out" })
                if (ResolvePath(model.Snapshot.Root, gameDir, u.RelPath, m.Get("graphics", key)?.Value.Trim(), m, out _) is string p) paths.Add(p);
        }
        foreach (var p in paths)
        {
            var fi = new FileInfo(p);
            sb.Append(p).Append('|').Append(fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "missing").Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>One cached thumbnail: PNG bytes plus where it came from.</summary>
    public sealed record CachedThumb(string RelPath, string Unit, byte[] Png, ThumbSource Source, string? Note);

    /// <summary>Reads a thumbnail zip (cache or --out) into RAM: unit file path -> thumbnail. Null when the file is missing or unreadable.</summary>
    public static (string? Signature, int Size, Dictionary<string, CachedThumb> Thumbs)? ReadZip(string zipPath)
    {
        if (!File.Exists(zipPath)) return null;
        try
        {
            byte[] all = File.ReadAllBytes(zipPath);   // one read, then everything from RAM
            using var za = new ZipArchive(new MemoryStream(all), ZipArchiveMode.Read);
            var ie = za.GetEntry("index.json");
            if (ie == null) return null;
            JsonNode? root;
            using (var s = ie.Open()) root = JsonNode.Parse(s);
            if (root == null || (int?)root["format"] != 1) return null;
            var dict = new Dictionary<string, CachedThumb>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in root["units"]?.AsArray() ?? new JsonArray())
            {
                if (n == null) continue;
                string file = (string?)n["file"] ?? "", entry = (string?)n["entry"] ?? "";
                var e = za.GetEntry(entry);
                if (e == null || file.Length == 0) return null;
                using var es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var src = Enum.TryParse<ThumbSource>((string?)n["source"], out var ts) ? ts : ThumbSource.Silhouette;
                dict[file] = new CachedThumb(file, (string?)n["unit"] ?? "", ms.ToArray(), src, (string?)n["note"]);
            }
            return ((string?)root["signature"], (int?)root["size"] ?? 0, dict);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Result of <see cref="LoadOrBuild"/>: the thumbnails by unit file path, whether they came from the cache, and timings.</summary>
    public sealed record ThumbSet(IReadOnlyDictionary<string, CachedThumb> Thumbs, bool FromCache, string CachePath, string? CacheProblem,
        TimeSpan SignatureTime, TimeSpan LoadOrBuildTime);

    /// <summary>
    /// The thumbnails of a copy: from the cache file when its signature matches, else built in RAM and written to the cache ONCE (a cache that cannot be
    /// written is reported, never fatal).
    /// </summary>
    public static ThumbSet LoadOrBuild(UnitModel model, string? gameDir, int size = DefaultSize, string? cacheRoot = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string sig = Signature(model, gameDir, size);
        var sigTime = sw.Elapsed;
        string cache = CachePath(model.Snapshot.Root, cacheRoot);
        var cached = ReadZip(cache);
        if (cached is { } c && c.Signature == sig && c.Size == size && model.Units.All(u => c.Thumbs.ContainsKey(u.RelPath)))
            return new ThumbSet(c.Thumbs, true, cache, null, sigTime, sw.Elapsed - sigTime);
        var report = Build(model, gameDir, size);
        var zip = Pack(report, sig);
        string? problem = null;
        try { WriteOnce(cache, zip); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { problem = "the thumbnail cache could not be saved: " + e.Message; }
        var dict = report.Thumbs.ToDictionary(t => t.Unit.RelPath, t => new CachedThumb(t.Unit.RelPath, t.Unit.Name, t.Png, t.Source, t.Note), StringComparer.OrdinalIgnoreCase);
        return new ThumbSet(dict, false, cache, problem, sigTime, sw.Elapsed - sigTime);
    }

    public static string CacheRoot => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "thumbs");

    /// <summary>Writes a packed zip once (temp + move/replace) and returns its path.</summary>
    public static string WriteOnce(string path, byte[] zip)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        File.WriteAllBytes(tmp, zip);
        if (File.Exists(full)) File.Replace(tmp, full, null); else File.Move(tmp, full);
        return full;
    }

    /// <summary>The cache file of a mod copy: %LOCALAPPDATA%\DepacheRwEditor\thumbs\&lt;copyId&gt;.zip (copyId as for backups).</summary>
    public static string CachePath(string modRoot, string? cacheRoot = null) => Path.Combine(cacheRoot ?? CacheRoot, Apply.BackupStore.CopyId(modRoot) + ".zip");

    /// <summary>The signature stored in a cache zip, or null.</summary>
    public static string? ReadSignature(string zipPath)
    {
        if (!File.Exists(zipPath)) return null;
        try
        {
            using var za = ZipFile.OpenRead(zipPath);
            var e = za.GetEntry("index.json");
            if (e == null) return null;
            using var s = e.Open();
            return (string?)JsonNode.Parse(s)?["signature"];
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException) { return null; }
    }
}
