using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RwEditor.Core.Generation;
using RwEditor.Core.Settings;

namespace RwEditor.Core.Options;

/// <summary>The state of one catalog number in a copy: where it stands in the shipped mod and now.</summary>
public sealed record SettingState(OptionSettingDef Def, bool Available, string? Reason, string? Shipped, string? Current);

/// <summary>The text lines of one option, as shipped and now (a part is null when the option has no such line).</summary>
public sealed record TextState(string Tag, string? ShippedName, string? CurrentName, string? ShippedDescription, string? CurrentDescription);

/// <summary>What an apply would change in the locked option files: the new bytes of each file that differs from what is on disk, and the recorded line edits.</summary>
public sealed record OptionPatch(IReadOnlyDictionary<string, byte[]> NewBytes, IReadOnlyDictionary<string, byte[]?> CurrentBytes, IReadOnlyList<OptionEdit> Edits)
{
    public bool Changes => NewBytes.Count > 0;
}

/// <summary>
/// The only code that rewrites locked files (E5b, PH-101). It works from the SHIPPED text, which it rebuilds from the current file by undoing the
/// recorded line edits (each undo checks the line is exactly what the editor wrote, so a file changed by hand is refused), applies what the user chose
/// (<see cref="OptionTuning"/>) to those shipped lines through the whitelist <see cref="OptionCatalog"/>, and records every line that differs. Putting
/// a value back, changing it again and "put everything back" are therefore the same operation with different choices, and a copy with no choices
/// is byte-identical to the shipped files.
/// </summary>
public sealed class OptionModel
{
    public OptionCatalog Catalog { get; }
    readonly Dictionary<string, OptionFile> _shipped;
    readonly Dictionary<string, OptionFile> _current;
    readonly Dictionary<string, byte[]> _currentBytes;
    readonly List<string> _missing;

    OptionModel(OptionCatalog catalog, Dictionary<string, OptionFile> shipped, Dictionary<string, OptionFile> current, Dictionary<string, byte[]> bytes, List<string> missing)
    {
        Catalog = catalog; _shipped = shipped; _current = current; _currentBytes = bytes; _missing = missing;
    }

    /// <summary>Reads the catalog's files through <paramref name="read"/> (null = file absent) and rebuilds their shipped text from the recorded edits.</summary>
    public static OptionModel Load(Func<string, byte[]?> read, IReadOnlyList<OptionEdit> recorded, OptionCatalog? catalog = null)
    {
        catalog ??= OptionCatalog.Embedded;
        var allowed = catalog.TargetFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var e in recorded)
            if (!allowed.Contains(e.File)) throw new EditorRefusalException("OPTION_EDIT_FILE", $"the settings record an edit of {e.File}, which the editor never changes");
        var shipped = new Dictionary<string, OptionFile>(StringComparer.Ordinal);
        var current = new Dictionary<string, OptionFile>(StringComparer.Ordinal);
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var rel in catalog.TargetFiles)
        {
            var b = read(rel);
            if (b == null) { missing.Add(rel); continue; }
            bytes[rel] = b;
            current[rel] = OptionFile.Parse(rel, b);
            var s = current[rel].Clone();
            foreach (var e in recorded.Where(e => e.File == rel))
            {
                if (e.Line < 1 || e.Line > s.Count || s[e.Line - 1] != e.NewLine)
                    throw new EditorRefusalException("OPTION_EDITS_STALE", $"{rel} line {e.Line} is not what the editor wrote there; the file was changed outside the editor, so its options cannot be edited here");
                s[e.Line - 1] = e.OldLine;
            }
            shipped[rel] = s;
        }
        foreach (var e in recorded)
            if (missing.Contains(e.File)) throw new EditorRefusalException("OPTION_EDITS_STALE", $"{e.File} is missing but the settings record an edit of it");
        return new OptionModel(catalog, shipped, current, bytes, missing);
    }

    public static OptionModel Load(string modRoot, IReadOnlyList<OptionEdit> recorded, OptionCatalog? catalog = null) =>
        Load(rel => { string p = Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(p) ? File.ReadAllBytes(p) : null; }, recorded, catalog);

    /// <summary>The shipped bytes of a file (the current bytes with the recorded edits undone), or null when it is absent.</summary>
    public byte[]? ShippedBytes(string rel) => _shipped.TryGetValue(rel, out var f) ? f.ToBytes() : null;

    /// <summary>
    /// The file's bytes with the given recorded edits undone, or null when a recorded line is not exactly what the editor wrote (the file was changed
    /// otherwise). Used by the locked-file check: a locked file is fine when it equals the manifest after its recorded edits are undone.
    /// </summary>
    public static byte[]? TryUndo(string rel, byte[] current, IEnumerable<OptionEdit> edits)
    {
        OptionFile f;
        try { f = OptionFile.Parse(rel, current); } catch (EditorRefusalException) { return null; }
        foreach (var e in edits.Where(e => e.File == rel))
        {
            if (e.Line < 1 || e.Line > f.Count || f[e.Line - 1] != e.NewLine) return null;
            f[e.Line - 1] = e.OldLine;
        }
        return f.ToBytes();
    }

    // ------------------------------------------------------------------------------------------------------------ what the page shows
    public IReadOnlyList<SettingState> Settings => Catalog.Settings.Select(Describe).ToList();

    SettingState Describe(OptionSettingDef d)
    {
        var first = d.Targets[0];
        if (!_shipped.TryGetValue(first.File, out var sf)) return new SettingState(d, false, $"{first.File} is not in this copy", null, null);
        string? why = Check(d, sf, out var shipped);
        if (why != null) return new SettingState(d, false, why, null, null);
        string? cur = Check(d, _current[first.File], out var now) == null ? now : null;
        return new SettingState(d, true, null, shipped, cur);
    }

    /// <summary>The value of a setting's first target in one file, or the reason it cannot be found exactly once.</summary>
    static string? Check(OptionSettingDef d, OptionFile file, out string? value)
    {
        value = null;
        var t = d.Targets[0];
        var hits = file.FindAll(t.Section, t.Key);
        if (hits.Count != 1) return $"[{t.Section}] {t.Key} is not found exactly once in {t.File}";
        var v = OptionFile.Split(file[hits[0]], t.Key).Value;
        var ms = t.Pattern.Matches(v);
        if (ms.Count != 1) return $"the number was not found exactly once in the {t.Key} line of {t.File}";
        value = ms[0].Groups["v"].Value;
        return null;
    }

    public TextState? Text(string tag, string onSection)
    {
        if (!_shipped.TryGetValue(Catalog.TextFile, out var sf)) return null;
        var cf = _current[Catalog.TextFile];
        return new TextState(tag, sf.Value(onSection, Catalog.NameKey), cf.Value(onSection, Catalog.NameKey),
            sf.Value(onSection, Catalog.DescriptionKey), cf.Value(onSection, Catalog.DescriptionKey));
    }

    // ------------------------------------------------------------------------------------------------------------ checks
    /// <summary>Problems with what the user chose. <paramref name="onSections"/> maps each option tag of the selector to its [action_xxxOn] section.</summary>
    public IReadOnlyList<Finding> Validate(OptionTuning tuning, IReadOnlyDictionary<string, string> onSections)
    {
        var f = new List<Finding>();
        foreach (var (id, value) in tuning.Values)
        {
            var d = Catalog.Find(id);
            if (d == null) { f.Add(new(Severity.Error, "OPTION_SETTING_UNKNOWN", $"'{id}' is not a setting this editor can change")); continue; }
            var st = Settings.First(s => s.Def.Id == id);
            if (!st.Available) { f.Add(new(Severity.Error, "OPTION_SETTING_UNAVAILABLE", $"{d.Label} cannot be changed in this copy: {st.Reason}")); continue; }
            if (!Regex.IsMatch(value, @"^[0-9]{1,9}$", RegexOptions.CultureInvariant) || (value.Length > 1 && value[0] == '0'))
            { f.Add(new(Severity.Error, "OPTION_VALUE_INVALID", $"{d.Label}: '{value}' is not a whole number (digits only, no sign or decimals)")); continue; }
            long n = long.Parse(value, CultureInfo.InvariantCulture);
            if (n < d.Min || n > d.Max) f.Add(new(Severity.Error, "OPTION_VALUE_RANGE", $"{d.Label}: {n} is outside {d.Min} to {d.Max}"));
            else if (d.WarnMax is long w && n > w) f.Add(new(Severity.Warning, "OPTION_VALUE_HIGH", $"{d.Label}: {n} is very high (the usual range ends near {w}); check it in a match"));
        }
        foreach (var (tag, t) in tuning.Texts)
        {
            if (!onSections.ContainsKey(tag)) { f.Add(new(Severity.Error, "OPTION_UNKNOWN", $"option '{tag}' does not exist in this mod's selector")); continue; }
            var ts = Text(tag, onSections[tag]);
            if (t.Name != null)
            {
                if (ts?.ShippedName == null) f.Add(new(Severity.Error, "OPTION_NAME_UNAVAILABLE", $"the name of '{tag}' cannot be changed in this copy"));
                else if (!NameRules.IsValidName(t.Name) || t.Name.Length > Catalog.MaxNameLength) f.Add(new(Severity.Error, "OPTION_NAME_INVALID", $"'{tag}': the name '{t.Name}' must be 1-{Catalog.MaxNameLength} letters, digits, spaces and - _ . ! ? + ' & ( )"));
            }
            if (t.Description != null)
            {
                if (ts?.ShippedDescription == null) f.Add(new(Severity.Error, "OPTION_TEXT_UNAVAILABLE", $"the description of '{tag}' cannot be changed in this copy"));
                else if (DescriptionRules.Problem(t.Description, Catalog) is string p) f.Add(new(Severity.Error, "OPTION_DESCRIPTION_INVALID", $"'{tag}': {p}"));
            }
        }
        return f;
    }

    /// <summary>The choices without anything that equals the shipped text or number (so equal states serialise equally).</summary>
    public OptionTuning Normalize(OptionTuning tuning, IReadOnlyDictionary<string, string> onSections)
    {
        var n = tuning.Clone();
        foreach (var st in Settings)
            if (n.Values.TryGetValue(st.Def.Id, out var v) && st.Available && st.Shipped == v) n.Values.Remove(st.Def.Id);
        foreach (var (tag, t) in n.Texts.ToList())
        {
            if (!onSections.TryGetValue(tag, out var sec)) continue;
            var ts = Text(tag, sec);
            if (ts != null && t.Name == ts.ShippedName) t.Name = null;
            if (ts != null && t.Description == ts.ShippedDescription) t.Description = null;
            if (t.IsEmpty) n.Texts.Remove(tag);
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------------------------ plan
    /// <summary>
    /// What the files must look like for <paramref name="tuning"/>: the shipped lines with the choices applied, every changed line recorded, and the bytes
    /// of each file that differs from the disk. Throws EditorRefusalException when a chosen target cannot be found (validate first).
    /// </summary>
    public OptionPatch Plan(OptionTuning tuning, IReadOnlyDictionary<string, string> onSections)
    {
        var work = _shipped.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal);
        var what = new Dictionary<(string, int), (string Section, string Key)>();

        foreach (var (tag, t) in tuning.Texts)
        {
            if (!onSections.TryGetValue(tag, out var sec)) throw new EditorRefusalException("OPTION_UNKNOWN", $"option '{tag}' does not exist in this mod's selector");
            if (!work.TryGetValue(Catalog.TextFile, out var tf)) throw new EditorRefusalException("OPTION_FILE_MISSING", Catalog.TextFile + " is not in this copy");
            if (t.Name != null) ReplaceValue(tf, sec, Catalog.NameKey, v => t.Name, what, required: true);
            if (t.Description != null) ReplaceValue(tf, sec, Catalog.DescriptionKey, v => t.Description, what, required: true);
        }
        foreach (var (id, value) in tuning.Values)
        {
            var d = Catalog.Find(id) ?? throw new EditorRefusalException("OPTION_SETTING_UNKNOWN", $"'{id}' is not a setting this editor can change");
            for (int k = 0; k < d.Targets.Count; k++)
            {
                var t = d.Targets[k];
                if (!work.TryGetValue(t.File, out var file)) { if (k == 0) throw new EditorRefusalException("OPTION_FILE_MISSING", t.File + " is not in this copy"); continue; }
                ReplaceValue(file, t.Section, t.Key, v =>
                {
                    var ms = t.Pattern.Matches(v);
                    if (ms.Count != 1) return null;
                    var g = ms[0].Groups["v"];
                    return v[..g.Index] + value + v[(g.Index + g.Length)..];
                }, what, required: k == 0);
            }
        }

        var edits = new List<OptionEdit>();
        var newBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cur = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var rel in work.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var s = _shipped[rel]; var w = work[rel];
            for (int i = 0; i < w.Count; i++)
            {
                if (w[i] == s[i]) continue;
                var (sec, key) = what.TryGetValue((rel, i), out var x) ? x : ("", "");
                edits.Add(new OptionEdit { File = rel, Line = i + 1, Section = sec, Key = key, OldLine = s[i], NewLine = w[i] });
            }
            var nb = w.ToBytes();
            cur[rel] = _currentBytes[rel];
            if (!nb.AsSpan().SequenceEqual(_currentBytes[rel])) newBytes[rel] = nb;
        }
        return new OptionPatch(newBytes, cur, edits);
    }

    /// <summary>Replaces the value part of the one "key: value" line of a section. <paramref name="change"/> returns the new value, or null to leave it.</summary>
    static void ReplaceValue(OptionFile file, string section, string key, Func<string, string?> change, Dictionary<(string, int), (string, string)> what, bool required)
    {
        var hits = file.FindAll(section, key);
        if (hits.Count != 1)
        {
            if (required) throw new EditorRefusalException("OPTION_LINE_NOT_FOUND", $"[{section}] {key} is not found exactly once in {file.Rel}");
            return;
        }
        int i = hits[0];
        var (pre, value, post) = OptionFile.Split(file[i], key);
        string? nv = change(value);
        if (nv == null)
        {
            if (required) throw new EditorRefusalException("OPTION_LINE_NOT_FOUND", $"the expected text was not found in the {key} line of [{section}] in {file.Rel}");
            return;
        }
        file[i] = pre + nv + post;
        what[(file.Rel, i)] = (section, key);
    }
}

/// <summary>Rules for an option description the user typed (the same care as group names: nothing the game could read as an expression).</summary>
public static class DescriptionRules
{
    static bool Allowed(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '-' or '_' or '.' or ',' or '!' or '?' or '+' or '\'' or '&' or '(' or ')' or ':' or ';' or '/' or '%';

    /// <summary>
    /// The stored (file) form of a typed description: real line breaks become the literal \n the game reads as a new line, characters outside the allowed
    /// set are dropped, spaces collapse, empty lines go, and the lengths are cut to the catalog's limits.
    /// </summary>
    public static string Sanitize(string? raw, OptionCatalog catalog)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var lines = new List<string>();
        foreach (var part in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var sb = new StringBuilder();
            foreach (char c in part)
            {
                char ch = c == '\t' ? ' ' : c;
                if (!Allowed(ch)) continue;
                if (ch == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
                sb.Append(ch);
            }
            string line = sb.ToString().Trim();
            if (line.Length > catalog.MaxDescriptionLineLength) line = line[..catalog.MaxDescriptionLineLength].TrimEnd();
            if (line.Length > 0) lines.Add(line);
            if (lines.Count == catalog.MaxDescriptionLines) break;
        }
        return string.Join("\\n", lines);
    }

    /// <summary>Why a stored description is not allowed, or null.</summary>
    public static string? Problem(string stored, OptionCatalog catalog)
    {
        if (stored.Length == 0) return "the description is empty (put it back to the shipped text instead)";
        if (stored != Sanitize(stored, catalog)) return $"it may only hold letters, digits, spaces and - _ . , ! ? + ' & ( ) : ; / %, at most {catalog.MaxDescriptionLines} lines of {catalog.MaxDescriptionLineLength} characters";
        return null;
    }

    /// <summary>The stored form shown with real line breaks.</summary>
    public static string ForEditing(string stored) => stored.Replace("\\n", "\r\n", StringComparison.Ordinal);
}
