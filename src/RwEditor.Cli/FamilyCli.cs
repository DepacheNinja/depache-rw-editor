using RwEditor.Core.Model;

/// <summary>The E2b commands: links (every unit-to-unit data link) and families (the browser's grouping), both read-only.</summary>
static class FamilyCli
{
    public static int Links(Opts o)
    {
        o.Only("mod", "unit", "kind", "unresolved", "missing");
        var model = UnitModel.Load(o.Req("mod"));
        var links = UnitLinks.Read(model);
        string? unit = o.Get("unit"), kind = o.Get("kind");
        bool list = unit != null || kind != null || o.Flag("missing");
        foreach (var l in links.Links)
        {
            if (!list) break;
            if (unit != null && l.From.Name != unit && l.TargetName != unit) continue;
            if (kind != null && !l.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (o.Flag("missing") && l.Target != null) continue;
            Console.WriteLine($"{l.From.Name,-32} {l.Kind,-12} {(l.Trigger == ConvertTrigger.None ? "" : l.Trigger.ToString()),-12} -> {l.TargetName,-32}{(l.Target == null ? " (NO SUCH UNIT)" : "")}  [{l.Section}] {l.Key}  {l.Origin.File}:{l.Origin.Line}");
        }
        if (o.Flag("unresolved"))
            foreach (var x in links.Unresolved)
                if (unit == null || x.From.Name == unit)
                    Console.WriteLine($"UNRESOLVED {x.From.Name,-32} {x.Kind,-12} [{x.Section}] {x.Key}: {x.RawValue}  {x.Origin.File}:{x.Origin.Line}");
        Console.WriteLine();
        foreach (var g in links.Links.GroupBy(l => l.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key,-12} links {g.Count(),6}  to a loadable unit {g.Count(l => l.Target != null),6}  to no such unit {g.Count(l => l.Target == null),5}  distinct pairs {g.Where(l => l.Target != null).Select(l => (l.From.RelPath, l.Target!.RelPath)).Distinct().Count(),6}");
        foreach (var g in links.Links.Where(l => l.Kind == LinkKind.ConvertTo && l.Target != null).GroupBy(l => l.Trigger).OrderBy(g => g.Key))
            Console.WriteLine($"  convertTo {g.Key,-12} {g.Count(),5}");
        Console.WriteLine($"unresolved link values: {links.Unresolved.Count}");
        foreach (var g in links.Unresolved.GroupBy(l => l.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"unresolved {g.Key,-12} {g.Count(),5} (units {g.Select(x => x.From.RelPath).Distinct().Count()})");
        return 0;
    }

    public static int Families(Opts o)
    {
        o.Only("mod", "unit", "detail", "top", "without");
        var model = UnitModel.Load(o.Req("mod"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var u in model.Units) model.Merge(u);   // the app has merged every unit already (browser rows); time the links on their own
        double mergeMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var links = UnitLinks.Read(model);
        double linkMs = sw.Elapsed.TotalMilliseconds;
        string without = o.Get("without") ?? "";
        var opts = new FamilyOptions(UseUpgradedFrom: !without.Contains("upgradedFrom", StringComparison.OrdinalIgnoreCase),
            UseCopyFrom: !without.Contains("copyFrom", StringComparison.OrdinalIgnoreCase),
            UseNamedWithSprite: !without.Contains("sprite", StringComparison.OrdinalIgnoreCase));
        var fam = UnitFamilies.Build(model, links, opts);
        double ms = sw.Elapsed.TotalMilliseconds;
        string? unit = o.Get("unit");
        if (unit != null)
        {
            var u = model.Find(unit) ?? throw new UsageException($"no loadable unit named '{unit}'");
            Print(fam.FamilyOf(u), fam, int.MaxValue);
        }
        if (o.Flag("detail"))
            foreach (var f in fam.Items) Print(f, fam, 400);
        int top = int.TryParse(o.Get("top"), out var t) ? t : 0;
        if (top > 0)
        {
            Console.WriteLine($"largest {top} families:");
            foreach (var f in fam.Items.Where(f => !f.IsBucket).OrderByDescending(f => f.Members.Count).ThenBy(f => f.Root!.RelPath, StringComparer.OrdinalIgnoreCase).Take(top))
                Console.WriteLine($"  {f.Members.Count + 1,4}  {f.Root!.Name} ({f.Root.RelPath})");
        }
        var s = fam.Stats;
        Console.WriteLine();
        Console.WriteLine($"entries {s.Entries}; complete {s.Complete} (buildable {s.Buildable}, starting unit {s.StartingUnits}, map-editor named {s.MapEditorNamed}, named with a sprite {s.NamedWithSprite})");
        Console.WriteLine($"top-level units (family roots) {s.Roots}; entries inside them {s.InsideFamilies} (variants {s.Variants}, parts {s.Parts}); " +
            $"shared parts {s.SharedParts}; helper parts {s.HelperParts}; total {s.Roots + s.InsideFamilies + s.SharedParts + s.HelperParts}");
        Console.WriteLine($"complete units inside a family (not roots) {s.CompleteInsideFamilies}; part groups used by more than one unit {s.PartGroupsWithSeveralSources}; " +
            $"helper-only sources ignored next to a real owner {s.SharedSourceIgnoredHelper}");
        Console.WriteLine($"links: unresolved values {s.UnresolvedLinkValues}, to no such unit {s.LinksToNoSuchUnit}; upgradedFrom from shared templates ignored {s.UpgradedFromTemplateIgnored}; " +
            $"copyFrom of a unit file ignored: not both complete {s.CopyFromNotBothCompleteIgnored}, both buildable on their own {s.CopyFromBothBuildableIgnored}");
        Console.WriteLine($"units merged in {mergeMs:0} ms, links read in {linkMs:0} ms, families built in {ms - linkMs:0} ms");
        return 0;
    }

    static void Print(UnitFamily f, UnitFamilies fam, int max)
    {
        Console.WriteLine(f.IsBucket ? $"[{f.BucketTitle}] {f.Members.Count} entries" : $"{f.Root!.Name}  ({f.Root.RelPath}; {fam.Complete[f.Root]}) - {f.Members.Count} inside");
        foreach (var e in f.Members.Take(max))
            Console.WriteLine($"  {new string(' ', 2 * Math.Max(0, e.Depth - 1))}{e.Unit.Name,-34} {e.Label}{(fam.Complete[e.Unit] != CompleteReason.None ? "  [complete: " + fam.Complete[e.Unit] + "]" : "")}");
        if (f.Members.Count > max) Console.WriteLine($"  ... {f.Members.Count - max} more");
    }
}
