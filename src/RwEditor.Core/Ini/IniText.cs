using System.Text;

namespace RwEditor.Core.Ini;

/// <summary>One physical line of an ini file: its text without the line terminator, and the terminator itself ("\r\n", "\n", "\r" or "" for a last line without one).</summary>
public sealed record IniLine(string Text, string Eol);

/// <summary>Where the value of a "key: value" line sits inside the line text (character offsets into <see cref="IniLine.Text"/>).</summary>
public sealed record ValueSpan(int LineIndex, string Key, int ValueStart, int ValueLength, string Value);

/// <summary>
/// Lossless model of one ini file for patching (DESIGN_EDITOR 1.2 "patches are byte-careful"): the exact bytes are kept as (BOM flag, lines with their own
/// terminators). <see cref="ToBytes"/> of an unmodified file gives the input bytes back exactly; <see cref="Parse"/> refuses any file for which that is
/// not true (invalid UTF-8, for example), so a patch can never silently re-encode a file. Every operation returns a NEW instance; nothing is written here.
/// Line splitting matches <see cref="IniDocument"/> ("\r\n", then lone "\r", then "\n"), so IniPair.Line (1-based) = line index + 1.
/// </summary>
public sealed class IniText
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    IniText(string relPath, bool hasBom, IReadOnlyList<IniLine> lines) { RelPath = relPath; HasBom = hasBom; Lines = lines; }

    public string RelPath { get; }
    public bool HasBom { get; }
    public IReadOnlyList<IniLine> Lines { get; }

    /// <summary>The decoded text without the BOM (what <see cref="IniDocument.Parse"/> reads).</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var l in Lines) sb.Append(l.Text).Append(l.Eol);
            return sb.ToString();
        }
    }

    /// <summary>The terminator used most often in the file ("\n" for a file without any).</summary>
    public string DominantEol =>
        Lines.Where(l => l.Eol.Length > 0).GroupBy(l => l.Eol).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? "\n";

    public static IniText Parse(byte[] bytes, string relPath)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == Bom[0] && bytes[1] == Bom[1] && bytes[2] == Bom[2];
        string text;
        try { text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException e) { throw new EditorRefusalException("FILE_NOT_UTF8", $"{relPath} is not valid UTF-8 ({e.Message}); the editor will not patch it."); }
        var lines = Split(text);
        var t = new IniText(relPath, bom, lines);
        if (!t.ToBytes().AsSpan().SequenceEqual(bytes))
            throw new EditorRefusalException("FILE_NOT_LOSSLESS", $"{relPath} cannot be read and written back byte for byte; the editor will not patch it.");
        return t;
    }

    static List<IniLine> Split(string text)
    {
        var lines = new List<IniLine>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                bool crlf = i + 1 < text.Length && text[i + 1] == '\n';
                lines.Add(new IniLine(text.Substring(start, i - start), crlf ? "\r\n" : "\r"));
                if (crlf) i++;
                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(new IniLine(text.Substring(start, i - start), "\n"));
                start = i + 1;
            }
        }
        // IniDocument splits "a\n" into ["a", ""]: the trailing empty piece is a line too, but it has no text and no terminator, so it adds no bytes.
        lines.Add(new IniLine(text.Substring(start), ""));
        return lines;
    }

    public byte[] ToBytes()
    {
        var body = StrictUtf8.GetBytes(Text);
        if (!HasBom) return body;
        var all = new byte[body.Length + 3];
        Bom.CopyTo(all, 0);
        body.CopyTo(all, 3);
        return all;
    }

    /// <summary>The value span of a "key: value" line, or null for a blank, comment, header or non-pair line. Same key/value rule as IniDocument (first ':').</summary>
    public ValueSpan? GetValueSpan(int lineIndex)
    {
        string raw = Lines[lineIndex].Text;
        string trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#' || (trimmed[0] == '[' && trimmed.EndsWith(']'))) return null;
        int colon = raw.IndexOf(':');
        if (colon <= 0 || raw.Substring(0, colon).Trim().Length == 0) return null;
        string key = raw.Substring(0, colon).Trim();
        int vs = colon + 1;
        while (vs < raw.Length && char.IsWhiteSpace(raw[vs])) vs++;
        int ve = raw.Length;
        while (ve > vs && char.IsWhiteSpace(raw[ve - 1])) ve--;
        return new ValueSpan(lineIndex, key, vs, ve - vs, raw.Substring(vs, ve - vs));
    }

    /// <summary>Replaces only the value text of one "key: value" line; everything else in the file (other lines, terminators, BOM, indentation) is kept.</summary>
    public IniText WithValue(int lineIndex, string newValue)
    {
        CheckValueText(newValue);
        var span = GetValueSpan(lineIndex) ?? throw new InvalidOperationException($"{RelPath} line {lineIndex + 1} is not a key: value line");
        string raw = Lines[lineIndex].Text;
        string text = raw.Substring(0, span.ValueStart) + newValue + raw.Substring(span.ValueStart + span.ValueLength);
        var lines = Lines.ToList();
        lines[lineIndex] = lines[lineIndex] with { Text = text };
        return new IniText(RelPath, HasBom, lines);
    }

    /// <summary>
    /// Inserts one line after <paramref name="afterLineIndex"/>. The new line takes the terminator of the line it follows; if that line had none (it was the
    /// last line of the file), it gets the file's dominant terminator and the new line becomes the last line without one, so "file ends without a newline"
    /// is preserved. Returns the new instance and whether a terminator had to be added to the previous line.
    /// </summary>
    public (IniText Text, bool EolAdded) WithLineInserted(int afterLineIndex, string lineText)
    {
        CheckLineText(lineText);
        var lines = Lines.ToList();
        var prev = lines[afterLineIndex];
        bool eolAdded = false;
        string eol = prev.Eol;
        if (eol.Length == 0)
        {
            // the previous line is the last line of a file that does not end with a terminator
            eolAdded = true;
            lines[afterLineIndex] = prev with { Eol = DominantEol };
            lines.Insert(afterLineIndex + 1, new IniLine(lineText, ""));
        }
        else
        {
            lines.Insert(afterLineIndex + 1, new IniLine(lineText, eol));
        }
        return (new IniText(RelPath, HasBom, lines), eolAdded);
    }

    /// <summary>Removes one line. When <paramref name="restoreNoEolOnPrevious"/> is set (undo of an insert that added a terminator), the previous line loses its terminator again.</summary>
    public IniText WithLineRemoved(int lineIndex, bool restoreNoEolOnPrevious)
    {
        var lines = Lines.ToList();
        lines.RemoveAt(lineIndex);
        if (restoreNoEolOnPrevious)
        {
            if (lineIndex != lines.Count) throw new InvalidOperationException($"{RelPath}: the removed line was not the last line; cannot restore the missing terminator");
            lines[lineIndex - 1] = lines[lineIndex - 1] with { Eol = "" };
        }
        return new IniText(RelPath, HasBom, lines);
    }

    /// <summary>Index of the last physical line that holds text (skips the empty trailing piece after a final terminator).</summary>
    public int LastContentLine
    {
        get
        {
            int i = Lines.Count - 1;
            if (i > 0 && Lines[i].Text.Length == 0 && Lines[i].Eol.Length == 0) i--;
            return i;
        }
    }

    static void CheckValueText(string v)
    {
        if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("a value cannot contain a line break");
        if (v != v.Trim()) throw new ArgumentException("a value cannot start or end with white space");
    }

    static void CheckLineText(string v)
    {
        if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("a line cannot contain a line break");
    }
}
