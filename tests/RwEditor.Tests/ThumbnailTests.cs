using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RwEditor.Core.Imaging;
using RwEditor.Core.Model;
using Xunit;

namespace RwEditor.Tests;

/// <summary>PNG codec and unit thumbnails on synthetic sprite sheets (no mod art).</summary>
public class ThumbnailTests
{
    // ------------------------------------------------------------------------------------------------ raw PNG builder for decoder coverage
    static byte[] RawPng(int w, int h, int bitDepth, int colorType, int interlace, Func<int, int, int, byte[]> rowBytes, byte[]? plte = null, byte[]? trns = null)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string t, byte[] b)
        {
            var len = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, b.Length);
            ms.Write(len); ms.Write(Encoding.ASCII.GetBytes(t)); ms.Write(b); ms.Write(new byte[4]);   // the decoder does not check CRCs
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)bitDepth; ihdr[9] = (byte)colorType; ihdr[12] = (byte)interlace;
        Chunk("IHDR", ihdr);
        if (plte != null) Chunk("PLTE", plte);
        if (trns != null) Chunk("tRNS", trns);
        using var raw = new MemoryStream();
        if (interlace == 0) for (int y = 0; y < h; y++) { raw.WriteByte(0); raw.Write(rowBytes(-1, y, w)); }
        else
        {
            int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, sy = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
            for (int p = 0; p < 7; p++)
            {
                int pw = (w - sx[p] + dx[p] - 1) / dx[p], ph = (h - sy[p] + dy[p] - 1) / dy[p];
                if (pw <= 0 || ph <= 0) continue;
                for (int y = 0; y < ph; y++) { raw.WriteByte(0); raw.Write(rowBytes(p, sy[p] + y * dy[p], pw)); }
            }
        }
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw.ToArray());
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void Encode_then_decode_gives_the_same_pixels_and_is_deterministic()
    {
        var img = new RgbaImage(5, 3);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) img.Set(x, y, (byte)(x * 40), (byte)(y * 80), (byte)(x + y), (byte)(x == 0 ? 0 : 255));
        var png = Png.Encode(img);
        Assert.Equal(png, Png.Encode(img));
        var back = Png.Decode(png);
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void Palette_with_transparency_decodes()
    {
        var png = RawPng(4, 1, 8, 3, 0, (_, _, w) => new byte[] { 0, 1, 2, 1 },
            plte: new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }, trns: new byte[] { 0 });
        var img = Png.Decode(png);
        Assert.Equal(0, img.Alpha(0, 0));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, img.Pixels[4..8]);
    }

    [Fact]
    public void Low_bit_gray_and_16_bit_rgb_decode()
    {
        var g = Png.Decode(RawPng(4, 1, 2, 0, 0, (_, _, _) => new byte[] { 0b00_01_10_11 }));
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, new[] { g.Pixels[0], g.Pixels[4], g.Pixels[8], g.Pixels[12] });
        var c = Png.Decode(RawPng(1, 1, 16, 2, 0, (_, _, _) => new byte[] { 0x12, 0x34, 0xAB, 0xCD, 0xFF, 0x00 }));
        Assert.Equal(new byte[] { 0x12, 0xAB, 0xFF, 255 }, c.Pixels);
    }

    [Fact]
    public void Interlaced_rgba_decodes_to_the_same_image()
    {
        int w = 9, h = 7;
        byte Px(int x, int y, int ch) => (byte)((x * 13 + y * 31 + ch * 7) & 0xFF);
        var plain = Png.Decode(RawPng(w, h, 8, 6, 0, (_, y, pw) => Enumerable.Range(0, pw).SelectMany(x => new[] { Px(x, y, 0), Px(x, y, 1), Px(x, y, 2), (byte)255 }).ToArray()));
        int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, dx = { 8, 8, 4, 4, 2, 2, 1 };
        var inter = Png.Decode(RawPng(w, h, 8, 6, 1, (p, y, pw) => Enumerable.Range(0, pw).SelectMany(i => { int x = sx[p] + i * dx[p]; return new[] { Px(x, y, 0), Px(x, y, 1), Px(x, y, 2), (byte)255 }; }).ToArray()));
        Assert.Equal(plain.Pixels, inter.Pixels);
    }

    [Fact]
    public void Not_a_png_is_refused() => Assert.Throws<InvalidDataException>(() => Png.Decode(Encoding.ASCII.GetBytes("GIF89a....")));

    // ------------------------------------------------------------------------------------------------ frames and fitting
    /// <summary>A 4-frame horizontal sheet, 6x4 per frame; frame k is filled with colour k except a 1-pixel transparent border.</summary>
    static RgbaImage Sheet(int frames = 4, int fw = 6, int fh = 4)
    {
        var img = new RgbaImage(frames * fw, fh);
        for (int f = 0; f < frames; f++)
            for (int y = 1; y < fh - 1; y++)
                for (int x = 1; x < fw - 1; x++) img.Set(f * fw + x, y, (byte)(50 * (f + 1)), 0, 0, 255);
        return img;
    }

    [Fact]
    public void Frame0_is_the_left_cell_of_total_frames()
    {
        var f = Thumbnails.Frame0(Sheet(), totalFrames: 4, frameWidth: -1, frameHeight: -1);
        Assert.Equal(6, f.Width); Assert.Equal(4, f.Height);
        Assert.Equal(50, f.Pixels[f.Index(2, 2)]);                            // colour of frame 0
        Assert.Equal(0, f.Alpha(0, 0));
    }

    [Fact]
    public void Frame_width_and_height_override_the_split()
    {
        var f = Thumbnails.Frame0(Sheet(), totalFrames: 1, frameWidth: 12, frameHeight: 2);
        Assert.Equal(12, f.Width); Assert.Equal(2, f.Height);
        var whole = Thumbnails.Frame0(Sheet(), totalFrames: 1, frameWidth: -1, frameHeight: -1);
        Assert.Equal(24, whole.Width);
        var tooMany = Thumbnails.Frame0(new RgbaImage(3, 3), totalFrames: 10, frameWidth: -1, frameHeight: -1);
        Assert.Equal(1, tooMany.Width);                                       // integer division, at least 1 (engine rule)
    }

    [Fact]
    public void Fit_trims_the_transparent_border_and_scales_with_nearest_neighbour()
    {
        var f = Thumbnails.Frame0(Sheet(), 4, -1, -1);                        // visible part 4x2
        var t = Thumbnails.Fit(f, 64)!;
        Assert.Equal(64, t.Width);
        // 4x2 scaled by 16 -> 64x32, centred vertically (rows 16..47)
        Assert.Equal(0, t.Alpha(10, 15));
        Assert.Equal(255, t.Alpha(10, 16));
        Assert.Equal(50, t.Pixels[t.Index(0, 30)]);
        Assert.Equal(0, t.Alpha(10, 48));
        Assert.Null(Thumbnails.Fit(new RgbaImage(5, 5), 64));                 // fully transparent
    }

    [Fact]
    public void Silhouettes_are_generated_per_kind_and_deterministic()
    {
        foreach (var k in new[] { "building", "AIR", "WATER", "HOVER", "LAND", "" })
        {
            var a = Png.Encode(Thumbnails.Silhouette(k, 64));
            Assert.Equal(a, Png.Encode(Thumbnails.Silhouette(k, 64)));
            Assert.Contains(Thumbnails.Silhouette(k, 64).Pixels.Where((_, i) => i % 4 == 3), b => b == 255);
        }
        Assert.NotEqual(Png.Encode(Thumbnails.Silhouette("AIR", 64)), Png.Encode(Thumbnails.Silhouette("building", 64)));
    }

    // ------------------------------------------------------------------------------------------------ whole build on a synthetic mod
    [Fact]
    public void Build_uses_image_then_minimap_icon_then_silhouette_and_resolves_paths_like_the_game()
    {
        var sheet = Png.Encode(Sheet());
        var blank = Png.Encode(new RgbaImage(4, 4));
        using var m = new TestMod(extraFiles: new Dictionary<string, string>
        {
            ["templates/tpl.ini"] = "[core]\ndont_load: true\nname: tpl\n[graphics]\nimage: body.png\ntotal_frames: 4\n",
            ["units/t1/real.ini"] = "[core]\nname: realImg\ncopyFrom: ROOT:/templates/tpl.ini\n",                     // inherited RELATIVE image: unit folder
            ["units/t2/root.ini"] = "[core]\nname: rootImg\n[graphics]\nimage: ROOT:/art/sheet.png\ntotal_frames: 4\n",
            ["units/t3/icon.ini"] = "[core]\nname: iconOnly\n[graphics]\nimage: blank.png\nicon_zoomed_out: ROOT:/art/sheet.png\n",
            ["units/t4/none.ini"] = "[core]\nname: noneImg\nisBuilding: true\n[graphics]\nimage: NONE\n",
            ["units/t5/expr.ini"] = "[core]\nname: exprImg\n[graphics]\nimage: body${level}.png\n[movement]\nmovementType: AIR\n",
            ["units/t6/shared.ini"] = "[core]\nname: sharedImg\n[graphics]\nimage: SHARED:turret.png\n",
            ["units/t7/missing.ini"] = "[core]\nname: missingImg\n[graphics]\nimage: nothere.png\n",
        });
        File.WriteAllBytes(Path.Combine(m.Root, "units", "t1", "body.png"), sheet);
        Directory.CreateDirectory(Path.Combine(m.Root, "art"));
        File.WriteAllBytes(Path.Combine(m.Root, "art", "sheet.png"), sheet);
        File.WriteAllBytes(Path.Combine(m.Root, "units", "t3", "blank.png"), blank);
        var game = Path.Combine(Path.GetDirectoryName(m.Root)!, "game");
        Directory.CreateDirectory(Path.Combine(game, "assets", "units", "shared"));
        File.WriteAllBytes(Path.Combine(game, "assets", "units", "shared", "turret.png"), sheet);

        var model = UnitModel.Load(m.Root);
        var rep = Thumbnails.Build(model, game, 64);
        ThumbSource Src(string n) => rep.Thumbs.Single(t => t.Unit.Name == n).Source;
        Assert.Equal(ThumbSource.Image, Src("realImg"));
        Assert.Equal(ThumbSource.Image, Src("rootImg"));
        Assert.Equal(ThumbSource.Image, Src("sharedImg"));
        Assert.Equal(ThumbSource.MinimapIcon, Src("iconOnly"));
        Assert.Equal(ThumbSource.Silhouette, Src("noneImg"));
        Assert.Equal(ThumbSource.Silhouette, Src("exprImg"));
        Assert.Equal(ThumbSource.Silhouette, Src("missingImg"));
        Assert.Contains("file not found", rep.Thumbs.Single(t => t.Unit.Name == "missingImg").Note);
        // the same frame from two paths gives the same thumbnail
        Assert.Equal(rep.Thumbs.Single(t => t.Unit.Name == "realImg").Png, rep.Thumbs.Single(t => t.Unit.Name == "rootImg").Png);
        // without the game folder, SHARED: falls back (never guessed)
        Assert.Equal(ThumbSource.Silhouette, Thumbnails.Build(model, null, 64).Thumbs.Single(t => t.Unit.Name == "sharedImg").Source);

        // packing is deterministic and the signature follows the inputs
        string sig = Thumbnails.Signature(model, game, 64);
        var z1 = Thumbnails.Pack(rep, sig);
        var model2 = UnitModel.Load(m.Root);
        var z2 = Thumbnails.Pack(Thumbnails.Build(model2, game, 64), Thumbnails.Signature(model2, game, 64));
        Assert.Equal(z1, z2);
        string cacheRoot = Path.Combine(m.BackupRoot, "thumbs");
        string cache = Thumbnails.WriteOnce(Thumbnails.CachePath(m.Root, cacheRoot), z1);
        Assert.Equal(sig, Thumbnails.ReadSignature(cache));
        // the cache reads back exactly what was packed
        var read = Thumbnails.ReadZip(cache)!.Value;
        Assert.Equal(sig, read.Signature);
        Assert.Equal(rep.Thumbs.Count, read.Thumbs.Count);
        foreach (var t in rep.Thumbs) { Assert.Equal(t.Png, read.Thumbs[t.Unit.RelPath].Png); Assert.Equal(t.Source, read.Thumbs[t.Unit.RelPath].Source); }
        // LoadOrBuild: a matching cache is used as it is
        var hit = Thumbnails.LoadOrBuild(model, game, 64, cacheRoot);
        Assert.True(hit.FromCache);
        Assert.Null(hit.CacheProblem);
        // a changed image file changes the signature (no decoding needed to notice), and the next load rebuilds and rewrites the cache
        File.WriteAllBytes(Path.Combine(m.Root, "art", "sheet.png"), Png.Encode(Sheet(frames: 2)));
        Assert.NotEqual(sig, Thumbnails.Signature(model, game, 64));
        var miss = Thumbnails.LoadOrBuild(model, game, 64, cacheRoot);
        Assert.False(miss.FromCache);
        Assert.Equal(Thumbnails.Signature(model, game, 64), Thumbnails.ReadSignature(cache));
        Assert.True(Thumbnails.LoadOrBuild(model, game, 64, cacheRoot).FromCache);
        // a changed template text changes the signature too (an image key can come from any template)
        var changed = model.WithFiles(new Dictionary<string, string> { ["templates/tpl.ini"] = "[core]\ndont_load: true\nname: tpl\n[graphics]\nimage: body.png\ntotal_frames: 2\n" });
        Assert.NotEqual(Thumbnails.Signature(model, game, 64), Thumbnails.Signature(changed, game, 64));
        // a damaged cache file is ignored (rebuilt), never fatal
        File.WriteAllBytes(cache, new byte[] { 1, 2, 3 });
        Assert.Null(Thumbnails.ReadZip(cache));
        Assert.False(Thumbnails.LoadOrBuild(model, game, 64, cacheRoot).FromCache);
    }
}
