using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3 (19 October 2026): Ezreal plays from his shipped scripts in a production <c>MatchRuntime</c> (the hand-written module is gone). Expected numbers
/// come from the shipped files (script tables and spell inibins) and are pinned a second time as literals. Written, not run (owner rule: no check runs
/// during the overnight phase 3); the scenario <c>S-ezreal</c> shows the same facts on a headless match, next to the hand-written module's numbers.
/// </summary>
public static class BbEzrealChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static double Between(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string? MathOp(BbBlock block) => (block.Param("MathOp") as BbValue.Reference)?.Name;

    private static void NoFailures(Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Ezreal");
        foreach (var name in new[] { "CharScriptEzreal", "EzrealMysticShot", "EzrealMysticShotMissile", "EzrealEssenceFlux", "EzrealEssenceFluxMissile",
                     "EzrealArcaneShift", "EzrealArcaneShiftMissile", "EzrealTrueshotBarrage", "EzrealRisingSpellForce", "CantAttack" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var hero = new Entity { ChampionName = "Ezreal" };
        TestAssert.Equal(["EzrealMysticShot", "EzrealEssenceFlux", "EzrealArcaneShift", "EzrealTrueshotBarrage"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Ezreal.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");
        // The instacast containers cast their child with a cast time of its own (BBSpellCast of an extra slot, FireWithoutCasting false): that child is
        // the cast the player sees, so a press of Q or W is held inside another windup like a non-instant spell; E and R have no such child.
        TestAssert.Equal(["EzrealMysticShotMissile", "EzrealEssenceFluxMissile", null, null],
            Enumerable.Range(0, 4).Select(slot => module.CasterChildSpell(hero, slot)).ToArray(), "CasterChildSpell");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Ezreal");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native an Ezreal hook runs is bound: " + string.Join(", ", unbound));
        var extras = GameData.Default.Champion("Ezreal").Raw;
        TestAssert.Equal("EzrealMysticShotMissile", extras[InibinReader.KeyHash("Data*ExtraSpell1")].AsString(), "ExtraSpell1 is the Mystic Shot bolt");
        TestAssert.Equal("EzrealArcaneShiftMissile", extras[InibinReader.KeyHash("Data*ExtraSpell2")].AsString(), "ExtraSpell2 is the Arcane Shift bolt");
        TestAssert.Equal("EzrealEssenceFluxMissile", extras[InibinReader.KeyHash("Data*ExtraSpell3")].AsString(), "ExtraSpell3 is the Essence Flux wave");
    }

    [Check("scripting.bb-ezreal-production-runs-the-shipped-scripts")]
    public static void ProductionRunsEzrealFromHisScripts()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0);
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        TestAssert.True(dispatcher.FindModule("Ezreal") is BbChampionModule, "Ezreal is a BbChampionModule in production (phase 3, 19 October 2026)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q: Mystic Shot
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-q-hits-the-first-unit-refunds-one-second-and-stacks-the-passive")]
    public static void MysticShotHitsTheFirstUnitRefundsOneSecondAndAddsAStack()
    {
        var missile = Script("EzrealMysticShotMissile");
        TestAssert.Equal([35.0, 55.0, 75.0, 95.0, 115.0], ByLevel("EzrealMysticShotMissile", "TargetExecute", "SpellBaseDamage"),
            "EzrealMysticShotMissile.luaobj: base damage by rank");
        TestAssert.Near(0.2, Blocks(missile, "TargetExecute").First(item => item.Function == "BBMath" && item.Text("DestVar") == "FinalAP")
            .Number("Src2Value")!.Value, 1e-12, "0.2 of the ability power");
        TestAssert.Near(1.0, Blocks(missile, "TargetExecute").First(item => item.Function == "BBMath" && item.Text("DestVar") == "newCooldown")
            .Number("Src2Value")!.Value, 1e-12, "every cooldown loses one second");

        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7600, 7000);
        match.FarFoe.Position = new Position2(7900, 7000);
        for (var slot = 1; slot < 4; slot++) match.Hero.SpellCooldowns[slot] = 10.0;
        var mana = match.Hero.Mana;
        var started = match.Now;
        TestAssert.True(match.Cast(0, at: new Position2(7600, 7000)), "Mystic Shot at the ground");
        TestAssert.Near(mana - 40.0, match.Hero.Mana, 1.0, "40 mana at rank 3");
        TestAssert.Near(5.0, match.Hero.SpellCooldowns[0], 0.1, "rank 3 cooldown 5 s");
        match.Advance(1.0);
        var hit = HitsOn(match, "Foe").Single(item => item.Kind == "Basic");
        NearF32(75.0 + match.Hero.AttackDamage, hit.Raw, "rank 3: 75 + total attack damage + 0.2 of the (zero) ability power, dealt as one attack-source hit");
        TestAssert.True(hit.At - started is > 0.44 and < 0.58,
            $"the bolt leaves after the child's 0.251 s and flies 600 less the contact distance at 2000: {hit.At - started}");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "the unit behind is not hit: the first hit destroys the bolt");
        TestAssert.True(match.Log.DestroyedMissiles.Count == 1, "one missile destroyed");
        // Every cooldown loses a second when a visible unit is hit (the engine ticks them for the second since the cast).
        TestAssert.Near(10.0 - 1.0 - 1.0, match.Hero.SpellCooldowns[1], 0.1, "W: 10 s, a second elapsed, a second refunded");
        TestAssert.Near(10.0 - 1.0 - 1.0, match.Hero.SpellCooldowns[3], 0.1, "R likewise");
        TestAssert.Near(5.0 - 1.0 - 1.0, match.Hero.SpellCooldowns[0], 0.1, "and Mystic Shot's own cooldown");
        TestAssert.Equal(1, Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "the hit adds a Rising Spell Force stack");
        NoFailures(match);
    }

    [Check("scripting.bb-ezreal-q-flies-its-rank-range-and-a-spell-shield-eats-the-hit-but-not-the-stack")]
    public static void MysticShotFliesOneThousandTwoHundredAndBreaksAShield()
    {
        foreach (var (distance, hits) in new[] { (1150.0, true), (1400.0, false) })
        {
            using var match = new Match("Ezreal");
            match.Learn(3);
            match.Refill();
            match.Foe.Position = new Position2(7000 + distance, 7000);
            match.Foe.Health = match.Foe.MaxHealth = 5000;
            match.Cast(0, at: new Position2(7000 + distance, 7000));
            match.Advance(2.0);
            TestAssert.Equal(hits, HitsOn(match, "Foe").Count == 1,
                $"EzrealMysticShotMissile: CastRange1..5 is 1200, width 60 + the unit's radius: a unit at {distance} {(hits ? "is" : "is not")} struck");
            NoFailures(match);
        }

        using var shielded = new Match("Ezreal");
        shielded.Learn(3);
        shielded.Refill();
        shielded.Foe.Position = new Position2(7600, 7000);
        shielded.Runtime.Buffs.AddBuff(shielded.Foe, "BansheesVeil", BuffType.Aura, 10.0, shielded.Foe.NetId);
        shielded.Cast(0, at: new Position2(7600, 7000));
        shielded.Advance(1.0);
        TestAssert.Near(5000.0, shielded.Foe.Health, 1e-9, "BBBreakSpellShields pops the veil and the hit is zeroed");
        TestAssert.False(shielded.Foe.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the veil is gone");
        TestAssert.Equal(1, Of(shielded.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "the passive stack is added all the same");
        NoFailures(shielded);
    }

    [Check("scripting.bb-ezreal-q-and-w-complete-the-client-cast-at-launch-and-the-next-order-stops-the-clip")]
    public static void TheWindupChildCompletesItsCastAndTheNextOrderStopsTheClip()
    {
        foreach (var slot in new[] { 0, 1 })
        {
            using var match = new Match("Ezreal");
            match.Learn(3);
            match.Refill();
            var wire = Observe(match, Team.Order, match.Hero);
            match.Advance(0.2);
            wire.Packets.Clear();
            var childHash = SpellSystem.ProtocolHash(slot == 0 ? "EzrealMysticShotMissile" : "EzrealEssenceFluxMissile");
            TestAssert.True(match.Cast(slot, at: new Position2(8000, 7000)), "the cast");
            match.Advance(0.1);
            var packets = wire.Packets.Select(item => item.Packet.Plaintext).ToList();
            TestAssert.False(packets.Any(p => p.Span[0] == 0x39), "no cast completion before the child's cast frame");
            // An order inside the windup is held by the gate and reaches the clip not at all.
            match.Runtime.Spells.CancelOrder(match.Hero);
            TestAssert.False(wire.Packets.Any(item => item.Packet.Plaintext.Span[0] == 0x2B), "a stop inside the windup would cut the gesture: none is sent");
            match.Advance(0.3);
            var completion = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] == 0x39).ToArray();
            TestAssert.Equal(1, completion.Length, $"slot {slot}: the launch completes the native child once");
            var finish = completion[0].Packet.Plaintext.Span;
            TestAssert.Equal(match.Hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(finish[1..]), "for the caster");
            TestAssert.Equal((byte)0x07, finish[5], "KeepAnimating | ForceSpellCast | ForceStop (the client's handler)");
            var child = wire.Packets.First(item => item.Packet.Plaintext.Span[0] == 0xBD && item.Packet.Plaintext.Length > 15 &&
                                                   BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[11..]) == childHash);
            TestAssert.True(wire.Packets.IndexOf(child) < wire.Packets.IndexOf(completion[0]), "the child cast precedes its completion");

            wire.Packets.Clear();
            match.Runtime.Spells.CancelOrder(match.Hero);
            match.Advance(0.05);
            TestAssert.Equal(1, wire.Packets.Count(item => item.Packet.Plaintext.Span[0] == 0x2B), $"slot {slot}: the first order after launch stops the clip");
            wire.Packets.Clear();
            match.Runtime.Spells.CancelOrder(match.Hero);
            match.Advance(0.05);
            TestAssert.Equal(0, wire.Packets.Count(item => item.Packet.Plaintext.Span[0] == 0x2B), "and only the first");
            NoFailures(match);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: Essence Flux
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-w-passes-allies-and-enemies-buffs-and-hurts-and-never-hits-ezreal")]
    public static void EssenceFluxBuffsAnAllyHurtsAnEnemyAndSkipsItsCaster()
    {
        TestAssert.Equal([0.2, 0.25, 0.3, 0.35, 0.4], ByLevel("EzrealEssenceFluxMissile", "TargetExecute", "AttackSpeedMod"), "EzrealEssenceFluxMissile.luaobj: attack speed by rank");
        var damage = Blocks(Script("EzrealEssenceFluxMissile"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal([80.0, 130.0, 180.0, 230.0, 280.0], Table(damage, "DamageByLevel"), "damage by rank");
        TestAssert.Near(0.6, damage.Number("SpellDamageRatio")!.Value, 1e-12, "0.6 of the ability power");

        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Refill();
        match.Ally.Position = new Position2(7150, 7000);
        match.Foe.Position = new Position2(7500, 7000);
        var started = match.Now;
        TestAssert.True(match.Cast(1, at: new Position2(8000, 7000)), "Essence Flux at the ground");
        match.Advance(1.2);
        var hit = HitsOn(match, "Foe").Single(item => item.Kind == "Spell");
        NearF32(180.0, hit.Raw, "rank 3: 180 + 0.6 of the (zero) ability power, magic");
        TestAssert.True(hit.At - started is > 0.3 and < 0.6, $"leaves after 0.25 s and flies 1600: {hit.At - started}");
        var ally = Of(match.Ally, "EzrealEssenceFlux");
        TestAssert.True(ally is { Type: BuffType.CombatEnhancer }, "the allied hero gets the attack speed buff");
        NearF32(0.3, ally!.Payload.AttackSpeedMod, "rank 3: +30 percent attack speed");
        TestAssert.True(ally.ExpiresAt is > 3.5 and <= 5.0, $"for 5 s (the count down since it was added): {ally.ExpiresAt}");
        var foe = Of(match.Foe, "EzrealEssenceFluxMissile");
        TestAssert.True(foe is { Type: BuffType.CombatDehancer }, "the enemy hero gets the debuff");
        NearF32(-0.3, foe!.Payload.AttackSpeedMult, "multiplicative -30 percent");
        TestAssert.True(Of(match.Hero, "EzrealEssenceFlux") is null && Of(match.Hero, "EzrealEssenceFluxMissile") is null, "NotAffectSelf: Ezreal is not struck by his own wave");
        TestAssert.Equal(2, Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "one stack per unit struck: the ally and the enemy");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Ezreal->Ally", StringComparison.Ordinal)), "the allied buff records the assist marker");
        NoFailures(match);
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Arcane Shift
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-e-blinks-475-then-bolts-the-nearest-unit-with-a-bubble-and-a-stack")]
    public static void ArcaneShiftBlinksAndFiresItsBoltAtTheNearestUnit()
    {
        var bolt = Blocks(Script("EzrealArcaneShiftMissile"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal([75.0, 125.0, 175.0, 225.0, 275.0], Table(bolt, "DamageByLevel"), "EzrealArcaneShiftMissile.luaobj: damage by rank");
        TestAssert.Near(0.75, bolt.Number("SpellDamageRatio")!.Value, 1e-12, "0.75 of the ability power");
        var shift = Script("EzrealArcaneShift");
        TestAssert.Near(475.0, Blocks(shift, "SelfExecute").First(item => item.Function == "BBGetPointByUnitFacingOffset").Number("Distance")!.Value, 1e-12,
            "EzrealArcaneShift.luaobj: the blink is 475");
        var scan = Blocks(shift, "SelfExecute").First(item => item.Function == "BBForNClosestUnitsInTargetArea");
        TestAssert.Near(750.0, scan.Number("Range")!.Value, 1e-12, "the bolt looks 750 around Ezreal");
        TestAssert.Near(5.0, scan.Number("MaximumUnitsToPick")!.Value, 1e-12, "at the five nearest units");

        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7775, 7000);
        match.FarFoe.Position = new Position2(7950, 7080);
        var bubbles = match.Runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, at: new Position2(7600, 7000)), "Arcane Shift 600 away: the blink is clamped");
        match.Advance(0.3);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        TestAssert.Near(mana - 90.0, match.Hero.Mana, 1.0, "90 mana");
        TestAssert.Near(15.0, match.Hero.SpellCooldowns[2] + 0.3 - match.CastFrameOf(2), 0.1, "rank 3 cooldown 15 s");
        TestAssert.Near(475.0, Between(new Position2(7000, 7000), match.Hero.Position), 5.0, "the blink is 475 units");
        TestAssert.True(match.Log.Casts.Any(cast => cast.Slot == SpellSystem.ExtraSpellSlotStart + 1 && cast.Targets.Count == 1 &&
                                                    cast.Targets[0].NetId == match.Foe.NetId),
            "the bolt is the script's extra-slot child, announced with the nearest unit as its only target");
        match.Advance(0.5);
        var hit = HitsOn(match, "Foe").Single(item => item.Kind == "Spell");
        NearF32(175.0, hit.Raw, "rank 3: 175 + 0.75 of the (zero) ability power, magic");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "one bolt, one unit");
        TestAssert.Equal(1, Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "the bolt adds a stack");
        var fresh = match.Runtime.World.TimedVisionBubbles.Where(bubble => !bubbles.Contains(bubble.BubbleId)).ToArray();
        TestAssert.Equal(1, fresh.Length, "the picked unit anchors one perception bubble");
        TestAssert.True(fresh[0].AnchorNetId == match.Foe.NetId && fresh[0].Team == Team.Order && fresh[0].Radius == 100.0 && !fresh[0].RevealStealth,
            "radius 100 for Ezreal's team, ordinary sight only");
        var toward = new Position2(match.Foe.Position.X - match.Hero.Position.X, match.Foe.Position.Z - match.Hero.Position.Z);
        TestAssert.Near(1.0, (match.Hero.Facing.X * toward.X + match.Hero.Facing.Z * toward.Z) / Between(Position2.Zero, toward), 0.02,
            "he turned to the unit he picked");
        NoFailures(match);

        using var alone = new Match("Ezreal");
        alone.Learn(3);
        alone.Refill();
        alone.Foe.Position = new Position2(12000, 10000);
        alone.Cast(2, at: new Position2(7300, 7000));
        alone.Advance(1.0);
        TestAssert.Near(300.0, Between(new Position2(7000, 7000), alone.Hero.Position), 5.0, "a short click blinks to the click");
        TestAssert.False(alone.Log.Casts.Any(cast => cast.Slot == SpellSystem.ExtraSpellSlotStart + 1), "no unit within 750: no bolt");
        TestAssert.Equal(0, Of(alone.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "and no stack");
        NoFailures(alone);
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: Trueshot Barrage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-r-falls-eight-percent-per-unit-after-a-one-second-windup-and-locks-his-attacks")]
    public static void TrueshotBarrageFallsEightPercentPerUnit()
    {
        var script = Script("EzrealTrueshotBarrage");
        var damage = Blocks(script, "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal([350.0, 500.0, 650.0], Table(damage, "DamageByLevel"), "EzrealTrueshotBarrage.luaobj: damage by rank");
        TestAssert.Near(0.9, damage.Number("SpellDamageRatio")!.Value, 1e-12, "0.9 of the ability power");
        TestAssert.Near(0.92, Blocks(script, "TargetExecute").First(item => item.Function == "BBMath" && item.Text("DestVarTable") == "CharVars" &&
                                                                         MathOp(item) == "MO_MULTIPLY").Number("Src2Value")!.Value, 1e-12, "each unit struck takes 8 percent off the next");
        TestAssert.Near(0.3, Blocks(script, "TargetExecute").First(item => item.Function == "BBMath" && MathOp(item) == "MO_MAX").Number("Src2Value")!.Value, 1e-12,
            "down to 30 percent");

        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(8000, 7000);
        var minion = Enemy(match, "M1", new Position2(8700, 7000));
        match.FarFoe.Position = new Position2(9400, 7000);
        var mana = match.Hero.Mana;
        var started = match.Now;
        TestAssert.True(match.Cast(3, at: new Position2(12000, 7000)), "the barrage along the lane");
        // Batch 340: the one-second windup pays at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not at the press.
        TestAssert.Near(mana, match.Hero.Mana, 1.0, "the press spends nothing yet");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 1e-9, "and starts no cooldown");
        match.Advance(0.1);
        TestAssert.True(Of(match.Hero, "CantAttack") is not null, "CharOnSpellCast locks his attacks for the cast");
        match.Advance(0.5);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing is hit inside the one second windup");
        match.Advance(2.0);
        TestAssert.True(Of(match.Hero, "CantAttack") is null, "the lock lasts one second");
        TestAssert.Near(mana - 150.0, match.Hero.Mana, 3.5, "150 mana, spent at the cast frame (regeneration allowed)");
        TestAssert.Near(80.0, match.Hero.SpellCooldowns[3] + 2.6 - match.CastFrameOf(3), 0.1, "80 s from the cast frame");
        var first = HitsOn(match, "Foe").Single(item => item.Kind == "SpellAoe");
        var second = HitsOn(match, "M1").Single(item => item.Kind == "SpellAoe");
        var third = HitsOn(match, "FarFoe").Single(item => item.Kind == "SpellAoe");
        var bonus = match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Ezreal"), match.Hero.Level).AttackDamage;
        TestAssert.Near(650.0 + bonus, first.Raw, 0.5, "rank 3: 650 + bonus attack damage + 0.9 of the (zero) ability power");
        TestAssert.Near(first.Raw * 0.92, second.Raw, 0.5, "the second unit takes 92 percent");
        TestAssert.Near(first.Raw * 0.92 * 0.92, third.Raw, 0.5, "the third 92 percent of that");
        TestAssert.True(first.At - started > 1.0 && first.At < second.At && second.At < third.At, "in order along the line, after the windup");
        TestAssert.Equal(3, Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "one stack per unit struck");

        // The next cast starts at full damage again: CharOnSpellCast resets the factor.
        match.Refill();
        match.Foe.Health = match.Foe.MaxHealth = 5000;
        match.Foe.Position = new Position2(8000, 7000);
        match.Cast(3, at: new Position2(12000, 7000));
        match.Advance(2.0);
        var again = HitsOn(match, "Foe").Where(item => item.Kind == "SpellAoe").ToArray();
        TestAssert.Equal(2, again.Length, "struck again");
        TestAssert.Near(first.Raw, again[1].Raw, 0.5, "the factor starts again at one");
        NoFailures(match);
    }

    [Check("scripting.bb-ezreal-q-and-w-pressed-inside-r-are-held-and-e-is-held-too")]
    public static void APressInsideTheBarragesWindupIsHeldToTheCastFrame()
    {
        foreach (var slot in new[] { 0, 1, 2 })
        {
            using var match = new Match("Ezreal");
            match.Learn(3);
            match.Refill();
            var started = match.Now;
            TestAssert.True(match.Cast(3, at: new Position2(12000, 7000)), "R");
            match.Advance(0.1);
            // The dispatcher holds a press of a spell that creates a SpellCaster inside another windup; the hold is applied by the cast gate
            // of the session, so here the hero's windup state is read the way the gate reads it.
            TestAssert.True(SpellWindup.IsWindingUp(match.Hero), "the barrage's windup is running");
            TestAssert.True(match.Runtime.Spells.CreatesSpellCaster(match.Hero, new SpellCastRequest(slot, false, new Position3(8000, 0, 7000), new Position3(8000, 0, 7000), 0)),
                $"slot {slot} creates a SpellCaster on the client (Q and W through their child), so the press is held");
            NoFailures(match);
            _ = started;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Rising Spell Force
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ezreal-passive-stacks-five-times-draws-a-glow-per-stack-and-removes-them-all")]
    public static void RisingSpellForceStacksFiveTimesAndItsGlowsEndWithTheBuff()
    {
        var passive = Script("EzrealRisingSpellForce");
        TestAssert.Near(0.1, Blocks(passive, "BuffOnUpdateStats").First(item => item.Function == "BBIncStat").Number("Delta")!.Value, 1e-12, "10 percent attack speed per stack");
        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Foe.Position = new Position2(7600, 7000);
        var stacks = new List<int>();
        for (var shot = 1; shot <= 6; shot++)
        {
            match.Refill();
            match.Foe.Health = match.Foe.MaxHealth = 5000;
            TestAssert.True(match.Cast(0, at: new Position2(7600, 7000)), $"shot {shot}");
            match.Advance(0.9);
            stacks.Add(Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0);
        }
        TestAssert.Equal([1, 2, 3, 4, 5, 5], stacks.ToArray(), "five stacks at most");
        NearF32(0.5, Of(match.Hero, "EzrealRisingSpellForce")!.Payload.AttackSpeedMod, "50 percent attack speed at five stacks");
        var glows = match.Log.EffectCalls.Where(call => call.Name.StartsWith("Ezreal_glow", StringComparison.OrdinalIgnoreCase)).ToArray();
        TestAssert.Equal(["Ezreal_glow1.troy", "Ezreal_glow2.troy", "Ezreal_glow3.troy", "Ezreal_glow4.troy", "Ezreal_glow5.troy"],
            glows.Select(call => call.Name).ToArray(), "one glow per stack, the sixth hit at full stacks draws none (LastCount guard of the script)");
        TestAssert.True(glows.All(call => call.Bind == match.Hero.NetId && call.Bone == "l_hand"), "bound to his hand");
        match.Advance(6.5);
        TestAssert.True(Of(match.Hero, "EzrealRisingSpellForce") is null, "six seconds after the last hit the buff is gone");
        TestAssert.True(glows.All(call => match.Log.Killed.Contains(call.Id)),
            "every glow is killed with the buff: the stacks' earlier ids are not lost to the shared InstanceVars");
        NoFailures(match);
    }

    [Check("scripting.bb-ezreal-death-clears-the-passive-and-the-cast-lock")]
    public static void DeathClearsTheStacksAndTheUnlaunchedCasts()
    {
        using var match = new Match("Ezreal");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7600, 7000);
        match.Cast(0, at: new Position2(7600, 7000));
        match.Advance(0.9);
        TestAssert.Equal(1, Of(match.Hero, "EzrealRisingSpellForce")?.Stacks ?? 0, "a stack");
        match.Refill();
        match.Cast(3, at: new Position2(12000, 7000));
        match.Advance(0.2);
        TestAssert.True(match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId), "he dies inside the barrage's windup");
        match.Advance(2.0);
        TestAssert.True(Of(match.Hero, "EzrealRisingSpellForce") is null && Of(match.Hero, "CantAttack") is null, "the stacks and the cast lock are gone");
        TestAssert.False(EzrealFlights.WindingUp(match.Runtime.Spells, match.Hero), "the unfinished barrage is cancelled");
        TestAssert.Equal(0, EzrealFlights.InFlight(match.Runtime.Spells, match.Hero).Length, "and never launches");
        NoFailures(match);
    }
}
