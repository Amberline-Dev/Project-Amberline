using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// Night 2 jungle audit fixes in NeutralAiService (F1, F4 to F8): the stop point inside the attack reach, the single
/// 0.5 s decision cadence, Leashed.luaobj's valid-target list, the damage callbacks, the HP replication rate and
/// the 100-unit retreat arrival. See "Aggro and return decisions" in docs/minions/patch126-neutral-spawn-placement.md.
/// </summary>
public static partial class NeutralAiChecks
{


    private static Entity[] JungleCamp(NeutralHarness harness, int camp) => harness.World.Entities.Values
        .Where(unit => unit.Kind == EntityKind.Neutral && unit.CampId == camp && unit.Alive)
        .OrderBy(unit => unit.NetId).ToArray();

    private static Position2 JungleHome(Entity monster) =>
        new(monster.NeutralHome!.Value.X, monster.NeutralHome.Value.Z);

    // The corpus hero keeps its recorded NetId, which the world's own hero allocator would hand out again.
    private static Entity JungleHero(NeutralHarness harness, Position2 at) => harness.World.Register(
        new Entity(harness.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Hero).Max(unit => unit.NetId) + 1,
            EntityKind.Hero, UnitType.Hero, harness.Hero.Team, at)
        { Health = 1_000_000, MaxHealth = 1_000_000, Created = true, ChampionName = "Annie", Radius = 55 });

    // One production-order step: movement, neutral AI, combat, buffs. Leashed.luaobj must not raise.
    private static void JungleStep(NeutralHarness harness, double dt)
    {
        harness.World.Clock.Advance(dt);
        harness.Movement.Tick(dt);
        harness.Ai.Tick(dt);
        harness.Combat.CombatTick(dt, regenerate: false);
        harness.TickBuffs(dt);
        harness.AssertNoScriptFailure();
    }

    private static int JungleHpPackets(NeutralHarness harness, Entity monster) => harness.Queue.Packets.Count(packet =>
        packet.Plaintext.Length == 23 && packet.Plaintext.Span[0] == 0xCC &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span.Slice(11, 4)) == monster.NetId);

    private static string JungleStates(IEnumerable<Entity> units) => string.Join("; ", units.Select(unit =>
        $"{unit.Name}:{unit.NeutralState}/{(unit.AttackTarget is uint id ? id.ToString("X") : "-")}"));

}
