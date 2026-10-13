using RwEditor.Core.Apply;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.Core.Stats;

/// <summary>One staged change inside a batch plan: its plan when it passed every check, otherwise the reasons (errors and warnings).</summary>
public sealed record StatBatchItem(PendingStatEdit Pending, StatEditPlan? Plan, IReadOnlyList<Finding> Findings)
{
    public bool Ok => Plan != null && !Findings.Any(f => f.Severity == Severity.Error);
    public IEnumerable<Finding> Errors => Findings.Where(f => f.Severity == Severity.Error);
    public IEnumerable<Finding> Warnings => Findings.Where(f => f.Severity == Severity.Warning);
}

/// <summary>Everything decided in RAM for "Apply changes": one item per staged change, the final bytes of every touched file (the bytes on disk before,
/// for the backup) and the batch-level problems (lint, a later change undoing an earlier one).</summary>
public sealed record StatBatchPlan(IReadOnlyList<StatBatchItem> Items, IReadOnlyDictionary<string, byte[]> NewBytes, IReadOnlyDictionary<string, byte[]?> CurrentBytes,
    IReadOnlyList<Finding> BatchFindings, IReadOnlyList<string> NewLintFindings, UnitModel FinalModel)
{
    public bool CanApply => Items.Count > 0 && Items.All(i => i.Ok) && !BatchFindings.Any(f => f.Severity == Severity.Error);
    public IEnumerable<Finding> AllWarnings => Items.SelectMany(i => i.Warnings).Concat(BatchFindings.Where(f => f.Severity == Severity.Warning));

    /// <summary>Every reason the batch cannot be applied, in plain words, one line each (empty when it can).</summary>
    public IReadOnlyList<string> Problems =>
        Items.Where(i => !i.Ok).SelectMany(i => i.Errors.Select(e => $"{i.Pending.Unit} - {StatText.StatName(i.Pending)}: {StatText.Plain(e)}"))
            .Concat(BatchFindings.Where(f => f.Severity == Severity.Error).Select(StatText.Plain)).ToList();
}

public sealed record StatBatchResult(StatBatchPlan Plan, string BackupPath, IReadOnlyList<string> WrittenFiles, EditorSettings Settings, string Summary);

/// <summary>"Apply changes" refused: nothing was written. <see cref="Plan"/> says which staged change failed and why.</summary>
public sealed class StatBatchRefusedException : EditorRefusalException
{
    public StatBatchRefusedException(StatBatchPlan plan)
        : base("STAGED_CHANGES_REFUSED", "Nothing was changed. These staged changes cannot be applied:\n- " + string.Join("\n- ", plan.Problems)) { Plan = plan; }

    public StatBatchPlan Plan { get; }
}

/// <summary>
/// The staged-changes pipeline (E3): every pending change is planned IN ORDER on a model that already holds the earlier ones (so two changes of one file
/// end in one write), each with the full single-edit checks of <see cref="StatPatcher"/> (whitelist, locks, hard limits, origin, unit-only vs template,
/// cross-key rules on every affected unit), then the whole result is linted ONCE (0 new findings) and every change is checked to still hold in the final
/// result. Apply = guards (folder, marker, locked hashes) -> plan -> backup zip of every touched file AND the settings (one write) -> each file written once
/// (temp + File.Replace, read back; any failure restores every file) -> settings written once (records added, pending list emptied; a failure here puts
/// the files back too, so files and settings never disagree). Undo = <see cref="ApplyPipeline.Undo"/> with the settings path.
/// </summary>
public static class StatBatch
{
    public static StatEditRequest Request(PendingStatEdit p) =>
        new(p.Unit, p.StatId, p.Value, p.Mode == "template" ? StatEditMode.Template : StatEditMode.UnitOnly, p.Section);

    /// <summary>Plans the batch on an in-RAM model. Pure: reads through <paramref name="readDisk"/> only (the bytes on disk now).</summary>
    public static StatBatchPlan Plan(UnitModel model, StatDictionary dict, IReadOnlyList<PendingStatEdit> pendings, Func<string, byte[]> readDisk)
    {
        var items = new List<StatBatchItem>();
        var planned = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var disk = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        byte[] Read(string rel)
        {
            rel = ModSnapshot.Norm(rel);
            if (planned.TryGetValue(rel, out var b)) return b;
            if (!disk.TryGetValue(rel, out var d)) disk[rel] = d = readDisk(rel);
            return d!;
        }
        var cur = model;
        foreach (var p in pendings)
        {
            try
            {
                var plan = StatPatcher.Plan(cur, dict, Request(p), Read, lint: false, chainIndex: model);
                items.Add(new StatBatchItem(p, plan, plan.Warnings));
                foreach (var (rel, bytes) in plan.NewBytes)
                {
                    string k = ModSnapshot.Norm(rel);
                    if (!disk.ContainsKey(k)) disk[k] = plan.CurrentBytes[rel];
                    planned[k] = bytes;
                }
                cur = cur.WithFiles(plan.NewBytes.ToDictionary(kv => kv.Key, kv => IniText.Parse(kv.Value, kv.Key).Text, StringComparer.OrdinalIgnoreCase));
            }
            catch (StatEditRefusedException e) { items.Add(new StatBatchItem(p, null, e.Findings)); }
            catch (EditorRefusalException e) { items.Add(new StatBatchItem(p, null, new[] { new Finding(Severity.Error, e.Code, e.Message) })); }
        }

        var batch = new List<Finding>();
        // every change must still hold in the final result (a later change of the same target replaces an earlier one; staging never keeps both)
        var seen = new HashSet<(string, string, string)>();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (it.Plan is not StatEditPlan pl) continue;
            if (!seen.Add((pl.Unit.RelPath.ToLowerInvariant(), pl.Entry.Id.ToLowerInvariant(), pl.Section.ToLowerInvariant()))) continue;
            var u = cur.UnitsInFile(pl.Unit.RelPath).FirstOrDefault(x => x.Name == pl.Unit.Name);
            var after = u == null ? null : StatReader.Read(cur.Merge(u), pl.Entry, pl.Section);
            if (after?.Number is not double n || Math.Abs(n - pl.NewNumber) > 1e-9 * Math.Max(1, Math.Abs(pl.NewNumber)))
                batch.Add(new Finding(Severity.Error, "BATCH_OVERRIDDEN", $"{pl.Unit.Name} - {pl.Entry.Label}: another staged change would undo this one (it would read '{after?.Raw?.Value}'). Remove one of them."));
        }
        var lintNew = items.Any(i => i.Plan != null) ? RwLint.NewFindings(model.Snapshot, cur.Snapshot) : new List<string>();
        if (lintNew.Count > 0) batch.Add(new Finding(Severity.Error, "LINT_NEW", "the changes together would add problems the game reports when loading: " + string.Join("; ", lintNew)));

        var changed = planned.Where(kv => disk[kv.Key] is not byte[] d || !d.AsSpan().SequenceEqual(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var current = changed.Keys.ToDictionary(k => k, k => disk[k], StringComparer.Ordinal);
        return new StatBatchPlan(items, changed, current, batch, lintNew, cur);
    }

    /// <summary>Guards + plan of the copy's pending changes; nothing is written.</summary>
    public static StatBatchPlan PlanCopy(string copyRoot, ApplyOptions? options = null, StatDictionary? dict = null)
    {
        options ??= new ApplyOptions();
        var (root, _, model) = StatPipeline.Open(copyRoot, options);
        var (settings, _) = StatPipeline.LoadSettings(CopyFiles.SettingsPath(root));
        return Plan(model, dict ?? StatDictionary.Embedded, settings.PendingStatEdits, rel => File.ReadAllBytes(ApplyPipeline.Full(root, rel)));
    }

    /// <summary>"Apply changes": writes every pending change of the copy in one go (see the class summary). Throws StatBatchRefusedException (nothing
    /// written), EditorRefusalException (guards; nothing written) or ApplyFailedException (a write failed; every file was put back).</summary>
    public static StatBatchResult Apply(string copyRoot, ApplyOptions? options = null, StatDictionary? dict = null)
    {
        options ??= new ApplyOptions();
        var (root, support, model) = StatPipeline.Open(copyRoot, options);
        string settingsPath = CopyFiles.SettingsPath(root);
        var (settings, settingsBefore) = StatPipeline.LoadSettings(settingsPath);
        if (settings.PendingStatEdits.Count == 0) throw new EditorRefusalException("NOTHING_STAGED", "There are no pending changes to apply.");
        var plan = Plan(model, dict ?? StatDictionary.Embedded, settings.PendingStatEdits, rel => File.ReadAllBytes(ApplyPipeline.Full(root, rel)));
        if (!plan.CanApply) throw new StatBatchRefusedException(plan);

        foreach (var it in plan.Items) StatPipeline.Merge(settings, it.Plan!.Record);
        settings.PendingStatEdits.Clear();
        settings.BaseMod ??= new BaseModRef(support.ModVersion, support.Schema, support.LockedFileCount, support.LockedManifestSha256);
        string summary = Summary(plan);

        var backup = BackupStore.Write(options.EffectiveBackupRoot, root, "stat-edit", plan.CurrentBytes.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(),
            SettingsJson.ContentHash(settings), options.Now, settingsPath, settingsBefore, summary);
        var written = plan.NewBytes.Count == 0 ? Array.Empty<string>() : ApplyPipeline.WriteAll(root, plan.NewBytes, plan.CurrentBytes, options.BeforeWriteFile);
        WriteSettingsOrRestore(root, settingsPath, settings, plan.NewBytes, plan.CurrentBytes);
        return new StatBatchResult(plan, backup, written, settings, summary);
    }

    /// <summary>Writes the settings once; if that fails, the files just written are put back to <paramref name="before"/> (files and settings must agree).</summary>
    internal static void WriteSettingsOrRestore(string root, string settingsPath, EditorSettings settings, IReadOnlyDictionary<string, byte[]> written,
        IReadOnlyDictionary<string, byte[]?> before)
    {
        try { StatPipeline.WriteSettings(settingsPath, settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bool restored = true;
            foreach (var rel in written.Keys)
            {
                try
                {
                    string full = ApplyPipeline.Full(root, rel);
                    if (before[rel] is byte[] b) File.WriteAllBytes(full, b); else if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { restored = false; }
            }
            throw new ApplyFailedException(restored
                ? $"The list of changes ({CopyFiles.SettingsName}) could not be saved ({e.Message}). The mod files were put back as they were: nothing changed."
                : $"The list of changes could not be saved ({e.Message}) AND some files could not be put back. Use History > Undo to restore the backup.", restored, e);
        }
    }

    /// <summary>The History line of a batch: one "unit: stat old -> new" per change.</summary>
    public static string Summary(StatBatchPlan plan) =>
        string.Join("; ", plan.Items.Where(i => i.Plan != null).Select(i =>
        {
            var p = i.Plan!;
            string old = p.Before.IsSet && p.Before.Number is double b ? StatText.Value(p.Entry, b) : "not set";
            string scope = p.Request.Mode == StatEditMode.Template ? $" (shared: {p.Affected.Count} units)" : "";
            return $"{p.Unit.Name} {StatText.StatName(p.Entry, p.Section)} {old} -> {StatText.Value(p.Entry, p.NewNumber)}{scope}";
        }));
}
