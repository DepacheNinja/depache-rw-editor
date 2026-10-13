using RwEditor.Core.Model;
using RwEditor.Core.Stats;

namespace RwEditor.Core.Browse;

/// <summary>One row of the unit browser (E2). <see cref="Kind"/> = "Building" or the unit's movement type in plain words; <see cref="Category"/> = the
/// folder the unit's file lives in (the mod authors' own grouping: the first folder, or the first folder below donors/&lt;mod&gt;/).</summary>
public sealed record UnitRow(int Index, UnitType Unit, string Name, string? DisplayText, string Area, string AreaLabel, bool IsBuilding, string Kind,
    string Category, bool Locked, bool DuplicateName)
{
    /// <summary>What the list shows as the main text: the in-game name when there is one, else the internal name.</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayText) ? Name : DisplayText!;
}

public enum TypeFilter { All, Units, Buildings }

public enum SortKey { Title, Name, Area, Kind, Category }

/// <summary>Search, filter and sort of the browser. Text = words that must ALL appear (case-insensitive) in the name or the in-game name.
/// <see cref="IncludeLocked"/> = false hides the units under the lock globs (the mod's machinery: markers, selectors, spawners).</summary>
public sealed record UnitQuery(string? Text = null, string? Area = null, TypeFilter Type = TypeFilter.All, SortKey Sort = SortKey.Title, bool Descending = false,
    bool IncludeLocked = true);

/// <summary>Builds the browser rows from the unit model (read-only) and answers queries. UI-free so it is unit-tested.</summary>
public static class UnitCatalog
{
    /// <summary>Movement types seen in the mod (grep of src, 2026-10-12: AIR, WATER, HOVER, LAND, OVER_CLIFF_WATER, OVER_CLIFF, NONE), read literally.</summary>
    public static string KindOf(bool isBuilding, string? movementType) => isBuilding ? "Building" : (movementType?.Trim().ToUpperInvariant() ?? "") switch
    {
        "" => "Unit (no movement type)",
        "LAND" => "Land",
        "AIR" => "Air",
        "WATER" => "Water",
        "HOVER" => "Hover",
        "OVER_CLIFF" => "Over cliffs",
        "OVER_CLIFF_WATER" => "Over cliffs and water",
        "NONE" => "Does not move",
        var other => other.Contains("${", StringComparison.Ordinal) ? "Unit (computed movement)" : char.ToUpperInvariant(other[0]) + other[1..].ToLowerInvariant().Replace('_', ' '),
    };

    public static string CategoryOf(string relPath)
    {
        var parts = relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        const string top = "(mod top folder)";
        if (parts.Length > 0 && parts[0].Equals("donors", StringComparison.OrdinalIgnoreCase)) return parts.Length >= 4 ? parts[2] : top;
        return parts.Length >= 2 ? parts[0] : top;
    }

    public static IReadOnlyList<UnitRow> Build(UnitModel model)
    {
        var dupes = model.Units.GroupBy(u => u.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var labels = model.Areas.Areas.ToDictionary(a => a.Id, a => a.Label, StringComparer.Ordinal);
        var rows = new List<UnitRow>(model.Units.Count);
        foreach (var u in model.Units)
        {
            var m = model.Merge(u);
            string? disp = m.Get("core", "displayText")?.Value.Trim();
            if (disp != null && (disp.Length == 0 || StatValue.IsExpression(disp))) disp = null;
            bool building = model.IsBuilding(u);
            string kind = KindOf(building, m.Get("movement", "movementType")?.Value);
            rows.Add(new UnitRow(rows.Count, u, u.Name, disp, u.Area, labels.TryGetValue(u.Area, out var l) ? l : u.Area, building, kind,
                CategoryOf(u.RelPath), u.Locked, dupes.Contains(u.Name)));
        }
        return rows;
    }

    public static IReadOnlyList<UnitRow> Query(IReadOnlyList<UnitRow> rows, UnitQuery q)
    {
        var words = (q.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<UnitRow> r = rows;
        if (!string.IsNullOrEmpty(q.Area)) r = r.Where(x => x.Area == q.Area);
        if (!q.IncludeLocked) r = r.Where(x => !x.Locked);
        if (q.Type == TypeFilter.Units) r = r.Where(x => !x.IsBuilding);
        else if (q.Type == TypeFilter.Buildings) r = r.Where(x => x.IsBuilding);
        if (words.Length > 0)
            r = r.Where(x => words.All(w => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || (x.DisplayText?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
        Func<UnitRow, string> key = q.Sort switch
        {
            SortKey.Name => x => x.Name,
            SortKey.Area => x => x.AreaLabel,
            SortKey.Kind => x => x.Kind,
            SortKey.Category => x => x.Category,
            _ => x => x.Title,
        };
        // stable, culture-free order; ties by title, then internal name, then file
        var ordered = q.Descending
            ? r.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : r.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        return ordered.ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Unit.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
