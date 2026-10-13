using System.Security.Cryptography;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Model;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;
using Xunit;

namespace RwEditor.Tests;

/// <summary>
/// E3 Core: staging (pending list in the settings file), the template-vs-unit choice, family scope, the batch apply (one plan, one backup, one write,
/// settings in step), undo round trip with the settings, history / undo stack, revert scopes (stat / unit / area / all with the copy's saved original and
/// without it via the backups), the baseline snapshot, refusal of folders that are not editor copies, and the plain-words input conversion.
/// </summary>
public class StagingTests
{
    static readonly Dictionary<string, string> Extra = new()
    {
        ["templates/base.ini"] = "[core]\ndont_load: true\nname: base\nmaxHp: 100\nprice: credits = 10, rust = 2\n",
        ["units/a.ini"] = "[core]\nname: unitA\ncopyFrom: ROOT:/templates/base.ini\nmaxHp: 150\n",
        ["units/b.ini"] = "[core]\nname: unitB\ncopyFrom: ROOT:/templates/base.ini\n",
        ["units/c.ini"] = "[core]\nname: unitC\ncopyFrom: a.ini\n",
        ["units/tank.ini"] = "[core]\nname: tank\ndisplayText: Tank\nmaxHp: 500\nbuildSpeed: 10s\n[turret_1]\ndelay: 30\n" +
                             "[hiddenAction_toWater]\nautoTrigger: if self.overWater()\nconvertTo: tank_water\n[action_up]\nconvertTo: tank2\n",
        ["units/tank2.ini"] = "[core]\nname: tank2\ndisplayText: Tank Mk2\nmaxHp: 500\n[action_up]\nconvertTo: tank3\n",
        ["units/tank3.ini"] = "[core]\nname: tank3\ndisplayText: Tank Mk3\nmaxHp: 900\n",
        ["units/tank_water.ini"] = "[core]\nname: tank_water\ndisplayText: Tank (water)\nmaxHp: 500\n[hiddenAction_toLand]\nautoTrigger: if not self.overWater()\nconvertTo: tank\n",
        ["donors/fe/gen.ini"] = "[core]\nname: feGen\nmaxHp: 40\n",
    };

    static TestMod Mod(bool marker = true) => new(null, true, Extra, marker);
    static ApplyOptions Opt(TestMod m, Action<int, string>? hook = null) => new() { BackupRoot = m.BackupRoot, BeforeWriteFile = hook };
    static string SettingsPath(TestMod m) => CopyFiles.SettingsPath(m.Root);

    static PendingStatEdit P(string unit, string file, string stat, string value, string section = "core", string mode = "unit") =>
        new() { Unit = unit, UnitFile = file, StatId = stat, Section = section, Value = value, Mode = mode };

    static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'),
            f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    static void AssertOnlyChanged(Dictionary<string, string> before, Dictionary<string, string> after, params string[] changed)
    {
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.Where(k => k != CopyFiles.SettingsName || before.ContainsKey(k)).OrderBy(k => k));
        foreach (var (k, h) in before)
            if (changed.Contains(k, StringComparer.OrdinalIgnoreCase)) Assert.NotEqual(h, after[k]);
            else if (k != CopyFiles.SettingsName) Assert.Equal(h, after[k]);
    }

    // ------------------------------------------------------------------------------------------------------------ settings: the pending list
    [Fact]
    public void Pending_changes_round_trip_canonically_and_old_settings_keep_their_hash()
    {
        var s = new EditorSettings();
        string emptyHash = SettingsJson.ContentHash(s);
        Assert.DoesNotContain("pendingStatEdits", SettingsJson.Serialize(s));   // absent when empty: E1b settings keep their exact text / hash
        s.Stage(P("unitA", "units/a.ini", "core.maxHp", "175"));
        s.Stage(P("tank2", "units/tank2.ini", "core.maxHp", "600"));
        s.PendingStatEdits[1].FromFamilyOf = "tank";
        string text = SettingsJson.Serialize(s);
        var back = SettingsJson.Parse(text);
        Assert.Equal(HashStatus.Match, back.Hash);
        Assert.Equal(text, SettingsJson.Serialize(back.Settings));             // deterministic
        Assert.Equal("tank", back.Settings.PendingStatEdits[1].FromFamilyOf);
        Assert.NotEqual(emptyHash, SettingsJson.ContentHash(back.Settings));
        // a newer change of the same target replaces the older one (newest value wins, order = when it was staged last)
        back.Settings.Stage(P("unitA", "units/a.ini", "core.maxHp", "180"));
        Assert.Equal(new[] { "tank2", "unitA" }, back.Settings.PendingStatEdits.Select(p => p.Unit));
        Assert.Equal("180", back.Settings.PendingStatEdits[1].Value);
    }

    [Fact]
    public void Strict_reader_refuses_unknown_members_bad_modes_and_a_target_staged_twice()
    {
        var s = new EditorSettings();
        s.Stage(P("unitA", "units/a.ini", "core.maxHp", "175"));
        string ok = SettingsJson.Serialize(s);
        Assert.Equal("SETTINGS_INVALID", Assert.Throws<EditorRefusalException>(() => SettingsJson.Parse(ok.Replace("\"value\"", "\"valu\""))).Code);
        Assert.Equal("SETTINGS_INVALID", Assert.Throws<EditorRefusalException>(() => SettingsJson.Parse(ok.Replace("\"mode\": \"unit\"", "\"mode\": \"all\""))).Code);
        string twice = ok.Replace("\"pendingStatEdits\": [", "\"pendingStatEdits\": [{\"mode\": \"unit\", \"section\": \"core\", \"statId\": \"core.maxHp\", \"unit\": \"unitA\", \"unitFile\": \"units/a.ini\", \"value\": \"1\"},");
        Assert.Contains("staged twice", Assert.Throws<EditorRefusalException>(() => SettingsJson.Parse(twice)).Message);
    }

    // ------------------------------------------------------------------------------------------------------------ input in plain words
    [Fact]
    public void Typed_values_convert_to_the_lines_own_form_and_quick_checks_give_plain_messages()
    {
        var d = StatDictionary.Embedded;
        var delay = d.ById("turret.delay")!;
        Assert.Equal("30", StatText.ToRequestValue(delay, "45", "0.5", out _));          // a frames line stays frames
        Assert.Equal("0.51s", StatText.ToRequestValue(delay, "45", "0.51", out _));      // not a whole number of frames: seconds form
        Assert.Equal("2s", StatText.ToRequestValue(delay, "1s", "2", out _));            // a seconds line stays seconds
        Assert.Equal("40s", StatText.ToRequestValue(d.ById("core.buildSpeed")!, "0.0005", "40", out _));   // build time always "Ns"
        Assert.Equal("2.5", StatText.ToRequestValue(d.ById("movement.moveSpeed")!, "1", "2,5", out _));    // a decimal comma is accepted
        Assert.Null(StatText.ToRequestValue(d.ById("core.maxHp")!, "10", "10.5", out var e1));
        Assert.Contains("whole number", e1);
        Assert.Null(StatText.ToRequestValue(d.ById("core.maxHp")!, "10", "lots", out var e2));
        Assert.Contains("not a number", e2);
        Assert.Contains("Allowed: 1 .. 100000000 hit points", StatText.QuickCheck(d.ById("core.maxHp")!, "10", "0", out _));
        Assert.Null(StatText.QuickCheck(d.ById("core.maxHp")!, "10", "250", out var rv));
        Assert.Equal("250", rv);
        Assert.Equal("0.0167 .. 3600 seconds", StatText.Range(delay));
        Assert.Equal("0.5 seconds", StatText.Value(delay, 30));
        // several "calculated from this value" notes of one unit become one plain line
        var w = StatText.PlainWarnings(new[]
        {
            new Finding(Severity.Warning, "EXPRESSION_DEPENDS", "tank [core] mass: ${core.maxHp*2} may be computed from maxHp"),
            new Finding(Severity.Warning, "EXPRESSION_DEPENDS", "tank [action_x] setUnitStats: maxHp+=${core.maxHp} may be computed from maxHp"),
            new Finding(Severity.Warning, "SOFT_LIMIT_RELATIVE", "core.maxHp goes above 10 x the current value 100"),
        });
        Assert.Equal(2, w.Count);
        Assert.Contains(w, l => l.StartsWith("Unusual value:", StringComparison.Ordinal));
        Assert.Contains("2 other value(s) of tank are calculated from this one and change with it (mass, setUnitStats).", w);
    }

    // ------------------------------------------------------------------------------------------------------------ template vs unit choice
    [Fact]
    public void Info_tells_whether_a_value_is_shared_and_with_which_units()
    {
        using var m = Mod();
        var model = UnitModel.Load(m.Root);
        var d = StatDictionary.Embedded;
        var b = StatStaging.Info(model, d, null, model.Find("unitB")!, d.ById("core.maxHp")!, "core");
        Assert.False(b.Shared);                                                     // the template's maxHp is read by unitB only (unitA has its own)
        var price = StatStaging.Info(model, d, null, model.Find("unitB")!, d.ById("core.price.credits")!, "core");
        Assert.True(price.Shared);
        Assert.Equal(new[] { "unitA", "unitB", "unitC" }, price.SharingUnits.Select(u => u.Name));   // unitC reads it through a.ini
        var a = StatStaging.Info(model, d, null, model.Find("unitA")!, d.ById("core.maxHp")!, "core");
        Assert.True(a.Shared);                                                      // unitA's OWN line: unitC copies a.ini
        Assert.Equal(new[] { "unitA", "unitC" }, a.SharingUnits.Select(u => u.Name));
    }

    [Fact]
    public void Shared_value_unit_only_and_template_choices_are_checked_exactly()
    {
        using var m = Mod();
        var model = UnitModel.Load(m.Root);
        var d = StatDictionary.Embedded;
        byte[] Read(string rel) => m.Bytes(rel);
        // unitB price: "this unit only" adds a line to b.ini; "every unit" patches the template and lists A, B and C
        var only = StatStaging.Check(model, d, P("unitB", "units/b.ini", "core.price.credits", "12"), Read);
        Assert.True(only.Ok);
        Assert.Equal(StatWriteKind.Insert, only.Items[0].Plan!.Record.WriteKind);
        var all = StatStaging.Check(model, d, P("unitB", "units/b.ini", "core.price.credits", "12", mode: "template"), Read);
        Assert.True(all.Ok);
        Assert.Equal(new[] { "unitA", "unitB", "unitC" }, all.Items[0].Plan!.Affected.Select(x => x.Unit.Name).OrderBy(x => x));
        // unitA maxHp is its own line, but unitC copies a.ini: "this unit only" is impossible (plain reason), "every unit that shares it" is fine
        var aOnly = StatStaging.Check(model, d, P("unitA", "units/a.ini", "core.maxHp", "175"), Read);
        Assert.False(aOnly.Ok);
        Assert.Contains(aOnly.Items[0].Errors, f => f.Code == "NOT_UNIT_ONLY");
        Assert.Contains("cannot be changed for this unit alone", aOnly.Problems.Single());
        var aAll = StatStaging.Check(model, d, P("unitA", "units/a.ini", "core.maxHp", "175", mode: "template"), Read);
        Assert.True(aAll.Ok);
        Assert.Equal(new[] { "unitA", "unitC" }, aAll.Items[0].Plan!.Affected.Select(x => x.Unit.Name));
        // hard limits, locked keys and locked units are refused with the dictionary's message
        Assert.Contains(StatStaging.Check(model, d, P("unitB", "units/b.ini", "core.maxHp", "0"), Read).Items[0].Errors, f => f.Code == "OUT_OF_RANGE");
        Assert.Contains(StatStaging.Check(model, d, P("managerUnit", "myAssets/managerUnit.ini", "core.maxHp", "5"), Read).Items[0].Errors, f => f.Code == "UNIT_LOCKED");
        // a soft limit is a warning, not a block
        var soft = StatStaging.Check(model, d, P("unitB", "units/b.ini", "core.maxHp", "5000"), Read);
        Assert.True(soft.Ok);
        Assert.Contains(soft.Warnings, f => f.Code == "SOFT_LIMIT_RELATIVE");
    }

    // ------------------------------------------------------------------------------------------------------------ family scope
    [Fact]
    public void Family_candidates_are_versions_with_the_same_value_and_every_one_is_checked()
    {
        using var m = Mod();
        var model = UnitModel.Load(m.Root);
        var fam = UnitFamilies.Build(model);
        var d = StatDictionary.Embedded;
        var info = StatStaging.Info(model, d, fam, model.Find("tank")!, d.ById("core.maxHp")!, "core");
        Assert.Equal(new[] { "tank2", "tank_water" }, info.FamilyCandidates.Select(c => c.Unit.Name).OrderBy(x => x));
        Assert.Contains(info.FamilySkipped, s => s.Unit.Name == "tank3" && s.Reason.Contains("different value"));
        var check = StatStaging.Check(model, d, P("tank", "units/tank.ini", "core.maxHp", "650"), rel => m.Bytes(rel), info.FamilyCandidates);
        Assert.True(check.Ok);
        Assert.Equal(new[] { "tank", "tank2", "tank_water" }, check.Pendings.Select(p => p.Unit).OrderBy(x => x));
        Assert.All(check.Pendings.Skip(1), p => Assert.Equal("tank", p.FromFamilyOf));
        Assert.All(check.Items, i => Assert.Single(i.Plan!.Affected));                 // each one "this unit only"
        // a member that would fail makes the whole family choice fail (here: a value the hard limit refuses for everyone)
        Assert.False(StatStaging.Check(model, d, P("tank", "units/tank.ini", "core.maxHp", "-5"), rel => m.Bytes(rel), info.FamilyCandidates).Ok);
        // a stat the members do not share in the same section gives no candidates
        Assert.Empty(StatStaging.Info(model, d, fam, model.Find("tank")!, d.ById("turret.delay")!, "turret_1").FamilyCandidates);
    }

    // ------------------------------------------------------------------------------------------------------------ batch apply + undo
    [Fact]
    public void Apply_writes_every_staged_change_in_one_go_and_undo_gives_back_identical_bytes()
    {
        using var m = Mod();
        var before = Hashes(m.Root);
        var beforeBytes = Directory.EnumerateFiles(m.Root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        StatStaging.SaveStaged(m.Root, new[]
        {
            P("unitB", "units/b.ini", "core.maxHp", "130"),                                    // unit only: inserted into b.ini
            P("tank", "units/tank.ini", "core.price.credits", "250"),                          // unit only: [core] price added to tank.ini
            P("unitA", "units/a.ini", "core.price.rust", "3", mode: "template"),               // shared line in base.ini: A, B, C
            P("tank", "units/tank.ini", "turret.delay", "0.25s", section: "turret_1"),          // a time value, seconds form
        });
        var staged = Hashes(m.Root);
        AssertOnlyChanged(before, staged);                                                     // staging writes only the settings file
        Assert.Equal(4, SettingsJson.Load(SettingsPath(m)).Settings.PendingStatEdits.Count);

        var r = StatBatch.Apply(m.Root, Opt(m));
        var after = Hashes(m.Root);
        AssertOnlyChanged(before, after, "units/b.ini", "units/tank.ini", "templates/base.ini");
        Assert.Contains("maxHp: 130", m.Read("units/b.ini"));
        Assert.Contains("price: credits=250", m.Read("units/tank.ini"));          // no price anywhere before: a new credits part
        Assert.Contains("delay: 0.25s", m.Read("units/tank.ini"));
        Assert.Contains("rust = 3", m.Read("templates/base.ini"));
        Assert.Equal(new[] { "templates/base.ini", "units/b.ini", "units/tank.ini" }, r.WrittenFiles.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(r.Plan.NewLintFindings);
        var s = SettingsJson.Load(SettingsPath(m));
        Assert.Equal(HashStatus.Match, s.Hash);
        Assert.Empty(s.Settings.PendingStatEdits);
        Assert.Equal(4, s.Settings.StatEdits.Count);
        Assert.Equal(3, s.Settings.StatEdits.Single(e => e.Mode == "template").AffectedUnits);
        var bk = BackupStore.Read(r.BackupPath);
        Assert.Equal("stat-edit", bk.Kind);
        Assert.NotNull(bk.SettingsBytes);                                                      // the staged list is in the backup too
        Assert.Contains("unitB Health 100 hit points -> 130 hit points", bk.Summary);
        Assert.Empty(Directory.EnumerateFiles(m.Root, "*.rwe-tmp", SearchOption.AllDirectories));

        // undo: every file byte-identical to before the apply; the settings list the changes as before (none applied), the pending list stays as now (empty)
        var u = ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: SettingsPath(m));
        Assert.Equal(r.BackupPath, u.RestoredFrom);
        foreach (var (f, bytes) in beforeBytes) Assert.Equal(bytes, File.ReadAllBytes(f));
        var s2 = SettingsJson.Load(SettingsPath(m)).Settings;
        Assert.Empty(s2.StatEdits);
        Assert.Empty(s2.PendingStatEdits);
        // nothing more to undo (the change is undone; the undo-safety backup is not a change)
        Assert.Null(History.NextUndo(m.BackupRoot, m.Root));
    }

    [Fact]
    public void A_refused_staged_change_blocks_the_whole_apply_and_nothing_is_written()
    {
        using var m = Mod();
        var before = Hashes(m.Root);
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130"), P("unitA", "units/a.ini", "core.maxHp", "175") });   // the 2nd: not unit-only
        var staged = Hashes(m.Root);
        var e = Assert.Throws<StatBatchRefusedException>(() => StatBatch.Apply(m.Root, Opt(m)));
        Assert.Contains("unitA", e.Message);
        Assert.True(e.Plan.Items[0].Ok);
        Assert.False(e.Plan.Items[1].Ok);
        Assert.Equal(staged, Hashes(m.Root));
        Assert.False(Directory.Exists(m.BackupRoot) && Directory.EnumerateFiles(m.BackupRoot, "*.zip", SearchOption.AllDirectories).Any());
        Assert.Equal("NOTHING_STAGED", Assert.Throws<EditorRefusalException>(() =>
        {
            StatStaging.SaveStaged(m.Root, null, _ => true);
            StatBatch.Apply(m.Root, Opt(m));
        }).Code);
        AssertOnlyChanged(before, Hashes(m.Root));
    }

    [Fact]
    public void A_write_failure_or_a_settings_failure_puts_every_file_back()
    {
        using var m = Mod();
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130"), P("tank", "units/tank.ini", "core.maxHp", "520") });
        var staged = Hashes(m.Root);
        Assert.Throws<ApplyFailedException>(() => StatBatch.Apply(m.Root, Opt(m, (i, rel) => { if (i == 1) throw new IOException("simulated"); })));
        var afterFail = Hashes(m.Root);
        Assert.Equal(string.Join(",", staged.Keys.OrderBy(k => k)), string.Join(",", afterFail.Keys.OrderBy(k => k)));
        Assert.Equal(staged, afterFail);
        // the settings file cannot be replaced: the two written files are put back, the settings still list the staged changes
        File.SetAttributes(SettingsPath(m), FileAttributes.ReadOnly);
        try
        {
            var e = Assert.Throws<ApplyFailedException>(() => StatBatch.Apply(m.Root, Opt(m)));
            Assert.True(e.Restored);
            Assert.Equal(staged, Hashes(m.Root));
        }
        finally { File.SetAttributes(SettingsPath(m), FileAttributes.Normal); }
        Assert.Equal(2, SettingsJson.Load(SettingsPath(m)).Settings.PendingStatEdits.Count);
    }

    [Fact]
    public void Folders_that_are_not_editor_copies_or_have_changed_locked_files_are_refused()
    {
        using var noCopy = Mod(marker: false);
        Assert.Equal("NO_COPY_MARKER", Assert.ThrowsAny<EditorRefusalException>(() => StatStaging.SaveStaged(noCopy.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130") })).Code);
        Assert.Equal("NO_COPY_MARKER", Assert.ThrowsAny<EditorRefusalException>(() => StatBatch.Apply(noCopy.Root, Opt(noCopy))).Code);
        Assert.Equal("NO_COPY_MARKER", Assert.ThrowsAny<EditorRefusalException>(() => StatRevert.Apply(noCopy.Root, new RevertRequest(RevertScope.All), Opt(noCopy))).Code);
        Assert.False(File.Exists(SettingsPath(noCopy)));

        using var m = Mod();
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130") });
        m.Put("myAssets/managerUnit.ini", "[core]\nname: managerUnit\nradius: 11\n");
        var before = m.Bytes("units/b.ini");
        Assert.Equal("LOCKED_FILES_CHANGED", Assert.ThrowsAny<EditorRefusalException>(() => StatBatch.Apply(m.Root, Opt(m))).Code);
        Assert.Equal(before, m.Bytes("units/b.ini"));
    }

    // ------------------------------------------------------------------------------------------------------------ history / undo stack
    [Fact]
    public void History_lists_changes_with_summaries_and_repeated_undo_steps_back()
    {
        using var m = Mod();
        var orig = m.Bytes("units/b.ini");
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130") });
        var t = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        StatBatch.Apply(m.Root, new ApplyOptions { BackupRoot = m.BackupRoot, UtcNow = () => t });
        var after1 = m.Bytes("units/b.ini");
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "140") });
        StatBatch.Apply(m.Root, new ApplyOptions { BackupRoot = m.BackupRoot, UtcNow = () => t.AddMinutes(1) });
        var h = History.List(m.BackupRoot, m.Root);
        Assert.Equal(2, h.Count);
        Assert.Contains("130 hit points -> 140 hit points", h[0].Summary);                  // newest first
        Assert.Equal("Values changed", h[0].KindText);

        ApplyPipeline.Undo(m.Root, new ApplyOptions { BackupRoot = m.BackupRoot, UtcNow = () => t.AddMinutes(2) }, settingsPath: SettingsPath(m));
        Assert.Equal(after1, m.Bytes("units/b.ini"));
        ApplyPipeline.Undo(m.Root, new ApplyOptions { BackupRoot = m.BackupRoot, UtcNow = () => t.AddMinutes(3) }, settingsPath: SettingsPath(m));
        Assert.Equal(orig, m.Bytes("units/b.ini"));
        Assert.Empty(SettingsJson.Load(SettingsPath(m)).Settings.StatEdits);
        var h2 = History.List(m.BackupRoot, m.Root);
        Assert.Equal(4, h2.Count);
        Assert.All(h2.Where(x => x.IsChange), x => Assert.True(x.Undone));
        Assert.Equal("NO_BACKUP", Assert.Throws<EditorRefusalException>(() => ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: SettingsPath(m))).Code);
        // a damaged backup is listed with its problem and never used
        File.WriteAllText(Path.Combine(BackupStore.Folder(m.BackupRoot, m.Root), "99999999-999999-999-stat-edit.zip"), "not a zip");
        Assert.Contains(History.List(m.BackupRoot, m.Root), x => x.Problem != null);
        Assert.Null(History.NextUndo(m.BackupRoot, m.Root));
    }

    // ------------------------------------------------------------------------------------------------------------ revert scopes
    [Fact]
    public void Revert_per_stat_unit_and_area_regenerates_a_plan_and_is_itself_undoable()
    {
        using var m = Mod();
        var origB = m.Bytes("units/b.ini");
        var origTank = m.Bytes("units/tank.ini");
        var origGen = m.Bytes("donors/fe/gen.ini");
        StatStaging.SaveStaged(m.Root, new[]
        {
            P("unitB", "units/b.ini", "core.maxHp", "130"), P("unitB", "units/b.ini", "core.price.credits", "11"),
            P("tank", "units/tank.ini", "core.maxHp", "520"), P("feGen", "donors/fe/gen.ini", "core.maxHp", "45"),
        });
        StatBatch.Apply(m.Root, Opt(m));

        // this stat: only unitB's Health
        var plan = StatRevert.Plan(m.Root, new RevertRequest(RevertScope.Stat, "unitB", "units/b.ini", "core.maxHp", "core"), Opt(m));
        Assert.Single(plan.Edits);
        StatRevert.Apply(m.Root, new RevertRequest(RevertScope.Stat, "unitB", "units/b.ini", "core.maxHp", "core"), Opt(m));
        Assert.DoesNotContain("maxHp: 130", m.Read("units/b.ini"));
        Assert.Contains("price: credits = 11, rust = 2", m.Read("units/b.ini"));   // the inherited price, with only the credits changed
        // this unit: the rest of unitB
        StatRevert.Apply(m.Root, new RevertRequest(RevertScope.Unit, "unitB", "units/b.ini"), Opt(m));
        Assert.Equal(origB, m.Bytes("units/b.ini"));
        // a mod area: Fabricator Extreme only
        StatRevert.Apply(m.Root, new RevertRequest(RevertScope.Area, Area: "fe"), Opt(m));
        Assert.Equal(origGen, m.Bytes("donors/fe/gen.ini"));
        Assert.Contains("maxHp: 520", m.Read("units/tank.ini"));
        Assert.Single(SettingsJson.Load(SettingsPath(m)).Settings.StatEdits);
        // a revert is a change: undo brings fe back
        ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: SettingsPath(m));
        Assert.Contains("maxHp: 45", m.Read("donors/fe/gen.ini"));
        Assert.Equal(2, SettingsJson.Load(SettingsPath(m)).Settings.StatEdits.Count);
        // nothing to revert -> null, nothing written
        Assert.Null(StatRevert.Apply(m.Root, new RevertRequest(RevertScope.Unit, "unitB", "units/b.ini"), Opt(m)));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(origTank).Replace("maxHp: 500", "maxHp: 520"), m.Read("units/tank.ini"));
    }

    [Fact]
    public void Revert_everything_with_the_saved_original_restores_every_value_file_including_hand_edits()
    {
        using var m = Mod();
        CopyBaseline.Write(m.Root, EditorSupport.Load(m.Root).LockedGlobs, "0123456789abcdef0123456789abcdef", DateTime.UtcNow);   // as "Make my copy" does
        var orig = Hashes(m.Root);
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130"), P("unitA", "units/a.ini", "core.price.rust", "3", mode: "template") });
        StatBatch.Apply(m.Root, Opt(m));
        m.Put("units/tank.ini", m.Read("units/tank.ini").Replace("maxHp: 500", "maxHp: 501"));   // changed outside the editor
        StatStaging.SaveStaged(m.Root, new[] { P("tank2", "units/tank2.ini", "core.maxHp", "610") });   // staged, not applied: stays staged

        var plan = StatRevert.Plan(m.Root, new RevertRequest(RevertScope.All), Opt(m));
        Assert.Equal(new[] { "templates/base.ini", "units/b.ini", "units/tank.ini" }, plan.NewBytes.Keys);
        Assert.StartsWith("every value file back to the copy's saved original", plan.Method);
        var r = StatRevert.Apply(m.Root, new RevertRequest(RevertScope.All), Opt(m))!;
        var now = Hashes(m.Root);
        foreach (var (k, h) in orig) Assert.Equal(h, now[k]);                                          // every file as at copy time
        var s = SettingsJson.Load(SettingsPath(m)).Settings;
        Assert.Empty(s.StatEdits);
        Assert.Single(s.PendingStatEdits);
        Assert.Equal("stat-revert-all", BackupStore.Read(r.BackupPath).Kind);
        // and it can be undone
        ApplyPipeline.Undo(m.Root, Opt(m), settingsPath: SettingsPath(m));
        Assert.Contains("maxHp: 501", m.Read("units/tank.ini"));
        Assert.Equal(2, SettingsJson.Load(SettingsPath(m)).Settings.StatEdits.Count);
    }

    [Fact]
    public void Revert_everything_without_a_saved_original_uses_the_oldest_backups_and_says_so()
    {
        using var m = Mod();                          // a copy made before E3: no rweditor-baseline.bin
        var origB = m.Bytes("units/b.ini");
        var origBase = m.Bytes("templates/base.ini");
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130"), P("unitA", "units/a.ini", "core.price.rust", "3", mode: "template") });
        StatBatch.Apply(m.Root, Opt(m));
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "140") });
        StatBatch.Apply(m.Root, Opt(m));
        m.Put("units/tank.ini", m.Read("units/tank.ini").Replace("maxHp: 500", "maxHp: 501"));   // hand edit: NOT covered without a saved original
        var plan = StatRevert.Plan(m.Root, new RevertRequest(RevertScope.All), Opt(m));
        Assert.Contains("oldest backup", plan.Method);
        Assert.Contains(plan.Notes, n => n.Contains("older editor version"));
        StatRevert.Apply(m.Root, new RevertRequest(RevertScope.All), Opt(m));
        Assert.Equal(origB, m.Bytes("units/b.ini"));
        Assert.Equal(origBase, m.Bytes("templates/base.ini"));
        Assert.Contains("maxHp: 501", m.Read("units/tank.ini"));
        Assert.Empty(SettingsJson.Load(SettingsPath(m)).Settings.StatEdits);
    }

    [Fact]
    public void A_damaged_saved_original_is_refused_in_plain_words_and_revert_falls_back_to_the_backups()
    {
        using var m = Mod();
        CopyBaseline.Write(m.Root, EditorSupport.Load(m.Root).LockedGlobs, "0123456789abcdef0123456789abcdef", DateTime.UtcNow);
        Assert.Empty(CopyBaseline.Load(m.Root)!.Differences(m.Root));
        File.WriteAllText(CopyBaseline.PathOf(m.Root), "damaged");
        Assert.Equal("BASELINE_INVALID", Assert.Throws<EditorRefusalException>(() => CopyBaseline.Load(m.Root)).Code);
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130") });
        var origB = File.ReadAllBytes(Path.Combine(m.Root, "units", "b.ini"));
        StatBatch.Apply(m.Root, Opt(m));
        var plan = StatRevert.Plan(m.Root, new RevertRequest(RevertScope.All), Opt(m));
        Assert.Contains(plan.Notes, n => n.Contains("cannot be read"));
        StatRevert.Apply(m.Root, new RevertRequest(RevertScope.All), Opt(m));
        Assert.Equal(origB, m.Bytes("units/b.ini"));
    }

    [Fact]
    public void The_saved_original_holds_only_stat_writable_files_and_its_texts_give_the_shipped_values()
    {
        using var m = Mod();
        var globs = EditorSupport.Load(m.Root).LockedGlobs;
        CopyBaseline.Write(m.Root, globs, "0123456789abcdef0123456789abcdef", DateTime.UtcNow);
        var bl = CopyBaseline.Load(m.Root)!;
        Assert.All(bl.Files.Keys, k => Assert.True(LockPolicy.IsStatWritableFile(k, globs)));
        Assert.DoesNotContain(bl.Files.Keys, k => k.StartsWith("editorGenerated/", StringComparison.Ordinal) || k.StartsWith("myAssets/", StringComparison.Ordinal));
        StatStaging.SaveStaged(m.Root, new[] { P("unitB", "units/b.ini", "core.maxHp", "130") });
        StatBatch.Apply(m.Root, Opt(m));
        var model = UnitModel.Load(m.Root);
        var changed = bl.ChangedTexts(model.Snapshot);
        Assert.Equal(new[] { "units/b.ini" }, changed.Keys);
        var orig = model.WithFiles(changed);
        var e = StatDictionary.Embedded.ById("core.maxHp")!;
        Assert.Equal(100, StatReader.Read(orig.Merge(orig.Find("unitB")!), e, "core").Number);    // shipped value (from the template)
        Assert.Equal(130, StatReader.Read(model.Merge(model.Find("unitB")!), e, "core").Number);
    }
}
