using RwEditor.Core.Apply;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.Core.Stats;

public enum RevertScope
{
    /// <summary>One stat of one unit: the applied changes recorded for it, plus a shared-line change this unit's value now comes from.</summary>
    Stat,
    /// <summary>Every applied change recorded on one unit.</summary>
    Unit,
    /// <summary>Every applied change of units in one mod area (main, fe, moo, vte, depache).</summary>
    Area,
    /// <summary>Everything in the copy back to the values it had when it was made (the copy's saved original values; older copies: the backups).</summary>
    All,
}

public sealed record RevertRequest(RevertScope Scope, string? Unit = null, string? UnitFile = null, string? StatId = null, string? Section = null, string? Area = null);

/// <summary>What a revert would do, decided in RAM: the recorded changes it puts back (scopes Stat / Unit / Area, or All without a saved original), or
/// the files it restores (All). <see cref="Method"/> says how, in plain words.</summary>
public sealed record RevertPlan(RevertRequest Request, IReadOnlyList<StatEdit> Edits, IReadOnlyDictionary<string, byte[]> NewBytes,
    IReadOnlyDictionary<string, byte[]?> CurrentBytes, string Method, IReadOnlyList<string> Notes, IReadOnlyList<string> NewLintFindings)
{
    public bool Changes => NewBytes.Count > 0 || Edits.Count > 0;
    /// <summary>One line per recorded change it puts back (or per file restored).</summary>
    public IReadOnlyList<string> Lines => Edits.Count > 0
        ? Edits.Select(e => $"{e.Unit} - {StatText.StatName(StatEditView(e), e.Section)}: back to {(e.OldText != null ? "'" + e.OldText + "'" : "the shared / default value")}" +
                             (e.Mode == "template" ? $" (shared line: {e.AffectedUnits} units)" : "")).ToList()
        : NewBytes.Keys.Select(k => "file " + k + " back to the original").ToList();

    static StatEntry StatEditView(StatEdit e) => StatDictionary.Embedded.ById(e.StatId) ?? StatDictionary.Embedded.Entries[0];
}

public sealed record RevertResult(RevertPlan Plan, string BackupPath, IReadOnlyList<string> WrittenFiles, EditorSettings Settings);

/// <summary>
/// "Revert..." (E3; owner rule 6c: revert to defaults per item / unit / mod area / everything). Every scope regenerates a plan in RAM and goes through the
/// same steps as an apply: folder guard + marker + locked hashes, plan, checks (re-parse, lint 0 new), backup zip of the files AND the settings (one
/// write), one write per file (temp + File.Replace; any failure restores every file), settings written once (or the files are put back). Everything is
/// itself undoable (History > Undo).
/// - Stat / Unit / Area: the recorded changes (settings statEdits) are taken back newest first BY CONTENT (StatPipeline.RevertOne): a line changed by hand
///   after the editor wrote it refuses the whole revert (REVERT_CONFLICT) instead of guessing.
/// - All with the copy's saved original values (<see cref="CopyBaseline"/>): every stat-writable file that differs from the snapshot gets the snapshot's
///   bytes back (also hand-made changes to those files); the generated start-option files and locked files are never touched. All without it (copies
///   made before E3): every stat-writable file the editor changed gets back its bytes from the OLDEST backup that holds it (= before the first change);
///   files the editor never changed are not touched (documented limit: hand edits are not covered, and deleted backups cannot be used).
/// Pending (staged, not applied) changes are never touched by a revert.
/// </summary>
public static class StatRevert
{
    public static RevertPlan Plan(string copyRoot, RevertRequest req, ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        var (root, _, model) = StatPipeline.Open(copyRoot, options);
        var (settings, _) = StatPipeline.LoadSettings(CopyFiles.SettingsPath(root));
        return PlanCore(root, model, settings, req, options);
    }

    static RevertPlan PlanCore(string root, UnitModel model, EditorSettings settings, RevertRequest req, ApplyOptions options)
    {
        if (req.Scope == RevertScope.All) return PlanAll(root, model, settings, req, options);
        Func<StatEdit, bool> sel = req.Scope switch
        {
            RevertScope.Unit => e => e.Unit == req.Unit && (req.UnitFile == null || e.UnitFile.Equals(req.UnitFile, StringComparison.OrdinalIgnoreCase)),
            RevertScope.Area => e => e.Area.Equals(req.Area, StringComparison.OrdinalIgnoreCase),
            _ => StatSelector(model, req),
        };
        var chosen = settings.StatEdits.Where(sel).OrderByDescending(e => e.Seq).ToList();
        if (chosen.Count == 0) return new RevertPlan(req, chosen, new Dictionary<string, byte[]>(), new Dictionary<string, byte[]?>(), "no recorded change matches", Array.Empty<string>(), Array.Empty<string>());

        var texts = new Dictionary<string, IniText>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var e in chosen)
        {
            if (!LockPolicy.IsStatWritableFile(e.File, model.LockGlobs)) { conflicts.Add($"{e.File} is not a file the editor may change"); continue; }
            string k = ModSnapshot.Norm(e.File);
            if (!texts.TryGetValue(k, out var t))
            {
                var bytes = File.ReadAllBytes(ApplyPipeline.Full(root, k));
                current[k] = bytes;
                t = IniText.Parse(bytes, k);
            }
            var r = StatPipeline.RevertOne(t, e);
            if (r == null) { conflicts.Add($"{e.Unit} - {e.StatId}: the line in {e.File} no longer holds '{e.Key}: {e.NewText}' (changed outside the editor?)"); continue; }
            texts[k] = r;
        }
        if (conflicts.Count > 0)
            throw new EditorRefusalException("REVERT_CONFLICT", "Nothing was put back, because:\n- " + string.Join("\n- ", conflicts) +
                "\nUse \"Undo\" in History, or put everything back to the original.");
        var model2 = model.WithFiles(texts.ToDictionary(kv => kv.Key, kv => kv.Value.Text, StringComparer.OrdinalIgnoreCase));
        var lintNew = RwLint.NewFindings(model.Snapshot, model2.Snapshot);
        if (lintNew.Count > 0) throw new EditorRefusalException("LINT_NEW", "Putting these values back would add problems the game reports when loading: " + string.Join("; ", lintNew));
        var newBytes = texts.ToDictionary(kv => kv.Key, kv => kv.Value.ToBytes(), StringComparer.Ordinal);
        return new RevertPlan(req, chosen, newBytes, current, "the recorded changes are taken back, newest first", Array.Empty<string>(), lintNew);
    }

    /// <summary>Scope Stat: the changes recorded on this unit + stat + section, and a shared-line change the unit's current value comes from.</summary>
    static Func<StatEdit, bool> StatSelector(UnitModel model, RevertRequest req)
    {
        Origin? origin = null;
        var u = req.UnitFile != null ? model.UnitsInFile(req.UnitFile).FirstOrDefault(x => x.Name == req.Unit) : model.Find(req.Unit ?? "");
        if (u != null && req.StatId != null && StatDictionary.Embedded.ById(req.StatId) is StatEntry entry && req.Section != null)
            origin = model.Merge(u).Get(req.Section, entry.Key)?.Origin;
        return e =>
            (e.Unit == req.Unit && e.StatId.Equals(req.StatId, StringComparison.OrdinalIgnoreCase) && e.Section.Equals(req.Section, StringComparison.OrdinalIgnoreCase))
            || (origin != null && e.Mode == "template" && e.File.Equals(origin.File, StringComparison.OrdinalIgnoreCase)
                && e.FileSection.Equals(origin.FileSection, StringComparison.OrdinalIgnoreCase) && e.StatId.Equals(req.StatId, StringComparison.OrdinalIgnoreCase));
    }

    static RevertPlan PlanAll(string root, UnitModel model, EditorSettings settings, RevertRequest req, ApplyOptions options)
    {
        var notes = new List<string>();
        CopyBaseline? baseline = null;
        try { baseline = CopyBaseline.Load(root); }
        catch (EditorRefusalException e) { notes.Add(e.Message); }

        var newBytes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var current = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        string method;
        if (baseline != null)
        {
            method = "every value file back to the copy's saved original";
            foreach (var (rel, (orig, now)) in baseline.Differences(root))
            {
                if (!LockPolicy.IsStatWritableFile(rel, model.LockGlobs)) continue;   // never a locked / generated file, whatever the snapshot holds
                newBytes[rel] = orig;
                current[rel] = now;
            }
        }
        else
        {
            method = "every value file the editor changed back to its bytes in the oldest backup (this copy has no saved original: it was made before this editor version)";
            notes.Add("This copy was made by an older editor version, so it has no saved original values. Files the editor changed are put back from the " +
                      "oldest backups; changes made outside the editor, or covered only by deleted backups, cannot be put back this way.");
            var oldest = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in BackupStore.List(options.EffectiveBackupRoot, root).Reverse())   // oldest first
            {
                BackupInfo info;
                try { info = BackupStore.Read(p); }
                catch (Exception e) when (e is InvalidDataException or IOException or System.Text.Json.JsonException) { notes.Add($"backup {Path.GetFileName(p)} cannot be read and was skipped: {e.Message}"); continue; }
                foreach (var f in info.Files)
                    if (LockPolicy.IsStatWritableFile(f.RelPath, model.LockGlobs) && !oldest.ContainsKey(f.RelPath)) oldest[f.RelPath] = f.Bytes;
            }
            foreach (var (rel, bytes) in oldest)
            {
                string full = ApplyPipeline.Full(root, rel);
                byte[]? now = File.Exists(full) ? File.ReadAllBytes(full) : null;
                if (bytes == null) continue;   // the file did not exist before the first change; the stat editor never creates files, so this does not occur
                if (now != null && now.AsSpan().SequenceEqual(bytes)) continue;
                newBytes[ModSnapshot.Norm(rel)] = bytes;
                current[ModSnapshot.Norm(rel)] = now;
            }
        }
        // the restored files must read back (lossless parser) and are linted like any change; findings are only reported (the original is what the copy
        // shipped with: a finding there is "already in the shipped mod")
        foreach (var (rel, bytes) in newBytes) IniText.Parse(bytes, rel);
        var texts = newBytes.ToDictionary(kv => kv.Key, kv => IniText.Parse(kv.Value, kv.Key).Text, StringComparer.OrdinalIgnoreCase);
        var lintNew = texts.Count > 0 ? RwLint.NewFindings(model.Snapshot, model.WithFiles(texts).Snapshot) : new List<string>();
        if (lintNew.Count > 0) notes.Add("Already in the original copy (the game reported these before any change): " + string.Join("; ", lintNew));
        return new RevertPlan(req, settings.StatEdits.OrderByDescending(e => e.Seq).ToList(), new Dictionary<string, byte[]>(newBytes, StringComparer.Ordinal), current, method, notes, lintNew);
    }

    /// <summary>Plans and applies a revert (see the class summary). Returns null when there is nothing to put back.</summary>
    public static RevertResult? Apply(string copyRoot, RevertRequest req, ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        var (root, _, model) = StatPipeline.Open(copyRoot, options);
        string settingsPath = CopyFiles.SettingsPath(root);
        var (settings, settingsBefore) = StatPipeline.LoadSettings(settingsPath);
        var plan = PlanCore(root, model, settings, req, options);
        if (!plan.Changes) return null;
        foreach (var e in plan.Edits) settings.StatEdits.RemoveAll(x => x.Seq == e.Seq);
        string kind = req.Scope == RevertScope.All ? "stat-revert-all" : "stat-revert";
        string summary = req.Scope switch
        {
            RevertScope.All => $"Everything put back to the original ({plan.NewBytes.Count} file(s); {plan.Edits.Count} recorded change(s))",
            RevertScope.Area => $"Mod area '{req.Area}' put back: " + string.Join("; ", plan.Lines),
            RevertScope.Unit => $"{req.Unit} put back: " + string.Join("; ", plan.Lines),
            _ => string.Join("; ", plan.Lines),
        };
        var backup = BackupStore.Write(options.EffectiveBackupRoot, root, kind, plan.CurrentBytes.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(),
            SettingsJson.ContentHash(settings), options.Now, settingsPath, settingsBefore, summary);
        var written = plan.NewBytes.Count == 0 ? Array.Empty<string>() : ApplyPipeline.WriteAll(root, plan.NewBytes, plan.CurrentBytes, options.BeforeWriteFile);
        StatBatch.WriteSettingsOrRestore(root, settingsPath, settings, plan.NewBytes, plan.CurrentBytes);
        return new RevertResult(plan, backup, written, settings);
    }
}
