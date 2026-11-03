using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 358, Part 1 sweep over the shipped data. Every <c>BBSpellEffectCreate</c> of the spell, item and character scripts whose <c>PosVar</c> or
/// <c>TargetPosVar</c> names the caster (<c>Owner</c>) or the target (<c>Target</c>) runs once through the production handler, and the effect it creates
/// is placed at that unit: its source is the <c>PosVar</c> unit's position, its target point the <c>TargetPosVar</c> unit's. Other names (script points,
/// units this probe does not bind) are outside the sweep; the output counts how many calls ran.
/// </summary>
public static class BbEffectPositionChecks
{
    [Check("scripting.bb-effect-create-positions-follow-the-unit-they-name")]
    public static void ShippedUnitOperandsSetTheirPositions()
    {
        using var match = new Match("Annie");
        var failures = new List<string>();
        var sweep = 0;
        var reached = 0;
        foreach (var block in ShippedEffectCreates())
        {
            var posName = block.Text("PosVar");
            var targetName = block.Text("TargetPosVar");
            if (!IsBound(posName) && !IsBound(targetName)) continue;
            sweep++;
            var before = match.Log.EffectCalls.Count;
            RunBlock(match, block);
            var fresh = match.Log.EffectCalls.Skip(before).ToArray();
            if (fresh.Length == 0) continue;
            reached++;
            if (IsBound(posName)) Expect(failures, block, $"PosVar {posName}", fresh[0].Source, Operand(match, posName!));
            if (IsBound(targetName)) Expect(failures, block, $"TargetPosVar {targetName}", fresh[0].TargetPoint, Operand(match, targetName!));
        }
        TestAssert.True(reached >= 60, $"the sweep ran {sweep} shipped calls and {reached} created an effect");
        TestAssert.True(failures.Count == 0,
            $"{failures.Count} of {reached} effect creates are not at the unit they name: {string.Join(" | ", failures.Take(10))}");
    }

    /// <summary>
    /// Batch 364 (item 4): <c>OrientTowardsVar</c> names the point or unit a create turns to (Cassiopeia's death dust, Galio's wind tunnel rune and
    /// Nocturne's frost path name a point; Oriana's ghosts and Riven's trails name the owner). The 0x8C direction is the flat unit vector from the
    /// effect to that point, which the handler used to leave at zero. Reconstruction: the client's use of the vector is not decoded.
    /// </summary>
    [Check("scripting.bb-effect-create-orients-toward-its-orientation-point")]
    public static void OrientationPointSetsTheCreateDirection()
    {
        using var match = new Match("Annie");
        var block = BbTest.Block("BBSpellEffectCreate", ["EffectName", "UrgotSwapDrip.troy", "PosVar", "Owner", "OrientTowardsVar", "Target"]);
        var before = match.Log.EffectCalls.Count;
        RunBlock(match, block);
        var fresh = match.Log.EffectCalls.Skip(before).ToArray();
        TestAssert.Equal(1, fresh.Length, "the create runs once");
        TestAssert.True(fresh[0].Direction is not null, "the 0x8C carries a direction toward the orientation point, not the zero vector");
        var dx = match.Foe.Position.X - match.Hero.Position.X;
        var dz = match.Foe.Position.Z - match.Hero.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var direction = fresh[0].Direction!.Value;
        TestAssert.Near(dx / length, direction.X, 1e-6, "the X component is the unit vector toward the foe");
        TestAssert.Near(0.0, direction.Y, 1e-6, "the vector is flat, like the facing of 0x53");
        TestAssert.Near(dz / length, direction.Z, 1e-6, "the Z component is the unit vector toward the foe");
    }

    private static bool IsBound(string? name) => name is "Owner" or "Target";

    private static Position2 Operand(Match match, string name) => name == "Owner" ? match.Hero.Position : match.Foe.Position;

    private static void Expect(List<string> failures, BbBlock block, string what, Position2? actual, Position2 expected)
    {
        if (actual is { } at && Math.Abs(at.X - expected.X) < 1e-6 && Math.Abs(at.Z - expected.Z) < 1e-6) return;
        failures.Add($"{block.Text("EffectName")} {what}: got {actual}, expected {expected}");
    }

    /// <summary>Runs one block as the SelfExecute of a throw-away spell of the hero, with the foe as its target (<c>Target</c>).</summary>
    private static void RunBlock(Match match, BbBlock block)
    {
        var script = new BbScript("B358Sweep", "Spells/B358Sweep.luaobj", [], [new BbEventTable("SelfExecute", [block])], []);
        var position = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
        match.Module.Router!.RunSelfExecute(script, match.Hero, 0, 1, 0.0, position, position, match.Foe);
    }

    /// <summary>Every block of every shipped script that calls <c>BBSpellEffectCreate</c>, in the spell, item and character folders.</summary>
    private static IEnumerable<BbBlock> ShippedEffectCreates()
    {
        foreach (var folder in new[] { "Spells", "Items", "Characters" })
        {
            var root = GameData.Default.Paths.Data(folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.luaobj", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (!Encoding.Latin1.GetString(File.ReadAllBytes(file)).Contains("BBSpellEffectCreate", StringComparison.Ordinal)) continue;
                var relative = folder + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                var script = BbScriptLoader.Load(GameData.Default.Paths, relative).Script;
                if (script is null) continue;
                foreach (var block in script.Events.SelectMany(hook => hook.Blocks).SelectMany(Flatten))
                    if (block.Function == "BBSpellEffectCreate") yield return block;
            }
        }
    }
}
