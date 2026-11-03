using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Pets;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Reconstruction (19 October 2026): the hand-written port refused a Bushwhack at a wall cell; the shipped script spawns the trap at the cast point,
    // and the engine's summon floor (the owner decision for summons, now also for BBSpawnMinion) moves a blocked aim to the closest legal floor.
    [Check("spells-live-nidalee-trap-wall-placement-is-projected")]
    public static void BushwhackAtAWallCellPlacesTheTrapOnTheClosestFloor()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.Position = new Position2(6913, 6635);
            var grid = runtime.Navigation;
            IEnumerable<Entity> Traps() => runtime.World.Entities.Values.Where(unit =>
                unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == hero.NetId);
            TestAssert.True(grid.IsMovementWalkable(hero.Position));
            var range = GameData.Default.Spell("Bushwhack").CastRange;
            var blocked = Enumerable.Range(0, grid.CountX * grid.CountY)
                .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX)))
                .First(point =>
                {
                    var dx = point.X - hero.Position.X;
                    var dz = point.Z - hero.Position.Z;
                    return dx * dx + dz * dz < range * range && !grid.IsMovementWalkable(point);
                });
            var mana = hero.Mana;
            match.CastGround(0, blocked, slot: 1);
            runtime.Tick(.5);
            TestAssert.True(hero.Mana < mana, $"{team}: the cast is paid");
            TestAssert.Equal(1, Traps().Count(), $"{team}: one trap");
            TestAssert.True(grid.IsMovementWalkable(Traps().Single().Position), $"{team}: the trap stands on walkable ground");
        }
    }

    [Check("spells-live-nidalee-trap-percent-modifier-wire")]
    public static void BushwhackPercentModifierIsAdditive()
    {
        var packet = CombatPackets.MinionVisionPercentModifier(0x40030000, -1);
        TestAssert.Equal((byte)0xCC, packet.Plaintext.Span[0]);
        TestAssert.Equal((byte)8, packet.Plaintext.Span[10]);
        TestAssert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]));
        TestAssert.Equal(-1f, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[19..]));
        TestAssert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(
            CombatPackets.MinionVisionPercentModifier(0x40030000, 0).Plaintext.Span[19..]));
        foreach (var invalid in new[] { -1.01f, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            TestAssert.Throws<ArgumentOutOfRangeException>(
                () => CombatPackets.MinionVisionPercentModifier(0x40030000, invalid));
    }

    [Check("spells-live-nidalee-placement-vision-flash")]
    public static void BushwhackPlacementMakesNoVisionFlash()
    {
        // Batch 222 (restated after batch 221): Bushwhack.luaobj's BBSpawnMinion passes VisibilitySize 0.0 and no sight spell (batch 221,
        // .artifacts/b221/bushwhack-dis.txt), so the trap's placement makes no timed vision bubble, no team sight and no wire add. The flash
        // this check used to read was the hand-written module's; the shipped script places none.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team, ally: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            // Every map turret's own perception bubble is the map's, not the cast's (see batch 159 step 2).
            var mapBubbles = runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
            caster.Position = new Position2(6913, 6635);
            match.Cast(0, 1, caster);
            for (var step = 0; step < 400 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == caster.NetId); step++)
                runtime.Spells.Tick(.001);
            runtime.Delivery.Flush();
            var trap = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == caster.NetId);
            var bubbleId = trap.NetId ^ 0x2000_0000u;
            TestAssert.Equal(0, runtime.World.TimedVisionBubbles.Count(bubble => !mapBubbles.Contains(bubble.BubbleId)),
                "the trap's placement makes no timed vision bubble (VisibilitySize 0.0)");
            TestAssert.Equal(0.0, VisibilityRules.TerrainVisionRange(runtime.World.Get(trap.NetId)!));
            TestAssert.False(runtime.Dependencies.AdditionalProviders(team).Any(provider => provider.Position == trap.Position),
                "the placement contributes no team sight");
            runtime.Spells.Tick(.25);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x26 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == bubbleId),
                    "no seat receives a placement bubble");
        }
    }
}
