using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Options;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.Core.Apply;

public sealed class ApplyOptions
{
    /// <summary>Where backups go; default %LOCALAPPDATA%\DepacheRwEditor\backups.</summary>
    public string? BackupRoot { get; init; }
    /// <summary>Allow a target inside a git work tree (development sources). Off by default.</summary>
    public bool AllowGitTree { get; init; }
    /// <summary>Clock for backup stamps (tests).</summary>
    public Func<DateTime>? UtcNow { get; init; }
    /// <summary>Test hook: called before each file write with (index, relPath); throwing simulates a failure mid-write.</summary>
    internal Action<int, string>? BeforeWriteFile { get; init; }

    public string EffectiveBackupRoot => BackupRoot ?? BackupStore.DefaultRoot;
    internal DateTime Now => (UtcNow ?? (() => DateTime.UtcNow))();
}

/// <summary>Everything decided in RAM before a write (DESIGN_EDITOR 5.4 steps 1-3).</summary>
public sealed record ApplyPlan(
    string ModRoot,
    EditorSettings Settings,
    GeneratedFiles Generated,
    IReadOnlyDictionary<string, byte[]> NewBytes,
    IReadOnlyDictionary<string, byte[]?> CurrentBytes,
    IReadOnlyList<Finding> Warnings,
    RuleReport Rules,
    IReadOnlyList<string> NewLintFindings,
    LockVerifyResult Locks,
    OptionPatch? Options = null,
    OptionTuning? Tuning = null)
{
    public bool Changes => NewBytes.Any(kv => CurrentBytes[kv.Key] is not byte[] cur || !cur.AsSpan().SequenceEqual(kv.Value));
}

public sealed record ApplyResult(ApplyPlan Plan, bool Written, string? BackupPath, IReadOnlyList<string> WrittenFiles);

public sealed record UndoResult(string RestoredFrom, string SafetyBackupPath, IReadOnlyList<string> RestoredFiles, RuleReport RulesAfter,
    string Kind = "apply", string? SettingsPath = null, byte[]? SettingsBytes = null);

/// <summary>
/// The apply pipeline for the two generated files (DESIGN_EDITOR 5.4): guard the folder, load the copy into RAM once, verify every locked file against
/// editor-support.json, validate the settings, generate, run the include rules and the lint on the PLANNED bytes, then back up the current bytes (one
/// zip, one write), write each file once (temp file + File.Replace) and verify. Any failure before the write leaves the disk untouched; a failure during
/// the write restores every touched file from the in-RAM backup.
/// </summary>
public static class ApplyPipeline
{
    public static readonly IReadOnlyList<string> Targets = EditorSupport.KnownGeneratedFiles;

    /// <summary>Steps 1-3: no writes. Throws EditorRefusalException (or SettingsInvalidException) when anything is wrong.</summary>
    public static ApplyPlan Plan(EditorSettings settings, string modRoot, ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        string root = FolderGuard.Check(modRoot, options.AllowGitTree);
        var support = EditorSupport.Load(root);
        var locks = support.VerifyLockedFiles(root);
        if (!locks.Ok) throw new EditorRefusalException("LOCKED_FILES_CHANGED", "this copy is read-only for the editor: " + locks.Describe());

        var snap = ModSnapshot.Load(root);
        var ctx = ModContext.Build(snap, support);
        var findings = SettingsValidator.Validate(settings, ctx);
        if (findings.Any(f => f.Severity == Severity.Error)) throw new SettingsInvalidException(findings);
        var gen = SpawnGroupGenerator.GenerateUnchecked(settings, ctx);

        // E5b: the locked option files, rebuilt from their shipped text with the user's choices (only the catalog's lines can change)
        var onSections = ctx.Options.ToDictionary(o => o.Tag, o => o.OnSection, StringComparer.Ordinal);
        var optModel = OptionModel.Load(root, settings.OptionEdits);
        var tuning = optModel.Normalize(settings.Tuning, onSections);
        var optPatch = optModel.Plan(tuning, onSections);

        // guard diff: only the generated files of the contract, never a locked path
        foreach (var rel in gen.ByRelPath.Keys)
            if (!support.GeneratedFiles.Contains(rel, StringComparer.Ordinal) || support.IsLocked(rel))
                throw new EditorRefusalException("GUARD_DIFF", $"the plan would write {rel}, which is not an editor-writable file");

        var plannedFiles = gen.ByRelPath.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (rel, bytes) in optPatch.NewBytes) plannedFiles[rel] = new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        var planned = snap.WithFiles(plannedFiles);
        var rules = IncludeRules.Check(planned);
        if (!rules.AllPass)
            throw new EditorRefusalException("INCLUDE_RULES", "the generated files would break the include rules:\n  " + string.Join("\n  ", rules.Failures));

        var empty = SpawnGroupGenerator.GenerateDefaults(ctx);
        var lintNew = RwLint.NewFindings(snap.WithFiles(empty.ByRelPath), planned);
        if (lintNew.Count > 0)
            throw new EditorRefusalException("LINT_NEW", "the generated files would add lint findings:\n  " + string.Join("\n  ", lintNew));

        var newBytes = gen.ByRelPath.ToDictionary(kv => kv.Key, kv => GeneratedFiles.Encoding.GetBytes(kv.Value), StringComparer.Ordinal);
        foreach (var (rel, bytes) in optPatch.NewBytes) newBytes[rel] = bytes;
        var cur = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var rel in newBytes.Keys)
        {
            string full = Full(root, rel);
            cur[rel] = File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        return new ApplyPlan(root, settings, gen, newBytes, cur, findings.Where(f => f.Severity == Severity.Warning).ToList(), rules, lintNew, locks, optPatch, tuning);
    }

    /// <summary>Plan, then back up and write once. Returns without writing (and without a backup) when the files already hold exactly the planned bytes.</summary>
    public static ApplyResult Apply(EditorSettings settings, string modRoot, ApplyOptions? options = null, string kind = "apply")
    {
        options ??= new ApplyOptions();
        var plan = Plan(settings, modRoot, options);
        // text / number choices rewrite protected files and must be recorded in the copy's own settings file, which only GroupsPipeline does
        if (plan.Options is { Changes: true })
            throw new EditorRefusalException("OPTION_CHOICES_NEED_COPY_SETTINGS", "these settings change the text or numbers of menu options; apply them with the editor's Menu options page or 'rwedit menu-apply', which record the change in the copy's settings file");
        if (!plan.Changes) return new ApplyResult(plan, false, null, Array.Empty<string>());
        var backup = BackupStore.Write(options.EffectiveBackupRoot, plan.ModRoot, kind,
            plan.CurrentBytes.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(), SettingsJson.ContentHash(settings), options.Now);
        var written = WriteAll(plan.ModRoot, plan.NewBytes, plan.CurrentBytes, options.BeforeWriteFile);
        return new ApplyResult(plan, true, backup, written);
    }

    /// <summary>Everything back to the shipped behaviour (no groups, nothing hidden), with a backup like any apply.</summary>
    public static ApplyResult RevertDefaults(string modRoot, ApplyOptions? options = null) =>
        Apply(EditorSettings.Default(), modRoot, options, "revert-defaults");

    /// <summary>
    /// Restores the generated files from a backup (default: the newest apply / revert-defaults backup of this copy). The current bytes are backed up
    /// first ("undo-safety"), so an undo can itself be undone with --backup.
    /// </summary>
    /// <param name="settingsPath">E3: the copy's settings file. When given, its current bytes go into the undo-safety backup too, and when the restored
    /// backup kept the settings of that same file, the settings are put back as well (applied stat edits as before the change; the list of PENDING changes
    /// is kept as it is now), so files and settings never disagree after an undo.</param>
    public static UndoResult Undo(string modRoot, ApplyOptions? options = null, string? backupPath = null, string? settingsPath = null)
    {
        options ??= new ApplyOptions();
        string root = FolderGuard.Check(modRoot, options.AllowGitTree);
        var support = EditorSupport.Load(root);   // same contract check as apply
        if (backupPath == null)
        {
            // the newest change that has not been undone yet (E3: an undo-safety backup names the change it undid), so repeated undos step back
            backupPath = History.NextUndo(options.EffectiveBackupRoot, root)?.Path
                ?? throw new EditorRefusalException("NO_BACKUP", $"no change to undo was found for {root} under {options.EffectiveBackupRoot}");
        }
        var info = BackupStore.Read(backupPath);
        if (!string.Equals(Path.GetFullPath(info.ModRoot).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
            throw new EditorRefusalException("BACKUP_OTHER_COPY", $"{backupPath} belongs to {info.ModRoot}, not to {root}");
        foreach (var f in info.Files)
        {
            // generated include files for start-option changes; editable (unlocked, non-generated) ini files for stat changes - never a locked file
            bool ok = BackupStore.IsRestorablePath(f.RelPath)
                || (BackupStore.IsStatKind(info.Kind) || info.Kind == "undo-safety") && Stats.LockPolicy.IsStatWritableFile(f.RelPath, support)
                // E5b: the option files of the Menu options page (the only locked files the editor ever writes), in a start-menu change or its undo
                || (info.Kind is "apply" or "revert-defaults" or "undo-safety") && OptionCatalog.Embedded.TargetFiles.Contains(ModSnapshot.Norm(f.RelPath), StringComparer.Ordinal);
            if (!ok) throw new EditorRefusalException("BACKUP_FORBIDDEN_PATH", $"{backupPath} holds {f.RelPath}, which the editor never writes");
        }

        var cur = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var f in info.Files) { string full = Full(root, f.RelPath); cur[f.RelPath] = File.Exists(full) ? File.ReadAllBytes(full) : null; }
        byte[]? settingsNow = settingsPath != null && File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        // the settings that go back: only when the backup kept the settings of this very file
        bool restoreSettings = settingsPath != null && info.SettingsPath != null && FolderGuard.SamePath(info.SettingsPath, settingsPath);
        EditorSettings? settingsAfter = null;
        if (restoreSettings)
        {
            settingsAfter = info.SettingsBytes == null ? EditorSettings.Default()
                : SettingsJson.Parse(new System.Text.UTF8Encoding(false).GetString(info.SettingsBytes).TrimStart('﻿'), info.Path + " (settings)").Settings;
            var pendingNow = settingsNow == null ? new List<PendingStatEdit>()
                : SettingsJson.Parse(new System.Text.UTF8Encoding(false).GetString(settingsNow).TrimStart('﻿'), settingsPath!).Settings.PendingStatEdits;
            settingsAfter.PendingStatEdits = pendingNow.Select(p => p.Clone()).ToList();
        }
        string safety = BackupStore.Write(options.EffectiveBackupRoot, root, "undo-safety", cur.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(), null, options.Now,
            settingsPath, settingsNow, summary: "Before undoing: " + (info.Summary ?? Path.GetFileName(backupPath)), undoes: Path.GetFileName(backupPath));

        var toWrite = info.Files.Where(f => f.Bytes != null).ToDictionary(f => f.RelPath, f => f.Bytes!, StringComparer.Ordinal);
        var written = WriteAll(root, toWrite, cur, options.BeforeWriteFile);
        // a file that did not exist before the change is removed again
        foreach (var f in info.Files.Where(f => f.Bytes == null)) { string full = Full(root, f.RelPath); if (File.Exists(full)) File.Delete(full); }
        if (restoreSettings)
        {
            try { Stats.StatPipeline.WriteSettings(settingsPath!, settingsAfter!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // files and settings must agree: put the files back to what they were before this undo
                var back = cur.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
                WriteAll(root, back, info.Files.ToDictionary(f => f.RelPath, f => f.Bytes, StringComparer.Ordinal), null);
                throw new ApplyFailedException($"the undo could not update the settings file ({e.Message}); the mod files were put back as they were before the undo", true, e);
            }
        }
        var rules = IncludeRules.Check(ModSnapshot.Load(root));
        return new UndoResult(backupPath, safety, written.Concat(info.Files.Where(f => f.Bytes == null).Select(f => f.RelPath)).ToList(), rules,
            info.Kind, info.SettingsPath, info.SettingsBytes);
    }

    internal static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Writes each file once: bytes to "&lt;file&gt;.rwe-tmp", then File.Replace (atomic per file on NTFS) or a move when the file is new; then reads it
    /// back. On any failure every file of the set is put back to <paramref name="before"/> and the temp files are removed.
    /// </summary>
    internal static IReadOnlyList<string> WriteAll(string root, IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<string, byte[]?> before, Action<int, string>? hook)
    {
        var written = new List<string>();
        int i = 0;
        try
        {
            foreach (var (rel, bytes) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                hook?.Invoke(i++, rel);
                string full = Full(root, rel), tmp = full + ".rwe-tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(full)) File.Replace(tmp, full, null);
                else File.Move(tmp, full);
                written.Add(rel);
            }
            foreach (var (rel, bytes) in files)
                if (!File.ReadAllBytes(Full(root, rel)).AsSpan().SequenceEqual(bytes)) throw new IOException($"read-back of {rel} differs from what was written");
            return written;
        }
        catch (Exception e)
        {
            bool restored = true;
            foreach (var rel in files.Keys)
            {
                string full = Full(root, rel);
                try
                {
                    if (File.Exists(full + ".rwe-tmp")) File.Delete(full + ".rwe-tmp");
                    var orig = before.TryGetValue(rel, out var b) ? b : null;
                    if (orig == null) { if (File.Exists(full)) File.Delete(full); }
                    else if (!File.Exists(full) || !File.ReadAllBytes(full).AsSpan().SequenceEqual(orig)) File.WriteAllBytes(full, orig);
                    if (orig != null && !File.ReadAllBytes(full).AsSpan().SequenceEqual(orig)) restored = false;
                }
                catch { restored = false; }
            }
            throw new ApplyFailedException(restored
                ? $"writing failed ({e.Message}); every generated file was restored to its bytes from before the attempt"
                : $"writing failed ({e.Message}) AND restoring failed; restore from the backup zip with 'rwedit undo'", restored, e);
        }
    }
}
