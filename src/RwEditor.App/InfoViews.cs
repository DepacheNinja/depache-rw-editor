using RwEditor.Core.Imaging;
using RwEditor.Core.Mod;

namespace RwEditor.App;

static class Ui
{
    public static Label Heading(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Padding = new Padding(4, 8, 0, 0) };

    public static Label Para(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(900, 0), Padding = new Padding(6, 6, 6, 6) };

    public static Button Btn(string text, Action onClick, IWin32Window? owner = null, string? what = null)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(6) };
        b.Click += (_, _) => AppHost.SafeRun(owner ?? b.FindForm(), what ?? text.ToLowerInvariant().TrimEnd('.'), onClick);
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
        p.Controls.AddRange(controls);
        return p;
    }

    /// <summary>Adds controls top to bottom (Dock=Top stacks in reverse add order, so they are added reversed).</summary>
    public static void Stack(Control parent, params Control[] controls)
    {
        for (int i = controls.Length - 1; i >= 0; i--) parent.Controls.Add(controls[i]);
    }
}

/// <summary>A section that comes in a later phase: says what it will do, in plain words.</summary>
sealed class ComingLaterView : UserControl
{
    public ComingLaterView(string title, string what, string when)
    {
        AutoScroll = true;
        Padding = new Padding(12);
        Ui.Stack(this,
            Ui.Heading(title),
            Ui.Para(what),
            Ui.Para(when),
            Ui.Para("Until then nothing in this section can be changed, so nothing here can break your game."));
    }
}

/// <summary>Shown when no mod folder is open yet.</summary>
sealed class WelcomeView : UserControl
{
    public WelcomeView(Action makeCopy, Action openFolder)
    {
        AutoScroll = true;
        Padding = new Padding(12);
        Ui.Stack(this,
            Ui.Heading("Welcome"),
            Ui.Para("This editor lets you look at (and later change) every unit of Depache's Mod Tribute and the four mods inside it."),
            Ui.Para("It never changes the mod you got from the Steam Workshop. Instead it makes your own copy, and only that copy is ever changed. " +
                    "Click \"Make my copy\" to start. It takes about a minute and tells you every step."),
            Ui.Row(Ui.Btn("Make my copy...", makeCopy, what: "making a copy"), Ui.Btn("Just look at a mod folder...", openFolder, what: "opening a folder")));
    }
}

/// <summary>Which copy and game folder the editor uses, and the actions around them.</summary>
sealed class SettingsView : UserControl
{
    readonly Label _copy = Ui.Para("");
    readonly Label _game = Ui.Para("");

    public SettingsView(Action makeCopy, Action openCopy, Action openFolder, Action changeGame, Action clearThumbs)
    {
        AutoScroll = true;
        Padding = new Padding(12);
        Ui.Stack(this,
            Ui.Heading("Settings"),
            new Label { Text = "Your copy", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(4, 10, 0, 0) },
            _copy,
            Ui.Row(Ui.Btn("Make my copy...", makeCopy, what: "making a copy"), Ui.Btn("Open my existing copy...", openCopy, what: "opening a copy"),
                   Ui.Btn("Just look at a mod folder...", openFolder, what: "opening a folder")),
            new Label { Text = "Game folder", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(4, 10, 0, 0) },
            _game,
            Ui.Row(Ui.Btn("Change the game folder...", changeGame, what: "changing the game folder")),
            new Label { Text = "Pictures", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(4, 10, 0, 0) },
            Ui.Para($"Unit pictures are made from your copy's own files and kept in {Thumbnails.CacheRoot} so the list opens fast. Nothing of the mod's art is sent anywhere."),
            Ui.Row(Ui.Btn("Rebuild the pictures", clearThumbs, what: "rebuilding the pictures")),
            new Label { Text = "About", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Padding = new Padding(4, 10, 0, 0) },
            Ui.Para($"{AppHost.Title} {AppHost.Version} by Depache.\nThank you for downloading this and taking the time to enjoy my work ^_^"));
    }

    public void ShowSession(ModSession? s)
    {
        if (s == null) _copy.Text = "No copy is open yet.";
        else if (s.IsEditorCopy && s.Marker is EditorCopyMarker m)
            _copy.Text = $"\"{m.Name}\" in {s.Root}\nMade {m.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} from {m.SourcePath} (mod version {m.SourceModVersion}).";
        else _copy.Text = $"Looking at {s.Root} (read-only: not a copy made by this editor).\n{s.NotCopyReason}";
        _game.Text = AppHost.Settings.GameFolder ?? s?.GameDir ?? "Not found yet. Click \"Change the game folder\" and pick the folder that holds \"Rusted Warfare.exe\".";
    }
}
