using RwEditor.Core.Model;

namespace RwEditor.Core.Stats;

/// <summary>Cross-key rules of the dictionary ("rules" ids), evaluated on a merged unit. A violation names the rule and the section.</summary>
public static class StatRules
{
    /// <summary>ag offset 12749: a turret limitingRange of 99999 or more counts as "not set".</summary>
    const double LimitingRangeUnset = 99999;

    public static IReadOnlyList<string> Evaluate(MergedUnit m, string ruleId) => ruleId switch
    {
        "limitingRangeVsMaxAttackRange" => LimitingRange(m),
        "projectileNeedsDamage" => ProjectileDamage(m),
        "limitingMinRangeBelowLimitingRange" => MinRange(m),
        "energyUsageNeedsEnergyMax" => EnergyUsage(m),
        _ => throw new ArgumentException("unknown rule " + ruleId),
    };

    static double? Lit(MergedUnit m, string section, string key) =>
        m.Get(section, key) is MergedValue v && StatValue.TryFloat(v.Value, out var d) ? d : null;

    static bool IsSet(MergedUnit m, string section, string key) => m.Get(section, key) != null;

    static IEnumerable<MergedSection> Turrets(MergedUnit m) => m.Sections.Where(s => s.Name.StartsWith("turret_", StringComparison.OrdinalIgnoreCase));

    static IReadOnlyList<string> LimitingRange(MergedUnit m)
    {
        var mar = Lit(m, "attack", "maxAttackRange");
        if (mar == null) return Array.Empty<string>();          // unset (or not a literal): the engine sets it from the turrets / cannot be judged here
        double scale = Lit(m, "core", "globalScale") ?? 1.0;
        var turrets = Turrets(m).ToList();
        if (turrets.Count == 0) return Array.Empty<string>();
        double max = -1;
        foreach (var t in turrets)
        {
            var lr = t.Get("limitingRange");
            if (lr == null) return Array.Empty<string>();        // not applied to every turret
            if (!StatValue.TryFloat(lr.Value, out var d)) return Array.Empty<string>();
            if (d >= LimitingRangeUnset) return Array.Empty<string>();
            max = Math.Max(max, d);
        }
        return max < mar.Value * scale
            ? new[] { $"limitingRangeVsMaxAttackRange: every turret sets limitingRange, the largest is {StatValue.Format(max)}, below maxAttackRange {StatValue.Format(mar.Value)} x globalScale {StatValue.Format(scale)} (the game refuses to load the unit)" }
            : Array.Empty<string>();
    }

    static IReadOnlyList<string> ProjectileDamage(MergedUnit m) =>
        m.Sections.Where(s => s.Name.StartsWith("projectile_", StringComparison.OrdinalIgnoreCase))
            .Where(s => !s.Has("directDamage") && !s.Has("areaDamage"))
            .Select(s => $"projectileNeedsDamage: [{s.Name}] sets neither directDamage nor areaDamage").ToList();

    static IReadOnlyList<string> MinRange(MergedUnit m)
    {
        var list = new List<string>();
        foreach (var t in Turrets(m))
        {
            if (t.Get("limitingMinRange") is not MergedValue mn || !StatValue.TryFloat(mn.Value, out var a)) continue;
            if (t.Get("limitingRange") is not MergedValue mx || !StatValue.TryFloat(mx.Value, out var b)) continue;
            if (a > b) list.Add($"limitingMinRangeBelowLimitingRange: [{t.Name}] limitingMinRange {StatValue.Format(a)} is above limitingRange {StatValue.Format(b)}");
        }
        return list;
    }

    static IReadOnlyList<string> EnergyUsage(MergedUnit m)
    {
        double energy = Lit(m, "core", "energyMax") ?? (IsSet(m, "core", "energyMax") ? double.NaN : 0);
        if (double.IsNaN(energy) || energy > 0) return Array.Empty<string>();
        return Turrets(m).Where(t => t.Get("energyUsage") is MergedValue v && StatValue.TryFloat(v.Value, out var u) && u > 0)
            .Select(t => $"energyUsageNeedsEnergyMax: [{t.Name}] uses energy but the unit has energyMax {StatValue.Format(energy)}").ToList();
    }
}
