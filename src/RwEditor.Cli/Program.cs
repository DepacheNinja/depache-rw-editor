using System.Text;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Generation;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;
using RwEditor.Core.Validation;

// rwedit - command line front end of the Depache RW Editor core (phase E1b-1: spawn groups + start-option hiding, no UI).
// Exit codes: 0 = done / all checks pass; 1 = refused, invalid settings or a failed check; 2 = usage error; 3 = unexpected error.
Console.OutputEncoding = new UTF8Encoding(false);
try { return Cli.Run(args); }
catch (EditorRefusalException e) { Console.Error.WriteLine($"REFUSED ({e.Code}): {e.Message}"); return 1; }
catch (ApplyFailedException e) { Console.Error.WriteLine("APPLY FAILED: " + e.Message); return 1; }
catch (UsageException e) { Console.Error.WriteLine("usage error: " + e.Message); Console.Error.WriteLine(); Console.Error.WriteLine(Cli.Help); return 2; }
catch (Exception e) { Console.Error.WriteLine("UNEXPECTED ERROR: " + e); return 3; }

sealed class UsageException(string message) : Exception(message);

static class Cli
{
    public const string Help = """
        rwedit - Depache RW Editor core, command line (E1b-2)

          rwedit gen-groups      --settings <x.json> --mod <folder> [--out <folder>] [--allow-git-tree]
                Validate the settings against the mod and print the two generated include files
                (or write anchorGroups.ini / selectorGroups.ini into --out). Never writes into the mod.
          rwedit apply           --settings <x.json> --mod <folder> [--backup-dir <d>] [--allow-git-tree] [--dry-run]
                Guard, verify locked files, validate, generate, check (include rules + lint), back up, write once.
          rwedit menu-apply      --mod <copy> --settings <desired.json> [--dry-run] [--backup-dir <d>] [--allow-git-tree]
                Apply groups, hidden / deleted options AND option text / numbers (what the editor's Start units and
                Menu options pages do): the desired settings are merged into the copy's own rweditor-settings.json,
                the generated files and the whitelisted lines of the protected option files are written once after
                one backup, and History / undo puts everything back.
          rwedit undo            --mod <folder> [--backup <zip>] [--list] [--backup-dir <d>] [--allow-git-tree]
                Restore the generated files from the newest apply backup (or the given one).
          rwedit revert-defaults --mod <folder> [--settings <x.json>] [--backup-dir <d>] [--allow-git-tree]
                Generated files back to the shipped behaviour (no groups, nothing hidden), with a backup.
                With --settings, that settings file is also reset to the defaults (one write).
          rwedit lint-includes   --mod <folder>
                Read-only: locked-file hashes vs editor-support.json, the include rules (port of
                check-start-spawner / check-option-sections), and lint of the generated files.
          rwedit settings-normalize --settings <x.json> --mod <folder> [--out <y.json>]
                Rewrite a settings file in canonical form with baseMod stamped from the mod (one write).

        Stats and units (E1b-2):
          rwedit units           --mod <folder> [--mod-group main|fe|moo|vte|depache] [--find text]
                Read-only: every loadable unit (file with [core] name, no dont_load: true) with its mod area.
          rwedit stats           --mod <folder> --unit <name> [--all]
                Read-only: the unit's whitelisted stats, resolved through copyFrom / @copyFromSection,
                each with its origin (file:line, own file or template) and how many units share it.
                --all also lists every other resolved key (not editable).
          rwedit set-stat        --mod <folder> --unit <name> --key <id> --value <v> [--section <s>]
                                 [--template] [--dry-run] --settings <x.json> [--backup-dir <d>] [--allow-git-tree]
                Change one stat. Default: this unit only (patch its own line, or add the key to its own
                section). --template patches the shared line the value comes from, after listing every
                unit it changes. Values: numbers; time keys frames or Ns; build time Ns. The edit is
                recorded in --settings (needed for revert-stats; not needed with --dry-run).
          rwedit revert-stats    --mod <folder> --settings <x.json> [--unit <name>] [--key <id>]
                                 [--mod-group <area>] [--dry-run] [--backup-dir <d>] [--allow-git-tree]
                Revert to defaults: the selected stat edits (all when no filter) back to the shipped text.
          rwedit thumbs          --mod <folder> --out <folder> [--game <RW folder>] [--size 64] [--no-cache]
                Unit thumbnails (frame 0 of [graphics] image, else the minimap icon, else a silhouette),
                built in RAM and written once as <out>\thumbs.zip; also cached under
                %LOCALAPPDATA%\DepacheRwEditor\thumbs\<copyId>.zip.
          rwedit dict-check      [--dict <stat-dictionary.json>] [--mod <folder>]
                Validate the stat dictionary; with --mod also report the shipped range of every entry and any
                shipped value outside a hard limit.

        Copies (E2):
          rwedit make-copy       --from <installed mod folder> --to <new or empty folder> [--name <copy name>]
                Copy the installed mod into a folder you control and mark it as an editor copy
                (rweditor-copy.json). Every command that writes needs such a copy. Never overwrites.

        Unit families (E2b, read-only):
          rwedit links           --mod <folder> [--unit <name>] [--kind convertTo|upgradedFrom|copyFromUnit|canBuild|produce|attachment|spawn]
                                 [--missing] [--unresolved]
                Every unit-to-unit link read from the merged units (with ${...} resolved), counted per kind.
          rwedit families        --mod <folder> [--unit <name>] [--detail] [--top N] [--without upgradedFrom,copyFrom,sprite]
                The browser's grouping: complete units at the top, their variants and parts inside, the shared /
                helper part lists; prints how many entries each rule covers. --without switches a rule off (diagnostics).

        Staged editing (E3; the same Core code as the app; the copy's own rweditor-settings.json holds the pending list):
          rwedit stage           --mod <copy> --unit <name> --key <id> --value <v> [--section <s>] [--template] [--family]
                Check one change with the full plan (nothing written to the mod) and add it to the pending list.
                Shared values need --template (every unit that shares it; the units are listed) or are staged
                for this unit only. --family also stages it on the upgrade levels / versions with the same value
                (each one checked; refused unless all pass). Values in the line's own form (frames / Ns / numbers).
          rwedit stage-many      --mod <copy> --key <id> --value <v> [--who buildings|units|all] [--area <mod area id>] [--have any|set|unset] [--with-copies] [--dry-run]
                Set one value (a [core] / [movement] / [attack] stat, e.g. core.generation_resources.credits) for every complete
                unit / building that matches, as pending changes (each checked like a normal edit; locked units, attached parts and
                units whose file other units copy are skipped with the reason). --have set = only those that already have a value,
                unset = only those without one. --with-copies also changes the units that copy a chosen unit's file
                (its upgrade levels / versions).
          rwedit staged          --mod <copy>          List the pending changes and check them together.
          rwedit unstage         --mod <copy> [--unit <name>] [--key <id>]   Remove pending changes (all when no filter).
          rwedit apply-staged    --mod <copy> [--backup-dir <d>]   Apply every pending change in one go (backup, one write).
          rwedit history         --mod <copy> [--backup-dir <d>]   The copy's changes, newest first (undone ones marked).
          rwedit analyze-log     --log <file>[;<file>...] [--mod <copy>] [--since <yyyy-MM-dd>] [--all] [--baseline <file>]
                Read game logs (lastrun.log, a Test load's output, crashes.txt, hs_err_pid*.log) and say in plain words what
                is wrong and whose edit it is. With --mod the copy's own changes are used to name the cause. Exit 2 = a problem.
          rwedit test-load       --mod <copy in the game's mods\units folder> [--game <Rusted Warfare folder>] [--timeout 240] [--backup-dir <d>]
                Start the game once with your switched-on mods, read its log until it finishes loading or reports an
                error, close it gently, and say what is wrong and whose edit it is. The game must not be running and
                the copy must be switched on in the game's Mods menu. preferences.ini is put back exactly afterwards.
          rwedit log-baseline    --log <clean load log of the unedited mod> --out <file>
                Write the list of notes the shipped mod normally produces (embedded in the editor as data).
          rwedit undo-last       --mod <copy> [--backup-dir <d>]   Undo the newest change not undone yet (files + settings).
          rwedit revert          --mod <copy> --scope stat|unit|area|all [--unit <name>] [--key <id>] [--section <s>]
                                 [--mod-group <area>] [--dry-run] [--backup-dir <d>]
                Put applied changes back; scope all = every value file back to the copy's saved original values
                (copies made before E3: from the oldest backups).

        Backups: %LOCALAPPDATA%\DepacheRwEditor\backups\<copyId>\ unless --backup-dir is given.
        Refused targets: Steam Workshop content folders, *_pristine / baseline_* folders, folders without
        editor-support.json, folders inside a git work tree (unless --allow-git-tree), and folders without
        the editor-copy marker rweditor-copy.json (made by make-copy or the app's "Make my copy").
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Help); return args.Length == 0 ? 2 : 0; }
        var o = Opts.Parse(args.Skip(1).ToArray());
        return args[0] switch
        {
            "gen-groups" => GenGroups(o),
            "apply" => Apply(o),
            "menu-apply" => MenuApply(o),
            "undo" => Undo(o),
            "revert-defaults" => Revert(o),
            "lint-includes" => LintIncludes(o),
            "settings-normalize" => Normalize(o),
            "units" => StatCli.Units(o),
            "stats" => StatCli.Stats(o),
            "set-stat" => StatCli.SetStat(o),
            "revert-stats" => StatCli.RevertStats(o),
            "thumbs" => StatCli.Thumbs(o),
            "dict-check" => StatCli.DictCheck(o),
            "make-copy" => MakeCopy(o),
            "links" => FamilyCli.Links(o),
            "families" => FamilyCli.Families(o),
            "stage" => EditCli.Stage(o),
            "staged" => EditCli.Staged(o),
            "stage-many" => EditCli.StageMany(o),
            "unstage" => EditCli.Unstage(o),
            "apply-staged" => EditCli.ApplyStaged(o),
            "history" => EditCli.History(o),
            "undo-last" => EditCli.UndoLast(o),
            "analyze-log" => LogCli.Analyze(o),
            "log-baseline" => LogCli.Baseline(o),
            "test-load" => LogCli.TestLoad(o),
            "revert" => EditCli.Revert(o),
            _ => throw new UsageException($"unknown command '{args[0]}'"),
        };
    }

    static int MakeCopy(Opts o)
    {
        o.Only("from", "to", "name");
        string version = typeof(RwEditor.Core.Setup.CopyMaker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var r = RwEditor.Core.Setup.CopyMaker.MakeCopy(o.Req("from"), o.Req("to"), o.Get("name") ?? RwEditor.Core.Setup.CopyMaker.DefaultName, version);
        Console.WriteLine($"copied {r.Files} files ({r.Bytes} bytes) in {r.Duration.TotalSeconds:0.0} s to {r.Folder}");
        if (r.NewTitle != null) Console.WriteLine($"mod title in the copy: {r.NewTitle}");
        Console.WriteLine($"marker: {Path.Combine(r.Folder, EditorCopyMarker.FileName)} (copyId {r.Marker.CopyId})");
        Console.WriteLine("COPY MADE");
        return 0;
    }

    static EditorSettings LoadSettings(string path)
    {
        var r = SettingsJson.Load(path);
        if (r.Hash == HashStatus.Mismatch)
            Console.WriteLine($"WARN  settings contentHash {r.StoredHash} does not match the content ({r.ComputedHash}): the file was edited after it was saved (run settings-normalize to re-stamp)");
        return r.Settings;
    }

    static void PrintFindings(IEnumerable<Finding> f) { foreach (var x in f) Console.WriteLine(x); }

    static int GenGroups(Opts o)
    {
        o.Only("settings", "mod", "out", "allow-git-tree");
        var settings = LoadSettings(o.Req("settings"));
        var ctx = ModContext.Load(o.Req("mod"));
        var findings = SettingsValidator.Validate(settings, ctx);
        PrintFindings(findings);
        if (findings.Any(f => f.Severity == Severity.Error)) { Console.WriteLine("NOT GENERATED: the settings are invalid"); return 1; }
        var gen = SpawnGroupGenerator.Generate(settings, ctx);
        var rules = IncludeRules.Check(ctx.Snapshot.WithFiles(gen.ByRelPath));
        if (!rules.AllPass) { foreach (var r in rules.Failures) Console.WriteLine(r); Console.WriteLine("NOT GENERATED: include rules fail"); return 1; }
        if (o.Get("out") is string outDir)
        {
            string dir = FolderGuard.Check(outDir, o.Flag("allow-git-tree"), requireEditorSupport: false, requireCopyMarker: false);
            if (string.Equals(dir, ctx.Root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(ctx.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new EditorRefusalException("OUT_INSIDE_MOD", "--out may not be inside the mod folder; use 'rwedit apply' to change a mod (it backs up and checks first)");
            File.WriteAllBytes(Path.Combine(dir, "anchorGroups.ini"), GeneratedFiles.Encoding.GetBytes(gen.AnchorGroups));
            File.WriteAllBytes(Path.Combine(dir, "selectorGroups.ini"), GeneratedFiles.Encoding.GetBytes(gen.SelectorGroups));
            Console.WriteLine($"wrote {Path.Combine(dir, "anchorGroups.ini")} ({gen.AnchorGroups.Length} chars) and {Path.Combine(dir, "selectorGroups.ini")} ({gen.SelectorGroups.Length} chars)");
        }
        else
        {
            Console.WriteLine("===== " + EditorSupport.AnchorGenerated);
            Console.Write(gen.AnchorGroups);
            Console.WriteLine("===== " + EditorSupport.SelectorGenerated);
            Console.Write(gen.SelectorGroups);
        }
        Console.WriteLine($"include rules: {rules.Summary}");
        return 0;
    }

    static ApplyOptions Options(Opts o) => new() { BackupRoot = o.Get("backup-dir"), AllowGitTree = o.Flag("allow-git-tree") };

    static void Report(ApplyResult r)
    {
        PrintFindings(r.Plan.Warnings);
        Console.WriteLine($"locked files: {r.Plan.Locks.Describe()}");
        Console.WriteLine($"include rules: {r.Plan.Rules.Summary}");
        Console.WriteLine($"lint: {r.Plan.NewLintFindings.Count} new finding(s) from the generated files");
        if (!r.Written) { Console.WriteLine("no change: the generated files already hold exactly these bytes (nothing written, no backup)"); return; }
        Console.WriteLine($"backup: {r.BackupPath}");
        foreach (var f in r.WrittenFiles) Console.WriteLine($"written: {f} ({r.Plan.NewBytes[f].Length} bytes)");
        Console.WriteLine($"settings contentHash: {SettingsJson.ContentHash(r.Plan.Settings)}");
    }

    static int Apply(Opts o)
    {
        o.Only("settings", "mod", "backup-dir", "allow-git-tree", "dry-run");
        var settings = LoadSettings(o.Req("settings"));
        if (o.Flag("dry-run"))
        {
            var p = ApplyPipeline.Plan(settings, o.Req("mod"), Options(o));
            PrintFindings(p.Warnings);
            Console.WriteLine($"locked files: {p.Locks.Describe()}");
            Console.WriteLine($"include rules: {p.Rules.Summary}");
            Console.WriteLine($"lint: {p.NewLintFindings.Count} new finding(s)");
            foreach (var kv in p.NewBytes)
                Console.WriteLine($"would write {kv.Key}: {kv.Value.Length} bytes ({(p.CurrentBytes[kv.Key] is byte[] c && c.AsSpan().SequenceEqual(kv.Value) ? "unchanged" : "changed")})");
            Console.WriteLine("DRY RUN: nothing written");
            return 0;
        }
        var r = ApplyPipeline.Apply(settings, o.Req("mod"), Options(o));
        Report(r);
        Console.WriteLine("APPLIED");
        return 0;
    }

    static int MenuApply(Opts o)
    {
        o.Only("settings", "mod", "backup-dir", "allow-git-tree", "dry-run");
        var desired = LoadSettings(o.Req("settings"));
        if (o.Flag("dry-run"))
        {
            var p = RwEditor.Core.Apply.GroupsPipeline.Plan(o.Req("mod"), desired, Options(o));
            PrintFindings(p.Warnings);
            Console.WriteLine($"locked files: {p.Locks.Describe()}");
            Console.WriteLine($"include rules: {p.Rules.Summary}");
            Console.WriteLine($"lint: {p.NewLintFindings.Count} new finding(s)");
            foreach (var kv in p.NewBytes)
                Console.WriteLine($"would write {kv.Key}: {kv.Value.Length} bytes ({(p.CurrentBytes[kv.Key] is byte[] c && c.AsSpan().SequenceEqual(kv.Value) ? "unchanged" : "changed")})");
            foreach (var e in p.Options?.Edits ?? Array.Empty<OptionEdit>()) Console.WriteLine($"line {e.File}:{e.Line} [{e.Section}] {e.Key}: {e.OldLine}  ->  {e.NewLine}");
            Console.WriteLine("DRY RUN: nothing written");
            return 0;
        }
        var r = RwEditor.Core.Apply.GroupsPipeline.Apply(o.Req("mod"), desired, Options(o));
        Console.WriteLine(r.Summary);
        if (!r.Written) { Console.WriteLine("no change: the copy already holds exactly this"); return 0; }
        Console.WriteLine($"backup: {r.BackupPath}");
        foreach (var f in r.WrittenFiles) Console.WriteLine($"written: {f}");
        Console.WriteLine("APPLIED");
        return 0;
    }

    static int Revert(Opts o)
    {
        o.Only("mod", "settings", "backup-dir", "allow-git-tree");
        var r = ApplyPipeline.RevertDefaults(o.Req("mod"), Options(o));
        Report(r);
        if (o.Get("settings") is string sp)
        {
            EditorSettings s;
            if (File.Exists(sp)) { s = SettingsJson.Load(sp).Settings; s.RevertAll(); }
            else s = EditorSettings.Default();
            File.WriteAllBytes(sp, SettingsJson.SerializeBytes(s));
            Console.WriteLine($"settings reset to defaults: {sp}");
        }
        Console.WriteLine("REVERTED TO DEFAULTS");
        return 0;
    }

    static int Undo(Opts o)
    {
        o.Only("mod", "backup", "list", "backup-dir", "allow-git-tree", "settings");
        var opts = Options(o);
        string root = FolderGuard.Check(o.Req("mod"), opts.AllowGitTree);
        if (o.Flag("list"))
        {
            foreach (var p in BackupStore.List(opts.EffectiveBackupRoot, root))
            {
                var b = BackupStore.Read(p);
                Console.WriteLine($"{p}  kind={b.Kind}  created={b.CreatedUtc:yyyy-MM-dd HH:mm:ss}Z  files={b.Files.Count}");
            }
            return 0;
        }
        var r = ApplyPipeline.Undo(root, opts, o.Get("backup"));
        Console.WriteLine($"restored from: {r.RestoredFrom}");
        Console.WriteLine($"safety backup of the state before the undo: {r.SafetyBackupPath}");
        foreach (var f in r.RestoredFiles) Console.WriteLine($"restored: {f}");
        if (r.SettingsPath != null)
        {
            // a stat edit / revert also changed a settings file: put it back too, but only the file named on this command line
            if (o.Get("settings") is string sp && string.Equals(Path.GetFullPath(sp), r.SettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                if (r.SettingsBytes != null) File.WriteAllBytes(r.SettingsPath, r.SettingsBytes);
                else RwEditor.Core.Stats.StatPipeline.WriteSettings(r.SettingsPath, EditorSettings.Default());
                Console.WriteLine($"settings restored: {r.SettingsPath}");
            }
            else Console.WriteLine($"WARN  this change also updated {r.SettingsPath}; repeat with --settings \"{r.SettingsPath}\" to put it back as well (it still lists the undone edit)");
        }
        Console.WriteLine($"include rules after undo: {r.RulesAfter.Summary}");
        Console.WriteLine("UNDONE");
        return r.RulesAfter.AllPass ? 0 : 1;
    }

    static int LintIncludes(Opts o)
    {
        o.Only("mod");
        var ctx = ModContext.Load(o.Req("mod"));
        var locks = ctx.Support.VerifyLockedFiles(ctx.Root);
        Console.WriteLine($"{(locks.Ok ? "PASS" : "FAIL")}  locked files: {locks.Describe()}");
        var rules = IncludeRules.Check(ctx.Snapshot);
        foreach (var r in rules.Results) Console.WriteLine(r);
        var empty = SpawnGroupGenerator.GenerateDefaults(ctx);
        var lintNew = RwLint.NewFindings(ctx.Snapshot.WithFiles(empty.ByRelPath), ctx.Snapshot);
        foreach (var l in lintNew) Console.WriteLine("  lint: " + l);
        Console.WriteLine($"{(lintNew.Count == 0 ? "PASS" : "FAIL")}  lint: {lintNew.Count} finding(s) from the generated files");
        Console.WriteLine();
        bool ok = locks.Ok && rules.AllPass && lintNew.Count == 0;
        Console.WriteLine(ok ? $"ALL ASSERTIONS PASS ({rules.Passed + 2})" : $"{rules.Failed + (locks.Ok ? 0 : 1) + (lintNew.Count == 0 ? 0 : 1)} ASSERTION(S) FAILED");
        return ok ? 0 : 1;
    }

    static int Normalize(Opts o)
    {
        o.Only("settings", "mod", "out");
        string src = o.Req("settings");
        var s = SettingsJson.Load(src).Settings;
        var support = EditorSupport.Load(Path.GetFullPath(o.Req("mod")));
        s.BaseMod = new BaseModRef(support.ModVersion, support.Schema, support.LockedFileCount, support.LockedManifestSha256);
        string dst = o.Get("out") ?? src;
        File.WriteAllBytes(dst, SettingsJson.SerializeBytes(s));
        Console.WriteLine($"wrote {dst} (contentHash {SettingsJson.ContentHash(s)})");
        return 0;
    }
}

/// <summary>--name value / --flag options.</summary>
sealed class Opts
{
    static readonly HashSet<string> Flags = new() { "allow-git-tree", "dry-run", "list", "template", "all", "no-cache", "unresolved", "missing", "detail", "with-copies" };
    readonly Dictionary<string, string?> _d = new(StringComparer.Ordinal);

    public static Opts Parse(string[] a)
    {
        var o = new Opts();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"unexpected argument '{a[i]}'");
            string k = a[i].Substring(2);
            if (o._d.ContainsKey(k)) throw new UsageException($"--{k} given twice");
            if (Flags.Contains(k)) { o._d[k] = null; continue; }
            if (i + 1 >= a.Length || a[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"--{k} needs a value");
            o._d[k] = a[++i];
        }
        return o;
    }

    public void Only(params string[] allowed)
    {
        foreach (var k in _d.Keys) if (!allowed.Contains(k)) throw new UsageException($"--{k} is not an option of this command");
    }

    public string Req(string k) => _d.TryGetValue(k, out var v) && v != null ? v : throw new UsageException($"--{k} is required");
    public string? Get(string k) => _d.TryGetValue(k, out var v) ? v : null;
    public bool Flag(string k) => _d.ContainsKey(k);
}
