using RwEditor.Core.Model;
using RwEditor.Core.Settings;

namespace RwEditor.Core.Stats;

public enum BulkWho { Buildings, Units, UnitsAndBuildings }

/// <summary>Which of the matching units already have a value for the stat.</summary>
public enum BulkHave { Any, OnlyThoseThatHaveIt, OnlyThoseWithout }

/// <param name="IncludeCopies">Also change the units that copy a chosen unit's own file (its upgrade levels / versions): the value is written in the chosen unit's
/// own file (never into a template other units share) and they inherit it. Off = only units whose own file nobody else copies.</param>
public sealed record BulkFilter(BulkWho Who, string? AreaId = null, BulkHave Have = BulkHave.Any, bool IncludeCopies = false);

/// <summary>One unit left out of a bulk change, with the reason in plain words.</summary>
public sealed record BulkSkip(UnitType Unit, string Reason);

/// <summary>The result of planning "set this value for many units": the checked changes to stage and the units left out (and why).</summary>
/// <param name="AffectedUnits">Every unit whose value changes (the chosen ones plus the units that copy their files when IncludeCopies is on).</param>
public sealed record BulkPlan(StatEntry Entry, string Section, string Value, IReadOnlyList<PendingStatEdit> Ok, IReadOnlyList<BulkSkip> Skipped, int Considered,
    IReadOnlyList<string> AffectedUnits, IReadOnlyList<string> BatchProblems)
{
    /// <summary>True when something can be staged and the changes hold together (no new load problem when all are written).</summary>
    public bool CanStage => Ok.Count > 0 && BatchProblems.Count == 0;

    /// <summary>Skip reasons with how many units gave each (most common first), for the dialog.</summary>
    public IReadOnlyList<(string Reason, int Count)> SkipSummary =>
        Skipped.GroupBy(s => s.Reason).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
}

/// <summary>
/// "Set for many units" (C-033): gives one value of one stat (a core / movement / attack value, e.g. Credits produced) to every complete unit or building
/// that matches a filter, as ordinary pending changes ("this unit only", each checked by the full single-edit plan inside one batch plan, so every
/// limit, lock and rule of a normal edit applies). Only the main unit of each family is considered (never attached parts, spawned helpers or the part
/// lists), locked units are never included, and a unit whose own file other units copy is skipped (a unit-only change there would change those units too).
/// Nothing is written: the caller stages the returned changes and the user applies them like any other.
/// </summary>
public static class StatBulk
{
    public static BulkPlan Plan(UnitModel model, StatDictionary dict, UnitFamilies families, StatEntry entry, string value, BulkFilter filter, Func<string, byte[]> readDisk)
    {
        string? section = entry.FixedSection;
        if (section == null) throw new EditorRefusalException("BULK_SECTION", $"{entry.Label} belongs to a numbered section (turret, projectile, action); set it for many units only for values of [core], [movement] or [attack].");
        string? problem = StatText.QuickCheck(entry, null, value, out var asRequest);
        if (problem != null) throw new EditorRefusalException("VALUE_INVALID", problem);

        var skipped = new List<BulkSkip>();
        var pend = new List<PendingStatEdit>();
        int considered = 0;
        foreach (var fam in families.Items)
        {
            if (fam.IsBucket || fam.Root == null) continue;
            var u = fam.Root;
            var m = model.Merge(u);
            bool building = model.IsBuilding(u);
            if (filter.Who == BulkWho.Buildings && !building) continue;
            if (filter.Who == BulkWho.Units && building) continue;
            if (filter.AreaId != null && !u.Area.Equals(filter.AreaId, StringComparison.OrdinalIgnoreCase)) continue;
            considered++;
            string? why = null;
            var sec = m.Section(section);
            if (u.Locked) why = "locked (mod machinery)";
            else if (model.UnitsNamed(u.Name).Count > 1) why = "its name is used by two files";
            else if (sec == null) why = $"has no [{section}] section";
            else if (LockPolicy.Approve(dict, entry, entry.Id, model, u, sec.Name) is Finding f) why = StatText.Plain(f);
            else
            {
                var r = StatReader.Read(m, entry, sec.Name);
                if (!r.Editable) why = "its value is calculated or not a plain number (" + StatText.Plain(r.ReadOnlyReason!) + ")";
                else if (filter.Have == BulkHave.OnlyThoseThatHaveIt && !HasValue(r, entry)) why = "has no value for it yet (you chose only those that have one)";
                else if (filter.Have == BulkHave.OnlyThoseWithout && HasValue(r, entry)) why = "already has a value (you chose only those without one)";
                else if (r.Number is double now && Math.Abs(now - Number(entry, asRequest!)) < 1e-9 * Math.Max(1, Math.Abs(now)) && HasValue(r, entry)) why = "already has this value";
            }
            if (why != null) { skipped.Add(new BulkSkip(u, why)); continue; }
            // the value is written in the unit's own file; "template" (= the units that copy this file are changed too) only where that write is the unit's
            // own line or a new line in its own file, never where the value comes from a template that unrelated units share
            var cur = StatReader.Read(m, entry, sec!.Name);
            var origin = cur.Raw?.Origin;
            bool ownScope = origin == null || (origin.Kind == OriginKind.Own && origin.ViaSection == null && origin.FileSection.Equals(sec.Name, StringComparison.OrdinalIgnoreCase));
            pend.Add(new PendingStatEdit { Unit = u.Name, UnitFile = u.RelPath, StatId = entry.Id, Section = sec.Name, Mode = filter.IncludeCopies && ownScope ? "template" : "unit", Value = asRequest! });
        }

        // one batch plan: every change is checked like a normal edit (nothing written), failures become skips
        var plan = pend.Count == 0 ? null : StatBatch.Plan(model, dict, pend, readDisk);
        var ok = new List<PendingStatEdit>();
        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (plan != null)
        {
            foreach (var it in plan.Items)
            {
                if (it.Ok) { ok.Add(it.Pending); foreach (var a in it.Plan!.Affected) affected.Add(a.Unit.Name); continue; }
                var unit = model.UnitsInFile(it.Pending.UnitFile).FirstOrDefault(x => x.Name == it.Pending.Unit);
                if (unit != null) skipped.Add(new BulkSkip(unit, ShortReason(it)));
            }
        }
        return new BulkPlan(entry, section, asRequest!, ok, skipped, considered, affected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            plan == null ? new List<string>() : plan.BatchFindings.Where(f => f.Severity == Severity.Error).Select(StatText.Plain).ToList());
    }

    /// <summary>True when the stat has a value of its own (for part entries: the part exists in the line).</summary>
    static bool HasValue(StatRef r, StatEntry e) =>
        r.IsSet && (e.Field == null || StatValue.PriceField(r.Raw!.Value, e.Field) != null);

    static double Number(StatEntry e, string request)
    {
        StatPatcher.ParseNew(e, request, out double n, out _, out _);
        return n;
    }

    static string ShortReason(StatBatchItem it)
    {
        var e = it.Errors.FirstOrDefault();
        if (e == null) return "cannot be changed";
        return e.Code switch
        {
            "NOT_UNIT_ONLY" => "its file is also used by other units (a unit-only change would change them too)",
            "TEMPLATE_REACHES_LOCKED" => "the change would reach locked units",
            "OUT_OF_RANGE" => "the value is outside the allowed range",
            "FILE_LOCKED" => "the value comes from a locked file",
            "SECTION_NOT_OWN" => "the section is inherited as a whole",
            _ => StatText.Plain(e),
        };
    }
}
