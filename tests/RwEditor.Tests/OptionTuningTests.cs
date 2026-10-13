using System.Security.Cryptography;
using System.Text;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Mod;
using RwEditor.Core.Options;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;
using Xunit;

namespace RwEditor.Tests;

/// <summary>E5b: the numbers and the text of the shipped menu options, edited inside LOCKED files only through the catalog, recorded, verified and undone.</summary>
public sealed class OptionTuningTests
{
    const string Manager = "myAssets/ManagerBasic.ini";
    const string Selector = "startingOptions/gameOptionSelector.ini";

    // the real ManagerBasic.ini is CRLF; the fixture keeps that
    static readonly string ManagerText =
        "\r\n[core]\r\nname: ManagerBasic\r\n\r\ngeneration_resources: credits=250, rust=100\r\ngeneration_active: if numberOfUnitsInNeutralTeam(withTag='baseIncome') > 0\r\n\r\n" +
        "[hiddenAction_richStart]\r\nautoTrigger: if memory.startingRes > 0\r\naddResourcesWithLogic: rust=memory.startingRes * 20, credits=memory.startingRes * 100\r\nalsoTriggerAction:richStart2\r\n";

    const string SelectorText = "[core]\nname: gameOptionSelector\n[action_richStartOn]\n@copyFromSection: template_turnOnOption\n@define actionTag: richStart\n@define actionName: Rich start\n" +
        "@define actionNameZh: x\n@define typeId: 1\ndescription:- All players acquire 100 times the amount of starting resources\ndescription_zh:- 获得100倍初始资源\n" +
        "[action_baseIncomeOn]\n@copyFromSection: template_turnOnOption\n@define actionTag: baseIncome\n@define actionName: Base Income\n@define typeId: 1\ndescription:- All players have a small unconditional income\n";

    static readonly IReadOnlyDictionary<string, string> OnSections = new Dictionary<string, string> { ["richStart"] = "action_richStartOn", ["baseIncome"] = "action_baseIncomeOn" };

    static Func<string, byte[]?> Files(string manager = "", string selector = "") => rel =>
        rel == Manager ? Encoding.UTF8.GetBytes(manager.Length > 0 ? manager : ManagerText)
        : rel == Selector ? Encoding.UTF8.GetBytes(selector.Length > 0 ? selector : SelectorText) : null;

    static OptionModel Model(IReadOnlyList<OptionEdit>? recorded = null) => OptionModel.Load(Files(), recorded ?? new List<OptionEdit>());

    static OptionTuning Values(params (string Id, string Value)[] v)
    {
        var t = new OptionTuning();
        foreach (var (id, value) in v) t.SetValue(id, value);
        return t;
    }

    // ---------------------------------------------------------------------------------------------------------------- catalog
    [Fact]
    public void The_embedded_catalog_loads_and_lists_income_and_rich_start()
    {
        var c = OptionCatalog.Embedded;
        Assert.Equal(new[] { "income.credits", "income.rust", "richStart.credits", "richStart.rust" }, c.Settings.Select(s => s.Id));
        Assert.Equal(new[] { Manager, Selector }.OrderBy(x => x), c.TargetFiles.OrderBy(x => x));
        Assert.Equal(new[] { "baseIncome", "incomeMe" }, c.Find("income.credits")!.Options);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":1,\"text\":{\"file\":\"../x.ini\",\"nameKey\":\"a\",\"descriptionKey\":\"b\",\"maxNameLength\":1,\"maxDescriptionLines\":1,\"maxDescriptionLineLength\":1},\"settings\":[]}")]
    [InlineData("{\"schema\":1,\"extra\":1,\"text\":{},\"settings\":[]}")]
    public void A_bad_catalog_is_refused_whole(string json) =>
        Assert.Equal("OPTION_CATALOG_INVALID", Assert.Throws<EditorRefusalException>(() => OptionCatalog.Parse(json, "test")).Code);

    [Fact]
    public void A_catalog_pattern_without_the_number_group_is_refused()
    {
        string json = "{\"schema\":1,\"text\":{\"file\":\"a.ini\",\"nameKey\":\"n\",\"descriptionKey\":\"d\",\"maxNameLength\":40,\"maxDescriptionLines\":8,\"maxDescriptionLineLength\":120}," +
            "\"settings\":[{\"id\":\"a.b\",\"label\":\"L\",\"options\":[\"x\"],\"desc\":\"d\",\"unit\":\"u\",\"kind\":\"int\",\"min\":0,\"max\":10,\"targets\":[{\"file\":\"a.ini\",\"section\":\"core\",\"key\":\"k\",\"pattern\":\"x=([0-9]+)\"}]}]}";
        Assert.Contains("group named v", Assert.Throws<EditorRefusalException>(() => OptionCatalog.Parse(json, "test")).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- file lines
    [Theory]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\r\nb\nc")]
    [InlineData("")]
    [InlineData("\n\n")]
    public void Lines_round_trip_byte_for_byte(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(bytes, OptionFile.Parse("x.ini", bytes).ToBytes());
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        Assert.Equal(bom, OptionFile.Parse("x.ini", bom).ToBytes());
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_refused()
    {
        Assert.Equal("OPTION_FILE_ENCODING", Assert.Throws<EditorRefusalException>(() => OptionFile.Parse("x.ini", new byte[] { 0x61, 0xFF, 0xFE })).Code);
    }

    [Fact]
    public void A_commented_line_is_not_a_line_of_the_section()
    {
        var f = OptionFile.Parse("x.ini", Encoding.UTF8.GetBytes("[a]\n#key: 1\nkey: 2\n[b]\nkey: 3\n"));
        Assert.Equal(new[] { 2 }, f.FindAll("a", "key"));
        Assert.Equal("3", f.Value("b", "key"));
    }

    // ---------------------------------------------------------------------------------------------------------------- numbers
    [Fact]
    public void Settings_are_read_from_the_shipped_text()
    {
        var s = Model().Settings.ToDictionary(x => x.Def.Id);
        Assert.All(s.Values, x => Assert.True(x.Available, x.Reason));
        Assert.Equal("250", s["income.credits"].Shipped);
        Assert.Equal("100", s["income.rust"].Shipped);
        Assert.Equal("100", s["richStart.credits"].Shipped);
        Assert.Equal("20", s["richStart.rust"].Shipped);
    }

    [Fact]
    public void Two_numbers_on_one_line_change_one_line_and_the_line_ending_stays()
    {
        var patch = Model().Plan(Values(("income.credits", "400"), ("income.rust", "150")), OnSections);
        var edit = Assert.Single(patch.Edits, e => e.File == Manager && e.Key == "generation_resources");
        Assert.Equal("generation_resources: credits=250, rust=100", edit.OldLine);
        Assert.Equal("generation_resources: credits=400, rust=150", edit.NewLine);
        Assert.Equal(5, edit.Line);
        string text = Encoding.UTF8.GetString(patch.NewBytes[Manager]);
        Assert.Equal(ManagerText.Replace("credits=250, rust=100", "credits=400, rust=150", StringComparison.Ordinal), text);   // nothing else moved, CRLF kept
        Assert.DoesNotContain(Selector, patch.NewBytes.Keys);
    }

    [Fact]
    public void The_rich_start_multiplier_also_changes_the_options_own_description_in_both_languages()
    {
        var patch = Model().Plan(Values(("richStart.credits", "300")), OnSections);
        Assert.Contains("credits=memory.startingRes * 300", Encoding.UTF8.GetString(patch.NewBytes[Manager]), StringComparison.Ordinal);
        string sel = Encoding.UTF8.GetString(patch.NewBytes[Selector]);
        Assert.Contains("acquire 300 times", sel, StringComparison.Ordinal);
        Assert.Contains("获得300倍", sel, StringComparison.Ordinal);
        Assert.Equal(3, patch.Edits.Count);
    }

    [Fact]
    public void A_description_the_user_wrote_wins_over_the_linked_number()
    {
        var t = Values(("richStart.credits", "300"));
        t.SetDescription("richStart", "- You start rich");
        var patch = Model().Plan(t, OnSections);
        string sel = Encoding.UTF8.GetString(patch.NewBytes[Selector]);
        Assert.Contains("description:- You start rich", sel, StringComparison.Ordinal);
        Assert.DoesNotContain("acquire", sel, StringComparison.Ordinal);
        Assert.Contains("获得300倍", sel, StringComparison.Ordinal);   // the other language has no custom text, so it follows the number
    }

    [Fact]
    public void No_choices_give_the_shipped_bytes_and_no_edits()
    {
        var patch = Model().Plan(new OptionTuning(), OnSections);
        Assert.Empty(patch.NewBytes);
        Assert.Empty(patch.Edits);
    }

    // ---------------------------------------------------------------------------------------------------------------- text
    [Fact]
    public void A_name_and_a_description_replace_only_their_lines()
    {
        var t = new OptionTuning();
        t.SetName("baseIncome", "Steady income");
        t.SetDescription("baseIncome", "- Everyone earns a bit\\n- All the time");
        var patch = Model().Plan(t, OnSections);
        string sel = Encoding.UTF8.GetString(patch.NewBytes[Selector]);
        Assert.Equal(SelectorText.Replace("@define actionName: Base Income", "@define actionName: Steady income", StringComparison.Ordinal)
            .Replace("description:- All players have a small unconditional income", "description:- Everyone earns a bit\\n- All the time", StringComparison.Ordinal), sel);
        Assert.Equal(2, patch.Edits.Count);
    }

    [Fact]
    public void Text_and_shipped_text_are_both_available_to_the_page()
    {
        var t = new OptionTuning();
        t.SetName("baseIncome", "Steady income");
        var edits = Model().Plan(t, OnSections).Edits;
        var after = OptionModel.Load(rel => Model().Plan(t, OnSections).NewBytes.TryGetValue(rel, out var b) ? b : Files()(rel), edits);
        var ts = after.Text("baseIncome", "action_baseIncomeOn")!;
        Assert.Equal("Base Income", ts.ShippedName);
        Assert.Equal("Steady income", ts.CurrentName);
    }

    // ---------------------------------------------------------------------------------------------------------------- recorded edits
    [Fact]
    public void Undoing_the_recorded_edits_gives_back_the_shipped_bytes()
    {
        var patch = Model().Plan(Values(("income.credits", "400"), ("richStart.credits", "7")), OnSections);
        foreach (var (rel, bytes) in patch.NewBytes)
            Assert.Equal(Files()(rel), OptionModel.TryUndo(rel, bytes, patch.Edits));
    }

    [Fact]
    public void A_line_changed_by_hand_is_not_what_was_recorded()
    {
        var patch = Model().Plan(Values(("income.credits", "400")), OnSections);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(patch.NewBytes[Manager]).Replace("credits=400", "credits=999", StringComparison.Ordinal));
        Assert.Null(OptionModel.TryUndo(Manager, tampered, patch.Edits));
        var load = Assert.Throws<EditorRefusalException>(() => OptionModel.Load(rel => rel == Manager ? tampered : Files()(rel), patch.Edits));
        Assert.Equal("OPTION_EDITS_STALE", load.Code);
    }

    [Fact]
    public void An_edit_of_a_file_outside_the_catalog_is_refused()
    {
        var bad = new List<OptionEdit> { new() { File = "units/tank.ini", Line = 1, OldLine = "a", NewLine = "b" } };
        Assert.Equal("OPTION_EDIT_FILE", Assert.Throws<EditorRefusalException>(() => Model(bad)).Code);
    }

    [Fact]
    public void Changing_the_value_again_and_putting_it_back_work_from_the_shipped_text()
    {
        var first = Model().Plan(Values(("income.credits", "400")), OnSections);
        byte[] manager1 = first.NewBytes[Manager];
        var second = OptionModel.Load(rel => rel == Manager ? manager1 : Files()(rel), first.Edits);
        var again = second.Plan(Values(("income.credits", "500")), OnSections);
        Assert.Equal("generation_resources: credits=250, rust=100", Assert.Single(again.Edits).OldLine);   // always relative to the shipped line
        var back = second.Plan(new OptionTuning(), OnSections);
        Assert.Empty(back.Edits);
        Assert.Equal(Files()(Manager), back.NewBytes[Manager]);
    }

    // ---------------------------------------------------------------------------------------------------------------- checks
    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("007")]
    [InlineData("100001")]
    [InlineData("")]
    public void A_number_must_be_a_plain_whole_number_in_range(string value) =>
        Assert.Contains(Model().Validate(Values(("income.credits", value)), OnSections), f => f.Severity == Severity.Error && f.Code is "OPTION_VALUE_INVALID" or "OPTION_VALUE_RANGE");

    [Fact]
    public void A_very_high_number_is_a_warning_and_a_normal_one_is_fine()
    {
        Assert.Empty(Model().Validate(Values(("income.credits", "400")), OnSections));
        var high = Model().Validate(Values(("income.credits", "9000")), OnSections);
        Assert.Equal(Severity.Warning, Assert.Single(high).Severity);
    }

    [Fact]
    public void Unknown_settings_and_options_are_errors()
    {
        var t = Values(("nope.value", "1"));
        t.SetName("noSuchOption", "x");
        var f = Model().Validate(t, OnSections);
        Assert.Contains(f, x => x.Code == "OPTION_SETTING_UNKNOWN");
        Assert.Contains(f, x => x.Code == "OPTION_UNKNOWN");
    }

    [Fact]
    public void A_missing_file_makes_its_settings_unavailable_and_refuses_choices_for_them()
    {
        var m = OptionModel.Load(rel => rel == Selector ? Files()(rel) : null, new List<OptionEdit>());
        Assert.All(m.Settings, s => Assert.False(s.Available));
        Assert.Contains(m.Validate(Values(("income.credits", "400")), OnSections), f => f.Code == "OPTION_SETTING_UNAVAILABLE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad ${name}")]
    [InlineData("two  spaces")]
    [InlineData("a: b")]
    public void A_name_must_be_a_valid_name(string name)
    {
        var t = new OptionTuning();
        t.SetName("baseIncome", name);
        Assert.Contains(Model().Validate(t, OnSections), f => f.Code == "OPTION_NAME_INVALID");
    }

    [Fact]
    public void A_description_is_cleaned_so_nothing_can_become_an_expression()
    {
        var c = OptionCatalog.Embedded;
        Assert.Equal("- Line one\\n- Line two", DescriptionRules.Sanitize("- Line one\r\n- Line two", c));
        Assert.Equal("- It costs 5%", DescriptionRules.Sanitize("- It costs 5%", c));
        Assert.DoesNotContain("$", DescriptionRules.Sanitize("a ${x} %{y} [z] #c \"q\" \\ b", c), StringComparison.Ordinal);
        Assert.Equal(c.MaxDescriptionLines, DescriptionRules.Sanitize(string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i)), c).Split("\\n").Length);
        Assert.All(DescriptionRules.Sanitize(new string('x', 500), c).Split("\\n"), l => Assert.True(l.Length <= c.MaxDescriptionLineLength));
        var t = new OptionTuning();
        t.SetDescription("baseIncome", "bad ${x}");
        Assert.Contains(Model().Validate(t, OnSections), f => f.Code == "OPTION_DESCRIPTION_INVALID");
    }

    [Fact]
    public void Choices_equal_to_the_shipped_text_or_number_are_dropped()
    {
        var t = Values(("income.credits", "250"), ("income.rust", "120"));
        t.SetName("baseIncome", "Base Income");
        t.SetName("richStart", "Rich start!");
        var n = Model().Normalize(t, OnSections);
        Assert.Equal(new[] { "income.rust" }, n.Values.Keys);
        Assert.Equal(new[] { "richStart" }, n.Texts.Keys);
    }

    // ---------------------------------------------------------------------------------------------------------------- through the pipeline (a test copy)
    static ApplyOptions Opt(TestMod m) => new() { BackupRoot = m.BackupRoot };

    static TestMod Copy() => new(extraFiles: new Dictionary<string, string> { [Manager] = ManagerText });

    static EditorSettings Desired(Action<OptionTuning> change)
    {
        var s = new EditorSettings();
        change(s.Tuning);
        return s;
    }

    [Fact]
    public void Apply_rewrites_the_locked_line_records_it_and_the_locked_file_check_still_passes()
    {
        using var m = Copy();
        var support = EditorSupport.Load(m.Root);
        var r = GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.credits", "400")), Opt(m));
        Assert.True(r.Written);
        Assert.Contains("credits=400, rust=100", m.Read(Manager), StringComparison.Ordinal);
        Assert.Contains("\r\n", m.Read(Manager), StringComparison.Ordinal);
        var saved = GroupsPipeline.LoadCurrent(m.Root);
        Assert.Equal("400", saved.Tuning.Values["income.credits"]);
        var edit = Assert.Single(saved.OptionEdits);
        Assert.Equal(("generation_resources: credits=250, rust=100", "generation_resources: credits=400, rust=100"), (edit.OldLine, edit.NewLine));
        Assert.True(support.VerifyLockedFiles(m.Root).Ok, support.VerifyLockedFiles(m.Root).Describe());
        Assert.Contains("income.credits = 400", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_locked_file_check_still_catches_every_change_the_editor_did_not_record()
    {
        using var m = Copy();
        var support = EditorSupport.Load(m.Root);
        GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.credits", "400")), Opt(m));
        string path = Path.Combine(m.Root, Manager);
        string good = File.ReadAllText(path);
        File.WriteAllText(path, good.Replace("name: ManagerBasic", "name: ManagerBasic2", StringComparison.Ordinal));   // another line, by hand
        Assert.False(support.VerifyLockedFiles(m.Root).Ok);
        File.WriteAllText(path, good.Replace("credits=400", "credits=401", StringComparison.Ordinal));   // the recorded line, by hand
        Assert.False(support.VerifyLockedFiles(m.Root).Ok);
        File.WriteAllText(path, good);
        Assert.True(support.VerifyLockedFiles(m.Root).Ok);
        File.Delete(CopyFiles.SettingsPath(m.Root));   // no record: the edit can no longer be explained
        Assert.False(support.VerifyLockedFiles(m.Root).Ok);
    }

    [Fact]
    public void A_recorded_edit_never_excuses_a_file_outside_the_catalog()
    {
        using var m = Copy();
        var support = EditorSupport.Load(m.Root);
        GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.credits", "400")), Opt(m));
        string other = Path.Combine(m.Root, "myAssets/managerUnit.ini");
        File.WriteAllText(other, File.ReadAllText(other).Replace("radius: 10", "radius: 11", StringComparison.Ordinal));
        Assert.False(support.VerifyLockedFiles(m.Root).Ok);
    }

    [Fact]
    public void Undo_puts_the_locked_file_the_generated_files_and_the_settings_back_byte_for_byte()
    {
        using var m = Copy();
        var before = new[] { Manager, EditorSupport.SelectorGenerated, EditorSupport.AnchorGenerated }.ToDictionary(x => x, x => m.Bytes(x));
        GroupsPipeline.Apply(m.Root, Desired(t => { t.SetValue("income.credits", "400"); t.SetName("richStart", "Rich beginning"); }), Opt(m));
        Assert.NotEqual(before[Manager], m.Bytes(Manager));
        ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: CopyFiles.SettingsPath(m.Root));
        foreach (var (rel, bytes) in before) Assert.Equal(bytes, m.Bytes(rel));
        Assert.True(GroupsPipeline.LoadCurrent(m.Root).Tuning.IsEmpty);
        Assert.Empty(GroupsPipeline.LoadCurrent(m.Root).OptionEdits);
        Assert.True(EditorSupport.Load(m.Root).VerifyLockedFiles(m.Root).Ok);
    }

    [Fact]
    public void Revert_all_restores_the_shipped_locked_files_and_clears_the_record()
    {
        using var m = Copy();
        var shipped = m.Bytes(Manager);
        GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.rust", "5")), Opt(m));
        var r = GroupsPipeline.RevertAll(m.Root, Opt(m));
        Assert.True(r.Written);
        Assert.Equal(shipped, m.Bytes(Manager));
        var saved = GroupsPipeline.LoadCurrent(m.Root);
        Assert.True(saved.Tuning.IsEmpty);
        Assert.Empty(saved.OptionEdits);
    }

    [Fact]
    public void A_choice_that_equals_the_shipped_number_is_not_saved_and_writes_nothing()
    {
        using var m = Copy();
        GroupsPipeline.Apply(m.Root, new EditorSettings(), Opt(m));   // the first apply writes the generator's own header into the (fixture) generated files
        var r = GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.credits", "250")), Opt(m));
        Assert.False(r.Written);
        Assert.True(GroupsPipeline.LoadCurrent(m.Root).Tuning.IsEmpty);
    }

    [Fact]
    public void A_bad_number_stops_the_apply_before_anything_is_written()
    {
        using var m = Copy();
        var before = m.Bytes(Manager);
        Assert.Throws<RwEditor.Core.Generation.SettingsInvalidException>(() => GroupsPipeline.Apply(m.Root, Desired(t => t.SetValue("income.credits", "-1")), Opt(m)));
        Assert.Equal(before, m.Bytes(Manager));
        Assert.Empty(History.List(m.BackupRoot, m.Root));
    }

    [Fact]
    public void Groups_hidden_options_and_option_choices_apply_together_and_undo_together()
    {
        using var m = Copy();
        var s = TestMod.Sample2();
        s.SetOptionState("fairAI", OptionState.Hidden);   // refused: always stays
        Assert.Throws<RwEditor.Core.Generation.SettingsInvalidException>(() => GroupsPipeline.Apply(m.Root, s, Opt(m)));
        s = TestMod.Sample2();
        s.SetOptionState("richStart", OptionState.Hidden);
        s.Tuning.SetValue("income.credits", "300");
        var r = GroupsPipeline.Apply(m.Root, s, Opt(m));
        Assert.True(r.Written);
        var saved = GroupsPipeline.LoadCurrent(m.Root);
        Assert.Equal(2, saved.SpawnGroups.Count);
        Assert.Equal(OptionState.Hidden, saved.GetOptionState("richStart"));
        Assert.Equal("300", saved.Tuning.Values["income.credits"]);
        ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: CopyFiles.SettingsPath(m.Root));
        var undone = GroupsPipeline.LoadCurrent(m.Root);
        Assert.Empty(undone.SpawnGroups);
        Assert.True(undone.Tuning.IsEmpty);
        Assert.Contains("credits=250, rust=100", m.Read(Manager), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_with_choices_and_edits_survive_a_json_round_trip_with_the_same_hash()
    {
        using var m = Copy();
        var t = Desired(x => { x.SetValue("income.credits", "400"); x.SetName("richStart", "Rich beginning"); x.SetDescription("richStart", "- a\\n- b"); });
        GroupsPipeline.Apply(m.Root, t, Opt(m));
        var saved = GroupsPipeline.LoadCurrent(m.Root);
        var again = SettingsJson.Parse(SettingsJson.Serialize(saved)).Settings;
        Assert.Equal(SettingsJson.ContentHash(saved), SettingsJson.ContentHash(again));
        Assert.Equal("- a\\n- b", again.Tuning.Texts["richStart"].Description);
        Assert.Equal(saved.OptionEdits.Count, again.OptionEdits.Count);
    }

    [Fact]
    public void Settings_without_option_choices_keep_the_exact_text_they_had_before_E5b()
    {
        string json = SettingsJson.Serialize(TestMod.Sample2());
        Assert.DoesNotContain("optionTuning", json, StringComparison.Ordinal);
        Assert.DoesNotContain("optionEdits", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- the real mod
    /// <summary>The strongest check: every number and the text of all 26 options changed at once on the REAL shipped files; undoing the recorded edits gives
    /// back exactly the bytes whose hashes editor-support.json lists, and the changed files pass the include rules and the lint.</summary>
    [RepoFact]
    public void Real_files_change_only_the_catalog_lines_and_undo_to_the_manifest_hashes()
    {
        var ctx = ModContext.Load(Repo.Src);
        var model = OptionModel.Load(Repo.Src, new List<OptionEdit>());
        Assert.All(model.Settings, s => Assert.True(s.Available, s.Def.Id + ": " + s.Reason));
        Assert.Equal(new[] { "250", "100", "100", "20" }, model.Settings.Select(s => s.Shipped));

        var t = new OptionTuning();
        foreach (var d in model.Catalog.Settings) t.SetValue(d.Id, "77");
        foreach (var o in ctx.Options)
        {
            var ts = model.Text(o.Tag, o.OnSection);
            Assert.NotNull(ts?.ShippedName);
            Assert.NotNull(ts!.ShippedDescription);
            t.SetName(o.Tag, "New " + o.Tag.Substring(0, Math.Min(8, o.Tag.Length)));
            t.SetDescription(o.Tag, "- Changed text\\n- Second line");
        }
        Assert.DoesNotContain(model.Validate(t, ctx.OnSections), f => f.Severity == Severity.Error);
        var patch = model.Plan(t, ctx.OnSections);
        Assert.Equal(new[] { Manager, Selector }.OrderBy(x => x), patch.NewBytes.Keys.OrderBy(x => x));
        Assert.All(patch.Edits, e => Assert.True(e.Line >= 1 && e.OldLine != e.NewLine));
        // only the lines of the catalog changed: 26 names + 26 descriptions + the (single) income line + the (single) rich start line (its description lines are the option's own)
        Assert.True(patch.Edits.Count <= 26 * 3 + 2, "edits: " + patch.Edits.Count);

        var support = EditorSupport.Load(Repo.Src);
        foreach (var (rel, bytes) in patch.NewBytes)
        {
            var back = OptionModel.TryUndo(rel, bytes, patch.Edits);
            Assert.NotNull(back);
            Assert.Equal(support.LockedFiles[rel], Convert.ToHexStringLower(SHA256.HashData(back!)));
        }

        var files = patch.NewBytes.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value));
        var planned = ctx.Snapshot.WithFiles(files);
        Assert.True(IncludeRules.Check(planned).AllPass);
        Assert.Empty(RwLint.NewFindings(ctx.Snapshot, planned));
        // the changed selector still loads as the same options (same tags, same sections, new names)
        var after = ModContext.Build(planned, support);
        Assert.Equal(ctx.Options.Select(o => o.Tag), after.Options.Select(o => o.Tag));
        Assert.All(after.Options, o => Assert.StartsWith("New ", o.Name, StringComparison.Ordinal));
    }

    [RepoFact]
    public void Real_selector_text_the_page_would_edit_survives_the_cleaning_rules_or_is_shown_as_it_is()
    {
        // the shipped descriptions are shown as they are; only an edited description is cleaned. The cleaner must keep the ordinary ones intact enough to be useful.
        var ctx = ModContext.Load(Repo.Src);
        int same = ctx.Options.Count(o => DescriptionRules.Sanitize(o.Description.Replace("\n", "\\n", StringComparison.Ordinal), OptionCatalog.Embedded) == o.Description.Replace("\n", "\\n", StringComparison.Ordinal));
        Assert.True(same >= ctx.Options.Count - 6, $"{same} of {ctx.Options.Count} shipped descriptions pass the cleaning rules unchanged");
    }
}
