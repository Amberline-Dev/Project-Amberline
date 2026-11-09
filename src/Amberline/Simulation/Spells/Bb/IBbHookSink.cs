using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The server-side hooks the production simulation forwards to the script engine (see MatchGameplayHooks.ScriptHooks).
/// Production leaves the sink null: the ten hand-written champions run exactly as before.
/// </summary>
public interface IBbHookSink
{
    /// <summary>One <c>BuffLifecycle.TickBuffs</c> update of a buff (BuffOnUpdateActions).</summary>
    void OnBuffTick(Entity unit, BuffState buff, double dt);

    /// <summary>The timer of an ammo buff (<c>BUFF_AmmoStack</c>) ran out: <c>BuffOnUpdateAmmo</c> of its script, which adds the next charge (E14).</summary>
    void OnAmmoTimer(Entity unit, BuffState buff) { }

    /// <summary>
    /// The damage a unit is about to take, after resistance and before it reaches the health bar (BuffOnPreDamage); returns the amount
    /// to use. The shipped shields (Black Shield, Hexdrinker, Mana Barrier) subtract their hit points from it and check it against the
    /// unit's current health (YorickRAZombie), which only makes sense for the damage that will actually be taken.
    /// </summary>
    double OnPreDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind);

    /// <summary>
    /// The damage a unit is about to take, before resistance (BuffOnPreMitigationDamage: the Jack-in-the-Box stealth immunity, the Yorick
    /// ghoul and revenant reductions of spell area damage); returns the amount to use.
    /// </summary>
    double OnPreMitigationDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? kind) => amount;

    /// <summary>
    /// A unit took damage (BuffOnTakeDamage, after the health was taken): <paramref name="amount"/> is the damage that reached the health bar.
    /// Sleep removes itself on any non-periodic hit; the other shipped users read the amount.
    /// </summary>
    void OnTakeDamage(Entity target, Entity? source, double amount, DamageSourceKind? kind, bool periodic) { }

    /// <summary>
    /// A buff is about to be added to <paramref name="target"/> (BuffOnAllowAdd): false when a script buff on it refuses the add. Asked for every add
    /// whoever makes it, a script (any champion's), a native control (BBApplyStun and its siblings), an item or a summoner spell, because the buff
    /// that refuses (Black Shield, Banshee's Veil, Spell Shield, the wards) belongs to a module other than the adder's.
    /// <paramref name="type"/> is the BUFF_* number of the incoming buff the shipped hooks compare with.
    /// </summary>
    bool AllowBuffAdd(Entity target, Entity adder, string name, ref double duration, double type, double maxStack) => true;

    /// <summary>A unit died (BuffOnDeath); <paramref name="killerNetId"/> is the unit that dealt the killing damage.</summary>
    void OnUnitDeath(Entity unit, uint killerNetId);

    /// <summary>
    /// The death's second half, after the kill credit (<c>BuffOnKill</c>, <c>BuffOnAssist</c>): the victim's buffs without PersistsThroughDeath go.
    /// Reconstruction: the shipped files do not state the retail order of this removal against the kill credit. The shipped Detonating Shot's
    /// <c>BuffOnKill</c> tests a mark that only the hit before the kill sets on the victim, which works only while the victim still carries its buffs.
    /// </summary>
    void OnUnitDeathReleased(Entity unit) { }

    /// <summary>
    /// The unit that is about to die: the script buffs that have a <c>BuffOnZombie</c> hook run their <c>BuffOnDeath</c> (once, here instead of
    /// at the death hook) and true comes back when one of them set <c>BecomeZombie</c> (Yorick's <c>YorickReviveAllySelf</c>).
    /// </summary>
    bool OnDeathBecomesZombie(Entity unit, uint killerNetId) => false;

    /// <summary>The unit that died into a zombie life stands up: <c>BuffOnZombie</c> of the buffs that chose it.</summary>
    void OnZombie(Entity unit) { }

    /// <summary><c>BBForceDead</c> of a unit: the server's <c>RemoveNonPersistantBuffs</c> for the script buffs (no <c>BuffOnDeath</c>: the first death ran it).</summary>
    void OnForceDead(Entity unit) { }

    /// <summary>
    /// <c>BuffOnHeal</c> of the script buffs of a unit that receives a heal: the hook reads the heal as <c>Health</c> and returns what the unit
    /// really gets with <c>BBSetReturnValue</c>; the answer is that amount (unchanged when no buff has the hook).
    /// </summary>
    double OnHeal(Entity unit, double amount) => amount;

    /// <summary>A hero gained a level (SetVarsByLevel, CharOnLevelUp and BuffOnLevelUp).</summary>
    void OnLevelChanged(Entity hero);

    /// <summary>A hero that died is alive again at its fountain (CharOnResurrect and BuffOnResurrect).</summary>
    void OnHeroRespawn(Entity hero) { }

    /// <summary>A hero's player dropped out of a live match (CharOnDisconnect, BuffOnDisconnect and the DisconnectTimer).</summary>
    void OnHeroDisconnect(Entity hero) { }

    /// <summary>A hero's player came back and the seat is ready again (CharOnReconnect and BuffOnReconnect).</summary>
    void OnHeroReconnect(Entity hero) { }

    /// <summary>A hero started a champion spell (CharOnSpellCast and BuffOnSpellCast).</summary>
    void OnSpellCast(Entity hero, int slot, string spellName);

    /// <summary>
    /// The same cast hooks for a cast the engine names rather than a champion slot — a summoner slot or <c>Recall</c> —
    /// with the unit it is aimed at. The rank is pinned to 0 (the summoner slots collide with champion slots 0..3, so
    /// <c>SpellRanks</c> does not describe them). Every bound module answers, not only the one whose slot holds the
    /// cast: the buffs that end on a cast live on another module's runtime. Shaco's <c>Deceive</c> and Vayne's
    /// <c>VayneTumbleFade</c> are the ones that matter here — both are the casting hero's own spell buffs on the
    /// champion module, while the summoner slots run on the summoner module's own <c>BbBuffRuntime</c>. Their
    /// <c>BuffOnSpellCast</c> is what ends the stealth (Spells/Deceive.luaobj and Spells/VayneTumbleFade.luaobj).
    /// </summary>
    void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target);

    /// <summary>
    /// A hero's landed swing is about to be resolved (the pre-mitigation raw damage of its basic attack): when the hero's attack was fired from
    /// a script's override spell whose own primary attack hit differs from a plain swing, the amount is changed by the difference.
    /// </summary>
    double OnAttackOverrideDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome) => damage;

    /// <summary>
    /// True when the hero's attack override passes <c>IgnoreDamageCrit</c> on its primary attack hit (Takedown): the swing is rolled without a
    /// critical chance, since the script's damage is never multiplied by one.
    /// </summary>
    bool AttackOverrideIgnoresCrit(Entity attacker, Entity target) => false;

    /// <summary>
    /// <c>IgnoreDamageIncreaseMods</c> of the primary attack hit <see cref="OnAttackOverrideDamage"/> just read (Takedown passes it): the swing
    /// is dealt without the attacker's damage-increase modifiers. Reading clears the answer.
    /// </summary>
    bool TakeAttackOverrideIgnoresIncreaseMods() => false;

    /// <summary>
    /// The result <see cref="OnAttackOverrideDamage"/> just found the attack script left in <c>HitResult</c> at its primary hit, when it differs
    /// from the swing's roll (Pantheon's Certain Death makes a normal swing critical); null when it agrees. Reading clears the answer.
    /// </summary>
    BasicAttackOutcome? TakeScriptedHitResult() => null;

    /// <summary>
    /// A unit a script spawned with an AI script of its own (<see cref="Entity.AiScript"/>) landed a swing: its character's attack spell has a script whose
    /// <c>TargetExecute</c> is the hit (Heimerdinger's turrets, Malzahar's voidlings), and runs now. True when it ran, so that the swing's own damage is not
    /// dealt as well; false when the unit's owner is not this sink's champion or its attack has no script.
    /// </summary>
    bool OnUnitAttackHit(Entity unit, Entity target) => false;

    /// <summary>A hero is about to swing (before the attack packet): an attack override that costs mana is paid here.</summary>
    void OnBasicAttackStart(Entity attacker, Entity target);

    /// <summary>
    /// A hero is about to send a normal or critical swing: true when the sink sent it itself, as a cast of the attack-override spell
    /// flagged as an auto attack (a chain attack), so that the caller sends no 0x0D.
    /// </summary>
    bool SendAttackAsCast(Entity attacker, Entity target, uint missileNetId) => false;

    /// <summary>The swing's packet and missile went out: a chain attack announces its hops to the client.</summary>
    void OnBasicAttackSent(Entity attacker, Entity target, uint missileNetId);

    /// <summary>
    /// A hero's basic attack landed: the attack spell's <c>TargetExecute</c> (an override's or the character's own) runs.
    /// <paramref name="rawDamage"/> is the hit before mitigation. <paramref name="attackOverride"/> is the launch-time flag: the attack was
    /// fired from a script's override spell, whose <c>TargetExecute</c> runs first. <c>CharOnHitUnit</c> and <c>BuffOnHitUnit</c> do not run here:
    /// they run as the hit is resolved, <see cref="ModifyBasicAttackDamage"/>.
    /// </summary>
    /// <paramref name="outcome"/> is the swing's result, which the scripts read as <c>HitResult</c>.
    void OnBasicAttackHit(Entity attacker, Entity target, double rawDamage, bool attackOverride, BasicAttackOutcome outcome);

    /// <summary>
    /// A hero's basic attack is resolving (<c>CharOnHitUnit</c> and <c>BuffOnHitUnit</c>), before mitigation: returns the damage the hit deals after
    /// the scripts, which read the hit as <c>DamageAmount</c> and may change it (Shaco's Backstab multiplies it by 1.2 from behind, Sheen adds its
    /// bonus, Mordekaiser's Mace of Spades takes it away to deal its own). The default leaves the hit alone.
    /// </summary>
    double ModifyBasicAttackDamage(Entity attacker, Entity target, double damage, bool attackOverride, BasicAttackOutcome outcome) => damage;

    /// <summary>
    /// A hero's attack missile reached the corpse of its target (it died in flight). Nothing is hit, but a chain attack
    /// (<paramref name="attackOverride"/>, the launch-time flag) goes on from there like the client's chain missile.
    /// </summary>
    void OnBasicAttackOnCorpse(Entity attacker, Entity corpse, bool attackOverride) { }

    /// <summary>A hero is about to deal damage, before mitigation (CharOnPreDealDamage); returns the amount to use.</summary>
    double OnPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind);

    /// <summary>The damage a hero deals, after mitigation (BuffOnPreDealDamage of its script buffs); returns the amount to use.</summary>
    double OnBuffPreDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind) => amount;

    /// <summary>
    /// An enemy spell lands on <paramref name="target"/> (BuffOnBeingSpellHit); true when one of the target's script buffs
    /// absorbs it (a spell shield that took the hit), which the combat gate treats like Banshee's Veil.
    /// </summary>
    bool OnBeingSpellHit(Entity? source, Entity target, DamageSourceKind? kind);

    /// <summary>
    /// <c>BBBreakSpellShields</c> on <paramref name="target"/>: the marker add (<c>BuffOnAllowAdd</c> with the duration 37037) of every
    /// script buff the unit holds. A shield pops whatever spell is running, so the running spell's flags are not asked. True when one
    /// of them popped.
    /// </summary>
    bool OnBreakSpellShields(Entity target) => false;

    /// <summary>
    /// True while a script shield of <paramref name="target"/> has popped (its <c>BuffOnBeingSpellHit</c> set <c>WillRemove</c>) and has
    /// not left yet: the shipped shield keeps WillRemove until its <c>BuffOnUpdateStats</c> swaps it out at the next 0.25 s poll.
    /// </summary>
    bool ShieldPopped(Entity target) => false;

    /// <summary>True when <paramref name="buff"/> of <paramref name="target"/> is a script instance, so its shipped hooks own it.</summary>
    bool IsScripted(Entity target, BuffState buff) => false;

    /// <summary>
    /// A fresh order or an accepted cast of <paramref name="hero"/> (<paramref name="reason"/> "order", "spell request", or the name of the effect
    /// that stops it, Zhonya's Hourglass): a script channel the hero is running ends with the matching stop source.
    /// </summary>
    void OnCancelChannel(Entity hero, string reason) { }

    /// <summary>A script buff of <paramref name="hero"/> has toggled the champion slot on (<c>SpellToggleSlot</c>): its next cast is free.</summary>
    bool IsSpellToggledOn(Entity hero, int slot) => false;

    /// <summary>
    /// A hero was credited the kill of <paramref name="victim"/> (<c>BuffOnKill</c> of its script buffs, <c>CharOnKillUnit</c> of its character script):
    /// Owner and Attacker are the killer, Target the unit that died. The kill rewards were paid already.
    /// </summary>
    void OnUnitKilled(Entity killer, Entity victim) { }

    /// <summary>
    /// A hero was credited an assist in the death of the champion <paramref name="victim"/> (<c>BuffOnAssist</c>, <c>CharOnAssistUnit</c>): Owner and
    /// Attacker are the assistant, Target the champion that died. The assist gold was paid already.
    /// </summary>
    void OnUnitAssisted(Entity assistant, Entity victim) { }

    /// <summary>
    /// A basic attack landed on <paramref name="target"/> (<c>CharOnBeingHit</c>, <c>BuffOnBeingHit</c>), before mitigation: returns the hit the target
    /// takes after the scripts, which read it as <c>DamageAmount</c> and may change it (Sion's block, Pantheon's Aegis, the Silent Killer's halving).
    /// </summary>
    double OnBeingHit(Entity target, Entity attacker, double amount, DamageType type, BasicAttackOutcome outcome) => amount;

    /// <summary>
    /// A unit is about to deal damage, before mitigation, once the pre-deal hooks have run (<c>BuffOnDealDamage</c> of its script buffs, any unit): returns
    /// the amount to use (Sona's Power Chord debuff takes a fifth off).
    /// </summary>
    double OnDealDamage(Entity source, Entity target, double amount, DamageType type, DamageSourceKind? kind) => amount;

    /// <summary>
    /// A basic attack missed or was dodged, at its impact (<c>CharOnMiss</c> of the attacker, <c>CharOnDodge</c> of the hero that dodged).
    /// </summary>
    void OnBasicAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome) { }

    /// <summary>
    /// A hero's basic attack leaves (the windup ended): <c>BuffOnLaunchAttack</c> of its script buffs, and <c>BuffOnLaunchMissile</c> when the attack
    /// has a missile (<paramref name="missileNetId"/>).
    /// </summary>
    void OnBasicAttackLaunched(Entity attacker, Entity target, uint missileNetId) { }

    /// <summary>
    /// Whether this sink's character hooks run for the unit (batch 159 step 2): a champion module owns the heroes of its
    /// champion, a unit character module the non-champion units bound to one shipped <c>CharScript</c> each (a turret). The
    /// gameplay hooks' dispatch admits a module-owned non-hero through this, so <c>CharOnHitUnit</c>,
    /// <c>CharOnBeingHit</c> and <c>CharOnPreDamage</c> reach a turret's script the way they reach a champion's. Default
    /// false: a sink that binds no unit characters (the loadout's, the summoner spells', the items') does not widen it.
    /// </summary>
    bool OwnsUnit(Entity unit) => false;
}
