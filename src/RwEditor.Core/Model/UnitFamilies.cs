namespace RwEditor.Core.Model;

/// <summary>How a family member is related to the member it hangs under (the label the browser shows).</summary>
public enum FamilyRelation
{
    /// <summary>The family root itself.</summary>
    Root,
    /// <summary>The in-water form: the land form converts into it on water (autoTrigger tests overWater), or it converts back when not on water.</summary>
    WaterVersion,
    /// <summary>The land form of a unit that hangs under its in-water form.</summary>
    LandVersion,
    /// <summary>A stand-in that turns into the anchor as soon as it is created / finished (convertTo on the created / completed / completeAndActive event).</summary>
    NanoBuild,
    /// <summary>The anchor's player action (an upgrade button) converts the anchor into it. <see cref="FamilyEntry.Level"/> = upgrade level.</summary>
    Upgrade,
    /// <summary>Its <c>[ai] upgradedFrom</c> names the anchor (declared in a unit file, not in a shared template).</summary>
    UpgradedFrom,
    /// <summary>Its own file copies the anchor's unit file (<c>[core] copyFrom</c>), both are complete units, and not both are buildable on their own.</summary>
    BasedOn,
    /// <summary>The two are joined by a convert that is not an upgrade button, water switch or nano build (hidden state switches, tier syncing of
    /// attached parts), or the entry turns into the anchor on creation while the anchor is not the product's root.</summary>
    Converts,
    /// <summary>It upgrades INTO the anchor (its own upgrade button / upgradedFrom points at a member nearer the root: two units that upgrade into the
    /// same thing, e.g. gunMech and rocketMech both into dualMech).</summary>
    UpgradesInto,
    /// <summary>The anchor's file copies its unit file (the inverse of <see cref="BasedOn"/>).</summary>
    BaseOf,
    /// <summary>Created as an attached part of the anchor (attachment onCreateSpawnUnitOf / attachments_addNewUnits).</summary>
    PartOf,
    /// <summary>Spawned by the anchor (spawnUnits / spawnUnit / unitsSpawnedOnDeath / addUnitsIntoTransport).</summary>
    SpawnedBy,
    /// <summary>The first entry of a group inside a bucket (not under a unit).</summary>
    InBucket,
}

/// <summary>Why an entry is a "complete" unit (may be a family root). Several can hold.</summary>
[Flags]
public enum CompleteReason
{
    None = 0,
    /// <summary>Named in a build list of another unit (<c>[canBuild_*] name</c>, legacy <c>canBuild_N_name</c>) or an action's <c>produceUnits</c>, in a
    /// section that is not literally <c>isVisible: false</c>.</summary>
    Buildable = 1,
    /// <summary><c>[core] isPickableStartingUnit: true</c> (engine default false, javap units/custom/ag offset 1349).</summary>
    StartingUnit = 2,
    /// <summary><c>[core] showInEditor</c> not false (engine default TRUE, javap units/custom/ag offset 1587-1590) AND a non-empty <c>displayText</c>:
    /// a named unit the map editor lists.</summary>
    MapEditorNamed = 4,
    /// <summary>A non-empty <c>displayText</c> AND a real body sprite: <c>[graphics] image</c> resolves to a file name that is not <c>blank.png</c> / NONE
    /// and the body is not hidden (<c>[graphics] isVisible</c> not false). A named, drawn unit even when nothing builds it (creeps spawned by nests,
    /// units only reached through an upgrade).</summary>
    NamedWithSprite = 8,
}

/// <summary>One unit inside a family or bucket, in display order. <paramref name="Anchor"/> = the entry it hangs under; <paramref name="Depth"/> = 1 for
/// entries directly under the root (or a bucket group head = 0); <paramref name="Evidence"/> = the data link the relation was read from;
/// <paramref name="Level"/> = upgrade level for upgrades, the number of distinct units that use a bucket group head, else 0.</summary>
public sealed record FamilyEntry(UnitType Unit, FamilyRelation Relation, UnitType? Anchor, int Level, int Depth, UnitLink? Evidence)
{
    /// <summary>The label the browser shows next to the entry, e.g. "Upgrade level 2", "Water version of ACV2", "Part of ACV".</summary>
    public string Label => Relation switch
    {
        FamilyRelation.WaterVersion => $"Water version of {Anchor!.Name}",
        FamilyRelation.LandVersion => $"Land version of {Anchor!.Name}",
        FamilyRelation.NanoBuild => $"Nano build of {Anchor!.Name}",
        FamilyRelation.Upgrade => Level > 1 ? $"Upgrade level {Level}" : $"Upgrade of {Anchor!.Name}",
        FamilyRelation.UpgradedFrom => $"Upgrade of {Anchor!.Name} (AI)",
        FamilyRelation.BasedOn => $"Based on {Anchor!.Name}",
        FamilyRelation.UpgradesInto => $"Upgrades into {Anchor!.Name}",
        FamilyRelation.BaseOf => $"Base of {Anchor!.Name}",
        FamilyRelation.Converts => $"Switches with {Anchor!.Name}",
        FamilyRelation.PartOf => $"Part of {Anchor!.Name}",
        FamilyRelation.SpawnedBy => $"Spawned by {Anchor!.Name}",
        FamilyRelation.InBucket => Evidence == null ? "Not used by any unit" : Level > 1 ? $"Used by {Evidence.From.Name} and {Level - 1} more" : $"Used by {Evidence.From.Name}",
        _ => "",
    };
}

public enum BucketKind
{
    /// <summary>Not a bucket: a top-level unit with its members.</summary>
    None,
    /// <summary>Parts / spawned helpers used by units of two or more different families (e.g. infoTips): no single owner.</summary>
    SharedParts,
    /// <summary>Not complete and not linked to any unit by the data (or only through other such helpers).</summary>
    HelperParts,
}

/// <summary>A top-level item of the browser: a family (root + members) or a bucket (no root unit, members only).</summary>
public sealed class UnitFamily
{
    internal UnitFamily(UnitType? root, BucketKind bucket, IReadOnlyList<FamilyEntry> members)
    {
        Root = root; Bucket = bucket; Members = members;
    }

    /// <summary>The family's root unit (null for a bucket).</summary>
    public UnitType? Root { get; }
    public BucketKind Bucket { get; }
    public bool IsBucket => Bucket != BucketKind.None;
    /// <summary>The entries under the root (the root itself is not in this list), or the bucket's entries, in display order.</summary>
    public IReadOnlyList<FamilyEntry> Members { get; }

    public string BucketTitle => TitleOf(Bucket);

    public static string TitleOf(BucketKind b) => b switch
    {
        BucketKind.SharedParts => "Shared parts (used by several units)",
        BucketKind.HelperParts => "Helper parts (used by no unit)",
        _ => "",
    };
}

/// <summary>Counts of the rules, for the report and the tests. Every entry is counted exactly once in
/// <see cref="Roots"/> + <see cref="InsideFamilies"/> + <see cref="SharedParts"/> + <see cref="HelperParts"/>.</summary>
public sealed record FamilyStats(int Entries, int Complete, int Buildable, int StartingUnits, int MapEditorNamed, int NamedWithSprite, int Roots, int InsideFamilies,
    int Variants, int Parts, int SharedParts, int HelperParts, int CompleteInsideFamilies, int PartGroupsWithSeveralSources, int SharedSourceIgnoredHelper,
    int UnresolvedLinkValues, int LinksToNoSuchUnit, int UpgradedFromTemplateIgnored, int CopyFromNotBothCompleteIgnored, int CopyFromBothBuildableIgnored);

/// <summary>Switches for diagnostics (the defaults are the shipped rules).</summary>
public sealed record FamilyOptions(bool UseUpgradedFrom = true, bool UseCopyFrom = true, bool UseNamedWithSprite = true)
{
    public static readonly FamilyOptions Default = new();
}

/// <summary>
/// Groups the loadable units of a mod into families for the browser (E2b): complete units at the top, every related entry (upgrade levels, water / land
/// forms, nano builds, attached parts, spawned helpers) under them, and two buckets for helpers that have no single owner. Built ONLY from data links
/// (<see cref="UnitLinks"/>); nothing is inferred from names. Deterministic: same input, same output (every choice ends in an ordinal tie-break on the
/// model's unit order = file path). Every loadable entry appears exactly once (as a root, a member, or in a bucket).
///
/// Rules (CHANGELOG C-030; counts printed by <c>rwedit families</c>):
/// (a) COMPLETE = Buildable OR StartingUnit OR MapEditorNamed OR NamedWithSprite (<see cref="CompleteReason"/>).
/// (b) VARIANT links join units into one family: every resolved convertTo (any trigger, incl. whenBuilding_temporarilyConvertTo and legacy
///     action_N_convertTo); [ai] upgradedFrom only when the value comes from a UNIT file (a shared template's upgradedFrom is a default copied into every
///     unit using it, e.g. heroTemplate.ini "upgradedFrom: hoverHero_t1" in every hero, so it says nothing about one unit); copyFrom of another unit's
///     file only when BOTH are complete (helpers copy dummyUnit.ini etc. as a base, which is not a family relation) and NOT both are buildable / starting
///     units on their own (lightTank copies mediumTank.ini: two products; such a link still orders the tree when other links join the two anyway).
///     Root = among the complete members, the one derived from the fewest other members (derived = upgrade target from source, nano build from product,
///     water form from land form, upgradedFrom / copyFrom child from parent; a member that is both ancestor and descendant does not count), then
///     buildable / starting unit first, then file path. Members hang under the member their most specific relation points to (water / land, nano,
///     upgrade, based on, upgradedFrom, other converts); among equals the main line wins over water / nano forms, then the parent whose unit file the
///     member copies most directly, then path. Upgrade level = upgrade steps below the root + 1.
/// (c) PARTS: a variant group without a complete member is a part group. It hangs under the ONE family that links to it by attachment or spawn
///     (directly or through part groups that already hang there); links from two or more families make it a SHARED part (links from unused helper parts
///     do not count when a real owner exists); no link at all makes it a HELPER part. Complete units are never parts (a spawned complete unit stays a root).
/// </summary>
public sealed class UnitFamilies
{
    UnitFamilies(IReadOnlyList<UnitFamily> items, IReadOnlyDictionary<UnitType, CompleteReason> complete, FamilyStats stats, UnitLinks links,
        IReadOnlyDictionary<UnitType, (UnitFamily Family, FamilyEntry? Entry)> where)
    {
        Items = items; Complete = complete; Stats = stats; Links = links; _where = where;
    }

    readonly IReadOnlyDictionary<UnitType, (UnitFamily Family, FamilyEntry? Entry)> _where;

    /// <summary>Families in root path order, then the buckets (shared, then helper).</summary>
    public IReadOnlyList<UnitFamily> Items { get; }
    public IReadOnlyDictionary<UnitType, CompleteReason> Complete { get; }
    public FamilyStats Stats { get; }
    public UnitLinks Links { get; }

    /// <summary>The family or bucket that holds the unit.</summary>
    public UnitFamily FamilyOf(UnitType u) => _where[u].Family;

    /// <summary>The unit's entry (null when the unit is a family root).</summary>
    public FamilyEntry? EntryOf(UnitType u) => _where[u].Entry;

    public static UnitFamilies Build(UnitModel model) => Build(model, UnitLinks.Read(model), FamilyOptions.Default);

    public static UnitFamilies Build(UnitModel model, UnitLinks links, FamilyOptions options)
    {
        var units = model.Units;
        var index = new Dictionary<UnitType, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < units.Count; i++) index[units[i]] = i;
        int n = units.Count;

        // ------------------------------------------------------------------ (a) complete
        var reason = new CompleteReason[n];
        foreach (var l in links.Links)
        {
            if (l.Target == null || ReferenceEquals(l.Target, l.From)) continue;
            if (l.Kind is not (LinkKind.CanBuild or LinkKind.Produce)) continue;
            if (IsHiddenBuildOption(model, l)) continue;
            reason[index[l.Target]] |= CompleteReason.Buildable;
        }
        for (int i = 0; i < n; i++)
        {
            var m = model.Merge(units[i]);
            if (IsTrue(m.Get("core", "isPickableStartingUnit")?.Value)) reason[i] |= CompleteReason.StartingUnit;
            bool showInEditor = !IsFalse(m.Get("core", "showInEditor")?.Value);
            bool named = !string.IsNullOrWhiteSpace(m.Get("core", "displayText")?.Value);
            if (showInEditor && named) reason[i] |= CompleteReason.MapEditorNamed;
            if (options.UseNamedWithSprite && named && HasSprite(m)) reason[i] |= CompleteReason.NamedWithSprite;
        }
        bool IsComplete(int i) => reason[i] != CompleteReason.None;
        bool Obtainable(int i) => (reason[i] & (CompleteReason.Buildable | CompleteReason.StartingUnit)) != 0;

        // ------------------------------------------------------------------ (b) variant links: (A, B, relation of B to A, evidence); B is derived from A
        var variant = new List<Edge>();
        var placementOnly = new List<Edge>();
        int skippedTemplateUf = 0, skippedCopy = 0, skippedCopyBoth = 0;
        var unitFiles = new HashSet<string>(units.Select(u => u.RelPath), StringComparer.OrdinalIgnoreCase);
        foreach (var l in links.Links)
        {
            if (l.Target == null || ReferenceEquals(l.Target, l.From)) continue;
            int f = index[l.From], t = index[l.Target];
            switch (l.Kind)
            {
                case LinkKind.ConvertTo:
                    variant.Add(l.Trigger switch
                    {
                        ConvertTrigger.PlayerAction => new Edge(f, t, FamilyRelation.Upgrade, l),        // t is the upgrade of f
                        ConvertTrigger.OnCreate => new Edge(t, f, FamilyRelation.NanoBuild, l),          // f is the nano build of t
                        ConvertTrigger.ToWater => new Edge(f, t, FamilyRelation.WaterVersion, l),        // t is f on water
                        ConvertTrigger.ToLand => new Edge(t, f, FamilyRelation.WaterVersion, l),         // f is t on water
                        _ => new Edge(f, t, FamilyRelation.Converts, l),
                    });
                    break;
                case LinkKind.UpgradedFrom when options.UseUpgradedFrom:
                    if (!unitFiles.Contains(l.Origin.File)) { skippedTemplateUf++; break; }
                    variant.Add(new Edge(t, f, FamilyRelation.UpgradedFrom, l));
                    break;
                case LinkKind.CopyFromUnit when options.UseCopyFrom:
                    // helpers copy dummyUnit.ini etc. as a base (not a family relation); two units the player can each build on their own are two products
                    // (lightTank copies mediumTank.ini, fortBaseD/R/U copy fortBaseL.ini)
                    if (!(IsComplete(f) && IsComplete(t))) { skippedCopy++; break; }
                    // (they still order the tree when other links put both into one family, e.g. fortBaseD/R/U under fortBaseL)
                    if (Obtainable(f) && Obtainable(t)) { skippedCopyBoth++; placementOnly.Add(new Edge(t, f, FamilyRelation.BasedOn, l)); break; }
                    variant.Add(new Edge(t, f, FamilyRelation.BasedOn, l));
                    break;
            }
        }

        var uf = new UnionFind(n);
        foreach (var e in variant) uf.Union(e.A, e.B);
        var groups = new SortedDictionary<int, List<int>>();     // representative = smallest index of the group
        for (int i = 0; i < n; i++)
        {
            int r = uf.Find(i);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
            g.Add(i);
        }
        var edgesOf = new Dictionary<int, List<Edge>>();
        foreach (var e in variant)
        {
            int g = uf.Find(e.A);
            if (!edgesOf.TryGetValue(g, out var list)) edgesOf[g] = list = new();
            list.Add(e);
        }
        foreach (var e in placementOnly)
        {
            int g = uf.Find(e.A);
            if (g != uf.Find(e.B)) continue;
            if (!edgesOf.TryGetValue(g, out var list)) edgesOf[g] = list = new();
            list.Add(e);
        }
        List<Edge> EdgesOf(int gid) => edgesOf.TryGetValue(gid, out var el) ? el : new();

        // ------------------------------------------------------------------ roots of complete groups
        var groupRoot = new Dictionary<int, int>();
        int completeInside = 0;
        foreach (var (gid, members) in groups)
        {
            if (!members.Any(IsComplete)) continue;
            completeInside += members.Count(IsComplete) - 1;
            groupRoot[gid] = ChooseRoot(members, EdgesOf(gid), reason);
        }

        // ------------------------------------------------------------------ (c) part groups: owner = a complete group, Shared or Helper
        const int Shared = -1, Helper = -2, Pending = -3;
        var owner = new Dictionary<int, int>();
        foreach (var gid in groups.Keys) owner[gid] = groupRoot.ContainsKey(gid) ? gid : Pending;
        var incoming = new Dictionary<int, List<UnitLink>>();
        foreach (var l in links.Links)
        {
            if (l.Target == null || ReferenceEquals(l.Target, l.From) || l.Kind is not (LinkKind.Attachment or LinkKind.Spawn)) continue;
            int tg = uf.Find(index[l.Target]), fg = uf.Find(index[l.From]);
            if (tg == fg || groupRoot.ContainsKey(tg)) continue;
            if (!incoming.TryGetValue(tg, out var list)) incoming[tg] = list = new();
            list.Add(l);
        }
        int ignoredHelper = 0;
        int Decide(List<UnitLink> inl, bool final)
        {
            var owners = new SortedSet<int>();
            bool waiting = false;
            foreach (var l in inl)
            {
                int o = owner[uf.Find(index[l.From])];
                if (o == Pending) waiting = true; else owners.Add(o);
            }
            if (owners.Contains(Shared)) return Shared;
            if (owners.Count > 1 && owners.Contains(Helper)) { owners.Remove(Helper); if (final) ignoredHelper++; }
            if (owners.Count > 1) return Shared;
            if (waiting && !final) return Pending;
            return owners.Count == 1 ? owners.Min : Helper;
        }
        while (true)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var gid in groups.Keys)
                {
                    if (owner[gid] != Pending) continue;
                    int o = incoming.TryGetValue(gid, out var inl) ? Decide(inl, false) : Helper;
                    if (o != Pending) { owner[gid] = o; changed = true; }
                }
            }
            // what is still pending waits on other pending groups (parts that use each other): decide the FIRST one (path order) that has a known
            // owner with what is known, then propagate again; groups that only hang on each other end as helper parts
            int next = groups.Keys.FirstOrDefault(g => owner[g] == Pending && incoming[g].Any(l => owner[uf.Find(index[l.From])] != Pending), -1);
            if (next < 0) break;
            owner[next] = Decide(incoming[next], true);
        }
        foreach (var gid in groups.Keys) if (owner[gid] == Pending) owner[gid] = Helper;
        ignoredHelper = 0;
        foreach (var gid in groups.Keys) if (!groupRoot.ContainsKey(gid) && incoming.TryGetValue(gid, out var il2) && owner[gid] >= 0) Decide(il2, true);

        // ------------------------------------------------------------------ assemble: one tree per family / bucket, flattened depth-first
        var placedOf = new Dictionary<int, Dictionary<int, Placement>>();   // owner (group id, Shared, Helper) -> unit -> placement
        Dictionary<int, Placement> Bag(int o) { if (!placedOf.TryGetValue(o, out var b)) placedOf[o] = b = new(); return b; }
        // copyFrom distance: position of the parent's file in the child's merge chain (1 = the child's own file copies it directly); none = int.MaxValue
        int ChainDistance(int child, int parent)
        {
            var chain = model.Merge(units[child]).ChainFiles;
            for (int i = 1; i < chain.Count; i++) if (chain[i].Equals(units[parent].RelPath, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
        foreach (var (gid, root) in groupRoot)
            foreach (var (k, p) in Place(root, groups[gid], EdgesOf(gid), ChainDistance, units)) Bag(gid)[k] = p;
        int multiSource = 0;
        var headUsers = new Dictionary<int, int>();
        // inside a bucket, a group used by exactly ONE other group of the same bucket hangs under it (flashSpark under flash); cycles are cut at their
        // first group in path order, which then stays a bucket head
        var nestIn = new Dictionary<int, UnitLink>();
        foreach (var (gid, _) in groups)
        {
            if (groupRoot.ContainsKey(gid) || owner[gid] >= 0 || !incoming.TryGetValue(gid, out var inl0)) continue;
            var srcGroups = inl0.Select(l => uf.Find(index[l.From])).Distinct().ToList();
            if (srcGroups.Count == 1 && owner[srcGroups[0]] == owner[gid])
                nestIn[gid] = inl0.OrderBy(l => index[l.From]).ThenBy(l => index[l.Target!]).First();
        }
        foreach (var gid in nestIn.Keys.OrderBy(g => g).ToList())
        {
            var seen = new HashSet<int> { gid };
            for (int cur = gid; nestIn.TryGetValue(cur, out var up);)
            {
                cur = uf.Find(index[up.From]);
                if (cur == gid) { nestIn.Remove(gid); break; }
                if (!seen.Add(cur)) break;
            }
        }
        // a part group of a family hangs under the first unit (path order) of the NEAREST already-placed group that links to it: the family's own
        // variant group first, then the part groups hanging on it, level by level, so the tree never loops
        var partLink = new Dictionary<int, UnitLink>();
        var fallbackGroups = new HashSet<int>();
        foreach (var byOwner in groups.Keys.Where(g => !groupRoot.ContainsKey(g) && owner[g] >= 0).GroupBy(g => owner[g]))
        {
            var placedGroups = new HashSet<int> { byOwner.Key };
            var todo = byOwner.OrderBy(g => g).ToList();
            while (todo.Count > 0)
            {
                var found = new List<(int G, UnitLink L)>();
                foreach (var g in todo)
                {
                    var l = incoming[g].Where(x => placedGroups.Contains(uf.Find(index[x.From]))).OrderBy(x => index[x.From]).ThenBy(x => index[x.Target!]).FirstOrDefault();
                    if (l != null) found.Add((g, l));
                }
                if (found.Count == 0) break;
                foreach (var (g, l) in found) { partLink[g] = l; placedGroups.Add(g); todo.Remove(g); }
            }
            // cannot happen (an owner is only given through a chain of links to the family); kept so nothing is ever lost: under the root directly
            foreach (var g in todo) { partLink[g] = incoming[g].Where(x => owner[uf.Find(index[x.From])] == byOwner.Key).OrderBy(x => index[x.From]).First(); fallbackGroups.Add(g); }
        }
        foreach (var (gid, members) in groups)
        {
            if (groupRoot.ContainsKey(gid)) continue;
            var edges = EdgesOf(gid);
            var inl = incoming.TryGetValue(gid, out var il) ? il : new List<UnitLink>();
            if (inl.Select(l => l.From).Distinct().Count() > 1) multiSource++;
            int o = owner[gid];
            UnitLink? first = o >= 0 ? partLink[gid]
                : nestIn.TryGetValue(gid, out var nl) ? nl : inl.OrderBy(l => index[l.From]).ThenBy(l => index[l.Target!]).FirstOrDefault();
            bool hangs = o >= 0 || nestIn.ContainsKey(gid);
            int head = hangs ? index[first!.Target!] : members.Min();
            headUsers[head] = inl.Select(l => l.From).Distinct().Count();
            var bag = Bag(o);
            int parent = hangs ? index[first!.From] : -1;
            if (fallbackGroups.Contains(gid)) parent = groupRoot[o];   // the fallback above
            bag[head] = hangs
                ? new Placement(parent, first!.Kind == LinkKind.Attachment ? FamilyRelation.PartOf : FamilyRelation.SpawnedBy, first)
                : new Placement(-1, FamilyRelation.InBucket, first);
            foreach (var (k, p) in Place(head, members, edges, ChainDistance, units)) if (k != head) bag[k] = p;
        }

        var items = new List<UnitFamily>();
        var where = new Dictionary<UnitType, (UnitFamily, FamilyEntry?)>(ReferenceEqualityComparer.Instance);
        int inside = 0, variants = 0, parts = 0;
        foreach (var gid in groupRoot.Keys.OrderBy(g => groupRoot[g]))
        {
            int root = groupRoot[gid];
            var list = Flatten(new[] { root }, Bag(gid), units, includeHeads: false);
            var fam = new UnitFamily(units[root], BucketKind.None, list);
            items.Add(fam);
            where[units[root]] = (fam, null);
            foreach (var e in list) where[e.Unit] = (fam, e);
            inside += list.Count;
            variants += list.Count(e => uf.Find(index[e.Unit]) == gid);
            parts += list.Count(e => uf.Find(index[e.Unit]) != gid);
        }
        int sharedCount = 0, helperCount = 0;
        foreach (var (o, kind) in new[] { (Shared, BucketKind.SharedParts), (Helper, BucketKind.HelperParts) })
        {
            if (!placedOf.TryGetValue(o, out var bag)) continue;
            var heads = bag.Where(kv => kv.Value.Parent < 0).Select(kv => kv.Key).OrderBy(k => k).ToList();
            var list = Flatten(heads, bag, units, includeHeads: true, headUsers);
            var b = new UnitFamily(null, kind, list);
            items.Add(b);
            foreach (var e in list) where[e.Unit] = (b, e);
            if (o == Shared) sharedCount = list.Count; else helperCount = list.Count;
        }

        var complete = new Dictionary<UnitType, CompleteReason>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++) complete[units[i]] = reason[i];
        var stats = new FamilyStats(n, reason.Count(r => r != 0), reason.Count(r => r.HasFlag(CompleteReason.Buildable)),
            reason.Count(r => r.HasFlag(CompleteReason.StartingUnit)), reason.Count(r => r.HasFlag(CompleteReason.MapEditorNamed)), reason.Count(r => r.HasFlag(CompleteReason.NamedWithSprite)),
            groupRoot.Count, inside, variants, parts, sharedCount, helperCount, completeInside, multiSource, ignoredHelper,
            links.Unresolved.Count, links.Links.Count(l => l.Target == null), skippedTemplateUf, skippedCopy, skippedCopyBoth);
        if (where.Count != n || groupRoot.Count + inside + sharedCount + helperCount != n)
            throw new InvalidOperationException($"unit families lost or duplicated entries: {where.Count} placed of {n}");
        return new UnitFamilies(items, complete, stats, links, where);
    }

    readonly record struct Edge(int A, int B, FamilyRelation Rel, UnitLink Ev);

    /// <summary>Where one unit hangs: the parent unit index (-1 = a root / bucket head), the relation and the link it was read from.</summary>
    readonly record struct Placement(int Parent, FamilyRelation Rel, UnitLink? Ev);

    /// <summary>[graphics] image names a real picture (expressions resolved with <see cref="LinkExpr"/>; unresolvable = no) and the body is drawn.</summary>
    static bool HasSprite(MergedUnit m)
    {
        if (IsFalse(m.Get("graphics", "isVisible")?.Value)) return false;
        var img = m.Get("graphics", "image")?.Value;
        if (string.IsNullOrWhiteSpace(img)) return false;
        var r = LinkExpr.Resolve(img.Trim(), m, "graphics");
        if (string.IsNullOrWhiteSpace(r) || r.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)) return false;
        string file = r.Trim().Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        if (file.Contains(':')) file = file[(file.LastIndexOf(':') + 1)..];
        return !file.Equals("blank.png", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTrue(string? v) => (v ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    static bool IsFalse(string? v) => (v ?? "").Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>A build option the player never sees: its section (or legacy canBuild_N_isVisible) is literally false. Conditional (expression) visibility
    /// counts as visible.</summary>
    static bool IsHiddenBuildOption(UnitModel model, UnitLink l)
    {
        var m = model.Merge(l.From);
        string? vis = l.Section.Equals("core", StringComparison.OrdinalIgnoreCase) && l.Key.EndsWith("_name", StringComparison.OrdinalIgnoreCase)
            ? m.Get("core", l.Key[..^"_name".Length] + "_isVisible")?.Value
            : m.Get(l.Section, "isVisible")?.Value;
        return IsFalse(vis);
    }

    static HashSet<int> Reach(int start, Dictionary<int, List<int>> next)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>(); stack.Push(start);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (!next.TryGetValue(x, out var nx)) continue;
            foreach (var y in nx) if (y != start && seen.Add(y)) stack.Push(y);
        }
        return seen;
    }

    static int ChooseRoot(List<int> members, List<Edge> edges, CompleteReason[] reason)
    {
        var parents = new Dictionary<int, List<int>>();
        var children = new Dictionary<int, List<int>>();
        foreach (var e in edges.Where(e => e.Rel != FamilyRelation.Converts))   // "Converts" has no direction
        {
            if (!parents.TryGetValue(e.B, out var p)) parents[e.B] = p = new(); p.Add(e.A);
            if (!children.TryGetValue(e.A, out var c)) children[e.A] = c = new(); c.Add(e.B);
        }
        return members.Where(i => reason[i] != CompleteReason.None)
            .Select(i => { var anc = Reach(i, parents); anc.ExceptWith(Reach(i, children)); return (I: i, Anc: anc.Count); })
            .OrderBy(x => x.Anc)
            .ThenBy(x => (reason[x.I] & (CompleteReason.Buildable | CompleteReason.StartingUnit)) != 0 ? 0 : 1)
            .ThenBy(x => x.I)
            .First().I;
    }


    /// <summary>Relation priority when a member could hang under several placed members: the most specific first.</summary>
    static int Priority(FamilyRelation r) => r switch
    {
        FamilyRelation.WaterVersion => 0,
        FamilyRelation.LandVersion => 1,
        FamilyRelation.NanoBuild => 2,
        FamilyRelation.Upgrade => 3,
        FamilyRelation.BasedOn => 4,
        FamilyRelation.UpgradedFrom => 5,   // an AI hint: the weakest of the directed relations
        _ => 6,
    };

    /// <summary>
    /// Places the members of one variant group under <paramref name="head"/> (Prim-style: repeatedly attach the best candidate to the tree built so far).
    /// An edge used against its direction gets the inverse relation (the land form of a water head; otherwise "Converts"). Among candidates: the most
    /// specific relation; then a parent on the main line (not itself a water / land form or nano build); then the parent whose unit file the child copies
    /// most directly (copyFrom chain: ACV3 copies ACV2.ini which copies ACV.ini, so ACV3 hangs under ACV2 although ACV's inherited button also converts to
    /// ACV3, and ACV3's inherited "hull level 2" button back to ACV2 never makes ACV2 a child of ACV3); then path order (parent, child).
    /// </summary>
    static Dictionary<int, Placement> Place(int head, List<int> members, List<Edge> edges, Func<int, int, int> chainDistance, IReadOnlyList<UnitType> units)
    {
        var placed = new Dictionary<int, Placement> { [head] = new Placement(-1, FamilyRelation.Root, null) };
        var forms = new HashSet<int>(edges.Where(e => e.Rel is FamilyRelation.WaterVersion or FamilyRelation.NanoBuild).Select(e => e.B));
        var dist = new Dictionary<(int, int), int>();
        int Dist(int child, int parent) => dist.TryGetValue((child, parent), out var d) ? d : dist[(child, parent)] = chainDistance(child, parent);
        while (placed.Count < members.Count)
        {
            (int Child, Placement P, int Prio)? best = null;
            foreach (var e in edges)
            {
                (int Child, Placement P, int Prio)? c = null;
                if (placed.ContainsKey(e.A) && !placed.ContainsKey(e.B)) c = (e.B, new Placement(e.A, e.Rel, e.Ev), Priority(e.Rel));
                else if (placed.ContainsKey(e.B) && !placed.ContainsKey(e.A))
                {
                    var inv = e.Rel switch
                    {
                        FamilyRelation.WaterVersion => FamilyRelation.LandVersion,
                        FamilyRelation.Upgrade or FamilyRelation.UpgradedFrom => FamilyRelation.UpgradesInto,
                        FamilyRelation.BasedOn => FamilyRelation.BaseOf,
                        _ => FamilyRelation.Converts,
                    };
                    c = (e.A, new Placement(e.B, inv, e.Ev), Priority(inv) + 10);
                }
                if (c is not { } cv) continue;
                if (best is not { } b || Better(cv, b)) best = cv;
            }
            if (best is not { } chosen) break;   // cannot happen inside one union-find group
            placed[chosen.Child] = chosen.P;
        }
        foreach (var m in members) if (!placed.ContainsKey(m)) placed[m] = new Placement(head, FamilyRelation.Converts, null);
        return placed;

        bool Better((int Child, Placement P, int Prio) a, (int Child, Placement P, int Prio) b)
        {
            int c = a.Prio.CompareTo(b.Prio);
            if (c == 0) c = (forms.Contains(a.P.Parent) ? 1 : 0).CompareTo(forms.Contains(b.P.Parent) ? 1 : 0);
            if (c == 0) c = Dist(a.Child, a.P.Parent).CompareTo(Dist(b.Child, b.P.Parent));
            if (c == 0) c = a.P.Parent.CompareTo(b.P.Parent);
            if (c == 0) c = a.Child.CompareTo(b.Child);
            return c < 0;   // equal = keep the first edge found (link order is deterministic)
        }
    }

    /// <summary>Display order of siblings: the anchor's own forms first (water / land, nano build), then upgrades, then the rest.</summary>
    static int Order(FamilyRelation r) => r switch
    {
        FamilyRelation.WaterVersion => 0,
        FamilyRelation.LandVersion => 1,
        FamilyRelation.NanoBuild => 2,
        FamilyRelation.Upgrade => 3,
        FamilyRelation.UpgradedFrom => 4,
        FamilyRelation.BasedOn => 5,
        FamilyRelation.UpgradesInto => 6,
        FamilyRelation.BaseOf => 7,
        FamilyRelation.Converts => 8,
        FamilyRelation.PartOf => 9,
        FamilyRelation.SpawnedBy => 10,
        _ => 11,
    };

    /// <summary>Depth-first flattening of the placed tree(s). Upgrade level = the anchor's level + 1 for an upgrade (button or upgradedFrom), the anchor's
    /// level for its other forms; the root is level 1 (so "Upgrade level N" = N-1 upgrade steps below the root).</summary>
    static List<FamilyEntry> Flatten(IReadOnlyList<int> heads, Dictionary<int, Placement> bag, IReadOnlyList<UnitType> units, bool includeHeads,
        Dictionary<int, int>? headUsers = null)
    {
        var kids = new Dictionary<int, List<int>>();
        foreach (var (k, p) in bag) if (p.Parent >= 0) { if (!kids.TryGetValue(p.Parent, out var l)) kids[p.Parent] = l = new(); l.Add(k); }
        var result = new List<FamilyEntry>();
        var done = new HashSet<int>();
        void Walk(int x, int depth, int level)
        {
            if (!kids.TryGetValue(x, out var l)) return;
            foreach (var k in l.OrderBy(k => Order(bag[k].Rel)).ThenBy(k => k))
            {
                if (!done.Add(k)) continue;
                var p = bag[k];
                bool up = p.Rel is FamilyRelation.Upgrade or FamilyRelation.UpgradedFrom;
                int lv = up ? level + 1 : level;
                result.Add(new FamilyEntry(units[k], p.Rel, units[p.Parent], up ? lv : 0, depth, p.Ev));
                Walk(k, depth + 1, p.Rel is FamilyRelation.PartOf or FamilyRelation.SpawnedBy ? 1 : lv);
            }
        }
        foreach (var h in heads)
        {
            done.Add(h);
            if (includeHeads)
                result.Add(new FamilyEntry(units[h], FamilyRelation.InBucket, null, headUsers != null && headUsers.TryGetValue(h, out var nu) ? nu : 0, 0,
                    bag.TryGetValue(h, out var hp) ? hp.Ev : null));
            Walk(h, 1, 1);
        }
        return result;
    }

    sealed class UnionFind
    {
        readonly int[] _p;
        public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
        public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
        /// <summary>The smaller index becomes the representative (deterministic).</summary>
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a == b) return; if (a < b) _p[b] = a; else _p[a] = b; }
    }
}