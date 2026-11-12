using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 743 (owner report, 11 November 2026: his Teemo held the Crest of the Ancient Golem at 45:36 with no blue particle over him).
/// Effects, missiles and perception bubbles share the missile id range (<see cref="NetIdAllocator"/>, 65536 ids), which wraps about
/// 40 minutes into a match. The crest's particle is the shipped script's (Spells/CrestoftheAncientGolem.luaobj OnBuffActivate 0010-0028,
/// BindObjectVar Owner, EffectIDVar buffParticle), sent to every view that sees its holder.
/// </summary>
public static class RecycledEffectIdChecks
{
    private static readonly uint BlueHash = SpellPackets.BuffNameHash(GameData.ParticleStem("NeutralMonster_buf_blue_defense.troy"));
    private const string Crest = "CrestoftheAncientGolem";
    private static readonly Position2 BlueCamp = new(3632.7, 7600.4);

    /// <summary>
    /// A fire-and-forget effect stays in its views (only a destroy takes it out). Before batch 743 the crest that the wrap gave its id was taken
    /// for it: no create reached the holder's own client or his team, and the late-sight replay skipped it too, for the whole buff.
    /// </summary>
    [Check("replication.recycled-effect-id-reaches-the-crest-holder")]
    public static void RecycledEffectIdReachesTheCrestHolder()
    {
        var (runtime, heroes, captures, observers) = Start();
        var teemo = heroes[0];
        teemo.Position = BlueCamp + new Position2(150, 0);
        runtime.Tick(0.1);
        var stale = runtime.Spells.Effects.CreateEffect("NeutralMonster_buf_red_offense.troy", teemo.Position,
            targetNetId: teemo.NetId, bindNetId: teemo.NetId);
        runtime.Delivery.Flush();
        TestAssert.True(observers[0].View.Effects.Contains(stale) && observers[1].View.Effects.Contains(stale),
            "Teemo's team holds the fire-and-forget effect");

        var golem = NewBlueGolem(runtime);
        WrapOnto(runtime, stale);
        TestAssert.True(runtime.Combat.Kill(golem, teemo.NetId), "Teemo takes the blue golem");
        runtime.Delivery.Flush();
        var crest = CrestParticle(runtime, teemo);
        TestAssert.Equal(stale, crest, "the wrap hands the crest the fire-and-forget effect's id");
        for (var step = 0; step < 8; step++) runtime.Tick(0.25);
        runtime.Delivery.Flush();
        for (var index = 0; index < 2; index++)
        {
            TestAssert.True(observers[index].View.Visible.Contains(teemo.NetId), $"observer {index} sees Teemo");
            TestAssert.True(Holds(captures[index], teemo.NetId, crest),
                $"observer {index} ({heroes[index].ChampionName}'s client) was sent the crest's particle on Teemo and keeps it");
        }
    }

    /// <summary>
    /// The wrap passes over an id a live persistent effect holds: before batch 743 the next crest took the live crest's id, its create was skipped
    /// by every view that held the first one, and the delivery's replay entry of the first crest was replaced by the second.
    /// </summary>
    [Check("replication.id-wrap-passes-over-a-live-persistent-effect")]
    public static void IdWrapPassesOverALivePersistentEffect()
    {
        var (runtime, heroes, captures, observers) = Start();
        var (teemo, annie) = (heroes[0], heroes[1]);
        teemo.Position = BlueCamp + new Position2(150, 0);
        annie.Position = BlueCamp + new Position2(-150, 0);
        runtime.Tick(0.1);
        TestAssert.True(runtime.Combat.Kill(NewBlueGolem(runtime), teemo.NetId), "Teemo takes the first blue golem");
        runtime.Delivery.Flush();
        var first = CrestParticle(runtime, teemo);

        var golem = NewBlueGolem(runtime);
        WrapOnto(runtime, first);
        TestAssert.True(runtime.Combat.Kill(golem, annie.NetId), "Annie takes the second blue golem");
        var second = CrestParticle(runtime, annie);
        TestAssert.True(second != first, $"the wrap does not hand Annie's crest the id of Teemo's live one (0x{first:X8})");
        for (var step = 0; step < 4; step++) runtime.Tick(0.25);
        runtime.Delivery.Flush();
        for (var index = 0; index < 2; index++)
        {
            TestAssert.True(Holds(captures[index], teemo.NetId, first), $"observer {index} keeps Teemo's crest");
            TestAssert.True(Holds(captures[index], annie.NetId, second), $"observer {index} was sent Annie's crest");
        }
    }

    /// <summary>A recycled id is a new effect for the script hosts too: a stack's superseded effect, a placemarker's end and the batch 726
    /// retirement kill only an id the host has not killed before (BbSpellHost.KilledEffects).</summary>
    [Check("replication.recycled-effect-id-is-no-longer-a-killed-effect")]
    public static void RecycledEffectIdIsNoLongerAKilledEffect()
    {
        var (runtime, _, _, _) = Start();
        var host = new BbSpellHost(runtime.Spells, (_, _) => "");
        var killed = runtime.World.Allocate(UnitType.Missile);
        host.KilledEffects.Add(killed);
        WrapOnto(runtime, killed);
        TestAssert.Equal(killed, runtime.World.Allocate(UnitType.Missile), "the wrap hands the killed effect's id out again");
        TestAssert.False(host.KilledEffects.Contains(killed), "the host forgets the old effect it killed under the recycled id");
    }

    // Allocates missile-range ids until the next one is netId, the state the range reaches about 40 minutes into a match.
    private static void WrapOnto(MatchRuntime runtime, uint netId)
    {
        var range = NetIdAllocator.RangeFor(UnitType.Missile);
        for (var guard = 0u; runtime.World.Allocator.Next(UnitType.Missile) != netId; guard++)
        {
            TestAssert.True(guard < range.Count, $"the missile range reaches 0x{netId:X8}");
            runtime.World.Allocate(UnitType.Missile);
        }
    }

    private static uint CrestParticle(MatchRuntime runtime, Entity hero)
    {
        var buff = hero.Buffs.Single(item => item.Name == Crest);
        var instance = runtime.Spells.UnitScripts!.Buffs!.InstanceOf(buff)
            ?? throw new InvalidOperationException($"{hero.ChampionName}'s crest runs as a script instance");
        return (uint)(double)instance.InstanceVars["buffParticle"]!;
    }

    // Whether the client holds the crest particle: a 0x8C of the blue troy with this id bound to the unit, and no 0x3D of the id after it.
    private static bool Holds(Capture capture, uint unit, uint effect)
    {
        var held = false;
        foreach (var packet in capture.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length >= 33 && bytes[0] == FxPackets.CreateGroupOpcode && BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..29]) == effect)
                held = BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..10]) == BlueHash && BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..33]) == unit;
            else if (bytes.Length == 9 && bytes[0] == FxPackets.KillOpcode && BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..9]) == effect)
                held = false;
        }
        return held;
    }

    private static Entity NewBlueGolem(MatchRuntime runtime)
    {
        var golem = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, Team.Neutral, BlueCamp)
        {
            Name = "AncientGolem", Alive = true, Created = true, Visible = true, Health = 1000.0, MaxHealth = 1000.0, CampId = 1,
        };
        runtime.World.Register(golem);
        // CharScriptAncientGolem's UpdateSelfBuffActions puts the crest on the golem; its BuffOnDeath hands it to the killer.
        TestAssert.True(runtime.Spells.UnitScripts!.BindUnit(golem, "AncientGolem"), "the golem binds its shipped character script");
        return golem;
    }

    private static (MatchRuntime Runtime, Entity[] Heroes, Capture[] Captures, ReplicationObserver[] Observers) Start()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order stealth", "Teemo", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order mana", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Chaos mana", "Orianna", Team.Chaos),
            new SessionParticipantDefinition(4, 3, "Chaos energy", "LeeSin", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, random: new SeededRandomSource(7));
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            TestAssert.True(runtime.Session.Authenticate(100L + index, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"seat {index} authenticates");
            runtime.Session.Spawn(100L + index);
            TestAssert.True(runtime.Session.MarkReady(100L + index), $"seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        var heroes = Enumerable.Range(0, roster.Length).Select(index => runtime.Session.Participants[index].Hero!).ToArray();
        var captures = heroes.Select(_ => new Capture()).ToArray();
        var observers = heroes.Select((hero, index) =>
            new ReplicationObserver(9700UL + (ulong)index, hero.Team, hero.NetId, captures[index], ready: true)).ToArray();
        foreach (var observer in observers) runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();
        return (runtime, heroes, captures, observers);
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
