using RwEditor.Core.Mod;
using RwEditor.Core.Model;

namespace RwEditor.Core.Stats;

/// <summary>
/// The single choke point that decides whether a stat may be edited (DESIGN_EDITOR 1.4 / 5.7). Refusal reasons, in order:
/// 1. the key is not in the dictionary (not whitelisted) or is on the dictionary's never-editable list (logic, ids, tags, images, movementType, techLevel...);
/// 2. the section is not one the entry allows;
/// 3. the unit's file, or the file that would be written, is under a lock glob of editor-support.json (myAssets/**, startingOptions/**, ...), is one of
///    the generated include files, or is not an .ini / .template inside the mod;
/// 4. the entry is for mobile units only and the unit is a building (or the reverse);
/// 5. the current value is not a literal (expression, ${...}, text): read-only.
/// A plan can only be built after Approve passed (StatPatcher calls it first and never writes anything it did not approve).
/// </summary>
public static class LockPolicy
{
    public static Finding? Approve(StatDictionary dict, StatEntry? entry, string typedKey, UnitModel model, UnitType unit, string section)
    {
        if (entry == null) return Refuse("NOT_IN_DICTIONARY", $"'{typedKey}' is not a whitelisted stat; keys outside the dictionary are never editable");
        if (dict.IsNeverEditable(entry.Key)) return Refuse("KEY_LOCKED", $"'{entry.Key}' is a locked key (logic, ids, tags, images, movementType, techLevel...)");
        if (!entry.MatchesSection(section)) return Refuse("SECTION_NOT_ALLOWED", $"{entry.Id} applies to [{string.Join("], [", entry.Sections)}], not [{section}]");
        if (!IsStatWritableFile(unit.RelPath, model.LockGlobs)) return Refuse("UNIT_LOCKED", $"{unit.Name} ({unit.RelPath}) is in a locked area (mod machinery); its stats cannot be edited");
        bool building = model.IsBuilding(unit);
        if (entry.AppliesTo == AppliesTo.Units && building) return Refuse("KEY_NOT_FOR_BUILDINGS", $"{entry.Id} is for mobile units; {unit.Name} is a building");
        if (entry.AppliesTo == AppliesTo.Buildings && !building) return Refuse("KEY_NOT_FOR_UNITS", $"{entry.Id} is for buildings; {unit.Name} is not a building");
        return null;
    }

    /// <summary>A file the stat editor may write: an .ini / .template, not a generated include, not under a lock glob, not the contract file.</summary>
    public static bool IsStatWritableFile(string rel, IReadOnlyList<string> lockGlobs)
    {
        rel = ModSnapshot.Norm(rel);
        if (!ModSnapshot.IsIniLike(rel)) return false;
        if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
        if (EditorSupport.KnownGeneratedFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) return false;
        return !EditorSupport.IsLocked(rel, lockGlobs);
    }

    public static bool IsStatWritableFile(string rel, EditorSupport support) => IsStatWritableFile(rel, support.LockedGlobs);

    static Finding Refuse(string code, string msg) => new(Severity.Error, code, msg);
}
