using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-same-pump-flash-targets-current-sight")]
    public static void FlashThenTargetedInputUsesCurrentSightBeforeDeliveryFlush()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.SummonerSpells = ["SummonerFlash", "SummonerDot"];
            hero.Inventory = [new InventorySlot(3144), null, null, null, null, null];
            enemy.Position = new Position2(4300, 6900);
            hero.Position = enemy.Position;
            runtime.Delivery.Flush();
            var seat = runtime.Session.Participants[0];
            TestAssert.True(seat.View.Visible.Contains(enemy.NetId),
                "the client receives the target before Flash");

            TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 0, 0,
                new Position2(4000, 6900))), "Flash itself is accepted");
            TestAssert.Near(4000, hero.Position.X, 2, "Flash lands across the brush boundary");
            var providers = VisibilityRules.BuildTeamProviders(runtime.World, team,
                runtime.Delivery.Balls, runtime.Dependencies.AdditionalProviders);
            TestAssert.False(VisibilityRules.RevealsEntity(runtime.World, team,
                    runtime.Dependencies.VisionEnvironment, providers,
                    point => runtime.Dependencies.RevealsStealth(team, point), enemy),
                "fresh team sight loses the enemy after Flash");
            TestAssert.True(seat.View.Visible.Contains(enemy.NetId),
                "delivery has not yet retired the old client-visible target");

            TestAssert.False(InputCast(match, 1, InputOrder(hero.NetId, 3, enemy)),
                "an attack order cannot follow a newly hidden target");
            TestAssert.Equal<uint?>(null, hero.AttackTarget);
            var itemCooldown = hero.ItemCooldowns[0];
            TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 4,
                enemy.NetId, enemy.Position)),
                "a targeted item cannot use stale client sight");
            TestAssert.Near(itemCooldown, hero.ItemCooldowns[0], 0);
            var summonerCooldown = hero.SummonerCooldowns[1];
            TestAssert.False(InputCast(match, 1, InputCastPacket(hero.NetId, 1,
                enemy.NetId, enemy.Position)),
                "Ignite cannot use stale client sight");
            TestAssert.Near(summonerCooldown, hero.SummonerCooldowns[1], 0);
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "SummonerDot"));

            hero.Position = enemy.Position;
            TestAssert.True(InputCast(match, 1, InputCastPacket(hero.NetId, 1,
                enemy.NetId, enemy.Position)),
                "the already disclosed enemy is castable again when current sight returns");
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "SummonerDot"));
        }
    }

    [Check("replication.entity-sight-shares-lee-mark-and-building-edge")]
    public static void OneEntitySightPredicateKeepsMarkAndBuildingEdgeRules()
    {
        var world = new WorldState();
        var caster = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0, 0)) { Alive = true };
        var enemy = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(120, 0)) { Alive = true };
        var neighbor = new Entity(3, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            enemy.Position) { Alive = true };
        // Batch 253 restates batch 244's change: a turret is seen through the client's turret line-of-sight test
        // (that test belongs to the turret), so the footprint-edge rule tested here is the inhibitors' and nexuses' (obj_Building has no
        // LineOfSightTest of its own); the building is an inhibitor.
        var building = new Entity(4, EntityKind.Inhibitor, UnitType.Structure, Team.Chaos,
            new Position2(100, 0)) { Alive = true, Radius = 60, Name = "EdgeInhibitor" };
        world.Register(caster); world.Register(enemy); world.Register(neighbor);
        world.Register(building);
        var environment = new CenterBlockedVision();
        VisionProvider[] providers = [new(caster.Position, 200)];
        TestAssert.False(VisibilityRules.ProvidersReveal(environment, providers,
            building.Position), "the building center is blocked");
        TestAssert.True(VisibilityRules.RevealsEntity(world, Team.Order, environment,
            providers, _ => false, building), "the building's near edge is revealed");

        // 19 October 2026: Sonic Wave's mark is the script's unit-attached perception bubbles (BlindMonkQOne: 400 and a 50 unit true sight on the
        // marked unit), no longer a buff-name rule. A unit is inside its own bubble, whatever cell it stands in.
        world.AddTimedVisionBubble(Team.Order, enemy.Position, 400.0, 3.0, anchorNetId: enemy.NetId);
        TestAssert.True(VisibilityRules.RevealsEntity(world, Team.Order, environment,
            providers, _ => false, enemy), "the team-owned mark reveals only its target");
        TestAssert.False(VisibilityRules.RevealsEntity(world, Team.Order, environment,
            providers, _ => false, neighbor), "an unmarked same-position enemy remains hidden");
        world.TimedVisionBubbles.Clear();
        TestAssert.False(VisibilityRules.RevealsEntity(world, Team.Order, environment,
            providers, _ => false, enemy), "mark removal restores ordinary LOS");

        // The shipped Map1 grid has an opaque cell at (128,100), centered at
        // (6096.097,4914.808), beside a walkable cell at (127,100). The walk samples
        // the viewer's own cell first, so the mark's 400u provider cannot reveal another
        // unit elsewhere in this blocked cell. Batch 253: a unit at the provider's exact
        // point is seen (the client's column test passes a viewer within the
        // target's footprint, 0 here), so the unmarked control stands 10 units away.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, extraEnemy: true,
                enemyChampion: "Annie", extraEnemyChampion: "Ezreal");
            var grid = match.Runtime.Navigation;
            var blockedCell = new NavCell(128, 100);
            var casterCell = new NavCell(127, 100);
            var blocked = grid.CellToWorld(blockedCell);
            var casterPoint = grid.CellToWorld(casterCell);
            TestAssert.False(grid.IsVisionClear(blockedCell),
                "the shipped Map1 target cell blocks vision");
            TestAssert.True(grid.IsMovementWalkable(casterPoint),
                "Lee starts on an adjacent walkable cell");
            TestAssert.False(grid.HasVisionLos(blocked, blocked),
                "a coincident provider cannot reveal an opaque cell");
            var beside = blocked + new Position2(10, 0);
            TestAssert.True(grid.WorldToCell(beside) == blockedCell, "the control point is in the same blocked cell");
            TestAssert.False(VisibilityRules.ProviderReveals(
                match.Runtime.Dependencies.VisionEnvironment,
                new VisionProvider(blocked, 400), beside));
            var lee = match.Hero(0);
            var marked = match.Hero(1);
            var unmarked = match.Hero(2);
            lee.Position = casterPoint;
            marked.Position = blocked;
            unmarked.Position = beside;
            lee.SummonerSpells = ["SummonerFlash", "SummonerDot"];
            match.Runtime.World.AddTimedVisionBubble(lee.Team, marked.Position, 400.0, 3.0, anchorNetId: marked.NetId);
            match.Runtime.Delivery.Flush();
            var view = match.Runtime.Session.Participants[0].View;
            TestAssert.True(view.Visible.Contains(marked.NetId),
                "observer received the team-marked target across the blocked cell");
            TestAssert.False(view.Visible.Contains(unmarked.NetId),
                "the mark does not disclose an unmarked opponent in the same blocked cell");
            TestAssert.False(InputCast(match, 1, InputOrder(lee.NetId, 3, unmarked)),
                "authenticated attack denies the unmarked control in the same blocked cell");
            TestAssert.True(InputCast(match, 1, InputOrder(lee.NetId, 3, marked)),
                "authenticated attack accepts the marked target");
            TestAssert.Equal<uint?>(marked.NetId, lee.AttackTarget,
                "production hero pursuit retains the marked target");
            TestAssert.False(InputCast(match, 1, InputCastPacket(lee.NetId, 1,
                unmarked.NetId, unmarked.Position)),
                "targeted cast cannot use the unmarked control");
            TestAssert.Near(0, lee.SummonerCooldowns[1], 0);
            TestAssert.True(InputCast(match, 1, InputCastPacket(lee.NetId, 1,
                marked.NetId, marked.Position)),
                "production targeted cast accepts the marked target");
            TestAssert.True(marked.Buffs.Any(buff => buff.Name == "SummonerDot"));
        }
    }

    [Check("network.reconnect-ready-gates-gameplay-shop-and-cheat")]
    public static void ReconnectedSpawnCannotMutateGameplayBeforeReady()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var session = runtime.Session;
        var hero = match.Hero(0);
        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        hero.SummonerSpells = ["SummonerFlash", "SummonerDot"];
        hero.Gold = 1000;
        hero.Inventory = [null, null, null, null, null, null];
        TestAssert.True(session.Disconnect(1));
        TestAssert.True(session.AuthenticateReconnect(3, session.BuildKeyCheck(1)) is not null);
        session.Spawn(3);
        TestAssert.True(session.Participant(3) is { Spawned: true, Loaded: false, Ready: false });

        var move = InputOrder(hero.NetId, 2, null,
            hero.Position + new Position2(150, 0));
        var flash = InputCastPacket(hero.NetId, 0, 0,
            hero.Position + new Position2(100, 0));
        var open = new byte[] { 0x60, 0, 0, 0, 0 };
        var buy = new byte[9]; buy[0] = 0x87;
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 1001);
        var cheat = new byte[16]; cheat[0] = 0xB6; cheat[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(cheat.AsSpan(8), hero.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(cheat.AsSpan(12), 25);
        var startingGold = hero.Gold;
        var startingPosition = hero.Position;
        TestAssert.False(InputCast(match, 3, move));
        TestAssert.False(InputCast(match, 3, flash));
        TestAssert.False(session.DispatchShop(InputSession(runtime, 3, open)));
        TestAssert.False(session.DispatchShop(InputSession(runtime, 3, buy)));
        TestAssert.False(session.DispatchCheat(InputSession(runtime, 3, cheat)));
        TestAssert.Equal(startingPosition, hero.Position);
        TestAssert.Equal(0, hero.Waypoints.Count);
        TestAssert.Near(0, hero.SummonerCooldowns[0], 0);
        TestAssert.Near(startingGold, hero.Gold, 0);
        TestAssert.True(hero.Inventory.All(slot => slot is null));

        TestAssert.True(session.MarkReady(3));
        TestAssert.False(session.Participant(3)!.Loaded,
            "manual Ready fixtures remain valid without the native Loaded latch");
        var statsRefresh = new byte[5]; statsRefresh[0] = 0x59;
        BinaryPrimitives.WriteUInt32LittleEndian(statsRefresh.AsSpan(1), hero.NetId);
        TestAssert.False(InputCast(match, 3, statsRefresh),
            "the stats-refresh opcode retains its stricter Loaded requirement");
        var spoofed = (byte[])move.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(spoofed.AsSpan(1), match.Hero(1).NetId);
        TestAssert.False(InputCast(match, 3, spoofed), "Ready does not relax actor ownership");
        TestAssert.True(InputCast(match, 3, move), "ordinary owned gameplay resumes at Ready");
        TestAssert.True(session.DispatchShop(InputSession(runtime, 3, open)));
        TestAssert.True(session.DispatchShop(InputSession(runtime, 3, buy)));
        TestAssert.True(hero.Inventory.Any(slot => slot?.ItemId == 1001));
        var afterBuy = hero.Gold;
        TestAssert.True(session.DispatchCheat(InputSession(runtime, 3, cheat)));
        TestAssert.Near(afterBuy + 25, hero.Gold, 0);
    }

    [Check("replication-player-name-shrine-suffix-remains-hero")]
    public static void PlayerNamedShrineKeepsHeroFogStatsAndReanchor()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var opposite = team == Team.Order ? Team.Chaos : Team.Order;
            var roster = new SessionParticipantDefinition[]
            {
                new(1, 0, "Observer", "Annie", team),
                new(2, 1, "MyShrine", "Annie", opposite),
                new(3, 2, "Normal", "Ezreal", opposite),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, readySettleSeconds: 0, roster: roster);
            runtime.World.Clock.SetVirtual(0);
            try
            {
                runtime.Session.CreateWorld();
                for (var i = 0; i < roster.Length; i++)
                {
                    var id = i + 1;
                    TestAssert.True(runtime.Session.Authenticate(id,
                        runtime.Session.BuildKeyCheck((ulong)id)) is not null);
                    runtime.Session.Spawn(id);
                    TestAssert.True(runtime.Session.MarkReady(id));
                    runtime.Session.Participants[i].Hero!.Position = new Position2(7000 + i * 100, 7000);
                }
                var observerSeat = runtime.Session.Participants[0];
                var named = runtime.Session.Participants[1].Hero!;
                var normal = runtime.Session.Participants[2].Hero!;
                TestAssert.Equal("MyShrine", named.Name,
                    "the source spawn path uses the player name as entity Name");
                var observer = new ReplicationObserver(1, team, observerSeat.Hero!.NetId,
                    new Capture(), ready: true, view: observerSeat.View);
                runtime.Delivery.AddObserver(observer);
                TestAssert.True(runtime.Session.Start());
                runtime.Delivery.Flush();
                TestAssert.True(observer.View.Visible.Contains(named.NetId) &&
                    observer.View.Visible.Contains(normal.NetId));

                var services = runtime.Dependencies.ReplicationServices;
                TestAssert.False(VisibilityRules.IsShrine(named));
                TestAssert.False(services.StatsBeforeReveal(named));
                TestAssert.True(services.BuildEntityStats(named, observer).Count > 0,
                    "a player-name suffix cannot suppress the hero stat snapshot");
                TestAssert.True(services.BuildReveal(normal, observer)
                        .Select(packet => packet.Plaintext.Span[0])
                        .SequenceEqual(services.BuildReveal(named, observer)
                            .Select(packet => packet.Plaintext.Span[0])),
                    "suffix hero uses the ordinary hero reveal packet classes");
                TestAssert.True(services.BuildWaypointReanchor(named, observer).Count > 0,
                    "suffix hero keeps waypoint reanchoring");

                var hidden = team == Team.Order
                    ? new Position2(13000, 13000) : new Position2(1000, 1000);
                named.Position = hidden;
                normal.Position = hidden + new Position2(100, 0);
                runtime.Delivery.Flush();
                TestAssert.False(observer.View.Visible.Contains(named.NetId) ||
                    observer.View.Visible.Contains(normal.NetId));
                named.Position = new Position2(7100, 7000);
                normal.Position = new Position2(7200, 7000);
                runtime.Delivery.Flush();
                TestAssert.True(observer.View.Visible.Contains(named.NetId) &&
                    observer.View.Visible.Contains(normal.NetId),
                    "suffix hero and normal control both re-enter fog sight");
            }
            finally { runtime.World.Clock.UseReal(); }
        }
    }

    private static bool InputCast(Match match, long connection, byte[] packet) =>
        match.Runtime.Session.DispatchGameplay(connection, 1, packet,
            match.Runtime.Dependencies.Gameplay);

    private static SessionInput InputSession(MatchRuntime runtime, long connection,
        byte[] packet) => new(connection, 1, runtime.Cipher.Encrypt(packet), packet, true);

    private static byte[] InputOrder(uint actor, byte order, Entity? target,
        Position2? destination = null)
    {
        var packet = new byte[22]; packet[0] = 0x75; packet[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), actor);
        var point = destination ?? target?.Position ?? default;
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(6), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(14), (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(18), target?.NetId ?? 0);
        return packet;
    }

    private static byte[] InputCastPacket(uint actor, int slot, uint target,
        Position2 point)
    {
        var packet = new byte[34]; packet[0] = 0xA2;
        packet[5] = (byte)((slot << 1) | (slot < 2 ? 1 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), actor);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset + 8), (float)point.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(30), target);
        return packet;
    }

    private sealed class CenterBlockedVision : IVisionEnvironment
    {
        public double CellSize => 1;
        public bool HasVisionLos(Position2 source, Position2 target) => target.X < 80;
        public bool BrushAllowsVision(Position2 source, Position2 target) => true;
        public int? BrushIdAt(Position2 point) => null;
    }

}
