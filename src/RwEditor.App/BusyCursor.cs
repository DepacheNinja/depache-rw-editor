namespace RwEditor.App;

/// <summary>
/// The "busy" mouse cursor (the spinning circle) while the window works. Steps nest (a save runs a re-read, which loads pictures), and pages are rebuilt
/// while the cursor is on, so setting <see cref="Control.UseWaitCursor"/> on the window alone left some controls with the busy cursor for good ("loading
/// forever" after Apply). Here a counter keeps the cursor on until the outermost step ends, and each control is set by hand, so a control that kept its
/// own value is still reset.
/// </summary>
static class BusyCursor
{
    static int _depth;

    public static IDisposable Begin(Form form)
    {
        if (_depth++ == 0) Apply(form, true);
        return new Scope(form);
    }

    static void End(Form form)
    {
        if (_depth > 0 && --_depth == 0) Apply(form, false);
    }

    /// <summary>True while a step is running (tests).</summary>
    public static bool Active => _depth > 0;

    /// <summary>A page that was out of the window while the cursor changed gets the current state when it is shown.</summary>
    public static void Sync(Control page) => Set(page, _depth > 0);

    static void Apply(Control root, bool on)
    {
        if (root.IsDisposed) return;
        Set(root, on);
        if (!on && root.IsHandleCreated)
        {
            // the cursor is only looked at again when the mouse moves: nudge it so the arrow comes back at once
            try { Cursor.Position = Cursor.Position; } catch { /* no desktop (unattended): nothing to refresh */ }
        }
    }

    static void Set(Control c, bool on)
    {
        c.UseWaitCursor = on;
        foreach (Control child in c.Controls) Set(child, on);
    }

    sealed class Scope(Form form) : IDisposable
    {
        bool _done;
        public void Dispose() { if (_done) return; _done = true; End(form); }
    }
}
