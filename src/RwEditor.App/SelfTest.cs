using System.Diagnostics;
using System.Text;
using RwEditor.Core;
using RwEditor.Core.Browse;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// --selftest: proves the app works on a real mod folder without anyone clicking: the bad-folder cases end in friendly refusals (no crash), the folder
/// loads, the main window opens (invisible), the pictures load, every unit's stat view can be built, every section opens, a search works, and a scroll
/// test pages through every row. Timings go to the log and the report. Exit 0 = everything passed.
/// </summary>
static class SelfTest
{
    public static int Run(string? folder, string? reportPath)
    {
        var report = new List<string>();
        int failures = 0;
        void Ok(string s) { report.Add("PASS " + s); AppHost.Log.Info("selftest PASS " + s); }
        void Fail(string s) { failures++; report.Add("FAIL " + s); AppHost.Log.Error("selftest FAIL " + s); }
        void Note(string s) { report.Add("     " + s); AppHost.Log.Info("selftest " + s); }

        folder ??= AppHost.Settings.CopyFolder;
        if (folder == null) { Fail("no folder: pass --mod <folder> (or make a copy first)"); return Finish(report, reportPath, failures); }

        // 1. bad folders must give a plain refusal, never a crash
        string tmp = Path.Combine(Path.GetTempPath(), "rwe-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            try { ModSession.Load(Path.Combine(tmp, "missing"), null); Fail("a missing folder was accepted"); }
            catch (EditorRefusalException e) { Ok($"missing folder refused in plain words ({e.Code})"); }
            Directory.CreateDirectory(Path.Combine(tmp, "empty"));
            try { ModSession.Load(Path.Combine(tmp, "empty"), null); Fail("a folder without units was accepted"); }
            catch (EditorRefusalException e) { Ok($"folder without units refused in plain words ({e.Code})"); }
            string corrupt = Path.Combine(tmp, "corrupt");
            Directory.CreateDirectory(Path.Combine(corrupt, "units"));
            File.WriteAllText(Path.Combine(corrupt, "editor-support.json"), "{ this is not json");
            File.WriteAllText(Path.Combine(corrupt, "rweditor-copy.json"), "{ nor this");
            File.WriteAllText(Path.Combine(corrupt, "units", "a.ini"), "[core]\nname: a\nmaxHp: 10\n[graphics]\nimage: missing.png\n");
            File.WriteAllBytes(Path.Combine(corrupt, "units", "bad.ini"), new byte[] { 0xFF, 0xFE, 0x00, 0x5B, 0x00 });
            var cs = ModSession.Load(corrupt, null);
            if (cs.Rows.Count >= 1 && !cs.IsEditorCopy && cs.Warnings.Count > 0) Ok($"corrupt copy opens read-only with a warning ({cs.Rows.Count} unit, reason: {Short(cs.NotCopyReason)})");
            else Fail($"corrupt copy: rows {cs.Rows.Count}, editor copy {cs.IsEditorCopy}, warnings {cs.Warnings.Count}");
            var t = RwEditor.Core.Imaging.Thumbnails.LoadOrBuild(cs.Model, null, cacheRoot: Path.Combine(tmp, "thumbs"));
            if (t.Thumbs.Count == cs.Rows.Count) Ok("pictures of the corrupt copy fall back without a crash"); else Fail("pictures of the corrupt copy");
        }
        catch (Exception e) { Fail("bad-folder checks crashed: " + e); }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // 2. the real folder in the real window
        MainForm? form = null;
        try
        {
            form = new MainForm(folder, explicitFolder: true) { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(20, 20) };
            form.Shown += async (_, _) =>
            {
                try
                {
                    bool loaded = await form.Loaded.Task.WaitAsync(TimeSpan.FromMinutes(3));
                    if (!loaded || form.Session == null) { Fail("the folder could not be loaded: " + folder); return; }
                    Checks(form, Ok, Fail, Note);
                    await EditChecksAsync(form, Ok, Fail, Note);
                }
                catch (Exception e) { Fail("self-test crashed: " + e); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        catch (Exception e) { Fail("main window crashed: " + e); }
        finally { form?.Dispose(); }
        if (AppHost.UnattendedFailures > 0) Fail($"{AppHost.UnattendedFailures} problem(s) were reported to the user during the run (see the log)");
        return Finish(report, reportPath, failures);
    }

    static void Checks(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var s = form.Session!;
        var th = form.Thumbs!;
        form.Refresh();          // paint the loaded list now, before the long checks below hold the UI thread (time to first paint)
        Application.DoEvents();
        Note($"folder: {s.Root} (editor copy: {s.IsEditorCopy}; game folder: {s.GameDir ?? "-"})");
        if (AppHost.AppIcon is Icon ic) Ok($"application icon loads ({ic.Width}x{ic.Height} picked for the window)"); else Fail("the application icon is missing from the build");
        if (form.Text == AppHost.Title) Ok($"window title '{form.Text}'"); else Fail("window title is " + form.Text);
        if (s.Rows.Count > 0) Ok($"{s.Rows.Count} units loaded in {s.LoadTime.TotalMilliseconds:0} ms"); else Fail("no units");
        if (th.Ready && th.Count == s.Rows.Count) Ok($"pictures: {form.ThumbReport}"); else Fail($"pictures: ready {th.Ready}, {th.Count} for {s.Rows.Count} units");

        // every unit's read-only stat view
        var sw = Stopwatch.StartNew();
        int simple = 0, expand = 0;
        foreach (var r in s.Rows) { var d = StatView.Describe(s.Model, s.Dict, r); simple += d.Simple.Count; expand += d.Expand.Count; }
        Ok($"stat view built for all {s.Rows.Count} units in {sw.Elapsed.TotalMilliseconds:0} ms ({simple} simple rows, {expand} expand rows)");

        // families (E2b): the counts, every entry exactly once, the owner's two examples
        var fs = s.Families.Stats;
        if (fs.Roots + fs.InsideFamilies + fs.SharedParts + fs.HelperParts == s.Rows.Count)
            Ok($"families: {fs.Roots} units at the top with {fs.InsideFamilies} entries inside them, {fs.SharedParts} shared parts, {fs.HelperParts} helper parts " +
               $"= {s.Rows.Count} entries; built in {s.FamilyTime.TotalMilliseconds:0} ms");
        else Fail($"families do not add up: {fs.Roots} + {fs.InsideFamilies} + {fs.SharedParts} + {fs.HelperParts} != {s.Rows.Count}");
        Note("status line: " + form.Browser.CountText);
        if (form.Browser.SelectUnit("ACV2") && form.Browser.SelectedRow is { Kind: DisplayKind.Child } acv2 && acv2.Family.Root?.Name == "ACV")
            Ok($"ACV2 is inside the ACV family ('{acv2.Entry!.Label}'), its stats show: {form.Browser.Details.FamilyText}");
        else if (s.Model.Find("ACV2") != null) Fail("ACV2 is not shown inside the ACV family");

        // search and filters through the real controls
        form.Browser.ExpandAll(false);
        form.Browser.SetSearch("hover");
        if (form.Browser.ViewCount > 0) Ok($"search 'hover': {form.Browser.Listing!.Units} units, {form.Browser.Listing.Inside} entries inside them, {form.Browser.ViewCount} rows ({form.Browser.Listing.AutoExpanded.Count} families opened by the search)");
        else Fail("search 'hover' found nothing");
        form.Browser.SetSearch("ACV3_water");
        if (form.Browser.VisibleRows.Any(r => r.Kind == DisplayKind.Child && r.Row?.Name == "ACV3_water") || s.Model.Find("ACV3_water") == null)
            Ok($"search for a family member shows it under its root, opened ({form.Browser.ViewCount} rows)");
        else Fail("search 'ACV3_water' did not show the entry under its family");
        form.Browser.SetSearch("zzzz-no-such-unit");
        if (form.Browser.ViewCount == 0) Ok("search for a missing name shows nothing (with a message)"); else Fail("nonsense search found units");
        form.Browser.SetSearch("");
        foreach (var tag in new[] { "area:fe", "area:moo", "area:vte", "area:depache", "area:main", "groups", "menu", "logs", "settings", "area:" })
        {
            form.SelectNav(tag);
            Application.DoEvents();
        }
        Ok($"every section opened; back on all units ({form.Browser.ViewCount} rows)");

        // open / close one family through the same path a click on the arrow takes
        int before = form.Browser.ViewCount;
        int fi = form.Browser.VisibleRows.ToList().FindIndex(r => r.Kind == DisplayKind.Root && r.HasChildren);
        if (fi >= 0)
        {
            int kids = form.Browser.VisibleRows[fi].ChildCount;
            form.Browser.Toggle(fi);
            int opened = form.Browser.ViewCount;
            form.Browser.Toggle(fi);
            if (opened == before + kids && form.Browser.ViewCount == before) Ok($"opening a family adds its {kids} entries, closing it removes them");
            else Fail($"open / close: {before} -> {opened} -> {form.Browser.ViewCount} rows (family of {kids})");
        }

        // the locked filter, per entry, with every family open: each entry exactly once
        form.Browser.ExpandAll(true);
        int withoutLocked = form.Browser.VisibleRows.Count(r => r.Row != null && !r.Context);
        form.Browser.ShowLocked = true;   // the scroll test covers every entry, the locked machinery included
        var shownUnits = form.Browser.VisibleRows.Where(r => r.Row != null).Select(r => r.Row!.Unit.RelPath).ToList();
        if (shownUnits.Count == s.Rows.Count && shownUnits.Distinct(StringComparer.OrdinalIgnoreCase).Count() == s.Rows.Count && withoutLocked == s.Rows.Count(r => !r.Locked))
            Ok($"locked machinery hidden by default ({withoutLocked} entries shown), with it and every family open each of the {s.Rows.Count} entries is listed exactly once");
        else Fail($"locked filter / completeness: {withoutLocked} without, {shownUnits.Count} rows ({shownUnits.Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct) with, {s.Rows.Count} total");
        form.Browser.Select(Math.Min(10, form.Browser.ViewCount - 1));
        Application.DoEvents();

        // scroll test: page through every row, painting each page now
        form.Refresh();     // the window is shown but fully transparent (Opacity 0): it still paints, nothing appears on screen
        double decodedBefore = th.DecodeMs;
        var (pages, total, worst, rows) = form.Browser.ScrollTest();
        Ok($"scroll test, every family open: {rows} rows in {pages} pages, {total:0} ms total, {total / Math.Max(1, pages):0.0} ms per page average, worst page {worst:0.0} ms; " +
           $"{th.Decoded} pictures decoded on first draw in {th.DecodeMs - decodedBefore:0} ms");
        form.Browser.ExpandAll(false);
        form.Refresh();
        var (pages2, total2, worst2, rows2) = form.Browser.ScrollTest();
        Ok($"scroll test, families closed (the default view): {rows2} rows in {pages2} pages, {total2:0} ms total, {total2 / Math.Max(1, pages2):0.0} ms per page average, worst page {worst2:0.0} ms");
        if (form.Browser.FirstRowPaintMs is double fp) Ok($"time to first painted unit row with picture: {fp:0} ms after process start");
        else Fail("no unit row was painted");

        // the global UI-thread handler: a deliberate error must be caught, logged to a crash file at once, and the window must keep running
        int failuresBefore = AppHost.UnattendedFailures, crashesBefore = AppHost.Log.CrashCount;
        var started = DateTime.UtcNow.AddSeconds(-1);
        form.BeginInvoke(new Action(() => throw new InvalidOperationException("selftest: deliberate error on the UI thread")));
        for (int i = 0; i < 20 && AppHost.Log.CrashCount == crashesBefore; i++) { Application.DoEvents(); Thread.Sleep(10); }
        var crashFile = Directory.Exists(AppHost.Log.Folder)
            ? new DirectoryInfo(AppHost.Log.Folder).GetFiles("crash-*.log").Where(f => f.LastWriteTimeUtc >= started).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (AppHost.Log.CrashCount == crashesBefore + 1 && crashFile != null && File.ReadAllText(crashFile.FullName).Contains("deliberate error", StringComparison.Ordinal) && !form.IsDisposed)
        {
            Ok($"a deliberate UI-thread error was caught, written to {crashFile.Name} at once, and the window kept running");
            try { crashFile.Delete(); } catch (IOException) { }   // the self-test's own synthetic report
            Interlocked.Exchange(ref AppHost.UnattendedFailures, failuresBefore);
        }
        else Fail($"the global error handler did not write a crash log (crashes {AppHost.Log.CrashCount - crashesBefore}, file {crashFile?.Name ?? "none"})");
    }

    /// <summary>
    /// E3 UI-level edit test, through the same code paths a click takes: (1) the folder given with --mod, when it is not an editor copy, is read-only
    /// (banner, no editable cell, a typed value is refused); (2) a scratch COPY is made from it by CopyMaker (temp folder, backups in the temp folder),
    /// opened, a Health and a price change are typed into the New value cells (staged: only the settings file changes), a shared value takes the
    /// "every unit that shares it" path and is removed again, the changes are applied: exactly the unit's file changes (and the settings), lint 0 new;
    /// then History > Undo: every mod file is byte-identical to the fresh copy again. The scratch copy, its backups and its picture cache are deleted.
    /// </summary>
    static async Task EditChecksAsync(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var s0 = form.Session!;
        // (1) read-only folder
        if (!s0.IsEditorCopy)
        {
            if (!s0.CanEdit && s0.EditBlockedReason != null) Ok("a folder that is not an editor copy is read-only: " + Short(s0.EditBlockedReason));
            else Fail("a non-copy folder is editable");
            if (form.Browser.SelectUnit("heavyAATank"))
            {
                if (form.Browser.Details.EditableRows > 0 && form.Browser.Details.SelectStat("core.maxHp", "core") is { } r0)
                {
                    int msgs = AppHost.UnattendedMessages.Count;
                    bool staged = await form.Editor.StageAsync(s0.Model.Find("heavyAATank")!, r0, "13000", false);
                    if (!staged && AppHost.UnattendedMessages.Skip(msgs).Any(m => m.Contains("switched off", StringComparison.Ordinal)) && s0.Settings.PendingStatEdits.Count == 0)
                        Ok("typing a value in the read-only folder is refused in plain words; nothing staged");
                    else Fail("a value was accepted in a read-only folder");
                }
                if (!File.Exists(Path.Combine(s0.Root, RwEditor.Core.Mod.CopyFiles.SettingsName))) Ok("nothing was written into the read-only folder");
                else Fail("a settings file appeared in the read-only folder");
            }
        }

        // (2) a scratch copy made by CopyMaker
        string source = s0.IsEditorCopy && s0.Marker != null ? s0.Marker.SourcePath : s0.Root;
        string tmp = Path.Combine(Path.GetTempPath(), "rwe-selftest-edit-" + Guid.NewGuid().ToString("N")[..8]);
        string copy = Path.Combine(tmp, "copy");
        AppHost.BackupRootOverride = Path.Combine(tmp, "backups");
        var autoShare = AppHost.AutoShareChoice;
        try
        {
            var made = await Task.Run(() => RwEditor.Core.Setup.CopyMaker.MakeCopy(source, copy, "selftest", AppHost.Version));
            Note($"scratch copy {copy}: {made.Files} files in {made.Duration.TotalSeconds:0.0} s; saved original values: {RwEditor.Core.Mod.CopyBaseline.Load(copy)?.Files.Count ?? 0} files");
            if (!await form.OpenAsync(copy, remember: false) || form.Session?.CanEdit != true) { Fail("the scratch copy cannot be edited: " + form.Session?.EditBlockedReason); return; }
            var s = form.Session!;
            if (s.OriginalModel != null) Ok("the scratch copy opens editable, with its saved original values"); else Fail("no saved original values: " + s.BaselineNote);
            var before = Hashes(copy);
            var snapBefore = RwEditor.Core.Mod.ModSnapshot.Load(copy);

            form.SelectNav("area:");
            if (!form.Browser.SelectUnit("heavyAATank")) { Fail("heavyAATank not found"); return; }
            await form.Browser.Details.TypeValueAsync("core.maxHp", "core", "13000");
            await form.Browser.Details.TypeValueAsync("core.price.credits", "core", "6000");
            var staged = Hashes(copy);
            var stagedDiff = Diff(before, staged);
            if (s.Settings.PendingStatEdits.Count == 2 && form.Browser.Details.PendingRows == 2 && stagedDiff.SequenceEqual(new[] { RwEditor.Core.Mod.CopyFiles.SettingsName }))
                Ok($"Health 13000 and price 6000 typed into the New value cells: 2 pending (highlighted), only {RwEditor.Core.Mod.CopyFiles.SettingsName} written");
            else Fail($"staging: {s.Settings.PendingStatEdits.Count} pending, {form.Browser.Details.PendingRows} highlighted, changed: {string.Join(", ", stagedDiff)}; messages: {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(3))}");

            // the help box and the Reset button follow the selected row
            form.Browser.Details.SelectStat("core.maxHp", "core");
            bool resetPending = form.Browser.Details.ResetEnabled;
            string help = form.Browser.Details.HelpText;
            form.Browser.Details.SelectStat("core.maxShield", "core");
            bool resetPlain = form.Browser.Details.ResetEnabled;
            if (resetPending && !resetPlain && help.Contains("If you raise it:", StringComparison.Ordinal) && help.Contains("If you lower it:", StringComparison.Ordinal)
                && help.Contains("Original: 11500 hit points", StringComparison.Ordinal))
                Ok("help box: what it does, if you raise / lower it, the original value; Reset is offered only where there is something to reset");
            else Fail($"help / reset: reset on pending {resetPending}, on an unchanged value {resetPlain}; help: {Short(help.Replace('\n', ' '))}");

            // a shared value: the "every unit that shares it" choice (unattended answer) -> a template change listing every unit; then removed again
            if (form.Browser.SelectUnit("GenT1"))
            {
                AppHost.AutoShareChoice = (unitOk, templateOk) => templateOk ? true : null;
                await form.Browser.Details.TypeValueAsync("core.buildSpeed", "core", "40");
                var tp = s.Settings.PendingStatEdits.FirstOrDefault(p => p.Unit == "GenT1");
                string? confirm = AppHost.UnattendedMessages.LastOrDefault(m => m.StartsWith("CONFIRM Change every unit", StringComparison.Ordinal));
                if (tp?.Mode == "template" && confirm != null && confirm.Contains("ALL 10 units", StringComparison.Ordinal))
                    Ok("shared build time of GenT1: the choice dialog, then a confirmation listing all 10 units, staged as one shared change");
                else Fail($"shared change: mode {tp?.Mode ?? "none"}, confirmation: {confirm ?? "none"}");
                AppHost.AutoShareChoice = autoShare;
                form.Editor.RemovePending(p => p.Unit == "GenT1", "GenT1 build time");
                if (s.Settings.PendingStatEdits.Count == 2) Ok("a pending change is removed again without touching the copy"); else Fail("removing a pending change");
            }

            // family scope: the first family whose root shares its Health value with an upgrade level / version; every member is checked, then removed again
            var famUnit = s.Families.Items.Where(f => !f.IsBucket && f.Root != null && !f.Root.Locked).Select(f => f.Root!)
                .FirstOrDefault(u => s.Model.UnitsNamed(u.Name).Count == 1 && StatStaging.Info(s.Model, s.Dict, s.Families, u, s.Dict.ById("core.maxHp")!, "core") is { FamilyCandidates.Count: > 0, Shared: false });
            if (famUnit != null && form.Browser.SelectUnit(famUnit.Name))
            {
                var info = StatStaging.Info(s.Model, s.Dict, s.Families, famUnit, s.Dict.ById("core.maxHp")!, "core");
                double hp = info.Current.Number ?? 100;
                int before2 = s.Settings.PendingStatEdits.Count;
                await form.Browser.Details.TypeValueAsync("core.maxHp", "core", StatText.Num(hp + 1), family: true);
                var fam = s.Settings.PendingStatEdits.Where(p => p.Unit == famUnit.Name || p.FromFamilyOf == famUnit.Name).ToList();
                if (fam.Count == 1 + info.FamilyCandidates.Count && fam.Count(p => p.FromFamilyOf == famUnit.Name) == info.FamilyCandidates.Count)
                    Ok($"\"this unit + its upgrade levels / versions\" on {famUnit.Name}: {fam.Count} changes staged after each one was checked ({string.Join(", ", fam.Select(p => p.Unit))})");
                else Fail($"family scope on {famUnit.Name}: {fam.Count} staged for 1 + {info.FamilyCandidates.Count}; {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(2))}");
                form.Editor.RemovePending(p => p.Unit == famUnit.Name || p.FromFamilyOf == famUnit.Name, "family test");
                if (s.Settings.PendingStatEdits.Count != before2) Fail("the family test changes were not removed");
            }
            else Note("no family with a shared Health value found for the family-scope check");

            // "Set for many units": the real dialog (preview, Stage button) and the staging flow; the pending list is put back afterwards
            if (form.Browser.SelectUnit("heavyAATank") && form.Browser.Details.SelectStat("core.generation_resources.credits", "core") is { } credRow)
            {
                int pendingBefore = s.Settings.PendingStatEdits.Count;
                using (var bd = new BulkDialog(s, credRow.Entry, null) { Opacity = 0, ShowInTaskbar = false })
                {
                    bd.Show(form);
                    bd.TypeForTest("5");
                    await bd.RefreshNow();
                    for (int i = 0; i < 100 && bd.Plan == null; i++) { await Task.Delay(200); await bd.PreviewDone; }   // a later preview (the window's own timer) may replace this one
                    if (bd.Plan?.CanStage == true && bd.StageButton.Enabled && bd.PreviewText.Contains("get Credits produced = 5", StringComparison.Ordinal)
                        && bd.Plan.Ok.Count > 10 && bd.Plan.AffectedUnits.Count > bd.Plan.Ok.Count)
                        Ok($"Set for many units (dialog): {bd.Plan.Ok.Count} buildings checked, {bd.Plan.AffectedUnits.Count} units change with their upgrade levels, {bd.Plan.Skipped.Count} skipped with reasons, Stage button on");
                    else Fail($"bulk dialog: plan {bd.Plan?.Ok.Count}, can stage {bd.Plan?.CanStage}, preview: {Short(bd.PreviewText.Replace(Environment.NewLine, " | "))}");
                    bd.Close();
                }
                var autoBulk = AppHost.AutoBulk;
                AppHost.AutoBulk = _ => ("5", new BulkFilter(BulkWho.Buildings, null, BulkHave.Any, true));
                bool bulk = await form.Editor.StageBulkAsync(credRow);
                AppHost.AutoBulk = autoBulk;
                int bulkStaged = s.Settings.PendingStatEdits.Count - pendingBefore;
                if (bulk && bulkStaged > 10 && s.Settings.PendingStatEdits.Count(p => p.StatId == "core.generation_resources.credits") == bulkStaged) Ok($"Set for many units (staging): {bulkStaged} pending changes, each checked");
                else Fail($"bulk staging: ok {bulk}, staged {bulkStaged}; {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(2))}");
                form.Editor.RemovePending(p => p.StatId == "core.generation_resources.credits", "bulk test");
                if (s.Settings.PendingStatEdits.Count != pendingBefore) Fail("the bulk test changes were not removed");
            }

            await GroupsChecksAsync(form, Ok, Fail, Note);
            await MenuChecksAsync(form, Ok, Fail, Note);
            await LogChecksAsync(form, Ok, Fail, Note);

            // apply
            bool applied = await form.Editor.ApplyAsync();
            s = form.Session!;
            var after = Hashes(copy);
            var diff = Diff(before, after);
            var lintNew = RwEditor.Core.Validation.RwLint.NewFindings(snapBefore, RwEditor.Core.Mod.ModSnapshot.Load(copy));
            string unitText = File.ReadAllText(Path.Combine(copy, "aa_tanks", "heavy_AA_tank.ini"));
            if (applied && diff.SequenceEqual(new[] { "aa_tanks/heavy_AA_tank.ini", RwEditor.Core.Mod.CopyFiles.SettingsName }) && unitText.Contains("maxHp: 13000", StringComparison.Ordinal)
                && unitText.Contains("6000", StringComparison.Ordinal) && lintNew.Count == 0 && s.Settings.PendingStatEdits.Count == 0 && s.Settings.StatEdits.Count == 2)
                Ok($"Apply changes: only aa_tanks/heavy_AA_tank.ini (and the settings) changed, lint 0 new, 2 changes recorded, pending list empty");
            else Fail($"apply: ok {applied}, changed {string.Join(", ", diff)}, lint new {lintNew.Count}, pending {s.Settings.PendingStatEdits.Count}, recorded {s.Settings.StatEdits.Count}; {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(3))}");
            var lineDiff = LineDiff(Path.Combine(source, "aa_tanks", "heavy_AA_tank.ini"), Path.Combine(copy, "aa_tanks", "heavy_AA_tank.ini"));
            Note("changed lines: " + string.Join(" | ", lineDiff));
            if (lineDiff.Count == 2) Ok("exactly 2 lines of the unit file differ from the original (the two values)"); else Fail($"{lineDiff.Count} lines differ");
            if (form.Browser.SelectUnit("heavyAATank") && form.Browser.Details.SelectStat("core.maxHp", "core")?.ValueText == "13000 hit points") Ok("after the apply the editor shows the new Health (13000)");
            else Fail("the new Health is not shown after the apply");
            if (form.History.List.Items.Count >= 0) { form.SelectNav("history"); Application.DoEvents(); }
            if (form.History.List.Items.Count >= 1 && form.History.List.Items[0].SubItems[2].Text.Contains("heavyAATank Health", StringComparison.Ordinal)) Ok("History lists the apply with what it changed");
            else Fail("History does not list the apply");

            // undo
            bool undone = await form.Editor.UndoLastAsync();
            var afterUndo = Hashes(copy);
            var undoDiff = Diff(before, afterUndo);
            s = form.Session!;
            if (undone && undoDiff.SequenceEqual(new[] { RwEditor.Core.Mod.CopyFiles.SettingsName }) && s.Settings.StatEdits.Count == 0 && s.Settings.PendingStatEdits.Count == 0)
                Ok("History > Undo: every mod file is byte-identical to the fresh copy again (only the settings file exists now, with no changes in it)");
            else Fail($"undo: ok {undone}, differs: {string.Join(", ", undoDiff)}, recorded {s.Settings.StatEdits.Count}");
            form.SelectNav("area:");

            // revert dialog + reload stalls (needs an applied change: make one again)
            if (form.Browser.SelectUnit("heavyAATank"))
            {
                // Enter in the New value box must stage the value (the owner's test: Enter ended the edit but staged nothing)
                bool entered = form.Browser.Details.TypeAndPressEnter("core.maxHp", "core", "13000");
                for (int i = 0; i < 60 && form.Session!.Settings.PendingStatEdits.Count == 0; i++) await Task.Delay(100);
                if (entered && form.Session!.Settings.PendingStatEdits.Count == 1 && form.Browser.Details.PendingRows == 1) Ok("Enter in the New value box stages the value (highlighted, 1 pending)");
                else Fail($"Enter did not stage the value: opened {entered}, pending {form.Session!.Settings.PendingStatEdits.Count}");
                if (await form.Editor.ApplyAsync()) await RevertDialogChecksAsync(form, Ok, Fail, Note);
                else Fail("could not apply a change for the revert dialog test");
            }
        }
        catch (Exception e) { Fail("edit test crashed: " + e); }
        finally
        {
            AppHost.AutoShareChoice = autoShare;
            AppHost.BackupRootOverride = null;
            try { File.Delete(RwEditor.Core.Imaging.Thumbnails.CachePath(copy)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); Note("scratch copy and its backups deleted"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note("scratch folder not deleted: " + e.Message); }
        }
    }

    /// <summary>
    /// E4: the spawn builder page on the scratch copy: the picker lists the complete units with pictures, a 2-group setup (like the owner's) is built the way the
    /// buttons build it, the live check passes, the ring preview shows every unit, "Apply to my copy" writes the generated files and the settings, the copy's
    /// include rules still hold, and History > Undo puts files and settings back.
    /// </summary>
    static async Task GroupsChecksAsync(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var s = form.Session!;
        var gv = form.Groups;
        form.SelectNav("groups");
        Application.DoEvents();
        if (!gv.IsBuilt) { Fail("spawn builder page: clicking 'Start units / spawn groups' did not build the page"); return; }
        if (gv.PickerCount > 100 && gv.DefaultEntryCount == 4 && gv.NewButtonEnabled && gv.PickerHas("heavyAATank")) Ok($"spawn builder: picker lists {gv.PickerCount} units with pictures, the 4 default entries are listed");
        else { Fail($"spawn builder page: picker {gv.PickerCount}, default entries {gv.DefaultEntryCount}, new button {gv.NewButtonEnabled}"); return; }
        string root = s.Root;
        var anchorBefore = File.ReadAllBytes(Path.Combine(root, "editorGenerated", "anchorGroups.ini"));
        gv.AddGroupForTest("Hover strike", RwEditor.Core.Settings.GroupMode.FollowToggle, true, ("heavyAATank", 2), ("fastSub", 1));
        gv.AddGroupForTest("Builders", RwEditor.Core.Settings.GroupMode.HumansOnly, false, ("builderLand", 1));
        gv.SetOptionForTest("extraACV", RwEditor.Core.Settings.OptionState.Hidden);
        var findings = gv.CheckNow();
        var errors = findings.Where(f => f.Severity == RwEditor.Core.Severity.Error).ToList();
        if (errors.Count == 0 && gv.CanApply && gv.RingDots == 4 && gv.Dirty) Ok("2 groups + a hidden default entry: the live check passes, Apply is on, the ring preview shows all 4 units");
        else Fail($"groups live check: errors [{string.Join(" | ", errors.Select(e => e.Message))}], can apply {gv.CanApply}, ring dots {gv.RingDots}, status {gv.StatusText}");
        // PH-099/PH-100: a crowd the automatic layout cannot place (the owner's 8-unit case) is placed by hand: Apply is off, "Place by hand" turns it on,
        // a drag moves one unit, the generated file carries the dragged place
        var crowd = gv.AddGroupForTest("Crowd", RwEditor.Core.Settings.GroupMode.FollowToggle, false, ("atlantis", 6), ("ACV", 1), ("achilles", 1));
        gv.SelectGroupForTest(crowd);
        var crowdFindings = gv.CheckNow();
        bool refused = !gv.CanApply && crowdFindings.Any(f => f.Code == "LAYOUT_NO_FIT");
        gv.ToggleHandPlacementForTest();
        bool dragged = gv.DragForTest(0, 2, -620, 140);
        var after = gv.CheckNow();
        if (refused && gv.PlacedByHand && dragged && gv.CanApply && after.All(f => f.Severity != RwEditor.Core.Severity.Error) && crowd.Units[0].Offsets![2] == new RwEditor.Core.Settings.Offset(-620, 140)
            && gv.HandButtonText.StartsWith("Back to automatic", StringComparison.Ordinal))
            Ok("8 units the automatic layout refuses: Apply is off, 'Place by hand' turns it on, a dragged unit keeps its place (-620, 140)");
        else Fail($"hand placement: refused {refused}, by hand {gv.PlacedByHand}, dragged {dragged}, can apply {gv.CanApply}, status {gv.StatusText}");
        bool applied = await form.Editor.ApplyGroupsAsync(gv.Working);
        s = form.Session!;
        string anchor = File.ReadAllText(Path.Combine(root, "editorGenerated", "anchorGroups.ini"));
        string selector = File.ReadAllText(Path.Combine(root, "editorGenerated", "selectorGroups.ini"));
        if (applied && selector.Contains("Hover strike", StringComparison.Ordinal) && selector.Contains("xhextraACV", StringComparison.Ordinal) && anchor.Contains("ug01", StringComparison.Ordinal)
            && s.Settings.SpawnGroups.Count == 3 && anchor.Contains("offsetX=-620, offsetY=140", StringComparison.Ordinal) && s.Settings.SpawnGroups.Single(g => g.Name == "Crowd").Units[0].Offsets![2] == new RwEditor.Core.Settings.Offset(-620, 140) && s.Settings.GetOptionState("extraACV") == RwEditor.Core.Settings.OptionState.Hidden && !form.Groups.Dirty)
            Ok("Apply to my copy: both generated files and the copy's settings hold the 3 groups (one placed by hand) and the hidden entry, the page shows them as applied");
        else Fail($"groups apply: ok {applied}, groups {s.Settings.SpawnGroups.Count}, selector has group {selector.Contains("Hover strike", StringComparison.Ordinal)}; {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(2))}");
        // owner report "loading forever" after Apply: the busy cursor stayed on over the page once the save was done
        static IEnumerable<Control> Walk(Control c) { yield return c; foreach (Control k in c.Controls) foreach (var d in Walk(k)) yield return d; }
        var stuck = Walk(form).Concat(Walk(gv)).Where(c => c.UseWaitCursor).Select(c => c.GetType().Name + ":" + c.Name).Distinct().ToList();
        if (!BusyCursor.Active && stuck.Count == 0) Ok("after the save no control keeps the busy cursor (no 'loading forever')");
        else Fail($"busy cursor stuck after Apply: active {BusyCursor.Active}, controls {string.Join(", ", stuck.Take(8))}");
        var rules = RwEditor.Core.Validation.IncludeRules.Check(RwEditor.Core.Mod.ModSnapshot.Load(root));
        if (rules.AllPass) Ok("the include rules (one anchor per team, Done tags, human-only term...) still pass on the copy with groups"); else Fail("include rules: " + string.Join("; ", rules.Failures));
        bool undone = await form.Editor.UndoLastAsync();
        s = form.Session!;
        if (undone && File.ReadAllBytes(Path.Combine(root, "editorGenerated", "anchorGroups.ini")).AsSpan().SequenceEqual(anchorBefore) && s.Settings.SpawnGroups.Count == 0 && form.Groups.Groups.Count == 0)
            Ok("History > Undo: the generated files and the saved groups are back to the shipped (empty) state");
        else Fail($"groups undo: ok {undone}, groups left {s.Settings.SpawnGroups.Count}");
        form.SelectNav("area:");
    }

    /// <summary>
    /// E5a: the Menu options page on the scratch copy: all four sections with every option listed, the two options that always stay cannot be hidden, one option
    /// is hidden and one deleted, "Apply to my copy" writes the hide tags and the settings, the include rules hold, the page shows the applied state after the
    /// re-read, and "put every option back" plus an apply returns the copy to the shipped (empty) state.
    /// </summary>
    static async Task MenuChecksAsync(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var mv = form.MenuPage;
        string root = form.Session!.Root;
        form.SelectNav("menu");
        Application.DoEvents();
        var ctx = RwEditor.Core.Mod.ModContext.Load(root);
        string gen = Path.Combine(root, "editorGenerated");
        string settingsFile = Path.Combine(root, "rweditor-settings.json");
        string managerFile = Path.Combine(root, "myAssets", "ManagerBasic.ini"), selectorFile = Path.Combine(root, "startingOptions", "gameOptionSelector.ini");
        var before = new[] { Path.Combine(gen, "anchorGroups.ini"), Path.Combine(gen, "selectorGroups.ini"), settingsFile, managerFile, selectorFile }.Select(f => File.Exists(f) ? File.ReadAllBytes(f) : null).ToArray();
        if (mv.OptionRows == ctx.Options.Count && mv.SectionCount == 4 && mv.OptionRows >= 20 && mv.GroupRows == 0) Ok($"menu page: {mv.OptionRows} options in {mv.SectionCount} sections are listed");
        else { Fail($"menu page: {mv.OptionRows} option rows (mod has {ctx.Options.Count}), {mv.SectionCount} sections, {mv.GroupRows} group rows"); return; }
        bool locked = mv.SelectOptionForTest("fairAI") && !mv.ToggleEnabled && !mv.DeleteEnabled && mv.SelectOptionForTest("extraHuman") && !mv.ToggleEnabled && !mv.DeleteEnabled;
        mv.SelectOptionForTest("richStart");
        bool canHide = mv.ToggleEnabled && mv.ToggleButtonText == "Hide" && mv.DeleteEnabled;
        mv.ToggleForTest();
        bool hid = mv.StateOf("richStart") == "Hidden" && mv.ToggleButtonText == "Show" && mv.Dirty;
        mv.SelectOptionForTest("noHero");
        mv.DeleteForTest();
        bool del = mv.StateOf("noHero") == "Deleted" && !mv.DeleteEnabled && mv.CanApply;
        // E5b: text and numbers (base income is shared by two options; Fair AI is a switch: text only)
        mv.SelectOptionForTest("baseIncome");
        bool nums = mv.NumbersVisible && mv.TextVisible && mv.NumberIds.Count == 2 && mv.NumberValue("income.credits") == 250 && mv.NumberValue("income.rust") == 100;
        mv.SetNumberForTest("income.credits", 400);
        mv.TypeNameForTest("Steady income");
        mv.TypeDescriptionForTest("- Everyone earns\r\n- all the time");
        mv.SelectOptionForTest("richStart");
        mv.SetNumberForTest("richStart.credits", 150);
        mv.SelectOptionForTest("fairAI");
        bool sw = !mv.NumbersVisible && mv.TextVisible;
        mv.SelectOptionForTest("baseIncome");
        bool kept = mv.NumberValue("income.credits") == 400 && mv.NameBoxText == "Steady income" && mv.NameOf("baseIncome") == "Steady income" && mv.Dirty && mv.CanApply;
        if (nums && sw && kept) Ok("menu page: base income shows its two numbers and text, Fair AI only its text; typed values are kept when another option is picked");
        else Fail($"menu numbers/text: numbers {nums}, switch {sw}, kept {kept}, status {mv.StatusText}");
        if (locked && canHide && hid && del) Ok("menu page: fairAI and extraHuman cannot be hidden; an option of section 1 is hidden and one of section 3 deleted, Apply is on");
        else Fail($"menu page: locked {locked}, can hide {canHide}, hidden {hid}, deleted {del}, status {mv.StatusText}");
        bool applied = await mv.ApplyForTest();
        var s = form.Session!;
        string sel = File.ReadAllText(Path.Combine(root, "editorGenerated", "selectorGroups.ini"));
        var rules = RwEditor.Core.Validation.IncludeRules.Check(RwEditor.Core.Mod.ModSnapshot.Load(root));
        string managerNow = File.ReadAllText(managerFile), selectorNow = File.ReadAllText(selectorFile);
        bool tuned = managerNow.Contains("credits=400, rust=100", StringComparison.Ordinal) && managerNow.Contains("credits=memory.startingRes * 150", StringComparison.Ordinal)
            && selectorNow.Contains("@define actionName: Steady income", StringComparison.Ordinal) && selectorNow.Contains("acquire 150 times", StringComparison.Ordinal)
            && selectorNow.Contains("description:- Everyone earns\\n- all the time", StringComparison.Ordinal) && s.Settings.OptionEdits.Count >= 5
            && ctx.Support.VerifyLockedFiles(root).Ok && form.MenuPage.NameOf("baseIncome") == "Steady income";
        if (applied && tuned && sel.Contains("temporarilyAddTags: xhrichStart, xhnoHero", StringComparison.Ordinal) && s.Settings.GetOptionState("richStart") == RwEditor.Core.Settings.OptionState.Hidden
            && s.Settings.GetOptionState("noHero") == RwEditor.Core.Settings.OptionState.Deleted && rules.AllPass && !mv.Dirty && mv.StateOf("noHero") == "Deleted" && mv.StateOf("richStart") == "Hidden")
            Ok("menu page: Apply wrote the hide tags (xhrichStart, xhnoHero) and the settings, the include rules pass, the page shows the applied state after the re-read");
        else Fail($"menu apply: ok {applied}, files tuned {tuned}, hide line {sel.Contains("xhrichStart, xhnoHero", StringComparison.Ordinal)}, rules {rules.AllPass}, dirty {mv.Dirty}; {string.Join(" / ", AppHost.UnattendedMessages.TakeLast(2))}");
        var stuck = new List<string>();
        void Walk(Control c) { if (c.UseWaitCursor) stuck.Add(c.GetType().Name + ":" + c.Name); foreach (Control k in c.Controls) Walk(k); }
        Walk(form); Walk(mv);
        if (!BusyCursor.Active && stuck.Count == 0) Ok("menu page: no control keeps the busy cursor after the save");
        else Fail($"busy cursor stuck after the menu apply: active {BusyCursor.Active}, {string.Join(", ", stuck.Take(8))}");
        mv.RevertAllForTest();
        bool back = mv.Dirty && mv.StateOf("noHero") == "Shown" && mv.StateOf("richStart") == "Shown" && await mv.ApplyForTest();
        s = form.Session!;
        sel = File.ReadAllText(Path.Combine(root, "editorGenerated", "selectorGroups.ini"));
        if (back && !sel.Contains("xh", StringComparison.Ordinal) && s.Settings.StartOptions.Count == 0 && !mv.Dirty)
            Ok("menu page: put every option back + Apply returns the generated selector file and the settings to the shipped state");
        else Fail($"menu revert: ok {back}, selector still has a hide tag {sel.Contains("xh", StringComparison.Ordinal)}, options left {s.Settings.StartOptions.Count}");
        // leave the scratch copy as it was: History > Undo twice (the revert, then the hide / delete)
        bool u1 = await form.Editor.UndoLastAsync();
        bool u2 = await form.Editor.UndoLastAsync();
        var after = new[] { Path.Combine(gen, "anchorGroups.ini"), Path.Combine(gen, "selectorGroups.ini"), settingsFile, managerFile, selectorFile }.Select(f => File.Exists(f) ? File.ReadAllBytes(f) : null).ToArray();
        bool same = before.Zip(after).All(p => p.First == null ? p.Second == null : p.Second != null && p.First.AsSpan().SequenceEqual(p.Second));
        if (u1 && u2 && same && form.Session!.Settings.StartOptions.Count == 0 && !mv.Dirty) Ok("menu page: History > Undo twice puts the generated files, the protected option files and the settings back byte for byte");
        else Fail($"menu undo: {u1}/{u2}, files identical {same}, options {form.Session!.Settings.StartOptions.Count}");
        form.SelectNav("area:");
    }

    /// <summary>A stand-in game for the self-test: prints the given lines, then waits to be closed (so the Test load flow runs for real without starting Java).</summary>
    sealed class SelfTestGame(IReadOnlyList<string> lines, Action<string> onLine) : RwEditor.Core.GameLog.IRunningGame
    {
        volatile bool _exited;
        public bool HasExited => _exited;
        public void CloseGracefully() { _exited = true; }
        public void Kill() { _exited = true; }
        public void Begin() => Task.Run(() => { foreach (var l in lines) { onLine(l); Thread.Sleep(2); } });
    }

    sealed class SelfTestLauncher(IReadOnlyList<string> lines) : RwEditor.Core.GameLog.IGameLauncher
    {
        public RwEditor.Core.GameLog.IRunningGame Start(string gameFolder, string heap, Action<string> onLine)
        {
            var g = new SelfTestGame(lines, onLine);
            g.Begin();
            return g;
        }
    }

    /// <summary>
    /// E6: the Logs page on the scratch copy: a real recorded load error (the incomeMeOff id) is read and pinpointed; a Test load runs through the real flow
    /// with a stand-in game (clean, then with an error), the result is remembered, the busy cursor is gone, and a crash report of the editor can be read in the tab.
    /// </summary>
    static async Task LogChecksAsync(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var s = form.Session!;
        var page = form.LogsPage.Game;
        form.SelectNav("logs");
        Application.DoEvents();
        string recorded = "2026-10-08 17:10:30.001: Loading mod at:/SD/mods/units/MajorExperimentals_DepacheEdit (name:[Depache Edit] MajorExperimentals V271.7)\n"
            + "2026-10-08 17:10:43.322: Adding error for mod: [Depache Edit] MajorExper: Error loading unit: startingOptions/gameOptionSelector.ini (in mod [Depache Edit] MajorExperimentals V271.7): \n"
            + "[action_incomeMeOff]id cannot be longer than 15 characters\n"
            + "2026-10-08 17:10:43.323: Disabling mod due to error: [Depache Edit] MajorExper path:D:\\x\\MajorExperimentals_DepacheEdit\n";
        page.AnalyzeTextForTest("recorded-incomeMeOff.log", recorded);
        bool pinned = page.FindingCount >= 1 && page.SummaryText.Contains("1 problem", StringComparison.Ordinal)
            && page.DetailText.Contains("An id is too long in [action_incomeMeOff]", StringComparison.Ordinal) && page.DetailText.Contains("startingOptions/gameOptionSelector.ini", StringComparison.Ordinal)
            && page.DetailText.Contains("What to do", StringComparison.Ordinal);
        if (pinned) Ok("logs page: a recorded game error is read and pinpointed (file, section, key) with what to do");
        else Fail($"logs page: findings {page.FindingCount}, summary '{page.SummaryText}', detail '{page.DetailText}'");

        // a stand-in game folder (the real checks of the runner: java.exe, game-lib.jar) outside the real game
        string fake = Path.Combine(Path.GetTempPath(), "rwe-selftest-game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fake, "jvm64", "bin"));
        File.WriteAllText(Path.Combine(fake, "jvm64", "bin", "java.exe"), "x");
        File.WriteAllText(Path.Combine(fake, "game-lib.jar"), "x");
        string copyId = s.Marker!.CopyId;
        string recordFile = Path.Combine(RwEditor.Core.GameLog.TestLoadStore.DefaultFolder, copyId + ".json");
        var savedLogs = new List<string>();
        try
        {
            var clean = new[]
            {
                "2026-10-12 21:07:27.156: Loading mod at:/SD/mods/units/" + Path.GetFileName(s.Root) + " (name:[Custom selftest] Depache's Mod Tribute)",
                "2026-10-12 21:07:42.982: Done loading custom units. image cacheHits:1 image cacheMisses:2 (in: 1234.5ms)",
                "2026-10-12 21:07:45.505: --Now loading:init complete",
            };
            bool ran = await page.TestLoadAsync(new SelfTestLauncher(clean), () => false, skipConfirm: true, gameFolderForTest: fake);
            bool cleanOk = ran && page.Report is { Clean: true, ReachedInit: true } && page.SummaryText.Contains("finished loading", StringComparison.Ordinal)
                && RwEditor.Core.GameLog.TestLoadStore.Load(copyId) is { Clean: true };
            if (cleanOk) Ok("logs page: Test load (stand-in game) finishes at 'init complete', is clean, and the result is remembered for the copy");
            else Fail($"test load clean: ran {ran}, report clean {page.Report?.Clean}, init {page.Report?.ReachedInit}, summary '{page.SummaryText}', stored {RwEditor.Core.GameLog.TestLoadStore.Load(copyId)}");

            var bad = new[] { clean[0], "2026-10-12 21:08:43.322: Adding error for mod: [Custom selftest]: Error loading unit: startingOptions/gameOptionSelector.ini (in mod [Custom selftest] Depache's Mod Tribute): ",
                "[action_x]id cannot be longer than 15 characters", "2026-10-12 21:08:43.323: Disabling mod due to error: [Custom selftest] path:/SD/mods/units/" + Path.GetFileName(s.Root) };
            bool ran2 = await page.TestLoadAsync(new SelfTestLauncher(bad), () => false, skipConfirm: true, gameFolderForTest: fake);
            bool badOk = ran2 && page.Report!.Problems.Any() && page.SummaryText.Contains("problem", StringComparison.Ordinal) && RwEditor.Core.GameLog.TestLoadStore.Load(copyId) is { Clean: false, Problems: >= 1 };
            if (badOk) Ok("logs page: Test load with a load error reports the problem and remembers it as not clean");
            else Fail($"test load error: ran {ran2}, problems {page.Report?.Problems.Count()}, summary '{page.SummaryText}'");

            bool refused = !await page.TestLoadAsync(new SelfTestLauncher(clean), () => true, skipConfirm: true, gameFolderForTest: fake);
            if (refused) Ok("logs page: a running game is never touched (the test is refused with a plain message)");
            else Fail("test load with a running game was not refused");
        }
        finally
        {
            try { Directory.Delete(fake, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { if (File.Exists(recordFile)) File.Delete(recordFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try
            {
                string logs = Path.Combine(RwEditor.Core.GameLog.TestLoadStore.DefaultFolder, "logs");
                if (Directory.Exists(logs)) foreach (var f in Directory.GetFiles(logs, "testload-*.log").Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddMinutes(-10))) File.Delete(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var stuck = new List<string>();
        void Walk(Control c) { if (c.UseWaitCursor) stuck.Add(c.GetType().Name); foreach (Control k in c.Controls) Walk(k); }
        Walk(form);
        if (!BusyCursor.Active && stuck.Count == 0 && !form.Editor.Busy && page.TestButtonEnabled) Ok("logs page: after the tests the editor is free again and no control keeps the busy cursor");
        else Fail($"after the tests: busy cursor {BusyCursor.Active}, stuck {string.Join(",", stuck.Take(5))}, editor busy {form.Editor.Busy}, test button {page.TestButtonEnabled}");
        form.SelectNav("area:");
    }

    /// <summary>
    /// The real "Put values back" dialog on the applied scratch copy, in a real message loop: every choice is picked and its preview awaited, "Put back" is
    /// clicked, and a heartbeat timer measures the longest time the window stopped answering (a freeze). Fails over 750 ms. Also reports memory after reloads.
    /// </summary>
    static async Task RevertDialogChecksAsync(MainForm form, Action<string> Ok, Action<string> Fail, Action<string> Note)
    {
        var s = form.Session!;
        if (!form.Browser.SelectUnit("heavyAATank")) { Fail("revert dialog test: heavyAATank not found"); return; }
        var row = form.Browser.Details.SelectStat("core.maxHp", "core");
        var u = s.Model.Find("heavyAATank");
        if (row == null || u == null) { Fail("revert dialog test: no Health row"); return; }
        long last = Stopwatch.GetTimestamp(), worst = 0;
        using var beat = new System.Windows.Forms.Timer { Interval = 15 };
        beat.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); worst = Math.Max(worst, now - last); last = now; };
        beat.Start();
        try
        {
            using var d = new RevertDialog(s, u, row) { Opacity = 0, ShowInTaskbar = false };
            d.Show(form);
            foreach (var scope in new[] { RevertScope.Stat, RevertScope.Unit, RevertScope.Area, RevertScope.All })
            {
                d.Choose(scope);
                await d.PreviewDone.WaitAsync(TimeSpan.FromSeconds(60));
                Note($"revert dialog, {scope}: put-back enabled {d.PutBackButton.Enabled}; preview: {Short(d.PreviewText.Replace("\r\n", " | "))}");
            }
            d.Choose(RevertScope.Stat);
            await d.PreviewDone.WaitAsync(TimeSpan.FromSeconds(60));
            if (!d.PutBackButton.Enabled || d.Request == null) { Fail("revert dialog: 'Put back' is not enabled for an applied change"); return; }
            Note($"revert check: longest gap after the dialog steps {worst * 1000.0 / Stopwatch.Frequency:0} ms");
            d.Close();
            form.SelectNav("history");   // the owner reverts from History and undo: the reload then returns to that page
            await Task.Delay(300);
            Note($"revert check: longest gap before RevertAsync {worst * 1000.0 / Stopwatch.Frequency:0} ms");
            bool done = await form.Editor.RevertAsync(u, row, RevertScope.Stat);
            await Task.Delay(1500);
            Note($"after the revert from the History page: heartbeat still beating (longest gap so far {worst * 1000.0 / Stopwatch.Frequency:0} ms), history rows {form.History.List.Items.Count}");
            double ms = worst * 1000.0 / Stopwatch.Frequency;
            if (done && ms < 750) Ok($"revert dialog + revert + reload: the window never stopped answering for more than {ms:0} ms");
            else Fail($"revert dialog: done {done}, longest stall {ms:0} ms");
            form.Refresh();
            long m0 = GC.GetTotalMemory(true) / (1024 * 1024);
            for (int i = 0; i < 3; i++) await form.ReloadAsync("heavyAATank");
            long m1 = GC.GetTotalMemory(true) / (1024 * 1024);
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            long p1 = me.PrivateMemorySize64 / (1024 * 1024);
            for (int i = 0; i < 5; i++) await form.ReloadAsync("heavyAATank");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            me.Refresh();
            long p2 = me.PrivateMemorySize64 / (1024 * 1024);
            Note($"private memory: {p1} MB -> {p2} MB after 5 more reloads; GDI objects {GdiCount(me)}");
            if (m1 - m0 < 150 && p2 - p1 < 600) Ok($"memory after reloads: managed {m0} -> {m1} MB, process {p1} -> {p2} MB (no growth per reload)"); else Fail($"memory grows with every reload: managed {m0} -> {m1} MB, process {p1} -> {p2} MB");
        }
        catch (Exception e) { Fail("revert dialog test crashed: " + e); }
        finally { beat.Stop(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr hProcess, uint flags);
    static uint GdiCount(Process p) => GetGuiResources(p.Handle, 0);

    static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'),
            f => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    static List<string> Diff(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

    static List<string> LineDiff(string a, string b)
    {
        var la = File.ReadAllLines(a); var lb = File.ReadAllLines(b);
        var d = new List<string>();
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            string x = i < la.Length ? la[i] : "(none)", y = i < lb.Length ? lb[i] : "(none)";
            if (x != y) d.Add($"line {i + 1}: '{x}' -> '{y}'");
        }
        return d;
    }

    static string Short(string? s) => s == null ? "-" : s.Length > 80 ? s[..80] + "..." : s;

    static int Finish(List<string> report, string? reportPath, int failures)
    {
        report.Add(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
        string text = string.Join(Environment.NewLine, report) + Environment.NewLine;
        try
        {
            string path = reportPath ?? Path.Combine(AppHost.Log.Folder, $"selftest-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { AppHost.Log.Warn("selftest report not written: " + e.Message); }
        try { Console.Out.Write(text); } catch (IOException) { }
        return failures == 0 ? 0 : 1;
    }
}
