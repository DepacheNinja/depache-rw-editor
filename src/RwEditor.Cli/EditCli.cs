using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Model;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Stats;

/// <summary>E3 commands: the app's staged-editing flow on the command line (same Core classes: StatStaging, StatBatch, History, StatRevert).</summary>
static class EditCli
{
    static ApplyOptions Options(Opts o) => new() { BackupRoot = o.Get("backup-dir") };

    static void Print(IEnumerable<string> lines) { foreach (var l in lines) Console.WriteLine("  " + l); }

    public static int Stage(Opts o)
    {
        o.Only("mod", "unit", "key", "value", "section", "template", "family");
        string root = FolderGuard.Check(o.Req("mod"), allowGitTree: false);
        var (_, _, model) = StatPipeline.Open(root, new ApplyOptions());
        var dict = StatDictionary.Embedded;
        var named = model.UnitsNamed(o.Req("unit"));
        if (named.Count != 1) throw new EditorRefusalException("UNIT_UNKNOWN", named.Count == 0 ? $"no loadable unit '{o.Req("unit")}'" : $"'{o.Req("unit")}' is defined by {named.Count} files");
        var u = named[0];
        var entry = dict.Resolve(o.Req("key"), out var err) ?? throw new EditorRefusalException("NOT_IN_DICTIONARY", err!);
        string section = model.Merge(u).Section(o.Get("section") ?? entry.FixedSection ?? "")?.Name ?? o.Get("section") ?? entry.FixedSection
            ?? throw new UsageException($"{entry.Id} needs --section");
        var families = o.Flag("family") ? UnitFamilies.Build(model) : null;
        var info = StatStaging.Info(model, dict, families, u, entry, section);
        Console.WriteLine($"{u.Name} {StatText.StatName(entry, section)}: now {(info.Current.IsSet ? info.Current.Raw!.Value.Trim() : "not set")}" +
                          (info.Origin != null ? $" from {info.Origin}" : ""));
        if (info.Shared) Console.WriteLine($"shared value: {info.SharingUnits.Count} units read this line: {string.Join(", ", info.SharingUnits.Select(x => x.Name))}");
        var change = new PendingStatEdit { Unit = u.Name, UnitFile = u.RelPath, StatId = entry.Id, Section = section, Mode = o.Flag("template") ? "template" : "unit", Value = o.Req("value") };
        if (o.Flag("family") && info.FamilyCandidates.Count == 0) Console.WriteLine("family: no upgrade level / version with the same value" + (info.FamilySkipped.Count > 0 ? ": " + string.Join("; ", info.FamilySkipped.Select(s => $"{s.Unit.Name} ({s.Reason})")) : ""));
        var check = StatStaging.Check(model, dict, change, rel => File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))),
            o.Flag("family") && change.Mode == "unit" && info.FamilyCandidates.Count > 0 ? info.FamilyCandidates : null);
        foreach (var it in check.Items)
        {
            if (it.Plan is StatEditPlan p)
            {
                Console.WriteLine($"{p.Unit.Name}: {StatText.StatName(p.Entry, p.Section)} -> {StatText.Value(p.Entry, p.NewNumber)}; write {SettingsJson.WriteKindToString(p.Record.WriteKind)} {p.Record.File} [{p.Record.FileSection}]; affected units {p.Affected.Count}");
                if (p.Request.Mode == StatEditMode.Template) Print(p.Affected.Select(a => $"{a.Unit.Name,-28} {a.Unit.RelPath}  {a.Before ?? "(not set)"} -> {a.After}"));
            }
            foreach (var f in it.Findings) Console.WriteLine("  " + f);
        }
        if (!check.Ok) { Console.Error.WriteLine("REFUSED: nothing staged"); return 1; }
        var s = StatStaging.SaveStaged(root, check.Pendings);
        Console.WriteLine($"STAGED ({check.Pendings.Count}); {s.PendingStatEdits.Count} pending change(s) in {CopyFiles.SettingsPath(root)}");
        return 0;
    }

    /// <summary>rwedit stage-many: "Set for many units" on the command line (same Core class as the app's dialog).</summary>
    public static int StageMany(Opts o)
    {
        o.Only("mod", "key", "value", "who", "area", "have", "dry-run", "with-copies");
        string root = FolderGuard.Check(o.Req("mod"), allowGitTree: false);
        var (_, _, model) = StatPipeline.Open(root, new ApplyOptions());
        var dict = StatDictionary.Embedded;
        var entry = dict.Resolve(o.Req("key"), out var err) ?? throw new EditorRefusalException("NOT_IN_DICTIONARY", err!);
        var who = (o.Get("who") ?? "all") switch
        {
            "buildings" => BulkWho.Buildings, "units" => BulkWho.Units, "all" => BulkWho.UnitsAndBuildings,
            var w => throw new UsageException($"--who must be buildings, units or all (got '{w}')"),
        };
        var have = (o.Get("have") ?? "any") switch
        {
            "any" => BulkHave.Any, "set" => BulkHave.OnlyThoseThatHaveIt, "unset" => BulkHave.OnlyThoseWithout,
            var h => throw new UsageException($"--have must be any, set or unset (got '{h}')"),
        };
        var families = UnitFamilies.Build(model);
        var plan = StatBulk.Plan(model, dict, families, entry, o.Req("value"), new BulkFilter(who, o.Get("area"), have, o.Flag("with-copies")),
            rel => File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
        Console.WriteLine($"{StatText.StatName(entry, plan.Section)} = {o.Req("value")}: {plan.Considered} unit(s) match, {plan.Ok.Count} can be changed ({plan.AffectedUnits.Count} units change in all), {plan.Skipped.Count} skipped");
        foreach (var (reason, count) in plan.SkipSummary) Console.WriteLine($"  skipped {count}: {reason}");
        foreach (var bp in plan.BatchProblems) Console.WriteLine("  PROBLEM: " + bp);
        foreach (var p in plan.Ok.Take(15)) Console.WriteLine("  would change: " + p.Unit);
        if (plan.Ok.Count > 15) Console.WriteLine($"  ... and {plan.Ok.Count - 15} more");
        if (plan.BatchProblems.Count > 0) { Console.Error.WriteLine("REFUSED: the changes together would add problems; nothing staged"); return 1; }
        if (o.Flag("dry-run") || plan.Ok.Count == 0) { Console.WriteLine(o.Flag("dry-run") ? "DRY RUN: nothing staged" : "nothing to stage"); return 0; }
        var s = StatStaging.SaveStaged(root, plan.Ok);
        Console.WriteLine($"STAGED ({plan.Ok.Count}); {s.PendingStatEdits.Count} pending change(s) in {CopyFiles.SettingsPath(root)}");
        return 0;
    }

    public static int Staged(Opts o)
    {
        o.Only("mod");
        var plan = StatBatch.PlanCopy(o.Req("mod"));
        foreach (var it in plan.Items)
        {
            var p = it.Pending;
            Console.WriteLine($"{(it.Ok ? "OK     " : "PROBLEM")} {p.Unit} {StatText.StatName(p)} = {p.Value} ({p.Mode}{(p.FromFamilyOf != null ? ", from family of " + p.FromFamilyOf : "")})" +
                              (it.Plan != null ? $" -> {it.Plan.Record.File}, {it.Plan.Affected.Count} unit(s)" : ""));
            foreach (var f in it.Findings) Console.WriteLine("    " + f);
        }
        foreach (var f in plan.BatchFindings) Console.WriteLine("  " + f);
        Console.WriteLine($"{plan.Items.Count} pending; files that would change: {string.Join(", ", plan.NewBytes.Keys)}; lint new {plan.NewLintFindings.Count}; {(plan.CanApply ? "CAN APPLY" : "CANNOT APPLY")}");
        return plan.CanApply || plan.Items.Count == 0 ? 0 : 1;
    }

    public static int Unstage(Opts o)
    {
        o.Only("mod", "unit", "key");
        string? unit = o.Get("unit"), key = o.Get("key");
        var s = StatStaging.SaveStaged(o.Req("mod"), null, p => (unit == null || p.Unit == unit) && (key == null || p.StatId.Equals(key, StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine($"{s.PendingStatEdits.Count} pending change(s) left");
        return 0;
    }

    public static int ApplyStaged(Opts o)
    {
        o.Only("mod", "backup-dir");
        try
        {
            var r = StatBatch.Apply(o.Req("mod"), Options(o));
            foreach (var it in r.Plan.Items)
            {
                var p = it.Plan!;
                Console.WriteLine($"{p.Unit.Name}: {StatText.StatName(p.Entry, p.Section)} -> {StatText.Value(p.Entry, p.NewNumber)} ({p.Request.Mode}, {p.Affected.Count} unit(s): {string.Join(", ", p.Affected.Select(a => a.Unit.Name))})");
                foreach (var f in it.Warnings) Console.WriteLine("    " + f);
            }
            Console.WriteLine($"backup: {r.BackupPath}");
            foreach (var f in r.WrittenFiles) Console.WriteLine($"written: {f} ({r.Plan.NewBytes[f].Length} bytes)");
            Console.WriteLine($"lint new: {r.Plan.NewLintFindings.Count}; recorded changes: {r.Settings.StatEdits.Count}; pending: {r.Settings.PendingStatEdits.Count}");
            Console.WriteLine("APPLIED");
            return 0;
        }
        catch (StatBatchRefusedException e)
        {
            Print(e.Plan.Problems);
            Console.Error.WriteLine("REFUSED: nothing written");
            return 1;
        }
    }

    public static int History(Opts o)
    {
        o.Only("mod", "backup-dir");
        string root = FolderGuard.Check(o.Req("mod"), allowGitTree: false);
        var list = RwEditor.Core.Apply.History.List(Options(o).EffectiveBackupRoot, root);
        foreach (var h in list)
            Console.WriteLine($"{h.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {h.KindText,-34} {(h.Undone ? "[undone] " : "")}{h.Problem ?? h.Summary}");
        Console.WriteLine($"{list.Count} backup(s); next undo: {RwEditor.Core.Apply.History.NextUndo(Options(o).EffectiveBackupRoot, root)?.Path ?? "none"}");
        return 0;
    }

    public static int UndoLast(Opts o)
    {
        o.Only("mod", "backup-dir");
        string root = FolderGuard.Check(o.Req("mod"), allowGitTree: false);
        var u = ApplyPipeline.Undo(root, Options(o), null, CopyFiles.SettingsPath(root));
        Console.WriteLine($"restored from: {u.RestoredFrom} ({u.Kind})");
        foreach (var f in u.RestoredFiles) Console.WriteLine($"restored: {f}");
        Console.WriteLine($"safety backup: {u.SafetyBackupPath}");
        Console.WriteLine("UNDONE");
        return 0;
    }

    public static int Revert(Opts o)
    {
        o.Only("mod", "scope", "unit", "key", "section", "mod-group", "dry-run", "backup-dir");
        string root = FolderGuard.Check(o.Req("mod"), allowGitTree: false);
        var scope = o.Req("scope") switch
        {
            "stat" => RevertScope.Stat, "unit" => RevertScope.Unit, "area" => RevertScope.Area, "all" => RevertScope.All,
            var x => throw new UsageException($"--scope must be stat, unit, area or all (not '{x}')"),
        };
        string? unit = o.Get("unit"), unitFile = null, statId = null, section = o.Get("section");
        if (scope is RevertScope.Stat or RevertScope.Unit)
        {
            if (unit == null) throw new UsageException("--unit is required for scope stat / unit");
            var model = UnitModel.Load(root);
            var u = model.Find(unit) ?? throw new EditorRefusalException("UNIT_UNKNOWN", $"no loadable unit '{unit}'");
            unitFile = u.RelPath;
            if (scope == RevertScope.Stat)
            {
                var e = StatDictionary.Embedded.Resolve(o.Req("key"), out var err) ?? throw new EditorRefusalException("NOT_IN_DICTIONARY", err!);
                statId = e.Id;
                section ??= e.FixedSection ?? throw new UsageException($"{e.Id} needs --section");
            }
        }
        if (scope == RevertScope.Area && o.Get("mod-group") == null) throw new UsageException("--mod-group is required for scope area");
        var req = new RevertRequest(scope, unit, unitFile, statId, section, o.Get("mod-group"));
        var plan = StatRevert.Plan(root, req, Options(o));
        Console.WriteLine($"how: {plan.Method}");
        Print(plan.Lines);
        Print(plan.Notes.Select(n => "note: " + n));
        if (!plan.Changes) { Console.WriteLine("nothing to put back"); return 0; }
        if (o.Flag("dry-run")) { Console.WriteLine("DRY RUN: nothing written"); return 0; }
        var r = StatRevert.Apply(root, req, Options(o))!;
        Console.WriteLine($"backup: {r.BackupPath}");
        foreach (var f in r.WrittenFiles) Console.WriteLine($"written: {f}");
        Console.WriteLine("REVERTED");
        return 0;
    }
}
