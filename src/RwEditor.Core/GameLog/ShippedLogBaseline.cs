using System.Text.RegularExpressions;

namespace RwEditor.Core.GameLog;

/// <summary>
/// What the shipped mod's own load log says is normal: the keys of the notes and warnings the game writes for the mod as released (for example the
/// lines of mod-info.txt it cannot parse). A finding with a key in here is labelled "already in the shipped mod", so the user is not sent hunting for
/// something an edit did not cause. The embedded file is made by <c>rwedit log-baseline</c> from a load log of the unedited mod that came out clean.
/// </summary>
public sealed class ShippedLogBaseline
{
    public const string ResourceName = "RwEditor.Data.shipped-log-baseline.txt";

    readonly HashSet<string> _keys;
    public ShippedLogBaseline(IEnumerable<string> keys) { _keys = new HashSet<string>(keys, StringComparer.Ordinal); }

    public int Count => _keys.Count;
    public IEnumerable<string> Keys => _keys.OrderBy(k => k, StringComparer.Ordinal);
    public bool Contains(string code, string firstLine) => _keys.Contains(Key(code, firstLine));

    static ShippedLogBaseline? _embedded;
    public static ShippedLogBaseline Embedded => _embedded ??= LoadEmbedded();

    static ShippedLogBaseline LoadEmbedded()
    {
        using var s = typeof(ShippedLogBaseline).Assembly.GetManifestResourceStream(ResourceName);
        if (s == null) return new ShippedLogBaseline(Array.Empty<string>());
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static ShippedLogBaseline Parse(string text) =>
        new(text.Split('\n').Select(l => l.TrimEnd('\r').TrimStart('﻿')).Where(l => l.Length > 0 && !l.StartsWith('#')));

    /// <summary>The baseline a report would make: the notes and warnings about THIS mod in a load log that came out clean (other mods' lines are not included).</summary>
    public static ShippedLogBaseline FromReport(LogReport report) =>
        new(report.Findings.Where(f => f.Code is "WARNING" or "UNEXPECTED_FORMAT" && f.Origin != LogOrigin.OtherMod && f.Sample.Count > 0).Select(f => Key(f.Code, f.Sample[0])));

    public string ToText(string header) => "# " + header.Replace("\n", "\n# ") + "\n" + string.Join("\n", Keys) + "\n";

    static readonly Regex ModPath = new(@"(?:[A-Za-z]:)?[\\/]SD[\\/]mods[\\/]units[\\/][^\\/]+[\\/]", RegexOptions.CultureInvariant);

    /// <summary>The comparable form of a finding: its code plus its first log line with the mod's folder, numbers and slashes made neutral.</summary>
    public static string Key(string code, string firstLine)
    {
        string s = ModPath.Replace(firstLine.Split('\n')[0].Trim(), "MOD/").Replace('\\', '/');
        s = Regex.Replace(s, @"[0-9]+", "N", RegexOptions.CultureInvariant);
        return code + "|" + (s.Length > 220 ? s[..220] : s);
    }
}
