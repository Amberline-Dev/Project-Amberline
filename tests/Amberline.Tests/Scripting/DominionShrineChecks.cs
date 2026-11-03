using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Batch 665 (owner, 3 November 2026: "Dominion fountain towers should not show HP bar"). The Crystal Scar's four base shrines
/// (CreateLevelProps: Turret_OrderTurretShrine, Turret_OrderTurretShrine1, Turret_ChaosTurretShrine, Turret_ChaosTurretShrine1, characters
/// OdinOrderTurretShrine and OdinChaosTurretShrine) are untargetable: their shipped character scripts' CharOnActivate sets SetTargetable false.
/// The wire keyed the shrine on the name's "Shrine" ending and on the Summoner's Rift fountain tier, so the two "...Shrine1" towers got the
/// EnterLocalVisibility (0xB5) that lights a tower's health bar, and all four went out targetable.
/// </summary>
public static class DominionShrineChecks
{
    private const double Step = 1.0 / 30.0;

    [Check("dominion.base-shrines-go-out-untargetable-with-no-health-bar", TimeoutSeconds = 600)]
    public static void BaseShrinesGoOutUntargetableWithNoHealthBar()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
        };
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)), GameData.Default,
                readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            var capture = new Capture();
            for (var index = 0; index < roster.Length; index++)
            {
                TestAssert.True(runtime.Session.Authenticate(100 + index, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                    $"seat {index} authenticates");
                runtime.Session.Spawn(100 + index);
                TestAssert.True(runtime.Session.MarkReady(100 + index), $"seat {index} is ready");
            }
            var seat = runtime.Session.Participants.Single(participant => participant.Team == Team.Order);
            runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId, capture, ready: true,
                view: seat.View, clientId: seat.ClientId));
            runtime.Session.RunSchedulers();
            TestAssert.True(runtime.Session.Started, "the map 8 match starts");
            for (var tick = 0; tick < 30 * 20; tick++) runtime.Tick(Step);
            var shrines = runtime.World.Entities.Values.Where(TurretRules.IsCrystalScarShrine).ToList();
            TestAssert.True(shrines.Count == 4, $"map 8 holds its four base shrines ({string.Join(", ", shrines.Select(shrine => shrine.Name))})");
            foreach (var shrine in shrines)
            {
                TestAssert.True(CharacterState.ScriptedCombatOf(shrine).Targetable == 0,
                    $"{shrine.Name} is untargetable on the server (its character script's SetTargetable false)");
                var packets = capture.Packets.Select(packet => packet.Plaintext.ToArray()).ToList();
                TestAssert.True(!packets.Any(bytes => bytes.Length >= 5 && bytes[0] == 0xB5 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == shrine.NetId),
                    $"{shrine.Name} gets no EnterLocalVisibility (0xB5), which lights a tower's health bar");
                // The protection block (0xCC, LOCAL1 mask 0x7000: IsInvulnerable at 19, IsTargetable at 23, IsTargetableToTeam at 27).
                var flags = packets.Where(bytes => bytes.Length == 31 && bytes[0] == 0xCC && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11)) == shrine.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15)) == 0x7000).ToList();
                TestAssert.True(flags.Count > 0 && flags.All(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(23)) == 0 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(27)) == CombatPackets.SFlagNontargetableAll),
                    $"{shrine.Name}'s protection blocks go out untargetable to both teams ({flags.Count} blocks: " +
                    string.Join(", ", flags.Select(bytes => $"{BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(23))}/0x{BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(27)):X}")) + ")");
            }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
