using System.Text.RegularExpressions;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Validation;

/// <summary>One assertion, printed like the PowerShell checks ("PASS  ..." / "FAIL  ...").</summary>
public sealed record RuleResult(bool Pass, string Text)
{
    public override string ToString() => (Pass ? "PASS  " : "FAIL  ") + Text;
}

public sealed class RuleReport
{
    public List<RuleResult> Results { get; } = new();
    public IEnumerable<RuleResult> Failures => Results.Where(r => !r.Pass);
    public bool AllPass => Results.All(r => r.Pass);
    public int Passed => Results.Count(r => r.Pass);
    public int Failed => Results.Count(r => !r.Pass);
    internal void Assert(bool ok, string what) => Results.Add(new RuleResult(ok, what));
    public string Summary => AllPass ? $"ALL ASSERTIONS PASS ({Passed})" : $"{Failed} ASSERTION(S) FAILED, {Passed} passed";
}

/// <summary>
/// C# port of the include-slot rules of tools/check-start-spawner.ps1 ((i) editor-tag collisions, (ii-m), (iv-m)) and tools/check-option-sections.ps1 ((f)),
/// evaluated on the MERGED unit (the locked file's own sections, then the generated file's new sections), exactly as the scripts decide (same reader
/// rules, same regexes, same case rules). The scripts stay the ground truth; this port lets the editor refuse a plan BEFORE writing it.
/// Not ported: the assertions about the locked files' own content ((ii) defaults, (iii), (iv), (a)-(e) for the shipped options) - a copy whose locked
/// files differ from editor-support.json is refused by the hash guard, so those files are the shipped, already-checked bytes.
/// </summary>
public static class IncludeRules
{
    const string GenAnchorRef = "ROOT:/" + EditorSupport.AnchorGenerated;
    const string GenSelRef = "ROOT:/" + EditorSupport.SelectorGenerated;
    const int MaxCond = SettingsValidator.MaxConditionLine;
    const string Confirmed = "numberOfUnitsInNeutralTeam(withTag='gameOptionsConfirmed') > 0";
    const string HumanOnly = "not (numberOfUnitsInNeutralTeam(withTag='extraHuman') > 0 and self.isControlledByAI)";

    static readonly Regex Ws = new(@"\s+", RegexOptions.CultureInvariant);
    static string Norm(string? s) => s == null ? "" : Ws.Replace(s, "");

    public static RuleReport Check(ModSnapshot snap)
    {
        var rep = new RuleReport();
        var unitNames = UnitNamesLikeScan(snap);
        AnchorRules(snap, rep, unitNames, out var anchorTags);
        SelectorRules(snap, rep, out var selectorTags);
        var a = anchorTags.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var s = selectorTags.OrderBy(x => x, StringComparer.Ordinal).ToList();
        rep.Assert(a.SequenceEqual(s, StringComparer.Ordinal), $"(iv-m) selector group tags ({string.Join(", ", selectorTags)}) equal anchor group tags ({string.Join(", ", anchorTags)})");
        var hits = TagCollisions(snap);
        rep.Assert(hits.Count == 0, $"(i) editor tags (ug01..ug08*, ugW01.., ugInit/Gate/Hide/Def, xGrpWait) are used only in the two generated files (xGrpWait also in the anchor's cleanup); found {hits.Count} elsewhere"
            + (hits.Count > 0 ? ": " + string.Join("; ", hits.Take(20)) : ""));
        return rep;
    }

    // ------------------------------------------------------------------------------------------------ (i) tag collisions
    static readonly Regex TokRx = new(@"(?<![A-Za-z0-9_])(ug0[1-8][A-Za-z]*|ugW0[1-8]|ugInit|ugGate|ugHide|ugDef|xGrpWait)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    static readonly Regex CommentLine = new(@"(?m)^\s*#.*$", RegexOptions.CultureInvariant);

    /// <summary>Editor tags used in any *.ini / *.template other than the two generated files ("file: token"), comments ignored; xGrpWait is allowed in the anchor.</summary>
    public static IReadOnlyList<string> TagCollisions(ModSnapshot snap)
    {
        var hits = new List<string>();
        foreach (var rel in snap.Files)
        {
            if (IsGenerated(rel)) continue;
            var text = snap.GetText(rel)!;
            if (!TokRx.IsMatch(text)) continue;
            string code = CommentLine.Replace(text, "");
            foreach (Match m in TokRx.Matches(code))
            {
                if (m.Value == "xGrpWait" && rel.Equals(EditorSupport.AnchorLocked, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add($"{rel}: {m.Value}");
            }
        }
        return hits;
    }

    static bool IsGenerated(string rel) =>
        rel.Equals(EditorSupport.AnchorGenerated, StringComparison.OrdinalIgnoreCase) || rel.Equals(EditorSupport.SelectorGenerated, StringComparison.OrdinalIgnoreCase);

    static readonly Regex CoreBlock = new(@"(?ms)^\s*\[core\]\s*$(.*?)(?=^\s*\[|\z)", RegexOptions.CultureInvariant);
    static readonly Regex NameLine = new(@"(?m)^\s*name\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant);

    /// <summary>The unit-name table of check-start-spawner.ps1's (i) scan: the first [core] block's name lines of every file, comma-split; case-insensitive lookup.</summary>
    static HashSet<string> UnitNamesLikeScan(ModSnapshot snap)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in snap.Files)
        {
            var mc = CoreBlock.Match(snap.GetText(rel)!);
            if (!mc.Success) continue;
            foreach (Match mn in NameLine.Matches(mc.Groups[1].Value))
                foreach (var part in mn.Groups[1].Value.Split(','))
                    if (part.Trim().Length > 0) names.Add(part.Trim());
        }
        return names;
    }

    // ------------------------------------------------------------------------------------------------ shared helpers
    static IniDocument? Doc(ModSnapshot snap, string rel)
    {
        var t = snap.GetText(rel);
        return t == null ? null : IniDocument.Parse(t, rel, IniDialect.Checker);
    }

    /// <summary>Get-ChainNames: section names of a file and its whole copyFrom chain, skipping the generated file; name -> file leaf (case-insensitive).</summary>
    static void ChainNames(ModSnapshot snap, string rel, string skipRel, HashSet<string> seen, Dictionary<string, string> names)
    {
        rel = ModSnapshot.Norm(rel);
        if (!seen.Add(rel) || rel.Equals(skipRel, StringComparison.OrdinalIgnoreCase)) return;
        var d = Doc(snap, rel);
        if (d == null) return;
        foreach (var s in d.Named) names[s.Name] = Path.GetFileName(rel);
        var cf = d.First("core")?.Get("copyFrom");
        if (cf == null) return;
        foreach (var e in cf.Split(','))
        {
            if (e.Trim().Length == 0) continue;
            var p = ModSnapshot.ResolveCopyFrom(rel, e);
            if (p != null) ChainNames(snap, p, skipRel, seen, names);
        }
    }

    static List<string> CopyFromEntries(IniSection? core) =>
        core?.Get("copyFrom") is string cf ? cf.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();

    static List<string> SplitEntries(string v)
    {
        var outp = new List<string>();
        int d = 0;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in v)
        {
            if (ch == '(') d++; else if (ch == ')') d--;
            if (ch == ',' && d == 0) { outp.Add(sb.ToString().Trim()); sb.Clear(); } else sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) outp.Add(sb.ToString().Trim());
        return outp;
    }

    // ------------------------------------------------------------------------------------------------ anchor (ii-m)
    static readonly Regex AnchorGenName = new(@"^hiddenAction_ug(Init|Gate|0[1-8]S|0[1-8]R)$", RegexOptions.CultureInvariant);
    static readonly Regex AnchorKind = new(@"^hiddenAction_(ug(Init|Gate)|ug0[1-8](S|R))$", RegexOptions.CultureInvariant);
    static readonly Regex GroupSpawnName = new(@"^hiddenAction_ug0[1-8]S$", RegexOptions.CultureInvariant);
    static readonly Regex GroupReleaseName = new(@"^hiddenAction_ug(0[1-8])R$", RegexOptions.CultureInvariant);
    static readonly Regex EntryRx = new(@"^([A-Za-z0-9_\.]+)\((.*)\)$", RegexOptions.CultureInvariant);
    static readonly Regex OffsetRandom = new("offsetRandom", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static void AnchorRules(ModSnapshot snap, RuleReport rep, HashSet<string> unitNames, out List<string> groupTags)
    {
        groupTags = new List<string>();
        var an = Doc(snap, EditorSupport.AnchorLocked);
        if (an == null) { rep.Assert(false, $"(ii) {EditorSupport.AnchorLocked} exists"); return; }
        var core = an.First("core");
        var cf = CopyFromEntries(core);
        int nRef = cf.Count(e => e.Equals(GenAnchorRef, StringComparison.OrdinalIgnoreCase));
        int nOther = cf.Count(e => e.Contains("editorGenerated", StringComparison.OrdinalIgnoreCase) && !e.Equals(GenAnchorRef, StringComparison.OrdinalIgnoreCase));
        rep.Assert(nRef == 1 && nOther == 0, $"(ii-m) anchor copyFrom includes {GenAnchorRef} exactly once and no other editorGenerated file (found {nRef} / other {nOther})");

        var gen = Doc(snap, EditorSupport.AnchorGenerated);
        if (gen == null) { rep.Assert(false, $"(ii-m) {EditorSupport.AnchorGenerated} exists"); return; }
        var genSecs = gen.Named.ToList();
        var genCore = genSecs.Where(s => s.Name.Equals("core", StringComparison.OrdinalIgnoreCase)).ToList();
        var coreKeys = genCore.SelectMany(s => s.Keys).ToList();
        rep.Assert(genCore.Count == 1 && coreKeys.Count == 1 && coreKeys[0].Equals("dont_load", StringComparison.OrdinalIgnoreCase)
            && string.Equals(genCore[0].Get("dont_load"), "true", StringComparison.OrdinalIgnoreCase),
            $"(ii-m) generated [core] holds only 'dont_load: true' (keys: {string.Join(", ", coreKeys)})");
        var genBody = genSecs.Where(s => !s.Name.Equals("core", StringComparison.OrdinalIgnoreCase)).ToList();
        var badName = genBody.Where(s => !AnchorGenName.IsMatch(s.Name)).Select(s => s.Name).ToList();
        rep.Assert(badName.Count == 0, "(ii-m) every generated section has a generated name" + (badName.Count > 0 ? ": not allowed " + string.Join(", ", badName) : ""));
        var dup = genBody.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        rep.Assert(dup.Count == 0, "(ii-m) no generated section is defined twice" + (dup.Count > 0 ? ": " + string.Join(", ", dup) : ""));

        var chain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ChainNames(snap, EditorSupport.AnchorLocked, EditorSupport.AnchorGenerated, seen, chain);
        ChainNames(snap, "all-units.template", EditorSupport.AnchorGenerated, seen, chain);
        var collide = genBody.Where(s => chain.ContainsKey(s.Name)).Select(s => $"[{s.Name}] (also in {chain[s.Name]})").ToList();
        rep.Assert(collide.Count == 0, $"(ii-m) no generated section re-uses a section name of the anchor, its copyFrom chain or all-units.template ({chain.Count} names checked)"
            + (collide.Count > 0 ? ": " + string.Join(", ", collide) : ""));

        var keySpec = new Dictionary<string, string>
        {
            ["S"] = "id|autoTrigger|addGlobalTeamTags|spawnUnits", ["R"] = "id|autoTrigger|temporarilyRemoveTags",
            ["Init"] = "id|autoTriggerOnEvent|temporarilyAddTags", ["Gate"] = "id|autoTrigger|temporarilyRemoveTags",
        };
        foreach (var g in genBody)
        {
            var m = AnchorKind.Match(g.Name);
            if (!m.Success) continue;
            string kind = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            string keys = string.Join("|", g.Keys);
            string wantId = g.Name.Substring(13);
            rep.Assert(keys == keySpec[kind] && g.Get("id") == wantId, $"(ii-m) [{g.Name}] is exactly id:{wantId} + {keySpec[kind].Replace("id|", "").Replace("|", " + ")} (has: {keys})");
        }

        var anSecs = an.Sections;
        var ownNames = new HashSet<string>(an.Named.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var merged = new List<IniSection>(anSecs);
        merged.AddRange(genBody.Where(g => !ownNames.Contains(g.Name)));

        var ownSpawns = anSecs.Count(s => s.Get("spawnUnits") != null);
        var groupSpawns = genBody.Where(s => GroupSpawnName.IsMatch(s.Name)).ToList();
        int allSpawns = merged.Count(s => s.Get("spawnUnits") != null);
        rep.Assert(allSpawns == ownSpawns + groupSpawns.Count && groupSpawns.Count <= 8,
            $"(ii-m) merged unit has {ownSpawns} default + {groupSpawns.Count} group spawn actions (found {allSpawns}; groups <= 8)");
        var otherWriters = merged.Count(s => s.Get("spawnUnits") == null && s.Get("addGlobalTeamTags") != null);
        rep.Assert(otherWriters == 0, "(ii) only spawn actions write global team tags in the anchor (merged unit)");

        var groupNN = new List<string>();
        int totalUnits = 0;
        foreach (var s in groupSpawns)
        {
            string nn = s.Name.Substring(15, 2), tag = "ug" + nn, done = $"ug{nn}Done";
            groupNN.Add(nn);
            string trig = s.Get("autoTrigger") ?? "";
            string baseT = $"if self.timeAlive > 1 and {Confirmed} and numberOfUnitsInNeutralTeam(withTag='{tag}') > 0 and not self.globalTeamTags(includes=\"{done}\")";
            string mode = Norm(trig) == Norm($"{baseT} and {HumanOnly}") ? "toggle" : Norm(trig) == Norm($"{baseT} and not self.isControlledByAI") ? "humans" : "";
            rep.Assert(mode.Length > 0, $"(ii-m) [{s.Name}] trigger is exactly the canonical pure-AND guard set (timeAlive, confirmed, option tag {tag}, not Done {done}, human-only term or 'not self.isControlledByAI'); mode: {(mode.Length > 0 ? mode : "NONE")}");
            rep.Assert(s.Get("addGlobalTeamTags") == done, $"(ii-m) [{s.Name}] sets its Done tag {done} in the same action");
            int len = ("autoTrigger: " + trig).Length;
            rep.Assert(len <= MaxCond, $"(ii-m) [{s.Name}] trigger line is <= {MaxCond} characters ({len})");
            var entries = SplitEntries(s.Get("spawnUnits") ?? "");
            totalUnits += entries.Count;
            var badE = entries.Where(e =>
            {
                var em = EntryRx.Match(e);
                return !(em.Success && !e.Contains('*') && !OffsetRandom.IsMatch(em.Groups[2].Value) && unitNames.Contains(em.Groups[1].Value));
            }).ToList();
            rep.Assert(entries.Count is >= 1 and <= 12 && badE.Count == 0,
                $"(ii-m) [{s.Name}] spawns 1..12 single entries of existing units, no '*count', no random offset ({entries.Count} entries)" + (badE.Count > 0 ? ": bad " + string.Join("; ", badE) : ""));
            var r = genBody.FirstOrDefault(x => x.Name == $"hiddenAction_ug{nn}R");
            string relBase = $"if {Confirmed} and self.tags(includes=\"ugW{nn}\") and (self.globalTeamTags(includes=\"{done}\") or numberOfUnitsInNeutralTeam(withTag='{tag}') < 1";
            string relWant = mode == "humans" ? relBase + " or self.isControlledByAI)" : relBase + ")";
            bool relOk = r != null && Norm(r.Get("autoTrigger")) == Norm(relWant) && r.Get("temporarilyRemoveTags") == $"ugW{nn}";
            rep.Assert(relOk, $"(ii-m) [hiddenAction_ug{nn}R] exists, matches the {mode} mode exactly and removes only ugW{nn}");
            if (r != null)
            {
                int rl = ("autoTrigger: " + (r.Get("autoTrigger") ?? "")).Length;
                rep.Assert(rl <= MaxCond, $"(ii-m) [hiddenAction_ug{nn}R] trigger line is <= {MaxCond} characters ({rl})");
            }
        }
        var orphanR = genBody.Select(g => GroupReleaseName.Match(g.Name)).Where(m => m.Success && !groupNN.Contains(m.Groups[1].Value)).Select(m => m.Value).ToList();
        rep.Assert(orphanR.Count == 0, "(ii-m) no release action without its spawn action" + (orphanR.Count > 0 ? ": " + string.Join(", ", orphanR) : ""));
        rep.Assert(totalUnits <= 40, $"(ii-m) at most 40 group units per team in total ({totalUnits})");

        var init = genBody.FirstOrDefault(s => s.Name == "hiddenAction_ugInit");
        var gate = genBody.FirstOrDefault(s => s.Name == "hiddenAction_ugGate");
        if (groupNN.Count == 0)
            rep.Assert(init == null && gate == null, "(ii-m) no group -> no ugInit / ugGate (xGrpWait is never set, the cleanup is exactly as before)");
        else
        {
            var sorted = groupNN.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var wantInit = new[] { "xGrpWait" }.Concat(sorted.Select(x => "ugW" + x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var hasInit = init?.Get("temporarilyAddTags") is string ta ? ta.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();
            rep.Assert(init != null && init.Get("autoTriggerOnEvent") == "created" && hasInit.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(wantInit),
                $"(ii-m) ugInit adds exactly xGrpWait + one ugWNN per group at creation ({string.Join(", ", hasInit)})");
            string gateWant = "if self.tags(includes=\"xGrpWait\")" + string.Concat(sorted.Select(x => $" and not self.tags(includes=\"ugW{x}\")"));
            string gateTrig = gate?.Get("autoTrigger") ?? "";
            rep.Assert(gate != null && Norm(gateTrig) == Norm(gateWant) && gate.Get("temporarilyRemoveTags") == "xGrpWait",
                "(ii-m) ugGate removes xGrpWait exactly when every ugWNN is gone (canonical trigger)");
            int gl = ("autoTrigger: " + gateTrig).Length;
            rep.Assert(gl <= MaxCond, $"(ii-m) ugGate trigger line is <= {MaxCond} characters ({gl})");
        }
        groupTags = groupNN.Select(x => "ug" + x).ToList();
    }

    // ------------------------------------------------------------------------------------------------ selector (f)
    static readonly Regex SelGenName = new(@"^(hiddenAction_ug(Hide|Def)|action_ug0[1-8](On|Off|Msg))$", RegexOptions.CultureInvariant);
    static readonly Regex GroupOnName = new(@"^action_ug0[1-8]On$", RegexOptions.CultureInvariant);
    static readonly Regex GroupOffMsgName = new(@"^action_ug(0[1-8])(Off|Msg)$", RegexOptions.CultureInvariant);
    static readonly Regex OwnOnLike = new(@"^action_.*On$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GroupNameRx = new(@"^[A-Za-z0-9 \-_.!?+'&()]{1,40}$", RegexOptions.CultureInvariant);
    static readonly Regex XhRx = new(@"^xh(.+)$", RegexOptions.CultureInvariant);
    static readonly Regex ExpandDotted = new(@"\$\{\s*([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s*\}", RegexOptions.CultureInvariant);
    static readonly Regex ExpandSimple = new(@"\$\{\s*([A-Za-z0-9_]+)\s*\}", RegexOptions.CultureInvariant);

    sealed class MSec
    {
        public required IniSection S;
        public bool Gen;
        public string Name => S.Name;
    }

    static void SelectorRules(ModSnapshot snap, RuleReport rep, out List<string> groupTags)
    {
        groupTags = new List<string>();
        var sel = Doc(snap, EditorSupport.SelectorLocked);
        if (sel == null) { rep.Assert(false, $"(f) {EditorSupport.SelectorLocked} exists"); return; }
        var secs = sel.Sections.Select(s => new MSec { S = s }).ToList();
        var ownCore = sel.First("core");
        var ownNames = new HashSet<string>(sel.Named.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

        var cf = CopyFromEntries(ownCore);
        int nRef = cf.Count(e => e.Equals(GenSelRef, StringComparison.OrdinalIgnoreCase));
        int nOther = cf.Count(e => e.Contains("editorGenerated", StringComparison.OrdinalIgnoreCase) && !e.Equals(GenSelRef, StringComparison.OrdinalIgnoreCase));
        rep.Assert(nRef == 1 && nOther == 0, $"(f) selector copyFrom includes {GenSelRef} exactly once and no other editorGenerated file (found {nRef} / other {nOther})");

        var gen = Doc(snap, EditorSupport.SelectorGenerated);
        if (gen == null) { rep.Assert(false, $"(f) {EditorSupport.SelectorGenerated} exists"); return; }
        var genSecs = gen.Named.ToList();
        var genCore = genSecs.Where(s => s.Name.Equals("core", StringComparison.OrdinalIgnoreCase)).ToList();
        var coreKeys = genCore.SelectMany(s => s.Keys).ToList();
        rep.Assert(genCore.Count == 1 && coreKeys.Count == 1 && coreKeys.All(k => k.Equals("dont_load", StringComparison.OrdinalIgnoreCase))
            && string.Equals(genCore[0].Get("dont_load"), "true", StringComparison.OrdinalIgnoreCase),
            $"(f) generated [core] holds only 'dont_load: true' (keys: {string.Join(", ", coreKeys)})");
        var genBody = genSecs.Where(s => !s.Name.Equals("core", StringComparison.OrdinalIgnoreCase)).ToList();
        var badName = genBody.Where(s => !SelGenName.IsMatch(s.Name)).Select(s => s.Name).ToList();
        rep.Assert(badName.Count == 0, "(f) every generated section has a generated name" + (badName.Count > 0 ? ": not allowed " + string.Join(", ", badName) : ""));
        var dup = genBody.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        rep.Assert(dup.Count == 0, "(f) no generated section is defined twice" + (dup.Count > 0 ? ": " + string.Join(", ", dup) : ""));
        var chain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ChainNames(snap, EditorSupport.SelectorLocked, EditorSupport.SelectorGenerated, seen, chain);
        ChainNames(snap, "all-units.template", EditorSupport.SelectorGenerated, seen, chain);
        var collide = genBody.Where(s => chain.ContainsKey(s.Name)).Select(s => $"[{s.Name}] (also in {chain[s.Name]})").ToList();
        rep.Assert(collide.Count == 0, $"(f) no generated section re-uses a section name of the selector, its copyFrom chain or all-units.template ({chain.Count} names checked)"
            + (collide.Count > 0 ? ": " + string.Join(", ", collide) : ""));

        // merged unit = own sections in file order, then the generated file's NEW sections
        foreach (var g in genBody) if (!ownNames.Contains(g.Name)) secs.Add(new MSec { S = g, Gen = true });
        var byName = new Dictionary<string, MSec>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in secs) if (s.Name.Length > 0) byName[s.Name] = s;
        var res = new SectionResolver(secs.Select(x => x.S));

        string? Resolved(string sec, string key) { try { return res.Get(sec, key); } catch (InvalidOperationException) { return null; } }
        string? Expand(string sec, string? v)
        {
            if (v == null) return null;
            IReadOnlyDictionary<string, string> map;
            try { map = res.Resolve(sec); } catch (InvalidOperationException) { return v; }
            for (int n = 0; n < 5 && v.Contains("${", StringComparison.Ordinal); n++)
            {
                v = ExpandDotted.Replace(v, m => Resolved(m.Groups[1].Value, m.Groups[2].Value) ?? m.Value);
                v = ExpandSimple.Replace(v, m => map.TryGetValue("@define " + m.Groups[1].Value, out var d) ? d : m.Value);
            }
            return v;
        }
        double PosOf(string sec)
        {
            var v = Expand(sec, Resolved(sec, "pos"));
            return v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 999.0;
        }

        const string hideTerm = "and not self.tags(includes=\"xh${actionTag}\")";
        foreach (var t in new[] { "template_turnOnOption", "template_turnOffOption" })
        {
            var iv = byName.TryGetValue(t, out var ts) ? ts.S.Get("isVisible") : null;
            rep.Assert(!string.IsNullOrEmpty(iv) && Norm(iv).EndsWith(Norm(hideTerm), StringComparison.Ordinal), $"(f) [{t}] isVisible ends with the editor hide term {hideTerm}");
        }

        var groupOn = secs.Where(s => s.Gen && GroupOnName.IsMatch(s.Name)).ToList();
        rep.Assert(groupOn.Count <= 8, $"(f) at most 8 editor groups ({groupOn.Count})");
        var okKeys = new[] { "@copyFromSection", "@define actionTag", "@define actionName", "@define actionNameZh", "@define typeId", "pos", "description" };
        foreach (var g in groupOn)
        {
            string nn = g.Name.Substring(9, 2), tag = "ug" + nn;
            groupTags.Add(tag);
            var keys = g.S.Keys.ToList();
            var badK = keys.Where(k => !okKeys.Contains(k, StringComparer.Ordinal)).ToList();
            var dupK = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
            rep.Assert(badK.Count == 0 && dupK.Count == 0, $"(f) [{g.Name}] uses only the generated keys, each once" + (badK.Count + dupK.Count > 0 ? ": bad " + string.Join(", ", badK.Concat(dupK)) : ""));
            rep.Assert(g.S.Get("@copyFromSection") == "template_turnOnOption" && g.S.Get("@define actionTag") == tag, $"(f) [{g.Name}] copies template_turnOnOption with actionTag {tag}");
            string? tid = Resolved(g.Name, "@define typeId");
            double pos = PosOf(g.Name);
            rep.Assert(string.Equals(tid, "4", StringComparison.OrdinalIgnoreCase) && pos == 8002, $"(f) [{g.Name}] typeId 4 and pos 8002 (typeId {tid}, pos {pos})");
            string nm = g.S.Get("@define actionName") ?? "", nz = g.S.Get("@define actionNameZh") ?? "";
            rep.Assert(GroupNameRx.IsMatch(nm) && GroupNameRx.IsMatch(nz), $"(f) [{g.Name}] group name is 1-40 allowed characters (no ':', '#', '$', quotes, brackets): '{nm}'");
            foreach (var (secName, tmpl) in new[] { ($"action_ug{nn}Off", "template_turnOffOption"), ($"action_ug{nn}Msg", "template_notificationAction") })
            {
                bool ok = byName.TryGetValue(secName, out var x) && x.Gen && x.S.Get("@copyFromSection") == $"{g.Name}, {tmpl}" && x.S.Pairs.Count == 1;
                rep.Assert(ok, $"(f) [{secName}] exists and is exactly '@copyFromSection: {g.Name}, {tmpl}'");
            }
        }
        var orphans = secs.Where(s => s.Gen).Select(s => GroupOffMsgName.Match(s.Name)).Where(m => m.Success && !byName.ContainsKey($"action_ug{m.Groups[1].Value}On")).Select(m => m.Value).ToList();
        rep.Assert(orphans.Count == 0, "(f) no generated Off/Msg without its On action" + (orphans.Count > 0 ? ": " + string.Join(", ", orphans) : ""));

        var ownOn = secs.Where(s => !s.Gen && s.Name.Length > 0 && OwnOnLike.IsMatch(s.Name) && res.Copies(s.Name, "template_turnOnOption")).ToList();
        var ownTags = ownOn.Select(s => Resolved(s.Name, "@define actionTag")).Where(t => t != null).Select(t => t!).ToList();
        List<string> TagList(IniSection s, string key) => (s.Get(key) ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var hidden = new List<string>(); var defaults = new List<string>();
        foreach (var (secName, id) in new[] { ("hiddenAction_ugHide", "ugHide"), ("hiddenAction_ugDef", "ugDef") })
        {
            if (!byName.TryGetValue(secName, out var s) || !s.Gen) continue;
            string keys = string.Join("|", s.S.Keys);
            rep.Assert(keys == "id|autoTriggerOnEvent|temporarilyAddTags" && s.S.Get("id") == id && s.S.Get("autoTriggerOnEvent") == "created",
                $"(f) [{secName}] is exactly id:{id} / autoTriggerOnEvent: created / temporarilyAddTags (keys: {string.Join(", ", s.S.Keys)})");
            if (id == "ugHide") hidden = TagList(s.S, "temporarilyAddTags"); else defaults = TagList(s.S, "temporarilyAddTags");
        }
        var badHide = hidden.Where(h =>
        {
            var m = XhRx.Match(h);
            return !(m.Success && ownTags.Contains(m.Groups[1].Value, StringComparer.Ordinal) && !ModContext.NeverHide.Contains(m.Groups[1].Value, StringComparer.Ordinal));
        }).ToList();
        rep.Assert(badHide.Count == 0, "(f) ugHide adds only xh<tag> of an existing option other than fairAI / extraHuman" + (badHide.Count > 0 ? ": bad " + string.Join(", ", badHide) : ""));
        var hiddenTags = hidden.Where(h => h.Length >= 2).Select(h => h.Substring(2)).ToList();
        var gTags = groupTags;
        var badDef = defaults.Where(d => (!ownTags.Contains(d, StringComparer.Ordinal) && !gTags.Contains(d, StringComparer.Ordinal)) || hiddenTags.Contains(d, StringComparer.Ordinal)).ToList();
        rep.Assert(badDef.Count == 0, "(f) ugDef turns on only known option or group tags, none of them hidden" + (badDef.Count > 0 ? ": bad " + string.Join(", ", badDef) : ""));
        var own4 = ownOn.Where(s => string.Equals(Resolved(s.Name, "@define typeId"), "4", StringComparison.OrdinalIgnoreCase))
            .Select(s => Resolved(s.Name, "@define actionTag")).Where(t => t != null).ToList();
        int shown4 = own4.Count(t => !hiddenTags.Contains(t!, StringComparer.Ordinal)) + groupOn.Count;
        rep.Assert(shown4 <= SettingsValidator.MaxSection4Buttons, $"(f) section 4 shows at most {SettingsValidator.MaxSection4Buttons} option buttons ({shown4}; O5 cap, PH-067)");
    }
}
