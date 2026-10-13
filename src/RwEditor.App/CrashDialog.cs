namespace RwEditor.App;

/// <summary>Says in plain words that something went wrong, where the crash log is, and offers to open the log folder.</summary>
sealed class CrashDialog : Form
{
    public CrashDialog(string what, Exception e, string? logPath, bool canContinue)
    {
        Text = "Something went wrong";
        Icon = AppHost.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(560, 300);

        var msg = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Text =
                $"The editor ran into a problem while {what}.\n\n" +
                (canContinue
                    ? "Nothing was saved halfway: your copy is as it was before. You can keep working.\n\n"
                    : "The editor has to close. Your copy is as it was before the last action.\n\n") +
                (logPath != null
                    ? $"A crash report was saved here:\n{logPath}\n\nIf this keeps happening, please send that file to Depache."
                    : "The crash report could not be saved (the log folder is not writable)."),
        };
        var detail = new TextBox
        {
            Dock = DockStyle.Bottom, Height = 70, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = $"{e.GetType().Name}: {e.Message}", Font = new Font("Consolas", 9f),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var ok = new Button { Text = canContinue ? "Keep working" : "Close the editor", AutoSize = true, DialogResult = DialogResult.OK };
        var open = new Button { Text = "Open log folder", AutoSize = true };
        open.Click += (_, _) => AppHost.OpenFolder(AppHost.Log.Folder);
        var copy = new Button { Text = "Copy details", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{what}\n{e}\n\nlog: {logPath}"); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: not important */ }
        };
        buttons.Controls.AddRange(new Control[] { ok, open, copy });
        Controls.Add(msg);
        Controls.Add(detail);
        Controls.Add(buttons);
        AcceptButton = ok;
    }
}
