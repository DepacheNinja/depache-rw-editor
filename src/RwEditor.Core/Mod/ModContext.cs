using System.Globalization;
using System.Text.RegularExpressions;
using RwEditor.Core.Ini;

namespace RwEditor.Core.Mod;

/// <summary>One unit type of the mod as the spawn builder needs it.</summary>
public sealed record UnitInfo(string Name, string RelPath, bool Loadable, bool Locked, string? DisplayText, bool? IsBuilding, double? Radius);

/// <summary>One existing option of the match option selector (an On action built from template_turnOnOption).</summary>
public sealed record OptionInfo(string Tag, string Name, string TypeId, string OnSection, int FileOrder, string Description = "");

/// <summary>
/// Everything the generator and the validator read from a mod folder, loaded once into RAM: the ini texts, editor-support.json, the unit table and the
/// selector's own options. Read-only.
/// </summary>
public sealed class ModContext
{
    ModContext(ModSnapshot snapshot, EditorSupport support, Dictionary<string, UnitInfo> units, IReadOnlyList<OptionInfo> options, IReadOnlyDictionary<string, string> sectionNames)
    {
        Snapshot = snapshot; Support = support; _units = units; Options = options; SectionNames = sectionNames;
    }

    readonly Dictionary<string, UnitInfo> _units;   // ordinal (exact) names

    public ModSnapshot Snapshot { get; }
    public EditorSupport Support { get; }
    public string Root => Snapshot.Root;
    public IReadOnlyList<OptionInfo> Options { get; }
    /// <summary>The selector's four sections by typeId ("1".."4"), named as the game shows them (economy options, extra feature options, ban options, start unit options).</summary>
    public IReadOnlyDictionary<string, string> SectionNames { get; }
    public IReadOnlyCollection<UnitInfo> Units => _units.Values;

    /// <summary>The two option tags that may never be hidden (DESIGN_EDITOR 2.3; tools/check-option-sections.ps1 (f)).</summary>
    public static readonly IReadOnlyList<string> NeverHide = new[] { "fairAI", "extraHuman" };

    /// <summary>Section 4 ("start unit options") of the selector.</summary>
    public const string Section4TypeId = "4";

    public static ModContext Load(string root)
    {
        root = Path.GetFullPath(root);
        var support = EditorSupport.Load(root);
        var snap = ModSnapshot.Load(root);
        return Build(snap, support);
    }

    public static ModContext Build(ModSnapshot snap, EditorSupport support)
    {
        var docs = new Dictionary<string, IniDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in snap.Files) docs[rel] = IniDocument.Parse(snap.GetText(rel)!, rel, IniDialect.Catalog);
        var units = BuildUnits(docs, support);
        var options = BuildOptions(snap, out var sectionNames);
        return new ModContext(snap, support, units, options, sectionNames);
    }

    /// <summary>Exact (ordinal) unit lookup. Unit names in spawnUnits are written exactly as stored.</summary>
    public UnitInfo? FindUnit(string name) => _units.TryGetValue(name, out var u) ? u : null;

    /// <summary>Case-insensitive lookup, only used to suggest the right spelling in an error.</summary>
    public UnitInfo? FindUnitIgnoreCase(string name) => _units.Values.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The option tag -> its [action_xxxOn] section, for the text edits of the Menu options page.</summary>
    public IReadOnlyDictionary<string, string> OnSections => Options.ToDictionary(o => o.Tag, o => o.OnSection, StringComparer.Ordinal);

    /// <summary>
    /// The locked option files of this snapshot as the Menu options page edits them (shipped text rebuilt from the recorded edits). Built from the snapshot's
    /// texts, so it is a RAM view for the live checks; the apply reads the disk itself. Throws EditorRefusalException when the recorded edits do not fit the files.
    /// </summary>
    public RwEditor.Core.Options.OptionModel LoadOptionModel(IReadOnlyList<RwEditor.Core.Settings.OptionEdit> recorded) =>
        RwEditor.Core.Options.OptionModel.Load(rel => Snapshot.GetText(rel) is string t ? new System.Text.UTF8Encoding(false).GetBytes(t) : null, recorded);

    public OptionInfo? FindOption(string tag) => Options.FirstOrDefault(o => o.Tag == tag);

    /// <summary>Options of every section a settings file may hide (or delete): all except fairAI / extraHuman, in selector file order. Every option's On and Off
    /// buttons are built from the same two templates, which carry the hide term, so the generated hide tag works in all four sections.</summary>
    public IReadOnlyList<OptionInfo> HideableOptions => Options.Where(o => !NeverHide.Contains(o.Tag)).ToList();

    /// <summary>Section-4 options a settings file may hide (or delete): every section-4 option except fairAI / extraHuman, in selector file order.</summary>
    public IReadOnlyList<OptionInfo> HideableSection4Options =>
        Options.Where(o => o.TypeId == Section4TypeId && !NeverHide.Contains(o.Tag)).ToList();

    // ---------------------------------------------------------------------------------------------------------------------------
    static Dictionary<string, UnitInfo> BuildUnits(Dictionary<string, IniDocument> docs, EditorSupport support)
    {
        docs.TryGetValue("all-units.template", out var allUnits);
        var units = new Dictionary<string, UnitInfo>(StringComparer.Ordinal);
        foreach (var (rel, doc) in docs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? nameVal = doc.Get("core", "name");
            if (nameVal == null) continue;
            bool loadable = !(doc.Get("core", "dont_load") ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            bool locked = support.IsLocked(rel);
            string? disp = Eff(docs, allUnits, doc, "core", "displayText");
            if (disp != null && disp.Contains("${", StringComparison.Ordinal)) disp = null;
            string? isb = Eff(docs, allUnits, doc, "core", "isBuilding")?.Trim();
            bool? isBuilding = isb == null ? false   // engine default for a unit that never sets it: not a building (no key = not a building in RW)
                : isb.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
                : isb.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
            string? rad = Eff(docs, allUnits, doc, "core", "radius")?.Trim();
            double? radius = rad != null && double.TryParse(rad, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
            foreach (var part in nameVal.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // A name defined twice: prefer a loadable definition (the game loads that one); otherwise the first in path order.
                if (units.TryGetValue(part, out var prev) && (prev.Loadable || !loadable)) continue;
                units[part] = new UnitInfo(part, rel, loadable, locked, disp, isBuilding, radius);
            }
        }
        return units;
    }

    /// <summary>
    /// Effective value with the engine's merge order (CHANGELOG C-026, game-lib.jar units/custom/ag + ab.a): the unit's own key wins; otherwise the
    /// copyFrom parents are merged LAST-listed first, so the last listed parent that has the key wins; each parent with its own chain; then
    /// all-units.template. Non-ROOT paths are relative to the copying file's folder, with the mod root as a fallback (as tools/RwCatalog does).
    /// </summary>
    static string? Eff(Dictionary<string, IniDocument> docs, IniDocument? allUnits, IniDocument doc, string section, string key)
    {
        var v = EffChain(docs, doc, section, key, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (v == null && allUnits != null && !ReferenceEquals(allUnits, doc)) v = allUnits.Get(section, key);
        return v;
    }

    static string? EffChain(Dictionary<string, IniDocument> docs, IniDocument doc, string section, string key, HashSet<string> seen)
    {
        if (!seen.Add(doc.RelPath)) return null;
        var own = doc.Get(section, key);
        if (own != null) return own;
        var cf = doc.Get("core", "copyFrom");
        if (cf == null) return null;
        var entries = cf.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            var parent = FindParent(docs, doc.RelPath, entries[i]);
            if (parent == null) continue;
            var v = EffChain(docs, parent, section, key, seen);
            if (v != null) return v;
        }
        return null;
    }

    static IniDocument? FindParent(Dictionary<string, IniDocument> docs, string fromRel, string entry)
    {
        var rel = ModSnapshot.ResolveCopyFrom(fromRel, entry);
        if (rel != null && docs.TryGetValue(rel, out var d)) return d;
        string e = entry.Trim();
        if (!e.StartsWith("ROOT:", StringComparison.OrdinalIgnoreCase) && !e.StartsWith("CORE:", StringComparison.OrdinalIgnoreCase))
        {
            var rootRel = ModSnapshot.ResolveCopyFrom("x.ini", e);
            if (rootRel != null && docs.TryGetValue(rootRel, out var d2)) return d2;
        }
        return null;
    }

    static readonly Regex OnName = new(@"^action_.*On$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The selector's OWN options (the locked file only; the generated include is ignored), as check-option-sections.ps1 $ownOn / $ownTags.</summary>
    static IReadOnlyList<OptionInfo> BuildOptions(ModSnapshot snap, out IReadOnlyDictionary<string, string> sectionNames)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        sectionNames = names;
        var text = snap.GetText(EditorSupport.SelectorLocked);
        if (text == null) return Array.Empty<OptionInfo>();
        var doc = IniDocument.Parse(text, EditorSupport.SelectorLocked, IniDialect.Checker);
        var res = new SectionResolver(doc.Sections);
        var list = new List<OptionInfo>();
        int order = 0;
        foreach (var s in doc.Named)
        {
            // "action_showOptionToPlayerN" carries the section's name (typeNameEn); the n-th section has typeId N
            var sec = Regex.Match(s.Name, @"^action_showOptionToPlayer(\d+)$", RegexOptions.CultureInvariant);
            if (sec.Success && res.Get(s.Name, "@define typeNameEn") is { Length: > 0 } secName) names[sec.Groups[1].Value] = secName.Trim();
            if (!OnName.IsMatch(s.Name) || !res.Copies(s.Name, "template_turnOnOption")) continue;
            var map = res.Resolve(s.Name);
            if (!map.TryGetValue("@define actionTag", out var tag)) continue;
            map.TryGetValue("@define actionName", out var nm);
            map.TryGetValue("@define typeId", out var tid);
            string desc = (res.Get(s.Name, "description") ?? "").Replace("\\n", "\n", StringComparison.Ordinal).Trim();
            list.Add(new OptionInfo(tag, nm ?? tag, tid ?? "", s.Name, order++, desc));
        }
        return list;
    }
}
