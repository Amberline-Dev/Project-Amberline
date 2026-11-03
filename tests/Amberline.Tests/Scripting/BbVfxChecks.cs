using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbAnnieDifferentialChecks;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// 16 October 2026 playtest, effects and animations of the five script-driven champions (Soraka, Ryze, Taric, Kayle, Sivir).
/// Every expectation is read from the shipped files (spell inibins, script tables, <c>DATA/Particles/troy.filelist</c>) or from the
/// stock client's code, and pinned a second time as the literal the two agree on.
/// </summary>
public static class BbVfxChecks
{
    private sealed class Wire : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    /// <summary>A packet tap for the hero's own seat of a <see cref="Match"/>: what its client would receive.</summary>
    private static Wire Tap(Match match)
    {
        var wire = new Wire();
        var participant = match.Runtime.Session.Participants[0];
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(1, match.Hero.Team, match.Hero.NetId, wire, ready: true,
            view: participant.View));
        match.Runtime.Delivery.Flush();
        wire.Packets.Clear();
        return wire;
    }

    private static void Run(Match match, Wire? wire, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step)
        {
            match.Runtime.Tick(Step);
            if (wire is not null) match.Runtime.Delivery.Flush();
        }
    }

    private static uint U32(byte[] packet, int offset) => BitConverter.ToUInt32(packet, offset);

    /// <summary>The first effect of a 0x8C packet: name hash, flags, bind bone hash, target unit, effect id, bind unit.</summary>
    private static (uint Hash, ushort Flags, uint Bone, uint Target, uint Id, uint Bind) Fx(byte[] packet) =>
        (U32(packet, 6), BitConverter.ToUInt16(packet, 10), U32(packet, 16), U32(packet, 21), U32(packet, 25), U32(packet, 29));

    private static List<byte[]> Packets(Wire wire, byte opcode) =>
        wire.Packets.Where(packet => packet.Plaintext.Span[0] == opcode).Select(packet => packet.Plaintext.ToArray()).ToList();

    private static string Stem(string name) => name.Contains('.') ? name[..name.LastIndexOf('.')] : name;

    /// <summary>The client's particle names, read independently of the engine: the NUL separated list the client's listing helper loads.</summary>
    private static HashSet<string> ClientParticles()
    {
        var list = File.ReadAllBytes(GameData.Default.Paths.Data("Particles/troy.filelist"));
        return System.Text.Encoding.Latin1.GetString(list).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Stem(name).ToLowerInvariant()).ToHashSet();
    }

    private static List<Trace.EffectCall> Calls(Match match, string name) =>
        match.Log.EffectCalls.Where(call => string.Equals(call.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    // ------------------------------------------------------------------------------------------------------------
    // The default hit pass (Soraka Q, Taric Q, Kayle Q/W, Taric E)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vfx-default-hit-effects-follow-the-spell-inibin")]
    public static void SpellsDrawTheirInibinHitAndAfterEffects()
    {
        // The shipped fields (SpellData HaveHitEffect/HitEffectName/HitBoneName/HaveAfterEffect/AfterEffectName).
        var data = GameData.Default;
        TestAssert.Equal(("Starcall_tar.troy", (string?)null), data.Spell("Starcall", 5).DefaultHitEffect!.Value, "Starcall.inibin hit effect");
        TestAssert.True(data.Spell("Starcall", 5).DefaultAfterEffect is null, "Starcall's after effect is switched off (HaveAfterEffect 0)");
        TestAssert.Equal("Global_Heal.troy", data.Spell("Imbue", 5).DefaultAfterEffect, "Imbue.inibin after effect");
        TestAssert.True(data.Spell("Imbue", 5).DefaultHitEffect is null, "Imbue's hit effect is switched off");
        TestAssert.Equal(("Reckoning_tar.troy", (string?)"head"), data.Spell("JudicatorReckoning", 5).DefaultHitEffect!.Value, "Reckoning hits the head bone");
        TestAssert.True(data.Spell("Infuse", 5).DefaultHitEffect is null && data.Spell("Wish", 5).DefaultHitEffect is null,
            "Infuse and Wish keep their effects in the script (HaveHitEffect 0)");

        // Soraka Q: a falling star on every enemy unit in range, none on friends or on the far hero.
        using (var match = new Match("Soraka"))
        {
            match.Learn(3);
            match.Refill();
            var minion = Enemy(match, "Minion", new Position2(7400, 7000));
            TestAssert.True(match.Cast(0), "Starcall cast");
            match.Advance(1.0);
            var stars = Calls(match, "Starcall_tar.troy");
            TestAssert.Equal(2, stars.Count, "one star per enemy unit in range (Foe and the minion)");
            TestAssert.Equal(new[] { match.Foe.NetId, minion.NetId }.OrderBy(id => id).ToArray(),
                stars.Select(call => call.Bind).OrderBy(id => id).ToArray(), "bound to each victim");
            // Batch 582: the pass places its hit effect as the client's own hit pass does (flags 8: the victim as bind, not attached, no target, no bone).
            TestAssert.True(stars.All(call => call.Target == 0 && call.Bone is null && call.Height == 0.0), "no target object, no bone, on the ground");
            TestAssert.Equal(0, Calls(match, "Starcall_tar2.troy").Count, "the switched-off after effect is not drawn");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
        }

        // Soraka W and E: Astral Blessing's heal flash comes from the pass, Infuse's enemy effect from its script only.
        using (var match = new Match("Soraka"))
        {
            match.Learn(3);
            match.Refill();
            TestAssert.True(match.Cast(1, match.Ally), "Astral Blessing on the ally");
            match.Advance(1.0);
            TestAssert.Equal(1, Calls(match, "Global_Heal.troy").Count(call => call.Bind == match.Ally.NetId), "the heal flash on the ally");
            match.Refill();
            TestAssert.True(match.Cast(2, match.Foe), "Infuse on the enemy");
            match.Advance(1.0);
            TestAssert.Equal(1, Calls(match, "soraka_infuse_enemy_tar.troy").Count, "the script's own effect, once");
            TestAssert.Equal(0, Calls(match, "Infuse_tar.troy").Count, "the inibin's name is switched off");
        }

        // Taric Q: the script flashes the heal on Taric, the after pass on the target; a self-cast gets the one flash.
        using (var match = new Match("Taric"))
        {
            match.Learn(1);
            match.Refill();
            match.Ally.Health = 100;
            TestAssert.True(match.Cast(0, match.Ally), "Imbue on the ally");
            match.Advance(1.0);
            var onAlly = Calls(match, "Global_Heal.troy").Where(call => call.Bind == match.Ally.NetId).ToList();
            var onHero = Calls(match, "Global_Heal.troy").Where(call => call.Bind == match.Hero.NetId).ToList();
            TestAssert.Equal(1, onAlly.Count, "the target shows the heal (AfterEffectName of Imbue.inibin): " + string.Join(" | ", match.Log.Effects));
            TestAssert.Equal(1, onHero.Count, "Taric shows the script's own flash");
            match.Refill();
            match.Log.EffectCalls.Clear();
            TestAssert.True(match.Cast(0, match.Hero), "Imbue on himself");
            match.Advance(1.0);
            TestAssert.Equal(1, Calls(match, "Global_Heal.troy").Count, "one flash on a self-cast, not two");
            match.Refill();
            match.Log.EffectCalls.Clear();
            TestAssert.True(match.Cast(2, match.Foe), "Dazzle");
            match.Advance(1.0);
            TestAssert.Equal(1, Calls(match, "Dazzle_tar.troy").Count(call => call.Bind == match.Foe.NetId), "Dazzle_tar on the stunned unit");
        }

        // Kayle Q on the head bone, W on the ally; Ryze's script already draws Overload_tar, so it is not drawn twice.
        using (var match = new Match("Kayle"))
        {
            match.Learn(3);
            match.Refill();
            TestAssert.True(match.Cast(0, match.Foe), "Reckoning");
            match.Advance(1.5);
            var reckoning = Calls(match, "Reckoning_tar.troy");
            TestAssert.Equal(1, reckoning.Count, "Reckoning_tar once");
            TestAssert.Equal(match.Foe.NetId, reckoning[0].Bind, "on the target");
            // Batch 582: at the head bone's height (HitBoneName), placed in the world as the client's own hit pass places it, not bound to the joint.
            TestAssert.True(reckoning[0].Bone is null, "no bone on the wire");
            TestAssert.Near(GameData.Default.JointHeight("Orianna", 0, "head") ?? double.NaN, reckoning[0].Height ?? double.NaN, 1e-6,
                "at Orianna's head height (her bind pose)");
            TestAssert.True(reckoning[0].Height > 100.0, $"well above her feet (got {reckoning[0].Height:0.#})");
            match.Refill();
            TestAssert.True(match.Cast(1, match.Ally), "Divine Blessing");
            match.Advance(0.5);
            TestAssert.Equal(1, Calls(match, "Intervention_tar.troy").Count(call => call.Bind == match.Ally.NetId), "Intervention_tar on the ally");
        }
        using (var match = new Match("Ryze"))
        {
            match.Learn(3);
            match.Refill();
            TestAssert.True(match.Cast(0, match.Foe), "Overload");
            match.Advance(1.5);
            TestAssert.Equal(1, Calls(match, "Overload_tar.troy").Count(call => call.Bind == match.Foe.NetId),
                "the script's Overload_tar and the pass's are one effect, not two");
        }
    }

    [Check("scripting.bb-vfx-starcall-falling-star-reaches-the-wire-bound-to-each-enemy")]
    public static void StarcallStarIsAnEffectPacketOnTheEnemy()
    {
        using var match = new Match("Soraka");
        match.Learn(3);
        match.Refill();
        var wire = Tap(match);
        TestAssert.True(match.Cast(0), "Starcall cast");
        Run(match, wire, 1.0);
        var stars = Packets(wire, 0x8C).Select(Fx).Where(fx => fx.Hash == SpellPackets.BuffNameHash("Starcall_tar")).ToList();
        TestAssert.Equal(1, stars.Count, "one 0x8C Starcall_tar for the one enemy in range");
        // Batch 582: flags 8 (the client's own hit pass places a hit effect that way: not attached to the bind unit, no target).
        TestAssert.Equal((match.Foe.NetId, 0u, 0u, (ushort)8), (stars[0].Bind, stars[0].Target, stars[0].Bone, stars[0].Flags),
            "the enemy as its bind, no target, no bone (HitBoneName is empty), flags 8");
    }

    [Check("scripting.bb-vfx-kayle-e-attacks-hit-at-the-cast-frame-and-show-the-nova")]
    public static void RighteousFuryAttackIsInstantAndDrawsItsNova()
    {
        var attack = GameData.Default.Spell("JudicatorRighteousFuryAttack", 5);
        // SpellData CastType 0 (the client flies a missile only for 1, 2 and 3): the 780 missile speed is unused data.
        TestAssert.Equal(0, BbChampionModule.CastType(attack), "JudicatorRighteousFuryAttack.inibin CastType");
        TestAssert.Near(779.9, attack.MissileSpeed, 1e-3, "the unused missile speed in the data");
        TestAssert.Equal(("RighteousFury_nova.troy", (string?)null), attack.DefaultHitEffect!.Value, "hit effect");
        TestAssert.Equal("globalhit_physical.troy", attack.DefaultAfterEffect, "after effect");

        using var match = new Match("Kayle");
        match.Learn(3);
        match.Foe.Position = new Position2(7400, 7000);
        var splashed = Enemy(match, "Splashed", new Position2(7400, 7200));
        match.Refill();
        TestAssert.True(match.Cast(2), "E cast");
        TestAssert.Near(0.0, match.Hero.MissileSpeed, 1e-9, "no flight: the hit lands at the cast frame");
        var swing = match.Now;
        match.Log.EffectCalls.Clear();
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the attack starts");
        match.Advance(1.2);
        var basic = HitsOn(match, "Foe").Single(item => item.Kind == "Basic");
        TestAssert.True(basic.At - swing < 0.6, $"the hit lands within the windup, not after a flight (at +{basic.At - swing:0.###} s)");
        // W23 (19 October 2026): the swing leaves as an auto-attack cast of the override spell, and the client draws the nova and the after flash
        // itself; the server no longer draws them (they showed twice, or never, as a 0x0D).
        TestAssert.True(match.Log.Casts.Any(item => item.Flags == 1 && item.Targets.Count == 1 && item.MissileNetId != 0),
            "the swing is an auto-attack cast of JudicatorRighteousFuryAttack");
        TestAssert.Equal(0, Calls(match, "RighteousFury_nova.troy").Count, "the nova is the client's, not the server's");
        TestAssert.Equal(0, Calls(match, "globalhit_physical.troy").Count(call => call.Bind == match.Foe.NetId), "so is the after flash");
        TestAssert.Equal(0, match.Log.EffectCalls.Count(call => call.Bind == splashed.NetId), "nothing on the unit beside it");
        // Holy Fervor lands with the hit, in the same step (CharOnHitUnit), not after a flight.
        var stack = match.Log.Buffs.Single(line => line.Contains("JudicatorHolyFervorDebuff", StringComparison.Ordinal) && line.Contains(" Foe ", StringComparison.Ordinal));
        TestAssert.Near(basic.At, double.Parse(stack.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), Step + 1e-6,
            "the passive's armor shred is added when the hit lands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Script effects follow their buffs (Taric)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vfx-radiance-effects-end-with-their-buffs-and-sit-on-their-holder")]
    public static void RadianceEffectsAreRemovedAndTargetTheirHolder()
    {
        using var match = new Match("Taric");
        match.Learn(1);
        match.Refill();
        var wire = Tap(match);
        TestAssert.True(match.Cast(3), "Command Power");
        Run(match, wire, 1.0);
        var aura = Calls(match, "Taric_GemStorm_Aura.troy");
        TestAssert.Equal(1, aura.Count, "Radiance's aura on the allied champion, once");
        TestAssert.Equal(match.Ally.NetId, aura[0].Bind, "bound to the ally");
        TestAssert.Equal(match.Ally.NetId, aura[0].Target, "the buff holder is the target object (Target in a buff hook)");
        var gem = Calls(match, "taricgemstorm.troy");
        TestAssert.Equal(1, gem.Count, "Radiance's own effect");
        TestAssert.Equal((match.Hero.NetId, match.Hero.NetId), (gem[0].Bind, gem[0].Target), "on Taric, target object Taric");
        TestAssert.Equal(0, Calls(match, "Taric_HammerInternal.troy").Count, "the effect the client has no particle for is not sent");
        // The 0x8C packets: the aura names its holder as bind and target object, Radiance's own effect is Taric's, no HammerInternal hash.
        var creates = Packets(wire, 0x8C).Select(Fx).ToList();
        var auraFx = creates.Single(fx => fx.Hash == SpellPackets.BuffNameHash("Taric_GemStorm_Aura"));
        TestAssert.Equal((match.Ally.NetId, match.Ally.NetId, 0u, (ushort)0), (auraFx.Bind, auraFx.Target, auraFx.Bone, auraFx.Flags),
            "0x8C Taric_GemStorm_Aura: bind and target object are the ally, no bone, no flags");
        var gemFx = creates.Single(fx => fx.Hash == SpellPackets.BuffNameHash("taricgemstorm"));
        TestAssert.Equal((match.Hero.NetId, match.Hero.NetId), (gemFx.Bind, gemFx.Target), "0x8C taricgemstorm: Taric binds and targets himself");
        TestAssert.False(creates.Any(fx => fx.Hash == SpellPackets.BuffNameHash("Taric_HammerInternal")),
            "no 0x8C for the hash the client logs 'Match not found' for (0560EECC)");
        TestAssert.Equal(0x0560EECCu, SpellPackets.BuffNameHash("Taric_HammerInternal"), "that hash is Taric_HammerInternal (owner's r3dlog)");
        // The aura buff is renewed every second for ten seconds: its id must survive every renewal.
        Run(match, wire, 13.0);
        var kills = Packets(wire, 0x3D).Select(packet => U32(packet, 5)).ToHashSet();
        TestAssert.True(kills.Contains(auraFx.Id) && kills.Contains(gemFx.Id), "0x3D ends the aura and Radiance's effect on the wire");
        TestAssert.True(Buff(match.Ally, "RadianceAura") is null && Buff(match.Hero, "Radiance") is null, "both buffs ended");
        foreach (var call in aura.Concat(gem))
            TestAssert.True(match.Log.Killed.Contains(call.Id), $"{call.Name} (id {call.Id:X}) is removed when its buff ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-vfx-shatter-ready-sparks-never-pile-up")]
    public static void ShatterReadySparksAreRemovedWhenTheBuffGoes()
    {
        using var match = new Match("Taric");
        match.Learn(1);
        match.Refill();
        void AssertAtMostOneLive(string when)
        {
            var live = Calls(match, "ShatterReady_buf.troy").Count(call => !match.Log.Killed.Contains(call.Id));
            TestAssert.True(live <= 1, $"{when}: {live} ShatterReady_buf effects are alive");
        }
        match.Advance(2.0);
        AssertAtMostOneLive("after the passive's first update");
        TestAssert.Equal(1, Calls(match, "ShatterReady_buf.troy").Count(call => !match.Log.Killed.Contains(call.Id)), "ready: one spark effect");
        for (var round = 0; round < 3; round++)
        {
            TestAssert.True(match.Cast(1), $"Shatter {round + 1}");
            match.Advance(0.5);
            TestAssert.Equal(0, Calls(match, "ShatterReady_buf.troy").Count(call => !match.Log.Killed.Contains(call.Id)), "casting ends the sparks");
            match.Advance(11.0);
            AssertAtMostOneLive($"round {round + 1} after the cooldown");
            TestAssert.Equal(1, Calls(match, "ShatterReady_buf.troy").Count(call => !match.Log.Killed.Contains(call.Id)), "ready again: one effect");
        }
    }

    [Check("scripting.bb-vfx-character-script-periodic-clock-persists")]
    public static void CharacterScriptPeriodicRunsOncePerSecond()
    {
        // UpdateSelfBuffActions tracks its BBExecutePeriodically clock in InstanceVars (Taric, Kayle, Sivir, Ryze).
        foreach (var champion in new[] { "Taric", "Kayle", "Sivir", "Ryze" })
            TestAssert.Equal("InstanceVars", Blocks(CharScript(champion), "UpdateSelfBuffActions")
                .First(block => block.Function == "BBExecutePeriodically").Text("TrackTimeVarTable"), $"CharScript{champion}.luaobj clock table");
        using var match = new Match("Taric");
        match.Learn(1);
        match.Refill();
        match.Advance(5.0);
        var auras = match.Log.Buffs.Count(line => line.Contains(" Taric ShatterAura ", StringComparison.Ordinal));
        var bonus = match.Log.Buffs.Count(line => line.Contains(" ShatterSelfBonus ", StringComparison.Ordinal));
        TestAssert.True(auras >= 2 && auras <= 8, $"the aura is renewed once per second, not per tick ({auras} adds in 5 s)");
        TestAssert.True(bonus <= 8, $"the ready bonus likewise ({bonus} adds in 5 s)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Buff slots (Soraka)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vfx-a-buff-added-while-another-is-removed-never-shares-its-slot")]
    public static void StarcallSlotIsNotTheSlotOfTheBuffThatGaveIt()
    {
        using var match = new Match("Soraka");
        match.Learn(3);
        match.Refill();
        var wire = Tap(match);
        TestAssert.True(match.Cast(0), "Starcall");
        Run(match, wire, 1.0);
        var damageHash = SpellPackets.BuffNameHash("StarcallDamage");
        var starcallHash = SpellPackets.BuffNameHash("Starcall");
        var adds = wire.Packets.Where(packet => packet.Plaintext.Span[0] == 0xBF &&
            BitConverter.ToUInt32(packet.Plaintext.Span[1..5]) == match.Foe.NetId).Select(packet => packet.Plaintext.ToArray()).ToList();
        var removes = wire.Packets.Where(packet => packet.Plaintext.Span[0] == 0x7F &&
            BitConverter.ToUInt32(packet.Plaintext.Span[1..5]) == match.Foe.NetId).Select(packet => packet.Plaintext.ToArray()).ToList();
        var starcall = adds.Single(packet => BitConverter.ToUInt32(packet, 9) == starcallHash);
        var damageRemoval = removes.Single(packet => BitConverter.ToUInt32(packet, 6) == damageHash);
        TestAssert.True(starcall[5] != damageRemoval[5],
            $"the client removes the buff in the slot of a RemoveBuff whatever its name: Starcall in slot {starcall[5]}, StarcallDamage removed from slot {damageRemoval[5]}");
        // The removal still names the slot the damage buff held (it was added first, slot 0).
        var damageAdd = adds.Single(packet => BitConverter.ToUInt32(packet, 9) == damageHash);
        TestAssert.Equal(damageAdd[5], damageRemoval[5], "the removal names the slot StarcallDamage was added in");
        TestAssert.True(Buff(match.Foe, "Starcall") is { Stacks: 1 }, "one Starcall stack on the unit");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hero stop-attack, Sivir Q
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vfx-a-cancelled-hero-swing-does-not-force-its-cast-frame")]
    public static void HeroStopAttackDoesNotForceTheSpellCast()
    {
        // the instant stop-attack routine: bit 0x02 (ForceSpellCast) executes the outstanding cast frame once, which
        // draws the hit particles and plays the hit sound on the target of an attack that never lands.
        var queue = new CaptureQueue();
        var grid = NavigationGrid.LoadDefault();
        var world = new WorldState();
        var sink = new ProtocolSpellEffectSink(queue, new ReplicationSyncCounter(),
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ), (x, z) => 0.0, world.Allocate, _ => { });
        var hero = new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(100, 100));
        var minion = new Entity(world.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos, new Position2(200, 100)) { Created = true };
        // The swings were announced (0x0D) and the server dropped them before their hit; a unit whose swing the server did not
        // drop (the hit stands) is not told to destroy the shot, and a unit that never attacked is not stopped at all.
        hero.ClientAttackArmed = minion.ClientAttackArmed = true;
        hero.ClientSwingDropped = minion.ClientSwingDropped = true;
        sink.SendStopAttack(hero);
        sink.SendStopAttack(minion);
        TestAssert.Equal(2, queue.Packets.Count, "two stop packets");
        TestAssert.Equal(0x10, (int)queue.Packets[0].Plaintext.Span[5], "a hero's cancelled swing: DestroyMissile only");
        TestAssert.Equal(0x10, (int)queue.Packets[1].Plaintext.Span[5], "so is every other unit's: never ForceSpellCast");
        queue.Packets.Clear();
        hero.ClientAttackArmed = minion.ClientAttackArmed = true;
        sink.SendStopAttack(hero);
        sink.SendStopAttack(minion);
        sink.SendStopAttack(minion);
        TestAssert.Equal(2, queue.Packets.Count, "the minion is not stopped a second time");
        TestAssert.Equal(0x00, (int)queue.Packets[0].Plaintext.Span[5], "a hero's hit that stands, current SpellCaster unproven: nothing destroyed, nothing forced");
        TestAssert.Equal(0x00, (int)queue.Packets[1].Plaintext.Span[5], "so is every other unit's");
        queue.Packets.Clear();
        hero.ClientAttackArmed = minion.ClientAttackArmed = true;
        hero.ClientSwingMissileNetId = minion.ClientSwingMissileNetId = 0x40020001;
        sink.SendStopAttack(hero);
        sink.SendStopAttack(minion);
        TestAssert.Equal(0x02, (int)queue.Packets[0].Plaintext.Span[5], "a hero's standing swing that is the current SpellCaster: its cast frame is forced");
        TestAssert.Equal(0x02, (int)queue.Packets[1].Plaintext.Span[5], "so is every other unit's");
    }

    [Check("scripting.bb-vfx-sivir-q-cancels-the-swing-so-its-animation-plays")]
    public static void SpiralBladeStopsAnAttackInProgress()
    {
        var spiral = GameData.Default.Spell("SpiralBlade", 5);
        TestAssert.True(spiral.IsInstant, "SpiralBlade.inibin Flags has the Instacast bit (327684 = 0x50004)");
        using var match = new Match("Sivir");
        match.Learn(3);
        match.Foe.Position = new Position2(7300, 7000);
        match.Refill();
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the swing starts");
        match.Advance(0.1);
        match.Hero.AttackTarget = match.Foe.NetId;
        match.Log.StopAttacks.Clear();
        var castsBefore = match.Log.Casts.Count;
        TestAssert.True(match.Cast(0, null, at: new Position2(8000, 7000)), "Q during the swing");
        TestAssert.Equal(1, match.Log.StopAttacks.Count, "the client is told to drop the swing before the cast reaches it");
        TestAssert.True(match.Log.Casts.Count >= castsBefore + 1, "and the cast goes out");
        TestAssert.Equal(0, match.Hero.Waypoints.Count, "she stops for the cast");

        // W is an instacast toggle with no child: it keeps the swing going, as before.
        match.Refill();
        match.Hero.AttackTarget = match.Foe.NetId;
        match.Log.StopAttacks.Clear();
        TestAssert.True(match.Cast(1), "W during an attack");
        TestAssert.Equal(0, match.Log.StopAttacks.Count, "an instacast toggle does not stop the attack");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Effect names the client cannot resolve
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-vfx-soraka-r-draws-wish-only-on-an-ally-the-heal-leaves-hurt")]
    public static void WishEffectFollowsTheScriptsHealthGate()
    {
        // Wish.luaobj: BBIncHealth, then BBGetPAROrHealth(GetHealthPercent) < 1 gates BBSpellEffectCreate Wish_tar on the target. The percent is the
        // client's health / max health, so an ally the heal tops up (or one already full) gets no effect: retail's own script.
        var gate = Blocks(Script("Wish"), "TargetExecute").First(block => block.Function == "BBIf");
        TestAssert.Equal("CO_LESS_THAN", (gate.Param("CompareOp") as BbValue.Reference)?.Name, "Wish.luaobj gate is health percent < 1");
        TestAssert.False(GameData.Default.Spell("Wish", 3).RawFlag("SpellData*HaveHitEffect"), "Wish.inibin HaveHitEffect is 0: the script owns Wish_tar");
        using var match = new Match("Soraka");
        match.Learn(1);
        match.Refill();
        match.Ally.Health = 100;                      // rank 1 heals 200 + 0.7 AP: still short of 5000
        TestAssert.True(match.Cast(3), "Wish");
        match.Advance(1.0);
        var wish = Calls(match, "Wish_tar.troy");
        TestAssert.Equal(1, wish.Count, "one Wish_tar: the hurt ally; the full-health heroes show none");
        TestAssert.Equal((match.Ally.NetId, match.Ally.NetId), (wish[0].Bind, wish[0].Target), "bound to and targeting the healed ally");
    }

    [Check("scripting.bb-vfx-casts-that-play-an-fx-track-are-not-instant-and-their-particles-exist")]
    public static void FxTrackCastsRunThroughTheClientsRenderer()
    {
        // OnTheHunt.fx starts DeathsCaress_nova on the weapon, Wish.fx soraka_wish_cas and Wish_tar: the client's SpellFXRenderer plays a track on a normal cast,
        // instant spells bypass it. Neither spell is instacast and every particle they name (track and buff auto effects) is in the client's list.
        var known = ClientParticles();
        foreach (var (spell, tracks) in new[]
                 {
                     ("OnTheHunt", new[] { "DeathsCaress_nova", "DeathsCaress_buf", "DeathsCaress_nova2", "OntheHunt_buf", "OntheHuntBase_buf" }),
                     ("Wish", new[] { "soraka_wish_cas", "Wish_tar" }),
                 })
        {
            var data = GameData.Default.Spell(spell, 3);
            TestAssert.False(data.IsInstant, $"{spell}.inibin is a windup cast, so its .fx track runs");
            var fx = File.ReadAllBytes(GameData.Default.Paths.Data($"Spells/{spell}.fx"));
            foreach (var name in tracks)
            {
                if (name.StartsWith("DeathsCaress_nova") || name is "soraka_wish_cas" or "Wish_tar")
                    TestAssert.True(System.Text.Encoding.Latin1.GetString(fx).Contains(name, StringComparison.OrdinalIgnoreCase), $"{spell}.fx names {name}");
                if (name != "DeathsCaress_nova2")           // the track's second nova has no particle of that name (the track entry is the wrong name in the shipped file)
                    TestAssert.True(known.Contains(name.ToLowerInvariant()), $"{name} is in the client's particle list");
            }
        }
        using var match = new Match("Sivir");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(3), "On The Hunt");
        var cast = match.Log.Casts.Single(info => info.Slot == 3);
        TestAssert.Near(0.15, cast.DesignerCastTime, 1e-6, "the cast announces its windup (designer cast time 0.15 s, not an instant 0)");
        TestAssert.Equal(0, match.Log.EffectCalls.Count, "no server effect: the cast effect is the client's .fx track, the buff effects the client's auto effects");
    }

    [Check("scripting.bb-vfx-no-sent-effect-is-missing-from-the-client-particle-list")]
    public static void EveryEffectTheFiveChampionsSendIsInTheClientsParticleList()
    {
        var known = ClientParticles();
        TestAssert.True(known.Count > 2700, $"troy.filelist holds the particle names ({known.Count})");
        TestAssert.True(known.Contains("global_heal") && known.Contains("taricgemstorm"), "the list holds the Taric and heal names");
        TestAssert.False(known.Contains("taric_hammerinternal"), "the one shipped name with no particle (TaricHammerInternal.luaobj)");
        TestAssert.False(GameData.Default.HasClientParticle("Taric_HammerInternal.troy"), "the engine agrees");
        TestAssert.True(GameData.Default.HasClientParticle("TARIC_gemstorm_AURA.troy"), "case does not matter, the extension is dropped");
        TestAssert.True(GameData.Default.HasClientParticle("StaticField_nova.prt"), "a .prt name resolves through its .troy entry");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var created = 0;
        foreach (var champion in new[] { "Soraka", "Ryze", "Taric", "Kayle", "Sivir" })
        {
            using var match = new Match(champion);
            match.Learn(3);
            match.Foe.Position = new Position2(7300, 7000);
            for (var slot = 0; slot < 4; slot++)
            {
                match.Refill();
                match.Hero.AttackTarget = null;
                var spell = GameData.Default.Spell(match.Module.SpellName(match.Hero, slot), slot == 3 ? 3 : 5);
                var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(spell));
                var target = !spell.TargetsUnit ? null : flags.Contains("AffectEnemies") ? match.Foe : match.Ally;
                Position2? at = champion == "Sivir" && slot == 0 ? new Position2(8000, 7000) : null;
                match.Cast(slot, target, at: at);
                match.Advance(1.0);
                if (champion is "Kayle" or "Sivir" && slot is 1 or 2)
                {
                    match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
                    match.Advance(2.0);
                }
            }
            match.Advance(3.0);
            foreach (var call in match.Log.EffectCalls)
            {
                created++;
                if (!known.Contains(Stem(call.Name).ToLowerInvariant())) missing.Add($"{champion}: {call.Name}");
            }
            // The scripts' own names too, whether or not a scenario reaches them.
            var resolver = new BbScriptResolver(GameData.Default.Paths).Resolve(champion);
            foreach (var script in resolver.Scripts)
                foreach (var (hook, block) in script.Script.Walk())
                    if (block.Function == "BBSpellEffectCreate" && block.Text("EffectName") is { } name && hook != BbScript.PreLoadHook &&
                        !GameData.Default.HasClientParticle(name) && !name.Equals("Taric_HammerInternal.troy", StringComparison.OrdinalIgnoreCase))
                        missing.Add($"{champion}: {script.Name} creates {name}");
        }
        TestAssert.True(created > 20, $"the five kits create effects ({created})");
        TestAssert.Equal(0, missing.Count, "effects the client has no particle for: " + string.Join(", ", missing));
    }

    // Batch 386 (sweep FX-RESOLVE: Riven E, Renekton Q): a shipped script names its effect with trailing spaces ("exile_E_mis.troy  " in
    // RivenFeint.luaobj, "renektoncleave_trail_rage.troy  " in RenektonCleave.luaobj), and Galio's E names "galio_..._red.tro". The client's effect
    // map holds stems, so the 0x8C must carry the hash of the stem, the name GameData.HasClientParticle judges the particle by.
    [Check("scripting.bb-vfx-padded-particle-names-hash-their-stem")]
    public static void PaddedParticleNamesHashTheirStem()
    {
        var data = GameData.Default;
        TestAssert.True(data.HasClientParticle("exile_E_mis.troy  "), "the padded name is a client particle");
        TestAssert.True(data.HasClientParticle("galio_windTunnel_speed_buf_team_red.tro"), "the mistyped name is a client particle");
        var queue = new CaptureQueue();
        var effects = new ProtocolSpellEffectSink(queue, new ReplicationSyncCounter(), new NavigationBounds(0, 10000, 0, 10000),
            (x, z) => 0.0, _ => 0x40000100u, _ => { });
        effects.CreateEffect("exile_E_mis.troy  ", new Position2(100, 100), bindNetId: 0x40000001u);
        effects.CreateEffect("renektoncleave_trail_rage.troy  ", new Position2(100, 100), bindNetId: 0x40000001u);
        effects.CreateEffect("galio_windTunnel_speed_buf_team_red.tro", new Position2(100, 100), bindNetId: 0x40000001u);
        var hashes = queue.Packets.Where(packet => packet.Plaintext.Span[0] == 0x8C)
            .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..])).ToArray();
        TestAssert.Equal(3, hashes.Length, "one 0x8C per create");
        TestAssert.Equal(SpellPackets.BuffNameHash("exile_E_mis"), hashes[0], "the 0x8C names the stem of Riven's E particle");
        TestAssert.Equal(SpellPackets.BuffNameHash("renektoncleave_trail_rage"), hashes[1], "the 0x8C names the stem of Renekton's Q particle");
        TestAssert.Equal(SpellPackets.BuffNameHash("galio_windTunnel_speed_buf_team_red"), hashes[2], "the 0x8C names the stem of Galio's E particle");
    }

    private sealed class CaptureQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId, Func<uint, PlaintextPacket> buildPacket) =>
            Packets.Add(buildPacket(killerNetId));
    }
}
