using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace RwEditor.App;

/// <summary>
/// Build-time helpers for the release (E7): the README's multiplayer warning is a PNG because GitHub's README renderers do not allow coloured text, and the
/// warning must be LARGE and RED (owner, DESIGN_SPEC 6b / 6d). Made by the editor itself so the repository needs no other tool.
/// </summary>
static class ReleaseTools
{
    /// <summary>make-banner &lt;out.png&gt;: the warning, red lettering on white, written once.</summary>
    public static int MakeBanner(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: DepacheRwEditor make-banner <out.png>"); return 2; }
        try
        {
            const int w = 1500, h = 300;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.White);
                var red = Color.FromArgb(200, 16, 16);
                using var border = new Pen(red, 10);
                g.DrawRectangle(border, 5, 5, w - 10, h - 10);
                using var big = new Font("Segoe UI", 72f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var small = new Font("Segoe UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(red);
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("NOT FOR MULTIPLAYER", big, brush, new RectangleF(0, 20, w, 150), center);
                g.DrawString("An edited copy cannot join normal online games: every player needs byte-identical mod files.\nFor online play use the original mod.",
                    small, brush, new RectangleF(40, 180, w - 80, 100), center);
            }
            string full = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            bmp.Save(full, ImageFormat.Png);
            Console.WriteLine(full);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            Console.Error.WriteLine("make-banner failed: " + e.Message);
            return 1;
        }
    }
}
