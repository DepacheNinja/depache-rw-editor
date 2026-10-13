using RwEditor.Core.Mod;
using RwEditor.Core.Settings;

namespace RwEditor.Core.Generation;

/// <summary>
/// Placing a group's units by hand (DESIGN_EDITOR 2.3, PH-099): a group is "by hand" when its units carry explicit offsets (all or none, see the validator);
/// otherwise <see cref="Layout.Auto"/> places them. These helpers switch a group between the two and keep the offsets right when units or counts change,
/// so the page and the tests share one set of rules. They change only the settings object they are given.
/// </summary>
public static class HandPlacement
{
    /// <summary>Two units count as overlapping when their centres are closer than this share of their radii added up (a slight overlap is harmless in the game).</summary>
    public const double OverlapFactor = 0.8;

    public static bool IsManual(SpawnGroup g) => g.Units.Any(u => u.Offsets != null);

    /// <summary>Back to the automatic layout: every offset is dropped.</summary>
    public static void BackToAuto(SpawnGroup g)
    {
        foreach (var u in g.Units) u.Offsets = null;
    }

    /// <summary>
    /// Makes the group "by hand": the positions the automatic layout gives now become the explicit offsets (when it does not fit, the relaxed layout is
    /// used, which always fits), so nothing moves until the user drags it. A group that is already by hand is only brought in line with its counts.
    /// </summary>
    public static void MakeManual(SpawnGroup g, EditorSettings s, ModContext mod)
    {
        if (IsManual(g)) { Sync(g, s, mod); return; }
        var planned = SpawnGroupGenerator.PlannedOffsets(g, s, mod, out _) ?? Layout.Relaxed(SectorIndex(g, s).Index, SectorIndex(g, s).Count, Copies(g, mod));
        int k = 0;
        foreach (var u in g.Units)
        {
            u.Offsets = new List<Offset>();
            for (int c = 0; c < Math.Max(0, u.Count); c++, k++)
                u.Offsets.Add(k < planned.Count ? planned[k] : Layout.FreeSpot(SectorIndex(g, s).Index, SectorIndex(g, s).Count, RadiusOf(u.Unit, mod), Taken(g, s, mod)));
        }
    }

    /// <summary>
    /// After units or counts of a by-hand group changed: every unit has exactly as many offsets as its count. Existing ones stay where the user put
    /// them; missing ones go to free spots; extra ones (count lowered) are cut from the end. Does nothing for an automatic group.
    /// </summary>
    public static void Sync(SpawnGroup g, EditorSettings s, ModContext mod)
    {
        if (!IsManual(g)) return;
        var (index, count) = SectorIndex(g, s);
        foreach (var u in g.Units)
        {
            u.Offsets ??= new List<Offset>();
            int want = Math.Max(0, u.Count);
            if (u.Offsets.Count > want) u.Offsets.RemoveRange(want, u.Offsets.Count - want);
            while (u.Offsets.Count < want)
            {
                var taken = Taken(g, s, mod).Concat(g.Units.SelectMany(x => (x.Offsets ?? new List<Offset>()).Select(o => (o, RadiusOf(x.Unit, mod))))).ToList();
                u.Offsets.Add(Layout.FreeSpot(index, count, RadiusOf(u.Unit, mod), taken));
            }
        }
    }

    /// <summary>The positions (and radii) of every switched-on group except <paramref name="except"/>, as they will be placed.</summary>
    public static IReadOnlyList<(Offset At, double Radius)> Taken(SpawnGroup except, EditorSettings s, ModContext mod)
    {
        var list = new List<(Offset, double)>();
        foreach (var other in SpawnGroupGenerator.ActiveGroups(s))
        {
            if (ReferenceEquals(other, except) || other.Slot == except.Slot || other.Units.Count == 0) continue;
            if (other.Units.Any(u => mod.FindUnit(u.Unit) == null)) continue;
            var offs = SpawnGroupGenerator.PlannedOffsets(other, s, mod, out _);
            if (offs == null) continue;
            int k = 0;
            foreach (var u in other.Units)
                for (int c = 0; c < u.Count && k < offs.Count; c++, k++) list.Add((offs[k], RadiusOf(u.Unit, mod)));
        }
        return list;
    }

    public static double RadiusOf(string unit, ModContext mod) => mod.FindUnit(unit)?.Radius is > 0 and var r ? r : Layout.DefaultRadius;

    static List<(string, double?)> Copies(SpawnGroup g, ModContext mod)
    {
        var copies = new List<(string, double?)>();
        foreach (var u in g.Units)
            for (int c = 0; c < Math.Max(0, u.Count); c++) copies.Add((u.Unit, mod.FindUnit(u.Unit)?.Radius));
        return copies;
    }

    static (int Index, int Count) SectorIndex(SpawnGroup g, EditorSettings s)
    {
        var slots = SpawnGroupGenerator.ActiveGroups(s).Select(x => x.Slot).Append(g.Slot).Distinct().OrderBy(x => x).ToList();
        return (slots.IndexOf(g.Slot), slots.Count);
    }

    /// <summary>
    /// Pairs of units of the switched-on groups whose circles overlap, where at least one of the two stands by hand (automatic layouts keep a gap by
    /// construction). Each result: the group slot of the by-hand unit, its unit name, the other unit's name.
    /// </summary>
    public static IReadOnlyList<(int Slot, string Unit, string Other)> Overlaps(EditorSettings s, ModContext mod)
    {
        var items = new List<(int Slot, string Unit, Offset At, double R, bool Hand)>();
        foreach (var g in SpawnGroupGenerator.ActiveGroups(s))
        {
            if (g.Units.Count == 0 || g.Units.Any(u => mod.FindUnit(u.Unit) == null)) continue;
            var offs = SpawnGroupGenerator.PlannedOffsets(g, s, mod, out _);
            if (offs == null) continue;
            bool hand = g.Units.All(u => u.Offsets != null);
            int k = 0;
            foreach (var u in g.Units)
                for (int c = 0; c < u.Count && k < offs.Count; c++, k++) items.Add((g.Slot, u.Unit, offs[k], RadiusOf(u.Unit, mod), hand));
        }
        var result = new List<(int, string, string)>();
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
            {
                if (!items[i].Hand && !items[j].Hand) continue;
                double d = Math.Sqrt(Math.Pow(items[i].At.X - items[j].At.X, 2) + Math.Pow(items[i].At.Y - items[j].At.Y, 2));
                if (d >= OverlapFactor * (items[i].R + items[j].R)) continue;
                if (items[i].Hand) result.Add((items[i].Slot, items[i].Unit, items[j].Unit));
                if (items[j].Hand) result.Add((items[j].Slot, items[j].Unit, items[i].Unit));
            }
        return result;
    }
}
