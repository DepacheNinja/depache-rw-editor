using System.Buffers.Binary;
using System.IO.Compression;

namespace RwEditor.Core.Imaging;

/// <summary>An RGBA image, 8 bits per channel, row-major, no padding.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(width), $"image size {width}x{height}");
        Width = width; Height = height; Pixels = new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public int Index(int x, int y) => (y * Width + x) * 4;
    public byte Alpha(int x, int y) => Pixels[Index(x, y) + 3];

    public void Set(int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = Index(x, y);
        Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = a;
    }
}

/// <summary>
/// Minimal PNG reader / writer in managed code (no System.Drawing), so RwEditor.Core stays UI-free and runs on any .NET platform (CLI, tests, the later
/// WinForms app). Reads every standard PNG form: colour types 0, 2, 3, 4, 6; bit depths 1, 2, 4, 8, 16; tRNS transparency; Adam7 interlacing. Writes
/// RGBA 8-bit, filter 0, one IDAT, no time or text chunks, so the same pixels always give the same bytes.
/// </summary>
public static class Png
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static RgbaImage Decode(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("not a PNG file");
        int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        bool sawHeader = false;
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            if (len < 0 || pos + 12 + len > data.Length) throw new InvalidDataException("truncated PNG chunk");
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body); height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    bitDepth = body[8]; colorType = body[9]; interlace = body[12];
                    if (body[10] != 0 || body[11] != 0) throw new InvalidDataException("unknown PNG compression or filter method");
                    sawHeader = true;
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": trns = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            pos += 12 + len;
            if (type == "IEND") break;
        }
        if (!sawHeader) throw new InvalidDataException("PNG without IHDR");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"unknown PNG colour type {colorType}") };
        if (colorType == 3 && palette == null) throw new InvalidDataException("palette PNG without PLTE");
        if (bitDepth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException($"unsupported PNG bit depth {bitDepth}");

        byte[] raw;
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            z.CopyTo(outMs);
            raw = outMs.ToArray();
        }

        var img = new RgbaImage(width, height);
        int bitsPerPixel = channels * bitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int rp = 0;
        if (interlace == 0)
        {
            DecodePass(raw, ref rp, img, width, height, 0, 0, 1, 1, bitsPerPixel, bpp, bitDepth, colorType, palette, trns);
        }
        else
        {
            int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, sy = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
            for (int p = 0; p < 7; p++)
            {
                int pw = (width - sx[p] + dx[p] - 1) / dx[p], ph = (height - sy[p] + dy[p] - 1) / dy[p];
                if (pw <= 0 || ph <= 0) continue;
                DecodePass(raw, ref rp, img, pw, ph, sx[p], sy[p], dx[p], dy[p], bitsPerPixel, bpp, bitDepth, colorType, palette, trns);
            }
        }
        return img;
    }

    static void DecodePass(byte[] raw, ref int rp, RgbaImage img, int w, int h, int x0, int y0, int dx, int dy,
        int bitsPerPixel, int bpp, int bitDepth, int colorType, byte[]? palette, byte[]? trns)
    {
        int stride = (w * bitsPerPixel + 7) / 8;
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            if (rp + 1 + stride > raw.Length) throw new InvalidDataException("truncated PNG image data");
            int filter = raw[rp++];
            Array.Copy(raw, rp, cur, 0, stride);
            rp += stride;
            Unfilter(filter, cur, prev, bpp);
            for (int x = 0; x < w; x++) WritePixel(img, x0 + x * dx, y0 + y * dy, cur, x, bitDepth, colorType, palette, trns);
            (prev, cur) = (cur, prev);
        }
    }

    static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            int v = filter switch
            {
                0 => cur[i],
                1 => cur[i] + a,
                2 => cur[i] + b,
                3 => cur[i] + ((a + b) >> 1),
                4 => cur[i] + Paeth(a, b, c),
                _ => throw new InvalidDataException($"unknown PNG filter {filter}"),
            };
            cur[i] = (byte)v;
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static int Sample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8: return row[index];
            case 16: return row[index * 2];   // high byte
            default:
                int bit = index * bitDepth;
                int shift = 8 - bitDepth - (bit % 8);
                return (row[bit / 8] >> shift) & ((1 << bitDepth) - 1);
        }
    }

    static int Raw16(byte[] row, int index) => (row[index * 2] << 8) | row[index * 2 + 1];

    static void WritePixel(RgbaImage img, int x, int y, byte[] row, int px, int bitDepth, int colorType, byte[]? palette, byte[]? trns)
    {
        int scale(int v) => bitDepth >= 8 ? v : v * 255 / ((1 << bitDepth) - 1);
        switch (colorType)
        {
            case 0:
            {
                int g = Sample(row, px, bitDepth);
                bool transparent = trns != null && trns.Length >= 2 && (bitDepth == 16 ? Raw16(row, px) : g) == ((trns[0] << 8) | trns[1]);
                byte v = (byte)scale(g);
                img.Set(x, y, v, v, v, transparent ? (byte)0 : (byte)255);
                break;
            }
            case 2:
            {
                int r = Sample(row, px * 3, bitDepth), g = Sample(row, px * 3 + 1, bitDepth), b = Sample(row, px * 3 + 2, bitDepth);
                bool transparent = false;
                if (trns != null && trns.Length >= 6)
                {
                    int tr = (trns[0] << 8) | trns[1], tg = (trns[2] << 8) | trns[3], tb = (trns[4] << 8) | trns[5];
                    transparent = bitDepth == 16
                        ? Raw16(row, px * 3) == tr && Raw16(row, px * 3 + 1) == tg && Raw16(row, px * 3 + 2) == tb
                        : r == tr && g == tg && b == tb;
                }
                img.Set(x, y, (byte)r, (byte)g, (byte)b, transparent ? (byte)0 : (byte)255);
                break;
            }
            case 3:
            {
                int i = Sample(row, px, bitDepth);
                if (i * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index out of range");
                byte a = trns != null && i < trns.Length ? trns[i] : (byte)255;
                img.Set(x, y, palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2], a);
                break;
            }
            case 4:
            {
                byte g = (byte)Sample(row, px * 2, bitDepth), a = (byte)Sample(row, px * 2 + 1, bitDepth);
                img.Set(x, y, g, g, g, a);
                break;
            }
            default:
                img.Set(x, y, (byte)Sample(row, px * 4, bitDepth), (byte)Sample(row, px * 4 + 1, bitDepth), (byte)Sample(row, px * 4 + 2, bitDepth), (byte)Sample(row, px * 4 + 3, bitDepth));
                break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ encode
    public static byte[] Encode(RgbaImage img)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);
        int stride = img.Width * 4;
        var raw = new byte[(stride + 1) * img.Height];
        for (int y = 0; y < img.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(img.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        }
        using (var zms = new MemoryStream())
        {
            using (var z = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            Chunk(ms, "IDAT", zms.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        var tb = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(tb);
        s.Write(body);
        uint crc = Crc32(tb, 0xFFFFFFFFu);
        crc = Crc32(body, crc) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    static readonly uint[] CrcTable = BuildCrc();

    static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
