using System.Text;
using RwEditor.Core;
using RwEditor.Core.Ini;
using Xunit;

namespace RwEditor.Tests;

/// <summary>The lossless ini text model: byte-exact round trips and value-only patches.</summary>
public class IniTextTests
{
    static byte[] B(string s, bool bom = false) => (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(new UTF8Encoding(false).GetBytes(s)).ToArray();

    public static IEnumerable<object[]> Samples() => new[]
    {
        new object[] { "[core]\nname: a\nmaxHp: 100\n", false },
        new object[] { "[core]\r\nname: a\r\nmaxHp: 100\r\n", true },
        new object[] { "[core]\r\nname: a\nmaxHp: 100", false },          // mixed endings, no final line break
        new object[] { "[core]\rname: a\rmaxHp: 100\r", false },         // old Mac endings
        new object[] { "", false },
        new object[] { "\n\n", true },
        new object[] { "# only a comment", false },
        new object[] { "[core]\nname: \"\"\"multi\nline\"\"\"\nmaxHp:  77   \n  # indented comment\n\tmoveSpeed:1.5\n", false },
        new object[] { "[core]\nname: ünïcödé ✓ 日本\nmaxHp: 5\n", true },
    };

    [Theory, MemberData(nameof(Samples))]
    public void Parse_then_ToBytes_is_byte_identical(string text, bool bom)
    {
        var bytes = B(text, bom);
        var t = IniText.Parse(bytes, "x.ini");
        Assert.Equal(bytes, t.ToBytes());
        Assert.Equal(bom, t.HasBom);
        Assert.Equal(text, t.Text);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Line_numbers_match_IniDocument(string text, bool bom)
    {
        var t = IniText.Parse(B(text, bom), "x.ini");
        var doc = IniDocument.Parse(t.Text, "x.ini", IniDialect.Catalog);
        foreach (var p in doc.Sections.SelectMany(s => s.Pairs))
        {
            var span = t.GetValueSpan(p.Line - 1);
            Assert.NotNull(span);
            Assert.Equal(p.Key, span!.Key);
            if (!p.Value.Contains('\n')) Assert.Equal(p.Value, span.Value);
        }
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        var e = Assert.Throws<EditorRefusalException>(() => IniText.Parse(new byte[] { (byte)'a', 0xFF, 0xFE, (byte)'\n' }, "bad.ini"));
        Assert.Equal("FILE_NOT_UTF8", e.Code);
    }

    [Fact]
    public void WithValue_changes_only_the_value_text_and_keeps_spacing_and_endings()
    {
        var bytes = B("[core]\r\nname: a\r\n  maxHp:   100   \r\nprice: 5\r\n", bom: true);
        var t = IniText.Parse(bytes, "x.ini");
        var p = t.WithValue(2, "250");
        Assert.Equal(B("[core]\r\nname: a\r\n  maxHp:   250   \r\nprice: 5\r\n", bom: true), p.ToBytes());
        // and back: identical to the original
        Assert.Equal(bytes, p.WithValue(2, "100").ToBytes());
    }

    [Fact]
    public void Insert_and_remove_round_trip_with_and_without_a_final_line_break()
    {
        foreach (var src in new[] { "[core]\nname: a\n", "[core]\nname: a", "[core]\r\nname: a\r\n[x]\r\ny: 1" })
        {
            var t = IniText.Parse(B(src), "x.ini");
            int last = t.LastContentLine;
            var (ins, eolAdded) = t.WithLineInserted(last, "maxHp: 9");
            Assert.Equal(!src.EndsWith('\n'), eolAdded);
            Assert.Contains("maxHp: 9", ins.Text);
            var back = ins.WithLineRemoved(last + 1, eolAdded);
            Assert.Equal(B(src), back.ToBytes());
        }
    }

    [Fact]
    public void Inserted_line_takes_the_terminator_of_the_line_before()
    {
        var t = IniText.Parse(B("[core]\r\nname: a\r\n"), "x.ini");
        var (ins, _) = t.WithLineInserted(0, "maxHp: 9");
        Assert.Equal(B("[core]\r\nmaxHp: 9\r\nname: a\r\n"), ins.ToBytes());
    }

    [Fact]
    public void Values_with_line_breaks_or_padding_are_rejected()
    {
        var t = IniText.Parse(B("[core]\nmaxHp: 1\n"), "x.ini");
        Assert.Throws<ArgumentException>(() => t.WithValue(1, "1\n2"));
        Assert.Throws<ArgumentException>(() => t.WithValue(1, " 2"));
        Assert.Throws<ArgumentException>(() => t.WithLineInserted(0, "a\r\nb"));
    }

    [Fact]
    public void Non_pair_lines_have_no_value_span()
    {
        var t = IniText.Parse(B("[core]\n# c: x\n\nnovalue\n:x\n"), "x.ini");
        for (int i = 0; i < t.Lines.Count; i++) Assert.Null(t.GetValueSpan(i));
    }
}
