using System.Drawing.Imaging;
using System.Text;
using RwEditor.Core.Imaging;

namespace RwEditor.App;

/// <summary>
/// Entry point. Normal start: the main window (opens the remembered copy, or starts "Make my copy" on the first run).
/// Hidden switches (for the developer and the build, not shown to players):
///   --mod &lt;folder&gt;                       open this folder (read-only unless it is an editor copy)
///   --selftest [--mod &lt;folder&gt;] [--report &lt;file&gt;]   load, build the model and pictures, open and close the main window without showing dialogs; exit 0 = OK
///   --screenshot &lt;dir&gt; --mod &lt;folder&gt;   render every main screen to PNG (Control.DrawToBitmap), then exit
///   make-banner &lt;out.png&gt; the README's red multiplayer warning picture
///   make-icon &lt;image.jpg|png&gt; &lt;out.ico&gt; build the multi-size application icon (16..256) from the mod thumbnail
/// Exit codes: 0 OK, 1 a check failed, 2 bad arguments, 3 crash.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "make-icon") return MakeIcon(args);
        if (args.Length >= 1 && args[0] == "make-banner") return ReleaseTools.MakeBanner(args);

        string? mod = Arg(args, "--mod");
        string? shotDir = Arg(args, "--screenshot");
        bool selftest = args.Contains("--selftest");
        AppHost.NoArt = args.Contains("--no-art");
        AppHost.Unattended = selftest || shotDir != null;
        AppHost.Init();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppHost.Crash("working in the editor window", e.Exception, canContinue: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown error");
            AppHost.Log.WriteCrashLog(ex, "a background task (the editor must close)");
            AppHost.Log.WriteSessionLog();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppHost.Log.Error("a background task failed without being checked", e.Exception);
            e.SetObserved();
        };

        int code = 3;
        try
        {
            ApplicationConfiguration.Initialize();
            if (selftest) code = SelfTest.Run(mod, Arg(args, "--report"));
            else if (shotDir != null) code = Screenshots.Run(shotDir, mod);
            else
            {
                using var main = new MainForm(mod, explicitFolder: mod != null);
                Application.Run(main);
                code = 0;
            }
        }
        catch (Exception e)
        {
            AppHost.Crash("starting the editor", e, canContinue: false);
            code = 3;
        }
        finally
        {
            AppHost.Log.Info($"exit code {code}");
            AppHost.Log.WriteSessionLog();
        }
        return code;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    /// <summary>make-icon &lt;source image&gt; &lt;out.ico&gt;: decodes the image with System.Drawing (JPEG / PNG), builds the icon in Core, writes it once.</summary>
    static int MakeIcon(string[] args)
    {
        if (args.Length is not (3 or 4)) { Console.Error.WriteLine("usage: DepacheRwEditor make-icon <image.jpg|png> <out.ico> [sizes, e.g. 16,32,48]"); return 2; }
        int[]? sizes = args.Length == 4 ? args[3].Split(',').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
        try
        {
            RgbaImage img;
            using (var bmp = new Bitmap(args[1]))
            {
                img = new RgbaImage(bmp.Width, bmp.Height);
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[bmp.Width * 4];
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 0; x < bmp.Width; x++) img.Set(x, y, row[x * 4 + 2], row[x * 4 + 1], row[x * 4], row[x * 4 + 3]);
                    }
                }
                finally { bmp.UnlockBits(data); }
            }
            byte[] ico = IconBuilder.Build(img, sizes);
            File.WriteAllBytes(args[2], ico);
            var sb = new StringBuilder($"wrote {args[2]} ({ico.Length} bytes) from {args[1]} ({img.Width}x{img.Height}):");
            foreach (var d in IconBuilder.ReadDirectory(ico)) sb.Append($" {d.Width}{(d.IsPng ? "png" : "")}");
            Console.WriteLine(sb.ToString());
            return 0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or FormatException or System.Runtime.InteropServices.ExternalException)
        {
            Console.Error.WriteLine("make-icon failed: " + e.Message);
            return 1;
        }
    }
}
