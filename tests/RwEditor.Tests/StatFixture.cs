using System.Text;
using RwEditor.Core.Mod;
using RwEditor.Core.Model;
using RwEditor.Core.Stats;

namespace RwEditor.Tests;

/// <summary>
/// A tiny synthetic mod for the unit model and the stat patcher, held in RAM (no disk): exact bytes per file plus the model built from them. Covers
/// own / template / all-units.template origins, copyFrom order, @copyFromSection, expressions, buildings, a locked unit, mod areas and a shared own file.
/// </summary>
public sealed class StatFixture
{
    public static readonly UTF8Encoding NoBom = new(false);

    public Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public UnitModel Model { get; private set; } = null!;

    public StatFixture(IDictionary<string, string>? extra = null, IDictionary<string, byte[]>? rawExtra = null)
    {
        foreach (var (k, v) in Files) Bytes[k] = NoBom.GetBytes(v);
        if (extra != null) foreach (var (k, v) in extra) Bytes[k] = NoBom.GetBytes(v);
        if (rawExtra != null) foreach (var (k, v) in rawExtra) Bytes[k] = v;
        Rebuild();
    }

    public void Rebuild()
    {
        var texts = Bytes.Where(kv => ModSnapshot.IsIniLike(kv.Key)).ToDictionary(kv => kv.Key, kv => Decode(kv.Value));
        Model = UnitModel.Build(ModSnapshot.FromTexts(Path.Combine(Path.GetTempPath(), "rwe-fixture"), texts), ModAreas.Embedded, EditorSupport.DefaultLockedGlobs);
    }

    /// <summary>Same decoding as ModSnapshot.Load (UTF-8, BOM dropped).</summary>
    public static string Decode(byte[] b) => Encoding.UTF8.GetString(b.AsSpan(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0));

    public byte[] Read(string rel) => Bytes[ModSnapshot.Norm(rel)];

    public StatEditPlan Plan(string unit, string key, string value, StatEditMode mode = StatEditMode.UnitOnly, string? section = null) =>
        StatPatcher.Plan(Model, StatDictionary.Embedded, new StatEditRequest(unit, key, value, mode, section), Read);

    /// <summary>Applies a plan to the in-RAM bytes and rebuilds the model (what the disk pipeline does, without the disk).</summary>
    public void Commit(StatEditPlan p)
    {
        foreach (var (k, v) in p.NewBytes) Bytes[k] = v;
        Rebuild();
    }

    public static readonly Dictionary<string, string> Files = new()
    {
        ["all-units.template"] = "[core]\nmaxShield: 0\nselfRegenRate: 0\n[projectile_fake]\ndirectDamage: 0\n",
        ["templates/tank_base.ini"] =
            "[core]\ndont_load: true\nname: tankBase\nmaxHp: 1000\nprice: credits = 500, rust = 100\nbuildSpeed: 20s\nmass: 3000\n" +
            "[movement]\nmoveSpeed: 1.5\n[attack]\nmaxAttackRange: 300\n[turret_1]\ndelay: 30\nlimitingRange: 320\n" +
            "[projectile_1]\ndirectDamage: 50\nareaDamage: 10\nareaRadius: 20\nspeed: 4\nlife: 60\n",
        ["units/tankA.ini"] = "[core]\nname: tankA\ncopyFrom: ROOT:/templates/tank_base.ini\nmaxHp: 1200\n[turret_1]\nturnSpeed: 4\n",
        ["units/tankB.ini"] = "[core]\nname: tankB\ncopyFrom: ROOT:/templates/tank_base.ini\n",
        ["units/tankC.ini"] = "[core]\nname: tankC\ncopyFrom: tankA.ini\n",
        ["units/expr.ini"] = "[core]\nname: exprUnit\nmaxHp: ${hpGlobal}\nprice: ${p}\nbuildSpeed: 0\n[movement]\nmoveSpeed: 2\n",
        ["units/tower.ini"] = "[core]\nname: tower\nisBuilding: true\nmaxHp: 5000\n[movement]\nmoveSpeed: 0\n",
        ["units/parent1.ini"] = "[core]\ndont_load: true\nname: p1\nmaxHp: 111\nmass: 1\n",
        ["units/parent2.ini"] = "[core]\ndont_load: true\nname: p2\nmaxHp: 222\n",
        ["units/cfs.ini"] =
            "[core]\nname: cfsUnit\ncopyFrom: parent1.ini, parent2.ini\n" +
            "[template_a]\ndelay: 10\nx: a\n[template_b]\ndelay: 20\ny: b\n" +
            "[turret_1]\n@copyFromSection: template_a, template_b\n[turret_2]\n@copyFromSection: template_a\ndelay: 5\n",
        ["templates/shared_locked.ini"] = "[core]\ndont_load: true\nname: sharedLocked\nmaxHp: 700\n",
        ["myAssets/lockedUnit.ini"] = "[core]\nname: lockedUnit\ncopyFrom: ROOT:/templates/shared_locked.ini\n",
        ["units/tankD.ini"] = "[core]\nname: tankD\ncopyFrom: ROOT:/templates/shared_locked.ini\n",
        ["units/dontload.ini"] = "[core]\nname: notLoaded\ndont_load: true\n",
        ["units/noname.ini"] = "[core]\nmaxHp: 1\n",
        ["units/thing.template"] = "[core]\nname: notAUnitTemplate\n",
        ["units/dupA.ini"] = "[core]\nname: dupName\nmaxHp: 1\n",
        ["units/dupB.ini"] = "[core]\nname: dupName\nmaxHp: 2\n",
        ["donors/fe/gen.ini"] = "[core]\nname: feGen\nmaxHp: 10\n",
        ["donors/moo/a.ini"] = "[core]\nname: mooA\nmaxHp: 10\n",
        ["donors/vte/b.ini"] = "[core]\nname: vteB\nmaxHp: 10\n",
        ["ACV/acvMainBldg.ini"] = "[core]\nname: acvMainBldg\nmaxHp: 10\n",
        ["units/ranged.ini"] = "[core]\nname: ranged\nmaxHp: 10\n[attack]\nmaxAttackRange: 200\n[turret_1]\nlimitingRange: 250\nlimitingMinRange: 50\nenergyUsage: 0\n[turret_2]\nlimitingRange: 190\n",
    };
}
