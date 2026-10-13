using System.Text.RegularExpressions;
using RwEditor.Core.Ini;
using RwEditor.Core.Mod;

namespace RwEditor.Core.Validation;

/// <summary>
/// Port of tools/RwCatalog/Lint.cs (rules R1, R1b, R2, R3, R4), same finding strings, run on an in-RAM snapshot so a PLANNED change can be linted before
/// it is written. Kept as a copy (not a reference) because RwCatalog is an exe with top-level statements on net9.0 and this phase may not edit it;
/// a test runs both on the same folder and compares (see tests). Findings are compared as sets: the pipeline requires 0 new findings.
/// </summary>
public static class RwLint
{
    /// <summary>tools/RwCatalog/Lint.cs: the jar's hard error is "id cannot be longer than 15 characters", a 15-character id failed in practice, so ids stay at 13.</summary>
    public const int MaxIdSafe = 13;

    static readonly Regex SpawnUnitName = new(@"(?:^|,)\s*([A-Za-z0-9_\.]+)\s*(?:\(|\*|,|$)", RegexOptions.CultureInvariant);

    public static List<string> Run(ModSnapshot snap)
    {
        var findings = new List<string>();
        var parsed = new List<(string Rel, IniDocument Doc)>();
        foreach (var rel in snap.Files) parsed.Add((rel, IniDocument.Parse(snap.GetText(rel)!, rel, IniDialect.Catalog)));

        var sectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, ini) in parsed)
        {
            foreach (var s in ini.Sections) sectionNames.Add(s.Name);
            var n = ini.Get("core", "name");
            if (n != null) foreach (var part in n.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) unitNames.Add(part);
        }

        foreach (var (rel, ini) in parsed)
            foreach (var s in ini.Sections)
                foreach (var p in s.Pairs)
                {
                    string key = p.Key, val = p.Value.Trim();
                    if (key.Equals("id", StringComparison.OrdinalIgnoreCase) && !val.Contains("${") && val.Length > MaxIdSafe)
                        findings.Add($"ID_TOO_LONG|{rel}|[{s.Name}] id '{val}' is {val.Length} chars (game fails at >= 14-15)");
                    if (key.Equals("@define actionTag", StringComparison.OrdinalIgnoreCase) && val.Length + 3 > MaxIdSafe)
                        findings.Add($"ID_TOO_LONG|{rel}|[{s.Name}] actionTag '{val}' makes ids of {val.Length + 3} chars (<= {MaxIdSafe} required)");
                    if (key.Equals("@copyFromSection", StringComparison.OrdinalIgnoreCase))
                        foreach (var t in val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            if (!t.Contains("${") && !sectionNames.Contains(t))
                                findings.Add($"MISSING_SECTION|{rel}|[{s.Name}] @copyFromSection '{t}' not defined anywhere");
                    if (s.Name.Equals("core", StringComparison.OrdinalIgnoreCase) && key.Equals("techLevel", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(val, out int tl) && (tl > 3 || tl < 1))
                        findings.Add($"TECHLEVEL_RANGE|{rel}|[core] techLevel {tl} must be 1..3");
                    if (key.Equals("convertTo", StringComparison.OrdinalIgnoreCase) && !val.Contains("${") && !unitNames.Contains(val))
                        findings.Add($"UNKNOWN_UNIT|{rel}|[{s.Name}] convertTo '{val}' has no unit with that name in this mod");
                    if (key.Equals("spawnUnits", StringComparison.OrdinalIgnoreCase))
                        foreach (Match m in SpawnUnitName.Matches(val))
                        {
                            string u = m.Groups[1].Value;
                            if (!u.Contains("${") && !unitNames.Contains(u)) findings.Add($"UNKNOWN_UNIT|{rel}|[{s.Name}] spawnUnits '{u}' has no unit with that name in this mod");
                        }
                }
        return findings;
    }

    /// <summary>Findings of <paramref name="planned"/> that are not findings of <paramref name="reference"/> (the "NEW (ours)" of RwCatalog lint).</summary>
    public static List<string> NewFindings(ModSnapshot reference, ModSnapshot planned)
    {
        var baseSet = new HashSet<string>(Run(reference), StringComparer.Ordinal);
        return Run(planned).Where(f => !baseSet.Contains(f)).ToList();
    }
}
