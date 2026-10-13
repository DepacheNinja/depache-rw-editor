namespace RwEditor.Core.Model;

/// <summary>The kinds of data links between unit types that the family builder uses (E2b). Every kind is read from engine keys of the MERGED unit.</summary>
public enum LinkKind
{
    /// <summary><c>convertTo</c> in an <c>action_*</c> / <c>hiddenAction_*</c> section: the unit turns into the target in game.</summary>
    ConvertTo,
    /// <summary><c>[ai] upgradedFrom</c>: the engine's AI hint "this unit is an upgrade of the target" (javap: units/custom/ag reads [ai]upgradedFrom,
    /// "Could not find [ai]upgradedFrom target:" when missing).</summary>
    UpgradedFrom,
    /// <summary>The unit's OWN file lists another loadable unit's file in <c>[core] copyFrom</c>: the unit is defined as a modified copy of that unit.</summary>
    CopyFromUnit,
    /// <summary>The target is in a build list of the unit: <c>[canBuild_*] name</c> or legacy <c>[core] canBuild_N_name</c>.</summary>
    CanBuild,
    /// <summary>The target is made by an action's <c>produceUnits</c> (the mod's factory buttons: <c>produceUnits: ${unit}(...)</c>).</summary>
    Produce,
    /// <summary>The target is created as an attached part: <c>[attachment_*] onCreateSpawnUnitOf</c> or an action's <c>attachments_addNewUnits</c>.</summary>
    Attachment,
    /// <summary>The target is spawned by the unit: <c>spawnUnits</c> / <c>spawnUnit</c> (actions, projectiles), <c>[core] unitsSpawnedOnDeath</c>,
    /// <c>addUnitsIntoTransport</c>.</summary>
    Spawn,
}

/// <summary>How a <see cref="LinkKind.ConvertTo"/> is started (read from the action section that holds it).</summary>
public enum ConvertTrigger
{
    /// <summary>Not a convert link.</summary>
    None,
    /// <summary>A player-visible <c>action_*</c> without an event trigger (an upgrade button).</summary>
    PlayerAction,
    /// <summary><c>autoTriggerOnEvent: created / completed / completeAndActive</c>: the unit becomes the target as soon as it exists or is finished
    /// (the nano-build stand-ins, e.g. <c>ACV_nano</c> → <c>ACV</c>).</summary>
    OnCreate,
    /// <summary><c>autoTrigger</c> whose condition tests <c>overWater</c> / <c>isOverWater</c> without negation: goes to the in-water version.</summary>
    ToWater,
    /// <summary><c>autoTrigger</c> whose condition tests <c>not ... overWater</c>: goes back to the land version.</summary>
    ToLand,
    /// <summary>Any other automatic or hidden trigger (tier syncing of attached parts, state switches...).</summary>
    Other,
}

/// <summary>One data link: <paramref name="From"/> names <paramref name="TargetName"/> through <paramref name="Key"/> in <paramref name="Section"/>.
/// <paramref name="Target"/> is the loadable unit with that exact name (null = no such unit: the link points outside the mod's loadable units).</summary>
public sealed record UnitLink(UnitType From, LinkKind Kind, string TargetName, UnitType? Target, string Section, string Key, ConvertTrigger Trigger, Origin Origin);

/// <summary>A link value that could not be followed (an expression with runtime parts, an unknown @define...). Counted and reported, never guessed.</summary>
public sealed record UnresolvedLink(UnitType From, LinkKind Kind, string Section, string Key, string RawValue, Origin Origin);

/// <summary>
/// Reads every unit-to-unit link of a mod from the merged units (E2b, unit families). Rules (survey of the shipped src + javap of game-lib.jar,
/// CHANGELOG C-030):
/// <list type="bullet">
/// <item>Only ACTIVE sections count: <c>action_*</c> and <c>hiddenAction_*</c> for convertTo / spawns / attachments_addNewUnits, <c>attachment_*</c> for
/// onCreateSpawnUnitOf, <c>canBuild_*</c> for build lists, <c>projectile_*</c> for projectile spawns. <c>template_*</c> and <c>comment_*</c> sections are
/// inert text that other sections copy through @copyFromSection (already merged into the copying section).</item>
/// <item>Values go through <see cref="LinkExpr"/>; NONE / empty / the unit's own name are not links.</item>
/// </list>
/// </summary>
public sealed class UnitLinks
{
    UnitLinks(List<UnitLink> links, List<UnresolvedLink> unresolved) { Links = links; Unresolved = unresolved; }

    public IReadOnlyList<UnitLink> Links { get; }
    public IReadOnlyList<UnresolvedLink> Unresolved { get; }

    static readonly string[] SpawnKeys = { "spawnUnits", "spawnUnit", "addUnitsIntoTransport" };

    public static UnitLinks Read(UnitModel model)
    {
        var links = new List<UnitLink>();
        var unresolved = new List<UnresolvedLink>();
        foreach (var u in model.Units)
        {
            var m = model.Merge(u);
            void Add(LinkKind kind, MergedSection sec, MergedValue v, bool isList, ConvertTrigger trig = ConvertTrigger.None)
            {
                var names = isList ? LinkExpr.NameParts(v.Value) : new[] { LinkExpr.NamePart(v.Value) };
                foreach (var raw in names)
                {
                    var name = LinkExpr.Resolve(raw, m, sec.Name);
                    if (name == null) { unresolved.Add(new UnresolvedLink(u, kind, sec.Name, v.Key, raw, v.Origin)); continue; }
                    // a define may carry the spawn arguments too (flare_x${typicalAbilitySpawnStats} = "flare_x(skipIfOverlapping=false, ...)")
                    name = LinkExpr.NamePart(name);
                    if (name.Length == 0 || name.Equals("NONE", StringComparison.OrdinalIgnoreCase) || name == u.Name) continue;
                    links.Add(new UnitLink(u, kind, name, model.Find(name), sec.Name, v.Key, trig, v.Origin));
                }
            }

            foreach (var sec in m.Sections)
            {
                string sn = sec.Name;
                bool isAction = sn.StartsWith("action_", StringComparison.OrdinalIgnoreCase) || sn.StartsWith("hiddenAction_", StringComparison.OrdinalIgnoreCase);
                if (isAction)
                {
                    var ct = sec.Get("convertTo");
                    if (ct != null) Add(LinkKind.ConvertTo, sec, ct, false, TriggerOf(sec));
                    var an = sec.Get("attachments_addNewUnits");
                    if (an != null) Add(LinkKind.Attachment, sec, an, true);
                    // the unit shows as another type while this action runs (amphibious jet -> subJet_transition, diggers)
                    var wb = sec.Get("whenBuilding_temporarilyConvertTo");
                    if (wb != null) Add(LinkKind.ConvertTo, sec, wb, false, ConvertTrigger.Other);
                    var pu = sec.Get("produceUnits");
                    if (pu != null) Add(LinkKind.Produce, sec, pu, true);
                }
                if (isAction || sn.StartsWith("projectile_", StringComparison.OrdinalIgnoreCase) || sn.Equals("core", StringComparison.OrdinalIgnoreCase))
                    foreach (var key in SpawnKeys)
                    {
                        var v = sec.Get(key);
                        if (v != null) Add(LinkKind.Spawn, sec, v, true);
                    }
                if (sn.StartsWith("attachment_", StringComparison.OrdinalIgnoreCase))
                {
                    var v = sec.Get("onCreateSpawnUnitOf");
                    if (v != null) Add(LinkKind.Attachment, sec, v, false);
                }
                if (sn.StartsWith("canBuild_", StringComparison.OrdinalIgnoreCase))
                {
                    var v = sec.Get("name");
                    if (v != null) Add(LinkKind.CanBuild, sec, v, true);
                }
                if (sn.Equals("core", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var v in sec.Values)
                    {
                        if (v.Key.StartsWith("canBuild_", StringComparison.OrdinalIgnoreCase) && v.Key.EndsWith("_name", StringComparison.OrdinalIgnoreCase))
                            Add(LinkKind.CanBuild, sec, v, true);
                        // legacy actions in [core]: action_N_convertTo (2 uses), started by the player
                        else if (v.Key.StartsWith("action_", StringComparison.OrdinalIgnoreCase) && v.Key.EndsWith("_convertTo", StringComparison.OrdinalIgnoreCase))
                            Add(LinkKind.ConvertTo, sec, v, false, ConvertTrigger.PlayerAction);
                    }
                    var sod = sec.Get("unitsSpawnedOnDeath");
                    if (sod != null) Add(LinkKind.Spawn, sec, sod, true);
                }
                if (sn.Equals("ai", StringComparison.OrdinalIgnoreCase))
                {
                    var v = sec.Get("upgradedFrom");
                    if (v != null) Add(LinkKind.UpgradedFrom, sec, v, false);
                }
            }

            // copyFrom of another loadable unit's FILE, listed in the unit's own file (not inherited: a parent's own copyFrom is that parent's link)
            var own = model.Doc(u.RelPath);
            var cf = own?.Get("core", "copyFrom");
            if (cf != null)
                foreach (var entry in cf.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var rel = model.ResolveCopyFromFile(u.RelPath, entry);
                    if (rel == null) continue;
                    foreach (var t in model.UnitsInFile(rel))
                        if (!ReferenceEquals(t, u))
                            links.Add(new UnitLink(u, LinkKind.CopyFromUnit, t.Name, t, "core", "copyFrom", ConvertTrigger.None,
                                new Origin(u.RelPath, "core", own!.Sections.Where(s => s.Name.Equals("core", StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Pairs)
                                    .LastOrDefault(p => p.Key.Equals("copyFrom", StringComparison.OrdinalIgnoreCase))?.Line ?? 0, OriginKind.Own, null)));
                }
        }
        return new UnitLinks(links, unresolved);
    }

    /// <summary>Classifies the trigger of a convertTo action section.</summary>
    public static ConvertTrigger TriggerOf(MergedSection sec)
    {
        string at = (sec.Get("autoTrigger")?.Value ?? "").Trim();
        bool hasAuto = at.Length > 0 && !at.Equals("false", StringComparison.OrdinalIgnoreCase);
        if (hasAuto)
        {
            int w = IndexOfWater(at);
            if (w >= 0)
            {
                // "not self.overWater()" / "not self.isOverWater" right before the test = back to land
                string before = at[..w].TrimEnd();
                if (before.EndsWith("self.", StringComparison.Ordinal)) before = before[..^5].TrimEnd();
                return before.EndsWith("not", StringComparison.OrdinalIgnoreCase) ? ConvertTrigger.ToLand : ConvertTrigger.ToWater;
            }
        }
        string ev = (sec.Get("autoTriggerOnEvent")?.Value ?? "").Trim();
        if (ev.Length > 0 && !ev.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in ev.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (e.Equals("created", StringComparison.OrdinalIgnoreCase) || e.Equals("completed", StringComparison.OrdinalIgnoreCase)
                    || e.Equals("completeAndActive", StringComparison.OrdinalIgnoreCase))
                    return ConvertTrigger.OnCreate;
            return ConvertTrigger.Other;
        }
        // an action_* button is a player upgrade even when it also has an autoTrigger (the mod's AI-only "if self.isControlledByAI ..." triggers)
        if (sec.Name.StartsWith("action_", StringComparison.OrdinalIgnoreCase)) return ConvertTrigger.PlayerAction;
        return ConvertTrigger.Other;
    }

    static int IndexOfWater(string cond)
    {
        foreach (var t in new[] { "isOverWater", "overWater" })
        {
            int i = cond.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return i;
        }
        return -1;
    }
}
