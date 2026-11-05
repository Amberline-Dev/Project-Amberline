using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Items;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Batch 460: the Summoner's Rift leftovers a map 8 match reached. Recall is the map's own (LEVELS/Map8/Mission.inibin
/// ExtraSummonerItem*ItemID 2005, whose spell is OdinRecall: 4.5 s), the camera and respawn packets carry the ground height, the shop
/// stands in the map 8 base, the opening ceremony's hold also holds Flash and dashes, and the ceremony turns itself off after the release
/// (SetBTInstanceStatus).
/// </summary>
public static class DominionMapLeftoverChecks
{
    private const double Step = 1.0 / 30.0;
    private const double HoldRadius = 1200.0;
    private static readonly Position2 OutsideOrderBase = new(1800.0, 3250.0);

    [Check("dominion.map-8-recall-is-the-shipped-odin-recall")]
    public static void MapEightRecallIsOdinRecall()
    {
        WithMatch(("Annie", "SummonerFlash", "SummonerHaste"), (runtime, bursts, captures) =>
        {
            var hero = Hero(runtime, Team.Order);
            var burst = bursts[0].Select(packet => packet.Plaintext.ToArray())
                .Where(bytes => bytes.Length > 5 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == hero.NetId).ToArray();
            TestAssert.True(burst.Any(bytes => bytes[0] == 0x56 && bytes[5] == 6 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6)) == 2005),
                "inventory slot 6 holds the map's extra summoner item, 2005 (Mission.inibin ExtraSummonerItem*ItemID)");
            TestAssert.True(burst.Any(bytes => bytes[0] == 0x73 && bytes[13] == SpellSystem.RecallSlot &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9)) == ProtocolEncoding.HashString("OdinRecall")),
                "spell slot 10 is OdinRecall (Items/2005.inibin Data*SpellName)");

            Advance(runtime, 1.0); // the hero stands outside the opening hold below, so the setup does not hold it
            hero.Position = new Position2(6900.0, 6450.0);
            captures[0].Packets.Clear();
            TestAssert.True(Cast(runtime, 1, hero, SpellSystem.RecallSlot, hero.Position), "recall accepted");
            TestAssert.True(captures[0].Packets.Any(packet => Contains(packet, ProtocolEncoding.HashString("OdinRecall"))),
                "the cast names OdinRecall, the spell the client holds in slot 10");
            var spawn = MapDefinitions.TeamSpawn(Team.Order);
            Advance(runtime, 4.9);
            TestAssert.True(runtime.Spells.RecallRemaining(hero) > 0.0 && Distance(hero.Position, spawn) > 1000.0,
                "still channelling 4.9 s after the press (0.5 s cast time + 4.5 s ChannelDuration)");
            Advance(runtime, 0.2);
            TestAssert.True(Distance(hero.Position, spawn) < 25.0,
                $"home 5.1 s after the press, on the map 8 spawn ({hero.Position.X:0.0}, {hero.Position.Z:0.0})");
        });
    }

    /// <summary>
    /// Batch 689 (community report, 4 November 2026: "minions hitting player do not cancel recall"). OdinRecall.luaobj carries Recall's
    /// BuffOnTakeDamage (a hit whose source type is not DAMAGESOURCE_PERIODIC sets WillRemove; BuffOnUpdateActions stops the channel), and its
    /// ChannelingStart adds the buff with no BuffName (pc 0110-0127), so the buff is the script's own. A basic hit cancels it on the next actions line.
    /// </summary>
    [Check("dominion.odin-recall-is-cancelled-by-a-basic-hit")]
    public static void OdinRecallIsCancelledByABasicHit()
    {
        WithMatch(("Annie", "SummonerFlash", "SummonerHaste"), (runtime, bursts, captures) =>
        {
            var hero = Hero(runtime, Team.Order);
            var foe = Hero(runtime, Team.Chaos);
            Advance(runtime, 1.0);
            hero.Position = new Position2(6900.0, 6450.0);
            TestAssert.True(Cast(runtime, 1, hero, SpellSystem.RecallSlot, hero.Position), "recall accepted");
            Advance(runtime, 1.5);
            TestAssert.True(runtime.Spells.RecallRemaining(hero) > 0.0, "the channel runs before the hit");
            var buffs = string.Join(",", hero.Buffs.Select(buff => buff.Name));
            runtime.Combat.ApplyHit(foe.NetId, hero.NetId, 10.0, sourceKind: DamageSourceKind.Basic);
            Advance(runtime, 0.3);
            TestAssert.Near(0.0, runtime.Spells.RecallRemaining(hero), 0.0, $"a basic hit cancels OdinRecall (buffs before the hit: {buffs})");
        });
    }

    [Check("dominion.map-8-camera-and-respawn-packets-carry-the-ground-height")]
    public static void MapEightPositionPacketsCarryTheGroundHeight()
    {
        WithMatch(("Annie", "SummonerFlash", "SummonerHaste"), (runtime, bursts, captures) =>
        {
            var hero = Hero(runtime, Team.Order);
            var ground = (float)runtime.Navigation.HeightAt(hero.Position);
            TestAssert.True(ground < -20.0f, $"the map 8 base floor lies below 0 (about -35, AIPath.aimesh_ngrid; {ground})");
            var start = bursts[2].Select(packet => packet.Plaintext.ToArray()).Single(bytes => bytes[0] == 0x27);
            TestAssert.Near(ground, BinaryPrimitives.ReadSingleLittleEndian(start.AsSpan(22)), 0.01, "the start camera looks at the ground");

            Advance(runtime, 1.0); // the hero stands outside the opening hold below, so the setup does not hold it
            hero.Position = new Position2(6900.0, 6450.0);
            TestAssert.True(Cast(runtime, 1, hero, SpellSystem.RecallSlot, hero.Position), "recall accepted");
            Advance(runtime, 9.0);
            var camera = captures[0].Packets.Select(packet => packet.Plaintext.ToArray()).Last(bytes => bytes[0] == VisionPackets.SetCameraPositionOpcode);
            TestAssert.Near((float)runtime.Navigation.HeightAt(hero.Position), BinaryPrimitives.ReadSingleLittleEndian(camera.AsSpan(9)), 0.01,
                "Recall's camera (BBSetCameraPosition) carries the ground height at the spawn");

            captures[0].Packets.Clear();
            TestAssert.True(runtime.Combat.Kill(hero, Hero(runtime, Team.Chaos).NetId), "the hero dies");
            Advance(runtime, 1.0);
            runtime.Combat.ScheduleRespawn(hero);
            TestAssert.True(runtime.Combat.RespawnImmediately(hero), "the hero respawns");
            runtime.Delivery.Flush();

            var respawn = captures[0].Packets.Select(packet => packet.Plaintext.ToArray()).Single(bytes => bytes[0] == 0x4D);
            TestAssert.Near((float)runtime.Navigation.HeightAt(hero.Position), BinaryPrimitives.ReadSingleLittleEndian(respawn.AsSpan(9)), 0.01,
                "the respawn packet carries the ground height");
        });
    }

    [Check("dominion.shop-opens-in-the-map-8-base")]
    public static void ShopOpensInTheMapEightBase()
    {
        WithMatch(("Annie", "SummonerFlash", "SummonerHaste"), (runtime, bursts, _) =>
        {
            var shop = new ShopService(runtime.World, new ItemCatalog(GameData.Default.Paths), runtime.HeroStats,
                new NullShopTransport());
            foreach (var team in new[] { Team.Order, Team.Chaos })
                TestAssert.True(shop.InShopRange(Hero(runtime, team)), $"the {team} champion at its map 8 spawn is in its shop's range");
            // The reveal of orderShop01 carries the map 8 shop (LEVELS/Map8/Scene/orderShop01.SCB central point, 16.54, 4452.44).
            var reveal = bursts[2].Select(packet => packet.Plaintext.ToArray())
                .Where(bytes => bytes[0] == VisionPackets.EnterVisibilityOpcode &&
                                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) == MapDefinitions.BuildingNetId("orderShop01")).ToArray();
            TestAssert.True(reveal.Length == 1 && ContainsFloat(reveal[0], 4452.44140625f), "the Order shop is revealed at its map 8 position");
        });
    }

    [Check("dominion.setup-hold-keeps-flash-and-dashes-in-the-base")]
    public static void SetupHoldKeepsFlashAndDashesInTheBase()
    {
        WithMatch(("Tristana", "SummonerFlash", "SummonerHaste"), (runtime, _, _) =>
        {
            var hero = Hero(runtime, Team.Order);
            var spawn = MapDefinitions.TeamSpawn(Team.Order);
            hero.SpellRanks[1] = 1;
            Advance(runtime, 1.0);
            Move(runtime, 1, hero, OutsideOrderBase);
            Advance(runtime, 10.0);
            Move(runtime, 1, hero, OutsideOrderBase);
            Advance(runtime, 3.0);
            TestAssert.True(Distance(hero.Position, spawn) <= HoldRadius,
                $"a second walk order from the edge stays inside ({Distance(hero.Position, spawn):0.0})");

            _ = CastGround(runtime, 1, hero, 0, OutsideOrderBase, summoner: true);
            Advance(runtime, 1.0);
            TestAssert.True(Distance(hero.Position, spawn) <= HoldRadius,
                $"Flash cannot leave the base during the setup ({Distance(hero.Position, spawn):0.0})");

            hero.Mana = hero.MaxMana;
            _ = CastGround(runtime, 1, hero, 1, OutsideOrderBase, summoner: false);
            Advance(runtime, 2.0);
            TestAssert.True(Distance(hero.Position, spawn) <= HoldRadius,
                $"Rocket Jump (BBMove) cannot leave the base during the setup ({Distance(hero.Position, spawn):0.0})");
        });
    }

    [Check("dominion.opening-ceremony-turns-itself-off-after-the-release")]
    public static void OpeningCeremonyTurnsItselfOffAfterTheRelease()
    {
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, new WorldState())
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var releases = 0;
        host.Context.Leaves["RemoveDebugCircle"] = _ => { releases++; return BehaviorStatus.Success; };
        // The release's SpawnShrines names an encounter no shipped file holds ("Shrine"); the production leaf succeeds without it, so the
        // sequence reaches its last node, SetBTInstanceStatus (OdinScriptBehaviorTrees.xml:4827).
        var now = 0.0;
        void Run(double seconds)
        {
            for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) host.Tick(now += Step, Step);
        }
        // The release is 80 s on this server (NOT RETAIL; the shipped 45), so the window runs past it.
        Run(Amberline.Simulation.BehaviorTrees.OpeningCeremonyTiming.ReleaseSeconds + 1.0);
        TestAssert.True(releases > 0, "the ceremony released the champions at its release (80 s, NOT RETAIL)");
        var released = releases;
        Run(2.0);
        TestAssert.Equal(released, releases, "the ceremony runs no more after its release (SetBTInstanceStatus Enabled false)");
    }

    [Check("dominion.bots-home-points-are-the-map-8-spawns")]
    public static void BotsHomePointsAreTheMapEightSpawns()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            // GetMaxTravelDistSquared is the bots' fountain-to-fountain distance: both fountains are the map 8 spawn points (12798.15 apart),
            // not Summoner's Rift's Order fountain (13855.8 from the map 8 Chaos spawn).
            TestAssert.Near(Distance(MapDefinitions.TeamSpawn(Team.Order), MapDefinitions.TeamSpawn(Team.Chaos)),
                Amberline.Simulation.Bots.BotController.MaxTravelDistance, 1e-6, "the bots' fountains are the map 8 spawns");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private sealed class NullShopTransport : IShopTransport
    {
        public void SendRemoveItem(Entity hero, int slot, int remainingCount) { }
        public void SendBuyItem(Entity hero, int slot, int itemId, int count) { }
        public void SendConsumedPurchase(Entity hero, int itemId) { }
        public void SendSwapItem(Entity hero, int sourceSlot, int destinationSlot) { }
        public void SendItemCooldown(Entity hero, int slot, double seconds) { }
    }

    private static Entity Hero(MatchRuntime runtime, Team team) =>
        runtime.Session.Participants.Single(participant => participant.Team == team).Hero!;

    private static void WithMatch((string Champion, string D, string F) order,
        Action<MatchRuntime, IReadOnlyList<IReadOnlyList<PlaintextPacket>>, Capture[]> body)
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Order", order.Champion, Team.Order, order.D, order.F), new(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            var bursts = new List<IReadOnlyList<PlaintextPacket>>();
            var captures = new Capture[roster.Count];
            for (var id = 1; id <= roster.Count; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                bursts.Add(runtime.Session.Spawn(id));
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
                var participant = runtime.Session.Participants[id - 1];
                captures[id - 1] = new Capture();
                runtime.Delivery.AddObserver(new ReplicationObserver((ulong)id, participant.Team, participant.Hero!.NetId, captures[id - 1],
                    ready: true, view: participant.View));
            }
            // bursts: the two seats' spawn bursts, then seat 1's map-ready burst (the start camera, the shop reveal).
            bursts.Add(runtime.Session.BuildMapReadyPackets(1));
            TestAssert.True(runtime.Session.Start(), "start");
            body(runtime, bursts, captures);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++) runtime.Tick(Step);
        runtime.Delivery.Flush();
    }

    private static void Move(MatchRuntime runtime, int seat, Entity hero, Position2 destination)
    {
        var request = new byte[22]; request[0] = 0x75; request[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        runtime.Session.Receive(seat, 1, runtime.Cipher.Encrypt(request), runtime.Dependencies.Gameplay);
        runtime.Delivery.Flush();
    }

    private static bool Cast(MatchRuntime runtime, int seat, Entity hero, int slot, Position2 point) =>
        CastGround(runtime, seat, hero, slot, point, summoner: false, target: hero.NetId);

    private static bool CastGround(MatchRuntime runtime, int seat, Entity hero, int slot, Position2 point, bool summoner, uint target = 0)
    {
        var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)point.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target);
        var accepted = false;
        runtime.Session.Receive(seat, 1, runtime.Cipher.Encrypt(request), context => accepted = runtime.Dependencies.Gameplay(context));
        runtime.Delivery.Flush();
        return accepted;
    }

    private static bool Contains(PlaintextPacket packet, uint value)
    {
        var bytes = packet.Plaintext.Span;
        for (var offset = 0; offset + 4 <= bytes.Length; offset++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) == value) return true;
        return false;
    }

    private static bool ContainsFloat(byte[] bytes, float value)
    {
        for (var offset = 0; offset + 4 <= bytes.Length; offset++)
            if (BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)) == value) return true;
        return false;
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
