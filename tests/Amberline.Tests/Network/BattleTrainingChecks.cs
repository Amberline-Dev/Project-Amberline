using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 674: Battle Training's own rules on the production match the Platform starts (Summoner's Rift in TUTORIAL, the trainee and nine
/// bots): the quest tracker's lesson, the quests in order with their triggers, the held waves, the weaker enemy turrets and the trainee's
/// bonus (BattleTrainingScript). The tracker's answers go through the session's wire boundary as the stock client's do (OnQuestEvent
/// 0xD6, BuyItemReq 0x87). The packet bytes are spelled out so the check reads the wire, not the builders.
/// </summary>
public static class BattleTrainingChecks
{
    private const double Step = 0.1;
    private const string Prefix = "game_advanced_tutorial_quest_name_";

    [Check("map1.battle-training-quests-turrets-and-bonus", Serial = true, TimeoutSeconds = 600)]
    public static void BattleTrainingRunsItsQuestsTurretsAndBonus()
    {
        var champions = new[] { "Ryze", "MasterYi", "Soraka", "Shen", "Sivir", "Malphite", "Sivir", "Nasus", "Annie", "MasterYi" };
        var roster = champions.Select((champion, seat) => new SessionParticipantDefinition((ulong)seat + 1, (uint)seat,
            seat == 0 ? "Trainee" : $"{GameData.Default.BotName(champion)} {seat}", champion, seat < 5 ? Team.Order : Team.Chaos,
            "SummonerHeal", "SummonerFlash", IsBot: seat != 0)).ToArray();
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), readySettleSeconds: 0.0, roster: roster,
            gameMode: GameMaps.TutorialMode);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        var seat = runtime.Session.Participants.Single(participant => !participant.IsBot);
        var connection = checked((long)seat.PlayerId);
        TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(seat.PlayerId)) is not null, "authenticate");
        runtime.Session.Spawn(connection);
        runtime.Session.MarkReady(connection);
        seat.Loaded = true;
        var trainee = seat.Hero!;
        trainee.HeroAutoAcquire = false;
        var capture = new Capture();
        runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team, trainee.NetId, capture, ready: true, view: seat.View));
        if (!runtime.Session.Started) runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the Battle Training match starts");
        var (ad, ap, armor, mr) = (trainee.AttackDamage, trainee.AbilityPower, trainee.Armor, trainee.SpellBlock);
        runtime.Tick(Step);

        // The trainee's bonus (wiki, Tutorial, Old tutorials, Battle Training): 15 attack damage, 30 ability power, 25 armor, 31 magic resistance.
        TestAssert.Near(ad + 15.0, trainee.AttackDamage, 1e-6, "attack damage +15");
        TestAssert.Near(ap + 30.0, trainee.AbilityPower, 1e-6, "ability power +30");
        TestAssert.Near(armor + 25.0, trainee.Armor, 1e-6, "armor +25");
        TestAssert.Near(mr + 31.0, trainee.SpellBlock, 1e-6, "magic resistance +31");

        // The enemy's turrets: outer at normal health (1300 + 250 per enemy champion), each tier deeper an even step down to the wiki's 525.
        foreach (var turret in Structures(runtime, EntityKind.Turret))
        {
            var tier = MapDefinitions.TurretTiers[turret.Name];
            if (tier == TurretTier.Fountain) continue;
            var expected = turret.Team == Team.Order
                ? (tier == TurretTier.Nexus ? 1925.0 : 2550.0)
                : tier switch { TurretTier.Outer => 2550.0, TurretTier.Inner => 1875.0, TurretTier.Inhibitor => 1200.0, _ => 525.0 };
            TestAssert.Near(expected, turret.MaxHealth, 1e-6, $"{turret.Team} {tier} turret {turret.Name} maximum health");
            TestAssert.Near(expected, turret.Health, 1e-6, $"{turret.Name} starts at full health");
        }

        // The tracker lesson: both placeholders and "Move Your Mouse Cursor Here", the one that reports its roll-over.
        TestAssert.Equal("1 add 0 primary_quests 0|2 add 1 secondary_quests 0|3 add 0 view_quest_tooltip 1", Quests(capture));
        capture.Clear();
        Send(runtime, connection, [0xD6, 0, 0, 0, 0, 0, 3, 0, 0, 0]);
        runtime.Tick(Step);
        TestAssert.Equal("3 complete 0 - 1|1 hide 0 - 0|2 hide 1 - 0|4 add 0 move_to_lane 0|5 add 1 starting_item 0", Quests(capture),
            "the roll-over ends the lesson: the first primary and secondary quests");

        // The waves wait for the trainee to leave the base ("for this training they will not spawn until your champion" leaves).
        while (runtime.Combat.CurrentTime < 95.0) runtime.Tick(Step);
        TestAssert.Equal(0, LaneMinions(runtime), "no wave at 1:35 while the trainee is on the platform");

        // Purchase a Starting Item: the shop's BuyItemReq for Boots of Speed; the quest completes and pays its 50 gold.
        capture.Clear();
        Send(runtime, connection, [0x87, .. BitConverter.GetBytes(trainee.NetId), .. BitConverter.GetBytes(1001u)]);
        TestAssert.True(trainee.Inventory.Any(slot => slot?.ItemId == 1001), "the boots are bought");
        var gold = trainee.Gold;
        runtime.Tick(Step);
        TestAssert.Equal("5 complete 1 - 1", Quests(capture), "the starting item completes");
        TestAssert.True(trainee.Gold - gold is >= 50.0 and < 51.0, $"its reward is 50 gold, got {trainee.Gold - gold}");

        // Exit the Base: past the inhibitors, at the Order mid lane's outer turret. The battle starts: the next primary quest, the secondary
        // slots fill in order, and the first wave comes at once.
        capture.Clear();
        trainee.Position = Structures(runtime, EntityKind.Turret).Single(turret => turret.Name == "Turret_T1_C_05").Position;
        runtime.Tick(Step);
        TestAssert.Equal("4 complete 0 - 1|6 add 0 front_turret 0|7 add 1 kill_minions 0|8 add 1 ancient_golem 0|9 add 1 lizard_elder 0",
            Quests(capture), "leaving the base completes the quest and opens the battle's");
        for (var step = 0; step < 30 && LaneMinions(runtime) == 0; step++) runtime.Tick(Step);
        TestAssert.True(LaneMinions(runtime) > 0, "the first wave spawns once the trainee is out");

        // Destroy an Enemy Turret: any enemy turret falls; the next primary quest follows, and a click on it pings its place.
        capture.Clear();
        var outer = Structures(runtime, EntityKind.Turret).First(turret => turret.Team == Team.Chaos && turret.Name == "Turret_T2_C_05");
        runtime.Combat.Kill(outer);
        runtime.Tick(Step);
        TestAssert.Equal("6 complete 0 - 1|10 add 0 lane_turrets 0", Quests(capture), "the turret quest completes");
        capture.Clear();
        Send(runtime, connection, [0xD6, 0, 0, 0, 0, 2, 10, 0, 0, 0]);
        runtime.Tick(Step);
        TestAssert.True(capture.Of(0x46).Any(), "clicking the lane quest pings an enemy turret");

        // The trainee's client learns the weaker turret when it first sees it: the last mMaxHP (a turret's 0xCC LOCAL1 bit 9) it holds for
        // the enemy's mid inner turret once the trainee stands near it.
        var inner = Structures(runtime, EntityKind.Turret).Single(turret => turret.Name == "Turret_T2_C_04");
        trainee.Position = new Position2(inner.Position.X - 500.0, inner.Position.Z - 500.0);
        for (var step = 0; step < 5; step++) runtime.Tick(Step);
        TestAssert.Near(1875.0, ClientMaxHealth(capture, inner.NetId), 1e-3, "the client holds the inner turret's maximum");
    }

    private static IEnumerable<Entity> Structures(MatchRuntime runtime, EntityKind kind) =>
        runtime.World.Entities.Values.Where(unit => unit.Kind == kind);

    /// <summary>The last LOCAL1 bit 9 a unit's replication blocks carried ([u32 mask][one u32 per set bit] per registry from offset 15).</summary>
    private static double ClientMaxHealth(Capture capture, uint netId)
    {
        var last = double.NaN;
        Span<uint> masks = stackalloc uint[5];
        foreach (var packet in capture.Of(0xCC))
        {
            if (!CombatPackets.TryReadReplicationBlock(packet, out _, out var unit, masks) || unit != netId || (masks[1] & 0x200u) == 0) continue;
            var at = 15 + (masks[0] != 0 ? 4 + 4 * System.Numerics.BitOperations.PopCount(masks[0]) : 0);
            at += 4 + 4 * System.Numerics.BitOperations.PopCount(masks[1] & 0x1FFu);
            last = BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(at));
        }
        return last;
    }

    private static int LaneMinions(MatchRuntime runtime) => Structures(runtime, EntityKind.Minion).Count(unit => unit.Alive);

    /// <summary>The client's channel-1 input: whole 8-byte blocks Blowfish-encrypted, the tail clear (HeadlessMatch's Wire).</summary>
    private static void Send(MatchRuntime runtime, long connection, byte[] payload)
    {
        var blocks = payload.Length - payload.Length % 8;
        runtime.Session.Receive(connection, 1, [.. runtime.Cipher.Encrypt(payload.AsSpan(0, blocks)), .. payload[blocks..]]);
    }

    /// <summary>
    /// The 0x90 quest packets the trainee's client was sent: id, command, type, the objective key without its prefix, and the flag
    /// (objective at +5, type +0x185, command +0x186, flag +0x187, id +0x188).
    /// </summary>
    private static string Quests(Capture capture) => string.Join("|", capture.Of(0x90).Select(packet =>
    {
        var objective = Encoding.ASCII.GetString(packet.AsSpan(5, 128)).TrimEnd('\0');
        var command = packet[0x186] switch { 0 => "add", 1 => "complete", 2 => "hide", _ => "?" };
        return $"{BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0x188))} {command} {packet[0x185]} " +
               $"{(objective.Length == 0 ? "-" : objective.Replace(Prefix, ""))} {packet[0x187]}";
    }));

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);

        public void Clear() => Packets.Clear();

        public IEnumerable<byte[]> Of(byte opcode) =>
            Packets.Select(packet => packet.Plaintext.ToArray()).Where(packet => packet.Length != 0 && packet[0] == opcode);
    }
}
