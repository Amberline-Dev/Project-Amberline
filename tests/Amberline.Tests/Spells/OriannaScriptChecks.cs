using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Orianna runs from her shipped scripts, the ball included (phase 3, 19 October 2026, W36): the ball is a placemarker unit the Q script spawns where its
/// missile ends and lets expire, announced to the clients through the ball projection. These are the engine-level and wire-level checks of that design (the
/// production composition of <see cref="ChampionLiveRegressionChecks"/>' <c>Match</c>); the per-spell numbers are the shipped scripts' and are read in the
/// <c>S-orianna</c> scenario. None of them was run when they were written (no tests in the conversion); a failing one is more likely a wrong expectation than a bug.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static Entity OriannaBall(Match match, Team team) =>
        match.Runtime.World.Entities.Values.Single(unit => unit.ChampionName == "OriannaBall" && unit.Alive && unit.Team == team);

    private static double OriannaGap(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));

    private static void OriannaReady(Entity hero)
    {
        hero.SpellCooldowns[0] = 0.0;
        hero.SpellCooldowns[1] = 0.0;
        hero.SpellCooldowns[2] = 0.0;
        hero.Mana = hero.MaxMana;
    }

    /// <summary>
    /// Batch 575 (owner question 2, batch 112 row 12): Q, W and E are instacasts (<c>SpellData*Flags</c> bit 4), and the client's
    /// cast handler hands an instacast to <c>TryCastInstaSpell</c> without stopping a current
    /// SpellCaster that is an auto attack. So a basic attack in its windup when one of them is cast keeps its windup and lands.
    /// </summary>
    [Check("scripting.bb-orianna-commands-leave-a-swing-in-its-windup")]
    public static void OriannaCommandsLeaveASwingInItsWindup()
    {
        foreach (var slot in new[] { 0, 1, 2 })
        {
            using var match = new BbM3.Match("Orianna", foeChampion: "Annie");
            match.Learn(1);
            match.Refill();
            var hero = match.Hero;
            // One swing, no attack order: a second swing cannot stand in for a cancelled first.
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), $"slot {slot}: the swing starts");
            var swing = match.Runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId);
            TestAssert.True(swing.WindupRemaining > 0.0, $"slot {slot}: the swing is in its windup");
            var cast = slot switch
            {
                0 => match.Cast(0, at: new Position2(7000, 7500)),   // Q away from the foe: the ball flies off the attack line
                1 => match.Cast(1, hero),                             // W around the ball on her hand (225: the foe 300 away is outside)
                _ => match.Cast(2, hero),                             // E on herself
            };
            TestAssert.True(cast, $"slot {slot}: the command is cast inside the windup");
            TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.MissileNetId == swing.MissileNetId && hit.WindupRemaining > 0.0),
                $"slot {slot}: the instacast leaves the swing in its windup");
            var before = BbM3.HitsOn(match, "Foe").Count(hit => hit.Kind == "Basic");
            match.Advance(1.5);
            TestAssert.Equal(before + 1, BbM3.HitsOn(match, "Foe").Count(hit => hit.Kind == "Basic"), $"slot {slot}: the swing lands");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"slot {slot}: no script failed");
        }
    }

    [Check("scripting.bb-orianna-module-binds-the-shipped-closure")]
    public static void OriannaClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Orianna");
        var needed = new[]
        {
            "CharScriptOrianna", "OrianaIzunaCommand", "OrianaIzuna", "OrianaDissonanceCommand", "OrianaDissonanceWave", "OrianaRedactCommand",
            "OrianaRedact", "OrianaRedactShield", "OrianaDetonateCommand", "OrianaStun", "MoveAwayCollision", "OrianaSpellSword", "OrianaPowerDagger",
            "OrianaGhost", "OrianaGhostSelf", "OrianaGhostMinion", "OriannaBallTracker", "ExpirationTimer", "OrianaGlobalCooldown",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Orianna closure");
        var hero = new Entity { ChampionName = "Orianna" };
        TestAssert.Equal(["OrianaIzunaCommand", "OrianaDissonanceCommand", "OrianaRedactCommand", "OrianaDetonateCommand"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Orianna.inibin");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Orianna");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => needed.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native an Orianna hook runs is bound: " + string.Join(", ", unbound));
        TestAssert.True(closure.Scripts.SelectMany(script => script.Script.Walk())
            .All(item => item.Block.Function != "BBSpawnMinion" || BbNativeHandlers.SpawnRefusal(item.Block) is null || item.Block.Text("Skin") != "OriannaBall"),
            "the spawn family no longer refuses the ball");
    }

    [Check("scripting.bb-orianna-q-spawns-the-ball-unit-and-announces-it")]
    public static void OriannaQSpawnsThePlacemarkerBall()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Orianna", team, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
            var hero = match.Hero(0);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), $"{team}: she starts with the ball (CharOnActivate)");
            TestAssert.True(hero.ScriptCharacterData is null, $"{team}: the model with the ball is her own");
            match.CastGround(0, hero.Position + new Position2(600, 0));
            match.Runtime.Tick(.6);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), $"{team}: the ball left her");
            TestAssert.Equal("OriannaNoBall", hero.ScriptCharacterData?.SkinName, $"{team}: the model without the ball is pushed");
            var ball = OriannaBall(match, team);
            TestAssert.True(ball.Placemarker && ball.Protected && !ball.Created, $"{team}: a placemarker unit no entity wire path sends");
            TestAssert.Equal(hero.NetId, ball.OwnerNetId, $"{team}: Orianna owns the ball (GoldRedirectTarget)");
            TestAssert.Equal(hero.SkinId, ball.SkinId, $"{team}: the ball wears her skin");
            TestAssert.Near(hero.Position.X + 600.0, ball.Position.X, 1.0, $"{team}: the ball lands where the line ends");
            TestAssert.True(ball.Buffs.Any(buff => buff.Name == "OrianaGhost") && ball.Buffs.Any(buff => buff.Name == "OrianaGhostMinion"),
                $"{team}: CharScriptOriannaBall.CharOnActivate gave it its two buffs");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OriannaBallTracker"), $"{team}: and the tracker went to Orianna");
            var projection = match.Runtime.Delivery.Balls.Single(item => item.NetId == ball.NetId);
            TestAssert.Equal(hero.NetId, projection.OwnerNetId, $"{team}: the projection is the ball's");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x80 && packet.EntityReferences.Any(reference => reference.NetId == ball.NetId)),
                $"{team}: Orianna's client gets the ball's 0x80 spawn");
            TestAssert.True(match.Runtime.Session.Participants[0].View.Known.Contains(ball.NetId), $"{team}: and knows the object");
        }
    }

    [Check("scripting.bb-orianna-the-ball-ends-silently-and-the-next-q-spawns-a-new-one")]
    public static void OriannaBallExpiresWithoutRewards()
    {
        using var match = new Match("Orianna", Team.Order, enemyChampion: "Annie");
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(7000, 8200);
        match.CastGround(0, hero.Position + new Position2(500, 0));
        match.Runtime.Tick(.6);
        var first = OriannaBall(match, Team.Order);
        var experience = enemy.Experience;
        var gold = enemy.Gold;
        var heroGold = hero.Gold;
        OriannaReady(hero);
        match.Clear();
        match.CastGround(0, hero.Position + new Position2(0, 500));
        match.Runtime.Tick(.05);
        TestAssert.True(first.Buffs.Any(buff => buff.Payload.NoRender), "the first ball is hidden at once (ExpirationTimer sets SetNoRender)");
        TestAssert.True(match.Runtime.Delivery.Balls.Single(item => item.NetId == first.NetId).NoRender, "the projection carries the no-render state");
        match.Runtime.Tick(.5);
        TestAssert.False(first.Alive, "the expiry's 9999 true damage ended the unit (0.249 s)");
        TestAssert.False(match.Runtime.Delivery.Balls.Any(item => item.NetId == first.NetId), "its projection is gone");
        TestAssert.True(first.Buffs.Count == 0, "the placemarker end removed every buff it held");
        TestAssert.Near(experience, enemy.Experience, 1e-9, "a placemarker's end gives the enemy champions no experience");
        TestAssert.Near(gold, enemy.Gold, 1e-9, "and no gold");
        TestAssert.Near(heroGold, hero.Gold, 1e-9, "and none to Orianna (the ball is no kill)");
        TestAssert.True(first.Placemarker && !first.Created, "the dead unit was never created for the wire");
        match.Runtime.Tick(.3); // the second throw lands 0.6 s after its cast, 500 units like the first
        var second = OriannaBall(match, Team.Order);
        TestAssert.True(second.NetId != first.NetId, "the second Q spawned a new ball unit");
        TestAssert.Near(hero.Position.Z + 500.0, second.Position.Z, 1.0, "at the second landing point");
        TestAssert.False(match.Captures[1].Packets.Any(packet => packet.EntityReferences.Any(reference => reference.NetId == first.NetId) && Opcode(packet) == 0xA6),
            "no death packet was sent for the placemarker");
    }

    [Check("scripting.bb-orianna-e-returns-the-ball-with-its-shield")]
    public static void OriannaERecallsTheBallAndShields()
    {
        using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var hero = match.Hero(0);
        var ally = match.Hero(2);
        match.CastGround(0, hero.Position + new Position2(500, 0));
        match.Runtime.Tick(.6);
        var ball = OriannaBall(match, Team.Order);
        OriannaReady(hero);
        match.Cast(0, 2, hero);
        match.Runtime.Tick(1.0);
        TestAssert.False(ball.Alive, "the ground ball expired when the command cleared its carrier buff");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "the Redact missile gave her the ball back");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OrianaRedactShield"), "with the shield on arrival");
        TestAssert.True(hero.ScriptCharacterData is null, "the model with the ball is hers again");

        OriannaReady(hero);
        match.Cast(0, 2, ally);
        match.Runtime.Tick(1.0);
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "OrianaGhost" && buff.CasterNetId == hero.NetId), "E on an ally carries the ball onto her");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "OrianaRedactShield"), "the ally has the shield");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "the ball left Orianna");
        TestAssert.Equal("OriannaNoBall", hero.ScriptCharacterData?.SkinName, "the model without the ball is pushed");
    }

    [Check("scripting.bb-orianna-pickup-and-leash-read-the-gap-between-the-bodies")]
    public static void OriannaPickupAndLeashAreEdgeToEdge()
    {
        using var match = new Match("Orianna", Team.Order, enemyChampion: "Annie");
        var hero = match.Hero(0);
        match.Hero(1).Position = new Position2(7000, 9000);
        match.CastGround(0, hero.Position + new Position2(500, 0));
        // Batch 713: the Izuna line (Instacast) flies from the step after its creation 0x41, so the ball lands one 1/30 s step later and its
        // OrianaGhost updates come one step later too; one more step keeps every window below on the same updates as before.
        match.Runtime.Tick(.6 + 1.0 / 30.0);
        var ball = OriannaBall(match, Team.Order);
        var radii = hero.CombatRadius + ball.CombatRadius;
        void Stand(double centre)
        {
            hero.Position = new Position2(ball.Position.X - centre, ball.Position.Z);
            match.Runtime.Tick(.1);
        }

        // OrianaGhost.BuffOnUpdateActions: Distance > 1125 returns the ball, Distance <= 25 picks it up; the distance is the gap between the bodies.
        Stand(radii + 1124.0);
        TestAssert.True(ball.Alive && !hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "a gap of 1124 is inside the leash");
        Stand(radii + 26.0);
        TestAssert.True(ball.Alive && !hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "a gap of 26 does not touch the ball");
        Stand(radii + 24.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "a gap of 24 picks the ball up");
        match.Runtime.Tick(.5);
        TestAssert.False(ball.Alive, "the picked-up ball unit is gone");

        OriannaReady(hero);
        match.CastGround(0, hero.Position + new Position2(500, 0));
        match.Runtime.Tick(.6 + 1.0 / 30.0);
        var far = OriannaBall(match, Team.Order);
        hero.Position = new Position2(far.Position.X - (radii + 1126.0), far.Position.Z);
        match.Runtime.Tick(.1);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"), "a gap of 1126 sends the ball back to her");
    }

    [Check("scripting.bb-orianna-ball-pointer-goes-to-her-client-only")]
    public static void OriannaPointerIsOwnerOnlyWithTheShippedBands()
    {
        using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var hero = match.Hero(0);
        match.CastGround(0, hero.Position + new Position2(600, 0));
        match.Runtime.Tick(.6);
        var ball = OriannaBall(match, Team.Order);
        IEnumerable<PlaintextPacket> Pointers(Capture capture, string name) => capture.Packets.Where(packet => Opcode(packet) == 0x8C &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name));
        var near = Pointers(match.Captures[0], "OrianaBallIndicatorNear").ToArray();
        TestAssert.True(near.Length >= 1, "the near pointer is created for Orianna's client");
        var created = near[0];
        TestAssert.Equal((ushort)0x0080, BinaryPrimitives.ReadUInt16LittleEndian(created.Plaintext.Span[10..]), "FacesTarget: the emitter faces its target every frame");
        TestAssert.Equal(ball.NetId, BinaryPrimitives.ReadUInt32LittleEndian(created.Plaintext.Span[21..]), "it points to the ball object");
        TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(created.Plaintext.Span[29..]), "bound to Orianna");
        TestAssert.True(created.Plaintext.Span[41..49].SequenceEqual(created.Plaintext.Span[49..57]), "its target anchor is the ball, not Orianna");
        TestAssert.False(match.Captures.Skip(1).Any(capture => Pointers(capture, "OrianaBallIndicatorNear").Any()), "no other client gets the pointer");

        // The bands are gaps between the bodies: 800 Medium, 1000 Far (OrianaGhost.BuffOnUpdateStats). That hook runs on
        // the 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval), so the window has to cross a sample for the swap to
        // be made: 0.3 s does that wherever the grid currently stands.
        var radii = hero.CombatRadius + ball.CombatRadius;
        match.Clear();
        hero.Position = new Position2(ball.Position.X - radii - 850.0, ball.Position.Z);
        match.Runtime.Tick(.3);
        TestAssert.True(Pointers(match.Captures[0], "OrianaBallIndicatorMedium").Any(), "a gap of 850 swaps in the medium pointer");
        match.Clear();
        hero.Position = new Position2(ball.Position.X - radii - 1050.0, ball.Position.Z);
        match.Runtime.Tick(.3);
        TestAssert.True(Pointers(match.Captures[0], "OrianaBallIndicatorFar").Any(), "a gap of 1050 swaps in the far pointer");
        TestAssert.False(match.Captures.Skip(1).Any(capture => Pointers(capture, "OrianaBallIndicatorMedium").Any() || Pointers(capture, "OrianaBallIndicatorFar").Any()),
            "the later pointers stay private as well");
    }

    // Batch 169 part 1. A child cast whose missile leaves from another point says so with the native mShouldOverrideCastPosition bit (0x08)
    // in the cast-info flag byte (the client's cast-info reader splits it, bit 3 being this one).
    // the client's unit cast routine snaps the caster to the packet's CasterPos only while the bit is clear and the point
    // is more than 400 units away (a constant of 160000, a squared distance), and the spell helper's cast routine
    // starts the missile at the caster's MissileBoneName joint instead of at CasterPos unless the bit is set.
    // Without it the owner saw exactly that pair: Orianna snapping to her ball and the ball's flight drawn from her hand. The scripts' own
    // OverrideCastPosition is what decides it, so the ball's Q and E carry the bit and her own Q (from her hand) does not.
    [Check("scripting.bb-orianna-ball-cast-flags-the-offset-launch-point")]
    public static void OriannaBallCastsFlagTheOffsetLaunchPoint()
    {
        using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var hero = match.Hero(0);
        var ally = match.Hero(2);

        // The cast-info tail after the target list: flags at +20, slot at +21, launch position at +26. Batch 582: the Instacast children (OrianaIzuna,
        // OrianaRedact) reach the client as their missile's 0x41 alone, whose cast info starts at 98 (a cast answer's at 9); the bit is the same field.
        IEnumerable<(int Header, byte Flags, byte Slot, Position2 Launch)> Casts(Capture capture) =>
            capture.Packets.Where(packet => Opcode(packet) is 0xBD or 0x41).Select(packet =>
            {
                var span = packet.Plaintext.Span;
                var info = span[0] == 0xBD ? 9 : 98;
                var count = span[info + 0x2F];
                var header = info + 0x30 + 17 * count;
                return (header, span[header + 20], span[header + 21],
                    new Position2(BinaryPrimitives.ReadSingleLittleEndian(span[(header + 26)..]),
                        BinaryPrimitives.ReadSingleLittleEndian(span[(header + 34)..])));
            });

        // A Q with the ball on her hand: OrianaIzunaCommand names OverrideCastPosition in both branches, the attached one with her own
        // position (BBGetUnitPosition Owner), so the child carries the bit and its launch point is where she stands. The bit does not mean
        // "far away", it means "CasterPos is the launch point, not the caster's missile bone".
        match.CastGround(0, hero.Position + new Position2(700, 0));
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var fromHand = Casts(match.Captures[0]).ToArray();
        TestAssert.True(fromHand.Length >= 2, "the first Q announces her slot cast and its line child");
        TestAssert.True(fromHand.Last().Flags is var handFlags && (handFlags & 0x08) != 0,
            "the child cast carries the offset-launch bit even with the ball on her");
        TestAssert.Near(hero.Position.X, fromHand.Last().Launch.X, 1.0, "and names her own position as the launch point");

        // The ball on the ground, far beyond the client's 400-unit snap range.
        OriannaReady(hero);
        for (var tick = 0; tick < 40; tick++) match.Runtime.Tick(1.0 / 30.0);
        match.Clear();
        OriannaReady(hero);
        match.CastGround(0, hero.Position + new Position2(0, 880));
        for (var tick = 0; tick < 60 && !match.Runtime.World.Entities.Values.Any(unit => unit.ChampionName == "OriannaBall" && unit.Alive); tick++)
            match.Runtime.Tick(1.0 / 30.0);
        var ball = OriannaBall(match, Team.Order);
        TestAssert.True(OriannaGap(ball.Position, hero.Position) > 400.0,
            "the ball lies beyond the 400 units the client snaps the caster over");

        // E away from the ground ball: its child leaves from the ball, so every cast packet of it carries the bit.
        OriannaReady(hero);
        match.Clear();
        match.Cast(0, 2, ally);
        for (var tick = 0; tick < 12; tick++) match.Runtime.Tick(1.0 / 30.0);
        var redact = Casts(match.Captures[0]).ToArray();
        var redactChild = redact.Where(cast => cast.Slot >= 42).ToArray();
        TestAssert.True(redactChild.Length >= 1, "the E announces its child cast on an extra slot");
        TestAssert.True(redactChild.All(cast => (cast.Flags & 0x08) != 0), "the E child carries the offset-launch bit");
        TestAssert.Near(ball.Position.X, redactChild[0].Launch.X, 1.0, "and names the ball, not Orianna");
        TestAssert.True(OriannaGap(redactChild[0].Launch, hero.Position) > 400.0,
            "which is far enough that a client without the bit would snap her to it");
        TestAssert.True(redact.Where(cast => cast.Slot < 42).All(cast => (cast.Flags & 0x08) == 0),
            "the slot cast itself still starts from her hand");

        // The E put the ball on the ally, so the next Q leaves from there: the child carries the bit and its launch point is the carrier's
        // position, which the attached branch of OrianaIzunaCommand reads from OriannaBallTracker (the ally, not Orianna).
        for (var tick = 0; tick < 90; tick++) match.Runtime.Tick(1.0 / 30.0);
        OriannaReady(hero);
        match.Clear();
        match.CastGround(0, hero.Position + new Position2(0, -700));
        // Batch 582: the child's missile packet leaves with the line's launch (the next steps), not with the press.
        for (var tick = 0; tick < 12; tick++) match.Runtime.Tick(1.0 / 30.0);
        var fromCarrier = Casts(match.Captures[0]).ToArray();
        TestAssert.True(fromCarrier.Length >= 2, "the next Q announces the slot cast and its line child");
        TestAssert.True((fromCarrier[0].Flags & 0x08) == 0, "the slot cast itself starts at her hand");
        TestAssert.True((fromCarrier[^1].Flags & 0x08) != 0, "the line child carries the offset-launch bit");
        TestAssert.Near(ally.Position.X, fromCarrier[^1].Launch.X, 1.0, "and its launch point is the ally carrying the ball");
    }

    // Batch 177 part 1, the owner's 24 October finding "the ball is invisible while travelling via Q or E". The cast answer alone cannot
    // create the ball's flight: OrianaIzuna and OrianaRedact are Instacast (SpellData*Flags bit 2), and the client's cast handler sends
    // such a cast to TryCastInstaSpell, which plays the sound, applies the costs and returns before the spell helper's cast routine -- the
    // only place obj_SpellLineMissile is built and the spell's MissileEffectName (Oriana_Ghost_mis.troy / Oriana_Ghost_mis_protect.troy) starts.
    // The missile packet (0x41, the world-event handler's case) is the one wire that reaches that function, so every instant line must send it at
    // launch: the probe (batch notes) showed none before the fix. unitPos is the caster's own position because the 0x41 branch applies it to the
    // caster's actor; naming the launch point there would step Orianna to her ball.
    [Check("scripting.bb-orianna-ball-flight-is-announced-as-a-missile")]
    public static void OriannaBallFlightIsAnnouncedAsAMissile()
    {
        using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var hero = match.Hero(0);
        var ally = match.Hero(2);

        List<JsonElement> Missiles(Capture capture) =>
            capture.Packets.Where(packet => Opcode(packet) == 0x41).Select(Decode).ToList();
        static JsonElement Info(JsonElement missile) => missile.GetProperty("cast_info");
        static bool At(JsonElement vector, Position2 point) =>
            Math.Abs(vector[0].GetDouble() - point.X) <= 1.0 && Math.Abs(vector[2].GetDouble() - point.Z) <= 1.0;
        IEnumerable<uint> Retired(Capture capture) => capture.Packets.Where(packet => Opcode(packet) == 0x5D)
            .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]));

        // Q with the ball on her hand: the line leaves her and lands the ball where she aimed.
        var hand = hero.Position;
        match.CastGround(0, hand + new Position2(700, 0));
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var ball = OriannaBall(match, Team.Order);
        var fromHand = Missiles(match.Captures[0]);
        TestAssert.Equal(1, fromHand.Count, "the Q's flight is announced once (later refreshes only reach views that missed it)");
        var q = fromHand[0];
        var qInfo = Info(q);
        var qId = qInfo.GetProperty("missile_netid").GetUInt32();
        TestAssert.Equal(SpellSystem.ProtocolHash("OrianaIzuna"), qInfo.GetProperty("spell_hash").GetUInt32(),
            "the missile packet names the child spell the client has no other way to build");
        TestAssert.True(qId != 0, "with a missile id");
        TestAssert.True(At(q.GetProperty("position"), hand) && At(q.GetProperty("start_point"), hand),
            "and starts where the cast's launch point is, on her hand");
        TestAssert.True(At(q.GetProperty("end_point"), ball.Position), "ending at the ball its aim lands");
        TestAssert.True(At(q.GetProperty("unit_position"), hand), "the packet's unit position is the caster's own, not the ball's");
        TestAssert.True(Retired(match.Captures[0]).Contains(qId), "and the flight is retired with the ball's landing");

        // E from the ground ball: the flight starts at the ball and the packet still names Orianna as the unit.
        OriannaReady(hero);
        for (var tick = 0; tick < 40; tick++) match.Runtime.Tick(1.0 / 30.0);
        match.Clear();
        OriannaReady(hero);
        // Inside the Q's 800 range, so she casts where she stands instead of walking into range; 700 is still past the client's 400-unit snap.
        match.CastGround(0, hero.Position + new Position2(0, 700));
        // Two seconds: the flight lands and the Q's own slot work is over before the E is asked for.
        for (var tick = 0; tick < 60; tick++) match.Runtime.Tick(1.0 / 30.0);
        var ground = OriannaBall(match, Team.Order);
        TestAssert.True(OriannaGap(ground.Position, hero.Position) > 400.0, "the ball lies far from her, as the owner's E does");
        OriannaReady(hero);
        match.Clear();
        var castAt = hero.Position;
        match.Cast(0, 2, ally);
        for (var tick = 0; tick < 12; tick++) match.Runtime.Tick(1.0 / 30.0);
        var redact = Missiles(match.Captures[0]);
        TestAssert.Equal(1, redact.Count, "the E's return flight is announced once");
        var e = redact[0];
        var eInfo = Info(e);
        var eId = eInfo.GetProperty("missile_netid").GetUInt32();
        TestAssert.Equal(SpellSystem.ProtocolHash("OrianaRedact"), eInfo.GetProperty("spell_hash").GetUInt32(), "with the E child's spell");
        TestAssert.True(eId != 0, "and a missile id");
        TestAssert.True(At(e.GetProperty("position"), ground.Position) && At(e.GetProperty("start_point"), ground.Position),
            "the flight leaves the ball");
        TestAssert.True(At(e.GetProperty("end_point"), ally.Position), "and heads for the unit she shielded");
        TestAssert.True(At(e.GetProperty("unit_position"), castAt), "while the packet's unit position keeps her where she stands");
        // Batch 707: the tracking line meets the ally it tracks on arrival (BbLineMissile.Track), not 80 + his radius before him, so the ball's
        // 806 units at 1850 take 0.44 s, past the 0.4 s above: a few more ticks let it arrive.
        for (var tick = 0; tick < 6; tick++) match.Runtime.Tick(1.0 / 30.0);
        TestAssert.True(Retired(match.Captures[0]).Contains(eId), "the return flight is retired when the ball arrives");
    }

    [Check("scripting.bb-orianna-passive-tooltip-and-daggers")]
    public static void OriannaPassiveIsOrianaSpellSword()
    {
        using var match = new Match("Orianna", Team.Order, enemyChampion: "Annie");
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.Level = 7;
        hero.AbilityPower = 100;
        var buff = hero.Buffs.Single(item => item.Name == "OrianaSpellSword");
        match.Runtime.Tick(0.1);
        // BuffOnUpdateActions executes at once and every ten seconds: the table is indexed by the hero's level, the damage adds 0.2 AP.
        TestAssert.Near(15.0, buff.Payload.TooltipValue2, 1e-9, "level seven: 15 base damage");
        TestAssert.Near(35.0, buff.Payload.TooltipValue3, 1e-9, "plus 0.2 of 100 AP");
        enemy.Position = hero.Position + new Position2(300, 0);
        enemy.Health = enemy.MaxHealth = 100000.0;
        var health = enemy.Health;
        match.Attack(0, enemy);
        match.Runtime.Tick(5.0);
        TestAssert.True(enemy.Health < health, "her attacks land");
        TestAssert.True(enemy.Buffs.Any(item => item.Name == "OrianaPowerDagger" && item.CasterNetId == hero.NetId), "each hit stacks a dagger on the target");
        TestAssert.True(hero.Buffs.Any(item => item.Name == "OrianaPowerDaggerDisplay"), "and the display on Orianna");
    }

    [Check("scripting.bb-orianna-w-slows-and-hastes-around-the-ball-and-the-effects-end-with-the-wave")]
    public static void OriannaWaveIsAScriptBuff()
    {
        using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var ally = match.Hero(2);
        hero.SpellRanks[1] = 5;
        match.CastGround(0, hero.Position + new Position2(500, 0));
        match.Runtime.Tick(.6);
        var ball = OriannaBall(match, Team.Order);
        enemy.Position = ball.Position + new Position2(60, 0);
        ally.Position = ball.Position + new Position2(-60, 0);
        enemy.Armor = enemy.SpellBlock = 0;
        OriannaReady(hero);
        var health = enemy.Health;
        var baseSpeed = enemy.Speed;
        match.CastGround(0, ball.Position, slot: 1);
        match.Runtime.Tick(.5);
        TestAssert.True(enemy.Health < health, "the wave's own damage lands in 225 around the ball");
        TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "OrianaSlow") && ally.Buffs.Any(buff => buff.Name == "OrianaHaste"),
            "OrianaDissonanceWave slows the enemies and hastes the allies standing in it");
        var slowed = enemy.Speed;
        TestAssert.True(slowed < baseSpeed, "the slow takes speed");
        match.Runtime.Tick(1.0);
        // While the foe stands in the field the wave replaces OrianaSlow every 0.25 s (BUFF_REPLACE_EXISTING restarts TicksLeft at 8), so the
        // slow holds its full strength; once the foe is out, the buff's own BuffOnUpdateStats counts TicksLeft down and the speed returns.
        TestAssert.Near(slowed, enemy.Speed, 1e-6, "inside the field the wave's refresh keeps the slow at full strength");
        enemy.Position = ball.Position + new Position2(700, 0);
        match.Runtime.Tick(1.0);
        TestAssert.True(enemy.Speed > slowed, "and outside it the slow decays on its own clock (OrianaSlow's BuffOnUpdateStats)");
        match.Runtime.Tick(5.0);
        TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "OrianaDissonanceWave"), "the wave buff ends");
        TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "OrianaSlow"), "and the slow with it");
    }

    // Batch 169 part 2, finding "her spells show 'Snared' text over her". The four command scripts each set
    // PopupMessage1 = game_floatingtext_Snared on themselves (OrianaIzunaCommand, OrianaDissonanceCommand, OrianaRedactCommand,
    // OrianaDetonateCommand), but a *spell* script's template is never sent: the only client code that paints a popup is
    // the buff manager's handler for NpcBuffAdd2, which walks the template's three
    // popupMessage slots and shows each through the player's 0x10 text slot — and only
    // for an add whose holder is the local player and whose slot it holds nothing in yet. So the globals of a *spell* script
    // reach that path only when a 0xBF's name hash resolves to a buff script that carries them. The one buff Orianna's casts
    // add whose own template carries the same popup is OrianaGlobalCooldown (the 0.25 s lock every command adds to her: its
    // template has PopupMessage1 = game_floatingtext_Snared). Its OnBuffActivate lowers the other slots' cooldowns and then
    // clears the buff itself (BBSpellBuffClear BuffName=OrianaGlobalCooldown TargetVar=Owner, unconditional), so the instance
    // exists only for the length of its own activation and no replication pass can see it — which is why no text appears.
    // This server queued the 0xBF (hash 193710, duration 0.25) before the hook removed it, and the client showed "Snared".
    // The fix is the shared rule in BbBuffRuntime.Add (ClearsItselfOnActivate -> BuffPayload.ServerOnlyMarker): a script buff
    // whose own activation clears it unconditionally is server-only.
    [Check("scripting.bb-orianna-commands-show-no-snared-text-the-self-clearing-lock-is-server-only")]
    public static void OriannaCommandsSendNoSnaredTemplate()
    {
        var cooldown = BbScriptLoader.Load(GameDataPaths.Default, "Spells/OrianaGlobalCooldown.luaobj").Script;
        TestAssert.True(cooldown is not null, "OrianaGlobalCooldown ships as a spell script");
        TestAssert.True(cooldown!.Global("PopupMessage1") is BbValue.Text { Value: "game_floatingtext_Snared" },
            "its template is the one that draws 'Snared' (the client paints a held buff's PopupMessage1)");
        TestAssert.True(cooldown.Event("OnBuffActivate") is { Blocks.Count: > 0 } &&
                        cooldown.Walk().Any(item => item.Hook == "OnBuffActivate" && item.Block.Function == "BBSpellBuffClear" &&
                                                    item.Block.Text("BuffName") == "OrianaGlobalCooldown"),
            "and its own activation clears it again, so it is never a buff a client can hold");
        var cooldownHash = SpellPackets.BuffNameHash("OrianaGlobalCooldown");
        IEnumerable<BuffAdd> Adds(Capture capture) => capture.Packets
            .Where(packet => Opcode(packet) == SpellPackets.BuffAddOpcode && packet.Plaintext.Length >= 25)
            .Select(packet => SpellPackets.TryReadBuffAdd(packet.Plaintext.Span, out var add) ? add : default)
            .Where(add => add.TargetNetId != 0);

        foreach (var slot in new[] { 0, 1, 2, 3 })
        {
            using var match = new Match("Orianna", Team.Order, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
            var hero = match.Hero(0);
            var ally = match.Hero(2);
            ally.Position = hero.Position + new Position2(-120, 0);
            for (var rank = 0; rank < 3; rank++) hero.SpellRanks[rank] = 5;
            hero.SpellRanks[3] = 3;
            match.Runtime.Tick(.3);
            match.Clear();
            if (slot == 0) match.CastGround(0, hero.Position + new Position2(300, 0));
            else if (slot == 1) match.CastGround(0, hero.Position, slot: 1);
            else if (slot == 2) match.Cast(0, 2, ally);
            else match.CastGround(0, hero.Position, slot: 3);
            // The lock is 0.25 s: watch the whole window, so a 0x7F-paired transient add would still be counted.
            var sendsLock = false;
            for (var tick = 0; tick < 30; tick++)
            {
                sendsLock |= Adds(match.Captures[0]).Any(add => add.TargetNetId == hero.NetId && add.BuffNameHash == cooldownHash);
                match.Captures[0].Packets.Clear();
                match.Runtime.Tick(1.0 / 30.0);
            }
            TestAssert.False(sendsLock, $"slot {slot}: the self-clearing global cooldown reaches no client (its template is 'Snared')");
            TestAssert.False(hero.Buffs.Any(buff => buff.Type is BuffType.Snare or BuffType.Net || buff.Payload.MovementDisabled),
                $"slot {slot}: and nothing roots her (no Snared status either)");
        }

        // The lock still does its job: the other learned slots carry 0.24 s after a cast, and the cast slot its own cooldown.
        using var after = new Match("Orianna", Team.Order, enemyChampion: "Annie");
        var afterHero = after.Hero(0);
        for (var rank = 0; rank < 3; rank++) afterHero.SpellRanks[rank] = 5;
        after.Runtime.Tick(.2);
        after.CastGround(0, afterHero.Position + new Position2(300, 0));
        after.Runtime.Tick(0.05);
        TestAssert.True(afterHero.SpellCooldowns[0] > 3.0, "the cast slot runs its own cooldown");
        TestAssert.True(afterHero.SpellCooldowns.Skip(1).All(cooldown => cooldown is > 0.15 and <= 0.25),
            "and OrianaGlobalCooldown's activation still raises every other learned slot to 0.24 s");
    }

    // Batch 169 part 2, finding "W gives allies the slow and the speed boost at once". Re-measured on this branch: the split is
    // already the scripts' own and this is a **gone** finding, not a fix. OrianaDissonanceCommand's TargetExecute scans enemies
    // (AffectEnemies AffectNeutral AffectMinions AffectHeroes) and adds OrianaSlow, then scans friends (AffectFriends
    // AffectMinions AffectHeroes) and adds OrianaHaste; the field's OrianaDissonanceWave repeats the same two scans every
    // 0.25 s. So no unit is ever offered both, and this check drives the whole life of the wave and its fade to pin it.
    // The owner saw the opposite at b137 because BuffOnUpdateStats was polled every simulation step there: OrianaSlow /
    // OrianaHaste take one off TicksLeft = 8 per stats call over Duration 2.0, and polled faster than the scripts' 0.25 s
    // grid TicksLeft runs negative and the sign of the speed mod flips. Reproduced in this session by setting
    // BbBuffRuntime.StatsUpdateInterval to 0.01 (b137's per-step cadence) on this same source: the ally fell to 110 of 310
    // and the foe rose to 772.5 of 310. Batch 150 part 2a (63cebb67) put the hook on the 0.25 s grid, which removed it.
    [Check("scripting.bb-orianna-w-keeps-the-slow-and-the-haste-apart-for-the-whole-wave")]
    public static void OriannaWaveKeepsSlowAndHasteApart()
    {
        // Batch 357: the add names the client script (GameData.ClientBuffName: the BuffName when a script of that name exists, else the binding).
        // The client holds no script named by their BuffNames (OrianaDissonanceAlly, OrianaDissonanceEnemy), so the adds name the bindings: OrianaHaste and OrianaSlow.
        var hasteHash = SpellPackets.BuffNameHash("OrianaHaste");
        var slowHash = SpellPackets.BuffNameHash("OrianaSlow");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Orianna", team, ally: true, enemyChampion: "Annie", allyChampion: "Annie");
            var hero = match.Hero(0);
            var foe = match.Hero(1);
            var friend = match.Hero(2);
            hero.SpellRanks[1] = 5;
            match.CastGround(0, hero.Position + new Position2(500, 0));
            match.Runtime.Tick(.6);
            var ball = OriannaBall(match, team);
            // Orianna herself, the foe and the ally all stand in the 225 field.
            foe.Position = ball.Position + new Position2(60, 0);
            friend.Position = ball.Position + new Position2(-60, 0);
            hero.Position = ball.Position + new Position2(0, 60);
            match.Runtime.Tick(.3);
            var foeBase = foe.Speed;
            var friendBase = friend.Speed;
            OriannaReady(hero);
            match.Clear();
            match.CastGround(0, ball.Position, slot: 1);
            var both = new List<string>();
            var adds = new List<(uint Target, uint Hash)>();
            // The whole window, not just its end: a buff whose own sign flips (the b137 shape below) restores the unit to
            // its base speed by the time the buff is gone, so only a reading taken while it lasts catches it.
            double friendMin = friendBase, foeMax = foeBase;
            // 8 s: the 3 s wave, then the 2 s fade after it ends.
            for (var tick = 0; tick <= 240; tick++)
            {
                foreach (var unit in new[] { hero, foe, friend })
                {
                    var slow = unit.Buffs.Any(buff => buff.Name == "OrianaSlow");
                    var haste = unit.Buffs.Any(buff => buff.Name == "OrianaHaste");
                    if (slow && haste) both.Add($"{team} {unit.ChampionName} t={tick / 30.0:F2}");
                }
                friendMin = Math.Min(friendMin, friend.Speed);
                foeMax = Math.Max(foeMax, foe.Speed);
                foreach (var packet in match.Captures[0].Packets.Where(packet =>
                             Opcode(packet) == SpellPackets.BuffAddOpcode && packet.Plaintext.Length >= 25))
                    if (SpellPackets.TryReadBuffAdd(packet.Plaintext.Span, out var add))
                        adds.Add((add.TargetNetId, add.BuffNameHash));
                match.Captures[0].Packets.Clear();
                match.Runtime.Tick(1.0 / 30.0);
            }
            TestAssert.Equal(0, both.Count, $"{team}: no unit ever holds the slow and the haste together: {string.Join(", ", both.Take(5))}");
            TestAssert.True(adds.Any(add => add.Target == foe.NetId && add.Hash == slowHash), $"{team}: the foe takes the slow");
            TestAssert.False(adds.Any(add => add.Target == foe.NetId && add.Hash == hasteHash), $"{team}: and never the haste");
            TestAssert.True(adds.Any(add => add.Target == friend.NetId && add.Hash == hasteHash), $"{team}: the ally takes the haste");
            TestAssert.False(adds.Any(add => add.Target == friend.NetId && add.Hash == slowHash), $"{team}: and never the slow");
            TestAssert.True(adds.Any(add => add.Target == hero.NetId && add.Hash == hasteHash), $"{team}: she hastes herself (a friend in her own field)");
            TestAssert.False(adds.Any(add => add.Target == hero.NetId && add.Hash == slowHash), $"{team}: and is never slowed by her own wave");
            TestAssert.True(friendMin >= friendBase - 1e-6,
                $"{team}: the ally's speed never falls below its base ({friendBase:F2}; lowest seen {friendMin:F2})");
            TestAssert.True(foeMax <= foeBase + 1e-6,
                $"{team}: and the foe's never rises above its own ({foeBase:F2}; highest seen {foeMax:F2})");
        }
    }

    [Check("scripting.bb-orianna-r-damages-stuns-and-pulls-around-the-ball")]
    public static void OriannaRIsTheShockwaveOfTheScript()
    {
        using var match = new Match("Orianna", Team.Order, enemyChampion: "Annie");
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.SpellRanks[3] = 3;
        enemy.Armor = enemy.SpellBlock = 0;
        enemy.MaxHealth = enemy.Health = 100000.0;
        enemy.Position = hero.Position + new Position2(200, 0);
        var health = enemy.Health;
        match.CastGround(0, hero.Position, slot: 3);
        match.Runtime.Tick(.3);
        TestAssert.Near(health, enemy.Health, 1e-9, "nothing lands before the 0.4 s windup ends");
        match.Runtime.Tick(.3);
        TestAssert.True(health - enemy.Health > 100.0, "the shockwave damages (150/225/300 plus 0.7 AP), centred on the ball, here on herself");
        TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "OrianaStun"), "the stun");
        TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "MoveAwayCollision"), "and the pull");
        TestAssert.True(enemy.Buffs.Any(buff => buff.Payload.ActionDisabled || buff.Type == BuffType.Stun), "the target cannot act");
    }
}
