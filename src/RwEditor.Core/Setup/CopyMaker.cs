using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Setup;

/// <summary>What the wizard knows about the folder to copy FROM.</summary>
public sealed record CopySource(string Path, string Title, string ModVersion, int FileCount, long TotalBytes);

/// <summary>Progress of a copy: files done of total, bytes done of total, and what is happening in plain words.</summary>
public sealed record CopyProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string Step);

public sealed record CopyResult(string Folder, EditorCopyMarker Marker, int Files, long Bytes, TimeSpan Duration, string? NewTitle);

/// <summary>
/// "Make my copy" (DESIGN_EDITOR 5.4, E2): checks a source and a destination and copies the installed mod into a folder the user controls, then writes the
/// editor-copy marker (<see cref="EditorCopyMarker"/>) that the folder guard requires before any edit. Rules:
/// - the source must be a mod made for this editor (editor-support.json schema 1) and must not itself be an editor copy;
/// - the destination may not be a drive root, a Steam Workshop folder, a *_pristine / baseline_* folder, inside a git work tree, inside the source, or hold
///   the source; it must not exist yet or be an EMPTY folder - an existing folder with anything in it is never overwritten (no replace mode at all);
/// - every file is copied byte for byte except the Workshop's steam.dat (a copy is a separate local mod, DOUBLE_SPAWN_INVESTIGATION E2/E3) and any old
///   marker; mod-info.txt gets the title "[Custom &lt;name&gt;] &lt;title&gt;" so the game's Mods menu can tell the copy from the original;
/// - the copy is built in a hidden sibling folder (&lt;dest&gt;.rwe-partial-&lt;id&gt;), its locked files are re-hashed against editor-support.json, the copy's
///   original values are saved (E3, <see cref="CopyBaseline"/>, rweditor-baseline.bin), the marker is written last, and only then is the folder renamed to the destination; any failure deletes the partial folder, so a half copy never looks like a copy.
/// Nothing in the game folder is changed unless the user picked a destination there.
/// </summary>
public static class CopyMaker
{
    public const string PartialSuffix = ".rwe-partial-";
    public const string DefaultName = "My copy";
    static readonly Regex NameRx = new(@"^[A-Za-z0-9 \-_.()]{1,40}$", RegexOptions.CultureInvariant);

    /// <summary>%LOCALAPPDATA%\DepacheRwEditor\mymod - the default destination (owner's E2 brief).</summary>
    public static string DefaultFolder => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "mymod");

    /// <summary>&lt;game&gt;\mods\units\DepacheTribute_Custom_&lt;name&gt; - the destination when the user wants the game to load the copy.</summary>
    public static string GameModsFolderFor(string gameDir, string name) =>
        Path.Combine(SteamLocator.ModsUnitsFolder(gameDir), "DepacheTribute_Custom_" + FolderSafe(name));

    static string FolderSafe(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "Copy" : sb.ToString();
    }

    /// <summary>Copy name rule: 1..40 of letters, digits, space and - _ . ( ) (it goes into the mod title, which is a single ini line).</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give your copy a name (for example: My copy)."
        : !NameRx.IsMatch(name.Trim()) ? "Use 1 to 40 letters, digits, spaces or - _ . ( ) for the name."
        : null;

    public static string TitleFor(string name, string originalTitle) => $"[Custom {name.Trim()}] {originalTitle}";

    // ------------------------------------------------------------------------------------------------------------------- checks
    /// <summary>Checks the folder to copy from. Throws EditorRefusalException with a plain-words message.</summary>
    public static CopySource CheckSource(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new EditorRefusalException("SOURCE_MISSING", "Choose the installed mod to copy.");
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new EditorRefusalException("SOURCE_INVALID", $"'{folder}' is not a valid folder ({e.Message})."); }
        if (!Directory.Exists(full)) throw new EditorRefusalException("SOURCE_MISSING", $"The folder {full} does not exist.");
        if (EditorCopyMarker.Exists(full))
            throw new EditorRefusalException("SOURCE_IS_COPY", $"{full} is already a copy made by this editor. Use \"Open my existing copy\" instead of copying it again.");
        EditorSupport es;
        try { es = EditorSupport.Load(full); }
        catch (EditorRefusalException e) when (e.Code == "NO_EDITOR_SUPPORT")
        { throw new EditorRefusalException("SOURCE_NOT_SUPPORTED", $"{full} is not a version of Depache's Mod Tribute that this editor can work with (no editor-support.json; v1.0.6 or newer is needed)."); }
        int files = 0; long bytes = 0;
        try { foreach (var f in SourceFiles(full)) { files++; bytes += f.Length; } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new EditorRefusalException("SOURCE_UNREADABLE", $"Some files of {full} cannot be read ({e.Message}), so it cannot be copied."); }
        string title = SteamLocator.ReadModTitle(full) ?? Path.GetFileName(full);
        return new CopySource(full, title, es.ModVersion, files, bytes);
    }

    /// <summary>Checks a destination for a NEW copy of <paramref name="sourceFolder"/>. Returns the full path. Throws EditorRefusalException in plain words.</summary>
    public static string CheckDestination(string sourceFolder, string destination)
    {
        string full = FolderGuard.CheckLocation(destination, allowGitTree: false, mustExist: false);
        string src = Path.GetFullPath(sourceFolder).TrimEnd('\\', '/');
        if (FolderGuard.SamePath(full, src)) throw new EditorRefusalException("DEST_IS_SOURCE", "The copy cannot go into the folder it is copied from. Choose another folder.");
        if (FolderGuard.IsSameOrInside(full, src)) throw new EditorRefusalException("DEST_INSIDE_SOURCE", "The copy cannot go inside the mod it is copied from. Choose a folder outside it.");
        if (FolderGuard.IsSameOrInside(src, full)) throw new EditorRefusalException("DEST_HOLDS_SOURCE", "That folder contains the mod you are copying. Choose an empty or new folder.");
        if (File.Exists(full)) throw new EditorRefusalException("DEST_IS_FILE", $"{full} is a file, not a folder. Choose another name.");
        if (Directory.Exists(full))
        {
            bool empty;
            try { empty = !Directory.EnumerateFileSystemEntries(full).Any(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new EditorRefusalException("DEST_UNREADABLE", $"{full} cannot be read ({e.Message})."); }
            if (!empty)
                throw new EditorRefusalException(EditorCopyMarker.Exists(full) ? "DEST_HAS_COPY" : "DEST_NOT_EMPTY",
                    EditorCopyMarker.Exists(full)
                        ? $"{full} already holds a copy made by this editor. Open that copy instead (\"Open my existing copy\"), or choose a new folder: nothing is ever overwritten."
                        : $"{full} already has files in it. Choose a new or empty folder: the editor never overwrites a folder.");
        }
        string? parent = Path.GetDirectoryName(full);
        if (parent == null) throw new EditorRefusalException("FOLDER_ROOT", "Choose a folder, not a drive.");
        string? drive = Path.GetPathRoot(full);
        if (drive == null || !Directory.Exists(drive)) throw new EditorRefusalException("DEST_NO_DRIVE", $"The drive of {full} is not available.");
        return full;
    }

    /// <summary>Checks an EXISTING copy the user wants to open for editing: the folder guard (marker included) and editor-support. Returns the full path.</summary>
    public static string CheckExistingCopy(string folder) => FolderGuard.Check(folder, allowGitTree: false);

    /// <summary>Free space on the destination's drive, or null when it cannot be read.</summary>
    public static long? FreeBytes(string destination)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    static IEnumerable<FileInfo> SourceFiles(string root)
    {
        var di = new DirectoryInfo(root);
        foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            string rel = Path.GetRelativePath(root, f.FullName);
            if (rel.Equals("steam.dat", StringComparison.OrdinalIgnoreCase) || CopyFiles.EditorOwned.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------- copy
    /// <summary>
    /// Makes the copy (see the class summary). Files are copied one after another (no parallel disk load). Throws EditorRefusalException (nothing
    /// written) or ApplyFailedException (Restored = the partial folder was removed again).
    /// </summary>
    public static CopyResult MakeCopy(string sourceFolder, string destination, string name, string editorVersion,
        IProgress<CopyProgress>? progress = null, CancellationToken cancel = default, Func<DateTime>? utcNow = null)
    {
        var sw = Stopwatch.StartNew();
        if (NameProblem(name) is string np) throw new EditorRefusalException("COPY_NAME", np);
        name = name.Trim();
        var source = CheckSource(sourceFolder);
        string dest = CheckDestination(source.Path, destination);
        var support = EditorSupport.Load(source.Path);
        var srcLocks = support.VerifyLockedFiles(source.Path);
        if (!srcLocks.Ok)
            throw new EditorRefusalException("SOURCE_CHANGED", "The installed mod's protected files do not match the mod's own list, so it cannot be copied safely: " + srcLocks.Describe()
                + ". Re-install or update the mod (Steam: verify / re-subscribe), then try again.");
        long? free = FreeBytes(dest);
        if (free is long fb && fb < source.TotalBytes + source.TotalBytes / 10 + 50L * 1024 * 1024)
            throw new EditorRefusalException("DEST_NO_SPACE", $"Not enough free space on {Path.GetPathRoot(dest)}: the copy needs about {Mb(source.TotalBytes)} MB, {Mb(fb)} MB are free.");

        string parent = Path.GetDirectoryName(dest)!;
        string partial = dest + PartialSuffix + Guid.NewGuid().ToString("N")[..8];
        bool createdParent = false;
        try
        {
            if (!Directory.Exists(parent)) { Directory.CreateDirectory(parent); createdParent = true; }
            Directory.CreateDirectory(partial);
            // the folder tree first, empty folders included, so the copy has exactly the source's layout
            foreach (var d in Directory.EnumerateDirectories(source.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                Directory.CreateDirectory(Path.Combine(partial, Path.GetRelativePath(source.Path, d)));
            var files = SourceFiles(source.Path).ToList();
            long done = 0; int n = 0;
            progress?.Report(new CopyProgress(0, files.Count, 0, source.TotalBytes, "Copying the mod files"));
            foreach (var f in files)
            {
                cancel.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(source.Path, f.FullName);
                string target = Path.Combine(partial, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f.FullName, target, overwrite: false);
                done += f.Length; n++;
                if (n % 50 == 0 || n == files.Count) progress?.Report(new CopyProgress(n, files.Count, done, source.TotalBytes, "Copying the mod files"));
            }

            progress?.Report(new CopyProgress(n, files.Count, done, source.TotalBytes, "Checking the copy"));
            string? newTitle = null;
            string modInfo = Path.Combine(partial, "mod-info.txt");
            if (File.Exists(modInfo))
            {
                byte[] raw = File.ReadAllBytes(modInfo);
                bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                string text = Encoding.UTF8.GetString(raw.AsSpan(bom ? 3 : 0));
                string? orig = ModInfo.Title(text);
                if (orig != null)
                {
                    newTitle = TitleFor(name, orig);
                    string patched = ModInfo.WithTitle(text, newTitle)!;
                    byte[] body = new UTF8Encoding(false).GetBytes(patched);
                    byte[] outBytes = bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
                    File.WriteAllBytes(modInfo, outBytes);   // one write (RAM-buffer rule)
                }
            }
            var copyLocks = support.VerifyLockedFiles(partial);
            if (!copyLocks.Ok) throw new IOException("the copied protected files differ from the original: " + copyLocks.Describe());

            var marker = EditorCopyMarker.Create(name, source.Path, support, editorVersion, (utcNow ?? (() => DateTime.UtcNow))());
            // E3: the copy's original values for "put everything back" (every stat-writable file, one zip written once), BEFORE the marker
            progress?.Report(new CopyProgress(n, files.Count, done, source.TotalBytes, "Saving the original values"));
            CopyBaseline.Write(partial, support.LockedGlobs, marker.CopyId, marker.CreatedUtc);
            File.WriteAllBytes(Path.Combine(partial, EditorCopyMarker.FileName), marker.ToBytes());

            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: false);   // checked empty above; a non-empty folder throws here and is never touched
            Directory.Move(partial, dest);
            progress?.Report(new CopyProgress(n, files.Count, done, source.TotalBytes, "Done"));
            return new CopyResult(dest, marker, n, done, sw.Elapsed, newTitle);
        }
        catch (Exception e) when (e is not EditorRefusalException)
        {
            bool cleaned = TryDelete(partial);
            if (createdParent) { try { if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } }
            if (e is OperationCanceledException) throw new ApplyFailedException("The copy was cancelled. " + (cleaned ? "The half-made copy was removed." : $"The half-made folder {partial} could not be removed; you can delete it."), cleaned, e);
            throw new ApplyFailedException($"The copy failed ({e.Message}). " + (cleaned ? "The half-made copy was removed; nothing else was changed." : $"The half-made folder {partial} could not be removed; you can delete it."), cleaned, e);
        }
    }

    static bool TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static long Mb(long bytes) => (bytes + 1024 * 1024 - 1) / (1024 * 1024);
}
