using RwEditor.Core.Model;

namespace RwEditor.Core.Stats;

/// <summary>Shipped values of one dictionary entry over every loadable unit (rwedit dict-check --mod).</summary>
/// <param name="OutsideHard">Shipped literals outside the hard range that a user could reach (unlocked unit, value from an editable file).</param>
/// <param name="OutsideHardLocked">The same in locked units or locked files (all-units.template, myAssets/...): the editor never edits those anyway.</param>
public sealed record StatSurveyRow(
    StatEntry Entry, int Occurrences, int Literal, int ReadOnly, double? Min, double? Max, double? Median,
    IReadOnlyList<string> OutsideHard, int OutsideHardLocked, int OutsideWarn)
{
    /// <summary>No editable shipped value outside the hard range, or the entry documents why those are accepted (shippedOutsideHard).</summary>
    public bool Clean => OutsideHard.Count == 0 || Entry.ShippedOutsideHard != null;
}

/// <summary>
/// Reads every whitelisted stat of every loadable unit once (each merged unit is cached by the model) and reports the shipped range per entry, so the
/// dictionary's hard limits can be checked against the real mod: an editable shipped literal outside a hard limit means the dictionary would refuse a
/// value the game already loads, unless the entry says why that is intended (e.g. turret delay 0, which the design refuses on purpose).
/// </summary>
public static class StatSurvey
{
    public static IReadOnlyList<StatSurveyRow> Run(UnitModel model, StatDictionary dict)
    {
        var rows = new List<StatSurveyRow>();
        var merged = model.Units.Select(model.Merge).ToList();
        foreach (var e in dict.Entries)
        {
            int occ = 0, lit = 0, ro = 0, outWarn = 0, outLocked = 0;
            var nums = new List<double>();
            var outside = new List<string>();
            foreach (var m in merged)
                foreach (var sec in StatReader.SectionsFor(m, e))
                {
                    var r = StatReader.Read(m, e, sec);
                    if (!r.IsSet) continue;
                    // a price entry only counts where its field is present
                    if (e.Field != null && r.Editable && StatValue.PriceField(r.Raw!.Value, e.Field) == null) continue;
                    occ++;
                    if (!r.Editable) { ro++; continue; }
                    lit++;
                    double v = r.Number!.Value;
                    nums.Add(v);
                    if (v < e.HardMin || v > e.HardMax)
                    {
                        bool reachable = !m.Unit.Locked && LockPolicy.IsStatWritableFile(r.Raw!.Origin.File, model.LockGlobs);
                        if (reachable) outside.Add($"{m.Unit.Name} [{sec}] {e.Key} = {r.Raw!.Value.Trim()} ({r.Raw.Origin})");
                        else outLocked++;
                    }
                    else if ((e.WarnMin is double wn && v < wn) || (e.WarnMax is double wx && v > wx)) outWarn++;
                }
            nums.Sort();
            rows.Add(new StatSurveyRow(e, occ, lit, ro, nums.Count > 0 ? nums[0] : null, nums.Count > 0 ? nums[^1] : null,
                nums.Count > 0 ? nums[(nums.Count - 1) / 2] : null, outside, outLocked, outWarn));
        }
        return rows;
    }
}
