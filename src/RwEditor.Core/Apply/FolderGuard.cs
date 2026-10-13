using RwEditor.Core.Mod;

namespace RwEditor.Core.Apply;

/// <summary>
/// Decides whether the editor may WRITE into a folder (DESIGN_EDITOR 5.4 "Folder guard"). Refused:
/// - a folder that does not exist, or a drive root;
/// - anything under a Steam Workshop content folder (steamapps\workshop\content): the Workshop owns those files;
/// - the pristine baseline: any path segment named *_pristine or baseline_*;
/// - a folder without editor-support.json at its root (not a mod made for this editor, or not its root);
/// - a folder inside a git work tree (e.g. the mod's own development source), unless explicitly allowed;
/// - (E2, PH-071 resolved) a folder without a valid "made by the editor" marker (<see cref="EditorCopyMarker"/>, rweditor-copy.json), or whose marker
///   names the folder itself as the source it was copied from.
/// Deviation from DESIGN_EDITOR 5.4: the copy is NOT required to sit under &lt;game&gt;\mods\units. The owner's E2 brief puts the default copy under
/// %LOCALAPPDATA%\DepacheRwEditor\mymod (user-controlled); the marker alone proves the editor made the folder.
/// </summary>
public static class FolderGuard
{
    public static string Check(string folder, bool allowGitTree, bool requireEditorSupport = true, bool requireCopyMarker = true)
    {
        string full = CheckLocation(folder, allowGitTree);
        if (requireEditorSupport && !File.Exists(Path.Combine(full, EditorSupport.FileName)))
            throw new EditorRefusalException("NO_EDITOR_SUPPORT", $"{EditorSupport.FileName} not found in {full}: not a mod made for the Depache RW Editor (or not its root folder)");

        if (requireCopyMarker)
        {
            var marker = EditorCopyMarker.Load(full);   // NO_COPY_MARKER / COPY_MARKER_INVALID
            if (SamePath(marker.SourcePath, full))
                throw new EditorRefusalException("COPY_IS_SOURCE", $"{full} is the folder the copy was made FROM, not the copy itself; the editor never changes the original.");
        }
        return full;
    }

    /// <summary>
    /// The location rules alone (missing / drive root / Workshop / baseline / git tree), used by the copy wizard for a destination that does not hold a
    /// mod yet. Returns the full path without a trailing separator.
    /// </summary>
    public static string CheckLocation(string folder, bool allowGitTree, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new EditorRefusalException("FOLDER_MISSING", "no folder given");
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new EditorRefusalException("FOLDER_INVALID", $"'{folder}' is not a valid folder path ({e.Message})"); }
        if (mustExist && !Directory.Exists(full)) throw new EditorRefusalException("FOLDER_MISSING", "folder not found: " + full);
        if (Path.GetPathRoot(full)?.TrimEnd('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            throw new EditorRefusalException("FOLDER_ROOT", "refusing a drive root: " + full);

        if (IsWorkshopPath(full))
            throw new EditorRefusalException("FOLDER_WORKSHOP", "refusing a Steam Workshop content folder (the Workshop owns these files; edit a copy instead): " + full);

        foreach (var seg in full.Split('\\', '/'))
        {
            if (seg.EndsWith("_pristine", StringComparison.OrdinalIgnoreCase) || seg.StartsWith("baseline_", StringComparison.OrdinalIgnoreCase))
                throw new EditorRefusalException("FOLDER_BASELINE", $"refusing the pristine baseline ('{seg}' in {full})");
        }

        if (!allowGitTree)
        {
            for (var d = new DirectoryInfo(full); d != null; d = d.Parent)
            {
                if (!d.Exists) continue;
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                    throw new EditorRefusalException("FOLDER_GIT_TREE", $"refusing a folder inside a git work tree ({d.FullName}): that is a development source, not a player copy. Use a copy (or --allow-git-tree).");
            }
        }
        return full;
    }

    public static bool IsWorkshopPath(string fullPath) =>
        (fullPath.Replace('/', '\\').TrimEnd('\\') + "\\").Contains(@"\steamapps\workshop\content\", StringComparison.OrdinalIgnoreCase);

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> or below it.</summary>
    public static bool IsSameOrInside(string inner, string outer)
    {
        string i = Path.GetFullPath(inner).TrimEnd('\\', '/') + "\\", o = Path.GetFullPath(outer).TrimEnd('\\', '/') + "\\";
        return i.StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }
}
