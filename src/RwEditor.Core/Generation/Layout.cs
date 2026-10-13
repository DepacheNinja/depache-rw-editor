using RwEditor.Core.Settings;

namespace RwEditor.Core.Generation;

/// <summary>
/// Automatic placement of a group's units when the settings give no offsets (DESIGN_EDITOR 2.3 "Layout"): the n generated groups share the circle
/// around the team's anchor, the i-th (by slot, 0-based) owning the arc sector of 360/n degrees centred on i x 360/n degrees (0 = +x, as the design's
/// sample puts group 1 at +x and group 2 at -x); units go along rings from radius 300 out to at most 420, neighbours spaced by r1 + r2 + 40 along the
/// arc and rings by the largest radii + 40, using each unit's [core] radius. Deterministic: the same groups and unit radii always give the same offsets.
/// Deviation from the design text ("group k gets an arc sector"): the sector is shared among the ACTIVE groups, because fixed 45-degree sectors cannot
/// hold 12 small units between rings 300 and 420.
/// Not verified in a match: what spawnUnits does with an offset off the map or on terrain the unit cannot use (PH-066).
/// </summary>
public static class Layout
{
    public const int InnerRing = 300;
    public const int OuterRing = 420;
    public const int Gap = 40;

    /// <summary>Hard bound on any offset (explicit or automatic). An editor-side sanity limit, not an engine limit (PH-070).</summary>
    public const int MaxAbsOffset = 1000;

    /// <summary>
    /// Offsets for every copy of every unit of the group, in spawn order. Returns null (and a reason) when a unit's radius is unknown or the group does
    /// not fit in its sector between the rings; the user must then give explicit offsets.
    /// </summary>
    public static IReadOnlyList<Offset>? Auto(int sectorIndex, int sectorCount, IReadOnlyList<(string Unit, double? Radius)> copies, out string? reason) =>
        Place(sectorIndex, sectorCount, copies, OuterRing, out reason);

    static IReadOnlyList<Offset>? Place(int sectorIndex, int sectorCount, IReadOnlyList<(string Unit, double? Radius)> copies, int outerLimit, out string? reason)
    {
        reason = null;
        if (copies.Count == 0) return Array.Empty<Offset>();
        if (sectorCount < 1 || sectorIndex < 0 || sectorIndex >= sectorCount) throw new ArgumentOutOfRangeException(nameof(sectorIndex));
        var unknown = copies.Where(c => c.Radius is null or <= 0).Select(c => c.Unit).Distinct().ToList();
        if (unknown.Count > 0) { reason = $"the [core] radius of {string.Join(", ", unknown)} could not be resolved to a number, so the group cannot be placed automatically"; return null; }

        double sector = 2 * Math.PI / sectorCount;
        double half = sector / 2.0;
        double centre = sectorIndex * sector;
        var result = new List<Offset>(copies.Count);
        double ring = InnerRing;
        int i = 0;
        while (i < copies.Count)
        {
            // fill one ring: walk along the arc from the sector's start edge
            double ringMax = 0;
            double pos = -half;                       // angle relative to the sector centre
            int placedOnRing = 0;
            double prevR = 0;
            while (i < copies.Count)
            {
                double r = copies[i].Radius!.Value;
                double step = placedOnRing == 0 ? (r + Gap / 2.0) / ring : (prevR + r + Gap) / ring;
                double a = pos + step;
                if (a + (r + Gap / 2.0) / ring > half) break;      // would leave the sector
                pos = a;
                result.Add(new Offset((int)Math.Round(ring * Math.Cos(centre + a), MidpointRounding.AwayFromZero),
                                      (int)Math.Round(ring * Math.Sin(centre + a), MidpointRounding.AwayFromZero)));
                ringMax = Math.Max(ringMax, r);
                prevR = r;
                placedOnRing++;
                i++;
            }
            if (i >= copies.Count) break;
            if (placedOnRing == 0) { reason = $"{copies[i].Unit} (radius {copies[i].Radius}) is too large for the group's sector at ring {ring}"; return null; }
            double nextR = copies.Skip(i).Max(c => c.Radius!.Value);
            ring += ringMax + nextR + Gap;
            if (ring > outerLimit) { reason = $"the group does not fit between rings {InnerRing} and {outerLimit} of its sector ({copies.Count} units; {i} placed)"; return null; }
        }
        return result;
    }

    /// <summary>Radius used for a unit whose [core] radius is not a plain number, when the user places units by hand.</summary>
    public const double DefaultRadius = 20;

    /// <summary>
    /// The starting positions when the user chooses "place by hand" and the automatic layout does not fit: the same walk as <see cref="Auto"/> but rings may
    /// reach out to the hard bound of the editor, and a unit without a numeric radius counts as <see cref="DefaultRadius"/>. Never fails for up to 12 units; the
    /// user then drags the units where they want them.
    /// </summary>
    public static IReadOnlyList<Offset> Relaxed(int sectorIndex, int sectorCount, IReadOnlyList<(string Unit, double? Radius)> copies)
    {
        var sized = copies.Select(c => (c.Unit, (double?)(c.Radius is > 0 ? c.Radius : DefaultRadius))).ToList();
        var o = Place(sectorIndex, sectorCount, sized, MaxAbsOffset - 60, out _);
        if (o != null) return o;
        o = Place(0, 1, sized, MaxAbsOffset - 60, out _);   // the sector is too narrow even for the outer rings: use the whole circle
        if (o != null) return o;
        return sized.Select((c, i) => new Offset(InnerRing, (i - sized.Count / 2) * 80)).ToList();   // last resort, cannot happen for 12 units
    }

    /// <summary>
    /// A free spot for one more unit of the given radius: the first place (rings from 300 outward, along the group's sector first, then the whole circle)
    /// that keeps half a <see cref="Gap"/> to every taken position. Deterministic.
    /// </summary>
    public static Offset FreeSpot(int sectorIndex, int sectorCount, double radius, IReadOnlyList<(Offset At, double Radius)> taken)
    {
        double sector = 2 * Math.PI / Math.Max(1, sectorCount);
        double centre = Math.Max(0, sectorIndex) * sector;
        bool Free(Offset p) => taken.All(t => Math.Sqrt(Math.Pow(t.At.X - p.X, 2) + Math.Pow(t.At.Y - p.Y, 2)) >= t.Radius + radius + Gap / 2.0);
        for (int pass = 0; pass < 2; pass++)
            for (int ring = InnerRing; ring <= MaxAbsOffset - 60; ring += 30)
            {
                double span = pass == 0 ? sector : 2 * Math.PI;
                double start = pass == 0 ? centre - sector / 2 : 0;
                int steps = Math.Max(1, (int)Math.Floor(span * ring / Math.Max(10.0, 2 * radius + Gap / 2.0)));
                for (int i = 0; i < steps; i++)
                {
                    double a = start + (i + 0.5) * span / steps;
                    var p = new Offset((int)Math.Round(ring * Math.Cos(a), MidpointRounding.AwayFromZero), (int)Math.Round(ring * Math.Sin(a), MidpointRounding.AwayFromZero));
                    if (Free(p)) return p;
                }
            }
        return new Offset(InnerRing, 0);
    }
}
