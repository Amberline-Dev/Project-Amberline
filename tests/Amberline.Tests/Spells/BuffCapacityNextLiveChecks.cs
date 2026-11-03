using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-recurring-champion-buffs-retry-after-capacity")]
    public static void RecurringChampionBuffsWaitForAnActualSlot()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var janna = new Match("Janna", team, ally: true))
            {
                // CharScriptJanna renews the allies' Tailwind every 4 s (UpdateSelfBuffActions, on the game clock); TailwindSelf is added once
                // when she is created, so only the allies' buff recurs.
                var ally = janna.Hero(2);
                janna.Runtime.Tick(.01);
                ClearNextNamed(janna, ally, "Tailwind");
                FillNextCapacity(ally);
                janna.Runtime.Tick(4.1);
                TestAssert.False(ally.Buffs.Any(buff => buff.Name == "Tailwind"));
                FreeNextCapacity(ally);
                janna.Runtime.Tick(4.1);
                TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "Tailwind"));
                janna.Runtime.Tick(4.1);
                TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "Tailwind"),
                    "full-table same-name Tailwind renewal keeps one live slot");
            }

            // 19 October 2026 (batch 98): Vayne plays from CharScriptVayne. Her Silver Bolts aura is added once, by CharOnLevelUpSpell when W
            // reaches rank 1, so a full table declines that add and nothing retries it (the retry was the hand-written module's, non-retail).
            // What recurs is Night Hunter: the script's UpdateSelfBuffStats is polled every spell update (BbBuffRuntime) and re-adds VayneHunted
            // and her speed part when a slot is free again; a module's character hooks run in the spell step (Runtime.Tick), not in the late phase.
            using (var vayne = new Match("Vayne", team))
            {
                var caster = vayne.Hero(0);
                var enemy = vayne.Hero(1);
                caster.Facing = new Position2(1, 0);
                ClearNextNamed(vayne, caster, "VayneSilveredBolts",
                    "VayneInquisitionSpeedPart", "VayneInquisitionSpeedPartNormal");
                FillNextCapacity(caster);
                FillNextCapacity(enemy);
                // Batch 252 (restated from 0.1 s): batch 246 runs the character script's bare self hooks on the holder's 0.25 s poll line
                // (BbSelfUpdate), so each window is one line long.
                vayne.Runtime.Tick(.25);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "VayneSilveredBolts"));
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "VayneHunted"));
                FreeNextCapacity(caster);
                FreeNextCapacity(enemy);
                vayne.Runtime.Tick(.25);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "VayneSilveredBolts"),
                    "the once-added aura is not retried by the poll");
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "VayneHunted"));
                TestAssert.True(caster.Buffs.Any(buff => buff.Name == "VayneInquisitionSpeedPartNormal"),
                    "the freed slot goes to the speed part, which the poll renews (no aura retry competes for it any more)");
            }

            using (var nidalee = new Match("Nidalee", team))
            {
                // 19 October 2026 (batch 108): Prowl is renewed from the character script's UpdateSelfBuffActions, which runs in the spell step.
                var caster = nidalee.Hero(0);
                var grid = nidalee.Runtime.Navigation;
                caster.Position = Enumerable.Range(0, grid.CountX * grid.CountY)
                    .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX)))
                    .First(point => grid.IsMovementWalkable(point) && grid.BrushIdAt(point) is not null);
                ClearNextNamed(nidalee, caster, "Prowl");
                FillNextCapacity(caster);
                nidalee.Runtime.Tick(.25); // batch 252: one 0.25 s poll line (batch 246), as for Vayne above
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "Prowl"));
                FreeNextCapacity(caster);
                nidalee.Runtime.Tick(.25);
                TestAssert.True(caster.Buffs.Any(buff => buff.Name == "Prowl"));
            }

            using (var orianna = new Match("Orianna", team))
            {
                // 19 October 2026 (batch 112): OrianaGhostSelf is added by the character script when she is created and again when she
                // resurrects (CharOnActivate, CharOnResurrect); a full table declines that add and nothing polls it (the hand-written ball
                // defense retried every tick, non-retail).
                var caster = orianna.Hero(0);
                ClearNextNamed(orianna, caster, "OrianaGhostSelf");
                FillNextCapacity(caster);
                orianna.Runtime.Tick(.1);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"));
                FreeNextCapacity(caster);
                orianna.Runtime.Tick(.1);
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "OrianaGhostSelf"),
                    "a declined script add is not retried by a poll");
            }
        }
    }

    [Check("spells-live-recall-full-buff-table-rejects-before-channel")]
    public static void RecallRejectsBeforeCommittingChannelWhenFull()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            FillNextCapacity(hero);
            var before = hero.Position;
            match.Clear();
            TestAssert.False(match.TryCast(0, 10, hero), "full table rejects Recall");
            TestAssert.Near(0, match.Runtime.Spells.RecallRemaining(hero), 1e-9);
            TestAssert.Equal(0u, match.Runtime.Spells.RecallEffectId(hero));
            TestAssert.Equal(before, hero.Position);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"));
            FreeNextCapacity(hero);
            TestAssert.True(match.TryCast(0, 10, hero), "one slot admits ordinary Recall");
            match.Runtime.Tick(0.6); // the channel and its effect start at the cast frame (batch 179)
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0 && match.Runtime.Spells.RecallEffectId(hero) != 0);
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "Recall"));
        }
    }

    [Check("spells-live-full-table-damage-procs-do-not-repeat-or-partial-cc")]
    public static void DamageProcsRetainHitsWhenFollowupBuffsCannotFit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var lee = new Match("LeeSin", team))
            {
                var caster = lee.Hero(0);
                var enemy = lee.Hero(1);
                enemy.Position = caster.Position + new Position2(350, 0);
                FillNextCapacity(enemy);
                var health = enemy.Health;
                lee.CastGround(0, enemy.Position);
                Advance(lee, .8);
                TestAssert.True(enemy.Health < health, "full-table Sonic Wave still hits");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name is
                    "BlindMonkQOne" or "BlindMonkQOneChaos"));
                TestAssert.Equal(0, LeeFlights(lee, caster, "BlindMonkQOne").Length,
                    "the unmarkable hit retires its missile rather than damaging again");
                var after = enemy.MatchStatistics.TotalDamageTaken;
                Advance(lee, .2);
                TestAssert.Near(after, enemy.MatchStatistics.TotalDamageTaken, 1e-8,
                    "retired Sonic Wave cannot apply damage a second time");
            }

            using (var orianna = new Match("Orianna", team))
            {
                var caster = orianna.Hero(0);
                var enemy = orianna.Hero(1);
                enemy.Armor = 0.0;
                enemy.SpellBlock = 0.0;
                FillNextCapacity(caster);
                var health = enemy.Health;
                TestAssert.True(orianna.Runtime.Combat.ApplyHit(caster.NetId, enemy.NetId,
                    10, DamageType.Physical, DamageSourceKind.Basic));
                TestAssert.True(enemy.Health < health - 10,
                    "the first passive damage proc remains even without a display slot");
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "OrianaPowerDaggerDisplay"));
                FreeNextCapacity(caster);
                TestAssert.True(orianna.Runtime.Combat.ApplyHit(caster.NetId, enemy.NetId,
                    10, DamageType.Physical, DamageSourceKind.Basic));
                TestAssert.Equal(1, caster.Buffs.Count(buff => buff.Name == "OrianaPowerDaggerDisplay"));
            }

            using (var leeE = new Match("LeeSin", team))
            {
                var caster = leeE.Hero(0);
                var enemy = leeE.Hero(1);
                FillNextCapacity(enemy);
                var before = enemy.Health;
                leeE.CastGround(0, caster.Position, slot: 2);
                Advance(leeE, .6);
                TestAssert.True(enemy.Health < before, "Tempest keeps its completed damage");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "BlindMonkEOne"));
                TestAssert.True(caster.Buffs.Any(buff => buff.Name == "BlindMonkEManager") &&
                    caster.ActiveSpellNames[2] == "BlindMonkETwo",
                    "capacity does not abort E1's remaining-target or E2-window logic");
            }

            using (var leeE2 = new Match("LeeSin", team))
            {
                var caster = leeE2.Hero(0);
                var enemy = leeE2.Hero(1);
                leeE2.CastGround(0, caster.Position, slot: 2);
                Advance(leeE2, .6);
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlindMonkEOne"));
                FillNextCapacity(enemy);
                leeE2.CastGround(0, caster.Position, slot: 2);
                Advance(leeE2, .6);
                TestAssert.Equal(0, LeeFlights(leeE2, caster).Length,
                    "E2 retires its homing missile when the slow cannot allocate");
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "BlindMonkETwoMissile"));
            }
        }
    }

    // 19 October 2026: "Shockwave never commits half of its buff pair" pinned the hand-written R at a full buff table. The script adds OrianaStun and
    // MoveAwayCollision one after the other with BBSpellBuffAdd (capacity is the engine's, BbBuffRuntime.Add); the two can no longer be promised to
    // land together.

    [Check("spells-live-ezreal-w-full-table-damage-and-passive-retry")]
    public static void EssenceFluxRetiresEvenWhenItsBuffsCannotFit()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Ezreal", team);
            var caster = match.Hero(0);
            var enemy = match.Hero(1);
            FillNextCapacity(caster);
            FillNextCapacity(enemy);
            var before = enemy.Health;
            match.CastGround(0, enemy.Position, slot: 1);
            Advance(match, .8);
            TestAssert.True(enemy.Health < before, "real Essence Flux still deals damage");
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "EzrealEssenceFluxMissile"));
            TestAssert.False(caster.Buffs.Any(buff => buff.Name == "EzrealRisingSpellForce"));
            FreeNextCapacity(caster);
            FreeNextCapacity(enemy);
            caster.SpellCooldowns[1] = 0.0;
            caster.Mana = caster.MaxMana;
            match.CastGround(0, enemy.Position, slot: 1);
            Advance(match, .8);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "EzrealEssenceFluxMissile"));
            TestAssert.True(caster.Buffs.Any(buff => buff.Name == "EzrealRisingSpellForce"));
        }
    }

    [Check("spells-live-jungle-buff-transfer-capacity-skips-and-renews")]
    public static void JungleDeathAndLevelCallbacksSkipMissingSlots()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            // The crest is the character script's own now (batch 159 part 2): binding CharScriptAncientGolem runs its
            // UpdateSelfBuffActions (0000-0075), which keeps CrestoftheAncientGolem on the golem, and the golem's death
            // runs that buff's own BuffOnDeath (Spells/CrestoftheAncientGolem.luaobj 0231-0462). A direct hero kill
            // renews the live instance (0323, BUFF_RENEW_EXISTING). The hand-written JungleBuffService transfer is gone.
            FillNextCapacity(hero);
            TestAssert.True(runtime.Combat.Kill(NewCrestGolem(runtime, hero), hero.NetId),
                "a golem death reaches the shipped transfer");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "CrestoftheAncientGolem"),
                "a full table skips the crest the death hook offers");
            FreeNextCapacity(hero);
            TestAssert.True(runtime.Combat.Kill(NewCrestGolem(runtime, hero), hero.NetId),
                "a golem death with one free slot reaches the shipped transfer");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "CrestoftheAncientGolem"));
            FillNextCapacity(hero);
            TestAssert.True(runtime.Combat.Kill(NewCrestGolem(runtime, hero), hero.NetId),
                "the renewal's golem death reaches the shipped transfer");
            TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "CrestoftheAncientGolem"),
                "direct-hero buff renewal reuses its occupied slot");

            hero.Inventory = [new InventorySlot(3010), null, null, null, null, null];
            // Batch 149 step 5: the level watch is Items/3010.luaobj's own UpdateSelfBuffActions now — BBGetLevel against
            // InstanceVars.OwnerLevel (seeded by its OnActivate, 0051-0061), and its own add of Spells/CatalystHeal.luaobj
            // for 8.5 s. The hand-written JungleBuffService.OnHeroLevelChanged is gone.
            // What the watch does on a full table is the sub-block's own shape (0011-0046): the compare is strict
            // (CO_GREATER_THAN) and the OwnerLevel write is a second call after the add (0039-0046), so it runs whatever
            // the add did. A capacity refusal therefore records the level and the heal is skipped for that level rather
            // than queued — the next poll only re-offers it at a further level-up. (This replaces the batch-149 reading
            // "a capacity refusal leaves the level unrecorded and the next poll retries": only BBBreakExecution stops a
            // run, and the declined add raises BbHookFrame.GroupDenied, which the damage readers honour but the script's
            // own variable writes do not.)
            // Batch 252: the item polls run on the holder's 0.25 s line (batch 246, BbSelfUpdate), so each level step below waits one line.
            runtime.Spells.Tick(0.01);
            hero.Level = 2;
            runtime.Spells.Tick(0.25);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "CatalystHeal"));
            FreeNextCapacity(hero);
            runtime.Spells.Tick(0.25);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "CatalystHeal"),
                "the level the declined add recorded is not a strict increase, so the poll does not re-offer the heal");
            hero.Level = 3;
            runtime.Spells.Tick(0.25);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "CatalystHeal"),
                "the next level-up re-enters the watch and the freed slot admits its add");

            // Batch 174 part 2: the Baron's team grant is ResistantSkin's own BuffOnDeath (Spells/ResistantSkin.luaobj 0519-0634), which the
            // death hook runs for the bound Worm: a full table refuses the 240 s renewal, a free slot takes it.
            Entity NewBaron()
            {
                var baron = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                    EntityKind.Neutral, UnitType.Monster, Team.Neutral, hero.Position)
                {
                    Name = "Worm", Alive = true, Created = true, Health = 3000, MaxHealth = 3000,
                });
                runtime.Spells.UnitScripts!.BindUnit(baron, "Worm");
                return baron;
            }
            FillNextCapacity(hero);
            TestAssert.True(runtime.Combat.Kill(NewBaron(), hero.NetId), "a Baron kill reaches the scripted team grant");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor"));
            FreeNextCapacity(hero);
            TestAssert.True(runtime.Combat.Kill(NewBaron(), hero.NetId), "a Baron kill with one free slot reaches the grant");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ExaltedWithBaronNashor"));
        }
    }

    /// <summary>
    /// A real AncientGolem of the blue camp shape: <c>BindUnit</c> runs CharScriptAncientGolem's own
    /// <c>UpdateSelfBuffActions</c> (0000-0075), so the crest the kill transfers is a buff the character script placed
    /// (and the transfer is that buff's own <c>BuffOnDeath</c>), not a hand-built row.
    /// </summary>
    private static Entity NewCrestGolem(MatchRuntime runtime, Entity hero)
    {
        var golem = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, hero.Position)
        {
            Name = "AncientGolem", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0,
        };
        runtime.World.Register(golem);
        TestAssert.True(runtime.Spells.UnitScripts!.BindUnit(golem, "AncientGolem"),
            "the AncientGolem binds its shipped character script");
        TestAssert.True(golem.Buffs.Any(buff => buff.Name == "CrestoftheAncientGolem"),
            "CharScriptAncientGolem's own UpdateSelfBuffActions keeps the crest on the golem");
        return golem;
    }

    private static void FillNextCapacity(Entity unit)
    {
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots; slot++)
        {
            if (unit.Buffs.Count == SpellPackets.MaxBuffSlots) break;
            if (unit.Buffs.Any(buff => buff.Slot == slot)) continue;
            unit.Buffs.Add(new BuffState($"NextCapacity{slot}", slot: slot, expiresAt: 60));
        }
        TestAssert.Equal(SpellPackets.MaxBuffSlots, unit.Buffs.Count);
    }

    private static void FreeNextCapacity(Entity unit)
    {
        var filler = unit.Buffs.First(buff => buff.Name.StartsWith("NextCapacity", StringComparison.Ordinal));
        unit.Buffs.Remove(filler);
    }

    private static void ClearNextNamed(Match match, Entity unit, params string[] names)
    {
        foreach (var buff in unit.Buffs.Where(buff => names.Contains(buff.Name)).ToArray())
            match.Runtime.Buffs.RemoveBuff(unit, buff.Slot);
    }
}
