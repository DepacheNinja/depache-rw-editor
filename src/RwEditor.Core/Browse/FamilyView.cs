using RwEditor.Core.Model;

namespace RwEditor.Core.Browse;

public enum DisplayKind
{
    /// <summary>A family root (a complete unit at the top level).</summary>
    Root,
    /// <summary>A bucket title row (Shared parts / Helper parts): no unit of its own.</summary>
    Bucket,
    /// <summary>An entry inside a family or bucket.</summary>
    Child,
}

/// <summary>One row of the family list. <see cref="Row"/> is null only for a bucket row. <see cref="Context"/> = a root shown only because a member
/// matches the filters (the root itself does not). <see cref="Key"/> identifies the family (root file path or "bucket:...") for the expanded set.</summary>
public sealed record DisplayRow(DisplayKind Kind, UnitFamily Family, string Key, UnitRow? Row, FamilyEntry? Entry, int Depth, bool HasChildren, bool Expanded,
    int ChildCount, bool Context)
{
    public string Title => Row?.Title ?? Family.BucketTitle;
}

/// <summary>The result of one query: the visible rows plus the counts for the status line.</summary>
public sealed record FamilyListing(IReadOnlyList<DisplayRow> Rows, int Units, int UnitsTotal, int Inside, int InsideTotal, int BucketEntries, int BucketEntriesTotal,
    IReadOnlyCollection<string> AutoExpanded)
{
    /// <summary>"376 units (1,112 entries inside them) · 211 shared / helper parts" or, when filtered,
    /// "12 of 376 units (40 entries inside them) · 3 of 211 shared / helper parts".</summary>
    public string StatusText
    {
        get
        {
            bool all = Units == UnitsTotal && Inside == InsideTotal && BucketEntries == BucketEntriesTotal;
            string units = all ? $"{Units:N0} units ({Inside:N0} entries inside them)" : $"{Units:N0} of {UnitsTotal:N0} units ({Inside:N0} entries inside them)";
            string parts = all ? $"{BucketEntries:N0} shared / helper parts" : $"{BucketEntries:N0} of {BucketEntriesTotal:N0} shared / helper parts";
            return units + " · " + parts;
        }
    }
}

/// <summary>
/// The browser's family list (E2b), UI-free so it is unit-tested. Rules:
/// <list type="bullet">
/// <item>An ENTRY matches when it passes the mod-area, units / buildings, locked and search filters (the same tests as <see cref="UnitCatalog.Query"/>).</item>
/// <item>A family is listed when its root or any member matches; its root row is always shown (marked <see cref="DisplayRow.Context"/> when only
/// members match). A bucket is listed when any member matches.</item>
/// <item>Children of an expanded family = its members that match; with search text, a family whose ROOT matches the text shows every member that passes
/// the other filters (so expanding a found unit shows its whole family). With search text, a family with a matching member (not only the root) is
/// expanded automatically.</item>
/// <item>Top-level order follows the sort key of the root (title, name, mod, kind, folder), ties by title / name / path; buckets always last (shared,
/// then helper). Children keep the family's tree order.</item>
/// </list>
/// </summary>
public static class FamilyView
{
    public const string SharedKey = "bucket:shared", HelperKey = "bucket:helper";

    public static string KeyOf(UnitFamily f) => f.Bucket switch
    {
        BucketKind.SharedParts => SharedKey,
        BucketKind.HelperParts => HelperKey,
        _ => "unit:" + f.Root!.RelPath,
    };

    /// <param name="expanded">Family keys the user opened.</param>
    /// <param name="collapsed">Family keys the user closed although the search opens them by itself.</param>
    public static FamilyListing Build(UnitFamilies families, IReadOnlyList<UnitRow> rows, UnitQuery q, IReadOnlySet<string> expanded, IReadOnlySet<string>? collapsed = null)
    {
        var byUnit = new Dictionary<UnitType, UnitRow>(ReferenceEqualityComparer.Instance);
        foreach (var r in rows) byUnit[r.Unit] = r;
        var words = (q.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool searching = words.Length > 0;

        bool PassFilters(UnitRow x) =>
            (string.IsNullOrEmpty(q.Area) || x.Area == q.Area)
            && (q.IncludeLocked || !x.Locked)
            && (q.Type == TypeFilter.All || (q.Type == TypeFilter.Units ? !x.IsBuilding : x.IsBuilding));
        bool PassText(UnitRow x) => words.All(w => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (x.DisplayText?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));

        var shownFamilies = new List<(UnitFamily F, UnitRow? RootRow, bool RootMatch, List<FamilyEntry> Kids, bool Auto)>();
        int unitsTotal = 0, insideTotal = 0, bucketTotal = 0;
        foreach (var f in families.Items)
        {
            if (f.IsBucket) bucketTotal += f.Members.Count;
            else { unitsTotal++; insideTotal += f.Members.Count; }
            UnitRow? rootRow = f.Root != null ? byUnit[f.Root] : null;
            bool rootMatch = rootRow != null && PassFilters(rootRow) && PassText(rootRow);
            bool rootTextMatch = rootRow != null && searching && PassText(rootRow);
            var kids = new List<FamilyEntry>();
            bool memberTextMatch = false;
            foreach (var e in f.Members)
            {
                var r = byUnit[e.Unit];
                if (!PassFilters(r)) continue;
                bool t = PassText(r);
                if (t && searching) memberTextMatch = true;
                if (t || rootTextMatch) kids.Add(e);
            }
            // listed when the root matches, or (searching) a member matches the text and the filters, or (not searching) a member passes the filters
            if (!rootMatch && !(searching ? memberTextMatch : kids.Count > 0)) continue;
            shownFamilies.Add((f, rootRow, rootMatch, kids, searching && memberTextMatch));
        }

        Func<UnitRow, string> key = q.Sort switch
        {
            SortKey.Name => x => x.Name,
            SortKey.Area => x => x.AreaLabel,
            SortKey.Kind => x => x.Kind,
            SortKey.Category => x => x.Category,
            _ => x => x.Title,
        };
        var units = shownFamilies.Where(s => !s.F.IsBucket).ToList();
        var ordered = (q.Descending ? units.OrderByDescending(s => key(s.RootRow!), StringComparer.OrdinalIgnoreCase) : units.OrderBy(s => key(s.RootRow!), StringComparer.OrdinalIgnoreCase))
            .ThenBy(s => s.RootRow!.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.RootRow!.Name, StringComparer.Ordinal)
            .ThenBy(s => s.RootRow!.Unit.RelPath, StringComparer.OrdinalIgnoreCase)
            .Concat(shownFamilies.Where(s => s.F.Bucket == BucketKind.SharedParts))
            .Concat(shownFamilies.Where(s => s.F.Bucket == BucketKind.HelperParts));

        var list = new List<DisplayRow>();
        var auto = new List<string>();
        int shownUnits = 0, inside = 0, bucketEntries = 0;
        foreach (var s in ordered)
        {
            string k = KeyOf(s.F);
            bool isExpanded = (expanded.Contains(k) || s.Auto) && !(collapsed?.Contains(k) ?? false);
            if (s.Auto) auto.Add(k);
            if (s.F.IsBucket)
            {
                bucketEntries += s.Kids.Count;
                list.Add(new DisplayRow(DisplayKind.Bucket, s.F, k, null, null, 0, s.Kids.Count > 0, isExpanded, s.Kids.Count, false));
            }
            else
            {
                shownUnits++;
                inside += s.Kids.Count;
                list.Add(new DisplayRow(DisplayKind.Root, s.F, k, s.RootRow, null, 0, s.Kids.Count > 0, isExpanded, s.Kids.Count, !s.RootMatch));
            }
            if (!isExpanded) continue;
            foreach (var e in s.Kids)
                list.Add(new DisplayRow(DisplayKind.Child, s.F, k, byUnit[e.Unit], e, Math.Max(1, e.Depth), false, false, 0, false));
        }
        return new FamilyListing(list, shownUnits, unitsTotal, inside, insideTotal, bucketEntries, bucketTotal, auto);
    }
}
