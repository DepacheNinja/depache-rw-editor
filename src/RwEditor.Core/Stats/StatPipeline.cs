using RwEditor.Core.Apply;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

namespace RwEditor.Core.Stats;

public sealed record StatApplyResult(StatEditPlan Plan, string BackupPath, IReadOnlyList<string> WrittenFiles, EditorSettings Settings, string SettingsPath);

public sealed record StatRevertResult(IReadOnlyList<StatEdit> Reverted, IReadOnlyDictionary<string, byte[]> NewBytes, bool Written, string? BackupPath,
    EditorSettings Settings, IReadOnlyList<string> NewLintFindings);

/// <summary>
/// Disk side of the stat editor, with the same guards as <see cref="ApplyPipeline"/> (DESIGN_EDITOR 5.4): folder guard (no Workshop / baseline /
/// non-editor folder / git tree), editor-support.json schema 1, every locked file hash-equal, the whole copy read once into RAM, plan + checks in RAM,
/// backup zip of the current bytes (and of the settings file) written once, each file written once (temp + File.Replace) and read back, then the
/// settings file written once. Undo = <see cref="ApplyPipeline.Undo"/> (kind stat-edit / stat-revert).
/// </summary>
public static class StatPipeline
{
    public static (string Root, EditorSupport Support, UnitModel Model) Open(string modRoot, ApplyOptions options)
    {
        string root = FolderGuard.Check(modRoot, options.AllowGitTree);
        var support = EditorSupport.Load(root);
        var locks = support.VerifyLockedFiles(root);
        if (!locks.Ok) throw new EditorRefusalException("LOCKED_FILES_CHANGED", "this copy is read-only for the editor: " + locks.Describe());
        var model = UnitModel.Build(ModSnapshot.Load(root), ModAreas.Embedded, support.LockedGlobs);
        return (root, support, model);
    }

    static Func<string, byte[]> Reader(string root) => rel => File.ReadAllBytes(ApplyPipeline.Full(root, rel));

    /// <summary>Guards + plan; nothing is written.</summary>
    public static StatEditPlan Plan(string modRoot, StatEditRequest req, ApplyOptions? options = null, StatDictionary? dict = null)
    {
        options ??= new ApplyOptions();
        var (root, _, model) = Open(modRoot, options);
        return StatPatcher.Plan(model, dict ?? StatDictionary.Embedded, req, Reader(root));
    }

    public static StatApplyResult Apply(string modRoot, StatEditRequest req, string settingsPath, ApplyOptions? options = null, StatDictionary? dict = null)
    {
        options ??= new ApplyOptions();
        var (root, support, model) = Open(modRoot, options);
        var (settings, settingsBefore) = LoadSettings(settingsPath);
        var plan = StatPatcher.Plan(model, dict ?? StatDictionary.Embedded, req, Reader(root));
        Merge(settings, plan.Record);
        if (settings.BaseMod == null) settings.BaseMod = new BaseModRef(support.ModVersion, support.Schema, support.LockedFileCount, support.LockedManifestSha256);

        var backup = BackupStore.Write(options.EffectiveBackupRoot, root, "stat-edit",
            plan.CurrentBytes.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(), SettingsJson.ContentHash(settings), options.Now, settingsPath, settingsBefore);
        var written = ApplyPipeline.WriteAll(root, plan.NewBytes, plan.CurrentBytes, options.BeforeWriteFile);
        WriteSettings(settingsPath, settings);
        return new StatApplyResult(plan, backup, written, settings, settingsPath);
    }

    /// <summary>Folds a new record into the settings: a further change of a line an earlier edit wrote updates that record (so a revert still goes back to
    /// the shipped text); a change back to the shipped text removes the record.</summary>
    public static void Merge(EditorSettings s, StatEdit rec)
    {
        var prev = rec.WriteKind == StatWriteKind.Patch
            ? s.StatEdits.LastOrDefault(e => e.File.Equals(rec.File, StringComparison.OrdinalIgnoreCase) && e.FileSection.Equals(rec.FileSection, StringComparison.OrdinalIgnoreCase)
                && e.Key.Equals(rec.Key, StringComparison.OrdinalIgnoreCase) && e.NewText == rec.OldText)
            : null;
        if (prev == null)
        {
            rec.Seq = s.NextStatSeq;
            s.StatEdits.Add(rec);
            return;
        }
        prev.NewText = rec.NewText;
        prev.AffectedUnits = Math.Max(prev.AffectedUnits, rec.AffectedUnits);
        if (rec.Mode == "template") prev.Mode = "template";
        if (prev.WriteKind == StatWriteKind.Patch && prev.NewText == prev.OldText) s.StatEdits.Remove(prev);
    }

    // ------------------------------------------------------------------------------------------------------------------ revert
    /// <summary>
    /// Puts the selected edits back (revert to defaults per key / unit / mod area / all, owner rule 6c): newest first, each one located by content (the
    /// line must still hold the text the edit wrote; otherwise the whole revert is refused as a conflict), all in RAM, then lint + guard, backup, one
    /// write per file, settings updated. <paramref name="dryRun"/> plans only.
    /// </summary>
    public static StatRevertResult Revert(string modRoot, string settingsPath, Func<StatEdit, bool> select, ApplyOptions? options = null, bool dryRun = false)
    {
        options ??= new ApplyOptions();
        var (root, _, model) = Open(modRoot, options);
        var (settings, settingsBefore) = LoadSettings(settingsPath);
        var chosen = settings.StatEdits.Where(select).OrderByDescending(e => e.Seq).ToList();
        if (chosen.Count == 0) return new StatRevertResult(chosen, new Dictionary<string, byte[]>(), false, null, settings, Array.Empty<string>());

        var texts = new Dictionary<string, IniText>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var e in chosen)
        {
            if (!LockPolicy.IsStatWritableFile(e.File, model.LockGlobs)) { conflicts.Add($"seq {e.Seq}: {e.File} is not an editable file"); continue; }
            if (!texts.TryGetValue(e.File, out var t))
            {
                var bytes = File.ReadAllBytes(ApplyPipeline.Full(root, e.File));
                current[ModSnapshot.Norm(e.File)] = bytes;
                t = IniText.Parse(bytes, e.File);
            }
            var r = RevertOne(t, e);
            if (r == null) { conflicts.Add($"seq {e.Seq}: {e.File} [{e.FileSection}] no longer holds '{e.Key}: {e.NewText}' (changed by hand?)"); continue; }
            texts[e.File] = r;
        }
        if (conflicts.Count > 0) throw new EditorRefusalException("REVERT_CONFLICT", "nothing reverted:\n  " + string.Join("\n  ", conflicts));

        var newTexts = texts.ToDictionary(kv => ModSnapshot.Norm(kv.Key), kv => kv.Value.Text, StringComparer.Ordinal);
        var model2 = model.WithFiles(newTexts);
        var lintNew = RwLint.NewFindings(model.Snapshot, model2.Snapshot);
        if (lintNew.Count > 0) throw new EditorRefusalException("LINT_NEW", "the revert would add lint findings: " + string.Join("; ", lintNew));
        var newBytes = texts.ToDictionary(kv => ModSnapshot.Norm(kv.Key), kv => kv.Value.ToBytes(), StringComparer.Ordinal);

        foreach (var e in chosen) settings.StatEdits.Remove(e);
        if (dryRun) return new StatRevertResult(chosen, newBytes, false, null, settings, lintNew);

        var backup = BackupStore.Write(options.EffectiveBackupRoot, root, "stat-revert", current.Select(kv => new BackupFile(kv.Key, kv.Value)).ToList(),
            SettingsJson.ContentHash(settings), options.Now, settingsPath, settingsBefore);
        ApplyPipeline.WriteAll(root, newBytes, current, options.BeforeWriteFile);
        WriteSettings(settingsPath, settings);
        return new StatRevertResult(chosen, newBytes, true, backup, settings, lintNew);
    }

    /// <summary>The file text with one edit undone, or null when the line the edit wrote is not there any more.</summary>
    public static IniText? RevertOne(IniText t, StatEdit e)
    {
        var doc = IniDocument.Parse(t.Text, t.RelPath, IniDialect.Catalog);
        (IniSection Sec, IniPair Pair)? hit = null;
        foreach (var s in doc.Sections.Where(s => s.Name.Equals(e.FileSection, StringComparison.OrdinalIgnoreCase)))
            foreach (var p in s.Pairs.Where(p => p.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase) && p.Value == e.NewText))
                hit = (s, p);
        if (hit == null) return null;
        var (sec, pair) = hit.Value;
        int idx = pair.Line - 1;
        switch (e.WriteKind)
        {
            case StatWriteKind.Patch:
                return t.WithValue(idx, e.OldText!);
            case StatWriteKind.Insert:
                // inserted right after the header; a later insert into the same section may sit between, which is fine
                if (e.EolAdded && idx != t.LastContentLine) return null;
                return t.WithLineRemoved(idx, e.EolAdded);
            default:
                // header + this one line only; when later edits added keys to the section, revert those first (or revert all, newest first)
                if (sec.Pairs.Count != 1 || idx != sec.Line) return null;
                var t1 = t.WithLineRemoved(idx, e.EolAdded);
                return t1.WithLineRemoved(sec.Line - 1, e.EolAdded);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ settings file
    /// <summary>The settings file (strict reader) and its bytes, or the defaults and null when it does not exist yet.</summary>
    public static (EditorSettings Settings, byte[]? Bytes) LoadSettings(string path)
    {
        if (!File.Exists(path)) return (EditorSettings.Default(), null);
        var bytes = File.ReadAllBytes(path);
        return (SettingsJson.Parse(new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('﻿'), path).Settings, bytes);
    }

    /// <summary>One write: temp file, then replace.</summary>
    public static void WriteSettings(string path, EditorSettings s)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".rwe-tmp";
        try
        {
            File.WriteAllBytes(tmp, SettingsJson.SerializeBytes(s));
            if (File.Exists(full)) File.Replace(tmp, full, null); else File.Move(tmp, full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Selection for revert-defaults: all, or by unit name, dictionary id / key, mod area (each optional, combined with AND).</summary>
    public static Func<StatEdit, bool> Select(string? unit, string? key, string? area) => e =>
        (unit == null || e.Unit == unit)
        && (key == null || e.StatId.Equals(key, StringComparison.OrdinalIgnoreCase) || e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        && (area == null || e.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
}
