using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RwEditor.Core.Apply;

namespace RwEditor.Core.GameLog;

/// <summary>What the last Test load of a copy found.</summary>
public sealed record TestLoadRecord(DateTime Utc, bool Clean, bool ReachedInit, int Problems, int Warnings, string Summary, string? LogPath);

/// <summary>
/// Remembers, per copy, how the last Test load came out, so that after a failed test the editor can list "changes since the last clean test" as the
/// suspects (a log cannot name a cause for wrong behaviour that has no error line; the honest answer then is the list of what changed since it last worked).
/// Stored in %LOCALAPPDATA%\DepacheRwEditor\testloads\&lt;copyId&gt;.json, written once per test (temp + replace). A damaged file is treated as "never tested".
/// </summary>
public static class TestLoadStore
{
    public static string DefaultFolder => Path.Combine(RwEditor.Core.Setup.ProfileFolder.Root, "testloads");

    static string PathOf(string folder, string copyId) => Path.Combine(folder, copyId + ".json");

    public static TestLoadRecord? Load(string copyId, string? folder = null)
    {
        try
        {
            string p = PathOf(folder ?? DefaultFolder, copyId);
            if (!File.Exists(p)) return null;
            if (JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) is not JsonObject o) return null;
            return new TestLoadRecord(DateTime.Parse((string)o["utc"]!, null, System.Globalization.DateTimeStyles.RoundtripKind), (bool)o["clean"]!, (bool)o["reachedInit"]!,
                (int)o["problems"]!, (int)o["warnings"]!, (string)o["summary"]!, (string?)o["logPath"]);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    public static void Save(string copyId, TestLoadRecord r, string? folder = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        string p = PathOf(folder, copyId), tmp = p + ".tmp";
        var o = new JsonObject
        {
            ["clean"] = r.Clean, ["logPath"] = r.LogPath, ["problems"] = r.Problems, ["reachedInit"] = r.ReachedInit, ["summary"] = r.Summary,
            ["utc"] = r.Utc.ToUniversalTime().ToString("o"), ["warnings"] = r.Warnings,
        };
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
        if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
    }

    /// <summary>The record a finished test makes: clean = it reached "init complete" and found nothing that touches this mod.</summary>
    public static TestLoadRecord RecordOf(LogReport report, string? logPath, DateTime utc) =>
        new(utc, report.Clean && report.ReachedInit == true, report.ReachedInit == true, report.Problems.Count(),
            report.Findings.Count(f => f.Severity == LogSeverity.Warning && f.Origin is not (LogOrigin.Shipped or LogOrigin.OtherMod)), report.Summary, logPath);

    /// <summary>The changes (applied, not undone) made after <paramref name="since"/>; all of them when the copy never had a clean test.</summary>
    public static IReadOnlyList<HistoryEntry> ChangesSince(IEnumerable<HistoryEntry> history, DateTime? since) =>
        history.Where(h => h.IsChange && !h.Undone && (since == null || h.CreatedUtc > since.Value.ToUniversalTime())).OrderBy(h => h.CreatedUtc).ToList();
}
