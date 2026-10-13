using System.Diagnostics;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Setup;

/// <summary>What the editor can tell about whether the game will load an editor copy (read-only; preferences.ini is never written, O10).</summary>
/// <param name="Linked">The copy is a direct child of &lt;game&gt;\mods\units, the only place the game loads local mods from.</param>
/// <param name="ModsUnitsFolder">&lt;game&gt;\mods\units, or null when the game folder is unknown.</param>
/// <param name="Title">The title the game's Mods menu shows for the copy (mod-info.txt), or null.</param>
/// <param name="Workshop">State of the Workshop item the copy was made from (only known for Workshop sources).</param>
/// <param name="OtherCopiesOfTheMod">Other folders in mods\units that show the same mod (they must be switched off in the game).</param>
/// <param name="Problems">What is wrong, in plain words (empty = linked correctly).</param>
/// <param name="Steps">What the user does in the game's Mods menu, in plain words.</param>
public sealed record GameLinkStatus(bool Linked, string? ModsUnitsFolder, string? Title, ModEnabledState Workshop, string? WorkshopId,
    IReadOnlyList<string> OtherCopiesOfTheMod, IReadOnlyList<string> Problems, IReadOnlyList<string> Steps);

/// <summary>
/// The link between an editor copy and the game (the owner's E3 test: edits "did not take in the game" because the copy sat in the editor's own folder,
/// which the game never reads). <see cref="Check"/> says whether the game will see the copy and what must be switched on / off; <see cref="CopyIntoGame"/>
/// puts a copy of an existing editor copy (with every change applied so far) into the game's mods folder. Nothing is ever overwritten.
/// </summary>
public static class GameLink
{
    public const string BaseTitle = "Depache's Mod Tribute";

    public static GameLinkStatus Check(string copyRoot, string? gameDir, string? sourceWorkshopId = null)
    {
        var problems = new List<string>();
        var steps = new List<string>();
        string root = Path.GetFullPath(copyRoot).TrimEnd('\\', '/');
        string? mods = gameDir == null ? null : SteamLocator.ModsUnitsFolder(gameDir);
        string? title = SteamLocator.ReadModTitle(root);
        bool linked = false;
        var others = new List<string>();
        var ws = ModEnabledState.Unknown;
        if (mods == null)
        {
            problems.Add("The game folder is not known, so the editor cannot tell whether the game loads this copy. Set it in Settings (Game folder).");
        }
        else
        {
            string? parent = Path.GetDirectoryName(root);
            linked = parent != null && FolderGuard.SamePath(parent, mods);
            if (!linked)
                problems.Add($"This copy is in {root}. Rusted Warfare only loads mods from {mods}, so the game does not see this copy and none of its changes. " +
                             "Use \"Put a copy in the game\" (Settings) or make a new copy in the game's mods folder.");
            else if (title == null)
                problems.Add("The copy has no readable title in mod-info.txt, so the game may not list it.");
            try
            {
                if (Directory.Exists(mods))
                    foreach (var d in Directory.EnumerateDirectories(mods))
                    {
                        if (FolderGuard.SamePath(d, root)) continue;
                        string? t = SteamLocator.ReadModTitle(d);
                        if (t != null && t.Contains(BaseTitle, StringComparison.OrdinalIgnoreCase)) others.Add($"{Path.GetFileName(d)}  (\"{t}\")");
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (sourceWorkshopId != null) ws = GamePreferences.WorkshopItemState(GamePreferences.ReadText(gameDir!), sourceWorkshopId);
        }
        if (linked)
        {
            steps.Add("Start Rusted Warfare and open Mods.");
            steps.Add($"Switch ON \"{title ?? Path.GetFileName(root)}\" (folder {Path.GetFileName(root)}).");
            steps.Add($"Switch OFF every other \"{BaseTitle}\": the one from the Steam Workshop and any other local copy. Two on at once load everything twice.");
            foreach (var o in others) steps.Add("   other copy in the mods folder: " + o);
            if (ws == ModEnabledState.Enabled) steps.Add($"   the Workshop item {sourceWorkshopId} is ON right now: switch it off.");
            steps.Add("Restart the game after every change in the editor (the game reads the mod files only when it starts).");
        }
        return new GameLinkStatus(linked, mods, title, ws, sourceWorkshopId, others, problems, steps);
    }

    /// <summary>The destination folder for an existing copy: mods\units\DepacheTribute_Custom_&lt;name&gt;, numbered when taken.</summary>
    public static string DestinationFor(string gameDir, string copyName)
    {
        string first = CopyMaker.GameModsFolderFor(gameDir, copyName);
        if (!Directory.Exists(first) && !File.Exists(first)) return first;
        for (int i = 2; i < 100; i++)
        {
            string n = first + "_" + i;
            if (!Directory.Exists(n) && !File.Exists(n)) return n;
        }
        throw new EditorRefusalException("DEST_TAKEN", $"Too many copies named like {first}. Choose a different copy name.");
    }

    /// <summary>
    /// Copies an existing editor copy (marker, settings, saved original values and every applied change included) into the game's mods folder, byte for
    /// byte, built in a hidden sibling folder, protected files re-hashed, then renamed. The source copy stays untouched. Returns the new folder.
    /// </summary>
    public static string CopyIntoGame(string copyRoot, string gameDir, IProgress<CopyProgress>? progress = null, CancellationToken cancel = default)
    {
        string src = FolderGuard.Check(copyRoot, allowGitTree: false);
        var marker = EditorCopyMarker.Load(src);
        string mods = SteamLocator.ModsUnitsFolder(gameDir);
        if (!Directory.Exists(mods)) throw new EditorRefusalException("GAME_MODS_MISSING", $"The game's mods folder {mods} does not exist. Check the game folder in Settings.");
        string dest = DestinationFor(gameDir, marker.Name);
        FolderGuard.CheckLocation(dest, allowGitTree: false, mustExist: false);
        if (FolderGuard.IsSameOrInside(dest, src) || FolderGuard.IsSameOrInside(src, dest))
            throw new EditorRefusalException("DEST_INSIDE_SOURCE", "The copy is already inside the game's mods folder.");
        var files = new DirectoryInfo(src).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList();
        long total = files.Sum(f => f.Length);
        long? free = CopyMaker.FreeBytes(dest);
        if (free is long fb && fb < total + total / 10 + 50L * 1024 * 1024)
            throw new EditorRefusalException("DEST_NO_SPACE", $"Not enough free space on {Path.GetPathRoot(dest)} for the copy ({total / (1024 * 1024) + 1} MB needed).");
        string partial = dest + CopyMaker.PartialSuffix + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(partial);
            foreach (var d in Directory.EnumerateDirectories(src, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                Directory.CreateDirectory(Path.Combine(partial, Path.GetRelativePath(src, d)));
            long done = 0; int n = 0;
            progress?.Report(new CopyProgress(0, files.Count, 0, total, "Copying into the game's mods folder"));
            foreach (var f in files)
            {
                cancel.ThrowIfCancellationRequested();
                string target = Path.Combine(partial, Path.GetRelativePath(src, f.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f.FullName, target, overwrite: false);
                done += f.Length; n++;
                if (n % 50 == 0 || n == files.Count) progress?.Report(new CopyProgress(n, files.Count, done, total, "Copying into the game's mods folder"));
            }
            progress?.Report(new CopyProgress(n, files.Count, done, total, "Checking the copy"));
            var support = EditorSupport.Load(partial);
            var locks = support.VerifyLockedFiles(partial);
            if (!locks.Ok) throw new IOException("the copied protected files differ: " + locks.Describe());
            EditorCopyMarker.Load(partial);
            Directory.Move(partial, dest);
            return dest;
        }
        catch (Exception e) when (e is not EditorRefusalException)
        {
            try { if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
            if (e is OperationCanceledException) throw new ApplyFailedException("Cancelled. The half-made copy was removed.", true, e);
            throw new ApplyFailedException($"Copying into the game failed ({e.Message}). The half-made copy was removed; nothing else was changed.", true, e);
        }
    }
}
