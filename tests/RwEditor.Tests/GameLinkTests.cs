using RwEditor.Core;
using RwEditor.Core.Mod;
using RwEditor.Core.Setup;
using Xunit;

namespace RwEditor.Tests;

/// <summary>The link between an editor copy and the game (owner's E3 test: edits did not show in the game because the copy was outside mods\units).</summary>
public sealed class GameLinkTests
{
    static void Title(TestMod m, string title) => m.Put("mod-info.txt", $"[mod]\r\ntitle: {title}\r\nminVersion: v1.15\r\n");

    static string GameOf(TestMod m)
    {
        string game = Path.Combine(m.BaseFolder, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods", "units"));
        return game;
    }

    [Fact]
    public void A_copy_outside_the_game_mods_folder_is_not_linked_and_says_why()
    {
        using var m = new TestMod();
        Title(m, "[Custom x] Depache's Mod Tribute");
        var st = GameLink.Check(m.Root, GameOf(m));
        Assert.False(st.Linked);
        Assert.Contains(st.Problems, p => p.Contains("does not see this copy", StringComparison.Ordinal));
        Assert.Empty(st.Steps);
    }

    [Fact]
    public void A_copy_inside_mods_units_is_linked_and_lists_the_other_copies_to_switch_off()
    {
        using var m = new TestMod(subPath: Path.Combine("game", "mods", "units", "DepacheTribute_Custom_x"));
        Title(m, "[Custom x] Depache's Mod Tribute");
        string game = Path.Combine(m.BaseFolder, "game");
        string other = Path.Combine(game, "mods", "units", "OriginalTribute");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "mod-info.txt"), "[mod]\r\ntitle: Depache's Mod Tribute\r\n");
        string unrelated = Path.Combine(game, "mods", "units", "SomethingElse");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "mod-info.txt"), "[mod]\r\ntitle: Another mod\r\n");
        var st = GameLink.Check(m.Root, game);
        Assert.True(st.Linked);
        Assert.Empty(st.Problems);
        Assert.Single(st.OtherCopiesOfTheMod);
        Assert.Contains("OriginalTribute", st.OtherCopiesOfTheMod[0], StringComparison.Ordinal);
        Assert.Contains(st.Steps, s => s.Contains("Switch ON", StringComparison.Ordinal));
        Assert.Contains(st.Steps, s => s.Contains("OriginalTribute", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_game_folder_is_reported_not_guessed()
    {
        using var m = new TestMod();
        var st = GameLink.Check(m.Root, null);
        Assert.False(st.Linked);
        Assert.Contains(st.Problems, p => p.Contains("game folder is not known", StringComparison.Ordinal));
    }

    [Fact]
    public void Copy_into_game_copies_everything_byte_for_byte_leaves_the_source_alone_and_never_overwrites()
    {
        using var m = new TestMod();
        Title(m, "[Custom x] Depache's Mod Tribute");
        m.Put("rweditor-settings.json", "{}\n");
        string game = GameOf(m);
        var before = Directory.EnumerateFiles(m.Root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(m.Root, f), f => File.ReadAllBytes(f));

        string made = GameLink.CopyIntoGame(m.Root, game);
        Assert.StartsWith(Path.Combine(game, "mods", "units"), made, StringComparison.OrdinalIgnoreCase);
        Assert.True(GameLink.Check(made, game).Linked);
        foreach (var (rel, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(made, rel)));
        Assert.Equal(before.Count, Directory.EnumerateFiles(made, "*", SearchOption.AllDirectories).Count());
        Assert.True(EditorCopyMarker.Exists(made));
        foreach (var (rel, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(m.Root, rel)));   // source unchanged

        string second = GameLink.CopyIntoGame(m.Root, game);   // a second one is numbered, the first is never touched
        Assert.NotEqual(made, second);
        Assert.EndsWith("_2", second, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(game, "mods", "units"), "*" + CopyMaker.PartialSuffix + "*"));
    }

    [Fact]
    public void Copy_into_game_refuses_a_folder_that_is_not_an_editor_copy_and_a_missing_mods_folder()
    {
        using var plain = new TestMod(withMarker: false);
        Assert.Throws<EditorRefusalException>(() => GameLink.CopyIntoGame(plain.Root, GameOf(plain)));
        using var m = new TestMod();
        string noMods = Path.Combine(m.BaseFolder, "nogame");
        Directory.CreateDirectory(noMods);
        Assert.Equal("GAME_MODS_MISSING", Assert.Throws<EditorRefusalException>(() => GameLink.CopyIntoGame(m.Root, noMods)).Code);
    }
}
