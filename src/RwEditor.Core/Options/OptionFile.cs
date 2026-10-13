using System.Text;
using System.Text.RegularExpressions;

namespace RwEditor.Core.Options;

/// <summary>
/// The lines of one ini file, kept so that putting them back together gives exactly the same bytes (UTF-8 with or without BOM, LF or CRLF per line, a
/// final line without a break). Only whole lines are ever replaced, so line numbers never move. Reading is strict: a file that does not survive
/// the round trip byte for byte is refused rather than rewritten.
/// </summary>
public sealed class OptionFile
{
    static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);
    static readonly Regex Header = new(@"^\s*\[(?<n>[^\]]+)\]\s*$", RegexOptions.CultureInvariant);

    public string Rel { get; }
    readonly bool _bom;
    readonly List<string> _lines;
    readonly List<bool> _cr;

    OptionFile(string rel, bool bom, List<string> lines, List<bool> cr) { Rel = rel; _bom = bom; _lines = lines; _cr = cr; }

    public int Count => _lines.Count;
    public string this[int index] { get => _lines[index]; set => _lines[index] = value; }

    public static OptionFile Parse(string rel, byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try { text = Strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { throw new EditorRefusalException("OPTION_FILE_ENCODING", $"{rel} is not valid UTF-8, so the editor will not rewrite it"); }
        var lines = new List<string>();
        var cr = new List<bool>();
        foreach (var piece in text.Split('\n'))
        {
            bool c = piece.EndsWith('\r');
            lines.Add(c ? piece[..^1] : piece);
            cr.Add(c);
        }
        var f = new OptionFile(rel, bom, lines, cr);
        if (!f.ToBytes().AsSpan().SequenceEqual(bytes))
            throw new EditorRefusalException("OPTION_FILE_ROUNDTRIP", $"{rel} cannot be rewritten byte for byte (unusual line endings), so the editor will not touch it");
        return f;
    }

    public OptionFile Clone() => new(Rel, _bom, _lines.ToList(), _cr.ToList());

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i]);
            if (_cr[i]) sb.Append('\r');
            if (i < _lines.Count - 1) sb.Append('\n');
        }
        var body = new UTF8Encoding(false).GetBytes(sb.ToString());
        return _bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    /// <summary>Indexes of the lines "key: value" inside [section] (comment lines are not lines of the section).</summary>
    public IReadOnlyList<int> FindAll(string section, string key)
    {
        var rx = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*:", RegexOptions.CultureInvariant);
        var hits = new List<int>();
        string? cur = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            string l = _lines[i];
            if (l.TrimStart().StartsWith('#')) continue;
            var h = Header.Match(l);
            if (h.Success) { cur = h.Groups["n"].Value; continue; }
            if (cur == section && rx.IsMatch(l)) hits.Add(i);
        }
        return hits;
    }

    /// <summary>The part of a "key: value" line before the value, the value, and what trails it.</summary>
    public static (string Pre, string Value, string Post) Split(string line, string key)
    {
        var m = Regex.Match(line, @"^(?<pre>\s*" + Regex.Escape(key) + @"\s*:\s*)(?<v>.*?)(?<post>\s*)$", RegexOptions.CultureInvariant);
        return m.Success ? (m.Groups["pre"].Value, m.Groups["v"].Value, m.Groups["post"].Value) : (line, "", "");
    }

    /// <summary>The value of the one line "key: value" of a section, or null when there is not exactly one.</summary>
    public string? Value(string section, string key)
    {
        var hits = FindAll(section, key);
        return hits.Count == 1 ? Split(_lines[hits[0]], key).Value : null;
    }
}
