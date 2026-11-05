using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Combat;

public readonly record struct AttackRolls(double Crit, double Dodge, double Miss)
{
    public void Validate()
    {
        if (!double.IsFinite(Crit) || !double.IsFinite(Dodge) || !double.IsFinite(Miss))
            throw new ArgumentOutOfRangeException(nameof(AttackRolls), "attack rolls must be finite");
    }
}

public enum BasicAttackOutcome { Normal, Critical, Miss, Dodge }

public readonly record struct BasicAttackResult(double Damage, bool Critical, bool Missed, bool Dodged)
{
    public BasicAttackOutcome Outcome => Dodged ? BasicAttackOutcome.Dodge :
        Missed ? BasicAttackOutcome.Miss : Critical ? BasicAttackOutcome.Critical : BasicAttackOutcome.Normal;
    public static BasicAttackResult Hit(double damage, bool critical) => new(damage, critical, false, false);
    public static BasicAttackResult Miss(bool dodge) => new(0.0, false, !dodge, dodge);
}

public readonly record struct PreDamageResult(
    bool Handled,
    bool Landed,
    bool AlreadyApplied,
    double Damage,
    uint SourceNetId,
    DamageType DamageType,
    DamageSourceKind? SourceKind)
{
    public static PreDamageResult Unhandled => new(false, false, false, 0.0, 0, DamageType.Physical, null);
}

public readonly record struct PendingHit(
    uint SourceNetId,
    uint TargetNetId,
    double Damage,
    DamageType DamageType,
    DamageSourceKind? SourceKind,
    double WindupRemaining,
    Position2 ProjectilePosition,
    double MissileSpeed,
    uint MissileNetId = 0,
    bool IsReflect = false,
    bool SourceIsPeriodic = false,
    uint? SpellOwnerNetId = null,
    double VampScale = 1.0,
    BasicAttackOutcome Outcome = BasicAttackOutcome.Normal,
    DeathRecapOrigin? RecapOrigin = null)
{
    // A queued shot retains its target's life, independently of its caster.
    // Yorick's temporary life can end without another counted champion death.
    internal int TargetChampionDeaths { get; init; }
    internal YorickRevivalState? TargetYorickRevival { get; init; }
    // The attack spell is chosen at launch: a shot started under a
    // BBOverrideAutoAttack buff (VayneTumbleAttack, Takedown) is empowered,
    // one started before it is a plain basic attack wherever it lands.
    internal bool AttackOverride { get; init; }
}

/// <summary>
/// Narrow extension points for champion, item, jungle, cast, and FX code. Every
/// member is required so a production match cannot silently drop a combat rule.
/// </summary>
public interface ICombatHooks
{
    bool IsInvulnerable(Entity unit);
    bool IsGodMode(Entity unit);
    bool IsTargetable(Entity unit, Entity? observer);
    bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind,
        bool force);
    bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind) =>
        ConsumeSpellShield(source, target, sourceKind, force: false);
    /// <summary>Ends a zombie life the unit is already in (a kill of a unit in its temporary life finishes it) before death state is committed.</summary>
    bool TryFinishExistingRevival(Entity unit, uint killerNetId);
    /// <summary>
    /// Asked before the death packet is built: whether the death starts a zombie life. A script buff that has a <c>BuffOnZombie</c> hook
    /// (Yorick's <c>YorickReviveAllySelf</c>) answers it from its <c>BuffOnDeath</c>, which sets <c>BecomeZombie</c>; the death packet then
    /// carries the become-zombie flag (<c>NpcDie</c>) and <see cref="BeginZombie"/> follows once the death is committed.
    /// </summary>
    bool DeathBecomesZombie(Entity unit, uint killerNetId) => false;
    /// <summary>The unit that died into a zombie life stands up again (the committed death is the original one): <c>BuffOnZombie</c> runs.</summary>
    void BeginZombie(Entity unit, uint killerNetId) { }
    /// <summary>
    /// The factor the script buffs of <paramref name="unit"/> put on every heal it receives (<c>BuffOnHeal</c>: the ghouls and the revenant return
    /// <c>Health * 0</c>, Spirit Visage's script <c>Health * 1.15</c>), 1 for a unit with none.
    /// </summary>
    double ScriptHealingScale(Entity unit) => 1.0;
    PreDamageResult PreDamage(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind);
    double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind);
    /// <summary>
    /// The mastery area's pre-mitigation changes (batch 140), the stage the hand-written mastery application used: the
    /// attacker's loadout scripts change a hit (Havoc's <c>CharOnPreDealDamage</c>, then the <c>BuffOnHitUnit</c> of the
    /// marker buffs they asked for), and the target's <c>CharOnBeingHit</c> (Harden Skin's flat block) runs before the
    /// raw damage is kept. <c>IgnoreDamageIncreaseMods</c> skips the attacker's increases (they are increase mods) and
    /// leaves the target's block alone. Default: no loadout, so a fake that does not wear masteries is unchanged.
    /// </summary>
    double ModifyDamageByLoadoutBeforeMitigation(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind, bool ignoreDamageIncreaseMods) => amount;
    /// <summary>
    /// Runs after the raw damage snapshot but before resistance.
    /// This is the source boundary for target-side pre-mitigation scripts such
    /// as Yorick's incoming passive and ghoul AOE reduction.
    /// </summary>
    double ModifyDamageAfterRawBeforeMitigation(Entity target, Entity? source,
        double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
    /// <summary>
    /// The damage the source's script buffs see as the damage it deals (<c>BuffOnPreDealDamage</c>): after armor or magic resistance and the
    /// reductions, before the target's own pre-damage scripts and shields. Batch 348, from the patch notes of V1.0.0.120 (22 June 2011, wiki), the
    /// last word before 1.0.0.126 on the three heals and shields this hook feeds: Turtle Stance "Now heals Udyr based upon the actual damage dealt
    /// (instead of the raw damage dealt)" and its "Heal is no longer reduced if the damage is shielded" (UdyrTurtleAttack adds <c>GlobalDrainMana</c>
    /// before its <c>BBApplyDamage</c>, whose <c>BuffOnPreDealDamage</c> heals a share of <c>DamageAmount</c>); Iron Man's "Shield generation is no
    /// longer reduced if the damage done to enemies is absorbed by shields" (Mordekaiser's damage buffs feed the shield from this hook); Swain's
    /// heal is "50% of the actual damage dealt". The client holds only the empty the empty pre-damage handler and one
    /// <c>DamageEffect.DamageAmount</c>, so the order itself is server-only;
    /// the notes fix it.
    /// </summary>
    double ModifyDamageDealtAfterMitigation(Entity? source, Entity target, double amount,
        DamageType damageType, DamageSourceKind? sourceKind) => amount;
    double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source, double amount,
        DamageType damageType, DamageSourceKind? sourceKind);
    void OnBasicAttackStart(Entity attacker, Entity target);
    /// <summary>
    /// Asked before a hero's normal or critical swing is sent as a 0x0D: true when the hook sent the swing itself, as a cast of the
    /// attack-override spell flagged as an auto attack (a scripted chain attack, whose chain missile only a cast can start on the client).
    /// </summary>
    bool SendAttackAsCast(Entity attacker, Entity target, uint missileNetId) => false;
    /// <summary>Runs right after the attack packet and its missile went out (a scripted chain attack tells the client its hops).</summary>
    void OnBasicAttackSent(Entity attacker, Entity target, uint missileNetId) { }
    /// <summary>Runs when windup completes, including attacks with no windup.</summary>
    void OnBasicAttackLaunch(Entity attacker) { }
    /// <summary>
    /// The same moment with the unit attacked and the attack's missile net id: the script engine's <c>BuffOnLaunchAttack</c> and, for a ranged
    /// attack, <c>BuffOnLaunchMissile</c>. Implementers that override it also run <see cref="OnBasicAttackLaunch(Entity)"/> themselves.
    /// </summary>
    void OnBasicAttackLaunch(Entity attacker, Entity target, uint missileNetId) => OnBasicAttackLaunch(attacker);
    /// <summary>
    /// A basic attack missed or was dodged, when it would have landed: the script engine's <c>CharOnMiss</c> (the attacker) and <c>CharOnDodge</c>
    /// (the hero that dodged).
    /// </summary>
    void OnBasicAttackAvoided(Entity attacker, Entity target, BasicAttackOutcome outcome) { }
    /// <summary>
    /// A basic attack landed on <paramref name="target"/>, after the masteries' pre-mitigation changes and before the raw damage is kept: the script engine's
    /// <c>CharOnBeingHit</c> and <c>BuffOnBeingHit</c> may change the hit (Sion's block, Pantheon's Aegis); returns the amount to use.
    /// </summary>
    double ModifyDamageOnBeingHit(Entity target, Entity attacker, double amount, DamageType damageType, BasicAttackOutcome outcome) => amount;
    /// <summary>Called only when the basic attack roll was a target dodge.</summary>
    void OnBasicAttackDodge(Entity attacker, Entity target) { }
    void BreakAttackStealth(Entity attacker);
    double ModifyBasicAttackDamage(Entity source, Entity target, double damage);
    /// <summary>Uses the queued shot's launch-time attack override; null reads live buffs.</summary>
    double ModifyBasicAttackDamage(Entity source, Entity target, double damage, bool? attackOverride) =>
        ModifyBasicAttackDamage(source, target, damage);
    /// <summary>
    /// The same stage with the swing's result (<see cref="BasicAttackOutcome.Normal"/> or <see cref="BasicAttackOutcome.Critical"/>, the result byte
    /// the hit carries), which the BuildingBlocks <c>CharOnHitUnit</c> and <c>BuffOnHitUnit</c> read as <c>HitResult</c> while they change the hit.
    /// </summary>
    double ModifyBasicAttackDamage(Entity source, Entity target, double damage, bool? attackOverride, BasicAttackOutcome outcome) =>
        ModifyBasicAttackDamage(source, target, damage, attackOverride);
    /// <summary>
    /// Asked right after <see cref="ModifyBasicAttackDamage(Entity, Entity, double, bool?)"/> for the same hit: the script re-application of the attack.
    /// <c>Proc</c> is never set any more (Yorick's armed Q and Vayne's Silver Bolts third hit deal their PROC themselves, a nested hit of kind Proc);
    /// <c>IgnoreIncreaseMods</c> carries the IgnoreDamageIncreaseMods of an attack override's own primary hit (Nidalee's Takedown). Reading clears the answer.
    /// </summary>
    (bool Proc, bool IgnoreIncreaseMods) TakeProcReapplication() => default;
    /// <summary>
    /// Asked right after <see cref="ModifyBasicAttackDamage(Entity, Entity, double, bool?, BasicAttackOutcome)"/> for the same hit: the result the
    /// attack script left in <c>HitResult</c> at its hit when it differs from the swing's roll (batch 301, Pantheon's Certain Death), which the
    /// rest of the hit and its 0x68 result type follow; null when the roll stands. Reading clears the answer.
    /// </summary>
    BasicAttackOutcome? TakeScriptedHitResult() => null;
    /// <summary>
    /// True when the attacker's script attack override passes <c>IgnoreDamageCrit</c> on its primary hit (Takedown): the swing is rolled with no
    /// critical chance, because the script's damage is never multiplied by one. Asked once per swing, before the rolls are used.
    /// </summary>
    bool AttackIgnoresCrit(Entity attacker, Entity target) => false;
    double ModifyOutgoingDamage(Entity? source, Entity target, double damage, DamageType type, DamageSourceKind? sourceKind);
    double ModifyIncomingDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind);
    double AbsorbDamage(Entity target, Entity? source, double damage, DamageType type, DamageSourceKind? sourceKind);
    /// <summary>Runs item damage processing after shields and champion absorption.</summary>
    double PreprocessItemDamage(Entity? source, Entity target, double damage,
        DamageType type, DamageSourceKind? sourceKind);
    double ModifyHealthRegeneration(Entity hero, double amount, double dt);
    double ModifyManaRegeneration(Entity hero, double amount, double dt);
    /// <summary>
    /// Runs after a committed hit. <paramref name="damage"/> is the final amount dealt;
    /// <paramref name="rawDamage"/> is the pre-mitigation hit (the script callback's DamageAmount).
    /// </summary>
    void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage, DamageSourceKind? sourceKind,
        bool reflected, bool sourceIsPeriodic);
    void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted);
    void RemoveBuff(Entity target, int slot);
    void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type);
    /// <summary>Uses the queued shot's launch-time attack override; null reads live buffs.</summary>
    void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type,
        bool? attackOverride) => OnBasicAttackHit(source, target, rawDamage, type);
    /// <summary>
    /// The same landed hit with its result: <see cref="BasicAttackOutcome.Normal"/> or <see cref="BasicAttackOutcome.Critical"/>
    /// (the wire result byte the hit carried). A missed or dodged swing deals no damage and never reaches this hook.
    /// </summary>
    void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type,
        bool? attackOverride, BasicAttackOutcome outcome) =>
        OnBasicAttackHit(source, target, rawDamage, type, attackOverride);
    void OnBasicAttackMiss(Entity source);
    void OnPendingHitCanceled(PendingHit hit);
    /// <summary>
    /// A launched attack missile reached the corpse of the unit it was aimed at, which died in flight (the client's missile flies
    /// on to the corpse and ends there). No damage is dealt; a scripted chain attack continues from the corpse.
    /// </summary>
    void OnAttackMissileOnCorpse(PendingHit hit, Entity corpse) { }
    /// <summary>Stops the client attack animation for an unfinished melee cancellation.</summary>
    void OnPendingBasicAttackCanceled(PendingHit hit) { }

    /// <summary>
    /// A new swing is about to start while the attacker's last swing was dropped in its windup and no 0x39 has retired it
    /// (<see cref="Entity.ClientSwingDropped"/>): the stop goes out first, so the client does not play the dropped swing beside the new one.
    /// </summary>
    void OnDroppedSwingReplaced(Entity attacker) { }
    void OnPendingCastCanceled(Entity unit);
    bool IsActionBlocked(Entity unit);
    void OnDeath(Entity unit, uint killerNetId);

    /// <summary>
    /// The second half of a death, after the kill credit (<c>OnKill</c>): every buff without PersistsThroughDeath leaves the dead unit, the
    /// script buffs and the audited lists alike (batch 287). The credit reads the victim's marks, so they are still there while it runs.
    /// </summary>
    void OnDeathBuffsReleased(Entity unit) { }
    void OnPetDeath(Entity pet);
    void OnNeutralDeath(Entity neutral, uint killerNetId);
    void OnItemKill(Entity? killer, Entity victim);
    void OnItemDeath(Entity hero);
    /// <summary>Applies item assist effects after an authoritative hero assist.</summary>
    void OnItemAssist(Entity hero, Entity victim) { }
    /// <summary>
    /// An authoritative hero assist, after the assist gold was paid and <see cref="OnItemAssist"/> ran: the script engine's <c>BuffOnAssist</c> and
    /// <c>CharOnAssistUnit</c>.
    /// </summary>
    void OnAssist(Entity assistant, Entity victim) { }
    /// <summary>
    /// A hero's kill of an enemy unit, after its gold and experience were paid (the script engine's <c>BuffOnKill</c> and <c>CharOnKillUnit</c>).
    /// </summary>
    void OnKill(Entity? killer, Entity victim);
    void OnRespawn(Entity unit);
    /// <summary>A hero's player dropped out of a live match (<paramref name="connected"/> false) or came back (true).</summary>
    void OnHeroConnection(Entity hero, bool connected) { }
    void OnLevelChanged(Entity hero);
    void OnNeutralDamaged(Entity target, Entity? source);
    void OnItemPassiveTick(double now, double dt);
    void OnPeriodicDamage(Entity target, Entity? source, double amount, DamageType type, DamageSourceKind? sourceKind);
    /// <summary>The match ended: report it to whatever keeps the match's own book.</summary>
    void OnEndGame(Team winner);
    /// <summary>
    /// <c>HaltAllAI()</c>: every unit's ordinary orders and in-flight swings stop. EndOfGame.luaobj
    /// calls this itself (EndOfGame.txt proto 0.0 pc 0080-0081), so it is not part of the end-game
    /// report any more; scripted displacement and effect lifetimes keep their own update phases.
    /// </summary>
    void HaltAllAi();
}

public interface ICombatTransport
{
    bool IsWireReady(Entity unit);
    void SendBasicAttack(Entity source, Entity target, uint missileNetId, double extraTimeSeconds,
        BasicAttackOutcome outcome, int animationIndex);
    void SendTurretTarget(Entity turret, Entity? target);
    void SendTurretTargetHero(Entity turret, Entity? target);
    void SendAggroOutline(Entity unit, uint targetNetId);
    void RevealShrine(Entity shrine, Entity target);
    void SendApplyDamage(uint sourceNetId, Entity? source, Entity target, double damage,
        DamageType type, byte typeByte);
    /// <summary>Batch 591: a unit an attack's line missile (Spray and Pray) hit, as LineMissileHitList, before its hit. Offline transports
    /// have no wire.</summary>
    void SendLineMissileHit(Entity attacker, uint attackTargetNetId, uint missileNetId, uint hitNetId) { }
    void SendHealth(Entity target, double health);
    void SendTurretStats(Entity turret);
    void SendHeroStats(Entity hero);
    void SendDeath(Entity target, uint killerNetId, double durationSeconds, bool becomeZombie = false);
    void SendTurretDestroyed(Entity turret, uint killerNetId);
    void SendStructureDestroyed(Entity structure, uint killerNetId);
    void SendInhibitorState(Entity inhibitor, bool dead, double respawnSeconds, uint killerNetId = 0);
    void SendGold(Entity killed, Entity hero, double gold);
    void SendLevelUp(Entity hero);
    /// <summary>The owner's gold alone: the delta a change of the whole value the HUD draws goes out as.</summary>
    void SendHeroGold(Entity hero);
    void SendHeroGoldExp(Entity hero);
    void SendHeroNeutralKills(Entity hero);
    void SendHeroMultiKill(Entity hero, Entity victim, int count);
    /// <summary>The champion death event <c>OnChampionDie</c> (batch 690): the HUD's kill counter and the kill callout run from it.</summary>
    void SendChampionDie(Entity victim, uint killerNetId, double goldGiven, IReadOnlyList<Entity> assistants);
    void SendRespawn(Entity hero);
    void SendMinionNoRender(Entity minion);
    // The end-of-game ceremony is EndOfGame.luaobj's own now: each of its natives is one of these
    // packet families, called as the script's phases run them. See docs/network/patch126-end-game.md.
    void SendInputLockingFlag(uint flag, bool locked);
    void SendDisableHudForEndOfGame();
    void SendMoveCameraToPoint(Entity hero, Position3 point, double travelSeconds);
    void SendGreyscaleEnabledWhenDead(Entity hero, bool enabled);
    void SendMainSfxFade(double seconds);
    void SendNexusSkin(Team team, uint skinId);
    void SendMinionFade(Team team, double amount, double seconds);
    void SendEndGame(Team winner, bool surrendered);
    void SendEndOfGameEvent(Team winner);
    void SendStructureProtection(Entity structure);
}

public readonly record struct CombatTickResult(int PendingHitsResolved, int Deaths, bool EndGameSent);
