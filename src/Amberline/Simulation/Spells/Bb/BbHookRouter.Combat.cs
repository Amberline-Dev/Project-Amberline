using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Combat event hooks (wave 3, W31b, E07 and E12): <c>BuffOnBeingHit</c> / <c>CharOnBeingHit</c> (a basic attack lands on the holder),
/// <c>CharOnPreDamage</c> (the damage the hero is about to take), <c>BuffOnDealDamage</c> (the damage the holder is about to deal), <c>CharOnMiss</c> /
/// <c>CharOnDodge</c> (an attack misses or is dodged), <c>CharOnNearbyDeath</c> (a unit dies near a hero) and <c>BuffOnLaunchAttack</c> /
/// <c>BuffOnLaunchMissile</c> of a basic attack. Every moment is a Reconstruction: the stock client has none of the hook names (the server dispatches
/// them), so each rule follows from what the shipped scripts read and write, as the hook table in docs/scripting/building-blocks-engine.md explains.
/// </summary>
internal sealed partial class BbHookRouter
{
    // ------------------------------------------------------------------------------------------------------------
    // BuffOnBeingHit / CharOnBeingHit
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CharOnBeingHit</c> of the victim's character script, then <c>BuffOnBeingHit</c> of its script buffs, for a basic attack that landed on it:
    /// Owner is the victim (the holder), Attacker the unit that hit, <c>DamageAmount</c> the hit before mitigation (the scripts change it: Sion's
    /// Feel No Pain takes a block off a hit, Pantheon's Aegis and the Silent Killer's halving of minion damage rewrite it; Tantrum, Defensive Ball
    /// Curl, Paragon and Deadly Venom's counter only react), <c>DamageType</c> and <c>HitResult</c> (HIT_Normal or HIT_Critical). The hook runs
    /// before the raw damage is kept for the items and recap, as the hand-written Harden Skin block (<c>Talents/113</c>, also a
    /// <c>CharOnBeingHit</c> script) and Thornmail's reflection (<c>DamageAmount</c> before armor) are staged. Reconstruction: a miss or a dodge reaches no
    /// BeingHit hook (the same ruling as the on-hit hooks: 4 of the 10 shipped tables guard on HIT_Miss and HIT_Dodge, so running them on an avoided swing
    /// would trigger the other 6), and only basic attacks come here (Sion tests <c>DAMAGESOURCE_ATTACK</c> himself; spells have the damage hooks).
    /// </summary>
    internal double RunBeingHit(Entity target, Entity attacker, double amount, DamageType type, BasicAttackOutcome outcome)
    {
        if (CharacterOf(target) is { } script && script.Event("CharOnBeingHit") is not null && Owns(target))
        {
            var scope = NewScope(target, attacker, target, target, null, CharInstanceVars(target));
            PrepareHit(scope, amount, type, outcome);
            var frame = new BbHookFrame("CharOnBeingHit", script.Name, target)
            {
                Caster = target, DamageSource = DamageSourceKind.Basic, InDamageHook = true,
            };
            Run(script, "CharOnBeingHit", scope, frame);
            amount = ChangedHit(frame, scope, amount);
        }
        foreach (var instance in Buffs.WithHook(target, "BuffOnBeingHit"))
        {
            if (!target.Buffs.Contains(instance.State)) continue; // an earlier hook of this hit removed it
            var scope = NewScope(target, attacker, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            PrepareHit(scope, amount, type, outcome);
            var frame = BuffFrame(instance, "BuffOnBeingHit", DamageSourceKind.Basic, inDamage: true);
            Run(instance.Script, "BuffOnBeingHit", scope, frame);
            amount = ChangedHit(frame, scope, amount);
        }
        return amount;
    }

    /// <summary>The hit variables a BeingHit or HitUnit hook reads (<c>BbItemModule</c> fills the same three for
    /// <c>ItemOnHitUnit</c> / <c>ItemOnBeingHit</c>, which is the same callback with the inventory copy as Owner).</summary>
    internal static void PrepareHit(BbScope scope, double amount, DamageType type, BasicAttackOutcome outcome)
    {
        scope.Vars["DamageAmount"] = amount;
        scope.Vars["DamageType"] = DamageTypeNumber(type);
        scope.Vars["HitResult"] = HitResultOf(outcome);
    }

    public double OnBeingHit(Entity target, Entity attacker, double amount, DamageType type, BasicAttackOutcome outcome)
    {
        var result = amount;
        Guard("sink", "OnBeingHit", () => result = RunBeingHit(target, attacker, amount, type, outcome));
        return result;
    }

    // ------------------------------------------------------------------------------------------------------------
    // CharOnPreDamage
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CharOnPreDamage</c> of the hero about to take damage, after its script buffs' <c>BuffOnPreDamage</c> (the slot after resistance and before the
    /// shields and the health bar): Owner is the hero, Attacker the damage source, <c>DamageAmount</c> the damage and <c>DamageType</c> its kind, and the
    /// hook may change the amount (Olaf will not let his own cost kill him: it stops one point short of his health; Malphite marks that he was hit).
    /// The character script comes last so that it sees what the shields left, the order <c>CharOnPreDealDamage</c> follows after its buffs.
    /// </summary>
    private double RunCharPreDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind)
    {
        if (CharacterOf(target) is not { } script || script.Event("CharOnPreDamage") is null || !Owns(target)) return amount;
        var scope = NewScope(target, source, target, target, null, CharInstanceVars(target));
        scope.Vars["DamageAmount"] = amount;
        scope.Vars["DamageType"] = DamageTypeNumber(type);
        var frame = new BbHookFrame("CharOnPreDamage", script.Name, target)
        {
            Caster = target, DamageSource = kind, InDamageHook = true,
        };
        Run(script, "CharOnPreDamage", scope, frame);
        return ChangedHit(frame, scope, amount);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BuffOnDealDamage
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BuffOnDealDamage</c> of the script buffs of the unit that deals damage, once the pre-deal hooks have run (before mitigation): Owner and Attacker
    /// are the holder, Target the victim, <c>DamageAmount</c> and <c>DamageType</c> the damage, which the hook may change (Sona's Power Chord takes a fifth
    /// of the non-true damage off the unit it marks, as Exhaust does in <c>BuffOnPreDealDamage</c>). Any unit holds such a buff (a marked minion
    /// too), so the hook is not limited to heroes. Evelynn's Shadow Walk and the call-for-help manager only react to the damage.
    /// </summary>
    internal double RunDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        foreach (var instance in Buffs.WithHook(source, "BuffOnDealDamage"))
        {
            if (!source.Buffs.Contains(instance.State)) continue;
            var scope = NewScope(source, source, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["DamageAmount"] = amount;
            scope.Vars["DamageType"] = DamageTypeNumber(type);
            var frame = BuffFrame(instance, "BuffOnDealDamage", kind, inDamage: true);
            Run(instance.Script, "BuffOnDealDamage", scope, frame);
            amount = ChangedHit(frame, scope, amount);
        }
        return amount;
    }

    public double OnDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind)
    {
        var result = amount;
        Guard("sink", "OnDealDamage", () => result = RunDealDamage(source, target, amount, type, kind));
        return result;
    }

    // ------------------------------------------------------------------------------------------------------------
    // CharOnMiss / CharOnDodge
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CharOnMiss</c> of the attacker whose basic attack missed (blind or dodged) and <c>CharOnDodge</c> of the hero that dodged it. Owner is the
    /// hero the hook belongs to, Attacker the unit that swung, Target the unit the swing was aimed at (Cho'Gath's Vorpal Spikes fire at it: he casts the
    /// spike spell from <c>CharOnMiss</c>; Jax's Counter Strike unlocks itself in <c>CharOnDodge</c>). Reconstruction: the moment is the impact of the
    /// avoided swing, where the client shows the miss, not the roll at its start.
    /// </summary>
    internal void RunAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome)
    {
        // One character script per unit: the attacker's owns CharOnMiss, the dodger's CharOnDodge (they differ when a bound
        // unit swings at a hero of a script-driven champion, batch 159 step 2).
        if (CharacterOf(attacker) is { } swing && swing.Event("CharOnMiss") is not null && Owns(attacker))
        {
            var scope = NewScope(attacker, attacker, target, attacker, null, CharInstanceVars(attacker));
            scope.Vars["HitResult"] = HitResultOf(outcome);
            Run(swing, "CharOnMiss", scope, new BbHookFrame("CharOnMiss", swing.Name, attacker) { Caster = attacker });
        }
        if (outcome == BasicAttackOutcome.Dodge && CharacterOf(target) is { } dodge && dodge.Event("CharOnDodge") is not null && Owns(target))
        {
            var scope = NewScope(target, attacker, attacker, target, null, CharInstanceVars(target));
            scope.Vars["HitResult"] = HitResultOf(outcome);
            Run(dodge, "CharOnDodge", scope, new BbHookFrame("CharOnDodge", dodge.Name, target) { Caster = target });
        }
    }

    public void OnBasicAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome) =>
        Guard("sink", "OnBasicAttackAvoided", () => RunAttackAvoided(attacker, target, outcome));

    // ------------------------------------------------------------------------------------------------------------
    // CharOnNearbyDeath
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CharOnNearbyDeath</c> of every living hero of this champion near a unit that died (Alistar's Trample cooldown, Trundle's heal from an enemy
    /// unit): Owner is that hero, Attacker the unit that got the kill, Target the unit that died. Reconstruction: "nearby" is the experience-sharing
    /// range of the dead unit (the same units that would share its experience, <see cref="CombatService.ExperienceShareRadius"/> for a unit, the Map1
    /// <c>ai_ExpRadius2</c> for a champion, the neutral share radius for a monster), the only nearby-death range the server has; the hero itself is not
    /// near its own death.
    /// </summary>
    internal void RunNearbyDeath(Entity dead, uint killerNetId)
    {
        var radius = dead.UnitType == UnitType.Hero ? CombatService.ChampionExperienceShareRadius
            : dead.Kind == EntityKind.Neutral ? CombatService.NeutralExperienceShareRadius : CombatService.ExperienceShareRadius;
        var killer = _host.Spells.World.Get(killerNetId);
        foreach (var hero in _host.Spells.World.Heroes().Where(hero => Owns(hero) && hero.Alive && !ReferenceEquals(hero, dead) &&
                     DistanceSquared(hero.Position, dead.Position) <= radius * radius).ToArray())
        {
            // One hero at a time: a champion module's heroes share one script, a bound unit's is its own (batch 159 step 2).
            if (CharacterOf(hero) is not { } script || script.Event("CharOnNearbyDeath") is null) continue;
            var scope = NewScope(hero, killer, dead, hero, null, CharInstanceVars(hero));
            Run(script, "CharOnNearbyDeath", scope, new BbHookFrame("CharOnNearbyDeath", script.Name, hero) { Caster = hero });
        }
    }

    private static double DistanceSquared(Position2 a, Position2 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    // ------------------------------------------------------------------------------------------------------------
    // BuffOnLaunchAttack / BuffOnLaunchMissile of a basic attack
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CharOnLaunchAttack</c> and then <c>BuffOnLaunchAttack</c> of the attacking hero's script buffs, then <c>BuffOnLaunchMissile</c> when the attack has a missile and a script's attack
    /// override made it (see below), at the moment the
    /// swing's windup ends and the attack leaves (the cast frame): Owner and Attacker are the holder, Target the unit attacked; <c>missileId</c> is the
    /// attack missile's net id. Twitch's Hide in Shadows, Lux's Prismatic Barrier stealth and the Monkey King's decoy end their stealth at it, Teemo's
    /// Camouflage restarts its two seconds, Ashe's Frost Shot pays its mana per attack and Twitch's Spray and Pray counts its attacks. The attack
    /// launch comes first, so a stealth that ends does not see the missile it launched. The shipped Spells/Sheen.luaobj and Spells/LichBane.luaobj
    /// name their launch hook <c>CharOnLaunchAttack</c> although both are buff scripts (the same way their hit hook is <c>BuffOnHitUnit</c>), and it
    /// adds the spellblade's <c>SheenDelay</c>; it runs first, beside the character hook of that name, before the buff hooks.
    /// </summary>
    internal void RunAttackLaunch(Entity attacker, Entity target, uint missileNetId)
    {
        // Batch 348: the cast routine spends the swing's cost first, then the cast leaves; see
        // OverrideSwingCost. The PAR's clamp at zero covers a drain inside the windup.
        if (Owns(attacker) && _swingCosts.Remove(attacker.NetId, out var cost))
        {
            attacker.Mana = Math.Max(0.0, attacker.Mana - cost);
            _host.Spells.SendChampionStats(attacker);
        }
        foreach (var instance in Buffs.WithHook(attacker, "CharOnLaunchAttack"))
        {
            if (!attacker.Buffs.Contains(instance.State)) continue;
            var scope = NewScope(attacker, attacker, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            Run(instance.Script, "CharOnLaunchAttack", scope,
                BuffFrame(instance, "CharOnLaunchAttack", DamageSourceKind.Basic, inDamage: false));
        }
        foreach (var instance in Buffs.WithHook(attacker, "BuffOnLaunchAttack"))
        {
            if (!attacker.Buffs.Contains(instance.State)) continue;
            var scope = NewScope(attacker, attacker, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            Run(instance.Script, "BuffOnLaunchAttack", scope, BuffFrame(instance, "BuffOnLaunchAttack", DamageSourceKind.Basic, inDamage: false));
        }
        // Reconstruction: the missile hook of an attack reaches only an attack that a script's override made (the swings of Frost Shot and of Spray and Pray, the
        // only shipped users). Retail probably raises it for every attack missile, but Orianna's ball buffs keep the id of their missile in CharVars and a
        // plain auto attack of hers that leaves during the ball's flight would replace it (measured by S-engine-w3: the id changed and the ball could no longer
        // be found by the scripts that destroy it).
        if (attacker.MissileSpeed <= 0.0 || attacker.ScriptAttackOverride is null) return;
        foreach (var instance in Buffs.WithHook(attacker, "BuffOnLaunchMissile"))
        {
            if (!attacker.Buffs.Contains(instance.State)) continue;
            var scope = NewScope(attacker, attacker, target, _host.Spells.World.Get(instance.CasterNetId),
                instance.SpellRank >= 1 ? instance.SpellRank : null, instance.InstanceVars);
            scope.Vars["missileId"] = (double)missileNetId;
            Run(instance.Script, "BuffOnLaunchMissile", scope, new BbHookFrame("BuffOnLaunchMissile", instance.Name, instance.Owner)
            {
                Caster = _host.Spells.World.Get(instance.CasterNetId), SpellSlot = instance.SpellSlot, SpellRank = instance.SpellRank,
                ManaCost = instance.ManaCost, Buff = instance, MissileNetId = missileNetId,
            });
        }
    }

    public void OnBasicAttackLaunched(Entity attacker, Entity target, uint missileNetId) =>
        Guard("sink", "OnBasicAttackLaunched", () => RunAttackLaunch(attacker, target, missileNetId));
}
