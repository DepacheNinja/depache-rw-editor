using System.Buffers.Binary;

namespace RwEditor.Core.Imaging;

/// <summary>
/// Builds a Windows .ico file from one RGBA image (the application icon = the mod thumbnail, DESIGN_SPEC 6b). Every size is made by area-averaging the
/// source (alpha-weighted), so large downscales stay smooth. By default EVERY entry, 256 px included, is a classic 32-bit DIB (BITMAPINFOHEADER, BGRA
/// bottom-up, height x2, all-zero AND mask). Finding (E2, 2026-10-12, SDK 10.0.302): with the 256 px entry PNG-compressed, the C# compiler's
/// ApplicationIcon step produced an assembly that fails to load ("Bad IL format"); an all-DIB icon of the same pixels works, so PNG entries are opt-in
/// (<c>pngFromSize</c>) only. The output is deterministic (same pixels, same bytes). Decoding the JPEG source is the caller's job (Core has no JPEG
/// codec); the app does it with System.Drawing.
/// </summary>
public static class IconBuilder
{
    public static readonly IReadOnlyList<int> StandardSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

    /// <summary>A centred square crop (the shorter side) of the image.</summary>
    public static RgbaImage CropSquare(RgbaImage src)
    {
        int s = Math.Min(src.Width, src.Height), ox = (src.Width - s) / 2, oy = (src.Height - s) / 2;
        if (ox == 0 && oy == 0 && src.Width == src.Height) return src;
        var dst = new RgbaImage(s, s);
        for (int y = 0; y < s; y++) Array.Copy(src.Pixels, src.Index(ox, oy + y), dst.Pixels, dst.Index(0, y), s * 4);
        return dst;
    }

    /// <summary>Area-average resize to size x size (alpha-weighted colour, so transparent pixels do not darken the edges). Upscaling repeats pixels.</summary>
    public static RgbaImage Resize(RgbaImage src, int size)
    {
        if (size <= 0 || size > 4096) throw new ArgumentOutOfRangeException(nameof(size));
        var dst = new RgbaImage(size, size);
        double sx = (double)src.Width / size, sy = (double)src.Height / size;
        for (int y = 0; y < size; y++)
        {
            double y0 = y * sy, y1 = (y + 1) * sy;
            for (int x = 0; x < size; x++)
            {
                double x0 = x * sx, x1 = (x + 1) * sx;
                double r = 0, g = 0, b = 0, a = 0, w = 0;
                for (int py = (int)Math.Floor(y0); py < Math.Min(src.Height, (int)Math.Ceiling(y1)); py++)
                {
                    double wy = Math.Min(y1, py + 1) - Math.Max(y0, py);
                    if (wy <= 0) continue;
                    for (int px = (int)Math.Floor(x0); px < Math.Min(src.Width, (int)Math.Ceiling(x1)); px++)
                    {
                        double wx = Math.Min(x1, px + 1) - Math.Max(x0, px);
                        if (wx <= 0) continue;
                        double ww = wx * wy;
                        int i = src.Index(px, py);
                        double al = src.Pixels[i + 3] / 255.0;
                        r += src.Pixels[i] * al * ww; g += src.Pixels[i + 1] * al * ww; b += src.Pixels[i + 2] * al * ww;
                        a += al * ww; w += ww;
                    }
                }
                if (w <= 0) continue;
                byte A = (byte)Math.Clamp(Math.Round(a / w * 255), 0, 255);
                if (a > 0)
                    dst.Set(x, y, (byte)Math.Clamp(Math.Round(r / a), 0, 255), (byte)Math.Clamp(Math.Round(g / a), 0, 255), (byte)Math.Clamp(Math.Round(b / a), 0, 255), A);
            }
        }
        return dst;
    }

    /// <summary>The .ico bytes with one entry per size (square crop of the source, then resized).</summary>
    /// <param name="pngFromSize">Entries of this size and larger are PNG-compressed; the default (above 256) makes every entry a DIB (see the class summary).</param>
    public static byte[] Build(RgbaImage source, IReadOnlyList<int>? sizes = null, int pngFromSize = int.MaxValue)
    {
        sizes ??= StandardSizes;
        if (sizes.Count == 0 || sizes.Any(s => s < 1 || s > 256) || sizes.Distinct().Count() != sizes.Count) throw new ArgumentException("sizes must be distinct, 1..256", nameof(sizes));
        var sq = CropSquare(source);
        var images = sizes.Select(s => (Size: s, Data: s >= pngFromSize ? Png.Encode(Resize(sq, s)) : Dib(Resize(sq, s)))).ToList();
        int headerLen = 6 + 16 * images.Count;
        var ico = new byte[headerLen + images.Sum(i => i.Data.Length)];
        var span = ico.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[0..], 0);      // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);      // type 1 = icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Count);
        int offset = headerLen;
        for (int k = 0; k < images.Count; k++)
        {
            var e = span[(6 + 16 * k)..];
            e[0] = (byte)(images[k].Size >= 256 ? 0 : images[k].Size);  // width (0 = 256)
            e[1] = (byte)(images[k].Size >= 256 ? 0 : images[k].Size);  // height
            e[2] = 0; e[3] = 0;                                          // palette, reserved
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);         // planes
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], 32);        // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)images[k].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            images[k].Data.CopyTo(ico, offset);
            offset += images[k].Data.Length;
        }
        return ico;
    }

    /// <summary>A 32-bit icon DIB: BITMAPINFOHEADER (height doubled), BGRA rows bottom-up, then a 1-bit AND mask (all 0: the alpha channel decides).</summary>
    static byte[] Dib(RgbaImage img)
    {
        int w = img.Width, h = img.Height, maskStride = ((w + 31) / 32) * 4;
        var d = new byte[40 + w * h * 4 + maskStride * h];
        var s = d.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], h * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 0);              // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], w * h * 4);
        int p = 40;
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                int i = img.Index(x, y);
                d[p++] = img.Pixels[i + 2]; d[p++] = img.Pixels[i + 1]; d[p++] = img.Pixels[i]; d[p++] = img.Pixels[i + 3];
            }
        return d;   // the AND mask stays zero
    }

    /// <summary>Reads the directory of an .ico (for checks): (width, height, bits, size in bytes, is PNG) per entry.</summary>
    public static IReadOnlyList<(int Width, int Height, int Bits, int Length, bool IsPng)> ReadDirectory(byte[] ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
            throw new InvalidDataException("not an .ico file");
        int n = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var list = new List<(int, int, int, int, bool)>();
        for (int k = 0; k < n; k++)
        {
            int e = 6 + 16 * k;
            if (e + 16 > ico.Length) throw new InvalidDataException("truncated .ico directory");
            int w = ico[e] == 0 ? 256 : ico[e], h = ico[e + 1] == 0 ? 256 : ico[e + 1];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(e + 6));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(e + 8));
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(e + 12));
            if (off < 0 || len < 0 || off + len > ico.Length) throw new InvalidDataException("an .ico entry points outside the file");
            list.Add((w, h, bits, len, Png.IsPng(ico.AsSpan(off, len))));
        }
        return list;
    }
}
