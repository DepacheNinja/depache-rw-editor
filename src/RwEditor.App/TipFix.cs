using System.Runtime.InteropServices;
using System.Text;

namespace RwEditor.App;

/// <summary>
/// A hover tooltip of the stat table can stay on screen on top of a dialog that opens while it is showing and hide the dialog's OK button (seen in the
/// owner's revert test: the window looked frozen). <see cref="HideAll"/> hides every tooltip window of this app's UI thread; dialogs call it when they
/// open, and the main window calls it before it shows any message box.
/// </summary>
static class TipFix
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    public static void HideAll()
    {
        try
        {
            var sb = new StringBuilder(64);
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                sb.Clear();
                if (GetClassName(h, sb, sb.Capacity) > 0 && sb.ToString().StartsWith("tooltips_class32", StringComparison.Ordinal)) ShowWindow(h, 0);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { AppHost.Log.Warn("tooltip hide failed: " + e.Message); }
    }

    /// <summary>Hides stuck tooltips when the form opens (and again a moment later: the stat table may show one just as the dialog appears).</summary>
    public static void Attach(Form f)
    {
        f.Shown += (_, _) =>
        {
            HideAll();
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            int n = 0;
            t.Tick += (_, _) => { HideAll(); if (++n >= 6 || f.IsDisposed) { t.Stop(); t.Dispose(); } };
            t.Start();
        };
    }
}
