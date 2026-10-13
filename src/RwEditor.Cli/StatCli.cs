using System.Globalization;
using RwEditor.Core;
using RwEditor.Core.Apply;
using RwEditor.Core.Imaging;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;

/// <summary>The E1b-2 commands: units, stats, set-stat, revert-stats, thumbs, dict-check.</summary>
static class StatCli
{
    static ApplyOptions Options(Opts o) => new() { BackupRoot = o.Get("backup-dir"), AllowGitTree = o.Flag("allow-git-tree") };

    static void Print(IEnumerable<Finding> f) { foreach (var x in f) Console.WriteLine(x); }

    // ------------------------------------------------------------------------------------------------------------------ units
    public static int Units(Opts o)
    {
        o.Only("mod", "mod-group", "find");
        var model = UnitModel.Load(o.Req("mod"));
        string? group = o.Get("mod-group"), find = o.Get("find");
        if (group != null && !model.Areas.IsKnown(group)) throw new UsageException($"--mod-group must be one of {string.Join(", ", model.Areas.Areas.Select(a => a.Id))}");
        var list = model.Units.Where(u => (group == null || u.Area == group)
            && (find == null || u.Name.Contains(find, StringComparison.OrdinalIgnoreCase) || u.RelPath.Contains(find, StringComparison.OrdinalIgnoreCase)
                || (DisplayText(model, u)?.Contains(find, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        foreach (var u in list)
            Console.WriteLine($"{u.Name,-28} {u.Area,-8} {(model.IsBuilding(u) ? "building" : "unit"),-8} {(u.Locked ? "LOCKED " : "       ")} {u.RelPath}  {DisplayText(model, u)}");
        Console.WriteLine();
        Console.WriteLine($"loadable units: {model.Units.Count} ({string.Join(", ", model.Areas.Areas.Select(a => $"{a.Id} {model.Units.Count(u => u.Area == a.Id)}"))}); locked {model.Units.Count(u => u.Locked)}; listed {list.Count}");
        var dupNames = model.Units.GroupBy(u => u.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        if (dupNames.Count > 0) Console.WriteLine($"note: {dupNames.Count} unit name(s) are defined by more than one loadable file (the editor refuses to edit those): {string.Join(", ", dupNames.Take(15).Select(g => g.Key))}{(dupNames.Count > 15 ? ", ..." : "")}");
        return 0;
    }

    static string? DisplayText(UnitModel model, UnitType u)
    {
        var v = model.Merge(u).Get("core", "displayText")?.Value.Trim();
        return v == null || v.Contains("${", StringComparison.Ordinal) ? null : v;
    }

    // ------------------------------------------------------------------------------------------------------------------ stats
    public static int Stats(Opts o)
    {
        o.Only("mod", "unit", "all");
        var model = UnitModel.Load(o.Req("mod"));
        var dict = StatDictionary.Embedded;
        string name = o.Req("unit");
        var named = model.UnitsNamed(name);
        if (named.Count == 0)
        {
            var near = model.FindIgnoreCase(name);
            throw new EditorRefusalException("UNIT_UNKNOWN", near != null ? $"no loadable unit '{name}'; did you mean '{near.Name}'?" : $"no loadable unit named '{name}'");
        }
        foreach (var u in named)
        {
            var m = model.Merge(u);
            Console.WriteLine($"{u.Name}  ({u.RelPath}, area {u.Area}{(u.Locked ? ", LOCKED: not editable" : "")}, {(model.IsBuilding(u) ? "building" : "unit")})");
            Console.WriteLine($"  chain: {string.Join(" -> ", m.ChainFiles)}");
            foreach (var p in m.UnresolvedParents) Console.WriteLine($"  unresolved: {p}");
            foreach (var p in m.Problems) Console.WriteLine($"  problem: {p}");
            var shown = new HashSet<(string, string)>();
            foreach (var e in dict.Entries)
            {
                var sections = StatReader.SectionsFor(m, e).ToList();
                if (sections.Count == 0 && e.FixedSection != null && e.Tier == StatTier.Simple) sections.Add(e.FixedSection);   // show "not set" for the basics
                foreach (var sec in sections)
                {
                    var r = StatReader.Read(m, e, sec);
                    if (!r.IsSet && e.Tier == StatTier.Expand) continue;
                    shown.Add((sec.ToLowerInvariant(), e.Key.ToLowerInvariant()));
                    string val = !r.IsSet ? "(not set: game default)" : r.Editable ? $"{r.Text}{Unit(e, r.Number)}" : r.Raw!.Value;
                    string origin = r.Raw == null ? "" : $"  <- {r.Raw.Origin}{Shared(model, u, r)}";
                    string ro = r.Editable ? (u.Locked ? "  [locked unit]" : "") : $"  [read-only: {r.ReadOnlyReason}]";
                    Console.WriteLine($"  {e.Tier.ToString().ToLowerInvariant(),-6} {e.Id,-30} [{sec}] = {val}{origin}{ro}");
                }
            }
            if (o.Flag("all"))
            {
                Console.WriteLine("  -- every other resolved key (not editable):");
                foreach (var s in m.Sections)
                    foreach (var v in s.Values)
                        if (!shown.Contains((s.Name.ToLowerInvariant(), v.Key.ToLowerInvariant())))
                            Console.WriteLine($"     [{s.Name}] {v.Key}: {v.Value}  <- {v.Origin}");
            }
        }
        return 0;
    }

    static string Unit(StatEntry e, double? n) => n is not double v ? "" : e.Type switch
    {
        StatType.Time => $"  ({StatValue.Format(Math.Round(v / 60.0, 3))} s)",
        StatType.BuildTime => $"  ({StatValue.Format(Math.Round(v, 3))} s)",
        _ => "",
    };

    /// <summary>For a value that comes from a shared line: how many loadable units read their value from that same line.</summary>
    static string Shared(UnitModel model, UnitType u, StatRef r)
    {
        if (r.Raw == null) return "";
        var origin = r.Raw.Origin;
        int n = 0;
        foreach (var x in model.UnitsUsingFile(origin.File))
        {
            var m = model.Merge(x);
            if (m.Sections.Any(s => s.Get(r.Entry.Key) is MergedValue v && v.Origin.File == origin.File && v.Origin.Line == origin.Line)) n++;
        }
        return n > 1 ? $"  [shared by {n} units]" : "";
    }

    // ------------------------------------------------------------------------------------------------------------------ set-stat
    public static int SetStat(Opts o)
    {
        o.Only("mod", "unit", "key", "value", "section", "template", "dry-run", "settings", "backup-dir", "allow-git-tree");
        var req = new StatEditRequest(o.Req("unit"), o.Req("key"), o.Req("value"), o.Flag("template") ? StatEditMode.Template : StatEditMode.UnitOnly, o.Get("section"));
        try
        {
            if (o.Flag("dry-run"))
            {
                var p = StatPipeline.Plan(o.Req("mod"), req, Options(o));
                Report(p);
                Console.WriteLine("DRY RUN: nothing written");
                return 0;
            }
            var r = StatPipeline.Apply(o.Req("mod"), req, o.Req("settings"), Options(o));
            Report(r.Plan);
            Console.WriteLine($"backup: {r.BackupPath}");
            foreach (var f in r.WrittenFiles) Console.WriteLine($"written: {f} ({r.Plan.NewBytes[f].Length} bytes)");
            Console.WriteLine($"settings: {r.SettingsPath} ({r.Settings.StatEdits.Count} stat edit(s) recorded)");
            Console.WriteLine("STAT SET");
            return 0;
        }
        catch (StatEditRefusedException e)
        {
            Print(e.Findings);
            Console.Error.WriteLine($"REFUSED ({e.Code}): nothing written");
            return 1;
        }
    }

    static void Report(StatEditPlan p)
    {
        Print(p.Warnings);
        var w = p.Record;
        Console.WriteLine($"unit: {p.Unit.Name} ({p.Unit.RelPath}, area {p.Unit.Area})");
        Console.WriteLine($"stat: {p.Entry.Id} [{p.Section}] {p.Entry.Label}: {(p.Before.IsSet ? p.Before.Text : "(not set)")} -> {(p.Entry.Field == null ? p.NewRawValue : StatValue.PriceField(p.NewRawValue, p.Entry.Field))}  ({StatDictionary.Describe(p.Entry, p.NewNumber)})");
        if (p.Before.Raw != null) Console.WriteLine($"was from: {p.Before.Raw.Origin}");
        Console.WriteLine($"write: {SettingsKind(w.WriteKind)} {w.File} [{w.FileSection}] '{w.Key}: {w.NewText}'{(w.OldText != null ? $" (was '{w.OldText}')" : "")}, mode {w.Mode}");
        Console.WriteLine($"affected units: {p.Affected.Count}");
        foreach (var a in p.Affected) Console.WriteLine($"  {a.Unit.Name,-28} {a.Unit.RelPath}  [{string.Join(", ", a.Sections)}] {a.Before ?? "(not set)"} -> {a.After}");
        Console.WriteLine($"lint: {p.NewLintFindings.Count} new finding(s)");
    }

    static string SettingsKind(RwEditor.Core.Settings.StatWriteKind k) => RwEditor.Core.Settings.SettingsJson.WriteKindToString(k);

    // ------------------------------------------------------------------------------------------------------------------ revert-stats
    public static int RevertStats(Opts o)
    {
        o.Only("mod", "settings", "unit", "key", "mod-group", "dry-run", "backup-dir", "allow-git-tree");
        var sel = StatPipeline.Select(o.Get("unit"), o.Get("key"), o.Get("mod-group"));
        var r = StatPipeline.Revert(o.Req("mod"), o.Req("settings"), sel, Options(o), o.Flag("dry-run"));
        if (r.Reverted.Count == 0) { Console.WriteLine("no recorded stat edit matches; nothing to revert"); return 0; }
        foreach (var e in r.Reverted)
            Console.WriteLine($"revert seq {e.Seq}: {e.Unit} {e.StatId} [{e.Section}] {SettingsKind(e.WriteKind)} {e.File} '{e.Key}: {e.NewText}' -> {(e.OldText != null ? $"'{e.OldText}'" : "line removed")}");
        Console.WriteLine($"lint: {r.NewLintFindings.Count} new finding(s)");
        if (!r.Written) { Console.WriteLine("DRY RUN: nothing written"); return 0; }
        Console.WriteLine($"backup: {r.BackupPath}");
        foreach (var f in r.NewBytes.Keys) Console.WriteLine($"written: {f}");
        Console.WriteLine($"settings: {r.Settings.StatEdits.Count} stat edit(s) still recorded");
        Console.WriteLine("REVERTED TO DEFAULTS");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------------------ thumbs
    public static int Thumbs(Opts o)
    {
        o.Only("mod", "out", "game", "size", "no-cache");
        string mod = Path.GetFullPath(o.Req("mod"));
        int size = Thumbnails.DefaultSize;
        if (o.Get("size") is string ss && (!int.TryParse(ss, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 16 || size > 256))
            throw new UsageException("--size must be 16..256");
        string? game = o.Get("game");
        if (game != null && !Directory.Exists(Path.Combine(game, "assets", "units"))) throw new UsageException($"--game {game}: no assets\\units folder there");
        string outDir = Path.GetFullPath(o.Req("out"));
        if (outDir.StartsWith(mod.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || outDir.Equals(mod, StringComparison.OrdinalIgnoreCase))
            throw new EditorRefusalException("OUT_INSIDE_MOD", "--out may not be inside the mod folder (thumbnails are never written into a mod)");

        var model = UnitModel.Load(mod);
        var report = Thumbnails.Build(model, game, size);
        string sig = Thumbnails.Signature(model, game, size);
        var zip = Thumbnails.Pack(report, sig);
        string outZip = Thumbnails.WriteOnce(Path.Combine(outDir, "thumbs.zip"), zip);
        Console.WriteLine($"wrote {outZip} ({zip.Length} bytes)");
        if (!o.Flag("no-cache"))
        {
            string cache = Thumbnails.CachePath(mod);
            if (Thumbnails.ReadSignature(cache) == sig) Console.WriteLine($"cache up to date: {cache}");
            else Console.WriteLine($"cache written: {Thumbnails.WriteOnce(cache, zip)}");
        }
        foreach (var g in report.Thumbs.Where(t => t.Source != ThumbSource.Image).GroupBy(t => t.Source))
        {
            Console.WriteLine($"{g.Key}: {g.Count()}");
            foreach (var t in g.Take(8)) Console.WriteLine($"  {t.Unit.Name} ({t.Unit.RelPath}): {t.Note}");
            if (g.Count() > 8) Console.WriteLine("  ...");
        }
        Console.WriteLine($"thumbnails: {report.Thumbs.Count} units; real image {report.Count(ThumbSource.Image)}, minimap icon {report.Count(ThumbSource.MinimapIcon)}, silhouette {report.Count(ThumbSource.Silhouette)}");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------------------ dict-check
    public static int DictCheck(Opts o)
    {
        o.Only("dict", "mod");
        var dict = o.Get("dict") is string p ? StatDictionary.Load(p) : StatDictionary.Embedded;   // throws DICTIONARY_INVALID with every problem
        Console.WriteLine($"dictionary: {dict.Entries.Count} entries ({dict.Entries.Count(e => e.Tier == StatTier.Simple)} simple, {dict.Entries.Count(e => e.Tier == StatTier.Expand)} expand), " +
            $"{dict.Rules.Count} cross-key rules, {dict.NeverEditable.Count} never-editable key patterns; verified entries {dict.Entries.Count(e => e.Verified)}; PASS");
        if (o.Get("mod") is not string mod) return 0;
        var model = UnitModel.Load(mod);
        var rows = StatSurvey.Run(model, dict);
        int bad = 0, accepted = 0;
        Console.WriteLine($"{"entry",-31} {"set",6} {"literal",7} {"r/o",5} {"min",12} {"median",12} {"max",12}  outside-hard editable / locked / warn");
        foreach (var r in rows)
        {
            Console.WriteLine($"{r.Entry.Id,-31} {r.Occurrences,6} {r.Literal,7} {r.ReadOnly,5} {N(r.Min),12} {N(r.Median),12} {N(r.Max),12}  {r.OutsideHard.Count} / {r.OutsideHardLocked} / {r.OutsideWarn}");
            if (r.OutsideHard.Count > 0 && r.Entry.ShippedOutsideHard != null)
            {
                Console.WriteLine($"    accepted ({r.OutsideHard.Count}): {r.Entry.ShippedOutsideHard}");
                accepted += r.OutsideHard.Count;
                continue;
            }
            foreach (var x in r.OutsideHard.Take(5)) Console.WriteLine("    OUTSIDE HARD LIMIT: " + x);
            bad += r.OutsideHard.Count;
        }
        Console.WriteLine(bad == 0
            ? $"shipped values: every editable literal is inside the hard limits ({model.Units.Count} units; {accepted} documented exception(s))"
            : $"shipped values: {bad} editable literal(s) outside a hard limit (the dictionary would refuse values the game loads)");
        return bad == 0 ? 0 : 1;
    }

    static string N(double? v) => v is double d ? StatValue.Format(Math.Round(d, 4)) : "-";
}
