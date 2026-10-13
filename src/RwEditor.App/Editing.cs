using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Browse;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Setup;
using RwEditor.Core.Stats;

namespace RwEditor.App;

/// <summary>
/// The editing flows of the app (E3), all through Core: stage a change (quick check while typing, then the full check, the "this value is shared" choice,
/// the family choice), remove pending changes, apply them in one go, undo the last change, revert (stat / unit / mod area / everything), reset one value.
/// Every flow runs inside the crash-safe wrappers of its caller, does its slow work on a background thread, never writes outside Core's pipelines, and
/// tells the user in plain words what happened. One flow at a time (<see cref="Busy"/>).
/// </summary>
sealed class EditController
{
    readonly MainForm _form;
    readonly Dictionary<string, string> _checks = new(StringComparer.Ordinal);
    int _checkVersion;

    public EditController(MainForm form) { _form = form; }

    ModSession? S => _form.Session;
    public bool Busy { get; private set; }
    /// <summary>Raised after the pending list or its checks changed (no files changed).</summary>
    public event Action? Changed;
    /// <summary>The last finished check of the pending list (null while none ran).</summary>
    public Task? ChecksRunning { get; private set; }

    public static string Key(PendingStatEdit p) => $"{p.UnitFile}|{p.Unit}|{p.StatId}|{p.Section}".ToLowerInvariant();

    /// <summary>The check result of a pending change in plain words ("OK", a warning, or the problem), or null when not checked yet.</summary>
    public string? CheckText(PendingStatEdit p) => _checks.TryGetValue(Key(p), out var t) ? t : null;

    public void ResetChecks() { _checks.Clear(); _checkVersion++; }

    async Task<T> Run<T>(string status, Func<T> work)
    {
        _form.SetStatus(status);
        using (BusyCursor.Begin(_form)) return await Task.Run(work);
    }

    bool Begin(string what)
    {
        if (S == null) return false;
        if (Busy) { Ask.Info(_form, "Please wait", "The editor is still busy with the last step. Try again in a moment."); return false; }
        if (!S.CanEdit) { Ask.Info(_form, "Changes are switched off here", S.EditBlockedReason ?? "This folder cannot be changed."); return false; }
        Busy = true;
        AppHost.Log.Info("start: " + what);
        return true;
    }

    void End() { Busy = false; }

    /// <summary>Runs a long job that must not overlap with a write (the Test load starts the game on the copy's files): the editor counts as busy meanwhile.</summary>
    public async Task<bool> TryRunExclusiveAsync(string what, Func<Task> work)
    {
        if (Busy) { Ask.Info(_form, "Please wait", "The editor is still busy with the last step. Try again in a moment."); return false; }
        Busy = true;
        AppHost.Log.Info("start: " + what);
        try { await work(); return true; }
        finally { Busy = false; }
    }

    /// <summary>
    /// Ends a write flow: the editor is free again, the copy is re-read FIRST (status says so; no dialog is open over a stale page), and only then the
    /// "done" message is shown. The message is shown even when the re-read fails.
    /// </summary>
    async Task FinishAsync(bool reload, string? selectUnit, Action? done)
    {
        End();
        try
        {
            if (reload) { _form.SetStatus("Saved. Re-reading your copy..."); await _form.ReloadAsync(selectUnit); }
        }
        finally { done?.Invoke(); }
    }

    // ------------------------------------------------------------------------------------------------------------ stage
    /// <summary>
    /// Stages a new value typed by the user for one stat of one unit. Returns true when it was staged. Nothing in the mod changes; the pending list in the
    /// copy's settings file is updated (one write).
    /// </summary>
    public async Task<bool> StageAsync(UnitType u, StatRow row, string typed, bool family)
    {
        if (!Begin($"staging {u.Name} {row.Entry.Id} [{row.Section}] = '{typed}'")) return false;
        try
        {
            var s = S!;
            var e = row.Entry;
            if (!row.Editable) { Ask.Info(_form, "This value cannot be changed", row.Note.Length > 0 ? row.Note : "It is locked to keep the game stable."); return false; }
            string? problem = StatText.QuickCheck(e, row.Ref?.Text, typed, out var value);
            if (problem != null) { Ask.Info(_form, "Please check the value", problem, problem: true); return false; }
            var info = StatStaging.Info(s.Model, s.Dict, s.Families, u, e, row.Section);
            var change = new PendingStatEdit { Unit = u.Name, UnitFile = u.RelPath, StatId = e.Id, Section = row.Section, Mode = "unit", Value = value! };
            var names = UnitRowText.For(s, u);
            StageCheck chosen;
            if (info.Shared)
            {
                bool templateWritable = info.Origin != null && LockPolicy.IsStatWritableFile(info.Origin.File, s.Model.LockGlobs);
                var (only, all) = await Run("Checking the change...", () =>
                {
                    var o = StatStaging.Check(s.Model, s.Dict, change, s.ReadDisk);
                    var t = new PendingStatEdit { Unit = change.Unit, UnitFile = change.UnitFile, StatId = change.StatId, Section = change.Section, Mode = "template", Value = change.Value };
                    return (o, templateWritable ? StatStaging.Check(s.Model, s.Dict, t, s.ReadDisk) : null);
                });
                if (!only.Ok && all?.Ok != true)
                {
                    Ask.Info(_form, "This change is not possible", "Neither choice works for this value:", only.Problems.Concat(all?.Problems ?? new[] { "the shared line is in a protected file" }), problem: true);
                    return false;
                }
                bool? pick = ShareChoiceDialog.Choose(_form, names, info, only, all);
                if (pick == null) { _form.SetStatus("Not changed (cancelled)."); return false; }
                if (pick == true)
                {
                    var units = all!.Items[0].Plan!.Affected.Select(a => names.Describe(a.Unit)).ToList();
                    if (!Ask.Confirm(_form, "Change every unit that shares it?",
                            $"\"{StatText.StatName(e, row.Section)}\" will become {StatText.Value(e, all.Items[0].Plan!.NewNumber)} for ALL {units.Count} units below:", units,
                            $"Yes, change all {units.Count}", "No"))
                    { _form.SetStatus("Not changed (cancelled)."); return false; }
                    chosen = all;
                }
                else chosen = only;
            }
            else chosen = await Run("Checking the change...", () => StatStaging.Check(s.Model, s.Dict, change, s.ReadDisk));

            if (family && chosen.Ok && chosen.Pendings[0].Mode == "unit" && info.FamilyCandidates.Count > 0)
            {
                var fam = await Run("Checking the upgrade levels / versions...", () => StatStaging.Check(s.Model, s.Dict, change, s.ReadDisk, info.FamilyCandidates));
                if (fam.Ok) chosen = fam;
                else if (!Ask.Confirm(_form, "Not every version can take this value",
                             "The change cannot be copied to every upgrade level / version:", fam.Problems, "Change only this unit", "Cancel"))
                { _form.SetStatus("Not changed (cancelled)."); return false; }
            }
            if (!chosen.Ok)
            {
                Ask.Info(_form, "This change is not possible", "Nothing was staged:", chosen.Problems, problem: true);
                return false;
            }
            var saved = StatStaging.SaveStaged(s.Root, chosen.Pendings);
            s.ReloadSettings();
            foreach (var it in chosen.Items) _checks[Key(it.Pending)] = CheckWords(it);
            AppHost.Log.Info($"staged {chosen.Pendings.Count} change(s); {saved.PendingStatEdits.Count} pending");
            _form.SetStatus($"Staged: {StatText.StatName(e, row.Section)} of {names.Title} -> {StatText.Value(e, chosen.Items[0].Plan!.NewNumber)}" +
                            (chosen.Pendings.Count > 1 ? $" (and {chosen.Pendings.Count - 1} upgrade level / version)" : "") + ". Nothing is written until you click \"Apply changes\".");
            Changed?.Invoke();
            return true;
        }
        finally { End(); }
    }

    /// <summary>
    /// "Set for many units": the dialog (or, unattended, <see cref="AppHost.AutoBulk"/>) picks the value and the filter; the plan (Core <see cref="StatBulk"/>,
    /// every change checked like a normal edit) is staged as pending changes after one confirmation. Nothing in the mod changes.
    /// </summary>
    public async Task<bool> StageBulkAsync(StatRow row)
    {
        if (!Begin($"set for many units: {row.Entry.Id}")) return false;
        try
        {
            var s = S!;
            var e = row.Entry;
            if (e.FixedSection == null) { Ask.Info(_form, "Not for this value", "Setting a value for many units works for values of the main sections (core, movement, attack), not for turret, projectile or action values."); return false; }
            BulkPlan? plan;
            if (AppHost.Unattended)
            {
                var auto = AppHost.AutoBulk?.Invoke(row);
                if (auto == null) return false;
                var (value, filter) = auto.Value;
                plan = await Run("Checking the change for many units...", () => StatBulk.Plan(s.Model, s.Dict, s.Families, e, value, filter, s.ReadDisk));
                AppHost.UnattendedMessages.Add($"BULK {e.Id} = {value}: {plan.Ok.Count} change(s), {plan.AffectedUnits.Count} unit(s) in all, {plan.Skipped.Count} skipped, problems {plan.BatchProblems.Count}");
            }
            else
            {
                using var d = new BulkDialog(s, e, null);
                if (d.ShowDialog(_form) != DialogResult.OK || d.Plan == null) { _form.SetStatus("Not staged (cancelled)."); return false; }
                plan = d.Plan;
            }
            if (!plan.CanStage) { Ask.Info(_form, "Nothing was staged", plan.BatchProblems.Count > 0 ? "The changes together would cause problems:" : "No unit can take this change.", plan.BatchProblems, problem: true); return false; }
            if (!Ask.Confirm(_form, "Stage these changes?",
                    $"\"{StatText.StatName(e, plan.Section)}\" = {plan.Value} {StatText.InputUnit(e)} for {plan.Ok.Count} unit(s) / building(s)" +
                    (plan.AffectedUnits.Count > plan.Ok.Count ? $" ({plan.AffectedUnits.Count} units change in all, with their upgrade levels and versions)" : "") +
                    ". They go into the pending list; nothing is written until you click \"Apply changes\".",
                    plan.Ok.Take(30).Select(p => UnitRowText.For(s, s.Model.UnitsInFile(p.UnitFile).First(u => u.Name == p.Unit)).Title + $" ({p.Unit})").Concat(plan.Ok.Count > 30 ? new[] { $"... and {plan.Ok.Count - 30} more" } : Array.Empty<string>()),
                    $"Stage {plan.Ok.Count} change(s)", "Cancel"))
            { _form.SetStatus("Not staged (cancelled)."); return false; }
            StatStaging.SaveStaged(s.Root, plan.Ok);
            s.ReloadSettings();
            AppHost.Log.Info($"staged {plan.Ok.Count} bulk change(s) of {e.Id}; {s.Settings.PendingStatEdits.Count} pending");
            _form.SetStatus($"Staged {plan.Ok.Count} change(s): {StatText.StatName(e, plan.Section)} = {plan.Value}. Nothing is written until you click \"Apply changes\".");
            Changed?.Invoke();
            _ = AppHost.SafeRunAsync(_form, "checking the pending changes", RefreshChecksAsync);
            return true;
        }
        finally { End(); }
    }

    /// <summary>
    /// "Apply to my copy" of the start-unit menu (E4): plans through Core <see cref="GroupsPipeline"/> (guards, validation, include rules, lint; nothing written),
    /// shows what will happen, then backs up and writes the generated files and the copy's settings in one go. Undo / History work as for every change.
    /// </summary>
    public async Task<bool> ApplyGroupsAsync(EditorSettings desired, Func<string, string>? optionName = null)
    {
        if (!Begin("applying the start-unit groups")) return false;
        var s = S!;
        bool reload = false;
        Action? done = null;
        try
        {
            ApplyPlan plan;
            try { plan = await Run("Checking the groups...", () => GroupsPipeline.Plan(s.Root, desired, AppHost.WriteOptions())); }
            catch (RwEditor.Core.Generation.SettingsInvalidException e) { Ask.Info(_form, "Nothing was changed", "These problems must be fixed first:", e.Findings.Where(f => f.Severity == Severity.Error).Select(f => StatText.Plain(f.Message)), problem: true); return false; }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", StatText.Plain(e.Message), problem: true); return false; }
            var support = EditorSupport.Load(s.Root);
            var merged = desired.Clone();
            string summary = GroupsPipeline.Summary(merged, support);
            var lines = new List<string>();
            foreach (var g in merged.SpawnGroups.OrderBy(g => g.Slot))
                lines.Add($"Group \"{g.Name}\": {string.Join(", ", g.Units.Select(u => (u.Count > 1 ? u.Count + "x " : "") + TitleOf(s, u.Unit)))}" + (g.Enabled ? "" : " (switched off)") + (g.Mode == GroupMode.HumansOnly ? " - human players only" : "") + (g.DefaultOn ? " - on by default" : ""));
            foreach (var (tag, st) in merged.StartOptions) lines.Add($"Menu option \"{(optionName?.Invoke(tag) ?? tag)}\": {(st == OptionState.Deleted ? "deleted" : "hidden")}");
            foreach (var (id, v) in merged.Tuning.Values) lines.Add($"Number \"{RwEditor.Core.Options.OptionCatalog.Embedded.Find(id)?.Label ?? id}\": {v}");
            foreach (var (tag, t) in merged.Tuning.Texts)
                lines.Add($"Text of \"{(optionName?.Invoke(tag) ?? tag)}\": " + string.Join(" and ", new[] { t.Name != null ? $"name \"{t.Name}\"" : null, t.Description != null ? "description" : null }.Where(x => x != null)));
            if (!merged.Tuning.IsEmpty) lines.Add("Only the lines named above are changed in the protected option files; History > Undo puts them back exactly.");
            if (lines.Count == 0) lines.Add("No groups, every menu option shown and nothing changed (the shipped menu).");
            foreach (var w in plan.Warnings) lines.Add("Note: " + StatText.Plain(w.Message));
            if (!Ask.Confirm(_form, "Apply to my copy", "This is written into your copy's start-unit menu. A backup is made first; History > Undo puts everything back.", lines, "Apply", "Cancel"))
            { _form.SetStatus("Not applied (cancelled)."); return false; }
            GroupsApplyResult r;
            try { r = await Run("Writing the groups...", () => GroupsPipeline.Apply(s.Root, desired, AppHost.WriteOptions())); }
            catch (RwEditor.Core.Generation.SettingsInvalidException e) { Ask.Info(_form, "Nothing was changed", "These problems must be fixed first:", e.Findings.Where(f => f.Severity == Severity.Error).Select(f => StatText.Plain(f.Message)), problem: true); return false; }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", StatText.Plain(e.Message), problem: true); return false; }
            catch (ApplyFailedException e) { Ask.Info(_form, "The groups could not be written", e.Message, problem: true); reload = true; return false; }
            reload = true;
            AppHost.Log.Info($"applied start-unit groups: {r.Summary}; files {string.Join(", ", r.WrittenFiles)}; backup {r.BackupPath}");
            string doneText = r.Written ? "Done: the start-unit menu of your copy was updated. A backup was made (History > Undo puts it back)." : "Nothing needed to change: your copy already holds exactly this.";
            var doneLines = new[] { r.Summary }.Concat(GameReminder(s)).ToList();
            done = () => Ask.Info(_form, "Applied", doneText, doneLines);
            return true;
        }
        finally { await FinishAsync(reload, null, done); }
    }

    static string TitleOf(ModSession s, string unit) { var u = s.Model.UnitsNamed(unit).FirstOrDefault(); return u == null ? unit : s.RowOf(u).Title; }

    static string CheckWords(StatBatchItem it) =>
        !it.Ok ? "Problem: " + string.Join(" ", it.Errors.Select(StatText.Plain))
        : it.Warnings.Any(w => w.Code != "EXPRESSION_DEPENDS") ? "OK, but: " + string.Join(" ", StatText.PlainWarnings(it.Warnings))
        : it.Warnings.Any() ? "OK. Note: " + string.Join(" ", StatText.PlainWarnings(it.Warnings))
        : "OK";

    /// <summary>Removes pending changes (no file of the mod changes).</summary>
    public bool RemovePending(Func<PendingStatEdit, bool> which, string what)
    {
        if (!Begin("removing pending changes: " + what)) return false;
        try
        {
            StatStaging.SaveStaged(S!.Root, null, which);
            S.ReloadSettings();
            _form.SetStatus("Removed from the pending changes: " + what + ".");
            Changed?.Invoke();
            return true;
        }
        finally { End(); }
    }

    /// <summary>Checks the whole pending list again in the background (as "Apply changes" will) and fills the Check column.</summary>
    public async Task RefreshChecksAsync()
    {
        var s = S;
        if (s == null || !s.CanEdit || s.Settings.PendingStatEdits.Count == 0) return;
        int v = ++_checkVersion;
        var pend = s.Settings.PendingStatEdits.Select(p => p.Clone()).ToList();
        var task = Task.Run(() => StatBatch.Plan(s.Model, s.Dict, pend, s.ReadDisk));
        ChecksRunning = task;
        StatBatchPlan plan;
        try { plan = await task; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EditorRefusalException) { AppHost.Log.Warn("pending check failed: " + e.Message); return; }
        if (v != _checkVersion || !ReferenceEquals(S, s)) return;
        foreach (var it in plan.Items) _checks[Key(it.Pending)] = CheckWords(it);
        foreach (var f in plan.BatchFindings.Where(f => f.Severity == Severity.Error))
            foreach (var it in plan.Items) _checks[Key(it.Pending)] = (_checks.TryGetValue(Key(it.Pending), out var t) && t != "OK" ? t + " " : "") + "Together: " + StatText.Plain(f);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------------------------------------------------ apply
    public async Task<bool> ApplyAsync()
    {
        if (!Begin("applying the pending changes")) return false;
        var s = S!;
        string? selectUnit = _form.Browser.SelectedRow?.Row?.Unit.Name;
        bool reload = false;
        Action? done = null;
        try
        {
            if (s.Settings.PendingStatEdits.Count == 0) { Ask.Info(_form, "Nothing to apply", "There are no pending changes. Type a new value in the \"New value\" column first."); return false; }
            StatBatchPlan plan;
            try { plan = await Run("Checking every pending change together...", () => StatBatch.PlanCopy(s.Root, AppHost.WriteOptions())); }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", e.Message, problem: true); return false; }
            foreach (var it in plan.Items) _checks[Key(it.Pending)] = CheckWords(it);
            Changed?.Invoke();
            if (!plan.CanApply)
            {
                Ask.Info(_form, "Nothing was changed", "These pending changes cannot be applied. Remove or correct them, then try again:", plan.Problems, problem: true);
                return false;
            }
            var names = (Func<UnitType, string>)(x => UnitRowText.For(s, x).Describe(x));
            var lines = plan.Items.Select(i => PlanLine(i.Plan!, names)).ToList();
            var warn = plan.Items.SelectMany(i => StatText.PlainWarnings(i.Warnings)).Concat(plan.BatchFindings.Where(f => f.Severity == Severity.Warning).Select(StatText.Plain))
                .Distinct().ToList();
            if (!Ask.Confirm(_form, "Apply changes", $"These {lines.Count} change(s) will be written into your copy \"{s.Marker?.Name}\". A backup is made first; History > Undo puts everything back.",
                    lines.Concat(warn.Select(w => "Note: " + w)), "Apply changes", "Cancel",
                    warn.Count > 0 ? "Notes do not stop the change: every value is inside the allowed range. Check unusual values in a match." : null))
            { _form.SetStatus("Not applied (cancelled)."); return false; }

            StatBatchResult r;
            try { r = await Run("Writing the changes...", () => StatBatch.Apply(s.Root, AppHost.WriteOptions())); }
            catch (StatBatchRefusedException e) { Ask.Info(_form, "Nothing was changed", "Something changed since the check:", e.Plan.Problems, problem: true); return false; }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", e.Message, problem: true); return false; }
            catch (ApplyFailedException e) { Ask.Info(_form, "The changes could not be written", e.Message, problem: true); reload = true; return false; }
            reload = true;
            AppHost.Log.Info($"applied {r.Plan.Items.Count} change(s), files {string.Join(", ", r.WrittenFiles)}, backup {r.BackupPath}");
            string doneText = $"Done: {r.Plan.Items.Count} value(s) changed in your copy. A backup was made (History > Undo puts them back).";
            var doneLines = lines.Concat(GameReminder(s)).ToList();
            done = () => Ask.Info(_form, "Changes applied", doneText, doneLines);
            return true;
        }
        finally { await FinishAsync(reload, selectUnit, done); }
    }

    static string PlanLine(StatEditPlan p, Func<UnitType, string> names)
    {
        string old = p.Before.IsSet && p.Before.Number is double b ? StatText.Value(p.Entry, b) : "not set (game default)";
        string how = p.Request.Mode == StatEditMode.Template ? $" - shared line, {p.Affected.Count} units: {string.Join(", ", p.Affected.Take(12).Select(a => a.Unit.Name))}{(p.Affected.Count > 12 ? ", ..." : "")}" : "";
        return $"{names(p.Unit)}: {StatText.StatName(p.Entry, p.Section)} {old} -> {StatText.Value(p.Entry, p.NewNumber)}{how}";
    }

    /// <summary>What to do in the game to see the change (plain words).</summary>
    public static IEnumerable<string> GameReminder(ModSession s)
    {
        var link = GameLink.Check(s.Root, s.GameDir);
        if (!link.Linked)
        {
            foreach (var p in link.Problems) yield return "NOT LINKED TO THE GAME: " + p;
        }
        else
        {
            yield return "To see it in the game: if the game is running, close it and start it again (the game reads the mod files only when it starts).";
            yield return $"In the game's Mods menu, your copy \"{s.Title}\" must be ON and every other \"{GameLink.BaseTitle}\" (the Workshop one and any other local copy) must be OFF (two on = every unit twice).";
            foreach (var o in link.OtherCopiesOfTheMod) yield return "Other copy in the mods folder to switch off: " + o;
        }
        yield return "Edited copies are for single player and skirmish against the AI only (see the red bar).";
    }

    // ------------------------------------------------------------------------------------------------------------ undo / revert / reset
    public async Task<bool> UndoLastAsync()
    {
        if (!Begin("undo the last change")) return false;
        var s = S!;
        string? selectUnit = _form.Browser.SelectedRow?.Row?.Unit.Name;
        bool reload = false;
        Action? done = null;
        try
        {
            var next = await Run("Reading the history...", () => History.NextUndo(AppHost.WriteOptions().EffectiveBackupRoot, s.Root));
            if (next == null) { Ask.Info(_form, "Nothing to undo", "There is no change of this copy left to undo."); return false; }
            if (!Ask.Confirm(_form, "Undo the last change?", $"This puts the files back as they were before this change ({next.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, {next.KindText}):",
                    next.Summary.Split("; ").Select(x => x.Trim()), "Undo it", "Cancel", "Pending (not yet applied) changes stay in the list. The undo can itself be undone from the backup it makes."))
            { _form.SetStatus("Not undone (cancelled)."); return false; }
            try
            {
                var u = await Run("Undoing...", () => ApplyPipeline.Undo(s.Root, AppHost.WriteOptions(), next.Path, s.SettingsPath));
                reload = true;
                AppHost.Log.Info($"undo restored {u.RestoredFrom}: {string.Join(", ", u.RestoredFiles)}; safety backup {u.SafetyBackupPath}");
                string doneText = $"The change was undone ({u.RestoredFiles.Count} file(s) put back).";
                var doneLines = GameReminder(s).Take(1).ToList();
                done = () => Ask.Info(_form, "Undone", doneText, doneLines);
                return true;
            }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", e.Message, problem: true); return false; }
            catch (ApplyFailedException e) { Ask.Info(_form, "The undo could not be completed", e.Message, problem: true); reload = true; return false; }
        }
        finally { await FinishAsync(reload, selectUnit, done); }
    }

    /// <summary>"Revert...": the dialog shows exactly what each choice does; the chosen one is applied through Core (backup, one write, undoable).</summary>
    public async Task<bool> RevertAsync(UnitType? u, StatRow? row, RevertScope? preset = null)
    {
        if (!Begin("revert")) return false;
        var s = S!;
        string? selectUnit = _form.Browser.SelectedRow?.Row?.Unit.Name;
        bool reload = false;
        Action? done = null;
        try
        {
            RevertRequest? req;
            if (AppHost.Unattended)
            {
                req = preset switch
                {
                    RevertScope.Stat => new RevertRequest(RevertScope.Stat, u!.Name, u.RelPath, row!.Entry.Id, row.Section),
                    RevertScope.Unit => new RevertRequest(RevertScope.Unit, u!.Name, u.RelPath),
                    RevertScope.All => new RevertRequest(RevertScope.All),
                    _ => null,
                };
                AppHost.UnattendedMessages.Add($"REVERT {req}");
            }
            else
            {
                using var d = new RevertDialog(s, u, row);
                if (preset != null) d.Choose(preset.Value); else d.ChooseDefault();
                if (d.ShowDialog(_form) != DialogResult.OK || d.Request == null) { _form.SetStatus("Nothing put back (cancelled)."); return false; }
                req = d.Request;
                if (req.Scope == RevertScope.All && !Ask.Confirm(_form, "Put EVERYTHING back?",
                        "Every value of this copy goes back to what it was when the copy was made. Your applied changes are undone (a backup is made first).",
                        Array.Empty<string>(), "Yes, put everything back", "Cancel"))
                { _form.SetStatus("Nothing put back (cancelled)."); return false; }
            }
            if (req == null) return false;
            RevertResult? r;
            try { r = await Run("Putting values back...", () => StatRevert.Apply(s.Root, req, AppHost.WriteOptions())); }
            catch (EditorRefusalException e) { Ask.Info(_form, "Nothing was changed", e.Message, problem: true); return false; }
            catch (ApplyFailedException e) { Ask.Info(_form, "The values could not be put back", e.Message, problem: true); reload = true; return false; }
            if (r == null) { Ask.Info(_form, "Nothing to put back", "There is no applied change for this choice."); return false; }
            reload = true;
            string doneText = $"Done ({r.WrittenFiles.Count} file(s)). History > Undo can take this back too.";
            var doneLines = r.Plan.Lines.Take(40).Concat(GameReminder(s).Take(1)).ToList();
            done = () => Ask.Info(_form, "Values put back", doneText, doneLines);
            return true;
        }
        finally { await FinishAsync(reload, selectUnit, done); }
    }

    /// <summary>
    /// "Reset" on one value: a pending change of it is simply removed; an applied change recorded on it is put back (Revert, scope this value); a value
    /// that differs from the copy's original without a record (changed outside the editor, or by a shared change made from another unit) gets its
    /// original value staged as a normal change.
    /// </summary>
    public async Task<bool> ResetAsync(UnitType u, StatRow row)
    {
        var s = S;
        if (s == null) return false;
        if (!s.CanEdit) { Ask.Info(_form, "Changes are switched off here", s.EditBlockedReason ?? ""); return false; }
        var pending = s.PendingFor(u, row.Entry, row.Section);
        if (pending != null)
            return RemovePending(p => p.SameTarget(pending), $"{StatText.StatName(row.Entry, row.Section)} of {u.Name}");
        bool recorded = s.Settings.StatEdits.Any(e => e.Unit == u.Name && e.StatId.Equals(row.Entry.Id, StringComparison.OrdinalIgnoreCase) && e.Section.Equals(row.Section, StringComparison.OrdinalIgnoreCase))
                        || (row.Ref?.Raw?.Origin is Origin o && s.Settings.StatEdits.Any(e => e.Mode == "template" && e.File.Equals(o.File, StringComparison.OrdinalIgnoreCase)
                            && e.StatId.Equals(row.Entry.Id, StringComparison.OrdinalIgnoreCase)));
        if (recorded) return await RevertAsync(u, row, RevertScope.Stat);
        var orig = s.Original(u, row.Entry, row.Section);
        if (orig == null || !orig.IsSet || !orig.Editable || orig.Number is not double n)
        {
            Ask.Info(_form, "Nothing to reset", orig == null ? "The original value of this copy is not known (the copy was made by an older editor version)." : "This value has no plain original value to go back to.");
            return false;
        }
        if (row.Ref?.Number is double now && Math.Abs(now - n) <= 1e-9 * Math.Max(1, Math.Abs(n)))
        {
            Ask.Info(_form, "Nothing to reset", "This value is already the original value.");
            return false;
        }
        return await StageAsync(u, row, StatText.Num(StatText.ToDisplay(row.Entry, n)), family: false);
    }
}
