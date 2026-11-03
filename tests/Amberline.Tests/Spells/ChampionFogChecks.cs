using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-blitz-grab-stealthed-collision-filter")]
    public static void BlitzGrabSkipsUnseenBoxesButHitsStealthedHeroes()
    {
        foreach (var outcome in new[] { "hidden-box", "revealed-box", "hidden-hero" })
        {
            using var match = new Match("Blitzcrank", Team.Order, enemyChampion: "Shaco");
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.SpellRanks[3] = 0;
            enemy.Position = hero.Position + new Position2(800, 0);
            match.CastGround(1, hero.Position + new Position2(450, 0), 1);
            Advance(match, 2.4);
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            TestAssert.True(box.Buffs.Any(buff => buff.Payload.Stealthed), "the real box has armed");
            if (outcome == "revealed-box")
                runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                    runtime.World.Allocate(UnitType.Missile), hero.Team, box.Position, 200, 10, RevealStealth: true));
            if (outcome == "hidden-hero")
                runtime.SpellBuffs.Add(enemy, "RegressionStealth", BuffType.Invisibility, 10,
                    enemy.NetId, new BuffPayload { Stealthed = true });
            TestAssert.Equal(outcome == "revealed-box", runtime.Spells.CanSeeTarget(hero, box));
            var boxHealth = box.Health;
            var enemyHealth = enemy.Health;
            var bubbles = runtime.World.TimedVisionBubbles.Count;
            match.CastGround(0, enemy.Position);
            SpellStep(match, .25);
            SpellStep(match, .6);
            TestAssert.Equal(outcome == "revealed-box", box.Health < boxHealth, "only the seen box consumes the hook");
            TestAssert.Equal(outcome != "revealed-box", enemy.Health < enemyHealth, "the hook reaches the eligible hero behind the hidden box");
            TestAssert.Equal(bubbles, runtime.World.TimedVisionBubbles.Count, "Rocket Grab adds no reveal");
            if (outcome == "hidden-hero")
                TestAssert.False(runtime.Combat.IsTargetable(enemy, hero), "a hit does not reveal a stealthed hero");
        }
    }

    [Check("spells-nidalee-b5-javelin-stealth-collision")]
    public static void NidaleeJavelinPassesUnseenBox()
    {
        foreach (var revealed in new[] { false, true })
        {
            using var match = new Match("Nidalee", Team.Order, enemyChampion: "Shaco");
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(900, 0);
            match.CastGround(1, hero.Position + new Position2(500, 0), 1);
            Advance(match, 2.5);
            var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
            TestAssert.True(box.Buffs.Any(buff => buff.Payload.Stealthed), "real box has armed unseen");
            if (revealed)
                runtime.SpellBuffs.Add(hero, "OracleElixirSight", Amberline.Core.Buffs.BuffType.Aura, 10,
                    hero.NetId, new Amberline.Core.Buffs.BuffPayload());
            var boxHealth = box.Health;
            runtime.SpellBuffs.Add(enemy, "RegressionStealth", Amberline.Core.Buffs.BuffType.Invisibility, 10,
                enemy.NetId, new Amberline.Core.Buffs.BuffPayload { Stealthed = true });
            TestAssert.Equal(revealed, runtime.Spells.CanSeeTarget(hero, box));
            var health = enemy.Health;
            // The javelin is a script line missile in the shared projectile queue (Spells.Tick), not a late champion tick.
            match.CastGround(0, enemy.Position); Advance(match, 1.25);
            TestAssert.Equal(revealed, box.Health < boxHealth, "NID-01: only revealed box intercepts");
            TestAssert.Equal(!revealed, enemy.Health < health, "stealthed heroes remain eligible behind unseen boxes");
        }
    }

    [Check("spells-fog-annie-projectile")]
    public static void AnnieFogFlight() => CheckFogFlights("Annie", 0);
    [Check("spells-fog-orianna-projectiles")]
    public static void OriannaFogFlights() => CheckFogFlights("Orianna", 0, 2);
    [Check("spells-fog-leesin-projectiles")]
    public static void LeeSinFogFlights()
    {
        // 19 October 2026: the second half of this check (a late observer sees the Q2 / W dash's particle by the dash's EffectNetId, and
        // its completion retires it) is removed. The effect id was a field of the hand-written LeeSinDashState; the script dashes
        // (BlindMonkQTwoDash, BlindMonkWOneDash) create their particle in the buff and keep its id in a script variable, and the late-sight
        // rebuild of a dash keys on the move's effect name now (see FourFeedbackLeeDashChecks).
        CheckFogFlights("LeeSin", 0, 2);
    }
    [Check("spells-fog-nidalee-projectile")]
    public static void NidaleeFogFlight() => CheckFogFlights("Nidalee", 0);
    [Check("spells-fog-shaco-projectile")]
    public static void ShacoFogFlight() => CheckFogFlights("Shaco", 2);
    [Check("spells-fog-ezreal-projectiles")]
    public static void EzrealFogFlights() => CheckFogFlights("Ezreal", 0, 1, 2, 3);
    [Check("spells-fog-vayne-projectile")]
    public static void VayneFogFlight() => CheckFogFlights("Vayne", 2);
    // Janna's Zephyr missile is an ordinary shared projectile. The Howling Gale wave is a line missile cast by the script's placemarker TestCube,
    // which no client is told of; the engine presents that missile through a temporary caster projection of the replication layer.
    [Check("spells-fog-janna-projectiles")]
    public static void JannaFogFlights() => CheckFogFlights("Janna", 1);
    [Check("spells-fog-blitzcrank-projectile")]
    public static void BlitzcrankFogFlight() => CheckFogFlights("Blitzcrank", 0);

    private static void CheckFogFlights(string champion, params int[] slots)
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in slots)
        {
            using var match = new Match(champion, team, ally: champion == "Orianna");
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var targeted = champion is "Annie" or "Shaco" or "Vayne" ||
                champion == "Janna" || champion == "Ezreal" && slot == 2 ||
                champion == "LeeSin" && slot == 2;
            enemy.Position = targeted ? new Position2(7400, 7000) : new Position2(7400, 7500);
            if (champion == "Orianna") match.Hero(2).Position = new Position2(7800, 7000);
            runtime.Delivery.Flush();
            if (champion == "LeeSin" && slot == 2)
            {
                match.CastGround(0, hero.Position, slot);
                runtime.Tick(.45);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BlindMonkEManager"), "Tempest opens Cripple");
                match.CastGround(0, hero.Position, slot);
            }
            else if (champion == "Orianna" && slot == 2) match.Cast(0, slot, match.Hero(2));
            else if (champion == "Ezreal" && slot == 2) match.CastGround(0, hero.Position, slot);
            else if (targeted) match.Cast(0, slot, enemy);
            else match.CastGround(0, new Position2(8000, 7000), slot);

            for (var tick = 0; tick < 250 && ActiveFogMissile(match).Id == 0; tick++)
                runtime.Tick(.01);
            var launched = ActiveFogMissile(match);
            var context = $"{champion} slot {slot} {team}";
            TestAssert.True(launched.Id != 0, $"{context}: authoritative missile launched");
            // A new observer missed the cast. Moving the owner away also
            // verifies that flight presentation does not require its sight.
            hero.Position = new Position2(1000, 14000);
            if (targeted) enemy.Position += new Position2(100, 0);
            var lateCapture = new Capture();
            var late = new ReplicationObserver(90, enemy.Team, enemy.NetId, lateCapture);
            late.View.Known.UnionWith(runtime.Session.Participants.Select(p => p.Hero!.NetId));
            runtime.Delivery.AddObserver(late);
            var ownerCreates = match.Captures[0].Packets.Count(p => Opcode(p) == 0x41);
            runtime.Tick(.01);
            var live = ActiveFogMissile(match);
            var packet = lateCapture.Packets.SingleOrDefault(p => Opcode(p) == 0x41);
            TestAssert.True(packet is not null, $"{context}: late viewer receives live flight");
            TestAssert.Equal(launched.Id, live.Id, $"{context}: still live after reveal");
            TestAssert.Equal(live.Id, BinaryPrimitives.ReadUInt32LittleEndian(packet!.Plaintext.Span[(0x62 + 19)..]), context);
            var missileName = champion switch
            {
                "Annie" => "Disintegrate", "Orianna" => slot == 0 ? "OrianaIzuna" : "OrianaRedact",
                "LeeSin" => slot == 0 ? "BlindMonkQOne" : "BlindMonkETwoMissile",
                "Nidalee" => "JavelinToss", "Shaco" => "TwoShivPoison", "Vayne" => "VayneCondemnMissile",
                "Janna" => "SowTheWind", "Blitzcrank" => "RocketGrabMissile",
                _ => new[] { "EzrealMysticShotMissile", "EzrealEssenceFluxMissile", "EzrealArcaneShiftMissile", "EzrealTrueshotBarrage" }[slot],
            };
            TestAssert.Equal(SpellSystem.ProtocolHash(missileName),
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[(0x62 + 2)..]), $"{context}: missile asset");
            if (targeted)
                TestAssert.Near(enemy.Position.X, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[65..]), .001,
                    $"{context}: homing destination follows moving target");
            TestAssert.Near(live.Position.X, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[5..]), .001, context);
            TestAssert.Near(live.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[13..]), .001, context);
            TestAssert.False(late.View.Visible.Contains(hero.NetId), $"{context}: caster stays hidden");
            TestAssert.False(lateCapture.Packets.Any(p => Opcode(p) == 0xBD), $"{context}: launch is not replayed");
            TestAssert.Equal(ownerCreates, match.Captures[0].Packets.Count(p => Opcode(p) == 0x41),
                $"{context}: existing owner missile is not duplicated");
            runtime.Tick(.01);
            TestAssert.Equal(1, lateCapture.Packets.Count(p => Opcode(p) == 0x41), $"{context}: one creation per ID");
            runtime.Tick(12);
            TestAssert.False(late.View.Effects.Contains(launched.Id), $"{context}: retirement clears view");
            TestAssert.False(late.View.LateMissiles.Contains(launched.Id), $"{context}: retirement clears late-flight bookkeeping");
            TestAssert.Equal(1, lateCapture.Packets.Count(p => Opcode(p) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == launched.Id),
                $"{context}: exactly one destroy");
            if (champion == "Annie")
                TestAssert.False(match.Captures[0].Packets.Any(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == launched.Id),
                    "native cast owner retains implicit Q impact cleanup");
        }
    }

    private static (uint Id, Position2 Position) ActiveFogMissile(Match match)
    {
        var hero = match.Hero(0);
        var shared = match.Runtime.Spells.SharedProjectiles.Select(p => p.Snapshot)
            .FirstOrDefault(p => p.CasterNetId == hero.NetId && p.Launched && p.MissileNetId != 0);
        return (shared.MissileNetId, shared.Position);
    }

    [Check("spells-fog-recall-teleport-home-late-sight")]
    public static void RecallParticleAppearsOnLateSightAndRetires()
    {
        // playtest B2 OS1: Recall.luaobj binds TeleportHome to the caster with
        // SendIfOnScreenOrDiscard=false, so an enemy arriving mid-channel sees it.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var view = match.Runtime.Session.Participants[1].View;
            enemy.Position = new Position2(5000, 5000);
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, SpellSystem.RecallSlot, hero);
            // The channel begins at the cast frame, CastTime 0.5 s after the cast; its ChannelingStart creates the effect (batch 179).
            match.Runtime.Tick(0.6);
            var id = match.Runtime.Spells.RecallEffectId(hero);
            TestAssert.True(id != 0 && match.Runtime.Spells.RecallRemaining(hero) > 0, $"{team}: recall channel starts");
            TestAssert.Equal(0, EffectCreates(match.Captures[1], id), $"{team}: hidden recall stays hidden");

            enemy.Position = hero.Position + new Position2(300, 0);
            match.Runtime.Delivery.Flush();
            TestAssert.True(view.Visible.Contains(hero.NetId), $"{team}: the enemy sees the channel");
            TestAssert.Equal(1, EffectCreates(match.Captures[1], id), $"{team}: late sight creates TeleportHome");
            var effect = match.Captures[1].Packets.Single(p => Opcode(p) == 0x8C &&
                p.EntityReferences.Any(r => r.NetId == id && r.Role == LeaguePacketEntityRole.Create));
            TestAssert.Equal(SpellPackets.BuffNameHash("TeleportHome"),
                BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[6..]), $"{team}: recall asset");
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(1, EffectCreates(match.Captures[1], id), $"{team}: no duplicate on the next flush");
            TestAssert.Equal(1, EffectCreates(match.Captures[0], id), $"{team}: owner not duplicated");

            // Batch 47 (a84f249): an order inside Recall's uncancellable 0.5 s windup is held and applied 0.1 s after the
            // windup, so the cancelling stop reaches a client whose own windup is over.
            // Batch 179: the channel started at the cast frame, so the order arrives inside the running channel and cancels it (the held
            // windup order is pinned by HeroAttackMoveChecks).
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, $"{team}: the channel runs before the order");
            match.Move(0, hero.Position + new Position2(0, 200));
            match.Runtime.Tick(0.7);
            TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, $"{team}: a fresh order cancels recall");
            TestAssert.Equal(1, match.Captures[1].Packets.Count(p => Opcode(p) == FxPackets.KillOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[5..]) == id),
                $"{team}: cancel kills the late observer's particle");
            TestAssert.False(view.Effects.Contains(id), $"{team}: cancel retires the effect from the view");
        }
    }

    private static int EffectCreates(Capture capture, uint id) => capture.Packets.Count(p =>
        Opcode(p) == 0x8C && p.EntityReferences.Any(r => r.NetId == id && r.Role == LeaguePacketEntityRole.Create));

    [Check("spells-fog-yorick-summoned-actor")]
    public static void YorickSummonUsesActorSightIndependentlyOfOwner()
    {
        using var match = new Match("Yorick", Team.Order);
        var hero = match.Hero(0);
        match.Cast(0, 3, hero);
        match.Runtime.Tick(.3);
        var clone = match.Runtime.World.Entities.Values.Single(e => e.Kind == EntityKind.YorickRevenant);
        hero.Position = new Position2(1000, 14000);
        var capture = new Capture();
        var late = new ReplicationObserver(90, Team.Chaos, match.Hero(1).NetId, capture);
        late.View.Known.UnionWith(match.Runtime.Session.Participants.Select(p => p.Hero!.NetId));
        match.Runtime.Delivery.AddObserver(late);
        match.Runtime.Delivery.Flush();
        TestAssert.True(late.View.Known.Contains(clone.NetId) && late.View.Visible.Contains(clone.NetId),
            "summoned actor is created and seen independently of its hidden owner");
        TestAssert.False(late.View.Visible.Contains(hero.NetId), "summon does not reveal owner");
    }
}
