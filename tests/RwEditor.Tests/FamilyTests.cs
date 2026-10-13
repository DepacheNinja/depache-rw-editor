using RwEditor.Core.Browse;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using Xunit;

namespace RwEditor.Tests;

/// <summary>
/// E2b: unit links (convertTo / upgradedFrom / copyFrom / canBuild / produceUnits / attachments / spawns, the ${...} resolver), the family builder
/// (complete units, variant families, root choice, labels and order, part groups, the shared / helper buckets, determinism, every entry exactly once)
/// and the browser's family list (filters, search opening a family, locked machinery, counts). A small synthetic mod in RAM, shaped like the real
/// patterns (ACV levels with water forms and a nano build, heroes copying the level below, a shared-template upgradedFrom, two buildable units sharing
/// a file).
/// </summary>
public sealed class FamilyTests
{
    static readonly Dictionary<string, string> Files = new()
    {
        ["all-units.template"] = "[core]\nselfRegenRate: 0\n",
        // a loadable helper base that other helpers copy (like myAssets/dummyUnit.ini): a copyFrom of it is NOT a family link
        ["templates/dummy.ini"] = "[core]\nname: dummyBase\nshowInEditor: false\n",
        ["templates/water.ini"] = "[core]\ndont_load: true\nshowInEditor: false\n[movement]\nmoveSpeed: 0.5\n",
        ["templates/heroTemplate.ini"] = "[core]\ndont_load: true\nshowInEditor: true\n[ai]\nupgradedFrom: otherHero\n",
        ["units/factory.ini"] =
            "[core]\nname: factory\nisBuilding: true\ndisplayText: Factory\n" +
            "[canBuild_1]\nname: tank_nano, scout, mediumTank, lightTank, hero_t1_nano, hero_t2_nano\n" +
            "[canBuild_2]\nname: secret\nisVisible: false\n" +
            "[action_makeDrone]\n@define unit: drone\nproduceUnits: ${unit}(copyWaypointsFrom=self)\n",
        ["units/tank.ini"] =
            "[core]\nname: tank\ndisplayText: Tank\n" +
            "[template_up]\nconvertTo: tank${level}\n" +
            "[action_up2]\n@copyFromSection: template_up\n@define level: 2\nisVisible: if memory.level == 1\n" +
            "[action_up3]\n@copyFromSection: template_up\n@define level: 3\n" +
            "[hiddenAction_checkTile]\nautoTrigger: if self.overWater() and not self.hasParent()\nconvertTo: ${core.name}_water\n" +
            "[attachment_gun]\nonCreateSpawnUnitOf: tankGun\n" +
            "[action_fire]\nspawnUnits: flash(skipIfOverlapping=false)\n" +
            "[ai]\nupgradedFrom: tank\n",
        ["units/tank2.ini"] = "[core]\nname: tank2\ncopyFrom: tank.ini\nshowInEditor: false\n",
        ["units/tank3.ini"] = "[core]\nname: tank3\ncopyFrom: tank2.ini\n",
        ["units/tank_water.ini"] = "[core]\nname: tank_water\ncopyFrom: tank.ini, ROOT:/templates/water.ini\n[hiddenAction_checkTile]\nautoTrigger: if self.hasParent() or not self.overWater()\nconvertTo: tank\n",
        ["units/tank2_water.ini"] = "[core]\nname: tank2_water\ncopyFrom: tank2.ini, ROOT:/templates/water.ini\n[hiddenAction_checkTile]\nautoTrigger: if not self.overWater()\nconvertTo: tank2\n",
        ["units/tank_nano.ini"] = "[core]\nname: tank_nano\ncopyFrom: tank.ini\nisBuilding: true\n[hiddenAction_convertOnCreate]\nautoTriggerOnEvent: completeAndActive\nconvertTo: tank\n",
        ["units/tankGun.ini"] = "[core]\nname: tankGun\ncopyFrom: ROOT:/templates/dummy.ini\n[hiddenAction_tier]\nautoTrigger: if parent.readUnitMemory('level', type='number') == 2\nconvertTo: tankGun2\n",
        ["units/tankGun2.ini"] = "[core]\nname: tankGun2\ncopyFrom: tankGun.ini\n",
        ["units/scout.ini"] = "[core]\nname: scout\ndisplayText: Scout\n[action_flare]\nspawnUnits: flash*2, ${core.name}Trail\n",
        ["units/scoutTrail.ini"] = "[core]\nname: scoutTrail\nshowInEditor: false\n",
        ["units/flash.ini"] = "[core]\nname: flash\nshowInEditor: false\n[hiddenAction_x]\nspawnUnits: flashSpark\n",
        ["units/flashSpark.ini"] = "[core]\nname: flashSpark\nshowInEditor: false\n",
        ["units/secret.ini"] = "[core]\nname: secret\n",
        ["units/mediumTank.ini"] = "[core]\nname: mediumTank\ndisplayText: Medium tank\n",
        ["units/lightTank.ini"] = "[core]\nname: lightTank\ncopyFrom: mediumTank.ini\ndisplayText: Light tank\n",
        ["units/drone.ini"] = "[core]\nname: drone\n",
        ["units/creep.ini"] = "[core]\nname: creep\ndisplayText: Creep\nshowInEditor: false\n[graphics]\nimage: creep.png\n",
        ["units/ghost.ini"] = "[core]\nname: ghost\ndisplayText: Ghost\nshowInEditor: false\n[graphics]\nimage: SHARED:blank.png\n",
        ["units/broken.ini"] = "[core]\nname: broken\nshowInEditor: false\n[hiddenAction_x]\nautoTrigger: true\nconvertTo: ${self.something}\n",
        ["heroes/hero_t1.ini"] = "[core]\nname: hero_t1\ncopyFrom: ROOT:/templates/heroTemplate.ini\ndisplayText: Hero 1\n",
        ["heroes/hero_t2.ini"] = "[core]\nname: hero_t2\ncopyFrom: hero_t1.ini\ndisplayText: Hero 2\n",
        ["heroes/hero_t1_nano.ini"] = "[core]\nname: hero_t1_nano\ncopyFrom: hero_t1.ini\n[hiddenAction_convertOnCreate]\nautoTriggerOnEvent: created\nconvertTo: hero_t1\n",
        ["heroes/hero_t2_nano.ini"] = "[core]\nname: hero_t2_nano\ncopyFrom: hero_t2.ini\n[hiddenAction_convertOnCreate]\nautoTriggerOnEvent: created\nconvertTo: hero_t2\n",
        ["heroes/otherHero.ini"] = "[core]\nname: otherHero\ncopyFrom: ROOT:/templates/heroTemplate.ini\ndisplayText: Other hero\n",
        ["myAssets/lockedThing.ini"] = "[core]\nname: lockedThing\ndisplayText: Locked thing\n",
        ["myAssets/lockedPart.ini"] = "[core]\nname: lockedPart\nshowInEditor: false\n",
        ["units/usesLocked.ini"] = "[core]\nname: usesLocked\ndisplayText: Uses locked\n[attachment_a]\nonCreateSpawnUnitOf: lockedPart\n",
    };

    static UnitModel Model(IDictionary<string, string>? extra = null)
    {
        var texts = new Dictionary<string, string>(Files, StringComparer.OrdinalIgnoreCase);
        if (extra != null) foreach (var (k, v) in extra) texts[k] = v;
        return UnitModel.Build(ModSnapshot.FromTexts(Path.Combine(Path.GetTempPath(), "rwe-family-fixture"), texts), ModAreas.Embedded, EditorSupport.DefaultLockedGlobs);
    }

    static readonly UnitModel M = Model();
    static readonly UnitFamilies F = UnitFamilies.Build(M);

    static UnitType U(string name) => M.Find(name) ?? throw new InvalidOperationException("no unit " + name);
    static UnitFamily Fam(string name) => F.FamilyOf(U(name));
    static FamilyEntry E(string name) => F.EntryOf(U(name)) ?? throw new InvalidOperationException(name + " is a root");

    // ------------------------------------------------------------------------------------------------------------- links and the resolver
    [Fact]
    public void Links_follow_defines_core_values_and_spawn_syntax()
    {
        var l = UnitLinks.Read(M);
        Assert.Contains(l.Links, x => x.From.Name == "tank" && x.Kind == LinkKind.ConvertTo && x.TargetName == "tank2" && x.Trigger == ConvertTrigger.PlayerAction);
        Assert.Contains(l.Links, x => x.From.Name == "tank" && x.Kind == LinkKind.ConvertTo && x.TargetName == "tank3");
        Assert.Contains(l.Links, x => x.From.Name == "tank" && x.TargetName == "tank_water" && x.Trigger == ConvertTrigger.ToWater);
        Assert.Contains(l.Links, x => x.From.Name == "tank_water" && x.TargetName == "tank" && x.Trigger == ConvertTrigger.ToLand);
        Assert.Contains(l.Links, x => x.From.Name == "tank2_water" && x.TargetName == "tank2" && x.Trigger == ConvertTrigger.ToLand);
        Assert.Contains(l.Links, x => x.From.Name == "tank_nano" && x.TargetName == "tank" && x.Trigger == ConvertTrigger.OnCreate);
        Assert.Contains(l.Links, x => x.From.Name == "tankGun" && x.TargetName == "tankGun2" && x.Trigger == ConvertTrigger.Other);
        Assert.Contains(l.Links, x => x.From.Name == "factory" && x.Kind == LinkKind.Produce && x.TargetName == "drone");
        Assert.Contains(l.Links, x => x.From.Name == "scout" && x.Kind == LinkKind.Spawn && x.TargetName == "flash");          // "flash*2"
        Assert.Contains(l.Links, x => x.From.Name == "scout" && x.Kind == LinkKind.Spawn && x.TargetName == "scoutTrail");     // "${core.name}Trail"
        Assert.Contains(l.Links, x => x.From.Name == "tank" && x.Kind == LinkKind.Attachment && x.TargetName == "tankGun");
        Assert.Contains(l.Links, x => x.From.Name == "lightTank" && x.Kind == LinkKind.CopyFromUnit && x.TargetName == "mediumTank");
        Assert.DoesNotContain(l.Links, x => x.Section.StartsWith("template_", StringComparison.OrdinalIgnoreCase));            // inert template sections
        Assert.DoesNotContain(l.Links, x => x.From == x.Target);                                                              // "upgradedFrom: tank" in tank
        var un = Assert.Single(l.Unresolved);
        Assert.Equal("broken", un.From.Name);                                                                                 // ${self.something}: never guessed
    }

    [Theory]
    [InlineData("a${x}b", "a7b")]
    [InlineData("u${x+1}", "u8")]
    [InlineData("u${x - 1}", "u6")]
    [InlineData("u${int(x/2)}", "u3")]
    [InlineData("${core.name}_w", "probe_w")]
    [InlineData("${section.k}", "kv")]
    [InlineData("${g}", "gv")]
    [InlineData("${self.hp}", null)]
    [InlineData("${unknown}", null)]
    [InlineData("${foo(1)}", null)]
    [InlineData("plain", "plain")]
    [InlineData("half$", null)]
    public void Expression_resolver_is_small_and_honest(string value, string? expected)
    {
        var m = Model(new Dictionary<string, string> { ["units/probe.ini"] = "[core]\nname: probe\n@global g: gv\n[action_a]\n@define x: 7\nk: kv\n" });
        Assert.Equal(expected, LinkExpr.Resolve(value, m.Merge(m.Find("probe")!), "action_a"));
    }

    [Theory]
    [InlineData("a, b(x=1, y=2), c*3(z=${q, r}), ${n}*${k}", new[] { "a", "b", "c", "${n}" })]
    [InlineData("  ", new string[0])]
    public void Name_parts_split_at_top_level_commas(string list, string[] expected) => Assert.Equal(expected, LinkExpr.NameParts(list));

    // ------------------------------------------------------------------------------------------------------------- (a) complete
    [Fact]
    public void Complete_rules()
    {
        Assert.True(F.Complete[U("tank_nano")].HasFlag(CompleteReason.Buildable));
        Assert.True(F.Complete[U("drone")].HasFlag(CompleteReason.Buildable));              // produceUnits
        Assert.Equal(CompleteReason.None, F.Complete[U("secret")]);                           // only in an isVisible: false build list
        Assert.True(F.Complete[U("tank")].HasFlag(CompleteReason.MapEditorNamed));           // showInEditor defaults to true + displayText
        Assert.False(F.Complete[U("tank2")].HasFlag(CompleteReason.MapEditorNamed));         // showInEditor: false
        Assert.Equal(CompleteReason.NamedWithSprite, F.Complete[U("creep")]);
        Assert.Equal(CompleteReason.None, F.Complete[U("ghost")]);                           // blank.png is no sprite
        Assert.Equal(CompleteReason.None, F.Complete[U("flash")]);
    }

    // ------------------------------------------------------------------------------------------------------------- (b) families
    [Fact]
    public void Tank_family_has_levels_water_forms_nano_build_and_parts_in_order()
    {
        var f = Fam("tank");
        Assert.Equal("tank", f.Root!.Name);
        Assert.Equal(new[] { "tank_water", "tank_nano", "tank2", "tank2_water", "tank3", "tankGun", "tankGun2" }, f.Members.Select(e => e.Unit.Name));
        Assert.Equal("Water version of tank", E("tank_water").Label);
        Assert.Equal("Nano build of tank", E("tank_nano").Label);
        Assert.Equal("Upgrade level 2", E("tank2").Label);
        Assert.Equal("Water version of tank2", E("tank2_water").Label);
        Assert.Equal("Upgrade level 3", E("tank3").Label);
        Assert.Equal("tank2", E("tank3").Anchor!.Name);                                       // tank3.ini copies tank2.ini: hangs under tank2, not tank
        Assert.Equal("Part of tank", E("tankGun").Label);
        Assert.Equal("Switches with tankGun", E("tankGun2").Label);
        Assert.Equal(2, E("tank2_water").Depth);
        Assert.Same(f, Fam("tank3"));
    }

    [Fact]
    public void Hero_levels_join_through_copyFrom_but_a_shared_template_upgradedFrom_does_not()
    {
        var f = Fam("hero_t1");
        Assert.Equal("hero_t1", f.Root!.Name);
        Assert.Equal(new[] { "hero_t1_nano", "hero_t2", "hero_t2_nano" }, f.Members.Select(e => e.Unit.Name));
        Assert.Equal("Based on hero_t1", E("hero_t2").Label);
        Assert.Equal("Nano build of hero_t2", E("hero_t2_nano").Label);
        Assert.Null(F.EntryOf(U("otherHero")));                                              // its own root: heroTemplate.ini's upgradedFrom is ignored
        Assert.True(F.Stats.UpgradedFromTemplateIgnored >= 4);
    }

    [Fact]
    public void Two_units_that_are_each_buildable_stay_two_roots()
    {
        Assert.Null(F.EntryOf(U("mediumTank")));
        Assert.Null(F.EntryOf(U("lightTank")));
        Assert.Equal(1, F.Stats.CopyFromBothBuildableIgnored);
    }

    // ------------------------------------------------------------------------------------------------------------- (c) parts and buckets
    [Fact]
    public void Parts_shared_parts_and_helper_parts()
    {
        Assert.Equal("Spawned by scout", E("scoutTrail").Label);
        var shared = F.Items.Single(i => i.Bucket == BucketKind.SharedParts);
        // the tank family (6 units through inherited buttons) and scout both spawn flash; flash's own spark hangs under it inside the bucket
        Assert.Equal(new[] { "flash", "flashSpark" }, shared.Members.Select(e => e.Unit.Name));
        Assert.Equal("Used by scout and 6 more", shared.Members[0].Label);
        Assert.Equal(0, shared.Members[0].Depth);
        Assert.Equal("Spawned by flash", shared.Members[1].Label);
        Assert.Equal(1, shared.Members[1].Depth);
        var helper = F.Items.Single(i => i.Bucket == BucketKind.HelperParts);
        Assert.Equal(new[] { "dummyBase", "broken", "ghost", "secret" }, helper.Members.Select(e => e.Unit.Name));   // path order
        Assert.All(helper.Members, e => Assert.Equal("Not used by any unit", e.Label));
        Assert.Equal("Part of usesLocked", E("lockedPart").Label);
    }

    [Fact]
    public void Every_entry_exactly_once_and_the_counts_add_up()
    {
        var seen = new List<string>();
        foreach (var i in F.Items)
        {
            if (i.Root != null) seen.Add(i.Root.RelPath);
            seen.AddRange(i.Members.Select(e => e.Unit.RelPath));
        }
        Assert.Equal(M.Units.Count, seen.Count);
        Assert.Equal(M.Units.Count, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var s = F.Stats;
        Assert.Equal(M.Units.Count, s.Roots + s.InsideFamilies + s.SharedParts + s.HelperParts);
        Assert.Equal(s.Variants + s.Parts, s.InsideFamilies);
        Assert.All(F.Items.Where(i => !i.IsBucket), i => Assert.NotEqual(CompleteReason.None, F.Complete[i.Root!]));    // roots are complete
        Assert.All(M.Units, u => Assert.Same(F.FamilyOf(u), F.Items.Single(i => i.Root == u || i.Members.Any(e => e.Unit == u))));
    }

    [Fact]
    public void Deterministic_for_the_same_input_and_independent_of_file_order()
    {
        string Dump(UnitFamilies f) => string.Join("\n", f.Items.Select(i => (i.Root?.Name ?? i.BucketTitle) + ": " + string.Join(",", i.Members.Select(e => e.Unit.Name + "=" + e.Label))));
        Assert.Equal(Dump(F), Dump(UnitFamilies.Build(M)));
        var reversed = Files.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        var m2 = UnitModel.Build(ModSnapshot.FromTexts(Path.Combine(Path.GetTempPath(), "rwe-family-fixture"), reversed), ModAreas.Embedded, EditorSupport.DefaultLockedGlobs);
        Assert.Equal(Dump(F), Dump(UnitFamilies.Build(m2)));
    }

    [Fact]
    public void Parts_that_use_each_other_never_lose_an_entry()
    {
        // pY (first in path order) and pX spawn each other; only hub attaches pX: pX hangs under hub, pY under pX (no loop, nothing lost)
        var m = Model(new Dictionary<string, string>
        {
            ["a/pY.ini"] = "[core]\nname: pY\nshowInEditor: false\n[action_s]\nspawnUnits: pX\n",
            ["z/hub.ini"] = "[core]\nname: hub\ndisplayText: Hub\n[attachment_1]\nonCreateSpawnUnitOf: pX\n",
            ["z/pX.ini"] = "[core]\nname: pX\nshowInEditor: false\n[action_s]\nspawnUnits: pY\n",
            ["b/qA.ini"] = "[core]\nname: qA\nshowInEditor: false\n[action_s]\nspawnUnits: qB\n",
            ["b/qB.ini"] = "[core]\nname: qB\nshowInEditor: false\n[action_s]\nspawnUnits: qA\n",
        });
        var f = UnitFamilies.Build(m);
        var hub = f.FamilyOf(m.Find("hub")!);
        Assert.Equal(new[] { "pX", "pY" }, hub.Members.Select(e => e.Unit.Name));
        Assert.Equal("Part of hub", f.EntryOf(m.Find("pX")!)!.Label);
        Assert.Equal("Spawned by pX", f.EntryOf(m.Find("pY")!)!.Label);
        // two helpers that only use each other: a helper-part cycle, cut at the first in path order
        var helper = f.Items.Single(i => i.Bucket == BucketKind.HelperParts);
        Assert.Equal(0, helper.Members.Single(e => e.Unit.Name == "qA").Depth);
        Assert.Equal("Spawned by qA", helper.Members.Single(e => e.Unit.Name == "qB").Label);
        Assert.Equal(m.Units.Count, f.Stats.Roots + f.Stats.InsideFamilies + f.Stats.SharedParts + f.Stats.HelperParts);
    }

    [Fact]
    public void A_complete_unit_spawned_by_another_stays_a_root()
    {
        var m = Model(new Dictionary<string, string> { ["units/nest.ini"] = "[core]\nname: nest\ndisplayText: Nest\n[action_x]\nspawnUnits: creep*3\n" });
        var f = UnitFamilies.Build(m);
        Assert.Null(f.EntryOf(m.Find("creep")!));
    }

    // ------------------------------------------------------------------------------------------------------------- the browser's family list
    static readonly IReadOnlyList<UnitRow> Rows = UnitCatalog.Build(M);
    static readonly HashSet<string> None = new();

    [Fact]
    public void Closed_list_shows_roots_then_buckets_and_counts()
    {
        var l = FamilyView.Build(F, Rows, new UnitQuery(), None);
        Assert.All(l.Rows, r => Assert.NotEqual(DisplayKind.Child, r.Kind));
        Assert.Equal(F.Stats.Roots + 2, l.Rows.Count);
        Assert.Equal(DisplayKind.Bucket, l.Rows[^1].Kind);
        Assert.Equal(BucketKind.HelperParts, l.Rows[^1].Family.Bucket);
        Assert.Equal(BucketKind.SharedParts, l.Rows[^2].Family.Bucket);
        Assert.Equal($"{F.Stats.Roots} units ({F.Stats.InsideFamilies} entries inside them) · {F.Stats.SharedParts + F.Stats.HelperParts} shared / helper parts", l.StatusText);
        var tank = l.Rows.Single(r => r.Row?.Name == "tank");
        Assert.True(tank.HasChildren);
        Assert.Equal(7, tank.ChildCount);
    }

    [Fact]
    public void Expanding_a_family_lists_its_members_in_order()
    {
        var key = FamilyView.KeyOf(Fam("tank"));
        var l = FamilyView.Build(F, Rows, new UnitQuery(), new HashSet<string> { key });
        int i = l.Rows.ToList().FindIndex(r => r.Kind == DisplayKind.Root && r.Key == key);
        Assert.Equal(new[] { "tank_water", "tank_nano", "tank2", "tank2_water", "tank3", "tankGun", "tankGun2" }, l.Rows.Skip(i + 1).Take(7).Select(r => r.Row!.Name));
        Assert.All(l.Rows.Skip(i + 1).Take(7), r => Assert.Equal(DisplayKind.Child, r.Kind));
        Assert.True(l.Rows[i].Expanded);
        // the user closes a family the search opened: it stays closed
        var s = FamilyView.Build(F, Rows, new UnitQuery("tank3"), None, new HashSet<string> { key });
        Assert.False(s.Rows.Single(r => r.Key == key && r.Kind == DisplayKind.Root).Expanded);
    }

    [Fact]
    public void Search_finds_a_member_under_its_root_opened()
    {
        var l = FamilyView.Build(F, Rows, new UnitQuery("tank2_water"), None);
        Assert.Equal(new[] { "tank", "tank2_water" }, l.Rows.Select(r => r.Row!.Name));
        Assert.True(l.Rows[0].Context);                                  // the root does not match the text itself
        Assert.Equal(DisplayKind.Child, l.Rows[1].Kind);
        Assert.Contains(FamilyView.KeyOf(Fam("tank")), l.AutoExpanded);
        // a search that matches the ROOT keeps it closed but lists the whole family inside
        var r2 = FamilyView.Build(F, Rows, new UnitQuery("Tank"), None);
        var tank = r2.Rows.Single(r => r.Row?.Name == "tank");
        Assert.False(tank.Context);
        Assert.Equal(7, tank.ChildCount);
        // the buckets are searched too
        var r3 = FamilyView.Build(F, Rows, new UnitQuery("spark"), None);
        Assert.Equal(new[] { DisplayKind.Bucket, DisplayKind.Child }, r3.Rows.Select(r => r.Kind));
        Assert.Empty(FamilyView.Build(F, Rows, new UnitQuery("zzzz"), None).Rows);
    }

    [Fact]
    public void Filters_apply_per_entry_and_locked_machinery_is_hidden_on_request()
    {
        var b = FamilyView.Build(F, Rows, new UnitQuery(Type: TypeFilter.Buildings), new HashSet<string> { FamilyView.KeyOf(Fam("tank")) });
        var tank = b.Rows.Single(r => r.Row?.Name == "tank");
        Assert.True(tank.Context);                                       // tank is not a building; its nano build is
        Assert.Equal(new[] { "tank_nano" }, b.Rows.SkipWhile(r => r != tank).Skip(1).TakeWhile(r => r.Kind == DisplayKind.Child).Select(r => r.Row!.Name));
        var noLocked = FamilyView.Build(F, Rows, new UnitQuery(IncludeLocked: false), F.Items.Select(FamilyView.KeyOf).ToHashSet());
        Assert.DoesNotContain(noLocked.Rows, r => r.Row is { Locked: true } && !r.Context);
        var all = FamilyView.Build(F, Rows, new UnitQuery(), F.Items.Select(FamilyView.KeyOf).ToHashSet());
        Assert.Equal(M.Units.Count, all.Rows.Count(r => r.Row != null));                   // everything open: each entry once
        Assert.Equal(M.Units.Count, all.Rows.Where(r => r.Row != null).Select(r => r.Row!.Unit.RelPath).Distinct().Count());
        Assert.Contains("of", FamilyView.Build(F, Rows, new UnitQuery(Area: "fe"), None).StatusText);
    }

    // ------------------------------------------------------------------------------------------------------------- the real mod (read-only)
    static UnitFamilies? _real;
    static UnitModel? _realModel;
    static (UnitModel M, UnitFamilies F) Real()
    {
        _realModel ??= UnitModel.Load(Repo.Src);
        _real ??= UnitFamilies.Build(_realModel);
        return (_realModel, _real);
    }

    [RepoFact]
    public void Real_src_acv_family_holds_its_levels_water_forms_and_nano_build()
    {
        var (m, f) = Real();
        var acv = f.FamilyOf(m.Find("ACV")!);
        Assert.Equal("ACV", acv.Root!.Name);
        var names = acv.Members.Select(e => e.Unit.Name).ToList();
        foreach (var n in new[] { "ACV2", "ACV3", "ACV_water", "ACV2_water", "ACV3_water", "ACV_nano" }) Assert.Contains(n, names);
        Assert.Equal("Upgrade level 2", f.EntryOf(m.Find("ACV2")!)!.Label);
        Assert.Equal("Upgrade level 3", f.EntryOf(m.Find("ACV3")!)!.Label);
        Assert.Equal("Water version of ACV", f.EntryOf(m.Find("ACV_water")!)!.Label);
        Assert.Equal("Water version of ACV2", f.EntryOf(m.Find("ACV2_water")!)!.Label);
        Assert.Equal("Water version of ACV3", f.EntryOf(m.Find("ACV3_water")!)!.Label);
        Assert.Equal("Nano build of ACV", f.EntryOf(m.Find("ACV_nano")!)!.Label);
        Assert.Equal("Part of ACV", f.EntryOf(m.Find("coreACV1")!)!.Label);
    }

    [RepoFact]
    public void Real_src_air_hero_levels_and_nano_builds_share_one_root()
    {
        var (m, f) = Real();
        var hero = f.FamilyOf(m.Find("airHero_t1")!);
        Assert.Equal("airHero_t1", hero.Root!.Name);
        for (int t = 1; t <= 4; t++)
        {
            Assert.Same(hero, f.FamilyOf(m.Find($"airHero_t{t}")!));
            Assert.Same(hero, f.FamilyOf(m.Find($"airHero_t{t}_nano")!));
            Assert.Equal($"Nano build of airHero_t{t}", f.EntryOf(m.Find($"airHero_t{t}_nano")!)!.Label);
        }
        Assert.NotSame(hero, f.FamilyOf(m.Find("hoverHero_t1")!));                 // the shared heroTemplate.ini upgradedFrom does not merge the heroes
        Assert.Same(hero, f.FamilyOf(m.Find("airHero_secondary_tripple")!));      // the grey "?" part from the owner's screenshot
    }

    [RepoFact]
    public void Real_src_summary_every_entry_once()
    {
        var (m, f) = Real();
        var s = f.Stats;
        Assert.Equal(1699, s.Entries);
        Assert.Equal(1699, s.Roots + s.InsideFamilies + s.SharedParts + s.HelperParts);
        var seen = f.Items.SelectMany(i => (i.Root != null ? new[] { i.Root } : Array.Empty<UnitType>()).Concat(i.Members.Select(e => e.Unit))).ToList();
        Assert.Equal(1699, seen.Distinct().Count());
        Assert.Equal(0, s.UnresolvedLinkValues);
        Assert.InRange(s.Roots, 300, 450);      // the owner's 1,699 'units' become a few hundred top-level units (exact number in the printed summary)
        Console.WriteLine($"real src: {s.Roots} top-level units, {s.InsideFamilies} entries inside them (variants {s.Variants}, parts {s.Parts}), " +
            $"{s.SharedParts} shared parts, {s.HelperParts} helper parts; complete {s.Complete} (buildable {s.Buildable}, starting {s.StartingUnits}, " +
            $"map-editor named {s.MapEditorNamed}, named with sprite {s.NamedWithSprite})");
        Assert.Equal("Major Experimentals", m.Areas.Areas.Single(a => a.Id == "main").Label);
    }
}
