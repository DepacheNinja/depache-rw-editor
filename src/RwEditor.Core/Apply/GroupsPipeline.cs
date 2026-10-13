using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;

namespace RwEditor.Core.Apply;

/// <summary>The result of "Apply to my copy" for the start-unit menu: the plan, whether files changed, the backup and the settings that were saved.</summary>
public sealed record GroupsApplyResult(ApplyPlan Plan, bool Written, string? BackupPath, IReadOnlyList<string> WrittenFiles, EditorSettings Settings, string Summary);

/// <summary>
/// Spawn groups and the default start options of a COPY (E4): the same guards, plan, backup and write as <see cref="ApplyPipeline"/> (guard, locked hashes,
/// validation, include rules, lint, one backup zip, each file written once), plus the copy's settings file: the new groups and option states are merged
/// into the copy's own <c>rweditor-settings.json</c> (stat edits and the pending list stay as they are), the backup also keeps the settings bytes, and the
/// settings are written once after the files; if that write fails the files are put back, so files and settings never disagree. Undo = History / Undo
/// (<see cref="ApplyPipeline.Undo"/> restores the generated files and the settings).
/// </summary>
public static class GroupsPipeline
{
    /// <summary>The groups and option states currently saved in the copy (what the editor shows when it opens).</summary>
    public static EditorSettings LoadCurrent(string copyRoot)
    {
        string root = FolderGuard.Check(copyRoot, allowGitTree: false);
        return StatPipeline.LoadSettings(CopyFiles.SettingsPath(root)).Settings;
    }

    static EditorSettings Merge(EditorSettings onDisk, EditorSettings desired, EditorSupport support)
    {
        var s = onDisk.Clone();
        s.SpawnGroups = desired.SpawnGroups.Select(g => g.Clone()).ToList();
        s.StartOptions = new SortedDictionary<string, OptionState>(desired.StartOptions, StringComparer.Ordinal);
        s.Tuning = desired.Tuning.Clone();   // the recorded line edits (s.OptionEdits) stay as on disk until the plan has rebuilt them
        s.BaseMod ??= new BaseModRef(support.ModVersion, support.Schema, support.LockedFileCount, support.LockedManifestSha256);
        return s;
    }

    /// <summary>Guards + validation + generation + checks on the planned bytes; nothing is written. Throws EditorRefusalException / SettingsInvalidException.</summary>
    public static ApplyPlan Plan(string copyRoot, EditorSettings desired, ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        string root = FolderGuard.Check(copyRoot, options.AllowGitTree);
        var support = EditorSupport.Load(root);
        var (disk, _) = StatPipeline.LoadSettings(CopyFiles.SettingsPath(root));
        return ApplyPipeline.Plan(Merge(disk, desired, support), root, options);
    }

    /// <summary>Plan, back up (files + settings, one zip) and write. Nothing is written when neither the generated files nor the saved groups change.</summary>
    public static GroupsApplyResult Apply(string copyRoot, EditorSettings desired, ApplyOptions? options = null, string kind = "apply")
    {
        options ??= new ApplyOptions();
        string root = FolderGuard.Check(copyRoot, options.AllowGitTree);
        var support = EditorSupport.Load(root);
        string settingsPath = CopyFiles.SettingsPath(root);
        var (disk, settingsBefore) = StatPipeline.LoadSettings(settingsPath);
        var merged = Merge(disk, desired, support);
        var plan = ApplyPipeline.Plan(merged, root, options);
        // E5b: what is saved is the normalised choices (nothing that equals the shipped text) and the lines the plan rewrites
        merged.Tuning = plan.Tuning ?? merged.Tuning;
        merged.OptionEdits = plan.Options?.Edits.Select(e => e.Clone()).ToList() ?? merged.OptionEdits;
        string summary = Summary(merged, support);
        bool settingsChange = settingsBefore == null
            ? merged.SpawnGroups.Count > 0 || merged.StartOptions.Count > 0 || !merged.Tuning.IsEmpty
            : SettingsJson.ContentHash(merged) != SettingsJson.ContentHash(disk);
        if (!plan.Changes && !settingsChange) return new GroupsApplyResult(plan, false, null, Array.Empty<string>(), merged, summary);

        var backup = BackupStore.Write(options.EffectiveBackupRoot, plan.ModRoot, kind, plan.CurrentBytes.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(),
            SettingsJson.ContentHash(merged), options.Now, settingsPath, settingsBefore, summary);
        var written = plan.Changes ? ApplyPipeline.WriteAll(plan.ModRoot, plan.NewBytes, plan.CurrentBytes, options.BeforeWriteFile) : Array.Empty<string>();
        StatBatch.WriteSettingsOrRestore(root, settingsPath, merged, plan.NewBytes, plan.CurrentBytes);
        return new GroupsApplyResult(plan, true, backup, written, merged, summary);
    }

    /// <summary>Everything in the start-unit menu back to the shipped behaviour (no groups, no default option hidden), with a backup like any apply.</summary>
    public static GroupsApplyResult RevertAll(string copyRoot, ApplyOptions? options = null)
    {
        var cur = LoadCurrent(copyRoot);
        cur.RevertAll();
        return Apply(copyRoot, cur, options, "revert-defaults");
    }

    /// <summary>The History line: groups with their units, hidden / deleted default options.</summary>
    public static string Summary(EditorSettings s, EditorSupport support)
    {
        var parts = new List<string>();
        foreach (var g in s.SpawnGroups.OrderBy(g => g.Slot))
            parts.Add($"group \"{g.Name}\" ({g.Units.Sum(u => u.Count)} units{(g.Enabled ? "" : ", switched off")}{(g.Mode == GroupMode.HumansOnly ? ", humans only" : "")}{(g.DefaultOn ? ", on by default" : "")})");
        foreach (var (tag, st) in s.StartOptions)
            parts.Add($"menu option {tag} {(st == OptionState.Deleted ? "deleted" : "hidden")}");
        foreach (var (id, v) in s.Tuning.Values) parts.Add($"setting {id} = {v}");
        foreach (var (tag, t) in s.Tuning.Texts)
            parts.Add($"option {tag} " + string.Join(" and ", new[] { t.Name != null ? "renamed" : null, t.Description != null ? "description changed" : null }.Where(x => x != null)));
        return parts.Count == 0 ? "start units: back to the defaults (no groups, nothing hidden or changed)" : "start units: " + string.Join("; ", parts);
    }
}
