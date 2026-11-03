using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-leesin-b5-q-stealth-collisions")]
    public static void LeeQRespectsScriptStealthCollisionBranches()
    {
        foreach (var revealed in new[] { false, true })
        {
            using var match = new Match("LeeSin", Team.Order);
            var lee = match.Hero(0); var hero = match.Hero(1);
            hero.Position = lee.Position + new Position2(600, 0);
            var minion = new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Chaos, lee.Position + new Position2(250, 0))
            {
                Alive = true, Created = true, Visible = true, Health = 1000, MaxHealth = 1000, Radius = 30,
            };
            match.Runtime.World.Register(minion);
            foreach (var unit in new[] { hero, minion })
                match.Runtime.SpellBuffs.Add(unit, "StealthProbe", BuffType.Internal, 10, unit.NetId,
                    new BuffPayload { Stealthed = true }, canMitigateDuration: false);
            // 19 October 2026: the mark's sight is the script's 50 unit true-sight bubble on the marked unit (BlindMonkQOne.OnBuffActivate), not a buff name.
            if (revealed)
                match.Runtime.World.AddTimedVisionBubble(lee.Team, minion.Position, 50.0, 3.0, revealStealth: true, anchorNetId: minion.NetId);
            TestAssert.Equal(revealed, match.Runtime.Spells.CanSeeTarget(lee, minion));
            var heroHp = hero.Health; var minionHp = minion.Health;
            match.CastGround(0, hero.Position);
            SpellStep(match, .25); SpellStep(match, .6);
            TestAssert.Equal(revealed, minion.Health < minionHp,
                "Q hits a stealthed non-hero only when the caster can see it");
            TestAssert.Equal(!revealed, hero.Health < heroHp,
                "an unseen stealthed hero remains hittable behind the ignored non-hero");
        }
    }

    // 19 October 2026: spells-leesin-b5-kick-final-segment is removed. It pinned the hand-written kick's final-segment sweep (LeeSinKickState: the
    // last scanned position, one rank-one collateral hit for the final 200 units, the primary kick retiring after its scan). The script kick has no
    // segment: BlindMonkRKick.BuffOnUpdateActions strikes every enemy within 160 of the kicked unit at each update, once (BlindMonkRMarker).

    [Check("spells-live-power-fist-lethal-recap-and-respawn-anchor")]
    public static void PowerFistLethalHitKeepsSpellOriginAndRespawnReanchors()
    {
        using var match = new Match("Blitzcrank", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Health = 1.0;

        // The real champion cast arms the one-shot buff. The hit is one basic attack whose damage the script's BuffOnHitUnit raises
        // (PowerFist.luaobj adds no damage call of its own), so the recap names the attack, not the spell: coordinator decision of
        // 19 October 2026, "the death recap follows the scripts" (the hand-written Power Fist named itself).
        match.Cast(0, 2, hero);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "PowerFist"),
            "Power Fist arms before the lethal hit");
        TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId,
            hero.AttackDamage, DamageType.Physical, DamageSourceKind.Basic),
            "the empowered lethal hit lands");
        var recap = match.Runtime.Combat.GetLastDeathRecap(enemy.NetId)
            ?? throw new InvalidOperationException("lethal Power Fist hit produced no recap");
        var row = recap.Records.Single();
        TestAssert.True(row.Origin.EventSource != DeathRecapEventSource.Spell,
            "the empowered hit is recorded as the basic attack, not as a named spell");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "PowerFist"),
            "the lethal hit consumes Power Fist");
        TestAssert.True(hero.SpellCooldowns[2] > 0.0,
            "the lethal hit starts Power Fist cooldown");

        // A new life must not inherit the dead route or target anchor.
        enemy.Waypoints.Add(enemy.Position + new Position2(900.0, 0.0));
        enemy.Target = enemy.Position + new Position2(900.0, 0.0);
        enemy.AttackTarget = hero.NetId;
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(enemy),
            "the dead victim respawns");
        TestAssert.Equal(1, enemy.AnchorPath.Count,
            "respawn leaves one authoritative anchor");
        TestAssert.Equal(enemy.Position, enemy.AnchorPath[0],
            "respawn anchor is the new base position");
        TestAssert.Equal(0, enemy.Waypoints.Count,
            "respawn clears the dead route");
        TestAssert.True(enemy.Target is null && enemy.AttackTarget is null,
            "respawn clears stale target state");
    }

    [Check("spells-live-empowered-sibling-recap-provenance")]
    public static void VayneTumbleLethalHitKeepsSpellOrigin()
    {
        using var match = new Match("Vayne", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Health = 1.0;

        match.Cast(0, 0, enemy);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "VayneTumbleBonus"),
            "Vayne Tumble arms its empowered attack");
        TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId,
            hero.AttackDamage, DamageType.Physical, DamageSourceKind.Basic),
            "the empowered Vayne hit lands");
        var recap = match.Runtime.Combat.GetLastDeathRecap(enemy.NetId)
            ?? throw new InvalidOperationException("lethal Vayne hit produced no recap");
        var row = recap.Records.Single();
        var expected = DeathRecapOrigin.ChampionSpell(hero, 0, 1);
        TestAssert.Equal(expected.SourceScriptNameHash, row.Origin.SourceScriptNameHash,
            "Tumble keeps its spell script hash in the recap");
        TestAssert.Equal(expected.SourceSpellLevel, row.Origin.SourceSpellLevel,
            "Tumble keeps its learned rank in the recap");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "VayneTumbleBonus"),
            "the lethal hit consumes Tumble's empowerment");
    }

    [Check("spells-live-janna-tailwind-renewal")]
    public static void TailwindRenewalKeepsOneLiveAllyBuff()
    {
        // CharScriptJanna: CharOnActivate gives the allies that exist then 5 s of Tailwind and UpdateSelfBuffActions renews it every 4 s with
        // BUFF_RENEW_EXISTING (BBExecutePeriodically seeds its timer at the first update), so one live instance is kept and no remove packet is sent.
        using var match = new Match("Janna", Team.Order, ally: true);
        var runtime = match.Runtime;
        var janna = match.Hero(0);
        var ally = match.Hero(2);
        runtime.Tick(.01);
        runtime.Tick(4.2);
        var first = ally.Buffs.Single(buff => buff.Name == "Tailwind");
        var slot = first.Slot;
        var speed = ally.Speed;
        match.Clear();

        runtime.Tick(4.2);

        var renewed = ally.Buffs.Single(buff => buff.Name == "Tailwind");
        TestAssert.True(ReferenceEquals(first, renewed), "Tailwind renews the live buff instance");
        TestAssert.Equal(slot, renewed.Slot, "Tailwind renewal keeps the buff slot");
        TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "Tailwind"));
        TestAssert.Near(speed, ally.Speed, 1e-9,
            "Tailwind renewal does not double-apply movement speed");
        var tailwindHash = SpellPackets.BuffNameHash("Tailwind");
        TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
            Opcode(packet) == 0x7F && packet.Plaintext.Length >= 10 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == ally.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == tailwindHash),
            "Tailwind renewal emits no remove packet");
        TestAssert.Equal(janna.NetId, renewed.CasterNetId,
            "Tailwind renewal retains its Janna caster");
    }

    [Check("spells-live-training-and-level-feedback-protocol")]
    public static void TrainingAndLevelFeedback()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var hero = match.Hero(0);
            hero.SpellRanks[0] = 0; hero.SkillPoints = 1;
            var request = new byte[6]; request[0] = 0x3E;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            runtime.Session.Receive(1, 1, runtime.Cipher.Encrypt(request), runtime.Dependencies.Gameplay);
            runtime.Delivery.Flush();
            TestAssert.Equal(1, hero.SpellRanks[0], "one accepted training request adds one rank");
            TestAssert.Equal(0, hero.SkillPoints, "training spends one point");
            TestAssert.Equal(1, hero.Level, "training does not level the champion");
            foreach (var capture in match.Captures)
            {
                var packet = capture.Packets.Single(packet => Opcode(packet) == 0x18).Plaintext.ToArray();
                TestAssert.Equal(new byte[] { 0, 1, 0 }, packet[5..], "native slot/rank/points fields");
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x5E), "no duplicate rank increment packet");
            }
            match.Clear();
            runtime.Combat.AwardHeroProgress(hero, experience: 280);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var packet = capture.Packets.Single(packet => Opcode(packet) == 0x45).Plaintext.ToArray();
                TestAssert.Equal(new byte[] { 2, 1 }, packet[5..], "native level-up VFX/sound handler gets level and available points");
            }
        }
    }

    [Check("spells-live-dash-orders-and-fog-cleanup")]
    public static void DashOrdersAndFogCleanup()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in new[] { 0, 1 })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var target = match.Hero(slot == 0 ? 1 : 2);
            target.Position = new Position2(7600, 7000);
            if (slot == 0) { match.Cast(0, 0, target); runtime.Tick(1.0); }
            match.Clear(); lee.Mana = 200;
            match.Cast(0, slot, target);
            TestAssert.True(lee.ForcedMove is { Completed: false }, "Q2/W begin immediately");
            TestAssert.Equal(0, runtime.HeroStats.Snapshot(lee).ActionState & 5,
                "dash replication disables movement and attacks");
            var move = lee.ForcedMove;
            var before = lee.Position;
            match.Clear();
            match.Move(0, new Position2(6400, 7500));
            TestAssert.True(ReferenceEquals(move, lee.ForcedMove), "move click retains dash");
            TestAssert.Equal(before, lee.Position, "move click does not reconcile dash position");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
                "move click sends no ordinary waypoint to interrupt client dash");
            SendHeroOrder(match, 7, new Position2(6400, 7500));
            TestAssert.True(ReferenceEquals(move, lee.ForcedMove) && lee.AttackMoveGoal is null,
                "attack-move cannot install an order during the dash");
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
                "attack-move does not interrupt the native dash animation");
            for (var tick = 0; lee.ForcedMove is not null && tick < 25; tick++) runtime.Tick(.1);
            TestAssert.True(lee.ForcedMove is null, "dash terminates");
            TestAssert.Equal(5, runtime.HeroStats.Snapshot(lee).ActionState & 5, "landing restores permissions");
        }
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 3, enemy); runtime.Tick(.3);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlindMonkRKick"), "kick buff installed");
            lee.Position = new Position2(4000, 4000);
            runtime.Tick(.4); match.Clear(); runtime.Tick(.7);
            TestAssert.False(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "victim remains in fog");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x7F &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == enemy.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("BlindMonkRKick")), // Batch 357: the binding name (the BuffName was BlindMonkDragonsRage)
                "kick removal reaches the observer that saw it before fog");
        }
        // Playtest B2 NL2: an out-of-range R requested during Q2's dash
        // queues without a waypoint (it overwrote the client's dash) and
        // fires only after the landing, from the landing point.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new Position2(7700, 7000);
            match.Cast(0, 0, enemy); runtime.Tick(1.0);
            lee.Mana = 200;
            match.Cast(0, 0, enemy);
            TestAssert.True(lee.ForcedMove is { Completed: false }, "Q2 dashes at the marked enemy");
            runtime.Tick(1.0 / 30.0);
            TestAssert.True(lee.ForcedMove is not null &&
                Distance(lee.Position, enemy.Position) > GameData.Default.Spell("BlindMonkRKick").CastRange,
                "R is requested mid-dash from outside its range");
            match.Clear();
            match.Cast(0, 3, enemy);
            TestAssert.True(lee.PendingCast is not null, "the out-of-range R queues behind the dash");
            bool LeeRoute() => match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == lee.NetId);
            bool RCast() => lee.SpellCooldowns[3] > 0.0 ||
                match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD);
            for (var tick = 0; lee.ForcedMove is not null && tick < 60; tick++)
            {
                TestAssert.False(LeeRoute(), "no Lee waypoint overwrites the client's Q2 dash");
                TestAssert.False(RCast(), "the queued R does not fire mid-dash");
                runtime.Tick(1.0 / 30.0);
            }
            TestAssert.True(lee.ForcedMove is null, "Q2 lands");
            var landing = lee.Position;
            if (!RCast()) runtime.Tick(1.0 / 30.0);
            // Batch 340: the cast packet is the fire; R's cooldown starts at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell).
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                "the queued R fires within one tick of the landing");
            TestAssert.True(lee.PendingCast is null, "the queued R is consumed");
            TestAssert.True(Distance(lee.Position, landing) < 1.0, "R fires from the landing point");
        }
        // Playtest B3 #10 (owner decision, 26 Sept 2026): an in-range R during
        // Q2 also queues. Q2's locked Spell1b made the stock client drop R's
        // gesture; it fires only after the landing's 0x2B unlock, and the
        // landing hands back no attack that could swing first.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new Position2(7700, 7000);
            match.Cast(0, 0, enemy); runtime.Tick(1.0);
            lee.Mana = 200;
            match.Cast(0, 0, enemy);
            var range = GameData.Default.Spell("BlindMonkRKick").CastRange;
            for (var tick = 0; lee.ForcedMove is not null &&
                 Distance(lee.Position, enemy.Position) > range && tick < 60; tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(lee.ForcedMove is { Completed: false } &&
                Distance(lee.Position, enemy.Position) <= range,
                "R is requested mid-dash from inside its range");
            match.Clear();
            match.Cast(0, 3, enemy);
            TestAssert.True(lee.PendingCast is not null, "the in-range R queues behind the dash");
            bool RCast() => lee.SpellCooldowns[3] > 0.0 ||
                match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD);
            for (var tick = 0; lee.ForcedMove is not null && tick < 60; tick++)
            {
                TestAssert.False(RCast(), "the in-range R does not fire mid-dash");
                runtime.Tick(1.0 / 30.0);
            }
            TestAssert.True(lee.ForcedMove is null, "Q2 lands");
            if (!RCast()) runtime.Tick(1.0 / 30.0);
            bool LeePacket(PlaintextPacket packet, byte opcode) => Opcode(packet) == opcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == lee.NetId;
            var packets = match.Captures[0].Packets;
            var unlock = packets.FindIndex(packet => LeePacket(packet, 0x2B));
            var cast = packets.FindIndex(packet => Opcode(packet) == 0xBD);
            TestAssert.True(unlock >= 0 && cast > unlock, "the landing 0x2B unlock precedes R's cast");
            TestAssert.False(packets.Take(cast).Any(packet => LeePacket(packet, 0x0D)),
                "no Lee basic attack precedes the queued R");
            // Batch 340: the cast packet is the fire (checked above); R's cooldown starts at its cast frame.
            TestAssert.True(lee.PendingCast is null, "the queued R fires within one tick of the landing");
        }
        // Summoners share the dash queue (B3 #10). SummonerFlash's CanCast
        // needs CanMove, which the dash clears, so it is refused outright;
        // ungated Ignite requested from 700 units waits for the landing.
        using (var match = new Match("LeeSin", Team.Order))
        {
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new Position2(7700, 7000);
            match.Cast(0, 0, enemy); runtime.Tick(1.0);
            lee.Mana = 200;
            match.Cast(0, 0, enemy);
            var dashPoint = lee.Position;
            TestAssert.False(match.TryCast(0, 0, enemy, summoner: true), "Flash is refused during Q2");
            TestAssert.True(lee.PendingCast is null && lee.SummonerCooldowns[0] == 0.0 &&
                lee.Position == dashPoint && lee.ForcedMove is { Completed: false },
                "refused Flash neither queues, spends cooldown nor moves Lee");
            match.Cast(0, 1, enemy, summoner: true);
            TestAssert.True(lee.PendingCast is { Request.IsSummoner: true } &&
                lee.SummonerCooldowns[1] == 0.0 && !enemy.Buffs.Any(buff => buff.Name == "SummonerDot"),
                "Ignite queues behind Q2");
            for (var tick = 0; lee.ForcedMove is not null && tick < 60; tick++) runtime.Tick(1.0 / 30.0);
            runtime.Tick(1.0 / 30.0);
            TestAssert.True(lee.PendingCast is null && lee.SummonerCooldowns[1] > 0.0 &&
                enemy.Buffs.Any(buff => buff.Name == "SummonerDot"), "queued Ignite fires after the landing");
        }
        // A newer move click during the dash replaces the queued cast, as any
        // newer order does in retail; the click itself is still dropped.
        using (var match = new Match("LeeSin", Team.Order))
        {
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new Position2(7700, 7000);
            match.Cast(0, 0, enemy); runtime.Tick(1.0);
            lee.Mana = 200;
            match.Cast(0, 0, enemy);
            match.Cast(0, 3, enemy);
            TestAssert.True(lee.PendingCast is not null, "R queues behind Q2");
            var dash = lee.ForcedMove;
            match.Move(0, new Position2(6400, 7500));
            TestAssert.True(lee.PendingCast is null && ReferenceEquals(dash, lee.ForcedMove),
                "the move click cancels the queued R and keeps the dash");
            match.Clear();
            for (var tick = 0; tick < 60; tick++) runtime.Tick(1.0 / 30.0);
            TestAssert.True(lee.ForcedMove is null, "Q2 lands");
            TestAssert.False(lee.SpellCooldowns[3] > 0.0 ||
                match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                "the replaced R never fires after the landing");
        }
    }

    [Check("spells-live-point-blank-q-missile")]
    public static void PointBlankMissile()
    {
        foreach (var distance in new[] { 1.0, 30.0, 80.0, 150.0 })
        {
            using var match = new Match("LeeSin", Team.Order);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = lee.Position + new Position2(distance, 0);
            match.Cast(0, 0, enemy);
            runtime.Tick(.4);
            var packets = match.Captures[0].Packets;
            // 19 October 2026 (batch 105): the cast packet creates the missile on the client (its id is in the cast info), so the cast is the creation.
            var spawn = packets.Single(packet => Opcode(packet) == 0xBD);
            var missileId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.Plaintext.Span[(9 + 0x13)..]);
            TestAssert.True(missileId != 0, "the cast names the missile it creates");
            var destroy = packets.Single(packet => Opcode(packet) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missileId);
            TestAssert.True(packets.IndexOf(spawn) < packets.IndexOf(destroy), "create precedes destroy even at point blank");
            TestAssert.Equal(spawn.Channel, destroy.Channel, "missile lifecycle shares ordered channel");
            TestAssert.Equal(LeaguePacketReliability.Reliable, spawn.Reliability, "creation reliable");
            TestAssert.Equal(LeaguePacketReliability.Reliable, destroy.Reliability, "destruction reliable");
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlindMonkQOne"), "Q hits and marks once");
            TestAssert.Equal(0, LeeFlights(match, lee).Length, "no continuing projectile");
        }
    }

    [Check("spells-live-janna-q-recast-origin-and-knockup")]
    public static void JannaQRecastOriginAndKnockup()
    {
        // HowlingGale: the cast adds the charge buff and makes Q self-targeted at once; the recast removes the buff, and its OnBuffDeactivate
        // restores location targeting, sets the cooldown and spawns the TestCube at the position the buff began at, whatever the walking hero
        // has done since. The wave knocks the victim up with the global Move buff.
        using var match = new Match("Janna", Team.Order);
        var runtime = match.Runtime;
        var janna = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(janna.Position.X + 300.0, janna.Position.Z);
        var chargeOrigin = janna.Position;

        match.Cast(0, 0, enemy);
        TestAssert.True(JannaCharging(janna), "HowlingGale's cast adds the charge buff");
        TestAssert.Equal(0, janna.SpellTargetingOverrides[0], "the charge makes Q self-targeted");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x1A &&
            packet.Plaintext.Span[^1] == 0), "charge changes Q to native self targeting");
        TestAssert.Near(0, janna.SpellCooldowns[0], 1e-9, "the cooldown reads zero while charging");

        janna.Position = chargeOrigin + new Position2(40.0, 25.0);
        var walkingPosition = janna.Position;
        match.Clear();
        match.Cast(0, 0, enemy);
        TestAssert.Equal(walkingPosition, janna.Position,
            "releasing Q does not translate its authoritative hero");
        TestAssert.False(JannaCharging(janna), "the recast removes the charge buff");
        TestAssert.Equal(6, janna.SpellTargetingOverrides[0], "release restores Q location targeting");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x1A &&
            packet.Plaintext.Span[^1] == 6), "release restores Q location targeting on the wire");
        TestAssert.Near(14, janna.SpellCooldowns[0], .05, "the release starts the rank one cooldown");
        var cube = JannaCubes(match).Single();
        TestAssert.Near(chargeOrigin.X, cube.Position.X, 1.0, "the TestCube stands where the charge began (X)");
        TestAssert.Near(chargeOrigin.Z, cube.Position.Z, 1.0, "the TestCube stands where the charge began (Z)");

        var move = AwaitForcedMove(match, enemy, "Move");
        TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "Move" && buff.Type == BuffType.Stun),
            "Howling Gale applies the shipped Move knockup buff");
        TestAssert.True(move is not null, "Howling Gale starts the victim forced movement");
    }

    [Check("spells-live-janna-q-testcube-tier-spell-contract")]
    public static void JannaQTestCubeHasTierSpellForEveryObserver()
    {
        // The buff's LifeTime picks the tier: up to 1 s HowlingGaleSpell, up to 2 s ...1, up to 3 s ...2, else (the buff's natural end) ...3.
        // BBSetSpell puts the tier into the cube's extra slot 42 and the cube wears the buff of the same name. The cube is a placemarker: no
        // observer is ever told of it.
        foreach (var (tier, chargeSeconds) in new[] { (0, 0.0), (1, 1.2), (2, 2.2), (3, 3.01) })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime;
            var janna = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = janna.Position + new Position2(900, 0);
            match.Cast(0, 0, enemy);
            janna.Position += new Position2(40, 25);
            if (chargeSeconds > 0) Advance(match, chargeSeconds);
            if (tier < 3) match.Cast(0, 0, enemy);
            var spellName = tier == 0 ? "HowlingGaleSpell" : $"HowlingGaleSpell{tier}";
            var cube = JannaCubes(match).SingleOrDefault()
                ?? throw new InvalidOperationException($"tier {tier} did not launch");
            TestAssert.Equal(spellName, cube.ActiveSpellNames[Amberline.Simulation.Spells.SpellSystem.ExtraSpellSlotStart],
                "charge selects the shipped wave tier in the cube's extra slot");
            TestAssert.True(cube.Buffs.Any(buff => buff.Name == spellName), "the cube wears the tier's buff");
            TestAssert.False(runtime.GameData.Champion("TestCube").Raw.Values.Any(value =>
                value.Kind == InibinValueKind.String && value.AsString() == spellName),
                "TestCube cannot seed the wave spell from its own character data");
            TestAssert.True(cube.Announced, "the placemarker is announced like a minion (Entity.Announced)");
            TestAssert.True(cube.NetId != janna.NetId, "the wave never substitutes Janna for its helper");
        }
    }

    [Check("spells-live-janna-q-death-restores-targeting")]
    public static void JannaQDeathRestoresLocationTargeting()
    {
        // The charge buff does not persist through death: its OnBuffDeactivate restores location targeting and still releases the wave from
        // where she stood.
        using var match = new Match("Janna", Team.Order);
        var runtime = match.Runtime;
        var janna = match.Hero(0);
        match.Cast(0, 0, match.Hero(1));
        TestAssert.True(JannaCharging(janna), "Janna Q is charging");
        TestAssert.Equal(0, janna.SpellTargetingOverrides[0], "the charge makes Q self-targeted");
        TestAssert.True(runtime.Combat.Kill(janna, match.Hero(1).NetId), "Janna dies while charging");
        TestAssert.False(JannaCharging(janna), "the charge ends with her");
        TestAssert.Equal(6, janna.SpellTargetingOverrides[0], "dead charge restores Q location targeting");
        TestAssert.Equal(1, JannaCubes(match).Count, "the wave is still released from where she stood");
    }

    [Check("spells-live-champion-animation-order")]
    public static void ChampionCastAnimationOrder()
    {
        foreach (var (champion, slot) in new[]
        {
            ("Ezreal", 0), ("Blitzcrank", 0), ("Janna", 0), ("Vayne", 0),
        })
        {
            using var match = new Match(champion, Team.Order);
            match.Cast(0, slot, match.Hero(1));
            var packets = match.Captures[0].Packets;
            // VFX sweep, 27 Sept 2026: Ezreal Q (EzrealMysticShotMissile 0x05FE9CE5) and
            // Blitzcrank Q (RocketGrabMissile) now play their clip and .fx
            // through a normal child cast (0xBD) instead of an explicit 0xB8.
            uint? childHash = champion switch { "Ezreal" => 0x05FE9CE5u, "Blitzcrank" => 0x00DD4395u, _ => null };
            var animationIndex = childHash is { } child
                ? packets.FindIndex(packet => Opcode(packet) == 0xBD &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == child)
                : packets.FindIndex(packet => Opcode(packet) == 0xB8);
            TestAssert.True(animationIndex >= 0, $"{champion} Q emits its animation packet");
            TestAssert.False(packets.Skip(animationIndex + 1).Any(packet => Opcode(packet) == 0xC1),
                $"{champion} Q has no stop waypoint after its animation");
        }
    }

    [Check("spells-live-vayne-dead-animation-idempotence")]
    public static void VayneDeadTickStopsTumbleOnce()
    {
        using var match = new Match("Vayne", Team.Order);
        var runtime = match.Runtime;
        match.Hero(0).Alive = false;
        match.Clear();
        runtime.Tick(.1);
        runtime.Tick(.1);
        TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
            .Any(packet => Opcode(packet) == 0x2B),
            "dead Vayne without a tumble does not emit repeated stop-animation packets");
    }

    [Check("spells-live-vayne-r-level-gate-and-cast")]
    public static void VayneUltimateFreshSpawnLevelFlow()
    {
        using var match = new Match("Vayne", Team.Order, initializeSpells: false);
        var runtime = match.Runtime;
        var vayne = match.Hero(0);
        vayne.SkillPoints = 1;
        TestAssert.Equal(0, vayne.SpellRanks[3], "fresh Vayne R starts unlearned");
        TestAssert.False(runtime.Spells.CanRankUp(vayne, 3).Allowed,
            "fresh level-one Vayne cannot train R");

        vayne.Level = 6;
        TestAssert.True(runtime.Spells.CanRankUp(vayne, 3).Allowed,
            "Vayne R rank one unlocks at level six");
        TestAssert.True(runtime.Spells.RankUp(vayne, 3), "Vayne R rank one trains");
        TestAssert.Equal(1, vayne.SpellRanks[3], "Vayne R rank one persisted");
        match.Clear();
        match.Cast(0, 3, vayne);
        TestAssert.True(vayne.Buffs.Any(buff => buff.Name == "VayneInquisition"),
            "trained Vayne R casts from the fresh-spawn flow");
    }

    [Check("movement-live-hero-route-refresh")]
    public static void HeroRouteRefresh()
    {
        using var match = new Match("LeeSin", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var blocker = match.Hero(1);
        blocker.Position = new Position2(7250, 7000); blocker.Radius = 90;
        var goal = new Position2(7800, 7000);
        match.Move(0, goal);
        var storedGoal = hero.Waypoints[^1];
        TestAssert.True(hero.Waypoints.Any(point => Math.Abs(point.Z - 7000) > 20), "initial route avoids the body");
        blocker.Position = new Position2(6000, 6000);
        var retained = hero.Waypoints.ToArray();
        match.Clear(); runtime.Tick(.3);
        // playtest B4 / retail sweep, 27 Sept 2026: #17 retains a progressing,
        // safe detour when a blocker leaves instead of publishing a shorter route.
        TestAssert.True(hero.Waypoints.SequenceEqual(retained.TakeLast(hero.Waypoints.Count)) &&
            hero.Waypoints[^1] == storedGoal, "the safe route retains its unconsumed suffix and destination");
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xC1),
            "a retained safe route needs no replacement packet");
        var destination = hero.Waypoints[^1];
        runtime.Tick(.3);
        TestAssert.Equal(destination, hero.Waypoints[^1], "destination survives repeated refresh");
    }

    [Check("spells-live-instant-cast-timings")]
    public static void InstantCastTimings()
    {
        foreach (var (champion, slot, buff) in new[] {
            ("Yorick", 0, "YorickSpectral"), ("Blitzcrank", 1, "Overdrive"),
            ("Blitzcrank", 2, "PowerFist"), ("Vayne", 3, "VayneInquisition"),
            ("Nidalee", 3, "AspectOfTheCougar"), ("Annie", 2, "MoltenShield") })
        {
            using var match = new Match(champion, Team.Order);
            match.Cast(0, slot, match.Hero(0));
            TestAssert.True(match.Hero(0).Buffs.Any(item => item.Name == buff), $"{champion} {slot} applies without advancing time");
            TestAssert.False(match.Hero(0).ChampionState?.BlocksActions == true, "no leftover instant cast lock");
            var cast = match.Captures[0].Packets.First(packet => Opcode(packet) == 0xBD).Plaintext.Span;
            var timingOffset = 9 + 0x30 + 17 * cast[9 + 0x2F];
            TestAssert.Near(0, BinaryPrimitives.ReadSingleLittleEndian(cast[timingOffset..]), 0,
                "instant acknowledgement carries zero cast duration");
        }
    }

    [Check("spells-leesin-q2-immediate-dash")]
    public static void ImmediateQ()
    {
        VerifyImmediateQ(1);
        VerifyImmediateQ(5);
    }

    internal static void VerifyImmediateQ(int rank)
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0); var enemy = match.Hero(1);
            lee.SpellRanks[0] = rank;
            enemy.Position = new Position2(7600, 7000);
            runtime.Delivery.Flush();
            match.Cast(0, 0, enemy);
            runtime.Tick(1.0);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name.StartsWith("BlindMonkQOne")), "Q1 marked target");
            match.Clear();
            lee.Mana = 200;
            var position = lee.Position;
            var time = runtime.World.Clock.Now();
            match.Cast(0, 0, enemy);
            TestAssert.True(lee.ForcedMove is { Completed: false }, "Q2 starts before any simulation tick");
            TestAssert.True(lee.PendingCast is null, "Q2 has no pending cast lock");
            TestAssert.Equal(position, lee.Position, "instant launch retains dash travel");
            TestAssert.Near(time, runtime.World.Clock.Now(), 1e-9, "no time elapsed");
            TestAssert.Near(170, lee.Mana, 1e-9, "Q2 costs 30 energy once");
            foreach (var capture in match.Captures)
            {
                var dash = capture.Packets.Single(packet => Opcode(packet) == 0x67 && packet.Channel == 4);
                var fields = Decode(dash);
                TestAssert.Equal(lee.NetId, fields.GetProperty("unit_netid").GetUInt32(), "dash actor");
                TestAssert.Equal(enemy.NetId, fields.GetProperty("follow_netid").GetUInt32(), "dash follows mark");
                TestAssert.Near(1675, fields.GetProperty("speed").GetDouble(), 1e-6, "Q2 shipped speed");
                TestAssert.True(capture.Packets.FindIndex(packet => Opcode(packet) == 0xBD) <
                    capture.Packets.IndexOf(dash), "cast acknowledgement precedes immediate dash");
            }
            runtime.Tick(0.1);
            TestAssert.True(lee.Position != position, "dash travels on first movement frame");
            // Freeze regeneration, then finish only as far as Q2's impact so
            // the automatic follow-up attack cannot mask its damage formula.
            enemy.HealthRegen = 0;
            var health = enemy.Health;
            var expectedDamage = ((rank == 1 ? 50 : 170) + .1 * (enemy.MaxHealth - health)) * 100 / (100 + enemy.Armor);
            for (var frame = 0; lee.ForcedMove is not null && frame < 30; frame++) runtime.Tick(1.0 / 30);
            TestAssert.True(lee.ForcedMove is null, "Q2 completes and clears movement");
            TestAssert.Near(expectedDamage, health - enemy.Health, .01, "Q2 base damage plus ten percent missing health");
            TestAssert.True(lee.SpellCooldowns[0] > 0 && lee.AttackTarget == enemy.NetId, "Q2 closes recast and resumes attack");
        }
    }

    // The body of spells-leesin-b5-data-cost-and-damage (ChampionContractChecks). 19 October 2026: it runs on a roster-based match, because a script
    // champion's module binds when its first hero is created; the costs are still the isolated GameData's, and the Q1 damage is now the
    // script's own rank row (BlindMonkQOne: 50, 80, 110, 140, 170), which the inibin effect row the old check altered does not feed.
    internal static void VerifyLeeIsolatedSpellData()
    {
        // This GameData owns its loaded arrays; never mutate the shared Default cache.
        var data = new GameData();
        var first = data.Spell("BlindMonkQOne", 5);
        var second = data.Spell("BlindMonkQTwo", 5);
        ((double[])first.ManaCosts)[1] = 70;
        ((double[])second.ManaCosts)[1] = 17;
        ((InibinValue[])first.Effects[1])[1] = InibinValue.FromDouble(123);
        using var match = new Match("LeeSin", Team.Order, data: data);
        var runtime = match.Runtime;
        var lee = match.Hero(0); var victim = match.Hero(1);
        lee.SpellRanks[0] = 2;
        lee.Mana = 69;
        TestAssert.Near(70, runtime.HeroStats.Snapshot(lee).ManaCosts[0], 0);
        TestAssert.False(match.TryCast(0, 0, victim), "LEE-07: admission uses altered first-cast cost");
        lee.Mana = 70;
        var hp = victim.Health;
        TestAssert.True(match.TryCast(0, 0, victim));
        // Batch 346 (batch 340's rule): Q1 is a windup, so its cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press;
        // the spell steps carry no energy regeneration, so the cast frame leaves exactly what admission allowed.
        TestAssert.Near(70, lee.Mana, 0, "the press spends nothing");
        SpellStep(match, .25); SpellStep(match, .5);
        TestAssert.Near(0, lee.Mana, 0, "spending agrees with admission");
        TestAssert.Near(80, (hp - victim.Health) * (1 + victim.Armor / 100), .001,
            "Q1 reads the script's rank-two damage row, not the altered inibin effect row");
        TestAssert.Near(17, runtime.HeroStats.Snapshot(lee).ManaCosts[0], 0,
            "HUD reads active Q2's altered rank-two cost");
        lee.Mana = 16;
        TestAssert.False(match.TryCast(0, 0, victim));
        lee.Mana = 17;
        TestAssert.True(match.TryCast(0, 0, victim));
        TestAssert.Near(0, lee.Mana, 0, "Q2 spending agrees with recast admission and HUD");
    }

    [Check("spells-yorick-production-respawn-order")]
    public static void YorickRespawn()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ally in new[] { false, true })
        foreach (var earlyDeath in new[] { false, true })
        foreach (var level in new[] { 1, 18 })
        {
            using var match = new Match("Yorick", team, ally);
            var runtime = match.Runtime;
            var victim = match.Hero(ally ? 2 : 0); var killer = match.Hero(1);
            victim.Level = level;
            match.Cast(0, 3, victim);
            runtime.Tick(0.3);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"), "R marker applied");
            match.Clear();
            runtime.Combat.Kill(victim, killer.NetId);
            runtime.Delivery.Flush();
            TestAssert.True(victim.YorickRevival is not null, "temporary revival started");
            var originalRespawn = victim.RespawnAt;
            var deathSeconds = level == 1 ? 12.0 : 50.0;
            TestAssert.Near(runtime.Combat.CurrentTime + deathSeconds, originalRespawn,
                1e-9, "Map1 death timer starts at the first death");
            var killExperience = killer.Experience;
            runtime.Tick(1.1);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickRAZombie"), "zombie becomes playable");
            if (earlyDeath)
            {
                runtime.Combat.Kill(victim, killer.NetId);
                runtime.Delivery.Flush();
                TestAssert.False(victim.Alive, "early zombie death waits for respawn");
                TestAssert.Near(originalRespawn, victim.RespawnAt, 1e-9, "original death timer retained");
            }
            runtime.Tick(originalRespawn - runtime.Combat.CurrentTime - 0.01);
            TestAssert.False(victim.Alive, "temporary life ends before the normal respawn boundary");
            TestAssert.Near(originalRespawn, victim.RespawnAt, 1e-9, "temporary life does not restart the death timer");
            runtime.Tick(0.02);
            TestAssert.True(victim.Alive && victim.YorickRevival is null && victim.RespawnAt == 0,
                "normal respawn completes");
            TestAssert.Equal(1, killer.ChampionKills, "temporary life grants no second kill");
            TestAssert.Near(killExperience, killer.Experience, 1e-9, "temporary life grants no second XP reward");
            var lifetime = victim.MatchStatistics.Snapshot(runtime.Combat.CurrentTime);
            TestAssert.Near(deathSeconds, lifetime.TotalTimeSpentDead, .02,
                "the temporary ghost does not split or restart the ordinary death interval");
            TestAssert.Near(.3, lifetime.LongestTimeSpentLiving, .02);
            foreach (var capture in match.Captures)
            {
                var lifecycle = capture.Packets.Where(packet => packet.Plaintext.Length >= 5 &&
                    Opcode(packet) is 0x61 or 0x1E or 0x4D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..5]) == victim.NetId).ToArray();
                TestAssert.Equal(new byte[] { 0x61, 0x1E, 0x4D }, lifecycle.Select(Opcode).ToArray(),
                    $"{team}/ally={ally}/early={earlyDeath}: death, force-dead, respawn on every client");
                var death = Decode(lifecycle[0]);
                TestAssert.Near(deathSeconds, death.GetProperty("death_duration").GetDouble(), 1e-9, "death timer serialized");
                TestAssert.Equal(1, death.GetProperty("become_zombie").GetInt32(), "temporary-life death packet");
            }
        }
    }

    [Check("spells-yorick-clone-death-before-owner-no-revival")]
    public static void YorickCloneDeathBeforeOwnerDoesNotRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var killer = match.Hero(1);
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickRevenant);
            TestAssert.True(owner.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "R leaves the owner revival marker armed");

            runtime.Combat.Kill(clone, killer.NetId);
            TestAssert.False(owner.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "a clone death consumes its own owner's revival marker");
            owner.Health = 1.0;
            runtime.Combat.Kill(owner, killer.NetId);

            TestAssert.False(owner.Alive, "the unmarked owner stays dead");
            TestAssert.True(owner.YorickRevival is null,
                "a clone killed before the lethal owner hit cannot revive it");
        }
    }

    [Check("spells-leesin-kick-wall-landing")]
    public static void KickLanding()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var rank in new[] { 1, 3 })
            VerifyKick(team, rank);
    }

    internal static void VerifyKick(Team team, int rank)
    {
        foreach (var wall in new[] { false, true })
        {
            using var match = new Match("LeeSin", team, extraEnemy: !wall);
            var runtime = match.Runtime;
            var lee = match.Hero(0); var enemy = match.Hero(1);
            // The user's 113.041s capture, with the caster on the opposite
            // side of the victim from the recorded 800-unit kick direction.
            lee.SpellRanks[3] = rank;
            enemy.Position = wall ? new Position2(7395, 7071) : new Position2(6999, 6757);
            lee.Position = wall ? new Position2(7515.5, 7230) : new Position2(7032, 6560);
            if (!wall) match.Hero(2).Position = new Position2(6941, 7102);
            var crossingOrigin = new Position2(7395, 7071);
            var crossingEnd = new Position2(8147, 7345);
            TestAssert.True(Enumerable.Range(1, 79).Any(step => !runtime.Navigation.IsWalkable(
                crossingOrigin + new Position2(752 * step / 80.0, 274 * step / 80.0))), "crossing fixture has intervening terrain");
            TestAssert.Equal(crossingEnd, runtime.Movement.FurthestWalkableLanding(crossingOrigin, crossingEnd),
                "a valid far landing crosses intervening terrain");
            TestAssert.False(runtime.Navigation.IsWalkable(new Position2(6913, 6435)), "captured endpoint is a wall");
            runtime.Delivery.Flush(); match.Clear();
            var mana = lee.Mana;
            match.Cast(0, 3, enemy);
            TestAssert.Near(mana, lee.Mana, 1e-9, "R has zero energy cost");
            runtime.Tick(0.27);
            // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), inside the tick above.
            TestAssert.True(lee.SpellCooldowns[3] is var rCooldown && rCooldown > (rank == 1 ? 90 : 60) - 0.27 - 1e-9 &&
                rCooldown <= (rank == 1 ? 90 : 60) + 1e-9, $"R rank cooldown ({lee.SpellCooldowns[3]:0.###})");
            var move = enemy.ForcedMove ?? throw new InvalidOperationException("R did not start kick");
            TestAssert.True(runtime.Navigation.IsWalkable(move.Destination), "kick endpoint is walkable");
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlindMonkRKick"), "primary kick crowd control");
            // BlindMonkRKick aims at Lee Sin + (BBDistanceBetweenObjects(Lee, target) + 800) along his facing, and that native is the gap between the
            // bodies (batch 112, edge to edge for every script): the kick travels 800 minus both combat radii from where the target stood.
            if (!wall)
                TestAssert.Near(800 - lee.CombatRadius - enemy.CombatRadius, Math.Sqrt(Math.Pow(move.Destination.X - 6999, 2) + Math.Pow(move.Destination.Z - 6757, 2)), 3,
                    "clear endpoint retains full travel (800 less the two body radii)");
            foreach (var capture in match.Captures)
            {
                var damage = Decode(capture.Packets.Single(packet => Opcode(packet) == 0x68));
                TestAssert.Near((rank == 1 ? 200 : 600) * 100 / (100 + enemy.Armor),
                    damage.GetProperty("damage").GetDouble(), .001, "R rank damage in packet");
                var dash = Decode(capture.Packets.Single(packet => Opcode(packet) == 0x67 && packet.Channel == 4));
                var points = dash.GetProperty("waypoints");
                var end = points[points.GetArrayLength() - 1];
                TestAssert.Equal(move.Destination, new Position2(end[0].GetDouble(), end[1].GetDouble()),
                    "wire endpoint equals authoritative landing");
            }
            runtime.Tick(1.0);
            TestAssert.True(enemy.ForcedMove is null && runtime.Navigation.IsWalkable(enemy.Position), "kick completes outside wall");
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "BlindMonkRKick"), "landing removes kick crowd control");
            if (!wall)
            {
                var secondary = match.Hero(2);
                var secondaryDamage = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x68)
                    .Select(Decode).Single(fields => fields.GetProperty("target_netid").GetUInt32() == secondary.NetId);
                TestAssert.Near((rank == 1 ? 200 : 600) * 100 / (100 + secondary.Armor),
                    secondaryDamage.GetProperty("damage").GetDouble(), .001, "swept victim receives R damage once");
                runtime.Tick(.8);
                TestAssert.True(secondary.ForcedMove is null && runtime.Navigation.IsWalkable(secondary.Position),
                    "secondary knockback also completes on walkable ground");
            }
            var landing = enemy.Position;
            // Obtain LOS without giving the victim any move input. Real fog
            // is allowed during wall crossing; re-entry must not need an echo.
            lee.Position = landing + new Position2(0, 20);
            runtime.Delivery.Flush();
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId), "idle enemy visible with LOS");
            runtime.Tick(0.3);
            TestAssert.Equal(landing, enemy.Position, "stationary landing remains authoritative");
        }
    }

    // 19 October 2026: Lee Sin runs from his shipped scripts (BbChampionModule). What the hand-written LeeSinState held is read from what is
    // observable now: the shared projectile queue (his lines and the Cripple missiles), the buffs, the slot spell names and the forced move.
    private static SharedProjectileSnapshot[] LeeFlights(Match match, Entity lee, string? spell = null) =>
        match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
            .Where(snapshot => snapshot.CasterNetId == lee.NetId && snapshot.SpellName.StartsWith("BlindMonk", StringComparison.Ordinal) &&
                               (spell is null || snapshot.SpellName == spell)).ToArray();

    // BlindMonkWOne.TargetExecute: ShieldAbsorb is 40 per rank plus 0.8 ability power; the script keeps it in the shield's instance variables.
    private static double SafeguardShield(Entity lee) => 40.0 * lee.SpellRanks[1] + 0.8 * lee.AbilityPower;

    private static byte Opcode(PlaintextPacket packet) => packet.Plaintext.Span[0];
    private static JsonElement Decode(PlaintextPacket packet) =>
        JsonSerializer.SerializeToElement(WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields);

    private sealed class Match : IDisposable
    {
        public Match(string champion, Team team, bool ally = false, bool extraEnemy = false,
            bool autoAcquire = false, int skinId = 0, bool initializeSpells = true,
            string enemyChampion = "Annie", Amberline.Core.IRandomSource? random = null,
            string allyChampion = "LeeSin",
            string extraEnemyChampion = "Annie", GameData? data = null)
        {
            var other = team == Team.Order ? Team.Chaos : Team.Order;
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Caster", champion, team, SkinId: skinId), new(2, 1, "Enemy", enemyChampion, other),
            };
            if (ally) roster.Add(new(3, 2, "Ally", allyChampion, team));
            if (extraEnemy) roster.Add(new(3, 2, "Secondary", extraEnemyChampion, other));
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data ?? GameData.Default,
                readySettleSeconds: 0, roster: roster, random: random);
            Runtime.World.Clock.SetVirtual(0);
            Runtime.Session.CreateWorld();
            for (var i = 0; i < roster.Count; i++)
            {
                var id = i + 1;
                TestAssert.True(Runtime.Session.Authenticate(id, Runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                SpawnBursts.Add(Runtime.Session.Spawn(id));
                TestAssert.True(Runtime.Session.MarkReady(id), "ready");
                var participant = Runtime.Session.Participants[i];
                var hero = participant.Hero!;
                // Spell-only scenarios begin with stopped actors. Order
                // scenarios enable acquisition through real player commands.
                hero.HeroAutoAcquire = autoAcquire;
                hero.Position = new Position2(7000 + i * 200, 7000);
                if (initializeSpells)
                    for (var slot = 0; slot < 4; slot++) hero.SpellRanks[slot] = 1;
                hero.MaxHealth = hero.Health = 5000;
                hero.Mana = hero.MaxMana;
                var capture = new Capture(); Captures.Add(capture);
                Runtime.Delivery.AddObserver(new ReplicationObserver((ulong)id, participant.Team,
                    hero.NetId, capture, ready: true, view: participant.View));
            }
            TestAssert.True(Runtime.Session.Start(), "start");
            Runtime.Delivery.Flush(); Clear();
        }

        public MatchRuntime Runtime { get; }
        public List<Capture> Captures { get; } = [];
        public List<IReadOnlyList<PlaintextPacket>> SpawnBursts { get; } = [];
        public Entity Hero(int seat) => Runtime.Session.Participants[seat].Hero!;
        /// <summary>
        /// A live script buff instance the hero's own character script or the match's unit-character module created (batch 159
        /// part 3: a script buff's own variables are read from the instance, not from a hand-written payload field).
        /// </summary>
        public Amberline.Simulation.Spells.Bb.BbBuffInstance ScriptBuff(Entity holder, string name) =>
            Runtime.Spells.UnitScripts?.Buffs?.Find(holder, name) ??
            (Runtime.Dependencies.ChampionDispatcher.FindModule(holder.ChampionName) as
                Amberline.Simulation.Spells.Bb.BbChampionModule)?.Buffs?.Find(holder, name) ??
            throw new InvalidOperationException($"no script instance of {name} on {holder.ChampionName}");
        public void Clear() { foreach (var capture in Captures) capture.Packets.Clear(); }
        public void Move(int seat, Position2 destination)
        {
            var request = new byte[22]; request[0] = 0x75; request[5] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(seat).NetId);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), Runtime.Dependencies.Gameplay);
            Runtime.Delivery.Flush();
        }
        public void Attack(int seat, Entity target)
        {
            var request = new byte[22]; request[0] = 0x75; request[5] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(seat).NetId);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)target.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)target.Position.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(18), target.NetId);
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), Runtime.Dependencies.Gameplay);
            Runtime.Delivery.Flush();
        }
        public void Cast(int seat, int slot, Entity target, bool summoner = false)
        {
            TestAssert.True(TryCast(seat, slot, target, summoner),
                $"seat {seat} cast {slot} accepted");
        }
        public void CastGround(int seat, Position2 point, int slot = 0)
        {
            TestAssert.True(TryCastGround(seat, slot, point),
                $"seat {seat} ground cast {slot} accepted");
        }
        public void CastDirected(int seat, int slot, Position2 source, Position2 end)
        {
            var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)(slot << 1);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(seat).NetId);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)source.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)source.Z);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(18), (float)end.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(26), (float)end.Z);
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            TestAssert.True(accepted, $"seat {seat} directed cast {slot} accepted");
        }
        public bool TryCastGround(int seat, int slot, Position2 point)
        {
            var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)(slot << 1);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(seat).NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)point.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)point.Z);
            }
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            return accepted;
        }
        public bool TryCast(int seat, int slot, Entity target, bool summoner = false)
        {
            var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), Hero(seat).NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            return accepted;
        }
        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    private sealed class Capture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
