using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("minions-b6-help-stick")]
    public static void ChampionHelpKeepsAcceptedPriorityDuringStick()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var victim = match.Hero(0);
            var aggressor = match.Hero(1);
            victim.Position = new(7000, 7000);
            aggressor.Position = new(7100, 7000);
            victim.AttackCooldown = aggressor.AttackCooldown = 100;
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team, new(7050, 7000))
            {
                MinionType = MinionType.Melee, Health = 1000, MaxHealth = 1000,
                AttackRange = 200, AcquisitionRange = 750, Radius = 35, Speed = 0, AttackCooldown = 100,
            });
            var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
                EntityKind.Turret, UnitType.Turret, aggressor.Team, new(7200, 7000))
            { Health = 1000, MaxHealth = 1000, AttackRange = 775, AttackCooldown = 100 });
            aggressor.AttackTarget = victim.NetId;
            runtime.Combat.ApplyHit(aggressor.NetId, victim.NetId, 1, DamageType.True, DamageSourceKind.Basic);
            runtime.Minions!.Tick(.01);
            TestAssert.Equal((uint?)aggressor.NetId, minion.AttackTarget);
            TestAssert.Near(1.5, minion.HelpStickRemaining, 1e-9, "shipped cfh_Stick");
            aggressor.AttackTarget = null; // Move/Stop drops the live champion-on-champion relationship.
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1, DamageType.True, DamageSourceKind.Basic);
            TestAssert.True(minion.HelpTarget is null, "nomination must retain accepted priority after Move");
            runtime.Combat.CombatTick(1.49, regenerate: false);
            var remaining = minion.HelpStickRemaining;
            minion.HelpTarget = aggressor.NetId;
            minion.HelpPriority = 4; // weaker same-target champion-on-minion event
            minion.HelpRemaining = 1;
            runtime.Minions.Tick(.001);
            TestAssert.Equal(0, minion.HelpStickPriority, "same-target renewal cannot downgrade accepted priority");
            TestAssert.Near(remaining, minion.HelpStickRemaining, 1e-9,
                "weaker renewal cannot extend the stronger priority beyond its original interval");
            // Model an already queued lower-priority event reaching consumption.
            minion.HelpTarget = turret.NetId;
            minion.HelpPriority = 3;
            minion.HelpRemaining = 1;
            runtime.Minions.Tick(.01);
            TestAssert.Equal((uint?)aggressor.NetId, minion.AttackTarget,
                "consumption also protects the accepted priority before 1.5 seconds");
            TestAssert.True(minion.HelpTarget is null, "rejected requests are consumed");
            runtime.Combat.CombatTick(.010001, regenerate: false);
            runtime.Combat.ApplyHit(turret.NetId, minion.NetId, 1, DamageType.True, DamageSourceKind.Basic);
            TestAssert.Equal((uint?)turret.NetId, minion.HelpTarget, "eligible help resumes after stick expiry");
            runtime.Minions.Tick(.01);
            TestAssert.Equal((uint?)turret.NetId, minion.AttackTarget);
        }
    }

    [Check("pets-b6-tibbers-target-transition-cancels-windup")]
    public static void TibbersTargetTransitionsCancelWindups()
    {
        // PET-01: Pet.lua TimerFindEnemies turns off attacks on target loss.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var parked in new[] { false, true })
        foreach (var replace in new[] { false, true })
        {
            using var match = new Match("Annie", team, extraEnemy: true);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            var replacement = match.Hero(2);
            enemy.Position = replacement.Position = new Position2(12000, 12000);
            var pet = AnnieChecks.SummonTibbers(runtime, owner, 1);
            var (outside, brush) = BrushEdge(runtime.Navigation);
            owner.Position = pet.Position = enemy.Position = outside;
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            pet.AttackCooldown = 0;
            // PetState.Order/Parked are gone with the hand-written machine: Pet.lua parks a hard
            // move that never gets anywhere in AI_PET_HARDIDLE on its next TimerScanDistance, so a
            // hard move to the pet's own position is the park order here.
            if (parked)
            {
                TestAssert.True(SendPetInput(match, pet, 6, pet.Position), "park input accepted");
                runtime.Pets.ScanNow(pet);
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
                TestAssert.True(IsParked(match, pet), "the parked pet holds in AI_PET_HARDIDLE");
            }
            void Decide()
            {
                runtime.Pets.ScanNow(pet);
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
            }
            Decide();
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId &&
                hit.TargetNetId == enemy.NetId && hit.WindupRemaining > 0), "visible target starts a windup");
            Decide();
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId &&
                hit.TargetNetId == enemy.NetId), "unchanged target keeps its windup");
            enemy.Position = brush;
            if (replace) replacement.Position = outside;
            TestAssert.False(runtime.Spells.CanSeeTarget(pet, enemy), "brush hides the old target");
            runtime.Delivery.Flush();
            match.Clear();
            var damage = enemy.MatchStatistics.TotalDamageTaken;
            Decide();
            runtime.Delivery.Flush();
            TestAssert.Equal(replace ? (uint?)replacement.NetId : null, pet.AttackTarget);
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == pet.NetId &&
                hit.TargetNetId == enemy.NetId), "target transition retires the old windup");
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x39 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == pet.NetId),
                "one stop-attack for the old target");
            // Advance combat only: Tibbers' independent burning aura may hit unseen units.
            runtime.Combat.CombatTick(1);
            TestAssert.Near(damage, enemy.MatchStatistics.TotalDamageTaken, 1e-9,
                "the cancelled basic attack cannot damage the unseen target");
        }
    }

    // A3 follow-up: Pet.lua OnCanAttack (the CanAttack status bit) and OnTauntBegin. A disarmed Tibbers starts no swing and picks the
    // fight back up when the bit comes back; a taunt sends him at its caster over the target he had; a blind Tibbers keeps swinging.
    [Check("pets-tibbers-honour-disarm-and-taunt")]
    public static void TibbersHonourDisarmAndTaunt()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, extraEnemy: true);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            var taunter = match.Hero(2);
            enemy.Position = taunter.Position = new Position2(12000, 12000);
            var pet = AnnieChecks.SummonTibbers(runtime, owner, 1);
            var (outside, _) = BrushEdge(runtime.Navigation);
            owner.Position = pet.Position = enemy.Position = outside;
            taunter.Position = outside + new Position2(40, 0);
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            pet.AttackCooldown = 0;
            void Decide()
            {
                runtime.Pets.ScanNow(pet);
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
            }
            bool Swinging(Entity target) => runtime.Combat.PendingHits.Any(hit =>
                hit.SourceNetId == pet.NetId && hit.TargetNetId == target.NetId);

            var disarm = runtime.Spells.Buffs.Add(pet, "Disarm", BuffType.Internal, 5.0, enemy.NetId,
                new BuffPayload { Disarmed = true }, canMitigateDuration: false);
            TestAssert.True(disarm is not null, "the disarm is added");
            Decide();
            TestAssert.False(Swinging(enemy), "a disarmed Tibbers starts no swing");
            // PetState.Controlled, the hand-written machine's OnCanAttack-pending flag, is gone
            // with it: Pet.lua only hears of the CanAttack bit from the BuffLifecycle control pair,
            // and its OnCanAttack resets the pet to AI_PET_IDLE when the bit comes back — and then
            // calls TimerFindEnemies() itself, so the enemy Tibbers already stands on is re-acquired
            // inside the same callback and the script ends the callback in AI_PET_ATTACK. The hand-
            // written machine left the pet in idle until the next scan; the script wins.
            runtime.Spells.Buffs.Remove(pet, disarm!.Slot);
            TestAssert.Equal(PetLuaAiEngine.StatePetAttack, runtime.Pets.StateOf(pet)!.Value,
                "OnCanAttack ran: the script re-acquires in the same callback");
            Decide();
            TestAssert.True(Swinging(enemy), "Tibbers swings again once the bit is back");

            // Blind.luaobj sets no status bit (a miss chance and a cancelled swing only): the pet keeps attacking.
            runtime.Combat.CancelPendingWindups(pet.NetId);
            pet.AttackCooldown = 0;
            var blind = runtime.Spells.Buffs.Add(pet, "Blind", BuffType.Blind, 5.0, enemy.NetId,
                new BuffPayload { MissChance = 0.5 }, canMitigateDuration: false);
            TestAssert.True(blind is not null, "the blind is added");
            Decide();
            TestAssert.True(Swinging(enemy), "a blind Tibbers still swings");
            runtime.Spells.Buffs.Remove(pet, blind!.Slot);

            runtime.Combat.CancelPendingWindups(pet.NetId);
            pet.AttackCooldown = 0;
            runtime.Spells.Buffs.Add(pet, "Taunt", BuffType.Taunt, 3.0, taunter.NetId, canMitigateDuration: false);
            Decide();
            TestAssert.Equal((uint?)taunter.NetId, pet.AttackTarget, "a taunt sends Tibbers at its caster");
            TestAssert.True(Swinging(taunter), "and he attacks the taunter, not the target he had");
        }
    }

    [Check("pets-live-tibbers-and-r-clones-respect-brush-sight")]
    public static void AutonomousPetsLoseUnseenBrushTargets()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "Shaco", "Yorick" })
        {
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            var (outside, brush) = BrushEdge(runtime.Navigation);
            Entity pet;
            if (champion == "Annie")
            {
                match.CastGround(0, owner.Position, 3);
                runtime.Tick(.5);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId);
            }
            else if (champion == "Shaco")
                pet = SpawnHallucinateClone(match, owner);
            else
            {
                match.Cast(0, 3, owner);
                runtime.Tick(.3);
                pet = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.YorickRevenant && unit.OwnerNetId == owner.NetId);
            }

            owner.Position = outside;
            enemy.Position = brush;
            pet.Position = outside;
            pet.Speed = 0.0;
            pet.Waypoints.Clear();
            pet.Target = null;
            pet.AttackTarget = null;
            var vision = new WorldMinionVisionService(runtime.World, runtime.Navigation,
                _ => Array.Empty<VisionProvider>());
            var label = $"{team}/{champion}";
            void Decide()
            {
                // Every pet runs Pet.lua's 0.15 s scan on the host (playtest B3 #7); the
                // hand-written machines' controller-buff NextAi step is gone with them.
                runtime.Pets.ScanNow(pet);
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0.0);
            }
            TestAssert.False(vision.HasBrushVision(team, enemy),
                $"{label}: adjacent brush edge really hides the enemy");
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: autonomous pet does not acquire an unseen brush target");

            owner.Position = brush;
            TestAssert.True(vision.HasBrushVision(team, enemy),
                $"{label}: an allied provider inside the brush restores sight");
            Decide();
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget,
                $"{label}: autonomous pet acquires the now-visible enemy");

            owner.Position = outside;
            TestAssert.False(vision.HasBrushVision(team, enemy),
                $"{label}: sight ends when the provider leaves brush");
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: pet releases the retained target after sight loss");

            // Brush sight is available here; only the production stealth rule
            // can keep this enemy hidden from autonomous pet targeting.
            owner.Position = brush;
            var stealth = runtime.SpellBuffs.Add(enemy, "PetSightProbe", BuffType.Invisibility,
                10.0, enemy.NetId, new BuffPayload { Stealthed = true }) ??
                throw new InvalidOperationException("Pet stealth probe was not applied.");
            TestAssert.True(vision.HasBrushVision(team, enemy),
                $"{label}: brush-only sight still sees the stealthed enemy");
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: autonomous pet does not acquire a stealthed enemy");
            runtime.SpellBuffs.Remove(enemy, stealth.Slot);
            Decide();
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget,
                $"{label}: autonomous pet acquires the enemy after destealth");
            var stealthAgain = runtime.SpellBuffs.Add(enemy, "PetSightProbeAgain",
                BuffType.Invisibility, 10.0, enemy.NetId,
                new BuffPayload { Stealthed = true }) ??
                throw new InvalidOperationException("Pet stealth retention probe was not applied.");
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: autonomous pet releases the target when stealth returns");
            runtime.SpellBuffs.Remove(enemy, stealthAgain.Slot);

            var opaque = runtime.Navigation.CellToWorld(new NavCell(128, 100));
            var clear = runtime.Navigation.CellToWorld(new NavCell(127, 100));
            TestAssert.True(runtime.Navigation.BrushIdAt(opaque) is null &&
                            !runtime.Navigation.HasVisionLos(clear, opaque) &&
                            runtime.Navigation.IsMovementWalkable(clear) &&
                            !VisibilityRules.ProviderReveals(runtime.Dependencies.VisionEnvironment,
                                // Batch 253: from the clear neighbour; the client's walk sees a target at the viewer's own point.
                                new VisionProvider(clear, 400), opaque),
                $"{label}: shipped adjacent nonbrush cells isolate opaque vision");
            owner.Position = clear;
            pet.Position = clear;
            enemy.Position = opaque;
            TestAssert.True(vision.HasBrushVision(team, enemy),
                $"{label}: old brush-only predicate incorrectly accepts opaque target");
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: autonomous pet cannot acquire through opaque terrain");
            enemy.Position = clear;
            Decide();
            TestAssert.Equal((uint?)enemy.NetId, pet.AttackTarget,
                $"{label}: pet acquires when target moves into clear sight");
            enemy.Position = opaque;
            Decide();
            TestAssert.Equal((uint?)null, pet.AttackTarget,
                $"{label}: pet drops retained target behind opaque terrain");
        }
    }

    [Check("pets-live-revenant-brush-loss-stops-windup")]
    public static void RevenantStopsAttackWhenTargetEntersBrush()
    {
        // Playtest B2 YV9, 26 Sept 2026: Pet.lua TurnOffAutoAttack(TARGET_LOST).
        // A stationary 0xC1 alone left the revenant swinging at nothing and
        // let the unseen windup land.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            var (outside, brush) = BrushEdge(runtime.Navigation);
            enemy.Position = new Position2(12000, 12000);
            match.Cast(0, 3, owner);
            runtime.Tick(.3);
            var revenant = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.YorickRevenant && unit.OwnerNetId == owner.NetId);
            owner.Position = outside;
            revenant.Position = outside;
            revenant.Speed = 0.0;
            revenant.Waypoints.Clear();
            revenant.Target = null;
            revenant.AttackTarget = null;
            revenant.AttackCooldown = 0.0;
            enemy.Position = outside;
            void Decide()
            {
                // The revenant runs Pet.lua like every other pet; the YorickRAPetBuff2
                // controller's NextAi step is gone with the hand-written machine.
                runtime.Pets.ScanNow(revenant);
                runtime.Pets.Tick(runtime.Combat.CurrentTime, 0.0);
            }

            Decide();
            TestAssert.Equal((uint?)enemy.NetId, revenant.AttackTarget,
                $"{team}: revenant attacks the visible hero");
            TestAssert.True(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == revenant.NetId &&
                    hit.TargetNetId == enemy.NetId && hit.WindupRemaining > 0.0),
                $"{team}: revenant is winding up on the hero");

            enemy.Position = brush;
            runtime.Delivery.Flush();
            match.Clear();
            var damage = enemy.MatchStatistics.TotalDamageTaken;
            Decide();
            runtime.Delivery.Flush();
            TestAssert.Equal((uint?)null, revenant.AttackTarget,
                $"{team}: revenant releases the hero hidden in brush");
            TestAssert.False(runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == revenant.NetId),
                $"{team}: the lost target retires the unlaunched windup");
            var packets = match.Captures[0].Packets;
            var stopAttack = packets.FindIndex(packet => Opcode(packet) == 0x39 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == revenant.NetId);
            var waypoint = packets.FindIndex(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == revenant.NetId);
            TestAssert.True(stopAttack >= 0 && waypoint > stopAttack,
                $"{team}: 0x39 stops the client attack before the revenant's stop 0xC1 " +
                $"(0x39 at {stopAttack}, 0xC1 at {waypoint})");
            runtime.Tick(1.0);
            TestAssert.Near(damage, enemy.MatchStatistics.TotalDamageTaken, 1e-9,
                $"{team}: no revenant damage lands on the unseen hero");
        }
    }

    [Check("pets-live-yorick-ghoul-releases-unseen-target")]
    public static void GhoulAcquisitionAndRetentionUseTeamSight()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            var (outside, brush) = BrushEdge(runtime.Navigation);
            owner.Position = outside;
            enemy.Position = brush;
            var ghoul = YorickScriptSupport.SpawnUnit(runtime, owner, "Ravenous", outside,
                runtime.Combat.CurrentTime);
            // No YorickSummon* buff drives this ghoul: it acquires on its 0.15 s TimerFindEnemies grid, one step per call here.
            var now = runtime.Combat.CurrentTime;
            var vision = new WorldMinionVisionService(runtime.World, runtime.Navigation,
                _ => Array.Empty<VisionProvider>());
            TestAssert.False(vision.HasBrushVision(team, enemy),
                $"{team}: ghoul's enemy starts hidden behind the brush edge");
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul does not acquire or route toward the unseen enemy");

            owner.Position = brush;
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)enemy.NetId, ghoul.AttackTarget,
                $"{team}: allied brush sight permits ghoul acquisition");

            owner.Position = outside;
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul releases the hidden retained target");

            owner.Position = brush;
            var stealth = runtime.SpellBuffs.Add(enemy, "GhoulSightProbe", BuffType.Invisibility,
                10.0, enemy.NetId, new BuffPayload { Stealthed = true }) ??
                throw new InvalidOperationException("Ghoul stealth probe was not applied.");
            TestAssert.True(vision.HasBrushVision(team, enemy),
                $"{team}: brush-only sight still sees the stealthed enemy");
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul cannot acquire the stealthed enemy");

            runtime.SpellBuffs.Remove(enemy, stealth.Slot);
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)enemy.NetId, ghoul.AttackTarget,
                $"{team}: ghoul acquires the enemy after destealth");

            var stealthAgain = runtime.SpellBuffs.Add(enemy, "GhoulSightProbeAgain",
                BuffType.Invisibility, 10.0, enemy.NetId,
                new BuffPayload { Stealthed = true }) ??
                throw new InvalidOperationException("Ghoul stealth retention probe was not applied.");
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul releases the target when stealth returns");
            runtime.SpellBuffs.Remove(enemy, stealthAgain.Slot);

            var opaque = runtime.Navigation.CellToWorld(new NavCell(128, 100));
            var clear = runtime.Navigation.CellToWorld(new NavCell(127, 100));
            TestAssert.True(runtime.Navigation.BrushIdAt(opaque) is null &&
                            !runtime.Navigation.HasVisionLos(clear, opaque) &&
                            runtime.Navigation.IsMovementWalkable(clear) &&
                            !VisibilityRules.ProviderReveals(runtime.Dependencies.VisionEnvironment,
                                // Batch 253: from the clear neighbour; the client's walk sees a target at the viewer's own point.
                                new VisionProvider(clear, 400), opaque),
                $"{team}: shipped adjacent nonbrush cells isolate opaque vision");
            owner.Position = clear;
            ghoul.Position = clear;
            enemy.Position = opaque;
            TestAssert.True(vision.HasBrushVision(team, enemy),
                $"{team}: old brush-only predicate incorrectly accepts opaque target");
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul cannot acquire through opaque terrain");
            enemy.Position = clear;
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)enemy.NetId, ghoul.AttackTarget,
                $"{team}: ghoul acquires when target moves into clear sight");
            enemy.Position = opaque;
            runtime.Pets.Tick(now += 0.15, 0);
            TestAssert.Equal((uint?)null, ghoul.AttackTarget,
                $"{team}: ghoul drops retained target behind opaque terrain");
        }
    }

    [Check("minions-live-help-retains-team-attack-reveal-through-brush")]
    public static void MinionHelpUsesTeamAttackRevealUntilItExpires()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in Enum.GetValues<DamageSourceKind>())
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var victim = match.Hero(0);
            var attacker = match.Hero(1);
            var (outside, brush) = BrushEdge(runtime.Navigation);
            victim.Position = outside;
            attacker.Position = brush;
            attacker.AttackRange = 500.0;
            attacker.AttackCooldown = 0.0;
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, team, outside)
            {
                Name = "HelpMelee", MinionType = MinionType.Melee,
                Health = 500, MaxHealth = 500, AttackRange = 125,
                AcquisitionRange = 450, Radius = 35, Speed = 0,
                Created = true, Visible = true,
            });
            var vision = new WorldMinionVisionService(runtime.World, runtime.Navigation,
                _ => Array.Empty<VisionProvider>());
            TestAssert.False(vision.HasBrushVision(team, attacker),
                $"{team}: attacker begins hidden at the brush edge");
            TestAssert.True(runtime.Combat.QueueBasicAttack(attacker, victim),
                $"{team}: real basic attack starts the team-specific reveal");
            // Batch 49 (1ccf9f7): the reveal runs at the cast frame, the end of the windup, not when the swing starts.
            TestAssert.Near(0.0, attacker.AttackRevealRemaining[(int)team], 0.0,
                $"{team}: a swing still in its windup reveals nothing");
            runtime.Combat.CombatTick(attacker.AttackWindup, regenerate: false);
            TestAssert.True(attacker.AttackRevealRemaining[(int)team] > 0.0,
                $"{team}: attack reveal is active for the victim team");
            TestAssert.Near(0.0, attacker.AttackRevealRemaining[(int)attacker.Team], 0.0,
                $"{team}: attack reveal does not grant the attacker's own team a slot");
            var current = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, attacker.Team, outside)
            {
                Health = 500, MaxHealth = 500, Radius = 35, Created = true, Visible = true,
            });
            var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
                EntityKind.Turret, UnitType.Turret, team, outside)
            {
                Health = 1000, MaxHealth = 1000, AttackRange = 775,
                Created = true, Visible = true,
            });
            minion.AttackTarget = current.NetId;
            minion.MinionState = MinionAiState.Engage;
            turret.AttackTarget = current.NetId;
            runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                DamageType.True, kind, isReflect: true);
            TestAssert.Equal((uint?)null, minion.HelpTarget, "reflected damage still emits no lane help");
            TestAssert.Equal((uint?)current.NetId, turret.AttackTarget, "reflection still emits no turret help");
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                DamageType.Magic, kind), $"{team}/{kind}: visible damage lands");
            // playtest B4 #3 review, 27 Sept 2026: single-target spells only (2011 wiki).
            // AoE, persistent and proc damage reverse the earlier blanket exclusion.
            TestAssert.Equal((uint?)attacker.NetId, turret.AttackTarget,
                $"{team}/{kind}: turret retaliation is preserved");
            if (kind == DamageSourceKind.Spell)
            {
                TestAssert.Equal((uint?)null, minion.HelpTarget,
                    $"{team}/{kind}: no lane-minion help event");
                TestAssert.Equal(0.0, victim.LaneHelpCallRemaining,
                    "excluded damage does not arm the lane cooldown");
                TestAssert.Equal(1.0, victim.HelpCallRemaining, "turret cooldown is armed independently");
                runtime.Minions!.Tick(1.0 / 30.0);
                TestAssert.Equal((uint?)current.NetId, minion.AttackTarget,
                    $"{team}/{kind}: visible spell damage keeps the living minion target");
                TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                    DamageType.Physical, DamageSourceKind.Basic),
                    $"{team}: basic within one second still calls for lane help without resetting timers");
            }
            TestAssert.Equal((uint?)attacker.NetId, minion.HelpTarget,
                $"{team}/{kind}: qualifying damage nominates the attacking hero");
            TestAssert.Equal(1.0, victim.LaneHelpCallRemaining, "qualifying damage arms the lane cooldown");
            TestAssert.True(vision.HasBrushVision(team, attacker),
                $"{team}: the reveal provider passes brush and LOS checks");
            runtime.Minions!.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)attacker.NetId, minion.AttackTarget,
                $"{team}: minion acquires the revealed help target");
            TestAssert.Equal((uint?)null, minion.HelpTarget,
                $"{team}: a help request is consumed on its first decision");
            runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic);
            TestAssert.Equal((uint?)null, minion.HelpTarget, "another basic within the lane cooldown emits no event");
            runtime.Minions.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)attacker.NetId, minion.AttackTarget,
                $"{team}: retention agrees with help acquisition");

            current.Protected = true; // isolate loss with no ordinary replacement
            attacker.AttackRevealRemaining[(int)team] = 0.0;
            TestAssert.False(vision.HasBrushVision(team, attacker),
                $"{team}: the hidden target loses team sight after reveal");
            runtime.Minions.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)null, minion.AttackTarget,
                $"{team}: minion releases the target when reveal expires");

            runtime.Combat.RevealAttack(attacker, victim);
            runtime.Minions.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)null, minion.AttackTarget,
                $"{team}: restored sight cannot replay the consumed help before the next scan");
            attacker.AttackRevealRemaining[(int)team] = 0.0;

            // Playtest B4 #3, 27 Sept 2026: reverse the old assertion that
            // even an unseen spell caster nominated a minion help target.
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                DamageType.Magic, DamageSourceKind.Spell),
                $"{team}: hidden spell damage lands without a basic reveal");
            TestAssert.Equal((uint?)null, minion.HelpTarget);
            runtime.Minions.Tick(1.0 / 30.0);
            TestAssert.Equal((uint?)null, minion.AttackTarget,
                $"{team}: unrevealed spell caster is not acquired through help");
        }
    }

    [Check("minions-live-help-rearms-anti-kite-only-on-new-event")]
    public static void MinionHelpRearmsAntiKiteOnlyOnNewEvent()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var victim = match.Hero(0);
        var attacker = match.Hero(1);
        victim.Position = new Position2(7000, 7000);
        attacker.Position = new Position2(7300, 7000);
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, victim.Team, victim.Position)
        {
            MinionType = MinionType.Melee, Health = 500, MaxHealth = 500,
            AttackRange = 110, AcquisitionRange = 450, Radius = 35, Speed = 325,
            Created = true, Visible = true,
        });
        void Help()
        {
            victim.LaneHelpCallRemaining = 0.0;
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1,
                DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.Equal((uint?)attacker.NetId, minion.HelpTarget);
            runtime.Minions!.Tick(.01);
            TestAssert.Equal((uint?)null, minion.HelpTarget);
        }

        // Advance decisions without movement so this stays an out-of-range chase.
        Help();
        runtime.Minions!.Tick(3.5);
        Help();
        runtime.Minions.Tick(.75);
        TestAssert.Equal((uint?)attacker.NetId, minion.AttackTarget,
            "a genuine same-target help event rearms the four-second pursuit timer");
        runtime.Minions.Tick(3.3);
        TestAssert.Equal((uint?)null, minion.AttackTarget,
            "the consumed event cannot keep rearming anti-kite without another hit");
    }

    private static (Position2 Outside, Position2 Brush) BrushEdge(NavigationGrid grid)
    {
        var offsets = new[]
        {
            new Position2(grid.CellSize, 0), new Position2(-grid.CellSize, 0),
            new Position2(0, grid.CellSize), new Position2(0, -grid.CellSize),
        };
        // Playtest #10 (25 Sept 2026): brush membership follows each unit's
        // SelectionRadius, so both points must hold for every radius these
        // checks place there (point providers, ghouls, heroes and pets).
        double[] radii = [-1.0, 75.0, 100.0, 125.0, 135.0, 155.0, 165.0];
        foreach (var index in Enumerable.Range(0, grid.BrushLabels.Count)
                     .Where(index => grid.BrushLabels[index] != 0)
                     .OrderBy(index => Distance(grid.CellToWorld(new NavCell(
                         index % grid.CountX, index / grid.CountX)), new Position2(7000, 7000))))
        {
            var brush = grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX));
            var brushId = grid.BrushIdAt(brush);
            if (!grid.IsMovementWalkable(brush) ||
                radii.Any(radius => grid.BrushIdAt(brush, radius) != brushId)) continue;
            for (var cells = 1; cells <= 3; cells++)
            foreach (var offset in offsets)
            {
                var outside = brush + new Position2(offset.X * cells, offset.Z * cells);
                if (grid.IsMovementWalkable(outside) && grid.HasMovementLineOfSight(outside, brush) &&
                    radii.All(radius => grid.BrushIdAt(outside, radius) is null) &&
                    !grid.BrushAllowsVision(outside, brush))
                    return (outside, brush);
            }
        }
        throw new InvalidOperationException("Map1 has no walkable brush edge for AI vision checks.");
    }
}
