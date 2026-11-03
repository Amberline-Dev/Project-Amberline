using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 6 (batch 312): Caitlyn plays from her shipped scripts. Expected numbers are the literals of the shipped scripts
/// (CharScriptCaitlyn, CaitlynPiltoverPeacemaker, CaitlynYordleTrap with its sight and debuff, CaitlynEntrapmentMissile,
/// CaitlynAceintheHoleMissile, CaitlynHeadshotCount, CaitlynHeadshot, CaitlynHeadshotMissile) and the spell inibins. The scenario
/// <c>S-caitlyn</c> shows the same facts on a headless run. No engine rule was added for her: the wave 6 items she uses (E02, E04, E08,
/// E11, E29) were on master, so these checks pin what the shipped scripts do through the engine.
/// </summary>
public static class BbCaitlynChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static BbScript CharScript() =>
        BbScriptLoader.Load(GameData.Default.Paths, "Characters/Caitlyn/Scripts/CharScriptCaitlyn.luaobj").Script!;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static Entity? Trap(Match match) =>
        match.Runtime.World.Entities.Values.FirstOrDefault(entity => entity.Alive && entity.Name == "CaitlynTrap" && entity.Team == match.Hero.Team);

    private static int TrapCount(Match match) =>
        match.Runtime.World.Entities.Values.Count(entity => entity.Alive && entity.Name == "CaitlynTrap" && entity.Team == match.Hero.Team);

    // The stacks that ready the headshot. CharScriptCaitlyn sets CharVars.tooltipAmount to 8, 7, 6 by rank, but the passive
    // (CaitlynHeadshotpassive, BuffOnUpdateActions, run at once and every ten seconds) writes the same variable with 7, 6, 5 by rank,
    // so the count that readies the headshot is the passive's table at the hero's level: 7 at level one.
    private static int HeadshotThreshold(Match match)
    {
        var table = Numbers(Blocks(Script("CaitlynHeadshotpassive"), "BuffOnUpdateActions").First(item => item.Function == "BBSetVarInTable" &&
            item.Text("DestVar") == "tooltipAmount").Param("SrcValueByLevel")!);
        return (int)table[Math.Clamp(match.Hero.Level, 1, table.Length) - 1];
    }

    private static void Swing(Match match)
    {
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a basic attack on the Foe is queued");
        match.Advance(1.6);
    }

    [Check("scripting.bb-caitlyn-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Caitlyn");
        foreach (var name in new[] { "CharScriptCaitlyn", "CaitlynPiltoverPeacemaker", "CaitlynYordleTrap", "CaitlynYordleTrapDebuff", "CaitlynYordleTrapSight",
                     "CaitlynEntrapment", "CaitlynEntrapmentMissile", "CaitlynAceintheHole", "CaitlynAceintheHoleMissile", "CaitlynAceintheHoleVisibility",
                     "CaitlynHeadshotCount", "CaitlynHeadshot", "CaitlynHeadshotpassive", "CaitlynHeadshotMissile", "Slow" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Caitlyn");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(30, closure.ScriptNames.Count(), "the closure is 30 scripts");
        // The passive's Lua name (the inibin's Data*Passive1LuaName, "Headshot Marker (Passive)") has no script: CharOnActivate adds CaitlynHeadshotPassive.
        TestAssert.Equal("Headshot Marker", string.Join(",", closure.Dangling.Select(reference => reference.Name)),
            "the only dangling name is the passive's Lua name");

        var hero = new Entity { ChampionName = "Caitlyn" };
        TestAssert.Equal(new[] { "CaitlynPiltoverPeacemaker", "CaitlynYordleTrap", "CaitlynEntrapment", "CaitlynAceintheHole" },
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Caitlyn.inibin");
        TestAssert.Equal(new[] { 5, 5, 5, 3 }, Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");
        TestAssert.Equal(new[] { 6, 11, 16 }, Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(),
            "Ace in the Hole is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Caitlyn")!;
        TestAssert.True(info is { Playable: true, Role: "adc", DefaultIp: 6300, Id: 51, RetailBot: false },
            "playable as an ADC for 6300 IP (store-prices-2011.json id 51), not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Caitlyn"), "listed once");
    }

    // The script literals the kit rests on, read from the shipped tables a second time.
    [Check("scripting.bb-caitlyn-script-literals-pin-the-kit")]
    public static void ShippedLiteralsPinTheKit()
    {
        var piltover = Script("CaitlynPiltoverPeacemaker");
        TestAssert.Equal(new[] { 20.0, 65.0, 110.0, 155.0, 200.0 },
            Numbers(Blocks(piltover, "TargetExecute").First(item => item.Function == "BBApplyDamage").Param("DamageByLevel")!),
            "Peacemaker's damage by rank");
        TestAssert.Equal(new[] { 1.3 }, Blocks(piltover, "TargetExecute").Where(item => item.Function == "BBMath" && item.Text("DestVar") == "BaseDamage")
            .Select(item => item.Number("Src1Value") ?? 0.0).Take(1).ToArray(), "the base damage is 1.3 times the total attack damage");
        TestAssert.Equal(new[] { 0.85, 0.4 }, Blocks(piltover, "TargetExecute").Where(item => item.Function == "BBMath" && item.Text("DestVar") == "percentOfAttack")
            .Select(item => item.Number("Src2Value") ?? 0.0).Distinct().ToArray(), "each unit keeps 85 percent of the last, at least 40 percent");

        var trap = Script("CaitlynYordleTrap");
        TestAssert.Equal(new[] { 3.0, 3.0, 3.0, 3.0, 3.0 }, ByLevel("CaitlynYordleTrap", "SelfExecute", "MaxStacks"), "three traps at once at every rank");
        TestAssert.True(Blocks(trap, "SelfExecute").Any(item => item.Function == "BBSpellBuffAdd" && item.Number("Duration") == 240.0),
            "a trap lasts 240 seconds");
        TestAssert.Equal(new[] { 80.0, 130.0, 180.0, 230.0, 280.0 }, ByLevel("CaitlynYordleTrapSight", "OnBuffActivate", "BaseDamage"),
            "the trap's damage by rank, spread over three ticks");

        var net = Script("CaitlynEntrapmentMissile");
        TestAssert.Equal(new[] { 1.0, 1.25, 1.5, 1.75, 2.0 }, Numbers(Blocks(net, "TargetExecute").First(item => item.Function == "BBSpellBuffAdd" &&
            item.Text("BuffName") == "Slow").Param("DurationByLevel")!), "the net's slow lasts 1 to 2 seconds by rank");
        TestAssert.Equal(new[] { 80.0, 130.0, 180.0, 230.0, 280.0 },
            Numbers(Blocks(net, "TargetExecute").First(item => item.Function == "BBApplyDamage").Param("DamageByLevel")!), "the net's damage by rank");
        // Batch 349: the hit's ratio is the block's SpellDamageRatio 0.8 (the inibin and the wiki's "+ 80% AP", which no patch entry changed);
        // the script's plain global SpellDamageRatio = 1 is not what BBApplyDamage reads.
        TestAssert.True(Blocks(net, "TargetExecute").Where(item => item.Function == "BBApplyDamage").All(item => item.Number("SpellDamageRatio") == 0.8),
            "every branch of the net's hit takes 0.8 of her ability power");

        TestAssert.Equal(new[] { 250.0, 475.0, 700.0 }, ByLevel("CaitlynAceintheHoleMissile", "TargetExecute", "BaseDamage"),
            "Ace in the Hole's base damage by rank");

        TestAssert.Equal(new[] { 1.75, 2.5, 1.25, 1.5 }, Blocks(Script("CaitlynHeadshotMissile"), "TargetExecute")
            .Where(item => item.Function == "BBMath" && item.Text("DestVar") == "DamageAmount").Select(item => item.Number("Src2Value") ?? 0.0).ToArray(),
            "the headshot multiplies by 1.75 (critical), 2.5 (other), 1.25 (critical hero), 1.5 (hero)");

        var character = CharScript();
        TestAssert.Equal(new[] { 2.0, 1.0 }, Blocks(character, "CharOnHitUnit").Where(item => item.Function == "BBSpellBuffAdd" &&
            item.Text("BuffName") == "CaitlynHeadshotCount").Select(item => item.Number("NumberOfStacks") ?? 0.0).ToArray(),
            "a hit adds two stacks from the brush and one elsewhere");
        TestAssert.Equal(new[] { 8.0, 7.0, 6.0 }, Numbers(Blocks(character, "SetVarsByLevel").First(item => item.Function == "BBSetVarInTable" &&
            item.Text("DestVar") == "tooltipAmount").Param("SrcValueByLevel")!).Distinct().ToArray(), "the character sets tooltipAmount to 8, 7, 6 by rank");
        TestAssert.Equal(new[] { 7.0, 6.0, 5.0 }, Numbers(Blocks(Script("CaitlynHeadshotpassive"), "BuffOnUpdateActions").First(item =>
            item.Function == "BBSetVarInTable" && item.Text("DestVar") == "tooltipAmount").Param("SrcValueByLevel")!).Distinct().ToArray(),
            "the passive writes the same variable 7, 6, 5 by rank: that is the count that readies the headshot");
        TestAssert.Equal(new[] { 6.0, 5.0, 4.0 }, Numbers(Blocks(character, "CharOnPreAttack").First(item => item.Function == "BBSetVarInTable" &&
            item.Text("DestVar") == "BrushCount").Param("SrcValueByLevel")!).Distinct().ToArray(), "in the brush the headshot needs 6, 5, 4 stacks");
    }

    [Check("scripting.bb-caitlyn-headshot-readies-at-the-passive-threshold")]
    public static void HeadshotReadiesAtThePassiveThreshold()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        var threshold = HeadshotThreshold(match);
        for (var hit = 1; hit < threshold; hit++)
        {
            Swing(match);
            TestAssert.Equal(hit, Buff(match.Hero, "CaitlynHeadshotCount")?.Stacks ?? 0, $"{hit} landed attacks leave {hit} stacks");
        }
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshot") is null, $"the headshot is not ready at {threshold - 1} stacks");
        Swing(match);
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshot") is not null, $"the attack that makes {threshold} stacks readies the headshot");
        TestAssert.Equal(0, Buff(match.Hero, "CaitlynHeadshotCount")?.Stacks ?? 0, "and the stacks are spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Batch 403 (finding 6, the passive indicator on the next shot): the sweep's passive probe is three attacks and never readies the
    // headshot, so this drives the threshold and reads the wire. The ready buff's add must reach an observing client with the server's
    // type and hidden byte, and must not be a server-only marker (the indicator is its AutoBuffActivateEffect on the client).
    [Check("scripting.bb-caitlyn-headshot-ready-add-reaches-the-client")]
    public static void HeadshotReadyAddReachesTheClient()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        var wire = Observe(match, Team.Order, match.Hero);
        var threshold = HeadshotThreshold(match);
        for (var hit = 0; hit < threshold; hit++) Swing(match);
        var ready = Buff(match.Hero, "CaitlynHeadshot");
        TestAssert.True(ready is not null, $"the headshot is ready after {threshold} landed attacks");
        TestAssert.False(ready!.Payload.ServerOnlyMarker, "the ready buff is not a server-only marker");
        var hash = Amberline.Network.Protocol.SpellPackets.BuffNameHash("CaitlynHeadshot");
        var adds = wire.Packets.Where(entry => entry.Packet.Plaintext.Length >= 25 &&
            entry.Packet.Plaintext.Span[0] == Amberline.Network.Protocol.SpellPackets.BuffAddOpcode &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == match.Hero.NetId &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[9..]) == hash).ToList();
        TestAssert.True(adds.Count > 0, "the 0xBF add of the ready buff reaches the observing client");
        var add = adds[^1].Packet.Plaintext.Span;
        TestAssert.Equal((int)ready.Type, (int)add[6], "the wire type byte is the ready buff's type");
        TestAssert.Equal((int)ready.HiddenOnClientByte, (int)add[8], "the wire hidden byte is the ready buff's");
    }

    [Check("scripting.bb-caitlyn-headshot-hits-a-champion-for-one-and-a-half-times-the-attack")]
    public static void HeadshotHitsAChampion()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        var threshold = HeadshotThreshold(match);
        for (var hit = 0; hit < threshold; hit++)
            Swing(match);
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshot") is not null, "the headshot is ready");
        var attackDamage = match.Hero.AttackDamage;
        var before = HitsOn(match, "Foe").Count;
        Swing(match);
        var shot = HitsOn(match, "Foe").Skip(before).ToList();
        TestAssert.Equal(1, shot.Count, "one hit from the headshot");
        TestAssert.True(Math.Abs(shot[0].Raw - 1.5 * attackDamage) < 1e-6 || Math.Abs(shot[0].Raw - 1.25 * attackDamage) < 1e-6,
            $"a hero takes 1.5 times the attack damage (1.25 on a critical hit): raw {shot[0].Raw}, attack damage {attackDamage}");
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshot") is null, "the headshot spends the ready buff");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-piltover-peacemaker-keeps-eighty-five-percent-after-each-unit")]
    public static void PiltoverFallsOffAfterEachUnit()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        Enemy(match, "Second", new Position2(7400, 7000));
        Enemy(match, "Third", new Position2(7500, 7000));
        match.Advance(0.3);
        var attackDamage = match.Hero.AttackDamage;
        TestAssert.True(match.Cast(0, match.Foe), "Peacemaker along the line through the Foe and the two minions");
        match.Advance(1.5);
        var first = HitsOn(match, "Foe");
        var second = HitsOn(match, "Second");
        var third = HitsOn(match, "Third");
        TestAssert.Equal(1, first.Count, "the Foe is hit once");
        TestAssert.Equal(1, second.Count, "the second unit is hit once");
        TestAssert.Equal(1, third.Count, "the third unit is hit once");
        // percentOfAttack multiplies the whole hit (the rank's 20 and the base part), so each unit takes 85 percent of the last.
        var baseHit = 20.0 + 1.3 * attackDamage;
        TestAssert.Near(baseHit, first[0].Raw, 1e-4, "the first unit takes 20 plus 1.3 times the attack damage");
        TestAssert.Near(baseHit * 0.85, second[0].Raw, 1e-4, "the second takes 85 percent of that");
        TestAssert.Near(baseHit * 0.85 * 0.85, third[0].Raw, 1e-4, "the third takes 72.25 percent of that");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-yordle-trap-arms-after-a-second-and-springs-on-a-champion")]
    public static void YordleTrapArmsThenSprings()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7800, 7600);
        match.Advance(0.3);
        var placed = new Position2(7300, 7000);
        TestAssert.True(match.Cast(1, at: placed), "Yordle Snap Trap 300 units ahead");
        match.Advance(0.5);
        TestAssert.True(Trap(match) is not null && Buff(Trap(match)!, "CaitlynYordleTrap") is not null, "the trap is on the ground with its buff");
        // A champion that steps in before the arming (one second after the placement) does not spring the trap.
        match.Foe.Position = new Position2(placed.X + 120.0, placed.Z);
        match.Advance(0.2);
        TestAssert.True(Buff(match.Foe, "CaitlynYordleTrapDebuff") is null, "nothing happens before the trap is armed");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "CaitlynYordleTrapDebuff") is not null, "the armed trap springs on a champion within 150: she is rooted");
        TestAssert.True(Buff(match.Foe, "CaitlynYordleTrapSight") is not null, "and revealed");
        TestAssert.True(Trap(match) is null, "the trap is spent");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "CaitlynYordleTrapDebuff") is null, "the root ends after 1.5 seconds");
        TestAssert.True(Buff(match.Foe, "CaitlynYordleTrapSight") is not null, "the reveal lasts eight seconds");
        TestAssert.Near(80.0, HitsOn(match, "Foe").Sum(hit => hit.Raw), 0.01, "the trap's damage is 80 at rank one, in three ticks");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // Owner decision, 28 October 2026: Caitlyn's trap follows her shipped script, which adds no stealth and gives the enemy team the red idle
    // effect (caitlyn_yordleTrap_idle_red). The enemy sees it in ordinary sight and its spawn (0x80, the unit id at bytes 5..8) reaches the
    // enemy's client. Nidalee's trap, in the same setup, is the one hidden until true sight (batch 113, scenario S-nidalee).
    [Check("scripting.bb-caitlyn-yordle-trap-is-visible-to-the-enemy-while-nidalee-trap-stays-hidden")]
    public static void EnemySeesCaitlynsTrapButNotNidaleesTrap()
    {
        using var caitlyn = new Match("Caitlyn");
        caitlyn.Learn(1);
        caitlyn.Refill();
        var placed = caitlyn.Hero.Position + new Position2(300, 0);
        caitlyn.Foe.Position = placed + new Position2(300, 0);
        var wire = Observe(caitlyn, caitlyn.Foe.Team, caitlyn.Foe);
        caitlyn.Advance(0.3);
        TestAssert.True(caitlyn.Cast(1, at: placed), "Yordle Snap Trap 300 units ahead");
        caitlyn.Advance(1.5);
        var trap = Trap(caitlyn);
        TestAssert.True(trap is not null, "the trap is on the ground");
        TestAssert.False(VisibilityRules.IsStealthed(trap!), "not stealthed: her shipped call and script add none");
        TestAssert.True(caitlyn.Runtime.Spells.CanSeeTarget(caitlyn.Foe, trap!), "the enemy 300 units away sees it in ordinary sight");
        TestAssert.True(wire.Packets.Any(entry => entry.Packet.Plaintext.Span is { Length: >= 9 } span && span[0] == 0x80 &&
            BinaryPrimitives.ReadUInt32LittleEndian(span[5..]) == trap!.NetId), "its spawn reaches the enemy's client");
        caitlyn.Advance(2.0);
        TestAssert.True(Trap(caitlyn) is not null && caitlyn.Runtime.Spells.CanSeeTarget(caitlyn.Foe, trap!), "and it stays visible and armed");
        TestAssert.Equal(0, caitlyn.Module.ScriptFailures.Count, "no script failed: " + Failures(caitlyn));

        using var nidalee = new Match("Nidalee");
        nidalee.Learn(3);
        nidalee.Refill();
        var aim = nidalee.Hero.Position + new Position2(300, 0);
        nidalee.Foe.Position = aim + new Position2(300, 0);
        var hidden = Observe(nidalee, nidalee.Foe.Team, nidalee.Foe);
        nidalee.Advance(0.3);
        TestAssert.True(nidalee.Cast(1, at: aim), "Bushwhack 300 units ahead");
        nidalee.Advance(1.5);
        var nidaleeTrap = nidalee.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == nidalee.Hero.NetId);
        TestAssert.True(VisibilityRules.IsStealthed(nidaleeTrap), "Nidalee's trap is stealthed (batch 113)");
        TestAssert.False(nidalee.Runtime.Spells.CanSeeTarget(nidalee.Foe, nidaleeTrap), "the enemy does not see it");
        TestAssert.False(hidden.Packets.Any(entry => entry.Packet.Plaintext.Span is { Length: >= 9 } span && span[0] == 0x80 &&
            BinaryPrimitives.ReadUInt32LittleEndian(span[5..]) == nidaleeTrap.NetId), "and its spawn does not reach the enemy");
        TestAssert.Equal(0, nidalee.Module.ScriptFailures.Count, "no script failed: " + Failures(nidalee));
    }

    [Check("scripting.bb-caitlyn-yordle-trap-ignores-minions")]
    public static void YordleTrapIgnoresMinions()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(12000, 12000);
        match.Advance(0.3);
        var placed = new Position2(7300, 7000);
        TestAssert.True(match.Cast(1, at: placed), "Yordle Snap Trap 300 units ahead");
        match.Advance(1.5);
        var minion = Enemy(match, "Minion", new Position2(placed.X + 120.0, placed.Z));
        match.Advance(1.0);
        TestAssert.True(Buff(minion, "CaitlynYordleTrapDebuff") is null, "a minion on the armed trap does not spring it");
        TestAssert.True(Trap(match) is not null, "the trap stays armed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-yordle-trap-keeps-three-and-the-fourth-kills-the-oldest")]
    public static void YordleTrapCapsAtThree()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Foe.Position = new Position2(12000, 12000);
        match.Advance(0.3);
        var points = new[] { new Position2(7150, 7000), new Position2(7150, 7250), new Position2(7150, 7500), new Position2(7150, 7750) };
        match.Refill();
        TestAssert.True(match.Cast(1, at: points[0]), "the first trap");
        match.Advance(0.5);
        var oldest = Trap(match);
        TestAssert.True(oldest is not null, "the first trap stands");
        for (var index = 1; index < 3; index++)
        {
            match.Advance(1.0);
            match.Refill();
            TestAssert.True(match.Cast(1, at: points[index]), $"trap {index + 1}");
            match.Advance(0.5);
        }
        TestAssert.Equal(3, TrapCount(match), "three traps stand");
        match.Advance(1.0);
        match.Refill();
        TestAssert.True(match.Cast(1, at: points[3]), "a fourth trap");
        match.Advance(0.5);
        TestAssert.True(!oldest!.Alive, "the fourth trap kills the oldest, which has the least time left");
        TestAssert.Equal(3, TrapCount(match), "and three stand again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-net-slows-the-unit-it-hits-and-pushes-caitlyn-back")]
    public static void NetSlowsAndPushesBack()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7250, 7000);
        match.Advance(0.3);
        var gap = Distance(match.Hero.Position, match.Foe.Position);
        TestAssert.True(match.Cast(2, match.Foe), "the net at the Foe 250 away");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "Slow") is not null, "the net slows the Foe");
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Raw - 80.0) < 1e-6), "the net deals 80 at rank one");
        match.Advance(0.5);
        var pushed = Distance(match.Hero.Position, match.Foe.Position) - gap;
        TestAssert.True(pushed > 400.0, $"Caitlyn is pushed back about 500 units: the gap grew by {pushed}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-ace-in-the-hole-marks-the-target-then-fires-after-the-channel")]
    public static void AceInTheHoleMarksThenFires()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7800, 7000);
        match.Advance(0.3);
        TestAssert.True(match.Cast(3, match.Foe), "Ace in the Hole at the Foe 800 away");
        match.Advance(0.8);
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is not null, "the Foe carries the mark during the channel");
        match.Advance(3.0);
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is null, "the shot removes the mark");
        TestAssert.True(HitsOn(match, "Foe").Any(hit => Math.Abs(hit.Raw - 250.0) < 1e-6),
            "the shot deals 250 at rank one (no bonus attack damage at level one)");
        TestAssert.True(match.Hero.SpellCooldowns[3] > 80.0, $"the ultimate's cooldown runs from its 90 seconds: {match.Hero.SpellCooldowns[3]}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-ace-in-the-hole-stopped-by-a-stun-sets-the-cooldown-to-five")]
    public static void AceInTheHoleCancelSetsFive()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7800, 7000);
        match.Advance(0.3);
        TestAssert.True(match.Cast(3, match.Foe), "Ace in the Hole at the Foe 800 away");
        match.Advance(0.6);
        match.Runtime.Buffs.AddBuff(match.Hero, "AcceptanceStun", BuffType.Stun, 2.0, match.Foe.NetId, canMitigateDuration: false);
        match.Advance(0.2);
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is null, "the cancel removes the mark");
        TestAssert.Near(5.0, match.Hero.SpellCooldowns[3], 0.5, "the cancel sets the ultimate's cooldown to five seconds");
        match.Advance(4.0);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "no shot fires after a cancel");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 349 (batch 312's open "zombie hook"): the mark's <c>BuffOnZombie</c> (and its update's <c>BBGetIsZombie</c>) stops Caitlyn's channel
    /// with a cancel when the marked champion starts a zombie life. Kog'Maw's Icathian Surprise is one: the cancel removes the mark and sets the
    /// ultimate's cooldown to five seconds (<c>ChannelingCancelStop</c>), and no shot fires.
    /// </summary>
    [Check("scripting.bb-caitlyn-ace-in-the-hole-stops-when-the-target-turns-zombie")]
    public static void AceInTheHoleStopsWhenTheTargetTurnsZombie()
    {
        using var match = new Match("Caitlyn", foeChampion: "KogMaw");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7800, 7000);
        match.Advance(1.2); // CharScriptKogMaw's one-second update arms his Icathian Surprise
        TestAssert.True(Buff(match.Foe, "KogMawIcathianSurpriseReady") is not null, "Kog'Maw's zombie life is armed");
        TestAssert.True(match.Cast(3, match.Foe), "Ace in the Hole at Kog'Maw 800 away");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is not null, "Kog'Maw carries the mark during the channel");
        var hitsBefore = HitsOn(match, "Foe").Count;
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        match.Advance(0.2);
        TestAssert.True(match.Foe.Alive && Buff(match.Foe, "KogMawIcathianSurprise") is not null, "the death starts his zombie life");
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is null, "the cancel removes the mark");
        TestAssert.Near(5.0, match.Hero.SpellCooldowns[3], 0.5, "the cancel sets the ultimate's cooldown to five seconds");
        match.Advance(1.5);
        TestAssert.False(HitsOn(match, "Foe").Skip(hitsBefore).Any(hit => Math.Abs(hit.Raw - 250.0) < 1e-6), "no shot fires");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-headshot-passive-persists-through-a-death")]
    public static void HeadshotPassivePersistsThroughDeath()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshotPassive") is not null, "the passive is on her from activation");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        for (var tick = 0; tick < 1200 && !match.Hero.Alive; tick++)
            match.Advance(0.1);
        TestAssert.True(match.Hero.Alive, "she respawns");
        TestAssert.True(Buff(match.Hero, "CaitlynHeadshotPassive") is not null, "the passive is back on her after the respawn");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-caitlyn-ace-in-the-hole-is-taken-by-the-first-enemy-champion-in-its-path")]
    public static void AceInTheHoleIsTakenByTheFirstEnemyChampion()
    {
        using var match = new Match("Caitlyn");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7800, 7000);
        // Another enemy champion on the line blocks the shot: the first champion it meets takes it. (A minion on the line does not block it.)
        match.FarFoe.Position = new Position2(7450, 7000);
        match.Advance(1.0);
        var farBefore = match.FarFoe.Health;
        var foeBefore = match.Foe.Health;
        TestAssert.True(match.Cast(3, match.Foe), "Ace in the Hole at the Foe, with another champion in the way");
        match.Advance(3.5);
        // The shot is 250 before her armour (the blocker's armour takes about a fifth of it): the Foe takes nothing and every mark ends.
        var farLost = farBefore - match.FarFoe.Health;
        TestAssert.True(farLost > 190.0 && farLost <= 250.0, $"the champion on the line takes the shot: {farLost} lost");
        TestAssert.Equal(foeBefore, match.Foe.Health, "the Foe takes nothing");
        TestAssert.True(Buff(match.Foe, "CaitlynAceintheHole") is null, "the mark on the Foe is removed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
