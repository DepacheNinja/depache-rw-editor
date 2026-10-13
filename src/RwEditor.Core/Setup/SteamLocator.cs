using System.Runtime.Versioning;
using System.Text;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Setup;

/// <summary>A node of Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf): either a string value or a block of children.</summary>
public sealed class VdfNode
{
    public VdfNode(string key, string? value) { Key = key; Value = value; }
    public string Key { get; }
    /// <summary>The string value, or null for a block.</summary>
    public string? Value { get; }
    public List<VdfNode> Children { get; } = new();
    public bool IsBlock => Value == null;

    /// <summary>First child with this key (case-insensitive, as Steam's own reader).</summary>
    public VdfNode? this[string key] => Children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reader for Valve's KeyValues text format as used by libraryfolders.vdf and appmanifest_*.acf: quoted (or bare) keys and values, { } blocks,
/// "//" comments, backslash escapes (\\ \" \n \t). Never throws on bad input: it returns what it could read and the problem.
/// </summary>
public static class Vdf
{
    public static VdfNode Parse(string text, out string? problem)
    {
        problem = null;
        var root = new VdfNode("", null);
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        int i = 0;
        string? pendingKey = null;
        while (true)
        {
            var tok = Next(text, ref i, out bool quoted, out string? err);
            if (err != null) { problem = err; break; }
            if (tok == null) break;
            if (!quoted && tok == "{")
            {
                if (pendingKey == null) { problem = $"'{{' without a key at offset {i}"; break; }
                var block = new VdfNode(pendingKey, null);
                stack.Peek().Children.Add(block);
                stack.Push(block);
                pendingKey = null;
            }
            else if (!quoted && tok == "}")
            {
                if (pendingKey != null) { problem = $"key '{pendingKey}' without a value at offset {i}"; break; }
                if (stack.Count == 1) { problem = $"unbalanced '}}' at offset {i}"; break; }
                stack.Pop();
            }
            else if (pendingKey == null) pendingKey = tok;
            else { stack.Peek().Children.Add(new VdfNode(pendingKey, tok)); pendingKey = null; }
        }
        if (problem == null && stack.Count != 1) problem = "missing '}' at the end";
        if (problem == null && pendingKey != null) problem = $"key '{pendingKey}' without a value at the end";
        return root;
    }

    static string? Next(string s, ref int i, out bool quoted, out string? err)
    {
        quoted = false; err = null;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            break;
        }
        if (i >= s.Length) return null;
        if (s[i] is '{' or '}') return s[i++].ToString();
        var sb = new StringBuilder();
        if (s[i] == '"')
        {
            quoted = true;
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => e });
                    i += 2;
                    continue;
                }
                sb.Append(s[i++]);
            }
            if (i >= s.Length) { err = "unterminated quoted string"; return null; }
            i++;   // closing quote
            return sb.ToString();
        }
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) sb.Append(s[i++]);
        return sb.ToString();
    }
}

/// <summary>One mod folder the wizard found: the installed mod (Workshop item or local folder) or an editor copy.</summary>
public sealed record ModCandidate(string Path, string Title, ModLocation Location, string? WorkshopId, bool HasEditorSupport, string? EditorSupportProblem,
    string? ModVersion, bool IsEditorCopy, string? CopyName)
{
    /// <summary>A folder the wizard may copy FROM: made for this editor (editor-support.json schema 1) and not itself an editor copy.</summary>
    public bool CanBeSource => HasEditorSupport && !IsEditorCopy;
}

public enum ModLocation { Workshop, GameModsFolder, Other }

/// <summary>
/// Finds the Rusted Warfare game folder and the installed mods (DESIGN_EDITOR 5.4 / E2 "Make my copy"). Facts used (VERIFIED on the owner's machine,
/// 2026-10-12): Steam's path is in HKCU\Software\Valve\Steam "SteamPath"; Steam libraries are listed in &lt;Steam&gt;\steamapps\libraryfolders.vdf ("path" of
/// each numbered block); Rusted Warfare is app 647960 with appmanifest_647960.acf "installdir" = "Rusted Warfare"; Workshop items are in
/// &lt;library&gt;\steamapps\workshop\content\647960\&lt;item id&gt;; the game loads local mods from &lt;game&gt;\mods\units (lastrun.log "loading mod custom units
/// at:/SD/mods/units"). The HKLM keys and the default install path are extra fallbacks only.
/// </summary>
public static class SteamLocator
{
    public const string AppId = "647960";
    public const string DefaultInstallDir = "Rusted Warfare";

    /// <summary>Library root paths ("path" of every numbered block) in libraryfolders.vdf, in file order, de-duplicated.</summary>
    public static IReadOnlyList<string> LibrariesFromVdf(string vdfText)
    {
        var root = Vdf.Parse(vdfText, out _);
        var lf = root["libraryfolders"] ?? root["LibraryFolders"];
        var list = new List<string>();
        if (lf == null) return list;
        foreach (var c in lf.Children)
        {
            // new format: "0" { "path" "D:\\SteamLibrary" ... }; old format: "1" "D:\\SteamLibrary"
            string? p = c.IsBlock ? c["path"]?.Value : (int.TryParse(c.Key, out _) ? c.Value : null);
            if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        return list;
    }

    /// <summary>installdir of an appmanifest_*.acf ("AppState" { "installdir" "..." }), or null.</summary>
    public static string? InstallDirFromManifest(string acfText)
    {
        var root = Vdf.Parse(acfText, out _);
        return root["AppState"]?["installdir"]?.Value;
    }

    /// <summary>A Rusted Warfare game folder: holds game-lib.jar and assets\units (both exist in the Steam install).</summary>
    public static bool IsGameFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(System.IO.Path.Combine(dir, "game-lib.jar")) && Directory.Exists(System.IO.Path.Combine(dir, "assets", "units"));

    public static string ModsUnitsFolder(string gameDir) => System.IO.Path.Combine(gameDir, "mods", "units");

    /// <summary>Steam install folders from the registry (Windows only) plus the default path, existing ones only.</summary>
    public static IReadOnlyList<string> SteamRoots()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows()) list.AddRange(RegistrySteamPaths());
        list.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return list.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<string> RegistrySteamPaths()
    {
        var found = new List<string>();
        void Try(Microsoft.Win32.RegistryKey hive, string key, string name)
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(name) is string s && s.Length > 0) found.Add(s);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        Try(Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        Try(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        Try(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        return found;
    }

    static string Normalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p.Replace('/', '\\')).TrimEnd('\\'); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    /// <summary>Every Steam library (the Steam folder itself plus libraryfolders.vdf entries) of the given Steam roots, existing ones only.</summary>
    public static IReadOnlyList<string> Libraries(IEnumerable<string> steamRoots)
    {
        var libs = new List<string>();
        foreach (var root in steamRoots)
        {
            libs.Add(root);
            string vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try { if (File.Exists(vdf)) libs.AddRange(LibrariesFromVdf(File.ReadAllText(vdf, Encoding.UTF8))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return libs.Select(Normalize).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Rusted Warfare game folders in the given libraries (appmanifest installdir, else the default folder name), verified with <see cref="IsGameFolder"/>.</summary>
    public static IReadOnlyList<string> GameFolders(IEnumerable<string> libraries)
    {
        var list = new List<string>();
        foreach (var lib in libraries)
        {
            string common = System.IO.Path.Combine(lib, "steamapps", "common");
            string? install = null;
            string acf = System.IO.Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
            try { if (File.Exists(acf)) install = InstallDirFromManifest(File.ReadAllText(acf, Encoding.UTF8)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var name in new[] { install, DefaultInstallDir })
            {
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || name.Contains("..", StringComparison.Ordinal)) continue;
                string dir = System.IO.Path.Combine(common, name);
                if (IsGameFolder(dir) && !list.Contains(dir, StringComparer.OrdinalIgnoreCase)) list.Add(dir);
            }
        }
        return list;
    }

    /// <summary>Auto-detect: registry / default Steam folder -> libraries -> game folders.</summary>
    public static IReadOnlyList<string> DetectGameFolders() => GameFolders(Libraries(SteamRoots()));

    /// <summary>The Steam library that holds a game folder (&lt;lib&gt;\steamapps\common\&lt;game&gt;), or null.</summary>
    public static string? LibraryOfGame(string gameDir)
    {
        var common = Directory.GetParent(System.IO.Path.GetFullPath(gameDir).TrimEnd('\\'));
        var steamapps = common?.Parent;
        if (common == null || steamapps == null || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) || !steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return null;
        return steamapps.Parent?.FullName;
    }

    /// <summary>
    /// Every mod folder the wizard can offer: Workshop items of this game in every given library, and the folders in &lt;game&gt;\mods\units. Only the
    /// mod-info.txt title, editor-support.json and rweditor-copy.json are read (small files).
    /// </summary>
    public static IReadOnlyList<ModCandidate> FindMods(string? gameDir, IEnumerable<string> libraries)
    {
        var list = new List<ModCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries)
        {
            string ws = System.IO.Path.Combine(lib, "steamapps", "workshop", "content", AppId);
            foreach (var d in SafeDirs(ws)) if (seen.Add(d)) list.Add(Describe(d, ModLocation.Workshop));
        }
        if (gameDir != null)
            foreach (var d in SafeDirs(ModsUnitsFolder(gameDir))) if (seen.Add(d)) list.Add(Describe(d, ModLocation.GameModsFolder));
        return list;
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>What the wizard shows for one folder. Never throws.</summary>
    public static ModCandidate Describe(string dir, ModLocation? location = null)
    {
        string full = Normalize(dir);
        var loc = location ?? (FolderGuard.IsWorkshopPath(full) ? ModLocation.Workshop : ModLocation.Other);
        string? wsId = loc == ModLocation.Workshop ? System.IO.Path.GetFileName(full) : null;
        string title = ReadModTitle(full) ?? System.IO.Path.GetFileName(full);
        bool hasSupport = false; string? problem = null, version = null;
        if (File.Exists(System.IO.Path.Combine(full, EditorSupport.FileName)))
        {
            try { var es = EditorSupport.Load(full); hasSupport = true; version = es.ModVersion; }
            catch (EditorRefusalException e) { problem = e.Message; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = e.Message; }
        }
        else problem = "this version of the mod has no editor support (editor-support.json); it is older than v1.0.6 or not this mod";
        var marker = EditorCopyMarker.TryLoad(full, out _);
        return new ModCandidate(full, title, loc, wsId, hasSupport, problem, version, marker != null, marker?.Name);
    }

    /// <summary>The [mod] title: of a mod-info.txt (first one), or null.</summary>
    public static string? ReadModTitle(string modDir)
    {
        string p = System.IO.Path.Combine(modDir, "mod-info.txt");
        try
        {
            if (!File.Exists(p)) return null;
            return ModInfo.Title(File.ReadAllText(p, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>mod-info.txt helpers (the game shows the [mod] title in its Mods menu; lastrun.log "Loading mod at:... (name:&lt;title&gt;)").</summary>
public static class ModInfo
{
    /// <summary>The value of the first "title:" line inside the [mod] section, trimmed; null when there is none.</summary>
    public static string? Title(string text)
    {
        var (line, _) = FindTitleLine(text);
        if (line < 0) return null;
        var lines = text.Split('\n');
        string l = lines[line].TrimEnd('\r');
        return l[(l.IndexOf(':') + 1)..].Trim();
    }

    /// <summary>The same text with the [mod] title replaced (line ending and everything else kept), or null when there is no title line.</summary>
    public static string? WithTitle(string text, string newTitle)
    {
        if (newTitle.Contains('\n') || newTitle.Contains('\r')) throw new ArgumentException("a title is one line", nameof(newTitle));
        var (line, _) = FindTitleLine(text);
        if (line < 0) return null;
        var lines = text.Split('\n');
        string l = lines[line];
        bool cr = l.EndsWith('\r');
        int colon = l.IndexOf(':');
        lines[line] = l[..(colon + 1)] + " " + newTitle + (cr ? "\r" : "");
        return string.Join('\n', lines);
    }

    static (int Line, string Section) FindTitleLine(string text)
    {
        var lines = text.Split('\n');
        string section = "";
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim().TrimStart('\uFEFF');
            if (t.StartsWith('[') && t.EndsWith(']')) { section = t[1..^1].Trim(); continue; }
            if (section.Equals("mod", StringComparison.OrdinalIgnoreCase) && t.StartsWith("title", StringComparison.OrdinalIgnoreCase))
            {
                int c = t.IndexOf(':');
                if (c > 0 && t[..c].Trim().Equals("title", StringComparison.OrdinalIgnoreCase)) return (i, section);
            }
        }
        return (-1, "");
    }
}

public enum ModEnabledState { Enabled, Disabled, NotListed, Unknown }

/// <summary>
/// Read-only look at the game's preferences.ini "modSettings:" line (DESIGN_EDITOR 5.4: the editor never WRITES preferences.ini, O10). Format seen on the
/// owner's machine (2026-10-12): comma-separated "&lt;id&gt;|&lt;hash or id&gt;|enabled|disabled" entries, Workshop items as "sw&lt;item id&gt;". Only Workshop
/// entries are interpreted; local-folder entries use a shortened name and a hash whose rule is not known (O10), so they are never matched.
/// </summary>
public static class GamePreferences
{
    public static ModEnabledState WorkshopItemState(string? preferencesText, string workshopId)
    {
        if (preferencesText == null) return ModEnabledState.Unknown;
        string? line = preferencesText.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("modSettings:", StringComparison.Ordinal));
        if (line == null) return ModEnabledState.Unknown;
        foreach (var entry in line["modSettings:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|');
            if (parts.Length < 3 || !parts[0].Equals("sw" + workshopId, StringComparison.Ordinal)) continue;
            return parts[^1].Equals("enabled", StringComparison.OrdinalIgnoreCase) ? ModEnabledState.Enabled
                : parts[^1].Equals("disabled", StringComparison.OrdinalIgnoreCase) ? ModEnabledState.Disabled : ModEnabledState.Unknown;
        }
        return ModEnabledState.NotListed;
    }

    public static string? ReadText(string gameDir)
    {
        string p = System.IO.Path.Combine(gameDir, "preferences.ini");
        try { return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
