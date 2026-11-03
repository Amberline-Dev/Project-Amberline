using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine M3: Ryze plays from his shipped scripts in a production <c>MatchRuntime</c>. Every expected number is
/// read from the shipped files (the script tables through <see cref="BbScriptLoader"/>, the spell inibins through
/// <see cref="GameData"/>) and pinned a second time as the literal the two files agree on.
/// </summary>
public static class BbRyzeChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // ------------------------------------------------------------------------------------------------------------
    // Closure, cast kinds, registration, packets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Ryze");
        var hero = new Entity { ChampionName = "Ryze" };
        TestAssert.Equal(["Overload", "RunePrison", "SpellFlux", "DesperatePower"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Ryze.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Ryze");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(module.ScriptNames.Count, closure.ScriptNames.Count(), "the module loads the whole closure");
        TestAssert.Equal(16, closure.ScriptNames.Count(), "the Ryze closure is 16 scripts (audit bb-coverage)");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 16 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Ryze hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-cast-type-and-chain-parameters-come-from-the-shipped-data")]
    public static void CastTypeDecidesTheFlightAndTheScriptNamesTheChain()
    {
        // The client builds a flying missile only for CastType 1 (missile), 2 (chain) and 3 (line); CastType 0 runs the effect at
        // once (the cast-and-serve routine), whatever MissileSpeed the inibin lists.
        var expected = new (string Spell, int CastType, double Speed)[]
        {
            ("Overload", 1, 1400.0), ("RunePrison", 0, 500.0), ("SpellFlux", 2, 1300.0), ("DesperatePower", 0, 1200.0),
            ("Imbue", 0, 1200.0), ("Dazzle", 1, 1400.0), ("JudicatorReckoning", 1, 1301.4), ("JudicatorDivineBlessing", 0, 20.0),
            ("RicochetAttack", 2, 900.0), ("SpiralBladeMissile", 3, 1350.0),
        };
        foreach (var (spell, castType, speed) in expected)
        {
            var data = GameData.Default.Spell(spell, 1);
            TestAssert.Equal(castType, BbChampionModule.CastType(data), $"{spell} CastType");
            TestAssert.Near(speed, data.MissileSpeed, 0.05, $"{spell} MissileSpeed");
        }

        var flux = BbChainParameters.Read(Script("SpellFlux"))!;
        TestAssert.Equal([5.0, 5.0, 5.0, 5.0, 5.0], flux.MaximumHits.ToArray(), "SpellFlux.luaobj MaximumHits");
        TestAssert.True(flux.CanHitCaster && flux.CanHitSameTarget && !flux.CanHitSameTargetConsecutively, "SpellFlux.luaobj chain flags");
        TestAssert.Near(375.0, BbChainParameters.BounceRadius(GameData.Default.Spell("SpellFlux", 1)), 1e-9, "SpellFlux.inibin BounceRadius");
        var ricochet = BbChainParameters.Read(Script("RicochetAttack"))!;
        TestAssert.Equal([2.0, 3.0, 4.0, 5.0, 6.0], ricochet.MaximumHits.ToArray(), "RicochetAttack.luaobj MaximumHits");
        TestAssert.True(!ricochet.CanHitCaster && !ricochet.CanHitSameTarget && !ricochet.CanHitSameTargetConsecutively, "RicochetAttack chain flags");
        TestAssert.True(BbChainParameters.Read(Script("Overload")) is null, "a spell without the table has no chain");
    }

    [Check("scripting.bb-chain-missile-sync-matches-the-client-struct")]
    public static void ChainMissileSyncPacketHasTheClientsLayout()
    {
        // ChainMissileSync: cmd, FromID, int size, owner, 32 target slots (141 bytes), the layout
        // the spell caster's chain-missile setter copies, and the patch-126 enum value ChainMissileSync = 0x6F.
        // The sender is the missile (the obj_SpellChainMissile registers for packets from its
        // own net ID, through its getter and handler); the owner is the caster.
        var packet = Amberline.Network.Protocol.SpellPackets.ChainMissileSync(0x40030007u, 0x40000001u,
            [0x40000002u, 0x40000001u, 0x40000002u]);
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(141, bytes.Length, "sizeof(ChainMissileSync)");
        TestAssert.Equal((byte)0x6F, bytes[0], "opcode");
        TestAssert.Equal(0x40030007u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "sender (FromID) is the missile");
        TestAssert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]), "target count at +5");
        TestAssert.Equal(0x40000001u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]), "owner at +9");
        TestAssert.Equal(0x40000002u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[13..]), "first target at +13 (0xD)");
        TestAssert.Equal(0x40000001u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[17..]), "second target");
        TestAssert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]), "unused slots are zero");
        TestAssert.True(packet.Reliability == Amberline.Network.Protocol.LeaguePacketReliability.Reliable, "reliable");
    }

    /// <summary>
    /// The owner's playtest of 16 October 2026: Spell Flux vanished after its first hit. The client's chain missile reads its hops from
    /// a ChainMissileSync whose sender is the missile (it registers for its own net ID, through its getter and
    /// its handler) and which reaches it only once it exists, one windup after the cast; the old packet (sender and owner the
    /// caster, sent with the cast) fell through to the world-event dispatcher's owner lookup, which never resolves a net ID.
    /// </summary>
    [Check("scripting.bb-ryze-chain-sync-reaches-the-client-in-the-missiles-name-after-its-windup")]
    public static void ChainSyncIsSentInTheMissilesNameWhenTheClientHasTheMissile()
    {
        using var match = new Match("Ryze");
        match.Learn(1);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        match.Refill();
        wire.Packets.Clear();
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        var cast = match.Log.Casts[^1];
        match.Advance(1.0);
        var packets = wire.Packets;
        var castIndex = packets.FindIndex(item => item.Packet.Plaintext.Span[0] == 0xBD);
        TestAssert.True(castIndex >= 0, "the client received the cast");
        var syncs = packets.Where(item => item.Packet.Plaintext.Span[0] == 0x6F).ToArray();
        TestAssert.Equal(2, syncs.Length, "the list is sent twice (BbChainSync)");
        TestAssert.True(packets.IndexOf(syncs[0]) > castIndex, "after the cast");
        var windup = GameData.Default.Spell("SpellFlux", 5).CastTime;
        var castAt = packets[castIndex].At;
        TestAssert.Near(windup + BbChainSync.FirstMargin, syncs[0].At - castAt, 0.08, "first copy: one windup and a margin after the cast");
        TestAssert.Near(windup + BbChainSync.SecondMargin, syncs[1].At - castAt, 0.08, "second copy a little later");
        foreach (var (_, packet) in syncs)
        {
            var bytes = packet.Plaintext.ToArray();
            TestAssert.Equal(141, bytes.Length, "sizeof(ChainMissileSync)");
            TestAssert.Equal(cast.MissileNetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
                "FromID is the missile the cast named (the client's missile registers for packets from its own ID)");
            TestAssert.True(cast.MissileNetId != match.Hero.NetId, "not the caster");
            TestAssert.Equal(5, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]), "five hops");
            TestAssert.Equal(match.Hero.NetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]), "owner is the caster");
            var hops = Enumerable.Range(0, 5).Select(index => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(13 + 4 * index)..])).ToArray();
            TestAssert.Equal([match.Foe.NetId, match.Hero.NetId, match.Foe.NetId, match.Hero.NetId, match.Foe.NetId], hops, "the hops in flight order");
        }
        TestAssert.False(packets.Any(item => item.Packet.Plaintext.Span[0] == 0x6F &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(item.Packet.Plaintext.Span[1..]) == match.Hero.NetId),
            "nothing is addressed to the caster any more");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // Batch 177 part 4, the owner's 24 October finding 19 ("E keeps bouncing off a dead target ... Otherwise it should not bounce off dead
    // targets"): a planned unit that dies before the orb starts toward it is not a stop. The orb replaces it with the nearest living unit when it
    // reaches its current target, and the client, which follows the list it was sent, is sent the new list. The last list no longer names it.
    [Check("scripting.bb-ryze-e-replans-a-planned-hop-that-died-before-the-orb-reached-it")]
    public static void SpellFluxDoesNotBounceOffAUnitThatDiedBeforeTheOrbReachedIt()
    {
        using var match = new Match("Ryze");
        match.Learn(1);
        var wire = Observe(match, Team.Order, match.Hero);
        match.FarFoe.Position = new Position2(7500, 7000);
        match.Advance(0.3);
        match.Refill();
        wire.Packets.Clear();
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        TestAssert.True(match.Runtime.Combat.Kill(match.FarFoe, match.Hero.NetId), "the second unit dies while the orb is still flying to the first");
        var dead = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dead, match.FarFoe.NetId);
        match.Advance(1.2);
        var syncs = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] == 0x6F).ToArray();
        TestAssert.True(syncs.Length >= 3, $"the list is sent again once the orb reaches the first unit ({syncs.Length} copies)");
        TestAssert.True(syncs[^1].Packet.Plaintext.Span.IndexOf(dead) < 0, "and the last list no longer names the dead unit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// "Ryze E should bounce off dead targets instead of disappearing" (owner, 16 October 2026): a chain missile whose next unit died
    /// in flight is not destroyed. The client's chain missile flies to the unit's last position, hits there and goes on
    /// down its list, so the server flies on to the corpse, hits nothing there and bounces on to the next unit.
    /// </summary>
    [Check("scripting.bb-ryze-e-bounces-off-a-corpse-and-is-not-destroyed")]
    public static void SpellFluxFliesOnToTheCorpseAndBouncesOff()
    {
        // Foe 300 from Ryze, the other enemy hero 200 beyond: Foe, Other, Foe, Other, Foe at 1300 units a second.
        using var match = new Match("Ryze");
        match.Learn(1);
        match.FarFoe.Position = new Position2(7500, 7000);
        match.Advance(0.2);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        var legToFarFoe = 200.0 / 1300.0;
        while (HitsOn(match, "Foe").Count == 0) match.Advance(Step);
        var firstHit = HitsOn(match, "Foe")[0].At;
        match.Advance(Step);
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "the second unit is still being flown to");
        TestAssert.True(match.Runtime.Combat.Kill(match.FarFoe, match.Hero.NetId), "the second unit dies under the orb");
        match.Advance(1.5);
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "a corpse takes no hit");
        var foeHits = HitsOn(match, "Foe");
        TestAssert.Equal(3, foeHits.Count, "the later hops on the Foe still land");
        // Flying on to the corpse and back costs the two legs; skipping the dead hop at once (the old rule) turned round after
        // one tick of flight and was back on the Foe within about a tenth of a second.
        TestAssert.Near(2.0 * legToFarFoe, foeHits[1].At - firstHit, 0.08,
            $"the orb bounced off the corpse: two legs to the next hit ({foeHits[1].At - firstHit:0.000} s)");
        TestAssert.Equal(0, match.Log.DestroyedMissiles.Count, "the missile was never destroyed");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "and the chain ended on its own");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");

        // The first unit dies before the orb reaches it: it still arrives at the corpse, which takes no hit, and goes on.
        if (match.FarFoe.RespawnAt <= 0.0) match.Runtime.Combat.ScheduleRespawn(match.FarFoe);
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(match.FarFoe), "the other hero is back");
        match.FarFoe.Position = new Position2(7500, 7000);
        match.Refill();
        match.Advance(15.0);
        match.Refill();
        var foeBefore = HitsOn(match, "Foe").Count;
        var farBefore = HitsOn(match, "FarFoe").Count;
        TestAssert.True(match.Cast(2, match.FarFoe), "second E cast, at the other hero");
        match.Advance(GameData.Default.Spell("SpellFlux", 5).CastTime + 0.1);
        TestAssert.Equal(farBefore, HitsOn(match, "FarFoe").Count, "the missile has left and is not there yet");
        TestAssert.True(match.Runtime.Combat.Kill(match.FarFoe, match.Hero.NetId), "the first unit dies in flight");
        match.Advance(2.0);
        TestAssert.Equal(farBefore, HitsOn(match, "FarFoe").Count, "no hit on the corpse");
        TestAssert.True(HitsOn(match, "Foe").Count - foeBefore >= 2, "the orb bounced on to the Foe: " + (HitsOn(match, "Foe").Count - foeBefore));
        TestAssert.Equal(0, match.Log.DestroyedMissiles.Count, "still never destroyed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-ryze-chain-hops-follow-the-parameters")]
    public static void TheChainBouncesToTheNearestAllowedUnitAndBackToTheCaster()
    {
        using var match = new Match("Ryze");
        var data = GameData.Default.Spell("SpellFlux", 1);
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(data));
        var parameters = BbChainParameters.Read(Script("SpellFlux"))!;
        string Chain(Entity first) => string.Join(",", parameters.Build(match.Runtime.Spells, match.Hero, first, data, 1, flags)
            .Select(unit => unit.NetId == match.Hero.NetId ? "Ryze" : unit.NetId == match.Foe.NetId ? "Foe" : unit.Name));

        // One enemy with Ryze 300 away (inside 375): the orb goes back and forth between them, five hits in all.
        TestAssert.Equal("Foe,Ryze,Foe,Ryze,Foe", Chain(match.Foe), "a lone enemy bounces to Ryze and back");
        // Ryze out of reach (500 > 375): nothing else to hit, the chain stops after the first.
        match.Hero.Position = new Position2(6800, 7000);
        TestAssert.Equal("Foe", Chain(match.Foe), "no unit within the bounce radius ends the chain");
        match.Hero.Position = new Position2(7000, 7000);

        // Two enemies: the orb alternates between them (the same unit twice in a row is not allowed, an earlier one again is);
        // Ryze is only the last resort.
        Enemy(match, "Minion", new Position2(7500, 7000));
        TestAssert.Equal("Foe,Minion,Foe,Minion,Foe", Chain(match.Foe), "two enemies alternate and Ryze is skipped");
        // Friends and enemies beyond the radius are not hops.
        match.Ally.Position = new Position2(7350, 7000);
        Enemy(match, "FarMinion", new Position2(7300, 7600));
        TestAssert.Equal("Foe,Minion,Foe,Minion,Foe", Chain(match.Foe), "an ally and a unit beyond 375 are not hops");
        // A dead unit is not a hop (Ryze steps up so that he is the only unit left within reach of the minion).
        match.Foe.Health = 0.0;
        match.Foe.Alive = false;
        match.Hero.Position = new Position2(7300, 7000);
        TestAssert.Equal("Minion,Ryze,Minion,Ryze,Minion", Chain(match.Runtime.World.Entities.Values.First(unit => unit.Name == "Minion")),
            "a dead unit is skipped and the orb returns to Ryze");
    }

    [Check("scripting.bb-ryze-cast-packets-pass-the-four-client-gates")]
    public static void CastPacketsCarryNamesTimesCostsAndTargets()
    {
        using var match = new Match("Ryze");
        match.Learn(1);
        var spells = GameData.Default.Champion("Ryze").SpellNames;
        TestAssert.Equal(["Overload", "RunePrison", "SpellFlux", "DesperatePower"], spells.ToArray(), "slot names");
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Hero);
        TestAssert.Equal([70.0, 80.0, 60.0, 0.0], snapshot.ManaCosts.ToArray(), "rank-one mana costs from the spell inibins");
        TestAssert.True(snapshot.CanCastBits1 == uint.MaxValue && snapshot.CanCastBits2 == uint.MaxValue, "cast bits are open");
        var expectedCooldown = new[] { 3.5, 14.0, 14.0, 70.0 };
        var expectedCost = new[] { 70.0, 80.0, 60.0, 0.0 };
        var targets = new Entity?[] { match.Foe, match.Foe, match.Foe, null };
        // Overload (CastType 1) and Spell Flux (CastType 2) fly a missile; Rune Prison (0) and Desperate Power (0) do not.
        var missile = new[] { true, false, true, false };
        for (var slot = 0; slot < 4; slot++)
        {
            match.Refill();
            var before = match.Log.Casts.Count;
            TestAssert.True(match.Cast(slot, targets[slot]), $"{spells[slot]} is accepted");
            TestAssert.Equal(before + 1, match.Log.Casts.Count, $"{spells[slot]} announces exactly one cast");
            var cast = match.Log.Casts[^1];
            var data = GameData.Default.Spell(spells[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(SpellSystem.ProtocolHash(spells[slot]), cast.SpellHash, $"{spells[slot]} hash");
            TestAssert.Equal((byte)slot, cast.Slot, $"{spells[slot]} slot");
            TestAssert.Equal(1, cast.Rank, $"{spells[slot]} rank");
            TestAssert.Equal(match.Hero.NetId, cast.CasterNetId, $"{spells[slot]} caster");
            TestAssert.Near(expectedCost[slot], cast.ManaCost, 1e-6, $"{spells[slot]} mana cost");
            TestAssert.Near(expectedCooldown[slot], cast.Cooldown, 1e-6, $"{spells[slot]} cooldown");
            TestAssert.True(cast.DesignerTotalTime > 0.0, $"{spells[slot]} designer total time {cast.DesignerTotalTime}");
            TestAssert.Near(data.CastDelay, cast.DesignerCastTime, 1e-6, $"{spells[slot]} designer cast time is the data's");
            TestAssert.Near(data.TotalTime, cast.DesignerTotalTime, 1e-6, $"{spells[slot]} designer total time is the data's");
            // The cast names one target even for a chain (the client's chain missile asserts getNumTargets() <= 1); the hops
            // (Foe, Ryze, Foe, Ryze, Foe: Ryze stands 300 from the only enemy) follow in the chain-sync packet, which goes out in the
            // missile's name once the client has the missile (one windup after the cast), not with the cast.
            TestAssert.Equal(targets[slot] is null ? 0 : 1, cast.Targets.Count, $"{spells[slot]} target list");
            if (targets[slot] is { } target) TestAssert.Equal(target.NetId, cast.Targets[0].NetId, $"{spells[slot]} names its target");
            TestAssert.Equal(0, match.Log.ChainSyncs.Count(item => item.Missile == cast.MissileNetId), $"{spells[slot]}: no list with the cast");
            TestAssert.Equal(missile[slot], cast.MissileNetId != 0u, $"{spells[slot]} missile");
            match.Advance(1.0);
            var syncs = match.Log.ChainSyncs.Where(item => item.Missile == cast.MissileNetId).ToArray();
            if (slot == 2)
            {
                TestAssert.Equal(2, syncs.Length, "the list goes out twice (BbChainSync)");
                foreach (var sync in syncs)
                    TestAssert.Equal([match.Foe.NetId, match.Hero.NetId, match.Foe.NetId, match.Hero.NetId, match.Foe.NetId],
                        sync.Targets, "the chain's hops in order");
            }
            else TestAssert.Equal(0, syncs.Length, $"{spells[slot]} is not a chain");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Overload
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-q-overload-damages-and-the-passive-trims-the-others")]
    public static void OverloadDamagesByRankAndManaAndArcaneMasteryTrimsTheOtherCooldowns()
    {
        var overload = Script("Overload");
        var damage = ByLevel("Overload", "TargetExecute", "BaseDamage");
        var data = GameData.Default.Spell("Overload", 5);
        TestAssert.Equal([40.0, 65.0, 90.0, 115.0, 140.0], damage, "Overload.luaobj BaseDamage");
        var blocks = Blocks(overload, "TargetExecute").ToArray();
        TestAssert.Near(0.08, blocks.First(block => block.Function == "BBMath" && block.Text("DestVar") == "BonusDamage").Number("Src2Value")!.Value,
            1e-12, "Overload.luaobj maximum-mana ratio");
        var apply = blocks.First(block => block.Function == "BBApplyDamage");
        TestAssert.Near(0.2, apply.Number("SpellDamageRatio")!.Value, 1e-12, "Overload.luaobj ability power ratio");
        TestAssert.Near(0.2, data.Coefficient, 1e-6, "Overload.inibin Coefficient agrees");
        for (var rank = 1; rank <= 5; rank++) TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"Overload.inibin Effect1 rank {rank}");
        TestAssert.Equal([3.5, 3.5, 3.5, 3.5, 3.5], data.Cooldowns.ToArray(), "Overload cooldowns");
        TestAssert.Equal([70.0, 70.0, 70.0, 70.0, 70.0], data.ManaCosts.ToArray(), "Overload mana costs");
        TestAssert.Near(675.0, data.CastRange, 1e-9, "Overload.inibin CastRange (the base entry)");
        for (var rank = 1; rank <= 5; rank++)
            TestAssert.Near(700.0, data.CastRangeAt(rank), 1e-9, $"Overload.inibin CastRange{rank} is what the client reaches at rank {rank}");

        using var match = new Match("Ryze");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var maxMana = match.Hero.MaxMana;
        match.Refill();
        match.Hero.SpellCooldowns[1] = 10.0;
        match.Hero.SpellCooldowns[2] = 6.5;
        match.Hero.SpellCooldowns[3] = 0.5;
        var spellBlock = match.Foe.SpellBlock;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, match.Foe), "Q cast");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press (checked after the flight below).
        TestAssert.Near(mana, match.Hero.Mana, 1e-6, "the press spends nothing");
        // Arcane Mastery (BuffOnSpellCast): every other slot with a cooldown running loses one second; the cast slot is untouched.
        TestAssert.Near(9.0, match.Hero.SpellCooldowns[1], 1e-6, "W cooldown 10 -> 9");
        TestAssert.Near(5.5, match.Hero.SpellCooldowns[2], 1e-6, "E cooldown 6.5 -> 5.5");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 1e-6, "R cooldown 0.5 stops at zero");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-6, "Q's own cooldown waits for its cast frame");
        var flightStart = match.Now;
        match.Advance(0.1);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands before the windup and the flight");
        match.Advance(0.7);
        TestAssert.Near(mana - 70.0, match.Hero.Mana, 1.5, "70 mana, spent at the cast frame (regeneration allowed)");
        TestAssert.Near(3.5, match.Hero.SpellCooldowns[0] + 0.8 - match.CastFrameOf(0), Step + 1e-6, "Q keeps its own 3.5 s from the cast frame");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "the Foe is hit once");
        var raw = damage[2] + 0.08 * maxMana + 0.2 * ap;
        NearF32(raw, hits[0].Raw, "rank 3: 90 + 8% of maximum mana + 0.2 AP");
        NearF32(Mitigated(raw, spellBlock), hits[0].Dealt, "Overload's damage is magic: mitigated by the spell block");
        TestAssert.True(hits[0].Kind == "Spell", "damage source SPELL");
        var arrival = data.CastDelay + 300.0 / 1400.0;
        TestAssert.Near(arrival, hits[0].At - flightStart, 0.1, "the missile lands after the windup plus 300 / 1400");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("overload_tar.troy", StringComparison.OrdinalIgnoreCase)), "the impact effect");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-ryze-q-ranks-add-the-overload-cooldown-reduction")]
    public static void EveryQRankReplacesTheOverloadBuffWithItsCooldownReduction()
    {
        var bonus = ByLevel("Overload", "OnBuffActivate", "CooldownBonus");
        TestAssert.Equal([-0.02, -0.04, -0.06, -0.08, -0.1], bonus, "Overload.luaobj CooldownBonus");
        var data = GameData.Default.Spell("Overload", 5);
        for (var rank = 1; rank <= 5; rank++)
            TestAssert.Near(2.0 * rank, data.EffectNumber(2, rank), 1e-9, $"Overload.inibin Effect2 rank {rank} is the cooldown reduction in percent");

        using var match = new Match("Ryze");
        match.Hero.SkillPoints = 10;
        TestAssert.True(Buff(match.Hero, "Overload") is null, "no Overload buff before the first rank");
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(match.Hero, 0.0, 100000.0), "experience");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 0), $"Q rank {rank}");
            match.Advance(0.1);
            TestAssert.True(Buff(match.Hero, "Overload") is { Type: BuffType.Internal }, $"rank {rank}: CharOnLevelUpSpell added the Overload buff");
            TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "Overload"), "REPLACE_EXISTING keeps one buff");
            TestAssert.Near(bonus[rank - 1], match.Hero.CooldownMod, 1e-9, $"rank {rank}: cooldown modifier");
        }
        match.Hero.SpellRanks[1] = 1;
        match.Refill();
        TestAssert.True(match.Cast(1, match.Foe), "W cast with the reduction");
        // Batch 340: the cooldown starts at the cast frame (CastFrameOf; 0 for an instacast).
        match.Advance(match.CastFrameOf(1));
        TestAssert.Near(14.0 * 0.9, match.Hero.SpellCooldowns[1], Step + 1e-6, "ten percent off the 14 s Rune Prison");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Rune Prison
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-w-rune-prison-roots-and-damages")]
    public static void RunePrisonRootsForItsDurationAndDamages()
    {
        var damage = ByLevel("RunePrison", "TargetExecute", "BaseDamage");
        TestAssert.Equal([60.0, 95.0, 130.0, 165.0, 200.0], damage, "RunePrison.luaobj BaseDamage");
        var buffAdd = Blocks(Script("RunePrison"), "TargetExecute").First(block => block.Function == "BBSpellBuffAdd");
        var duration = Table(buffAdd, "DurationByLevel");
        TestAssert.Equal([1.0, 1.25, 1.5, 1.75, 2.0], duration, "RunePrison.luaobj DurationByLevel");
        var data = GameData.Default.Spell("RunePrison", 5);
        for (var rank = 1; rank <= 5; rank++)
        {
            // Rank one's duration (1) is stored in the inibin's boolean section, which the effect table does not carry.
            if (rank > 1)
                TestAssert.Equal(duration[rank - 1], Amberline.Simulation.Spells.Champions.ChampionSpellSupport.RawNumber(data, $"SpellData*Effect1Level{rank}Amount", -1.0),
                    $"RunePrison.inibin Effect1 rank {rank} is the duration");
            TestAssert.Equal(damage[rank - 1], Amberline.Simulation.Spells.Champions.ChampionSpellSupport.RawNumber(data, $"SpellData*Effect2Level{rank}Amount", -1.0),
                $"RunePrison.inibin Effect2 rank {rank} is the damage");
        }
        TestAssert.Near(0.6, data.Coefficient, 1e-6, "RunePrison.inibin Coefficient");
        TestAssert.Equal([80.0, 95.0, 110.0, 125.0, 140.0], data.ManaCosts.ToArray(), "RunePrison mana costs");
        TestAssert.Near(625.0, data.CastRange, 1e-9, "RunePrison range");

        using var match = new Match("Ryze");
        match.GiveAbilityPower();
        match.Learn(4);
        var ap = match.Hero.AbilityPower;
        var maxMana = match.Hero.MaxMana;
        match.Refill();
        var spellBlock = match.Foe.SpellBlock;
        TestAssert.True(match.CanCast(1, match.Foe).Allowed, "an enemy in range is a legal target");
        TestAssert.True(match.CanCast(1, match.Ally).Failure == SpellCastFailure.Target, "an ally is not (AffectEnemies only)");
        TestAssert.True(match.Cast(1, match.Foe), "W cast");
        var windup = match.Now;
        match.Advance(0.1);
        TestAssert.True(Buff(match.Foe, "RunePrison") is null, "no root during the windup (the effect lands when it ends)");
        match.Advance(0.5);
        var buff = Buff(match.Foe, "RunePrison");
        TestAssert.True(buff is { Type: BuffType.Net }, "the Net buff named after the spell is on the enemy: " + Names(match.Foe));
        TestAssert.True(buff!.Payload.MovementDisabled, "SetCanMove(false) holds the enemy in place");
        TestAssert.Near(windup + 1.75, buff.ExpiresAt, 0.4, "rank 4: 1.75 seconds");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        var raw = damage[3] + 0.05 * maxMana + 0.6 * ap;
        NearF32(raw, hits[0].Raw, "rank 4: 165 + 5% of maximum mana + 0.6 AP");
        NearF32(Mitigated(raw, spellBlock), hits[0].Dealt, "magic damage");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Ryze->Foe", StringComparison.Ordinal)), "assist marker on the enemy");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("RunePrison_tar.troy", StringComparison.OrdinalIgnoreCase)), "the rune effect");
        match.Advance(1.4);
        TestAssert.True(Buff(match.Foe, "RunePrison") is null, "the root ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Spell Flux
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-e-spell-flux-bounces-damages-and-shreds-resistance")]
    public static void SpellFluxHitsEveryHopOnceAndShredsMagicResistance()
    {
        var damage = ByLevel("SpellFlux", "TargetExecute", "Damage");
        var shred = ByLevel("SpellFlux", "TargetExecute", "ResistanceMod");
        TestAssert.Equal([50.0, 70.0, 90.0, 110.0, 130.0], damage, "SpellFlux.luaobj Damage");
        TestAssert.Equal([-12.0, -15.0, -18.0, -21.0, -24.0], shred, "SpellFlux.luaobj ResistanceMod");
        var data = GameData.Default.Spell("SpellFlux", 5);
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"SpellFlux.inibin Effect1 rank {rank}");
            TestAssert.Equal(-shred[rank - 1], data.EffectNumber(2, rank), $"SpellFlux.inibin Effect2 rank {rank} is the resistance shred");
        }
        TestAssert.Near(0.35, data.Coefficient, 1e-6, "SpellFlux.inibin Coefficient");
        TestAssert.Equal([60.0, 75.0, 90.0, 105.0, 120.0], data.ManaCosts.ToArray(), "SpellFlux mana costs");
        TestAssert.Equal([14.0, 14.0, 14.0, 14.0, 14.0], data.Cooldowns.ToArray(), "SpellFlux cooldowns");

        // A lone enemy 300 from Ryze: Foe, Ryze, Foe, Ryze, Foe. The Foe is hit three times, Ryze none (the script skips the caster).
        using var match = new Match("Ryze");
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Hero.AbilityPower;
        match.Refill();
        var spellBlock = match.Foe.SpellBlock;
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        var cast = match.Now;
        match.Advance(0.1);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands during the windup");
        match.Advance(1.6);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "the Foe is hit on hops one, three and five");
        TestAssert.Equal(0, HitsOn(match, "Ryze").Count, "Ryze takes nothing from his own orb");
        var raw = damage[1] + 0.35 * ap;
        foreach (var hit in hits)
        {
            TestAssert.Near(raw, hit.Raw, 1e-6, "rank 2: 70 + 0.35 AP on every hop");
            TestAssert.True(hit.Kind == "SpellAoe", "damage source SPELLAOE");
        }
        // The first hit already meets the shred (the buff is added before the damage): 15 less magic resistance for the first hit.
        TestAssert.Near(Mitigated(raw, spellBlock + shred[1]), hits[0].Dealt, 1e-6, "the first hop is mitigated by the shredded resistance");
        TestAssert.Near(Mitigated(raw, spellBlock + shred[1]), hits[2].Dealt, 1e-6, "so is the last (the shred renews, it does not stack)");
        var leg = 300.0 / 1300.0;
        TestAssert.Near(data.CastDelay + leg, hits[0].At - cast, 0.1, "the orb reaches the first unit after the windup plus 300 / 1300");
        TestAssert.Near(2.0 * leg, hits[1].At - hits[0].At, 0.1, "and the next Foe hit comes after a leg to Ryze and one back");
        TestAssert.Near(2.0 * leg, hits[2].At - hits[1].At, 0.1, "again");
        TestAssert.True(Buff(match.Foe, "SpellFlux") is { Type: BuffType.CombatDehancer }, "the shred buff is named after the spell");
        TestAssert.Near(spellBlock + shred[1], match.Foe.SpellBlock, 1e-6, "spell block -15 for the duration");
        TestAssert.True(match.Log.Effects.Count(line => line.Contains("SpellFlux_tar2.troy", StringComparison.OrdinalIgnoreCase)) >= 5,
            "the hit effect plays on every hop, Ryze's included");
        match.Advance(5.2);
        TestAssert.True(Buff(match.Foe, "SpellFlux") is null, "the shred ends after five seconds");
        TestAssert.Near(spellBlock, match.Foe.SpellBlock, 1e-6, "resistance restored");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-ryze-e-spell-flux-alternates-between-two-enemies-and-survives-a-death")]
    public static void SpellFluxAlternatesAndCarriesOnPastADeadUnit()
    {
        using var match = new Match("Ryze");
        match.Learn(1);
        var minion = Enemy(match, "Minion", new Position2(7500, 7000));
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E cast");
        TestAssert.Equal(1, match.Log.Casts[^1].Targets.Count, "the cast names the first unit only");
        match.Advance(1.2);
        TestAssert.Equal([match.Foe.NetId, minion.NetId, match.Foe.NetId, minion.NetId, match.Foe.NetId],
            match.Log.ChainSyncs[^1].Targets, "Foe, Minion, Foe, Minion, Foe");
        TestAssert.Equal(3, HitsOn(match, "Foe").Count, "the Foe is hit three times");
        TestAssert.Equal(2, HitsOn(match, "Minion").Count, "the minion twice");
        TestAssert.Equal(0, HitsOn(match, "Ryze").Count, "Ryze never");

        // A unit that dies on the way: the orb skips it and goes on to the next hop (the client follows the server's list).
        match.Refill();
        match.Advance(15.0);
        match.Refill();
        var before = HitsOn(match, "Minion").Count;
        TestAssert.True(match.Cast(2, match.Foe), "second E cast");
        match.Advance(0.2);
        minion.Health = 0.0;
        minion.Alive = false;
        match.Advance(1.5);
        TestAssert.Equal(before, HitsOn(match, "Minion").Count, "a dead unit takes no hit");
        TestAssert.Equal(3 + 3, HitsOn(match, "Foe").Count, "the later hops on the Foe still land");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Desperate Power
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-r-desperate-power-gives-spell-vamp-and-widens-the-spells")]
    public static void DesperatePowerAddsSpellVampAndAnAreaToEverySpell()
    {
        var vamp = ByLevel("DesperatePower", "TargetExecute", "Vamp");
        TestAssert.Equal([0.15, 0.2, 0.25], vamp, "DesperatePower.luaobj Vamp");
        var duration = Table(Blocks(Script("DesperatePower"), "TargetExecute").First(block => block.Function == "BBSpellBuffAdd"), "DurationByLevel");
        TestAssert.Equal([5.0, 6.0, 7.0], duration, "DesperatePower.luaobj DurationByLevel");
        var data = GameData.Default.Spell("DesperatePower", 3);
        for (var rank = 1; rank <= 3; rank++)
        {
            TestAssert.Equal(vamp[rank - 1] * 100.0, data.EffectNumber(2, rank), $"DesperatePower.inibin Effect2 rank {rank}");
            TestAssert.Equal(duration[rank - 1], data.EffectNumber(3, rank), $"DesperatePower.inibin Effect3 rank {rank}");
        }
        TestAssert.Equal([70.0, 60.0, 50.0], data.Cooldowns.ToArray(), "DesperatePower cooldowns");

        using var match = new Match("Ryze");
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var maxMana = match.Hero.MaxMana;
        var minion = Enemy(match, "Minion", new Position2(7450, 7000));
        Enemy(match, "FarMinion", new Position2(7300, 7700));
        match.Refill();
        TestAssert.True(match.Cast(3), "R cast");
        var buff = Buff(match.Hero, "DesperatePower");
        TestAssert.True(buff is { Type: BuffType.CombatEnhancer }, "the buff is on Ryze: " + Names(match.Hero));
        TestAssert.Near(match.Now + 7.0, buff!.ExpiresAt, 0.4, "rank 3: seven seconds");
        TestAssert.Near(0.25, match.Hero.SpellVamp, 1e-9, "rank 3: 25 percent spell vamp");
        TestAssert.Near(50.0, match.Hero.SpellCooldowns[3], 1e-6, "rank 3 cooldown 50 s");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("ManaLeach_tar2.troy", StringComparison.OrdinalIgnoreCase)), "the aura effect on Ryze");

        // Q with the buff: the target takes the full hit, every other enemy within 300 half of the base (+ 0.1 AP), none beyond.
        match.Hero.Health = 40.0;
        var hero = match.Hero.Health;
        TestAssert.True(match.Cast(0, match.Foe), "Q during Desperate Power");
        match.Advance(0.8);
        var total = 90.0 + 0.08 * maxMana;
        var foeHit = HitsOn(match, "Foe").Single();
        NearF32(total + 0.2 * ap, foeHit.Raw, "the target takes the full Overload damage");
        var splash = HitsOn(match, "Minion").Single();
        NearF32(0.5 * total + 0.1 * ap, splash.Raw, "the minion within 300 takes half of the base damage + 0.1 AP");
        TestAssert.True(splash.Kind == "SpellAoe", "the splash is area damage");
        TestAssert.Equal(0, HitsOn(match, "FarMinion").Count + HitsOn(match, "Ally").Count, "units beyond 300 and friends are untouched");
        TestAssert.True(match.Hero.Health > hero, "spell vamp heals Ryze for the damage he dealt");
        var expected = 0.25 * foeHit.Dealt + 0.25 * splash.Dealt * 0.333;
        TestAssert.Near(Math.Min(match.Hero.MaxHealth, hero + expected), match.Hero.Health, 1.5, "25 percent of the target's damage plus a third of that of the splash");

        // The buff ends after its seven seconds and the splash goes with it.
        match.Advance(7.2);
        TestAssert.True(Buff(match.Hero, "DesperatePower") is null, "the buff ends");
        TestAssert.Near(0.0, match.Hero.SpellVamp, 1e-9, "spell vamp gone");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-ryze-desperate-power-adds-splash-to-rune-prison-and-spell-flux")]
    public static void DesperatePowerSplashesRunePrisonAndEveryFluxHop()
    {
        using var match = new Match("Ryze");
        match.GiveAbilityPower();
        match.Learn(1);
        var ap = match.Hero.AbilityPower;
        var maxMana = match.Hero.MaxMana;
        var minion = Enemy(match, "Minion", new Position2(7500, 7000));
        match.Refill();
        TestAssert.True(match.Cast(3), "R cast");

        TestAssert.True(match.Cast(1, match.Foe), "W during Desperate Power");
        match.Advance(0.8);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Take(2).Select(f => f.Message)));
        NearF32(60.0 + 0.05 * maxMana + 0.6 * ap, HitsOn(match, "Foe").Single().Raw, "Rune Prison's own damage");
        var splash = HitsOn(match, "Minion").Single();
        NearF32(0.5 * (60.0 + 0.05 * maxMana) + 0.3 * ap, splash.Raw, "the splash is half of it + 0.3 AP");
        TestAssert.True(Buff(match.Hero, "DesperatePower") is not null, "still on");

        // Spell Flux: Foe, Minion, Foe, Minion, Foe, each hop splashing the other unit (half of the 50 + 0.175 AP).
        match.Refill();
        match.Hero.SpellCooldowns[3] = 99.0;
        var foeBefore = HitsOn(match, "Foe").Count;
        var minionBefore = HitsOn(match, "Minion").Count;
        TestAssert.True(match.Cast(2, match.Foe), "E during Desperate Power");
        match.Advance(1.3);
        var foe = HitsOn(match, "Foe").Skip(foeBefore).ToList();
        var other = HitsOn(match, "Minion").Skip(minionBefore).ToList();
        TestAssert.Equal(3 + 2, foe.Count, "the Foe: three hops and two splashes from the Minion's hops");
        TestAssert.Equal(2 + 3, other.Count, "the Minion: two hops and three splashes from the Foe's hops");
        TestAssert.Equal(2, foe.Count(hit => hit.Kind == "SpellAoe" && Math.Abs(hit.Raw - (0.5 * 50.0 + 0.175 * ap)) < 1e-6),
            "the splashes are half of the 50 + 0.175 AP");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passives: tooltips, mana per ultimate rank
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-passive-publishes-tooltips-and-r-ranks-add-mana")]
    public static void CharacterScriptWritesTooltipsAndUltimateRanksAddMana()
    {
        using var match = new Match("Ryze");
        TestAssert.True(Buff(match.Hero, "ArcaneMastery") is { Type: BuffType.Aura }, "CharOnActivate added Arcane Mastery");
        // UpdateSelfBuffActions: once a second, the Overload and Rune Prison tooltip values from the maximum mana.
        match.Advance(0.2);
        var maxMana = match.Hero.MaxMana;
        TestAssert.True(match.Hero.ScriptSpellTooltips.ContainsKey(0) && match.Hero.ScriptSpellTooltips.ContainsKey(1),
            "the character script wrote tooltip variables for the slots 0 and 1");
        var overload = match.Hero.ScriptSpellTooltips[0];
        var prison = match.Hero.ScriptSpellTooltips[1];
        TestAssert.Near(0.08 * maxMana, overload[0], 1e-6, "Overload: 8% of maximum mana");
        TestAssert.Near(0.05 * maxMana, prison[0], 1e-6, "Rune Prison: 5% of maximum mana");

        // CharOnLevelUpSpell: every rank of the ultimate adds 75 maximum mana.
        match.Hero.SkillPoints = 10;
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(match.Hero, 0.0, 100000.0), "experience");
        var mana = match.Hero.MaxMana;
        for (var rank = 1; rank <= 3; rank++)
        {
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), $"R rank {rank}");
            match.Advance(0.1);
            TestAssert.Near(mana + 75.0 * rank, match.Hero.MaxMana, 1e-6, $"rank {rank}: +75 maximum mana");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Registration, ranks and production safety
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-production-installs-the-sink-and-his-passives")]
    public static void RyzeJoinsTheMatchThroughTheSharedScriptSink()
    {
        using var match = new Match("Ryze");
        TestAssert.True(((Amberline.Simulation.Runtime.MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks is not null,
            "a Ryze in the roster installs the sink when his hero is created");
        TestAssert.True(match.Module.IsBound, "the module is bound to the match");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "ChampionChampionDelta"),
            "the shared system buffs are applied once, natively");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "batch 203: the champion's shipped CharOnActivate add is the one tower buff; nothing refuses it any more");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-ryze-ranks-levels-and-cooldowns")]
    public static void RankGatesLevelUpsAndCooldownsFollowTheData()
    {
        using var match = new Match("Ryze");
        var ryze = match.Hero;
        ryze.SkillPoints = 20;
        TestAssert.True(match.Runtime.Spells.RankUp(ryze, 0), "Q rank 1 at level 1");
        TestAssert.False(match.Runtime.Spells.RankUp(ryze, 0), "Q rank 2 needs level 3");
        TestAssert.False(match.Runtime.Spells.RankUp(ryze, 3), "R needs level 6");
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(ryze, 0.0, 100000.0), "experience");
        TestAssert.Equal(18, ryze.Level, "enough experience reaches level 18");
        for (var slot = 0; slot < 4; slot++)
            while (match.Runtime.Spells.RankUp(ryze, slot)) { }
        TestAssert.Equal([5, 5, 5, 3], ryze.SpellRanks.ToArray(), "all ranks reachable at level 18");

        var cooldowns = new[] { new[] { 3.5, 3.5, 3.5, 3.5, 3.5 }, new[] { 14.0, 14, 14, 14, 14 }, new[] { 14.0, 14, 14, 14, 14 }, new[] { 70.0, 60, 50 } };
        var manaCosts = new[] { new[] { 70.0, 70, 70, 70, 70 }, new[] { 80.0, 95, 110, 125, 140 }, new[] { 60.0, 75, 90, 105, 120 }, new[] { 0.0, 0, 0 } };
        for (var slot = 0; slot < 4; slot++)
        {
            var data = GameData.Default.Spell(GameData.Default.Champion("Ryze").SpellNames[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(cooldowns[slot], data.Cooldowns.ToArray(), $"slot {slot} cooldowns");
            TestAssert.Equal(manaCosts[slot], data.ManaCosts.ToArray(), $"slot {slot} mana costs");
        }
        Entity?[] targets = [match.Foe, match.Foe, match.Foe, null];
        foreach (var slot in new[] { 1, 3 })
        {
            ryze.SpellRanks[slot] = slot == 3 ? 2 : 4;
            match.Refill();
            ryze.CooldownMod = -0.1;
            ryze.Position = new Position2(7000, 7000);
            TestAssert.True(match.Cast(slot, targets[slot]), $"slot {slot} cast");
            // Batch 340: a windup's cooldown starts at its cast frame (CastFrameOf; 0 for an instacast).
            match.Advance(match.CastFrameOf(slot));
            TestAssert.Near(cooldowns[slot][ryze.SpellRanks[slot] - 1] * 0.9, ryze.SpellCooldowns[slot], Step + 1e-6, $"slot {slot} cooldown with 10% reduction");
            match.Advance(0.5);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ryze bots in a headless match
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-ryze-bots-play-a-six-minute-headless-match", Serial = true)]
    public static void RyzeBotsPlayWithoutScriptExceptionsOrPacketViolations()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-ryze-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] roster = ["Ryze", "Annie", "Janna", "Ezreal", "Vayne", "Ryze", "Orianna", "Blitzcrank", "Nidalee", "Shaco"];
            var casts = new Dictionary<(uint Hero, int Slot), int>();
            var previous = new Dictionary<(uint Hero, int Slot), double>();
            var failures = new List<string>();
            var options = new Amberline.Tools.Diagnostics.HeadlessMatchOptions(20261016, Path.Combine(root, "m"), 6.0, roster)
            {
                OnStep = runtime =>
                {
                    foreach (var hero in runtime.World.Heroes().Where(hero => hero.ChampionName == "Ryze"))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            var key = (hero.NetId, slot);
                            var now = hero.SpellCooldowns[slot];
                            if (previous.TryGetValue(key, out var before) && now > before + 0.5) casts[key] = casts.GetValueOrDefault(key) + 1;
                            previous[key] = now;
                        }
                    }
                    var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Ryze")!;
                    foreach (var failure in module.ScriptFailures.Skip(failures.Count))
                        failures.Add($"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}");
                },
            };
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(options);
            TestAssert.Equal("cap", report.Outcome, "six game minutes end at the cap, not in a crash");
            TestAssert.Equal(0L, report.PacketViolations, "the packet-stream checker finds no violation");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
            TestAssert.Equal(0, failures.Count, "no script exception: " + string.Join(" | ", failures.Take(3)));
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            TestAssert.False(log.Contains("script.exception", StringComparison.Ordinal), "no script.exception line in the session log");
            TestAssert.True(casts.Values.Sum() > 0, "the Ryze bots cast their spells: " +
                string.Join(", ", casts.OrderBy(pair => pair.Key.Slot).Select(pair => $"slot {pair.Key.Slot} x{pair.Value}")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
