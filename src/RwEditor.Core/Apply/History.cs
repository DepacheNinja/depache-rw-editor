namespace RwEditor.Core.Apply;

/// <summary>One backup of a copy as the History screen lists it.</summary>
/// <param name="IsChange">A backup made by a change (apply, stat edit, revert): undoing it puts the files back as they were before that change.</param>
/// <param name="Undone">For a change: an undo already restored it (a later "undo-safety" backup names it).</param>
/// <param name="Problem">The backup cannot be read (damaged / unknown format); it is listed but never used.</param>
public sealed record HistoryEntry(string Path, string Kind, DateTime CreatedUtc, string Summary, IReadOnlyList<string> Files, bool IsChange, bool Undone, string? Problem)
{
    /// <summary>The kind in plain words.</summary>
    public string KindText => Kind switch
    {
        "stat-edit" => "Values changed",
        "stat-revert" => "Values put back",
        "stat-revert-all" => "Everything put back to the original",
        "undo-safety" => "Undo",
        "apply" => "Start units / menu changed",
        "revert-defaults" => "Start units / menu put back",
        _ => Kind,
    };
}

/// <summary>
/// The change history of a copy (E3), read from its backup zips (BackupStore, newest first). The undo stack: a change backup counts as UNDONE when an
/// "undo-safety" backup names it (BackupStore "undoes"); <see cref="NextUndo"/> is the newest change not undone yet, so pressing "Undo last change" again
/// steps further back. Backups are never deleted by the editor.
/// </summary>
public static class History
{
    public static IReadOnlyList<HistoryEntry> List(string backupRoot, string modRoot)
    {
        var list = new List<HistoryEntry>();
        var infos = new List<(string Path, BackupInfo? Info, string? Problem)>();
        foreach (var p in BackupStore.List(backupRoot, modRoot))
        {
            try { infos.Add((p, BackupStore.ReadManifest(p), null)); }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                          or InvalidOperationException or NullReferenceException or FormatException)
            { infos.Add((p, null, "this backup cannot be read: " + e.Message)); }
        }
        var undone = infos.Where(i => i.Info?.Kind == "undo-safety" && i.Info.Undoes != null).Select(i => i.Info!.Undoes!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, info, problem) in infos)
        {
            if (info == null) { list.Add(new HistoryEntry(p, "?", File.GetLastWriteTimeUtc(p), System.IO.Path.GetFileName(p), Array.Empty<string>(), false, false, problem)); continue; }
            bool change = BackupStore.ChangeKinds.Contains(info.Kind);
            string summary = info.Summary ?? (info.Files.Count == 1 ? info.Files[0].RelPath : $"{info.Files.Count} files");
            list.Add(new HistoryEntry(p, info.Kind, info.CreatedUtc, summary, info.Files.Select(f => f.RelPath).ToList(), change,
                change && undone.Contains(System.IO.Path.GetFileName(p)), null));
        }
        return list;
    }

    /// <summary>The newest readable change backup that has not been undone, or null.</summary>
    public static HistoryEntry? NextUndo(string backupRoot, string modRoot) =>
        List(backupRoot, modRoot).FirstOrDefault(h => h.IsChange && !h.Undone && h.Problem == null);
}
