using RwEditor.Core.Apply;
using RwEditor.Core.Browse;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>
/// Mod 1.1.0 (CHANGELOG C-043 .. C-043h): EK Definitive Edition and EK - Sylvian Mettle built in. The editor's own areas for them, the second currency
/// 'experimental' in prices / income / actions (shown and editable part by part, every other part kept byte for byte), the EK factory entries whose price
/// is repeated as fixed numbers in their queue condition and refund (read-only with the reason), ROOT:/ copyFrom includes and [resource_*] sections.
/// Fixture shapes are copied from the real files named in each test.
/// </summary>
public class EkMergeTests
{
    // ------------------------------------------------------------------------------------------------------------ fixtures (shapes of the real files)
    static readonly Dictionary<string, string> Ek = new()
    {
        // donors/ek/units/airship_brutus.ini [core]: price:170000,experimental=9000 (no spaces, bare credits)
        ["donors/ek/units/airship_x.ini"] = "[core]\nname: EkAirX\nmaxHp: 80200\nprice:170000,experimental=9000\nbuildSpeed: 90s\n[movement]\nmoveSpeed: 0.65\n",
        // donors/ek/units/space_elevator.ini: three-part income line; reclaimPrice is not in the dictionary
        ["donors/ek/units/elevator_x.ini"] = "[core]\nname: EkElevX\nisBuilding: true\nmaxHp: 150000\nprice:300000,experimental=7000,EKSEF=1\n" +
                                             "generation_resources:credits=400, rust=80, experimental=10\nreclaimPrice:300000,experimental=7000\n[movement]\nmoveSpeed: 0\n",
        // experimental_space_battlecruiser/torpedo_battery.ini: a unit's own [resource_*] section
        ["donors/ek/units/door_x.ini"] = "[core]\nname: EkDoorX\nmaxHp: 500\n[resource_doorOpenProcess]\nhidden:true\ndisplayName:doorOpenProcess\n\n[hiddenAction_openDoor]\naddResources: doorOpenProcess=1\n",
        ["donors/ek/legacy/lgc_x.ini"] = "[core]\nname: EkLegacyX\nmaxHp: 900\nprice: 4000\n",
        // a factory host that includes a generated EK menu file through ROOT:/ (the EK-HOOK form of C-043f) and owns the requeue template
        // (myAssets/factoryBaseAir.ini [template_requeueCostResourceAction])
        ["units/factory_x.ini"] = "[core]\nname: facX\nisBuilding: true\nmaxHp: 4000\ncopyFrom: ROOT:/donors/ek/menus/ekFacX.ini\n[movement]\nmoveSpeed: 0\n" +
                                  "[template_requeueCostResourceAction]\nbuildSpeed:0s\nhighPriorityQueue: true\nisVisible: false\nalsoQueueAction:${unit}\n",
        // donors/ek/menus/ekT1landExp.ini, verbatim shape: one plain entry (refund computed) and one two-currency entry (refund and condition literal)
        ["donors/ek/menus/ekFacX.ini"] =
            "[core]\ndont_load:true\n\n" +
            "[action_EkChassis]\n@define unit: EkChassis\nprice: 10000\nbuildSpeed: 50s\n" +
            "[action_EkChassis2]\n@copyFromSection: template_requeueCostResourceAction\n@define unit: EkChassis\naddResources: credits=${-1*action_EkChassis.price}\n\n" +
            "[action_EkMarshall]\n@define unit: EkMarshall\nprice: credits=16500,experimental=1000\nbuildSpeed: 83s\n" +
            "alsoTriggerOrQueueActionConditional:if memory.recursiveBuild == true and self.hasResources(credits=16500,experimental=1000)\n" +
            "[action_EkMarshall2]\n@copyFromSection: template_requeueCostResourceAction\n@define unit: EkMarshall\naddResources: credits=-16500,experimental=-1000\n",
    };

    static StatFixture Fx() => new(Ek);
    static StatEntry D(string id) => StatDictionary.Embedded.ById(id)!;
    static StatRef Read(StatFixture f, string unit, string id, string section) => StatReader.Read(f.Model.Merge(f.Model.Find(unit)!), D(id), section);
    static string Text(StatEditPlan p) => StatFixture.Decode(p.NewBytes.Single().Value);

    // ------------------------------------------------------------------------------------------------------------ areas
    [Theory]
    [InlineData("donors/ek/units/airship_brutus.ini", "ek")]
    [InlineData("donors/ek/units/ekCommon.ini", "ek")]
    [InlineData("donors/ek/menus/ekT1landExp.ini", "ek")]
    [InlineData("DONORS/EK/Units/Valkyrie.ini", "ek")]
    [InlineData("donors/ek/legacy/lgc_orca_dropship.ini", "eksm")]
    [InlineData("donors\\ek\\legacy\\lgc_rocket_truck.ini", "eksm")]
    [InlineData("donors/fe/generators/GenT1.ini", "fe")]
    [InlineData("extractor/extractorT3_overclocked.ini", "main")]
    public void Ek_files_get_their_own_mod_areas(string rel, string area) => Assert.Equal(area, ModAreas.Embedded.AreaOf(rel));

    [Fact]
    public void The_two_ek_areas_are_labelled_by_their_mods()
    {
        Assert.Equal("EK Definitive Edition", ModAreas.Embedded.Areas.Single(a => a.Id == "ek").Label);
        Assert.Equal("EK - Sylvian Mettle", ModAreas.Embedded.Areas.Single(a => a.Id == "eksm").Label);
        var f = Fx();
        Assert.Equal("ek", f.Model.Find("EkAirX")!.Area);
        Assert.Equal("eksm", f.Model.Find("EkLegacyX")!.Area);
        var rows = UnitCatalog.Build(f.Model);
        Assert.Equal("EK - Sylvian Mettle", rows.Single(r => r.Name == "EkLegacyX").AreaLabel);
        Assert.Equal(new[] { "EkAirX", "EkDoorX", "EkElevX" }, UnitCatalog.Query(rows, new UnitQuery(Area: "ek")).Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------------------ two-currency unit price
    [Fact]
    public void A_two_currency_price_shows_credits_and_experimental_and_rust_reads_zero()
    {
        var f = Fx();
        var c = Read(f, "EkAirX", "core.price.credits", "core");
        var x = Read(f, "EkAirX", "core.price.experimental", "core");
        Assert.True(c.Editable); Assert.Equal(170000, c.Number);
        Assert.True(x.Editable); Assert.Equal(9000, x.Number);
        Assert.Equal(0, Read(f, "EkAirX", "core.price.rust", "core").Number);   // no rust part: 0 rust
        // the app's view: credits in Simple, experimental in Expand (only because the line has that part)
        var d = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "EkAirX"));
        Assert.True(d.Simple.Single(r => r.Entry.Id == "core.price.credits").Editable);
        var xr = d.Expand.Single(r => r.Entry.Id == "core.price.experimental");
        Assert.True(xr.Editable);
        Assert.Equal(9000, xr.Ref!.Number);
        var legacy = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "EkLegacyX"));
        Assert.DoesNotContain(legacy.Expand, r => r.Entry.Id == "core.price.experimental");   // a credits-only price lists no "0 experimental" row
    }

    [Fact]
    public void Editing_the_credits_keeps_the_experimental_part_byte_for_byte_and_the_other_way_round()
    {
        var f = Fx();
        var p = f.Plan("EkAirX", "core.price.credits", "175000");
        Assert.Contains("\nprice:175000,experimental=9000\n", Text(p));
        Assert.Empty(p.NewLintFindings);
        f.Commit(p);
        Assert.Equal(9000, Read(f, "EkAirX", "core.price.experimental", "core").Number);
        var q = f.Plan("EkAirX", "core.price.experimental", "9500");
        Assert.Contains("\nprice:175000,experimental=9500\n", Text(q));
        // decimals are allowed for a mod resource (Float), not for credits (Integer.parseInt)
        Assert.Contains("experimental=9500.5", Text(f.Plan("EkAirX", "core.price.experimental", "9500.5")));
        Assert.Contains(Assert.Throws<StatEditRefusedException>(() => f.Plan("EkAirX", "core.price.credits", "1.5")).Findings, x => x.Code == "VALUE_INVALID");
    }

    [Fact]
    public void A_three_part_price_and_income_line_change_one_part_only()
    {
        var f = Fx();
        Assert.Contains("\nprice:300000,experimental=7000,EKSEF=1\n", f.Model.Snapshot.GetText("donors/ek/units/elevator_x.ini"));
        Assert.Contains("\nprice:300000,experimental=7500,EKSEF=1\n", Text(f.Plan("EkElevX", "core.price.experimental", "7500")));
        Assert.Equal(10, Read(f, "EkElevX", "core.generation_resources.experimental", "core").Number);
        Assert.Contains("\ngeneration_resources:credits=400, rust=80, experimental=12\n", Text(f.Plan("EkElevX", "core.generation_resources.experimental", "12")));
        Assert.Contains("\ngeneration_resources:credits=450, rust=80, experimental=10\n", Text(f.Plan("EkElevX", "core.generation_resources.credits", "450")));
        // reclaimPrice is not in the dictionary: never written
        Assert.Throws<StatEditRefusedException>(() => f.Plan("EkElevX", "reclaimPrice", "1"));
    }

    [Fact]
    public void Disk_round_trip_edit_then_undo_gives_the_original_bytes_of_a_two_currency_file()
    {
        using var m = new TestMod(extraFiles: Ek);
        const string rel = "donors/ek/units/airship_x.ini";
        var before = m.Bytes(rel);
        var opt = new ApplyOptions { BackupRoot = m.BackupRoot };
        string settings = Path.Combine(m.BaseFolder, "settings.json");
        var r = StatPipeline.Apply(m.Root, new StatEditRequest("EkAirX", "core.price.credits", "180000"), settings, opt);
        Assert.Equal(new[] { rel }, r.WrittenFiles);
        Assert.Equal(StatFixture.Decode(before).Replace("price:170000,experimental=9000", "price:180000,experimental=9000"), m.Read(rel));
        ApplyPipeline.Undo(m.Root, opt);
        Assert.Equal(before, m.Bytes(rel));
        StatPipeline.Apply(m.Root, new StatEditRequest("EkAirX", "core.price.experimental", "9100"), settings, opt);
        Assert.Equal(StatFixture.Decode(before).Replace("price:170000,experimental=9000", "price:170000,experimental=9100"), m.Read(rel));
        ApplyPipeline.Undo(m.Root, opt);
        Assert.Equal(before, m.Bytes(rel));
    }

    // ------------------------------------------------------------------------------------------------------------ EK factory entries (ROOT:/ include)
    [Fact]
    public void Root_include_actions_resolve_with_their_origin_in_the_ek_menu_file()
    {
        var f = Fx();
        var merged = f.Model.Merge(f.Model.Find("facX")!);
        Assert.Contains("donors/ek/menus/ekFacX.ini", merged.ChainFiles);
        var plain = Read(f, "facX", "action.price.credits", "action_EkChassis");
        Assert.True(plain.Editable);
        Assert.Equal(10000, plain.Number);
        Assert.Equal("donors/ek/menus/ekFacX.ini", plain.Raw!.Origin.File);
        // the refund helper inherits alsoQueueAction from the host's template through @copyFromSection
        Assert.NotNull(merged.Get("action_EkMarshall2", "alsoQueueAction"));
    }

    [Fact]
    public void A_two_currency_factory_entry_is_read_only_because_its_price_is_repeated_in_the_queue_condition_and_the_refund()
    {
        var f = Fx();
        foreach (var id in new[] { "action.price.credits", "action.price.experimental", "action.price.rust" })
        {
            var r = Read(f, "facX", id, "action_EkMarshall");
            Assert.False(r.Editable, id);
            Assert.Contains("hasResources(credits=16500,experimental=1000)", r.ReadOnlyReason);
        }
        foreach (var id in new[] { "action.addResources.credits", "action.addResources.experimental" })
        {
            var r = Read(f, "facX", id, "action_EkMarshall2");
            Assert.False(r.Editable, id);
            Assert.Contains("refund of a re-queued build", r.ReadOnlyReason);
        }
        var e = Assert.Throws<StatEditRefusedException>(() => f.Plan("facX", "action.price.credits", "17000", StatEditMode.Template, "action_EkMarshall"));
        Assert.Contains(e.Findings, x => x.Code == "VALUE_READ_ONLY");
        var e2 = Assert.Throws<StatEditRefusedException>(() => f.Plan("facX", "action.addResources.credits", "-17000", StatEditMode.Template, "action_EkMarshall2"));
        Assert.Contains(e2.Findings, x => x.Code == "VALUE_READ_ONLY");
        // the app shows them, read-only, with the reason in words
        var d = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "facX"));
        var row = d.Expand.Single(r => r.Entry.Id == "action.price.credits" && r.Section == "action_EkMarshall");
        Assert.False(row.Editable);
        Assert.StartsWith("Read-only: ", row.Note);
    }

    [Fact]
    public void A_plain_factory_entry_of_the_same_include_stays_editable_and_its_computed_refund_follows()
    {
        var f = Fx();
        var p = f.Plan("facX", "action.price.credits", "12000", StatEditMode.Template, "action_EkChassis");
        Assert.Equal("donors/ek/menus/ekFacX.ini", p.Record.File);
        Assert.Contains("[action_EkChassis]\n@define unit: EkChassis\nprice: 12000\n", Text(p));
        Assert.Contains("addResources: credits=${-1*action_EkChassis.price}", Text(p));   // untouched, computed from the price
        Assert.Contains("price: credits=16500,experimental=1000", Text(p));                // the coupled entry is untouched
    }

    // ------------------------------------------------------------------------------------------------------------ [resource_*] sections
    [Fact]
    public void A_units_own_resource_section_loads_is_not_offered_and_survives_an_edit()
    {
        var f = Fx();
        var u = f.Model.Find("EkDoorX")!;
        Assert.NotNull(f.Model.Merge(u).Section("resource_doorOpenProcess"));
        var d = StatView.Describe(f.Model, StatDictionary.Embedded, UnitCatalog.Build(f.Model).Single(r => r.Name == "EkDoorX"));
        Assert.DoesNotContain(d.Simple.Concat(d.Expand), r => r.Section.StartsWith("resource_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(d.Other!, o => o.Section == "resource_doorOpenProcess" && o.Key == "hidden");
        // addResources of a resource that is not a dictionary part: not offered (no 'doorOpenProcess' entry), never touched
        Assert.DoesNotContain(d.Expand, r => r.Section == "hiddenAction_openDoor");
        var p = f.Plan("EkDoorX", "core.maxHp", "600");
        Assert.Equal("[core]\nname: EkDoorX\nmaxHp: 600\n[resource_doorOpenProcess]\nhidden:true\ndisplayName:doorOpenProcess\n\n[hiddenAction_openDoor]\naddResources: doorOpenProcess=1\n", Text(p));
    }

    // ------------------------------------------------------------------------------------------------------------ the real mod 1.1.0 tree (read-only)
    static UnitModel? _real;
    static UnitModel Real => _real ??= UnitModel.Load(Repo.Src);

    [RepoFact]
    public void Real_every_ek_ini_is_in_an_ek_area()
    {
        string ek = Path.Combine(Repo.Src, "donors", "ek");
        var files = Directory.EnumerateFiles(ek, "*.ini", SearchOption.AllDirectories).Select(p => RwEditor.Core.Mod.ModSnapshot.ToRel(Repo.Src, p)).ToList();
        Assert.Equal(576 + 10 + 21, files.Count);   // C-043: 576 ported ini (570 units + templates), C-043e: 10 legacy, C-043f: 21 menu includes
        foreach (var r in files) Assert.Equal(r.Contains("/legacy/", StringComparison.OrdinalIgnoreCase) ? "eksm" : "ek", ModAreas.Embedded.AreaOf(r));
    }

    [RepoFact]
    public void Real_brutus_two_currency_price_plans_in_ram_with_the_experimental_part_kept()
    {
        var u = Real.Find("Ek_Brutus")!;
        Assert.Equal("ek", u.Area);
        var m = Real.Merge(u);
        Assert.Equal(170000, StatReader.Read(m, D("core.price.credits"), "core").Number);
        Assert.Equal(9000, StatReader.Read(m, D("core.price.experimental"), "core").Number);
        var p = StatPatcher.Plan(Real, StatDictionary.Embedded, new StatEditRequest("Ek_Brutus", "core.price.credits", "175000"),
            rel => File.ReadAllBytes(Path.Combine(Repo.Src, rel.Replace('/', Path.DirectorySeparatorChar))));   // RAM only, nothing written
        Assert.Equal("donors/ek/units/airship_brutus.ini", p.Record.File);
        Assert.Equal("170000,experimental=9000", p.Record.OldText!.Trim());
        Assert.Equal("175000,experimental=9000", p.Record.NewText);
        Assert.Empty(p.NewLintFindings);
    }

    [RepoFact]
    public void Real_space_elevator_income_reads_ten_experimental()
    {
        var m = Real.Merge(Real.Find("EkSmallSpaceElevator")!);
        Assert.Equal(400, StatReader.Read(m, D("core.generation_resources.credits"), "core").Number);
        Assert.Equal(80, StatReader.Read(m, D("core.generation_resources.rust"), "core").Number);
        Assert.Equal(10, StatReader.Read(m, D("core.generation_resources.experimental"), "core").Number);
    }

    /// <summary>The coupled-price rule makes ONLY the generated EK factory entries read-only: every value it hits comes from donors/ek/menus.</summary>
    [RepoFact]
    public void Real_coupled_prices_are_exactly_the_ek_menu_two_currency_entries()
    {
        var ids = new[] { "action.price.credits", "action.addResources.credits" };
        var hits = new List<string>();
        foreach (var u in Real.Units)
        {
            var m = Real.Merge(u);
            foreach (var id in ids)
                foreach (var sec in StatReader.SectionsFor(m, D(id)))
                {
                    var r = StatReader.Read(m, D(id), sec);
                    if (r.IsSet && StatReader.CoupledReason(m, D(id), sec) != null && !StatValue.IsExpression(r.Raw!.Value)) hits.Add($"{r.Raw.Origin.File}|{sec}|{id}");
                }
        }
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.StartsWith("donors/ek/menus/", h));
        Console.WriteLine($"coupled EK factory values (read-only): {hits.Distinct().Count()} distinct file/section/stat");
    }
}
