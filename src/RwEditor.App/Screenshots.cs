using System.Drawing.Imaging;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// --screenshot &lt;dir&gt; --mod &lt;folder&gt;: opens the folder in the main window and renders each main screen and each wizard page to a PNG with
/// Control.DrawToBitmap (for the README). Nothing is copied or changed; the wizard pages show the real detected game and mods.
/// </summary>
static class Screenshots
{
    public static int Run(string dir, string? folder)
    {
        if (folder == null) { AppHost.Log.Error("--screenshot needs --mod <folder>"); Console.Error.WriteLine("--screenshot needs --mod <folder>"); return 2; }
        string outDir = Path.GetFullPath(dir);
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        int failures = 0;
        var form = new MainForm(folder, explicitFolder: true) { StartPosition = FormStartPosition.Manual, Location = new Point(10, 10) };
        form.Shown += async (_, _) =>
        {
            try
            {
                if (!await form.Loaded.Task.WaitAsync(TimeSpan.FromMinutes(3))) { failures++; AppHost.Log.Error("screenshot: the folder did not load"); return; }
                void Shot(string name, Control c)
                {
                    Application.DoEvents();
                    c.Refresh();
                    Application.DoEvents();
                    using var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppArgb);
                    c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
                    string p = Path.Combine(outDir, name + ".png");
                    bmp.Save(p, ImageFormat.Png);   // one write per picture
                    written.Add(p);
                    AppHost.Log.Info($"screenshot {p} ({c.Width}x{c.Height})");
                }

                form.SelectNav("area:");
                form.Browser.ExpandAll(false);
                form.Browser.Select(0);
                form.Browser.Details.Tabs.SelectedIndex = 0;
                Shot("01-units-all-families-closed", form);

                // one family opened (ACV: upgrade levels, water versions, nano build, parts), the ACV2 entry selected
                if (form.Browser.SelectUnit("ACV2"))
                {
                    string fam = form.Browser.SelectedRow!.Key;
                    int top = form.Browser.VisibleRows.ToList().FindIndex(r => r.Kind == RwEditor.Core.Browse.DisplayKind.Root && r.Key == fam);
                    form.Browser.List.TopItem = form.Browser.List.Items[Math.Max(0, top)];   // the family's root row at the top
                    Shot("02-units-family-acv-open", form);
                }
                form.Browser.ExpandAll(false);

                form.Browser.SetSearch("hover");
                form.Browser.Select(0);
                Shot("03-units-search-hover-simple", form);
                form.Browser.Details.Tabs.SelectedIndex = 1;
                Shot("04-units-search-hover-expand", form);
                form.Browser.Details.Tabs.SelectedIndex = 0;
                form.Browser.SetSearch("");

                form.SelectNav("area:moo");
                form.Browser.Select(0);
                Shot("05-units-moos-defence-agency", form);
                form.SelectNav("area:fe");
                form.Browser.Select(0);
                Shot("06-units-fabricator-extreme", form);
                form.SelectNav("area:");

                // the end of the list: the two part lists
                form.Browser.Select(form.Browser.ViewCount - 1);
                Shot("07-units-part-lists", form);

                // the three pages that need something on them to be worth a picture are filled with an example first (in memory only; nothing is applied)
                if (form.Session!.CanEdit)
                {
                    form.SelectNav("groups");
                    form.Groups.AddGroupForTest("Hover strike", RwEditor.Core.Settings.GroupMode.FollowToggle, true, ("heavyAATank", 2), ("fastSub", 1));
                    form.Groups.AddGroupForTest("Builders", RwEditor.Core.Settings.GroupMode.HumansOnly, false, ("heavyAATank", 1));
                }
                Shot("08-spawn-groups", form);
                form.SelectNav("menu");
                if (form.MenuPage.SelectOptionForTest("baseIncome") && form.Session.CanEdit)
                {
                    form.MenuPage.SetNumberForTest("income.credits", 400);
                    form.MenuPage.TypeNameForTest("Steady income");
                }
                Shot("09-menu-options", form);
                form.SelectNav("logs");
                form.LogsPage.Game.AnalyzeTextForTest("recorded-load-error.log",
                    "2026-10-08 17:10:30.001: Loading mod at:/SD/mods/units/MyCopy (name:[Custom My copy] Depache's Mod Tribute)\n"
                    + "2026-10-08 17:10:43.322: Adding error for mod: [Custom My copy]: Error loading unit: startingOptions/gameOptionSelector.ini (in mod [Custom My copy] Depache's Mod Tribute): \n"
                    + "[action_incomeMeOff]id cannot be longer than 15 characters\n"
                    + "2026-10-08 17:10:43.323: Disabling mod due to error: [Custom My copy] path:/SD/mods/units/MyCopy\n");
                Shot("10-logs", form);
                form.SelectNav("settings"); Shot("11-settings", form);

                int n = 12;
                foreach (var page in new[] { "welcome", "game", "source", "destination", "done" })
                {
                    using var w = new CopyWizard { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
                    w.Show(form);
                    w.PrepareForScreenshot(page);
                    Shot($"{n++:00}-wizard-{page}", w);
                    w.Close();
                }

                await EditShotsAsync(form, Shot, outDir);
            }
            catch (Exception e) { failures++; AppHost.Log.Error("screenshot run failed", e); }
            finally { form.Close(); }
        };
        Application.Run(form);
        form.Dispose();
        foreach (var p in written) Console.WriteLine(p);
        if (AppHost.UnattendedFailures > 0) failures++;
        return failures == 0 && written.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// E3 screens, on an editor COPY (--mod must be one): editable rows, staged changes in the pending panel, the "this value is shared" dialog, the
    /// Expand and "All other values" tabs, History after an apply, the Revert dialog, and the read-only banner (the copy's source opened read-only).
    /// Backups go to a temp folder; the apply is undone and the pending list emptied at the end, so the copy is left as it was.
    /// </summary>
    static async Task EditShotsAsync(MainForm form, Action<string, Control> Shot, string outDir)
    {
        var s = form.Session!;
        if (!s.CanEdit)
        {
            AppHost.Log.Warn("screenshot: the folder is not an editable copy; only the read-only banner is shown");
            form.SelectNav("area:");
            form.Browser.SelectUnit("heavyAATank");
            Shot("27-read-only-banner", form);
            return;
        }
        string bk = Path.Combine(Directory.GetParent(outDir)?.FullName ?? Path.GetTempPath(), "rwe-shots-backups-" + Guid.NewGuid().ToString("N")[..8]);
        AppHost.BackupRootOverride = bk;
        try
        {
            form.SelectNav("area:");
            form.Browser.SelectUnit("heavyAATank");
            form.Browser.Details.SelectStat("core.maxHp", "core");
            Shot("20-edit-unit-editable-rows", form);

            await form.Browser.Details.TypeValueAsync("core.maxHp", "core", "13000");
            await form.Browser.Details.TypeValueAsync("core.price.credits", "core", "6000");
            await form.Editor.RefreshChecksAsync();
            form.Browser.Details.SelectStat("core.price.credits", "core");
            Shot("21-edit-pending-panel", form);

            form.Browser.Details.Tabs.SelectedIndex = 1;
            Shot("23-edit-expand-tab", form);
            form.Browser.Details.Tabs.SelectedIndex = 2;
            Shot("24-edit-all-other-values", form);
            form.Browser.Details.Tabs.SelectedIndex = 0;

            // the shared-value dialog with the real checks (GenT1's build time comes from donors/fe/generators/gen_common.ini: 10 units)
            var gen = s.Model.Find("GenT1");
            if (gen != null)
            {
                var e = s.Dict.ById("core.buildSpeed")!;
                var info = StatStaging.Info(s.Model, s.Dict, s.Families, gen, e, "core");
                var ch = new RwEditor.Core.Settings.PendingStatEdit { Unit = gen.Name, UnitFile = gen.RelPath, StatId = e.Id, Section = "core", Value = "40s" };
                var (only, all) = await Task.Run(() => (StatStaging.Check(s.Model, s.Dict, ch, s.ReadDisk),
                    StatStaging.Check(s.Model, s.Dict, new RwEditor.Core.Settings.PendingStatEdit { Unit = gen.Name, UnitFile = gen.RelPath, StatId = e.Id, Section = "core", Value = "40s", Mode = "template" }, s.ReadDisk)));
                using var d = new ShareChoiceDialog(UnitRowText.For(s, gen), info, only, all) { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) };
                d.Show(form);
                Shot("22-edit-shared-value-dialog", d);
                d.Close();
            }

            // apply, then History, the Revert dialog, undo
            await form.Editor.ApplyAsync();
            form.SelectNav("history");
            Shot("25-history", form);
            form.SelectNav("area:");
            form.Browser.SelectUnit("heavyAATank");
            var row = form.Browser.Details.SelectStat("core.maxHp", "core");
            using (var rd = new RevertDialog(form.Session!, form.Browser.Details.Unit, row) { StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) })
            {
                rd.Show(form);
                rd.Choose(RevertScope.Unit);
                await rd.PreviewDone;
                Shot("26-revert-dialog", rd);
                rd.Close();
            }
            await form.Editor.UndoLastAsync();

            // the read-only banner: the copy's source, opened just to look
            string? src = form.Session?.Marker?.SourcePath;
            if (src != null && Directory.Exists(src) && await form.OpenAsync(src, remember: false))
            {
                form.Browser.SelectUnit("heavyAATank");
                Shot("27-read-only-banner", form);
            }
        }
        finally
        {
            AppHost.BackupRootOverride = null;
            try { if (Directory.Exists(bk)) Directory.Delete(bk, true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        }
    }
}
