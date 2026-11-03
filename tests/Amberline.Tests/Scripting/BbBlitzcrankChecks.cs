using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Blitzcrank runs from his shipped scripts (phase 3, 19 October 2026, W27). The expected numbers are read from the shipped script tables
/// and the spell inibins and pinned a second time as the literal both files agree on. The per-spell wire and timing checks are
/// <c>BlitzcrankLiveChecks</c> (the production composition); these are the engine-level ones: the closure, the general rules he needed
/// (the hero as <c>Target</c> in <c>CharOnActivate</c>, the held press of an instacast parent with a windup child, a pre-damage hook that
/// cannot go negative, the cooldown cheat) and the passive's arithmetic.
/// </summary>
public static class BbBlitzcrankChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // A production match: Blitzcrank (Order) and an Annie (Chaos), created through the session so that the script module is bound.
    private sealed class Rig : IDisposable
    {
        public Rig(string foe = "Annie", GameData? data = null)
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Blitz", "Blitzcrank", Team.Order), new(2, 1, "Foe", foe, Team.Chaos),
            };
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), data ?? GameData.Default, readySettleSeconds: 0, roster: roster);
            Runtime.World.Clock.SetVirtual(0);
            Runtime.Session.CreateWorld();
            for (var i = 0; i < roster.Count; i++)
            {
                var id = i + 1;
                TestAssert.True(Runtime.Session.Authenticate(id, Runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                Runtime.Session.Spawn(id);
                TestAssert.True(Runtime.Session.MarkReady(id), "ready");
                var hero = Runtime.Session.Participants[i].Hero!;
                hero.HeroAutoAcquire = false;
                hero.MaxHealth = hero.Health = 5000;
                hero.Mana = hero.MaxMana;
                for (var slot = 0; slot < 4; slot++) hero.SpellRanks[slot] = 1;
                hero.Position = new Position2(7000 + i * 300, 7000);
            }
            TestAssert.True(Runtime.Session.Start(), "start");
            Blitz = Runtime.Session.Participants[0].Hero!;
            Foe = Runtime.Session.Participants[1].Hero!;
        }

        public MatchRuntime Runtime { get; }
        public Entity Blitz { get; }
        public Entity Foe { get; }
        public double Now => Runtime.World.Clock.Now();

        public bool Cast(int slot, Entity? target = null)
        {
            var point = target?.Position ?? Blitz.Position;
            var at = new Position3(point.X, 0, point.Z);
            return Runtime.Spells.HandleCastRequest(Blitz, new SpellCastRequest(slot, false, at, at, target?.NetId ?? 0), Now);
        }

        public void Advance(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 1.0 / 30.0) Runtime.Tick(1.0 / 30.0);
        }

        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    [Check("scripting.bb-blitzcrank-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Blitzcrank");
        TestAssert.Equal(20, module.ScriptNames.Count, "the Blitzcrank closure is 20 scripts (audit bb-coverage)");
        foreach (var name in new[] { "CharScriptBlitzcrank", "RocketGrab", "RocketGrabMissile", "RocketGrab2", "Overdrive", "PowerFist", "PowerFistSlow",
                     "StaticField", "ManaBarrierIcon", "ManaBarrier", "ManaBarrierCooldown", "Root" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        // OverdriveSlow ("Underdrive") is shipped but no script adds it: Overdrive has no slow in this build.
        TestAssert.False(module.ScriptNames.Contains("OverdriveSlow", StringComparer.OrdinalIgnoreCase), "OverdriveSlow is referenced by no script");
        var hero = new Entity { ChampionName = "Blitzcrank" };
        TestAssert.Equal(["RocketGrab", "Overdrive", "PowerFist", "StaticField"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Blitzcrank.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks come from the spell inibins");
        TestAssert.Equal("RocketGrabMissile", GameData.Default.Champion("Blitzcrank").Raw.TryGetValue(InibinReader.KeyHash("Data*ExtraSpell1"), out var extra)
            ? extra.AsString() : null, "ExtraSpell1 is the Rocket Grab missile");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Blitzcrank");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 20 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Blitzcrank hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-blitzcrank-q-is-an-instacast-parent-with-a-windup-child")]
    public static void RocketGrabIsHeldLikeAWindupSpell()
    {
        // BbChampionModule.CasterChildSpell: the parent RocketGrab is an instacast whose SelfExecute casts the normal child RocketGrabMissile
        // (0.25 s), so a press of Q inside another spell's windup is held like Static Field's (the windup gate); Overdrive and Power Fist
        // are true instacasts. The same rule holds Sivir's Q (SpiralBlade casts its child), which it did not before.
        using var rig = new Rig();
        var system = rig.Runtime.Spells;
        SpellCastRequest Press(int slot) => new(slot, false, new Position3(7300, 0, 7000), new Position3(7300, 0, 7000), 0);
        TestAssert.True(system.CreatesSpellCaster(rig.Blitz, Press(0)), "Rocket Grab creates a SpellCaster through its child");
        TestAssert.False(system.CreatesSpellCaster(rig.Blitz, Press(1)), "Overdrive is a true instacast");
        TestAssert.False(system.CreatesSpellCaster(rig.Blitz, Press(2)), "Power Fist is a true instacast");
        TestAssert.True(system.CreatesSpellCaster(rig.Blitz, Press(3)), "Static Field has a windup of its own");
        TestAssert.Equal("RocketGrabMissile", new BbChampionModule("Blitzcrank").CasterChildSpell(rig.Blitz, 0), "the child is named by ExtraSpell1");
        TestAssert.True(new BbChampionModule("Blitzcrank").CasterChildSpell(rig.Blitz, 1) is null, "Overdrive casts no child");
        TestAssert.Equal("SpiralBladeMissile", new BbChampionModule("Sivir").CasterChildSpell(new Entity { ChampionName = "Sivir" }, 0),
            "Sivir's Spiral Blade is the same shape");
    }

    [Check("scripting.bb-blitzcrank-char-on-activate-adds-mana-barrier-to-the-hero")]
    public static void CharOnActivateReachesTheHeroAsTarget()
    {
        // CharScriptBlitzcrank.CharOnActivate adds ManaBarrierIcon to TargetVar="Target"; the activation scope names the hero as Target,
        // otherwise BBSpellBuffAdd does nothing and the passive never exists.
        using var rig = new Rig();
        TestAssert.True(rig.Blitz.Buffs.Any(buff => buff.Name == "ManaBarrierIcon"), "the passive icon is on the hero at creation");
        TestAssert.True(rig.Blitz.Buffs.First(buff => buff.Name == "ManaBarrierIcon").Type == BuffType.Aura, "BUFF_Aura");
        TestAssert.False(rig.Foe.Buffs.Any(buff => buff.Name == "ManaBarrierIcon"), "only Blitzcrank has it");
    }

    [Check("scripting.bb-blitzcrank-mana-barrier-follows-the-shipped-arithmetic")]
    public static void ManaBarrierBlocksDownToTwentyPercent()
    {
        // ManaBarrierIcon.BuffOnPreDamage: when the hit would leave 20 percent of the maximum or less, the damage down to the 20 percent line
        // goes through and a shield of half the current mana absorbs the rest; the shield buff (10 s) is added only when it covers the rest,
        // the 60 s cooldown buff always.
        using (var rig = new Rig())
        {
            var hero = rig.Blitz;
            hero.Health = 1000;
            hero.Mana = 2000;
            TestAssert.True(rig.Runtime.Combat.ApplyHit(rig.Foe.NetId, hero.NetId, 500, DamageType.True, DamageSourceKind.Spell), "the hit lands");
            // 1000 - 500 = 500 is under the 1000 line: let through 1000 - 1000 = 0, block 500 with a 1000 shield: no damage, shield 1000 - 500.
            TestAssert.Near(1000, hero.Health, 1e-6, "the shield covers the whole hit");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ManaBarrier"), "the shield buff is added");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ManaBarrierCooldown"), "the cooldown buff is added");
            TestAssert.Near(2000, hero.Mana, 1e-6, "the mana is not spent");
        }
        using (var rig = new Rig())
        {
            var hero = rig.Blitz;
            hero.Health = 1000;
            hero.Mana = 400;
            TestAssert.True(rig.Runtime.Combat.ApplyHit(rig.Foe.NetId, hero.NetId, 500, DamageType.True, DamageSourceKind.Spell), "the hit lands");
            // The shield (half of 400 = 200) does not cover the 500 to block: the hit loses 200 and the shield buff is not added.
            TestAssert.Near(700, hero.Health, 1e-6, "an uncovered hit loses the shield's 200");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ManaBarrier"), "no shield buff when the shield does not cover the hit");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ManaBarrierCooldown"), "the cooldown buff is added");
        }
        using (var rig = new Rig())
        {
            // Already under the line with the cooldown over: the script's damageToLetThrough is negative, the block exceeds the hit and
            // DamageAmount would go negative. The pipeline refuses a negative amount, so the hit is blocked in full (NOT RETAIL clamp).
            var hero = rig.Blitz;
            hero.Health = 500;
            hero.Mana = 400;
            TestAssert.True(rig.Runtime.Combat.ApplyHit(rig.Foe.NetId, hero.NetId, 100, DamageType.True, DamageSourceKind.Spell), "no exception, the hit lands");
            TestAssert.Near(500, hero.Health, 1e-6, "the hit is blocked in full");
        }
    }

    [Check("scripting.bb-blitzcrank-power-fist-arms-seals-and-starts-its-cooldown-when-spent")]
    public static void PowerFistArmsSealsAndReleases()
    {
        using var rig = new Rig();
        var hero = rig.Blitz;
        TestAssert.True(rig.Cast(2), "Power Fist is accepted");
        var fist = hero.Buffs.Single(buff => buff.Name == "PowerFist");
        TestAssert.Near(0, hero.SpellCooldowns[2], 1e-9, "the script sets the slot cooldown to 0 while the fist is armed");
        TestAssert.True(hero.SpellSeals.IsSealed(false, 2), "OnBuffActivate seals the E slot (the cast bit)");
        // Batch 252 (restated from the buff payload): batch 241 item 4d made BBSetDodgePiercing the unit's one status bit: the client sets
        // bit 0x8000 to Value, Entity.DodgePiercing; PowerFist's OnBuffActivate sets it and its OnBuffDeactivate clears it.
        TestAssert.True(hero.DodgePiercing, "BBSetDodgePiercing");
        TestAssert.False(rig.Cast(2), "a sealed slot cannot be pressed again");
        rig.Runtime.Buffs.RemoveBuff(hero, fist.Slot);
        TestAssert.False(hero.DodgePiercing, "OnBuffDeactivate clears the dodge-piercing bit");
        TestAssert.False(hero.SpellSeals.IsSealed(false, 2), "OnBuffDeactivate unseals the slot");
        // SpellCooldown = SrcValueByLevel {9,8,7,6,5} (rank one: 9) times 1 + the cooldown reduction (none).
        TestAssert.Near(9.0, hero.SpellCooldowns[2], 1e-6, "the cooldown starts when the fist is spent");
    }

    [Check("scripting.bb-blitzcrank-cooldown-cheat-reaches-the-scripted-cooldown")]
    public static void CooldownCheatAppliesToPowerFist()
    {
        using var rig = new Rig();
        var hero = rig.Blitz;
        hero.CooldownsDisabled = true;
        TestAssert.True(rig.Cast(2), "Power Fist is accepted");
        var fist = hero.Buffs.Single(buff => buff.Name == "PowerFist");
        rig.Runtime.Buffs.RemoveBuff(hero, fist.Slot);
        TestAssert.Near(0, hero.SpellCooldowns[2], 1e-9, "BBSetSlotSpellCooldownTime honours /cooldowns");
    }

    [Check("scripting.bb-blitzcrank-overdrive-has-no-slow")]
    public static void OverdriveHasNoSlowAfterwards()
    {
        // Overdrive.OnBuffDeactivate only removes its effects; OverdriveSlow ("Underdrive") is shipped but nothing adds it
        // (a search of every file under DATA and of the client executable), and the spell tooltip says "for 8 seconds" and no more.
        using var rig = new Rig();
        var hero = rig.Blitz;
        TestAssert.True(rig.Cast(1), "Overdrive is accepted");
        var buff = hero.Buffs.Single(item => item.Name == "Overdrive");
        TestAssert.Equal(BuffType.Haste, buff.Type, "BUFF_Haste");
        rig.Advance(8.2);
        TestAssert.False(hero.Buffs.Any(item => item.Name == "Overdrive"), "the buff ends after 8 seconds");
        TestAssert.False(hero.Buffs.Any(item => item.Name == "OverdriveSlow"), "no slow follows");
    }
}
