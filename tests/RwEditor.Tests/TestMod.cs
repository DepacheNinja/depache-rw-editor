using System.Text;
using RwEditor.Core.Mod;
using RwEditor.Core.Settings;

namespace RwEditor.Tests;

/// <summary>
/// A tiny synthetic mod (no mod art, no real unit files) laid out like the shipped mod's editor contract: the locked anchor and selector with the C-026
/// include slot, the two shipped-empty generated files, a few unit inis, and an editor-support.json computed for exactly these files. Each instance lives in
/// its own temp folder (outside any git tree) and is deleted on Dispose.
/// </summary>
public sealed class TestMod : IDisposable
{
    static readonly UTF8Encoding NoBom = new(false);

    public string Root { get; }
    public string BackupRoot { get; }
    readonly string _base;

    /// <param name="withMarker">Write the editor-copy marker (rweditor-copy.json, PH-071) as "Make my copy" would; needs <paramref name="withSupport"/>.</param>
    public TestMod(string? subPath = null, bool withSupport = true, IDictionary<string, string>? extraFiles = null, bool withMarker = true)
    {
        _base = Path.Combine(Path.GetTempPath(), "rwe-tests", Guid.NewGuid().ToString("N"));
        Root = subPath == null ? Path.Combine(_base, "mod") : Path.Combine(_base, subPath);
        BackupRoot = Path.Combine(_base, "backups");
        Directory.CreateDirectory(Root);
        foreach (var (rel, text) in Files) Put(rel, text);
        if (extraFiles != null) foreach (var (rel, text) in extraFiles) Put(rel, text);
        if (withSupport) Put(EditorSupport.FileName, EditorSupport.BuildJson(Root, "9.9.9"));
        if (withSupport && withMarker)
            Put(EditorCopyMarker.FileName, EditorCopyMarker.Create("test copy", Path.Combine(_base, "installed-mod"), EditorSupport.Load(Root), "0.0.0", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)).ToJson());
    }

    /// <summary>The temp folder that holds this test mod (and its backups); deleted on Dispose.</summary>
    public string BaseFolder => _base;

    public void Put(string rel, string text)
    {
        string full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, NoBom);
    }

    public string Read(string rel) => File.ReadAllText(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);
    public byte[] Bytes(string rel) => File.ReadAllBytes(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));

    public ModContext Context() => ModContext.Load(Root);

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------------------------------------------------------- files
    public const string ShippedAnchorGen = "[core]\ndont_load: true\n#shipped empty (test fixture)\n";
    public const string ShippedSelectorGen = "[core]\ndont_load: true\n#shipped empty (test fixture)\n";

    static string Option(string tag, string name, int typeId, int pos) => $"""
        [action_{tag}On]
        @copyFromSection: template_turnOnOption
        @define actionTag: {tag}
        @define actionName: {name}
        @define actionNameZh: {name}
        @define typeId: {typeId}
        pos: {pos}
        description:- {name}
        [action_{tag}Off]
        @copyFromSection: action_{tag}On, template_turnOffOption
        [action_{tag}Msg]
        @copyFromSection: action_{tag}On, template_notificationAction

        """;

    static readonly Dictionary<string, string> Files = new()
    {
        ["mod-info.txt"] = "[mod]\ntitle: test mod\n",
        ["all-units.template"] = "[core]\nshowInEditor: true\n",
        ["myAssets/dummyUniqueUnit.ini"] = "[core]\ndont_load: true\nname: dummyUniqueUnit\n[hiddenAction_unique]\nautoTriggerOnEvent: created\ndeleteSelf: true\n",
        ["myAssets/managerUnit.ini"] = "[core]\nname: managerUnit\nradius: 10\n",
        ["myAssets/extraStartAnchor.ini"] = """
            [core]
            name: extraStartAnchor
            copyFrom: dummyUniqueUnit.ini, ROOT:/editorGenerated/anchorGroups.ini
            onNewMapSpawn: spawnPoint_eachActiveTeam
            #comment naming xGrpWait and ug01 is fine
            [hiddenAction_extraStartACV]
            id:extraACV
            autoTrigger: if self.timeAlive > 1 and numberOfUnitsInNeutralTeam(withTag='gameOptionsConfirmed') > 0 and numberOfUnitsInNeutralTeam(withTag='extraACV') > 0 and not self.globalTeamTags(includes="extraACVDone")
            addGlobalTeamTags: extraACVDone
            spawnUnits: hoverBuilder(offsetX=100, offsetY=0)
            [hiddenAction_cleanup]
            autoTrigger: if numberOfUnitsInNeutralTeam(withTag='gameOptionsConfirmed') > 0 and not self.tags(includes="xGrpWait") or self.timeAlive > 150
            deleteSelf: true

            """,
        ["startingOptions/gameOptionSelector.ini"] = """
            [core]
            name: gameOptionSelector
            copyFrom: ROOT:/editorGenerated/selectorGroups.ini
            [action_confirm]
            requireConditional: if not memory.confirmed
            pos: 19000
            [template_turnOnOption]
            @define actionTag: something
            @define actionName: something
            @define actionNameZh: something
            @define typeId: 1
            isVisible:${action_confirm.requireConditional} and not self.tags(includes="${actionTag}") and not self.tags(includes="xh${actionTag}")
            pos: 8000
            id:${actionTag}on
            [template_turnOffOption]
            isVisible:${action_confirm.requireConditional} and self.tags(includes="${actionTag}") and not self.tags(includes="xh${actionTag}")
            id:${actionTag}Off
            [template_notificationAction]
            id:${actionTag}Msg

            """
            + Option("richStart", "Rich start", 1, 8000)
            + Option("fairAI", "Fair AI", 1, 8000)
            + Option("extraACV", "Extra ACV", 4, 8001)
            + Option("extraSCB", "Extra SCB", 4, 8001)
            + Option("extraSpdr", "Extra Spider", 4, 8001)
            + Option("extraDrop", "Extra Drop", 4, 8001)
            + Option("extraHuman", "Extra units human players only", 4, 8003),
        [EditorSupport.AnchorGenerated] = ShippedAnchorGen,
        [EditorSupport.SelectorGenerated] = ShippedSelectorGen,
        ["templates/air.ini"] = "[core]\nname: airTemplate\ndont_load: true\nradius: 30\n",
        ["units/expHoverTank.ini"] = "[core]\nname: expHoverTank\ndisplayText: Experimental Hovertank\nradius: 40\n",
        ["units/valkyrie.ini"] = "[core]\nname: Valkyrie\ncopyFrom: ROOT:/templates/air.ini\ndisplayText: Valkyrie Frigate\n",
        ["units/hoverBuilder.ini"] = "[core]\nname: hoverBuilder\ndisplayText: Hovercraft Engineer\nradius: 15\n",
        ["units/heliBase.ini"] = "[core]\nname: heliBase\ndisplayText: Heli Battlebase\nradius: 50\n",
        ["units/wall.ini"] = "[core]\nname: wallBlock\ndisplayText: Wall: Mk II\nisBuilding: true\nradius: 20\n",
        ["units/smallBot.ini"] = "[core]\nname: smallBot\ndisplayText: Small bot\nradius: 10\n",
        ["units/bigBase.ini"] = "[core]\nname: bigBase\ndisplayText: Big base\nradius: 200\n",
        ["units/noRadius.ini"] = "[core]\nname: noRadius\ndisplayText: No radius\nradius: ${someGlobal}\n",
    };

    // ---------------------------------------------------------------------------------------------------------------- settings
    /// <summary>The DESIGN_EDITOR 2.4 sample: "Hover strike" (2x expHoverTank + Valkyrie, follow the toggle, on by default), "Builders" (humans only), ACV hidden.</summary>
    public static EditorSettings Sample2() => new()
    {
        SpawnGroups =
        {
            new SpawnGroup
            {
                Slot = 1, Name = "Hover strike", Mode = GroupMode.FollowToggle, DefaultOn = true,
                Units =
                {
                    new GroupUnit { Unit = "expHoverTank", Count = 2, Offsets = new() { new(300, -60), new(300, 10) } },
                    new GroupUnit { Unit = "Valkyrie", Count = 1, Offsets = new() { new(370, -25) } },
                },
            },
            new SpawnGroup
            {
                Slot = 2, Name = "Builders", Mode = GroupMode.HumansOnly,
                Units =
                {
                    new GroupUnit { Unit = "hoverBuilder", Count = 1, Offsets = new() { new(-300, 40) } },
                    new GroupUnit { Unit = "heliBase", Count = 1, Offsets = new() { new(-380, -30) } },
                },
            },
        },
        StartOptions = new(StringComparer.Ordinal) { ["extraACV"] = OptionState.Hidden },
    };

    /// <summary>Directory holding the golden files (copied next to the test assembly).</summary>
    public static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Golden");
    public static string Golden(string name) => File.ReadAllText(Path.Combine(GoldenDir, name), Encoding.UTF8);
}
