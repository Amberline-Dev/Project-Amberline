using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The BBxxx natives the Annie closure calls, bound to the production services. Parameter handling follows the stock
/// client's shared number getter, used by the damage native for Damage, PercentOfAttack, SpellDamageRatio ...:
/// a number parameter is the sum of its literal <c>X</c>, its <c>XByLevel[Level]</c> and the variable <c>XVar</c> read from
/// <c>XVarTable</c> (the root scope when no table is named), which is also how every shipped call site is written
/// (<c>Damage=0 DamageByLevel={...}</c>, <c>Damage=0 DamageVar="..."</c>). The client bodies only parse and store; what the
/// server does with the parsed record is derived from the shipped call sites, as documented per native.
/// </summary>
internal sealed partial class BbNativeHandlers : IBbNativeHandler
{
    private readonly BbSpellHost _host;

    /// <summary>
    /// DIAGNOSTICS: one <c>BBSpellEffectCreate</c> a script ran, as the spell sweep's FX rule reads it. The create reaches the wire only when
    /// <see cref="ParticleKnown"/>, not <see cref="Premature"/>, and the audience sees it, so the rule needs the create before those drops.
    /// <see cref="AlliedTeam"/> is the team the effect is for (null: every team), <see cref="OwnerHeroNetId"/> the one hero whose client gets it (0:
    /// every client), and <see cref="EnemyNamed"/> whether the other team gets the other-team name instead (then the team filter does not apply).
    /// </summary>
    internal sealed record EffectCreateEvent(string EffectName, uint BindNetId, uint TargetNetId, Position2 Position, bool GroundPoint,
        bool Premature, bool ParticleKnown, Team? AlliedTeam, uint OwnerHeroNetId, bool EnemyNamed, bool BindIsPlacemarker = false, bool WithheldAsCopy = false);

    /// <summary>
    /// DIAGNOSTICS: test seam (the spell sweep). Sees every <c>BBSpellEffectCreate</c> a script runs with its position, bind and target,
    /// before the drops in <see cref="SpellEffectCreate"/>. One match runs per process, so the seam is set for one run and cleared after.
    /// </summary>
    internal static Action<EffectCreateEvent>? EffectCreateTap { get; set; }

    public BbNativeHandlers(BbSpellHost host) => _host = host;

    /// <summary>The natives <see cref="Execute"/> runs; any other native raises an "unbound" error when a script calls it.</summary>
    private static readonly HashSet<string> Bound = new(StringComparer.Ordinal)
    {
        "BBPreloadSpell", "BBPreloadParticle", "BBPreloadCharacter", "BBApplyDamage", "BBApplyStun", "BBSpellBuffAdd",
        "BBSpellBuffRemove", "BBSpellBuffRemoveCurrent", "BBSpellEffectCreate", "BBSpellEffectRemove", "BBIncPAR",
        "BBIncMaxHealth", "BBGetSkinID", "BBGetCastSpellTargetPos", "BBForEachUnitInTargetArea",
        // M2 (Soraka): support, healing, silence and the random area pick.
        "BBApplyAssistMarker", "BBApplySilence", "BBIncHealth", "BBForEachUnitInTargetAreaRandom",
        // Rally (batch 148): the area iteration that adds a buff to every unit it admits (BeaconAura, BeaconAuraAP, OdinVanguardAura).
        "BBForEachUnitInTargetAreaAddBuff",
        // Taunt: the shipped global Taunt buff (Galio, Rammus and Shen cast it; the Yorick and turret scripts too).
        "BBApplyTaunt",
        // Spell slots and character data (BbNativeHandlers.Slots.cs).
        "BBSetSpell", "BBSetTargetingType", "BBSealSpellSlot", "BBPushCharacterData", "BBPopCharacterData", "BBPopAllCharacterData",
        // Fear, Root and NearSight: the other shipped global control buffs (Fear.luaobj, Root.luaobj, NearSight.luaobj).
        "BBApplyFear", "BBApplyRoot", "BBApplyNearSight",
        // Buff queries and clears (the client's buff natives), the clock, visibility, the
        // dodge-piercing flag and the nearest-N / every-champion iterations.
        "BBSpellBuffClear", "BBSpellBuffRemoveType", "BBGetBuffRemainingDuration", "BBGetBuffStartTime", "BBGetGameTime",
        "BBDispellNegativeBuffs",
        "BBCanSeeTarget", "BBSetDodgePiercing", "BBForNClosestUnitsInTargetArea", "BBForEachChampion", "BBGetPetOwner",
        "BBGetOffsetAngle", "BBIsInBrush",
        // The shield display natives: the scripts absorb in BuffOnPreDamage themselves.
        "BBIncreaseShield", "BBReduceShield", "BBRemoveShield", "BBModifyShield",
        // Animation overrides and the unlock-by-name.
        "BBOverrideAnimation", "BBClearOverrideAnimation", "BBStopCurrentOverrideAnimation",
        // Character fades: the translucency layers of stealth and phase effects.
        "BBPushCharacterFade", "BBPopCharacterFade",
        // M3 (Ryze, Taric, Kayle, Sivir): positions and facing, slot reads and writes, tooltips, animation, the permanent mana
        // pool, the scope-only unit setters, and the auto-attack override family.
        "BBGetUnitPosition", "BBGetPointByUnitFacingOffset", "BBFaceDirection", "BBGetCastRange", "BBSetTriggerUnit",
        "BBSetBuffCasterUnit", "BBSetSpellToolTipVar", "BBSetBuffToolTipVar", "BBPlayAnimation", "BBUnlockAnimation",
        "BBIncPermanentFlatPARPoolMod", "BBIncPermanentFlatPARRegenMod", "BBSetSlotSpellCooldownTimeVer2",
        // Batch 450: the Odin capture channel's cancel and success stops (SetUseSlotSpellCooldownTime below).
        "BBSetUseSlotSpellCooldownTime",
        // Batch 450: the damaging buff of the hit the BuffOnPreDamage hook reads (OdinCaptureChannel's damage branch, the first call of its guard).
        "BBGetDamagingBuffName",
        "BBOverrideAutoAttack", "BBRemoveOverrideAutoAttack", "BBCancelAutoAttack", "BBSpellCast",
        // Phase 3 (Annie): the pet a spell summons (Tibbers).
        "BBSpawnPet",
        // Phase 3 (W28): the nearest-N iteration restricted to what the Attacker's team can see (Shaco's Jack-in-the-Box).
        "BBForNClosestVisibleUnitsInTargetArea",
        // The team roster size the turret characters count their bonus health and their team lead with (batch 159 step 2).
        "BBGetNumberOfHeroesOnTeam",
    };

    // The movement, spawn and missile families are partial files of this class (Phase 3, W22), each with its own table of bound natives.
    public static bool IsBound(string function) =>
        Bound.Contains(function) || MovementBound.Contains(function) || SpawnBound.Contains(function) || MissileBound.Contains(function) ||
        VisionBound.Contains(function) || UnitsBound.Contains(function) || Wave2Bound.Contains(function) ||
        Wave7Bound.Contains(function);

    private SpellSystem Spells => _host.Spells;

    public void Execute(BbCall call)
    {
        // A dry probe run reads and computes only: every native but the damage call that ends it has an effect. A live one lets the effect happen
        // (and notes it); a native that runs sub-blocks of its own cannot be resumed after, so the script is not live when it has one.
        if (_host.HasFrame && _host.Frame.Probe is { } probe && call.Function != "BBApplyDamage")
        {
            if (!probe.Live || call.SubBlocks.Count > 0) throw new BbProbeAbort();
            probe.EffectsRan = true;
        }
        if (ExecuteMovement(call) || ExecuteSpawn(call) || ExecuteMissile(call) || ExecuteVision(call) || ExecuteUnits(call) || ExecuteWave2(call) || ExecuteWave7(call)) return;
        // A buff found by name or type sees the adds the same run made before it (BbBuffRuntime.LandHeldAdds, batch 703).
        if (call.Function is "BBSpellBuffRemove" or "BBSpellBuffClear" or "BBSpellBuffRemoveType" or "BBDispellNegativeBuffs"
            or "BBGetBuffRemainingDuration" or "BBGetBuffStartTime")
            _host.Buffs.LandHeldAdds();
        switch (call.Function)
        {
            case "BBPreloadSpell" or "BBPreloadParticle" or "BBPreloadCharacter":
                return; // asset preloads: the closure is resolved by name, there is nothing to load
            case "BBApplyDamage": ApplyDamage(call); return;
            case "BBApplyStun": ApplyStun(call); return;
            case "BBSpellBuffAdd": SpellBuffAdd(call); return;
            case "BBSpellBuffRemove":
                // One stack of the buff (BbBuffRuntime.Remove). ResetDuration restarts what stays with that many seconds (0: leaves the
                // timer alone): Akali's Shadow Dance and Corki's Missile Barrage pass the recharge time of the charge just spent, Riven's passive its window.
                // Without a BuffName the buff is the one named after the running script, as for BBSpellBuffAdd: AspectOfTheCougar's TargetExecute
                // adds and removes "its own" buff with no name (the second cast of the ultimate leaves the form).
                _host.Buffs.Remove(Unit(call, "TargetVar") ?? _host.Frame.Owner!, call.Value("BuffName") as string ?? _host.Frame.ScriptName,
                    Unit(call, "AttackerVar"), Math.Max(0.0, Sum(call, "ResetDuration")));
                return;
            case "BBSpellBuffRemoveCurrent":
                if (Unit(call, "TargetVar") is { } holder) _host.Buffs.RemoveCurrent(holder);
                return;
            case "BBSpellEffectCreate": SpellEffectCreate(call); return;
            case "BBSpellEffectRemove": SpellEffectRemove(call); return;
            case "BBIncPAR": IncPar(call); return;
            case "BBIncMaxHealth": IncMaxHealth(call); return;
            case "BBGetSkinID": GetSkinId(call); return;
            case "BBGetCastSpellTargetPos": GetCastSpellTargetPos(call); return;
            case "BBForEachUnitInTargetArea": ForEachUnitInTargetArea(call, random: false); return;
            case "BBForEachUnitInTargetAreaRandom": ForEachUnitInTargetArea(call, random: true); return;
            case "BBForEachUnitInTargetAreaAddBuff": ForEachUnitInTargetAreaAddBuff(call); return;
            case "BBApplyAssistMarker": ApplyAssistMarker(call); return;
            case "BBApplySilence": ApplySilence(call); return;
            case "BBApplyTaunt": ApplyTaunt(call); return;
            case "BBSpellBuffClear": SpellBuffClear(call); return;
            case "BBSpellBuffRemoveType": SpellBuffRemoveType(call); return;
            case "BBDispellNegativeBuffs":
                // Quicksilver Sash's TargetExecute is the only shipped call and it names the hero as AttackerVar.
                if (Unit(call, "AttackerVar") is { } dispelled) _host.Buffs.DispellNegatives(dispelled);
                return;
            case "BBGetBuffRemainingDuration": GetBuffRemainingDuration(call); return;
            case "BBGetBuffStartTime": GetBuffStartTime(call); return;
            // Reconstruction (batch 450): no buff of this engine is tracked as the source of a hit, so the name is empty. BBGetBuffStartTime
            // answers 0 for an empty name, so the Odin guard reads a hero's non-periodic hit as "DamageStartTime == 0", a cancel.
            case "BBGetDamagingBuffName": StoreOut(call, "Dest", ""); return;
            case "BBGetGameTime": StoreOut(call, "Seconds", GameTimeSeconds(Spells.CurrentTime)); return;
            case "BBCanSeeTarget": CanSeeTarget(call); return;
            case "BBSetDodgePiercing": SetDodgePiercing(call); return;
            case "BBForNClosestUnitsInTargetArea": ForEachUnitInTargetArea(call, random: false, nearest: true); return;
            case "BBForNClosestVisibleUnitsInTargetArea": ForEachUnitInTargetArea(call, random: false, nearest: true, visibleOnly: true); return;
            case "BBForEachChampion": ForEachChampion(call); return;
            case "BBGetNumberOfHeroesOnTeam": GetNumberOfHeroesOnTeam(call); return;
            case "BBGetPetOwner": GetPetOwner(call); return;
            case "BBGetOffsetAngle": GetOffsetAngle(call); return;
            case "BBIsInBrush": IsInBrush(call); return;
            case "BBPushCharacterFade": PushCharacterFade(call); return;
            case "BBPopCharacterFade": PopCharacterFade(call); return;
            case "BBOverrideAnimation": OverrideAnimation(call, set: true); return;
            case "BBClearOverrideAnimation": OverrideAnimation(call, set: false); return;
            case "BBStopCurrentOverrideAnimation": StopCurrentOverrideAnimation(call); return;
            case "BBIncreaseShield" or "BBModifyShield": ChangeShield(call, 1.0); return;
            case "BBReduceShield" or "BBRemoveShield": ChangeShield(call, -1.0); return;
            case "BBSetSpell": SetSpell(call); return;
            case "BBSetTargetingType": SetTargetingType(call); return;
            case "BBSealSpellSlot": SealSpellSlot(call); return;
            case "BBPushCharacterData": PushCharacterData(call); return;
            case "BBPopCharacterData": PopCharacterData(call); return;
            case "BBPopAllCharacterData": PopAllCharacterData(call); return;
            case "BBApplyFear": ApplyFear(call); return;
            case "BBApplyRoot": ApplyRoot(call); return;
            case "BBApplyNearSight": ApplyNearSight(call); return;
            case "BBIncHealth": IncHealth(call); return;
            case "BBGetUnitPosition": GetUnitPosition(call); return;
            case "BBGetPointByUnitFacingOffset": GetPointByUnitFacingOffset(call); return;
            case "BBFaceDirection": FaceDirection(call); return;
            case "BBGetCastRange": GetCastRange(call); return;
            case "BBSetTriggerUnit":
                // An empty body in the stock client (it returns at once): the shipped shields name the unit that triggered
                // the hook, which this engine already gives every hook as Attacker. Reconstruction: nothing to store.
                return;
            case "BBSetBuffCasterUnit": SetBuffCasterUnit(call); return;
            case "BBSetSpellToolTipVar": SetSpellToolTipVar(call); return;
            case "BBSetBuffToolTipVar": SetBuffToolTipVar(call); return;
            case "BBPlayAnimation": PlayAnimation(call); return;
            case "BBUnlockAnimation": UnlockAnimation(call); return;
            case "BBIncPermanentFlatPARPoolMod": IncPermanentFlatParPoolMod(call); return;
            case "BBIncPermanentFlatPARRegenMod": IncPermanentFlatParRegenMod(call); return;
            case "BBSetSlotSpellCooldownTimeVer2": SetSlotSpellCooldownTimeVer2(call); return;
            case "BBSetUseSlotSpellCooldownTime": SetUseSlotSpellCooldownTime(call); return;
            case "BBOverrideAutoAttack": OverrideAutoAttack(call); return;
            case "BBRemoveOverrideAutoAttack": RemoveOverrideAutoAttack(call); return;
            case "BBCancelAutoAttack": CancelAutoAttack(call); return;
            case "BBSpawnPet": BbPetSpawn.Execute(_host, call); return;
            case "BBSpellCast": SpellCast(call); return;
            default:
                throw BbSpellHost.Unbound(call.Function, "M2");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Parameter helpers
    // ------------------------------------------------------------------------------------------------------------

    private static Entity? Unit(BbCall call, string key) => BbSpellHost.UnitOrNull(call.Var(key));

    /// <summary>
    /// <c>(X + XByLevel[Level] + XVarTable[XVar]) * XMultiplier</c>, the client's shared number getter (the three
    /// parts are summed, then the result is multiplied by the parameter <c>XMultiplier</c>, default 1; the formats are the strings
    /// "%sByLevel", "%sVar", "%sVarTable" and "%sMultiplier"). A missing part adds nothing, a nil variable is an error. No shipped script
    /// passes an <c>XMultiplier</c>; it is honoured because the getter is shared by every number parameter of every native.
    /// The getter keeps every partial sum and its result as a float32 (it casts to <c>float</c> after the literal, after
    /// each addition and after the multiply, and returns <c>(float)</c>), so a script's Lua double 0.7 reads 0.69999999 exactly like the
    /// inibin byte/10 value <c>(float)7 * 0.1f</c> the hand-written modules take (batch 64): the same coefficient on both sides of the
    /// Annie differential.
    /// </summary>
    internal static double Sum(BbCall call, string key)
    {
        var total = 0f;
        if (call.Value(key) is double literal) total = (float)literal;
        if (call.Param(key + "ByLevel") is { } byLevel && call.Scope.Level is { } level)
            total = (float)(BbRuntimeNames.Number(BbRuntime.Index(byLevel, level)) + total);
        // A variable that holds nothing counts 0, like the client's number getter (a Lua nil read as 0): the shipped MoveAwayCollision names
        // IdealDistanceVar in the root scope while the script keeps IdealDistance in InstanceVars, and Orianna's Shockwave pull works in retail.
        if (call.Value(key + "Var") is { } name)
        {
            // Reconstruction: the shipped Move and MoveAway buffs name IdealDistanceVar and no IdealDistanceVarTable (their Speed, Gravity
            // and Distance have theirs, in InstanceVars); the lookup is the root scope, where nothing sets it, and the retail knock-up and
            // knockback run, so a nil variable of a group that names no table adds nothing (the literal part, 0, stands). Batch 337: the same
            // holds when the table is named. LeblancSlideMove's BBMove names IdealDistanceVarTable InstanceVars for a 'Distance' that
            // BBDistanceBetweenObjectAndPoint wrote to the root scope, so the variable is nil there too; the old throw stopped her Distortion.
            var value = call.Table(key + "VarTable", create: false)[name];
            if (value is not null)
                total = (float)(BbRuntimeNames.Number(value) + total);
        }
        return (float)((call.Value(key + "Multiplier") is double multiplier ? (float)multiplier : 1f) * total);
    }

    /// <summary>
    /// The client's output writer: the value goes to <c>vars[XVarTable][XVar]</c> for the output called <c>X</c>
    /// ("%sVar" and "%sVarTable"; the root scope when no table is named). Nothing happens when the script names no variable.
    /// </summary>
    private static void StoreOut(BbCall call, string prefix, object? value)
    {
        if (call.Value(prefix + "Var") is not { } name) return;
        call.Table(prefix + "VarTable", create: true)[name] = value;
    }

    private static bool Flag(BbCall call, string key, bool fallback) =>
        call.Value(key) is { } value ? BbRuntime.Truthy(value) : fallback;

    // ------------------------------------------------------------------------------------------------------------
    // BBApplyDamage
    // ------------------------------------------------------------------------------------------------------------

    private void ApplyDamage(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        // The client's damage native stores nothing without a target: it tests the Target lookup first.
        if (target is null || attacker is null) return;
        // PercentOfAttack is 1 almost everywhere. Reconstruction (M3): the two shipped spells that scale it (Sivir's Spiral Blade
        // decays percentOfAttack 0.8 per unit hit, Ricochet decays DamagePercent 0.75 per hop; both write the literal 0 and the
        // value in PercentOfAttackVar) use it as the multiplier of the damage the call computes. A literal 0 with no variable
        // would be a damage of zero nobody writes, so that stays an unbound error.
        var percent = Sum(call, "PercentOfAttack");
        // Reconstruction (batch 321): a literal 0 with no PercentOfAttackVar multiplies the whole call to 0, so the call deals nothing and the
        // script goes on. Talon's Noxian Diplomacy BuffOnHitUnit passes one (Damage 0, no DamageVar, PercentOfAttack 0) before it clears its
        // buffs and bleeds; refusing the call (the M2 rule that stood here) stopped that hook at the call, so the bleed never landed.
        if (percent != 1.0 && percent <= 0.0 && call.Value("PercentOfAttackVar") is null) return;
        // ExpirationTimer ends the helpers a script spawned (Janna's TestCube) with 9999 InternalRaw damage to itself: the ordinary hit below, whose
        // own InternalRaw kill passes the placemarker's Protected flag and its invulnerability (CombatService.ApplyHit, batch 246). A placemarker's
        // own hit of another source (Talon's 50000 DAMAGESOURCE_DEFAULT on each blade cube) passes the flag but not invulnerability (batch 709).
        var type = call.Value("DamageType") is double typeNumber ? (BbDamageType)(int)typeNumber : BbDamageType.PHYSICAL_DAMAGE;
        // The damage native reads DamageType and SourceDamageType as optional integers whose default is 0 (PHYSICAL_DAMAGE, DAMAGESOURCE_RAW); every
        // shipped call names both.
        var sourceNumber = call.Value("SourceDamageType") is double source ? (int)source : (int)BbDamageSource.DAMAGESOURCE_RAW;
        // DAMAGESOURCE_DEFAULT is resolved by the damage record's constructor: the running cast's
        // own source, DAMAGESOURCE_ATTACK for an auto attack's cast (SpellCastInfo isAutoAttack) and DAMAGESOURCE_SPELL for any other; with no
        // cast it is DAMAGESOURCE_PERIODIC. PantheonBasicAttack deals his swing that way (batch 301: it was dealt a second time as
        // an uncategorised hit). Batch 331: the client's body takes the running cast from the script context (which
        // only the server sets; the client's context guard only restores it) for a SPELL, ATTACK or DEFAULT hit, so a spell script's own
        // execute hook (Seismic Shard, Feast, Consume, Infected Cleaver, the item actives' spells: SpellFrame) deals its DEFAULT hit as a spell.
        // Reconstruction: whether the server runs a buff or character hook with a cast is not in the client (its DEFAULT would be PERIODIC with
        // none); those calls (Ignite's SummonerDot, the shrine and relic buffs, 3077's cleave) keep the uncategorised hit.
        if (sourceNumber == (int)BbDamageSource.DAMAGESOURCE_DEFAULT && _host.HasFrame && _host.Frame.IsAttackOverride)
            sourceNumber = (int)BbDamageSource.DAMAGESOURCE_ATTACK;
        else if (sourceNumber == (int)BbDamageSource.DAMAGESOURCE_DEFAULT && _host.HasFrame && _host.Frame.CastRunning)
            sourceNumber = (int)BbDamageSource.DAMAGESOURCE_SPELL;
        // The attack spell of an auto-attack override (Righteous Fury, Ricochet) lists the attack's own damage first. The engine's
        // basic attack has already dealt it, crit and life steal included, so that one hit is not applied a second time
        // (Reconstruction: retail's override attack is the script's damage; the emulator keeps one attack pipeline).
        if (sourceNumber == (int)BbDamageSource.DAMAGESOURCE_ATTACK && _host.HasFrame && _host.Frame is { IsAttackOverride: true, AttackTarget: { } primary } &&
            ReferenceEquals(primary, target) && ReferenceEquals(attacker, _host.Frame.Owner))
        {
            // The probe run of OnAttackOverrideDamage ends here with the amount this call asks for: Damage plus PhysicalDamageRatio of the
            // bonus attack damage (the shipped base attack scripts pass the base attack damage with the ratio 1, VayneTumbleAttack its
            // whole total with 0), times PercentOfAttack. Reconstruction: the client body only stores the fields.
            if (_host.Frame.Probe is { } primaryProbe)
            {
                primaryProbe.Amount = (Sum(call, "Damage") + (type == BbDamageType.PHYSICAL_DAMAGE
                    ? Sum(call, "PhysicalDamageRatio") * _host.BonusAttackDamage(attacker) : 0.0)) * percent;
                primaryProbe.IgnoreCrit = Flag(call, "IgnoreDamageCrit", false);
                primaryProbe.IgnoreIncreaseMods = Flag(call, "IgnoreDamageIncreaseMods", false);
                // Where the live run stopped, for the run that finishes the script after the engine's swing (the path as CurrentBlockString reports it).
                primaryProbe.Path = call.Interpreter.CurrentBlockString.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                primaryProbe.Scope = call.Scope;
                throw new BbProbeAbort();
            }
            return;
        }
        // Any other hit before the primary one is more than a prefix of pure arithmetic; a live run deals it for real.
        if (_host.HasFrame && _host.Frame.Probe is { } earlier)
        {
            if (!earlier.Live) throw new BbProbeAbort();
            earlier.EffectsRan = true;
        }
        var amount = Sum(call, "Damage");
        // SpellDamageRatio / PhysicalDamageRatio (docs/scripting/building-blocks-engine.md, ApplyDamage ratio fields, 16 Oct
        // 2026): coefficients on spell damage; the ratio of the other damage type is a leftover (usually 1) and ignored, and
        // item procs, periodics and reactives are flat. A magic call from a champion's proc or attack carries a real AP coefficient
        // too (the box shot ShacoBoxSpell PROC 0.25, whose tooltip reads "(+AP)"; TwistedFate's RedCard 0.4, RumbleOverheatAttack 0.3,
        // RyzeDesperatePowerAttack ATTACK 0.3 to 0.5): all 12 shipped magic PROC and ATTACK calls that name a non-zero SpellDamageRatio
        // are such scripts, while every item and talent one passes 0, so the AP term counts for those two sources as well.
        var spellSource = sourceNumber is (int)BbDamageSource.DAMAGESOURCE_SPELL or (int)BbDamageSource.DAMAGESOURCE_SPELLAOE
            or (int)BbDamageSource.DAMAGESOURCE_SPELLPERSIST;
        if (type == BbDamageType.MAGIC_DAMAGE && (spellSource || sourceNumber is (int)BbDamageSource.DAMAGESOURCE_PROC
                or (int)BbDamageSource.DAMAGESOURCE_ATTACK))
            amount += Sum(call, "SpellDamageRatio") * attacker.AbilityPower;
        else if (spellSource && type == BbDamageType.PHYSICAL_DAMAGE)
            // Reconstruction: the physical coefficient scales bonus attack damage (Vayne Condemn 0.5); the shipped
            // call sites that scale AD explicitly set the field to 0.
            amount += Sum(call, "PhysicalDamageRatio") * _host.BonusAttackDamage(attacker);
        amount *= percent;
        var ignoreCrit = Flag(call, "IgnoreDamageCrit", false);
        var critical = sourceNumber == (int)BbDamageSource.DAMAGESOURCE_ATTACK && !ignoreCrit && CriticalHitResult(call);
        if (critical && attacker.CritMultiplier > 0.0) amount *= attacker.CritMultiplier;
        var (kind, periodic) = SourceKind(sourceNumber);
        Spells.Combat.ApplyDamage(attacker, target, amount, type switch
        {
            BbDamageType.MAGIC_DAMAGE => DamageType.Magic,
            BbDamageType.TRUE_DAMAGE => DamageType.True,
            _ => DamageType.Physical,
        }, kind, 1.0, false, periodic, null, _host.Frame.RecapOrigin ?? DeathRecapOrigin.PetSpell(attacker, _host.Frame.ScriptName) ??
            WorldScriptOrigin(attacker),
            ignoreDamageIncreaseMods: Flag(call, "IgnoreDamageIncreaseMods", false),
            ignoreDamageCrit: ignoreCrit,
            // CallForHelpAttackerVar: the unit idle camps wake on (ShacoBoxSpell credits Shaco and names the box); absent, the attacker.
            callForHelpSource: Unit(call, "CallForHelpAttackerVar"),
            critical: critical);
    }

    /// <summary>
    /// Whether the hit's <c>HitResult</c> is <c>HIT_Critical</c> (batch 301). The damage native reads it with the integer getter
    /// which looks the name up in the script's own variables first (the passThroughParams table)
    /// and only then in the call's parameters; no shipped call passes it as a parameter, so it is the variable the script left:
    /// the swing's roll the engine put there, or what the script set (Parley's own roll). It goes into the damage record with
    /// <c>IgnoreDamageCrit</c> (the damage record's HitResult and IgnoreCrit fields).
    /// <para>
    /// Reconstruction: the multiplication itself is the server's (the client's damage application hands the record to
    /// the health's damage callback). The scripts fix the rule: an attack hit (<c>DAMAGESOURCE_ATTACK</c>) with a critical result is multiplied as a
    /// whole by the attacker's critical multiplier unless the call ignores critical strikes (<c>VayneTumbleAttack</c> divides its ratio by
    /// 2 + <c>GetFlatCritDamageMod</c>, <c>NocturneUmbraBladesAttack</c> takes 1.1 instead of 1.2 of the attack on a crit, <c>XenZhaoThrust</c>
    /// doubles the attack part itself and passes <c>IgnoreDamageCrit</c>). Other sources are never multiplied: 30 shipped on-hit procs
    /// (<c>BuffOnHitUnit</c> <c>DAMAGESOURCE_PROC</c>, Wit's End, Malady, Madred's Razors, Pick a Card) pass <c>IgnoreDamageCrit</c> false where
    /// <c>HitResult</c> is the swing's critical, and none of them strikes critically.
    /// </para>
    /// </summary>
    private static bool CriticalHitResult(BbCall call) =>
        call.Scope.Vars["HitResult"] is double result && result == (double)BbHitResult.HIT_Critical;

    /// <summary>
    /// The world-script catalog's origin of this frame's script when it names one (SummonerFortify's splash is dealt by the
    /// fortified turret itself, which no pet or buff-producer catalog covers), else null so the hit keeps the origin it would
    /// have had without the catalog. The catalog refuses an origin whose live attacker does not prove its parent.
    /// </summary>
    private DeathRecapOrigin? WorldScriptOrigin(Entity attacker)
    {
        var origin = DeathRecapOrigin.WorldScript(attacker, _host.Frame.ScriptName);
        return origin == DeathRecapOrigin.Unclassified ? null : origin;
    }

    /// <summary>The server damage category of a script DAMAGESOURCE_* value, and whether it is a periodic tick.</summary>
    internal static (DamageSourceKind? Kind, bool Periodic) SourceKind(int source) => source switch
    {
        (int)BbDamageSource.DAMAGESOURCE_RAW => (DamageSourceKind.Raw, false),
        (int)BbDamageSource.DAMAGESOURCE_INTERNALRAW => (DamageSourceKind.InternalRaw, false),
        (int)BbDamageSource.DAMAGESOURCE_PERIODIC => (null, true),
        (int)BbDamageSource.DAMAGESOURCE_PROC => (DamageSourceKind.Proc, false),
        (int)BbDamageSource.DAMAGESOURCE_SPELL => (DamageSourceKind.Spell, false),
        (int)BbDamageSource.DAMAGESOURCE_ATTACK => (DamageSourceKind.Basic, false),
        (int)BbDamageSource.DAMAGESOURCE_SPELLAOE => (DamageSourceKind.SpellAoe, false),
        (int)BbDamageSource.DAMAGESOURCE_SPELLPERSIST => (DamageSourceKind.SpellPersist, false),
        _ => (null, false), // TO_REACTIVE, TO_ONDEATH and DEFAULT carry no category (Thornmail's reflection: sourceKind null)
    };

    // ------------------------------------------------------------------------------------------------------------
    // BBApplyStun
    // ------------------------------------------------------------------------------------------------------------

    private void ApplyStun(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        var caster = attacker ?? target;
        var duration = Sum(call, "Duration");
        // The native adds the shipped global Stun buff (Stun.luaobj: SetStunned, then BBApplyAssistMarker 10 s for the
        // attacker), which ChampionSpellSupport.AddBuff reproduces through RecordSupportAssist.
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard; retail's outcome on a full table is not recovered, so a full table skips the stun
        // (Boots of Mobility needs a second free slot for its debuff) exactly as the hand-written control spells do.
        if (!target.Alive && caster.Team != target.Team) return; // Reconstruction: no control on a corpse (BbBuffRuntime.Add; retail's add to a corpse is not recovered)
        if (!ChampionSpellSupport.CanAddControl(caster, target, "Stun")) return;
        ChampionSpellSupport.AddBuff(Spells, target, "Stun", BuffType.Stun, duration, caster.NetId, null, mitigate: true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBApplySilence and BBApplyAssistMarker
    // ------------------------------------------------------------------------------------------------------------

    private void ApplySilence(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        var caster = attacker ?? target;
        var duration = Sum(call, "Duration");
        // Like the stun, the native adds the shipped global Silence buff (Silence.luaobj: SetSilenced, then
        // BBApplyAssistMarker for the attacker); ChampionSpellSupport.AddBuff records that marker for "Silence".
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard, and retail's outcome on a full table is not recovered (see ApplyStun).
        if (!target.Alive && caster.Team != target.Team) return; // Reconstruction: no control on a corpse (BbBuffRuntime.Add; retail's add to a corpse is not recovered)
        if (!ChampionSpellSupport.CanAddControl(caster, target, "Silence")) return;
        ChampionSpellSupport.AddBuff(Spells, target, "Silence", BuffType.Silence, duration, caster.NetId,
            new BuffPayload { Silence = true }, mitigate: true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBApplyTaunt
    // ------------------------------------------------------------------------------------------------------------

    private void ApplyTaunt(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        // The client's handler parses Target, then Duration and Attacker only when Target is set, and then asserts
        // InvalidClientCall for the taunt's apply call: the body is server-only.
        // Reconstruction: a taunt names the unit to attack through the buff's caster (Taunt.luaobj reads Attacker, and Pet.lua /
        // minionLib / Leashed.lua GetTauntTarget return it), so without an attacker there is nothing to apply.
        if (target is null || attacker is null) return;
        var duration = Sum(call, "Duration");
        // The native adds the shipped global Taunt buff (Taunt.luaobj: SetTaunted, then the 10 s assist marker only when the
        // attacker is a champion; BuffOnUpdateStats removes the buff once the attacker is dead, BuffLifecycle.TickBuffs).
        // The pets, minions and camp monsters read it through CharacterState / GetTauntTarget.
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard, and retail's outcome on a full table is not recovered (see ApplyStun).
        if (!ChampionSpellSupport.CanAddControl(attacker, target, "Taunt")) return;
        // Added without ChampionSpellSupport.AddBuff: that helper records the assist marker for every "Taunt" add and credits a pet's owner,
        // while Taunt.luaobj applies it only when the attacker itself is a champion (CO_IS_TYPE_HERO).
        var buff = Spells.Buffs.Add(target, "Taunt", BuffType.Taunt, duration, attacker.NetId, null, canMitigateDuration: true);
        if (buff is not null && attacker.UnitType == UnitType.Hero) Spells.Combat.RecordSupportAssist(attacker, target, 10.0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBApplyFear, BBApplyRoot, BBApplyNearSight
    // ------------------------------------------------------------------------------------------------------------

    // The client parses all of them like BBApplyStun (Target, Duration, Attacker) and then asserts InvalidClientCall in
    // the three client apply methods (ApplyFear, ApplyRoot, ApplyNearSight): the body is server-only. Each adds the
    // shipped global buff of the same name, whose hooks only set a CharacterState bit (Fear.luaobj: SetFeared, SetCanCast(false),
    // 40 percent slow, the assist marker; Root.luaobj: SetRooted; NearSight.luaobj: SetNearSight).

    private void ApplyFear(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        var caster = attacker ?? target;
        var duration = Sum(call, "Duration");
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard, and retail's outcome on a full table is not recovered (see ApplyStun).
        if (!ChampionSpellSupport.CanAddControl(caster, target, "Fear")) return;
        // The payload is the one the Jack-in-the-Box fear adds (ShacoBoxService.Fear): the walking of a feared unit (minionLib / Pet.lua
        // OnFearBegin, the shared one-second wander around FearOrigin) is the buff lifecycle's, and Fear.luaobj's 40 percent slow is
        // MoveSpeedMult. ChampionSpellSupport.AddBuff records the 10 s assist marker Fear.luaobj's activation applies.
        var payload = new BuffPayload { ActionDisabled = true, MoveSpeedMult = -0.4, FearOrigin = target.Position, NextWander = Spells.CurrentTime };
        ChampionSpellSupport.AddBuff(Spells, target, "Fear", BuffType.Fear, duration, caster.NetId, payload, mitigate: true);
    }

    private void ApplyRoot(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        var caster = attacker ?? target;
        var duration = Sum(call, "Duration");
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard, and retail's outcome on a full table is not recovered (see ApplyStun).
        if (!ChampionSpellSupport.CanAddControl(caster, target, "Root")) return;
        // Reconstruction: Root.luaobj sets no buff type and the C++ body is server-only (the client parses the call and asserts
        // InvalidClientCall in the root apply method). BUFF_Snare is the type of a movement lock
        // (CharacterState and the lifecycle read Snare as a root). The payload is what Root.luaobj's SetRooted writes
        // (BbBuffRuntime.ApplyStatus): the client's SetRooted clears CanMove and CanAttack, so Rammus cannot
        // attack in the 0.5 s his Powerball roots him for (PowerBall/PowerballStun, the two shipped callers). Root.luaobj records
        // no assist marker.
        ChampionSpellSupport.AddBuff(Spells, target, "Root", BuffType.Snare, duration, caster.NetId,
            new BuffPayload { MovementDisabled = true, Disarmed = true }, mitigate: true);
    }

    private void ApplyNearSight(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        var caster = attacker ?? target;
        var duration = Sum(call, "Duration");
        // Reconstruction: the 64-slot buff table is a client limit the scripts never guard, and retail's outcome on a full table is not recovered (see ApplyStun).
        if (!ChampionSpellSupport.CanAddControl(caster, target, "NearSight")) return;
        // Reconstruction: NearSight.luaobj's SetNearSight marks the unit, and no shipped file gives its sight radius (DATA/Buffs/NearSight.inibin
        // holds only the tooltip key). The client's SetNearSight (the setter that calls through a pointer) and
        // ProccessNearSight (a bubble-owner gate: with the status word's bit 4 set a sight
        // bubble passes only when the unit itself owns it) name no radius either. The nearsighted unit's sight list is therefore its own bubble
        // and the enemy attack reveals (VisibilityRules.BuildObserverProviders, batch 438); its radius is Paranoia's shipped -300.
        // Not recovered: the pointer target of the setter and the fog code that reads the status bit (bit 9).
        // Reconstruction: BUFF_NearSight is the buff type of the name, and the script's PersistsThroughDeath is not modelled.
        ChampionSpellSupport.AddBuff(Spells, target, "NearSight", BuffType.NearSight, duration, caster.NetId, null, mitigate: false);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Buff queries and clears, visibility, dodge piercing, pets, iteration over champions
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSpellBuffClear</c>: Target and BuffName; the client body clears every buff of that name from the unit's
    /// buff manager, whoever cast it (the other removal, BBSpellBuffRemove, names an Attacker). The deactivation hooks run per buff.
    /// </summary>
    private void SpellBuffClear(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target || call.Value("BuffName") is not string name) return;
        _host.Buffs.ClearDeferred(target, name);
    }

    /// <summary><c>BBSpellBuffRemoveType</c> (parses Target and Type; the client's body asserts a dispel by type).</summary>
    private void SpellBuffRemoveType(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target || call.Value("Type") is not double type) return;
        _host.Buffs.RemoveType(target, type);
    }

    /// <summary>
    /// <c>BBGetBuffRemainingDuration</c> (Target, BuffName, the output; the body is the buff's end time minus the game time).
    /// 0 when the unit has no such buff.
    /// </summary>
    private void GetBuffRemainingDuration(BbCall call)
    {
        var buff = Unit(call, "TargetVar") is { } target && call.Value("BuffName") is string name
            ? target.Buffs.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
        // During a buff's deactivation the answer is the time it had left: YorickRARemovePet asks that to tell its removal from its expiry,
        // and kills the revenant only for the removal. Here the lifecycle has taken the buff off the unit already, so the buff whose
        // deactivation is running answers with the time it had left.
        if (buff is null && _host.HasFrame && _host.Frame.Buff is { } leaving && Unit(call, "TargetVar") is { } holder &&
            ReferenceEquals(holder, leaving.Owner) && call.Value("BuffName") is string leavingName &&
            string.Equals(leaving.Name, leavingName, StringComparison.OrdinalIgnoreCase) && _host.Frame.Hook == "OnBuffDeactivate")
        {
            StoreOut(call, "Dest", Math.Max(0.0, leaving.State.ExpiresAt));
            return;
        }
        StoreOut(call, "Dest", buff is null ? 0.0 : Math.Max(0.0, buff.ExpiresAt));
    }

    /// <summary>
    /// <c>BBGetBuffStartTime</c> (Target, BuffName, the output; the body reads the buff's start time). The name may come from
    /// a variable (<c>BuffNameVar</c>). The client body (shared code, batch 320) starts the answer at 0, looks the name's buff script up
    /// asks the target's buff manager for that script's instance (the lookup by script),
    /// and, when there is one, reads its start time, then stores the answer: a missing buff
    /// answers 0. Every retail buff is a script instance; a buff the emulator adds without a script has none and answers 0 too.
    /// </summary>
    private void GetBuffStartTime(BbCall call)
    {
        var name = call.Value("BuffName") as string ??
                   (call.Value("BuffNameVar") is { } variable ? call.Table("BuffNameVarTable", create: false)[variable] as string : null);
        var instance = Unit(call, "TargetVar") is { } target && name is not null ? _host.Buffs.Find(target, name) : null;
        StoreOut(call, "Dest", instance?.StartTime ?? 0.0);
    }

    /// <summary>
    /// <c>BBCanSeeTarget</c> (Viewer, Target, the output <c>Result</c>). The client answers from its own view only for the
    /// local player; the server's answer is the team vision rule the spell system already uses for targeting.
    /// </summary>
    private void CanSeeTarget(BbCall call)
    {
        var viewer = Unit(call, "ViewerVar");
        var target = Unit(call, "TargetVar");
        StoreOut(call, "Result", viewer is not null && target is not null && Spells.CanSeeTarget(viewer, target));
    }

    /// <summary>
    /// <c>BBSetDodgePiercing</c> (Target, Value; the client body sets bit 0x8000 of the unit's status word, the dodge-piercing
    /// bit, to Value). Entity.DodgePiercing is that bit, which the combat service reads for an attack that cannot be dodged. The shipped scripts
    /// set it in a buff's OnBuffActivate and clear it in OnBuffDeactivate (17 buffs) or once in CharOnActivate (the turrets). It is one bit that
    /// only another call changes (batch 241): no buff payload holds it and the hero stat refresh leaves it alone. A hero's refresh pushes the
    /// status word that carries it.
    /// </summary>
    private void SetDodgePiercing(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var on = BbRuntime.Truthy(call.Value("Value"));
        if (target.DodgePiercing == on) return;
        target.DodgePiercing = on;
        if (target.UnitType == UnitType.Hero && Spells.HeroStats is { } stats && !(_host.HasFrame && _host.Frame.Stats is not null))
            stats.Refresh(target, push: Spells.Effects.IsReferenceReady(target));
    }

    /// <summary>
    /// <c>BBPushCharacterFade</c> (Target, FadeAmount, fadeTime and the output <c>ID</c>): the client body takes the next fade id from a
    /// counter shared by all fades (16 bits), sends packet 0xBA (id, fadeTime, FadeAmount) and returns the id so the script can pop the layer later.
    /// </summary>
    private void PushCharacterFade(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        // Reconstruction: a stats hook is polled on the 0.25 s buff grid here (retail's poll timing, when the stat accumulators are rebuilt, is not recovered), and
        // VayneTumbleFade pushes its 0.2 fade from BuffOnUpdateStats as well as from OnBuffActivate; polled, that is a new fade layer and a
        // 0xBA every poll for as long as the stealth lasts. The activation push is the one the client needs.
        if (_host.HasFrame && _host.Frame.Hook == "BuffOnUpdateStats") return;
        var id = _host.NextFadeId();
        var opacity = (float)Sum(call, "FadeAmount");
        var seconds = (float)Sum(call, "fadeTime");
        if (opacity >= 1.0f) unit.ScriptFades.Clear();
        unit.ScriptFades.Add((id, opacity, seconds));
        if (Spells.Effects.IsReferenceReady(unit))
            Spells.Effects.SendCharacterFade(unit, id, opacity, seconds);
        StoreOut(call, "ID", (double)id);
    }

    /// <summary><c>BBPopCharacterFade</c> (Target and the integer <c>ID</c>): packet 0x34 removes that fade layer.</summary>
    private void PopCharacterFade(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        var id = checked((short)(int)Sum(call, "ID"));
        unit.ScriptFades.RemoveAll(layer => layer.Id == id);
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendCharacterFade(unit, id);
    }

    /// <summary>
    /// <c>BBOverrideAnimation</c> (Owner, ToOverrideAnim, OverrideAnim) and <c>BBClearOverrideAnimation</c> (Owner,
    /// ToOverrideAnim): the client body stores or removes one entry of the unit's animation override map (the key lower-cased). The
    /// server's copy of the map is replicated whole with packet 0x6E after each change.
    /// </summary>
    private void OverrideAnimation(BbCall call, bool set)
    {
        if (Unit(call, "OwnerVar") is not { } unit || call.Value("ToOverrideAnim") is not string original) return;
        var key = original.ToLowerInvariant();
        if (set)
        {
            if (call.Value("OverrideAnim") is not string replacement) return;
            unit.ScriptAnimationOverrides[key] = replacement;
        }
        else if (!unit.ScriptAnimationOverrides.Remove(key)) return;
        if (Spells.Effects.IsReferenceReady(unit))
            Spells.Effects.SetAnimationStates(unit, unit.ScriptAnimationOverrides.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    /// <summary>
    /// <c>BBStopCurrentOverrideAnimation</c> (Target, AnimationName, Blend default true): the client body sends the 0x2B unlock packet
    /// BBUnlockAnimation sends (the clip name is parsed and not put on the wire) and resets the unit's animation state.
    /// </summary>
    private void StopCurrentOverrideAnimation(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit || !Spells.Effects.IsReferenceReady(unit)) return;
        Spells.Effects.StopAnimation(unit, Flag(call, "Blend", true));
    }

    /// <summary>
    /// The clock <c>BBGetGameTime</c> returns: the match clock snapped to 1/1024 s. Reconstruction: retail's game time is a float32 of seconds (the
    /// client's handler returns float32(clock - start)), but the frame grid that its reads land on is not in the shipped files. Read as the float32 of the 1/30 s tick sum, two reads 90 ticks apart fall
    /// under 3 s (scripting.bb-game-time-keeps-an-interval-of-whole-ticks fails, and two Soul Shackles checks with it), so the
    /// snap stays.
    /// Ours adds 1/30 s per tick in a double, a buff of three seconds ends 90 ticks after it began, and the difference of the two clock readings
    /// is then 3.0 give or take the rounding of the sums (2.99999999999983 at 117 s): Soul Shackles' <c>TimeElapsed >= 3</c> of its
    /// <c>OnBuffDeactivate</c> failed on it every time and dropped the second damage and the stun. On a binary grid the readings of two
    /// ticks that are N ticks apart differ by exactly N/30 rounded to the grid, so an interval of whole ticks keeps its length; the
    /// error of the snap (half of 1/1024 s at most, 0.5 ms) is a sixtieth of a tick.
    /// </summary>
    internal static double GameTimeSeconds(double clock) => Math.Round(clock * 1024.0) / 1024.0;

    /// <summary>
    /// The four shield natives (client bodies: Amount, MagicShield, PhysicalShield, Unit, and NoFade for BBModifyShield). The client adds the amount, or
    /// minus the amount for BBReduceShield and BBRemoveShield (their bodies are identical), to the unit's physical, magic or combined shield value
    /// and sends the 0x69 packet; the scripts do the absorbing themselves in BuffOnPreDamage (Black Shield, Hexdrinker, Mana Barrier
    /// subtract their hit points from DamageAmount), so the native only keeps the displayed shield in step. The engine has no shield value of its
    /// own for these (its payload shields are the hand-written ones): the packet is all there is.
    /// </summary>
    private void ChangeShield(BbCall call, double sign)
    {
        if (Unit(call, "UnitVar") is not { } unit) return;
        var amount = sign * Sum(call, "Amount");
        if (amount == 0.0 || !Spells.Effects.IsReferenceReady(unit)) return;
        Spells.Effects.SendShieldDelta(unit, amount, physical: Flag(call, "PhysicalShield", false), magic: Flag(call, "MagicShield", false),
            noFade: Flag(call, "NoFade", false));
    }

    /// <summary>
    /// <c>BBGetOffsetAngle</c> (Unit, OffsetPoint, the output <c>OutputAngle</c>): the angle in degrees between the unit's
    /// facing and the direction to the point (the body takes the dot products of the normalised difference with the unit's facing basis and
    /// converts atan2 to degrees). The sign is the one <c>BBGetPointByUnitFacingOffset</c> turns back: rotating the facing by this angle with
    /// the rotation points at the point. That round trip fixes the atan2 operands: the rotation turns by theta with x' = x cos + z sin
    /// and z' = z cos - x sin, so theta = atan2(dx*fz - dz*fx, dx*fx + dz*fz). The x87 code hides the operands themselves; the standard
    /// orientation is the only one that makes a 90 degree RotateAroundY a real rotation.
    /// </summary>
    private void GetOffsetAngle(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBGetOffsetAngle has no unit");
        var point = call.Table("OffsetPointVarTable", create: false)[call.Value("OffsetPointVar")] switch
        {
            Entity other => other.Position,
            BbTable table => BbSpellHost.Ground(table),
            _ => throw new BbRuntimeException("BBGetOffsetAngle has no offset point"),
        };
        var dx = point.X - unit.Position.X;
        var dz = point.Z - unit.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var angle = 0.0;
        if (length > 1e-9)
        {
            dx /= length;
            dz /= length;
            angle = Math.Atan2(dx * unit.Facing.Z - dz * unit.Facing.X, dx * unit.Facing.X + dz * unit.Facing.Z) * 180.0 / Math.PI;
        }
        StoreOut(call, "OutputAngle", angle);
    }

    /// <summary>
    /// <c>BBIsInBrush</c> (Unit, the output <c>Result</c>): the client asks the unit (a virtual call, the brush check) and also
    /// counts the fake-brush status bit (bit 18 of the status word); the navigation grid's brush test with the
    /// unit's selection radius is the same test. No shipped file names <c>SetBrushVisibilityFake</c> or a variant, so no shipped script sets that
    /// bit, and it is not modelled (model it only if a script starts to set it).
    /// </summary>
    private void IsInBrush(BbCall call)
    {
        var unit = Unit(call, "UnitVar");
        StoreOut(call, "Result", unit is not null && Spells.Movement.Navigation.BrushIdAt(unit.Position, unit.SelectionRadius) is not null);
    }

    /// <summary><c>BBGetPetOwner</c> (Pet, the output <c>Dest</c>): the unit that owns the pet; Nothing when it has none.</summary>
    private void GetPetOwner(BbCall call)
    {
        var pet = Unit(call, "PetVar");
        var owner = pet is not null && pet.OwnerNetId != 0 ? Spells.World.Get(pet.OwnerNetId) : null;
        StoreOut(call, "Dest", owner ?? (object?)BbScope.NothingUnit);
    }

    /// <summary>
    /// <c>BBForEachChampion</c>: the sub-blocks run once for every champion of <c>Team</c> (TEAM_UNKNOWN: both teams), alive or not, with the
    /// iterator set, filtered by <c>BuffNameFilter</c>/<c>InclusiveBuffFilter</c> like the area iteration. The client walks the
    /// global hero list in order, takes every hero for team 0 and the team's otherwise, applies the buff filter
    /// (the buff filter) and has no life test. The shipped uses give every other champion of the team a permanent bonus or penalty.
    /// </summary>
    private void ForEachChampion(BbCall call)
    {
        var team = call.Value("Team") is double teamNumber ? (BbTeam)(int)teamNumber : BbTeam.TEAM_UNKNOWN;
        var iterator = call.Value("IteratorVar") ?? throw new BbRuntimeException("BBForEachChampion has no IteratorVar");
        var buffFilter = call.Value("BuffNameFilter") as string;
        var inclusive = Flag(call, "InclusiveBuffFilter", true);
        var picked = Spells.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Hero &&
            (team == BbTeam.TEAM_UNKNOWN || TeamMapping.ToNative(unit.Team) == (double)team)).ToList();
        if (buffFilter is not null)
            picked = picked.Where(unit => unit.Buffs.Any(buff => string.Equals(buff.Name, buffFilter, StringComparison.OrdinalIgnoreCase)) == inclusive).ToList();
        foreach (var unit in picked)
        {
            call.Scope.Vars[iterator] = unit;
            call.ExecuteSubBlocks();
        }
    }

    /// <summary>
    /// <c>BBGetNumberOfHeroesOnTeam</c>: the heroes of <c>Team</c> counted into <c>DestVar</c>. The turret characters are
    /// the callers (batch 159 step 2): <c>TurretBonusHealth</c> scales a turret's health by the enemy team's size and
    /// <c>TurretChampionDelta</c> compares the two teams to pick its buff. <c>IncludeBots</c> and
    /// <c>ConnectedFromStart</c> are read and accepted: this engine's roster is a list of seats that a bot drives or a
    /// person does (both are real team members, as the flag says) and every roster hero was connected from the start —
    /// the roster is fixed when the match opens and a reconnect reuses its own hero — so neither flag changes the count.
    /// The one hero that is not a team member is the /dummy training champion, which the server adds and which stays out
    /// of it. DIAGNOSTICS: retail has no /dummy training champion (a debug addition), so it stays out of the count and a real team keeps its size. Reconstruction: the native is
    /// server-only; the client body is not in the build, and nothing but these two scripts passes either flag.
    /// </summary>
    private void GetNumberOfHeroesOnTeam(BbCall call)
    {
        var team = call.Value("Team") is double teamNumber ? (BbTeam)(int)teamNumber : BbTeam.TEAM_UNKNOWN;
        _ = Flag(call, "IncludeBots", true);
        _ = Flag(call, "ConnectedFromStart", true);
        var count = Spells.World.Entities.Values.Count(unit => unit.UnitType == UnitType.Hero &&
            (team == BbTeam.TEAM_UNKNOWN || TeamMapping.ToNative(unit.Team) == (double)team) && !unit.IsTrainingDummy);
        StoreOut(call, "Dest", (double)count);
    }

    private void ApplyAssistMarker(BbCall call)
    {
        // The assist-marker native reads Duration and, when bounty.ini's AssistDurationOverride is non-zero, replaces it
        // (15 s in this build, CombatService.AssistCreditSeconds); the support credit therefore ignores the 10 s the scripts pass.
        if (Unit(call, "TargetVar") is not { } target || Unit(call, "SourceVar") is not { } source) return;
        var duration = Sum(call, "Duration");
        // Batch 149 step 4: the marker of a recipient whose adds are one admission group waits for that recipient's group
        // (BbBuffRuntime.CommitProcAdds). Randuin's cast hook marks every unit it visits after its Slow/BlackOmen pair, and
        // the script has no branch on whether that pair landed; a declined group leaves no debuff and gets no credit (the
        // hand-written active skipped a saturated recipient whole). The marker itself is bookkeeping, so it keeps the
        // script's own place in the run and is only deferred.
        if (_host.HasFrame && _host.Buffs.HoldsAdds(_host.Frame))
        {
            (_host.Frame.HeldMarkers ??= new List<(Entity, Entity, double)>()).Add((target, source, duration));
            return;
        }
        Spells.Combat.RecordSupportAssist(source, target, duration);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpellBuffAdd
    // ------------------------------------------------------------------------------------------------------------

    private void SpellBuffAdd(BbCall call)
    {
        var target = Unit(call, "TargetVar");
        if (target is null) return;
        // A placemarker that applies a buff to another unit (Propel's control, whose AttackerVar is the placemarker that casts it) applies as the unit
        // that owns it (see PlacemarkerApplier); every other add keeps its attacker, and the target when it names none. A placemarker that applies a
        // buff to its own owner keeps its attacker: Gragas's barrel adds GragasBarrelRollBoom to him with the barrel as AttackerVar, and the shipped
        // hook reads that Attacker as the blast centre and as the unit the blast ends (the barrel). Reconstruction: retail's caster of a placemarker's
        // buff on its own owner is not in the shipped files; the barrel's script is the evidence.
        var applier = Unit(call, "AttackerVar");
        var applierOwner = applier is { Placemarker: true } ? PlacemarkerApplier(applier) : null;
        // A placemarker that applies a buff to another placemarker is a link between two of its own posts, not a caster's buff: Wall of Pain's
        // WallOfPainBeam is added to the last post with AttackerVar Other1 (the first post), and its scripts bind the posts and the beam to Attacker,
        // so the applier stays that post (finding 36; the owner rule read Karthus there).
        var attacker = applier is { Placemarker: true } && applierOwner is not null && !ReferenceEquals(applierOwner, target) && !target.Placemarker
            ? applierOwner
            : applier ?? target;
        // Reconstruction: a placemarker belongs to the first unit that applies a buff to it (PropelTimer adds PropelSpellCaster with
        // AttackerVar Owner, the Worm). The placemarker's own buffs end before its spells land, so the owner is what outlives them.
        if (target.Placemarker && target.OwnerNetId == 0 && !ReferenceEquals(attacker, target)) target.OwnerNetId = attacker.NetId;
        // Without a BuffName the buff is named after the running script: Disintegrate's TargetExecute adds the Disintegrate
        // buff whose BuffOnDeath refunds the mana (the spell and its mark share one script file).
        var name = call.Value("BuffName") as string ?? _host.Frame.ScriptName;
        // NumberOfStacks (E29): that many stacks in one add, each a stack of its own (BbBuffRuntime.Add). A count below one adds nothing
        // (RegenerationPotion and FlaskOfCrystalWater name the count in a variable that can hold 0). Batch 576: a call that names no count
        // adds one. The add native reads NumberOfStacks as an optional integer (default 1, not
        // required), and the shipped BBLuaConversionLibrary's TutorialSpellBuffAdd names it NumberStacks, so every buff the
        // TutorialScript adds (TutorialPlayerBuff, Stun, SetNoRender, ExpirationTimer, ...) was dropped here.
        var stacks = call.Value("NumberOfStacks") is null && call.Value("NumberOfStacksVar") is null ? 1
            : (int)Math.Round(Math.Clamp(Sum(call, "NumberOfStacks"), 0.0, 1000.0));
        if (stacks < 1) return;
        // The add native reads BuffVarsTable with the default "NextBuffVars"; the buff gets a copy of that table.
        var vars = new BbTable();
        // Reconstruction (batch 430): a spell's own self buff, added by its SelfExecute, also starts from the cast's InstanceVars (NextBuffVars
        // wins on a shared key). CarpetBomb's SelfExecute sets TickDuration = Duration / TickAmount into InstanceVars, and its self buff's
        // BuffOnUpdateActions reads it back as the BBExecutePeriodically period. Handed over only NextBuffVars, the period was 0 and the trail dropped
        // a zone every server step (26 zones at the max range, where TickAmount 15 is the script's count). Retail's hand-off is not in the shipped
        // files; the shipped script's own clock is the evidence.
        if (_host.Frame is { Hook: "SelfExecute" } && name == _host.Frame.ScriptName && call.Scope.InstanceVars is { } cast)
            foreach (var entry in cast.Entries) vars[entry.Key] = entry.Value;
        var varsName = call.Value("BuffVarsTable") ?? "NextBuffVars";
        foreach (var entry in BbBaseLibrary.GetTable(call.Scope, varsName, create: false).Entries) vars[entry.Key] = entry.Value;
        var request = new BbBuffRequest(target, attacker, name,
            (int)(call.Value("BuffAddType") as double? ?? 0.0), (int)(call.Value("BuffType") as double? ?? 0.0),
            (int)(call.Value("MaxStack") as double? ?? 1.0), BuffAddDuration(name, Sum(call, "Duration")),
            call.Value("TickRate") as double? ?? 0.0,
            // The add native parses CanMitigateDuration as an optional boolean with the default false (StacksExclusive defaults to true,
            // IsHiddenOnClient to false). 288 of the 2723 shipped hook adds omit the key and 19 name it true. Nothing reads it since batch 296:
            // tenacity is Hardening.luaobj's BuffOnAllowAdd, which tests no such flag (Reconstruction: retail's server consumer is not recovered).
            Flag(call, "CanMitigateDuration", false), vars, stacks, Flag(call, "StacksExclusive", true),
            // The same function parses IsHiddenOnClient (default false; 48 shipped adds say true): the isHidden byte of the add's 0xBF.
            Flag(call, "IsHiddenOnClient", false));
        _host.Buffs.Add(request);
    }

    /// <summary>
    /// Batch 520 (NOT RETAIL; owner finding, 31 October 2026: the Autumn rift's Urf ghost "cuts off abruptly", the rise/vanish loop
    /// itself). CharScriptUrf adds HalloweenUrfAppear for 6.25 s (pc 0147), and the buff's OnBuffDeactivate hides him at once (SetNoRender
    /// true, BBUnlockAnimation, the eight ghostUrf.troy removed; HalloweenUrfAppear pc 0203-0273). Its OnBuffActivate plays Idle1
    /// (Loop false, ScaleTime 0), which is <c>Urf_idle1.anm</c>: 199 frames at 30 fps, a rise from the grave, a bob, a swoop up to
    /// root y 380 (frame 178) and a dive back under it (y -153 at frame 197, -10014 on the last frame 198, 6.6 s). The glow's own life
    /// is 6.5 s (ghostUrf.troybin <c>glow*e-life</c>). So at 6.25 s the shipped buff hid him at frame ~187, about 280 units up in the
    /// middle of the dive. Here the appear lasts until the clip's last, underground frame: 6.7 s, one 0.1 s margin past 6.6 s for the
    /// server step and the delivery jitter (the clip is locked, so the client holds that frame until the hide). Every other buff keeps
    /// its script's duration; HalloweenUrfCD (9 s) still spaces the reveals 10 s apart.
    /// </summary>
    private static double BuffAddDuration(string name, double duration) =>
        name == "HalloweenUrfAppear" ? Math.Max(duration, UrfAppearClipSeconds) : duration;

    /// <summary>The last frame of <c>Urf_idle1.anm</c> (198 / 30 s) plus a 0.1 s margin (<see cref="BuffAddDuration"/>).</summary>
    private const double UrfAppearClipSeconds = 6.7;

    /// <summary>
    /// The unit that owns a placemarker: the first unit that applied a buff to it (the owner rule in SpellBuffAdd; PropelTimer's
    /// PropelSpellCaster is applied with AttackerVar Owner, the Worm). Batch 174 part 2, Reconstruction: the exe does not say how
    /// retail credits a placemarker's adds; the placemarker leaves the world when its geyser ends, so its control and that control's
    /// landing hit need an owner that outlives it.
    /// </summary>
    private Entity? PlacemarkerApplier(Entity placemarker) => _host.Spells.World.Get(placemarker.OwnerNetId);

    // ------------------------------------------------------------------------------------------------------------
    // BBSpellEffectCreate (the client's handler returns at once, so the parameters are the shipped scripts', Reconstruction)
    // and BBSpellEffectRemove (EffectIDVar and EffectIDVarTable)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The flat unit vector from <paramref name="from"/> to <paramref name="to"/> (the 0x8C direction); zero with no point or when the point is <paramref name="from"/>.</summary>
    private static Position3 FlatDirection(Position2 from, Position2? to)
    {
        if (to is not { } point) return default;
        var dx = point.X - from.X;
        var dz = point.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return length <= 1e-6 ? default : new Position3(dx / length, 0.0, dz / length);
    }

    // Reconstruction (batch 397): a script's placemarker cube is the Placemarker flag, or the TestCube / SpellBook1 cube the shipped Akali and Brand
    // scripts spawn (BrandFissure's BBSpawnMinion passes Placemarker false: the spawn trace of the A/B runs). The cube is NoRender, so an effect bound
    // to it never enters the client's view (see SpellEffectCreate). Only a NoRender one: a placemarker that draws its own model (Orianna's ball) keeps
    // its bound effects, which stand on the model (oriana_ball_glow, yomu_ring).
    // Batch 677 (GitHub issue #2): or a cube the client hides on its own. A NeverRender character is NoRender on the client from its creation
    // (sets bit 13 of +0xA1C), and a ServerOnly one gets no replication manager (batch 464), so no server
    // word lifts it or adds ForceRenderParticles: its bound particles are never drawn, whenever the script sets NoRender.
    // Nasus's Spirit Fire binds its three effects to a SpellBook1 cube before the server holds the cube's NoRender (SpiritFire.OnBuffActivate
    // creates the warning before its SetNoRender, and the statuses of an activation reach the unit when the hook ends), so they went out bound.
    private bool IsPlacemarkerCube(Entity unit) => (unit.Placemarker || unit.Name is "TestCube" or "SpellBook1" || IsTestCubeCharacter(unit)) &&
        ((Amberline.Simulation.Buffs.CharacterState.ActionWord(unit) & Amberline.Simulation.Buffs.CharacterState.NoRenderBit) != 0 ||
         unit.ChampionName is { } character && Spells.Data.OptionalChampion(character) is { NeverRender: true, ServerOnly: true });

    // Batch 668: Dominion's opening barrier keeps the script's bind (OdinOpeningBarrierParticle.luaobj, BindObjectVar Owner). Batch 521 sent it
    // unbound to keep the blur after the camera looks away, but the client drops the particles of any system that leaves the view unless the troy
    // sets SimulateWhileOffScreen, bound or not, and SetForceRenderParticles only lets a NoRender
    // unit draw its particles. So retail loses the wall's one-shot layers too.

    // Batch 431 (Reconstruction, batch 431 evidence): Xerath's launcher cube (skin XerathArcaneBarrageLauncher, Placemarker false) runs the shipped
    // CharScriptTestCube, the TestCube placemarker script (its CharOnActivate sets NoRender on the cube). Its warning effect was sent bound to a cube the
    // client does not draw. The script's own character file decides it. Read only for a NoRender unit, so the file lookup stays off the common path.
    private bool IsTestCubeCharacter(Entity unit) =>
        (Amberline.Simulation.Buffs.CharacterState.ActionWord(unit) & Amberline.Simulation.Buffs.CharacterState.NoRenderBit) != 0 &&
        _host.Router?.CharacterScriptPath(unit.Name) is { } path && path.EndsWith("/CharScriptTestCube.luaobj", StringComparison.Ordinal);

    private void SpellEffectCreate(BbCall call)
    {
        if (call.Value("EffectName") is not string effect) return;
        // The stats sample before a buff exists creates nothing (BbHookFrame.PreActivation): its id would be overwritten by the activation's.
        if (_host.HasFrame && _host.Frame.PreActivation)
        {
            if (call.Value("EffectIDVar") is { } skipped) call.Table("EffectIDVarTable", create: true)[skipped] = 0.0;
            return;
        }
        var bound = Unit(call, "BindObjectVar");
        var targetObject = Unit(call, "TargetObjectVar");
        // PosVar and TargetPosVar name a point or a unit, read as the client's shared position getter reads them (BbSpellHost.Ground) and as
        // SpawnPosition reads a spawn's PosVar. A unit names its own position: Gragas's barrel boom names the barrel (PosVar 'Attacker') and Vladimir's nova
        // the victim (PosVar 'Target'). Without PosVar the source falls back to the bind object, then the target object, then the owner; without TargetPosVar
        // the target point is the target object's position (the 0x8C target field, e.g. Galio's travel path).
        var sourceOperand = PositionOperand(call, "PosVar");
        var targetOperand = PositionOperand(call, "TargetPosVar");
        var position = PositionOf(sourceOperand) ?? (bound ?? targetObject ?? _host.Frame.Owner)?.Position
            ?? throw new BbRuntimeException("BBSpellEffectCreate has no position");
        var targetPoint = PositionOf(targetOperand) ?? targetObject?.Position;
        // OrientTowardsVar names the point or unit the create turns to: Cassiopeia's CassDeathDust, Galio's wind tunnel rune and Nocturne's frost path name
        // a point, Oriana's ghosts and Riven's trails name the owner. The 0x8C direction is the flat unit vector from the effect to it. Batch 582 decoded
        // the client's use: a non-zero direction becomes the emitter's frame (local z = the
        // direction, y = up) and a zero one leaves the emitter unturned; flag 0x40 (FollowsGroundTilt; not sent: Galio's create sets it false) would first project it on
        // the ground's normal. Galio's wind tunnel leaves (galio_windTunnel_rune: e-local-orient, p-vel (0, 0, 700)) blow along that
        // local z, so the zero direction sent before batch 364 laid the tunnel along the world axis (owner finding 18, game of 26 October).
        var direction = FlatDirection(position, PositionOf(PositionOperand(call, "OrientTowardsVar")));
        // Reconstruction: retail's server sending of every name is not recovered; the client logs a missing name and draws nothing for
        // one that is not in DATA/Particles/troy.filelist (Taric_HammerInternal.troy, which the shipped TaricHammerInternal script
        // creates, has no particle file). Sending the packet only fills the client log, so the name is dropped here; the script's
        // effect id stays 0 and BBSpellEffectRemove of it is a no-op.
        var known = Spells.Data.HasClientParticle(effect);
        // Batch 401 (finding 127): a copied torch (BbBuffRuntime.IsCopiedEffectCreate, Warwick's Meditate_eff) is sent like an unknown name is
        // dropped: no packet, the id stays 0, no frame entry. `known` stays the shipped-list fact for the sweep's tap. Owner decision, 27 October 2026:
        // foreign torches copied from another champion are bugs.
        var copied = known && BbBuffRuntime.IsCopiedEffectCreate(_host.HasFrame ? _host.Frame.ScriptName : null, effect);
        var sent = known && !copied;
        // Who sees it: the other-team name, the team and single-client filters (BbNativeHandlers.Effects.cs).
        var audience = sent ? AudienceOfEffect(call, effect, bound, targetObject) : default;
        // DIAGNOSTICS: the spell sweep's FX rule reads every create a script runs, including the ones the drops below keep off the wire, with the
        // audience each one has (the caster's client may be outside it).
        EffectCreateTap?.Invoke(new EffectCreateEvent(effect, bound?.NetId ?? 0, targetObject?.NetId ?? 0, position, sourceOperand is BbTable,
            Premature(bound) || Premature(targetObject), known, audience.Allied, audience.OnlyHero, audience.EnemyName is not null,
            bound is { } tapped && IsPlacemarkerCube(tapped), copied));
        // FOWVisibilityRadius and SendIfOnScreenOrDiscard have no counterpart on the sink; FOWTeam names the own team of the other-team name
        // (BbNativeHandlers.Effects.cs) and (batch 473) the effect's own fog-of-war team, which gets an effect on no current unit wherever it is
        // (ReplicationDelivery.CanRouteEffectCreate). An effect bound to a placemarker cube is sent unbound (below) and keeps the audience its
        // binding gave it, the cube's team, when the script names no FOWTeam (Rumble's RumbleCarpetBombSoundEnd).
        // The third position stays the target object's: the native effect handler applies the packet's third position to its target object before it
        // creates the effect, so the bind object's or TargetPosVar's point there would drag the target object to it. The target
        // point (the 0x8C target field) is TargetPosVar's, else the target object's.
        // Rocket Grab binds FistReturn_mis to the victim and targets Blitzcrank's hand (the 24 September fix, "FistReturnDoesNotMoveItsTarget").
        // An effect bound to a unit whose id the script keeps and later removes (BBSpellEffectRemove of the same variable: Vayne's speed trail and
        // Silver Bolts rings, the auras of Taric and Kayle) lives as long as its buff, so an observer who first sees the unit meanwhile gets it too
        // (the persistent-effect replay of the replication layer). Reconstruction: how retail's client learns an effect that was created before it
        // saw the unit is not in the exe; the hand-written Vayne replayed these two visuals from the buff ledger (playtest B2 YV3).
        // The lifetime is the script's whether or not the effect is bound: Teleport_Target's Teleport_target.troy is grounded
        // (BindObjectVar 'Nothing', SummonerTeleport adds the buff on the unit it arrives at) and is still removed when the channel ends,
        // so a late observer gets it the same way. An effect the script never removes stays fire-and-forget (Summoner_Cast.troy).
        var persistent = RemovedLater("EffectIDVar") ||
            (audience.EnemyName is not null && RemovedLater("EffectID2Var"));
        // A placemarker is announced like a minion (SpawnGeneric), so an effect bound to it is bound on the wire: the soil particle of Tormented Soil
        // stands on its hidden cube, and the delivery layer sends it to the observers that know the cube (CanRouteEffectCreate).
        // An effect that names a unit the clients were never given is routed to nobody
        // (ReplicationDelivery.CanRouteEffectCreate refuses an entity reference the observer does not know) but still takes an id
        // from the allocator the whole wire shares. Batch 159 part 2: a jungle member's character script runs before the create
        // burst that announces it (JungleScheduler.SpawnMember binds first so the burst can wire the buffs the script added), so the
        // buff activations of that bind create effects naming a unit nobody has heard of. The particle is not lost — the burst draws
        // it itself (NeutralWire.AddBuff's troy of the same name) — and dropping the create keeps the id for the effect that is sent,
        // so the script's EffectIDVar stays 0 and its BBSpellEffectRemove is the no-op the PreActivation rule above leaves. Reconstruction:
        // retail's activation order is not recovered (believed to run once the unit is on the wire, where its effect is the one that is created).
        if (Premature(bound) || Premature(targetObject))
        {
            if (call.Value("EffectIDVar") is { } held) call.Table("EffectIDVarTable", create: true)[held] = 0.0;
            return;
        }
        // FacesTarget: wire flag 0x0080, the emitter's per-frame target-facing branch, recorded in docs/maintenance/2026-09-24-orianna-native-retest.md; the
        // effect keeps following its source and target, so a replay for a late observer reads their positions at delivery.
        var faces = Flag(call, "FacesTarget", false) && targetObject is not null;
        // Batch 479b (proven in the client: the flags word's 0x100 binds a PARFlexValue to the effect):
        // BindFlexToOwnerPAR (Odin_Prism_Red/Green, the centre crystals' shards) sets it on the wire. The shipped scripts that set it bind the
        // flex to their owner's PAR, which the wire carries for the unit (ProductionMatchComposition.ReplicatesPar).
        var barePar = Flag(call, "BindFlexToOwnerPAR", false);
        // Batch 147 step 4: a proc hook run that holds its adds as one admission group holds its particles with them
        // (BbBuffRuntime.CommitProcAdds), so a group that cannot get every slot it needs draws none of the trigger effects
        // that stand before its adds in the script. The effect id a held create leaves is written when it is made.
        if (_host.HasFrame && _host.Buffs.HoldsProcWork(_host.Frame))
        {
            (_host.Frame.HeldEffects ??= new List<Action>()).Add(CreateNow);
            return;
        }
        CreateNow();

        void CreateNow()
        {
            // Reconstruction (batch 397): an effect the script binds to a placemarker cube is sent unbound, at the cube's position (bind 0; the target
            // is the cube's own point, see the batch 398 note below). Sent bound, the NoRender cube keeps its effect out of the client's view: on the throwaway client the
            // Akali smoke and the Brand ring showed at the cast point only this way; see docs/maintenance/2026-10-27-batch-397-area-fx-ab.md.
            // Batch 398: a target that is another unit (Karthus's wall beam aims at Karthus) keeps its net id, point and live positions, as a bound
            // effect's does; only a target that IS the cube goes to 0 (with the cube's point). Reconstruction: the wire of an unbound effect with a unit
            // target is not recovered; this takes the bound effect's target fields.
            var placemarker = bound is { } cube && IsPlacemarkerCube(cube) ? cube : null;
            // Batch 431 (Reconstruction): a placemarker effect aimed at another placemarker cube (Karthus's wall beam, each post's target) names that
            // cube's point, not its net id. The stock client places a unit end at the unit's own position, which a post inside a building can leave
            // (the beam then moves off its pillar); a point stays where the script put the cube.
            var postTarget = placemarker is not null && targetObject is not null && !ReferenceEquals(targetObject, placemarker) && IsPlacemarkerCube(targetObject) ? targetObject : null;
            var beam = placemarker is not null && targetObject is not null && !ReferenceEquals(targetObject, placemarker) && postTarget is null ? targetObject : null;
            // Batch 419 (Reconstruction, batch 418 evidence): a bound effect whose unit is dead when it goes on the wire is sent as the unbound variant of the
            // placemarker rule above, at the unit's point, when the unit is a script- or pet-owned non-hero (ReplicationDelivery.SendsUnboundWhenDead,
            // decided at Flush after the step's death). The fallback is that variant.
            var hostBeam = placemarker is null && bound is not null && targetObject is not null && !ReferenceEquals(targetObject, bound) ? targetObject : null;
            UnboundEffectFallback? fallback = placemarker is null && bound is { } host
                ? new UnboundEffectFallback(host.Position, hostBeam is not null ? targetPoint : host.Position, hostBeam?.NetId ?? 0, hostBeam?.Position)
                : null;
            var id = sent ? Spells.Effects.CreateEffect(effect, placemarker?.Position ?? position, target: beam is not null || postTarget is not null ? targetPoint : placemarker?.Position ?? targetPoint,
                targetNetId: placemarker is null ? (targetObject?.NetId ?? 0) : (beam?.NetId ?? 0), bindNetId: placemarker is null ? (bound?.NetId ?? 0) : 0, direction: direction,
                ownerPosition: placemarker is null ? targetObject?.Position : (beam ?? postTarget)?.Position,
                alliedTeam: audience.Allied, enemyEffectName: audience.EnemyName, ownerHeroNetId: audience.OnlyHero,
                boneName: call.Value("BoneName") as string, persistent: persistent,
                livePositions: (placemarker is null || beam is not null || postTarget is not null) && faces && persistent ? () => (PositionOf(sourceOperand) ?? (bound ?? targetObject)!.Position,
                    PositionOf(targetOperand) ?? targetObject!.Position) : null,
                flags: (ushort)((faces ? 0x0080 : 0) | (barePar ? 0x0100 : 0)), targetBoneName: call.Value("TargetBoneName") as string, unbound: fallback,
                fowTeam: EffectTeam(call, "FOWTeam", "FOWTeamOverrideVar") ?? placemarker?.Team) : 0u;
            // Batch 398 (Reconstruction): an unbound placemarker effect the script never removes ends with its cube. The client's bound emitter ends with its
            // unit, and this effect is sent unbound, so BbBuffRuntime.OnUnitDeath kills it when the cube's corpse goes (batch 404).
            // Batch 419 (Reconstruction, batch 418 evidence): a cube that is already dead has had its OnUnitDeath, so its effect ends with its corpse from here.
            if (id != 0u && placemarker is not null && !persistent)
            {
                if (placemarker.Alive)
                {
                    if (!_host.PlacemarkerEffects.TryGetValue(placemarker.NetId, out var placed)) _host.PlacemarkerEffects[placemarker.NetId] = placed = new();
                    placed.Add(id);
                }
                else Spells.EndEffectWithUnitCorpse(id, placemarker.NetId);
            }
            if (sent && _host.HasFrame) _host.Frame.Effects.Add((effect, bound?.NetId ?? targetObject?.NetId ?? 0u));
            if (call.Value("EffectIDVar") is { } idName)
            {
                var table = call.Table("EffectIDVarTable", create: true);
                // Reconstruction: a stack of a buff runs OnBuffActivate again on the one shared instance and overwrites the id the previous stack stored
                // there (Rising Spell Force: a glow per stack in InstanceVars.Particle). Retail keeps an instance per stack, each removing its own effect when
                // it ends; the overwritten id is kept with the buff and killed when the buff goes (BbBuffRuntime.OnRemoved).
                if (id != 0u && _host.HasFrame && _host.Frame.Buff is { } instance && ReferenceEquals(table, instance.InstanceVars) &&
                    table[idName] is double old && old > 0.0 && (uint)old != id && !_host.KilledEffects.Contains((uint)old) &&
                    !instance.SupersededEffects.Contains((uint)old))
                    instance.SupersededEffects.Add((uint)old);
                table[idName] = (double)id;
            }
            // The retail pair has two ids (the own-team effect and the other-team effect); here it is one effect, so the second variable holds the
            // same id and a script that removes either one ends it (Monsoon removes both).
            if (audience.EnemyName is not null && call.Value("EffectID2Var") is { } id2Name)
                call.Table("EffectID2VarTable", create: true)[id2Name] = (double)id;
        }

        bool RemovedLater(string key) => call.Value(key) is string name && _host.Router.RemovesEffectVar(_host.Frame.ScriptName, name);
    }

    /// <summary>An ordinary unit whose create the wire has not sent yet. A placemarker is never premature: it is announced with its spawn
    /// (<see cref="Entity.Announced"/>), and the ball by its own projection.</summary>
    private static bool Premature(Entity? unit) => unit is { Placemarker: false, Created: false };

    /// <summary>A position variable's operand under the name its parameter gives: a point (a BbTable) or a unit (an Entity); null when the script names none.</summary>
    private object? PositionOperand(BbCall call, string key)
    {
        if (call.Value(key) is not { } name) return null;
        var operand = call.Table(key + "Table", create: false)[name];
        return operand is BbTable or Entity ? operand : null;
    }

    /// <summary>The position an operand names: the point itself, or a unit's position (the shared position getter, BbSpellHost.Ground).</summary>
    private static Position2? PositionOf(object? operand) => operand is null ? null : BbSpellHost.Ground(operand);

    private void SpellEffectRemove(BbCall call)
    {
        if (call.Value("EffectIDVar") is not { } idName || _host.HasFrame && _host.Frame.PreActivation) return;
        if (call.Table("EffectIDVarTable", create: false)[idName] is double id)
        {
            _host.KilledEffects.Add((uint)id);
            Spells.Effects.KillEffect((uint)id);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Resources, ids, positions
    // ------------------------------------------------------------------------------------------------------------

    // Batch 512: the shipped scripts whose BBIncPAR on a capture guardian the point's tree models itself (see IncPar). Batch 711: not
    // OdinMinionSpellAttack: the tree's minion +-9 came with its kill of the arriving minion, which no longer happens (BehaviorLeaves.KillUnit),
    // so each hit's 1 % to 3 % of the pool is the progress it shows (BehaviorTreeQuestHost's pending PAR, batch 694).
    private static readonly HashSet<string> TreeModelledMeterScripts =
        new(StringComparer.OrdinalIgnoreCase) { "OdinGuardianSuppression", "OdinGuardianBuff" };

    private void IncPar(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var delta = Sum(call, "Delta");
        // PARType picks the pool; a unit without that pool is untouched (heroes in the Annie closure carry mana). The native parses it
        // as an optional integer with the default 0, PAR_MANA, and calls the unit's IncPAR(type, delta): the 6 shipped calls that omit it
        // fill the mana pool of a mana hero and do nothing for an energy hero.
        var parType = call.Value("PARType") is double named ? named : (double)BbParType.PAR_MANA;
        if (_host.PowerType(target) != (BbParType)(int)parType) return;
        // Batch 512 (Reconstruction, owner "one timer", 31 October 2026): a capture guardian's pool is the point tree's progress
        // (BehaviorLeaves.CapturePointSetValue). OdinGuardianSuppression's drain and OdinGuardianBuff's regen keep running for their other
        // effects (particles, sounds, buffs) but do not move it: a second writer is what made the meter go back (batch 510).
        // Batch 694: those are the tree's own math (CapturePointMath's rate, the owner's regen; batch 711 took the minions' hit out). Any other script's PAR
        // has no tree counterpart (SummonerOdinGarrison's 800 a stats sample, "Quickly restores the neutralized portion"), so it goes to the point's
        // tree as progress, and the tree's CapturePoint_SetValue writes the pool. A stats hook's mutation lands on the buff's mutation clock (M4).
        if (target.ParIsCaptureMeter)
        {
            if (_host.HasFrame && !_host.Frame.MutationSuppressed && !TreeModelledMeterScripts.Contains(_host.Frame.ScriptName))
                target.CaptureMeterParPending += delta;
            return;
        }
        target.Mana = Math.Clamp(target.Mana + delta, 0.0, target.MaxMana);
        Spells.SendChampionStats(target);
    }

    private void IncHealth(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        var healer = Unit(call, "HealerVar");
        var delta = Sum(call, "Delta");
        // A negative delta is a health cost (Mordekaiser's Creeping Death and Syphon of Destruction, Enrage on hit): the scripts keep it below the
        // current health themselves ("HealthCost = health - 1", "health >= 15"), so it is not damage and not a heal: no mitigation, no incoming-heal
        // multiplier, no credit. The shared body (the BBIncHealth helper) parses Target, Delta and HealerVar.
        // For a healer that is an obj_AI_Base, it scales Delta by (1 + the healer's PercentHealingAmountMod, whose default is 1.0) before it
        // looks at the sign, so a cost is scaled too. Then it calls the heal-text routine (in the client only the "+%.0f" text for a positive delta).
        // Batch 292: the factor is 1 for every unit, because no shipped item, rune, character or script names PercentHealingAmountMod. So the
        // emulator leaves it out for both signs. Reconstruction: the server's half of that routine is not in the client. Whether it floors a
        // cost at 1 health is unknown, so the floor the scripts' own guards imply stays.
        if (delta < 0.0)
        {
            target.Health = Math.Max(Math.Min(target.Health, 1.0), target.Health + delta);
            if (target.UnitType == UnitType.Hero) Spells.SendChampionStats(target);
            else if (Spells.Effects.IsReferenceReady(target)) Spells.Effects.SendHealth(target, target.Health);
            return;
        }
        var before = target.Health;
        // The incoming-heal multiplier (Grievous-style reductions) applies to every heal, as for the hand-written heals.
        Healing.Apply(target, delta * Math.Max(0.0, Spells.Combat.HealingMultiplier(target)), healer);
        if (target.Health <= before) return;
        if (target.UnitType == UnitType.Hero) Spells.SendChampionStats(target);
        else if (Spells.Effects.IsReferenceReady(target)) Spells.Effects.SendHealth(target, target.Health);
    }

    private void IncMaxHealth(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } target) return;
        // The BBIncMaxHealth native: Target, Delta and IncCurrentHealth; the body adds Delta to the unit's
        // mFlatHPPoolMod when IncCurrentHealth is true and to m_NonHealingFlatHPPoolMod when it is false. A hero recomputes its maximum health from
        // the stat pipeline (HeroStatsService.Refresh) every update, which is how retail's pools behave: the stats hook of the buff (Renekton's Dominus,
        // Vladimir's Blood Gorged, Jax's equipment mastery) gives the pool through the sample, and a call from anywhere else on a hero is refused.
        if (target.UnitType == UnitType.Hero)
        {
            if (!_host.HasFrame || _host.Frame.Stats is not { } collector || !ReferenceEquals(target, _host.Frame.Owner))
                throw BbSpellHost.Unbound("IncMaxHealth on a hero outside its own BuffOnUpdateStats", "M2");
            collector.AddMaxHealth(Sum(call, "Delta"), Flag(call, "IncCurrentHealth", false));
            return;
        }
        // Retail's stats hook runs every update and its max-health pool is rebuilt each time; this engine adds the amount once,
        // with the first sample taken when the buff is added, and ignores the polls that follow (Reconstruction).
        if (_host.HasFrame && _host.Frame.IsResample) return;
        var delta = Sum(call, "Delta");
        target.MaxHealth += delta;
        if (Flag(call, "IncCurrentHealth", false)) target.Health = Math.Min(target.MaxHealth, target.Health + delta);
        Spells.SendChampionStats(target);
    }

    private static void GetSkinId(BbCall call)
    {
        var unit = Unit(call, "UnitVar");
        if (call.Value("SkinIDVar") is not { } name) return;
        call.Table("SkinIDVarTable", create: true)[name] = (double)(unit?.SkinId ?? 0);
    }

    /// <summary>
    /// <c>BBSpellCast</c> (the client body is a bare return, so the parameters Caster, Target, Pos, EndPos, the slot and the force/cooldown
    /// flags are read from the shipped call sites; the server does the cast). Reconstruction: the child of an extra
    /// slot or of the caster's own spell slot (a pet or box minion casting its spell) is run by <c>BbChampionModule.CastChild</c> by its
    /// <c>CastType</c> (0 effect at once, 1 homing missile, 2 chain missile, 3 line missile); the level is the call's <c>OverrideForceLevel</c> or else
    /// the parent's rank. <c>OverrideCoolDownCheck</c> true (281 of the 323 shipped calls) casts regardless of the slot's cooldown; false checks it and
    /// starts it (<c>BbChampionModule.CastChild</c>); mana is never charged (of the 42 checked calls only the Wukong decoy clone's Double Attack lists a cost). Inventory slots
    /// are only the disconnect trigger and stay unbound.
    /// </summary>
    private void SpellCast(BbCall call)
    {
        var caster = Unit(call, "CasterVar") ?? throw new BbRuntimeException("BBSpellCast has no caster");
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        // InventorySlots children are only the disconnect trigger: 83 shipped scripts cast inventory slot 6, all of them CharOnDisconnect or the
        // DisconnectTimer buff, which the disconnect hook runs itself (batch 184, `BbHookRouter.RunDisconnect`; special case 1). Any other type reads as a spell slot, as the client's parser does.
        if (type == BbSlotType.InventorySlots)
            return; // Reconstruction: the disconnect trigger (slot 6) has no effect of its own; the hook that cast it has already run.
        if (type != BbSlotType.ExtraSlots) type = BbSlotType.SpellSlots;
        var index = (int)Sum(call, "SlotNumber");
        StoredSlot("BBSpellCast", type, index);
        Position2 PointOf(string key) => call.Value(key) is { } name && call.Table(key + "Table", create: false)[name] is BbTable or Entity
            ? BbSpellHost.Ground(call.Table(key + "Table", create: false)[name]) : caster.Position;
        var at = PointOf("PosVar");
        var end = call.Value("EndPosVar") is null ? at : PointOf("EndPosVar");
        var caster2 = _host.ChildCaster ?? throw BbSpellHost.Unbound("BBSpellCast without a module that runs child casts", "M3");
        // OverrideForceLevel is read like every number (the client's number getter: the literal plus the variable, so "0, Var Level" is the variable); a
        // positive value is the level the child runs at, else the parent's rank. FireWithoutCasting skips the child's own windup.
        // ForceCastingOrChannelling is the cast packet's isForceCastingOrChannelling bit (0x04): an extra-slot cast without it runs at once with no
        // SpellCaster (the client's cast routine), so a child with a windup and a gesture (Yorick's three summons) must carry it.
        var level = HasNumber(call, "OverrideForceLevel") ? (int)Sum(call, "OverrideForceLevel") : 0;
        // OverrideCastPosition: the child starts at OverrideCastPosVar instead of at the caster (Orianna's Izuna and Redact leave from the ball); the
        // announced launch position and the missile's origin are that point.
        var overridePosition = Flag(call, "OverrideCastPosition", false);
        Position2? castFrom = overridePosition ? PointOf("OverrideCastPosVar") : null;
        // NOT RETAIL: crash containment (batch 280). A child with no cast time lands inside this call, so a child whose own hook casts it again
        // nests without end: DeathsCaress's BBElse (the second cast without its shield) casts extra slot 0, which is DeathsCaress itself. Retail's
        // handling is server-only and unknown; here, past MaxSpellCastNesting, the cast is a script failure, because a stack overflow ends the
        // whole process and no handler can catch it.
        if (_spellCastNesting >= MaxSpellCastNesting)
            throw new BbRuntimeException($"BBSpellCast nested {MaxSpellCastNesting} deep (slot {index} of {type}): a child that casts itself");
        _spellCastNesting++;
        try
        {
            caster2(new BbChildCast(caster, index, Unit(call, "TargetVar"), at, end, _host.Frame, level > 0 ? level : null,
                Flag(call, "FireWithoutCasting", false), type, Flag(call, "OverrideCoolDownCheck", true), castFrom,
                Flag(call, "ForceCastingOrChannelling", false), overridePosition));
        }
        finally { _spellCastNesting--; }
    }

    /// <summary>How deep <c>BBSpellCast</c> children may nest inside one another on this thread (every module's handlers share it).</summary>
    internal const int MaxSpellCastNesting = 16;

    [ThreadStatic] private static int _spellCastNesting;

    private void GetCastSpellTargetPos(BbCall call)
    {
        var at = _host.Frame.CastPosition ?? throw new BbRuntimeException("GetCastSpellTargetPos outside a spell hook");
        StoreOut(call, "Dest", BbTable.Point(at.X, at.Y, at.Z));
    }

    // ------------------------------------------------------------------------------------------------------------
    // M3: positions, facing and slot reads
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSetBuffCasterUnit</c>: stores the unit that cast the buff the running hook belongs to in <c>CasterVar</c> (of <c>CasterVarTable</c>). The
    /// client body is empty (a bare return), so the effect is the server's; the shipped use is a variable named after the thing the
    /// script wants to hold. <c>CasterVar="Caster"</c> (most of the shipped uses) finds the buff's caster there already; <c>CasterVar="Attacker"</c> is the case that
    /// needs the store: Kassadin's Force Pulse sits on every hero in range and, in its <c>BuffOnSpellCast</c>, names Kassadin (the buff's caster) as
    /// <c>Attacker</c> instead of the hero that cast, and adds the charge to him (Maokai's Sap Magic does the same). Without the store the charge
    /// went to the casting hero and the next buff end sealed that hero's E slot. Reconstruction: the variable keeps its value when the hook has no
    /// buff (a spell script's <c>TargetExecute</c> names Attacker the caster already) or the caster is gone from the world.
    /// </summary>
    private void SetBuffCasterUnit(BbCall call)
    {
        if (!_host.HasFrame || _host.Frame.Buff is not { } buff || _host.Spells.World.Get(buff.CasterNetId) is not { } caster) return;
        call.Table("CasterVarTable", create: true)[call.Value("CasterVar")] = caster;
    }

    private void GetUnitPosition(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBGetUnitPosition has no unit");
        // The position native stores the unit's position vector under the "Position" parameter.
        call.Table("PositionVarTable", create: true)[call.Value("PositionVar")] = _host.Point(unit.Position);
    }

    private void GetPointByUnitFacingOffset(BbCall call)
    {
        var unit = Unit(call, "UnitVar") ?? throw new BbRuntimeException("BBGetPointByUnitFacingOffset has no unit");
        var distance = Sum(call, "Distance");
        var angle = Sum(call, "OffsetAngle") * Math.PI / 180.0;
        // The offset-position native: the unit's facing vector turned by OffsetAngle, scaled by Distance and added to the position.
        // The angle is in degrees (the client looks the sine up in a 3600-entry table indexed by angle * 10, cosine is sin(90 - angle)) and
        // The client's rotation computes x' = x cos + z sin, z' = z cos - x sin, so a positive angle turns the facing from +z toward +x.
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var x = unit.Facing.X * cos + unit.Facing.Z * sin;
        var z = unit.Facing.Z * cos - unit.Facing.X * sin;
        var point = new Position2(unit.Position.X + x * distance, unit.Position.Z + z * distance);
        call.Table("PositionVarTable", create: true)[call.Value("PositionVar")] = _host.Point(point);
    }

    private void FaceDirection(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        var location = call.Table("LocationVarTable", create: false)[call.Value("LocationVar")]
            ?? throw new BbRuntimeException("BBFaceDirection has no location");
        var target = BbSpellHost.Ground(location);
        // The facing native always sends the 0x53 packet: the direction from the unit to the point, or (0, 0, 1) when the point is exactly the
        // unit's position, and turns the unit the same way. (The packet carries the height difference too; the client's facing is flat.)
        Position2 facing;
        if (BbSpellHost.Distance(unit.Position, target) <= 1e-6) unit.Facing = facing = new Position2(0.0, 1.0);
        else facing = Geometry.FaceTowards(unit, target);
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendFaceDirection(unit, new Position3(facing.X, 0.0, facing.Z));
    }

    private void GetCastRange(BbCall call)
    {
        var owner = Unit(call, "SpellSlotOwnerVar") ?? throw new BbRuntimeException("BBGetCastRange has no spell owner");
        var slot = ChampionSlot(call, owner, "SlotNumber");
        // The range native reads the range of the spell in the owner's slot. The cast-range data is the one the cast validation uses.
        var name = _host.SpellNameOf(owner, slot);
        var range = Spells.Data.Spell(name, Spells.Data.SpellMaxLevel(owner.ChampionName, slot)).CastRangeAt(owner.SpellRanks[slot]);
        call.Table("RangeVarTable", create: true)[call.Value("RangeVar")] = range;
    }

    /// <summary>The champion spell slot a native names: <c>SlotNumber</c> (or <c>SlotNumberVar</c>) of <c>SlotType</c> SpellSlots.</summary>
    private static int ChampionSlot(BbCall call, Entity owner, string key)
    {
        if ((BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0) != BbSlotType.SpellSlots)
            throw BbSpellHost.Unbound($"{call.Function} for a non-champion slot type", "M3");
        var slot = (int)Sum(call, key);
        if ((uint)slot >= 4 || (uint)slot >= owner.SpellRanks.Count)
            throw new BbRuntimeException($"{call.Function}: spell slot {slot} is out of range");
        return slot;
    }

    // ------------------------------------------------------------------------------------------------------------
    // M3: tooltips, slot cooldown, permanent mana pool, animation
    // ------------------------------------------------------------------------------------------------------------

    private void SetSpellToolTipVar(BbCall call)
    {
        var owner = Unit(call, "TargetVar") ?? throw new BbRuntimeException("BBSetSpellToolTipVar has no unit");
        if ((BbSpellbook)(int)(call.Value("SlotBook") as double? ?? 0.0) != BbSpellbook.SPELLBOOK_CHAMPION)
            throw BbSpellHost.Unbound("BBSetSpellToolTipVar on the summoner spellbook", "M3");
        var index = (int)Sum(call, "Index");
        if (index is < 1 or > 3) throw new BbRuntimeException($"BBSetSpellToolTipVar: tooltip index {index} is outside 1..3");
        // The spellbook slot the packet names: spells 0-3, inventory spells from 4 (the client maps InventorySlots to +4 and
        // ExtraSlots to +42 the same way).
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        var number = (int)Sum(call, "SlotNumber");
        var slot = type switch
        {
            BbSlotType.SpellSlots => number,
            BbSlotType.InventorySlots => number + 4,
            _ => number + SpellSystem.ExtraSpellSlotStart,
        };
        var value = Sum(call, "Value");
        var values = owner.ScriptSpellTooltips.TryGetValue(slot, out var existing) ? existing : owner.ScriptSpellTooltips[slot] = new double[3];
        if (values[index - 1] == value) return;
        values[index - 1] = value;
        // Retail's packet cadence is not recovered (see HeroStatsService.TickSpellTooltips): publish when a value changes.
        if (owner.UnitType == UnitType.Hero && Spells.Effects.IsReferenceReady(owner)) Spells.HeroStats?.PushItemTooltips(owner);
    }

    private void SetBuffToolTipVar(BbCall call)
    {
        // A character script's own hooks run in no buff (Rumble's UpdateSelfBuffStats sets the punch damage of a tooltip variable there): the client's
        // setter writes the variable of the buff the hook belongs to and there is none, so nothing shows it. Any other hook without a buff is a
        // script the engine does not know, which stays loud.
        if (_host.Frame.Buff is null && (_host.Frame.Hook is "UpdateSelfBuffStats" or "UpdateSelfBuffActions" ||
                                         _host.Frame.Hook.StartsWith("CharOn", StringComparison.Ordinal))) return;
        var instance = _host.Frame.Buff ?? throw new BbRuntimeException("BBSetBuffToolTipVar runs outside a buff hook");
        var value = Sum(call, "Value");
        var payload = instance.State.Payload;
        switch ((int)Sum(call, "Index"))
        {
            case 1: payload.TooltipValue1 = value; break;
            case 2: payload.TooltipValue2 = value; break;
            case 3: payload.TooltipValue3 = value; break;
            default: throw new BbRuntimeException("BBSetBuffToolTipVar: tooltip index is outside 1..3");
        }
        if (instance.Owner.Buffs.Contains(instance.State)) Spells.Effects.SendBuffTooltip(instance.Owner, instance.State);
    }

    /// <summary>
    /// <c>BBSetUseSlotSpellCooldownTime</c> (the body is
    /// not recovered). Its shipped calls (OdinCaptureChannel's cancel and success stops, <c>Src</c> 3, <c>OwnerVar</c> Owner,
    /// <c>BroadcastEvent</c> false) carry no duration beyond <c>Src</c>, which the Ver2 sibling reads as seconds; OdinCaptureChannelBomb's
    /// stops pass 4 (0 when the relic died).
    /// Batch 494 (replaces batch 450's guess that the use slot is the channel's spell slot): the use slot is spellbook slot 0x33, where
    /// the client's use routine binds the useable's spell, and the client refuses its own uses while that slot cools down
    /// (the can-use check answers 1 when the slot's expiry minus now is above 1e-5). Only a 0x8A
    /// writes that expiry from the server (the client's cooldown setter: now + cooldown).
    /// Reconstruction: the server's native is not shipped; the seconds go to the hero's use slot and out as that 0x8A
    /// (<see cref="BbSummonerModule.SetUseSlotCooldown"/>).
    /// </summary>
    private void SetUseSlotSpellCooldownTime(BbCall call)
    {
        var owner = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBSetUseSlotSpellCooldownTime has no owner");
        if (owner.UnitType != UnitType.Hero) return;
        var seconds = owner.CooldownsDisabled ? 0.0 : Math.Max(0.0, Sum(call, "Src")); // DIAGNOSTICS: the /cooldowns playtest cheat
        Spells.SummonerScripts?.SetUseSlotCooldown(owner, seconds);
    }

    private void SetSlotSpellCooldownTimeVer2(BbCall call)
    {
        var owner = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBSetSlotSpellCooldownTimeVer2 has no owner");
        // The base function BBSetSlotSpellCooldownTime does the same through the host; the Ver2 native adds BroadcastEvent
        // (a client notification that is not recovered) and resolves its numbers like every other native.
        var seconds = owner.CooldownsDisabled ? 0.0 : Math.Max(0.0, Sum(call, "Src")); // DIAGNOSTICS: the /cooldowns playtest cheat
        // The summoner book takes the base function's path (BbSpellHost.SetSlotSpellCooldownTime): the summoner record and the 0x8A with the summoner
        // bit. Reconstruction: BroadcastEvent is not recovered on this path either. Pantheon's Grand Skyfall sets a summoner slot under 2.75 s to 2.
        if ((BbSpellbook)(int)(call.Value("SpellbookType") as double? ?? 0.0) == BbSpellbook.SPELLBOOK_SUMMONER)
        {
            var summonerSlot = (int)Sum(call, "SlotNumber");
            if (owner.UnitType != UnitType.Hero || (uint)summonerSlot >= (uint)owner.SummonerCooldowns.Count)
                throw new BbRuntimeException($"BBSetSlotSpellCooldownTimeVer2: summoner slot {summonerSlot} is not a hero's summoner slot");
            owner.SummonerCooldowns[summonerSlot] = seconds;
            Spells.Effects.SendCooldown(owner, summonerSlot, seconds, summoner: true);
            return;
        }
        // Batch 149 step 4: every shipped item active closes with a cooldown loop over the six inventory slots and writes
        // the script's own time onto each slot whose SpellName matched (Zhonya's 90, QSS 90, Shurelya's 60, DFG 60 ...).
        // The wire index of an inventory slot is the slot plus InventorySlotStart (the client's slot map).
        if ((BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0) == BbSlotType.InventorySlots)
        {
            var itemSlot = (int)Sum(call, "SlotNumber");
            if ((uint)itemSlot >= (uint)owner.ItemCooldowns.Count)
                throw new BbRuntimeException($"{call.Function}: inventory slot {itemSlot} is out of range");
            owner.ItemCooldowns[itemSlot] = seconds;
            Spells.Effects.SendCooldown(owner, itemSlot + InventorySlotStart, seconds);
            return;
        }
        var slot = ChampionSlot(call, owner, "SlotNumber");
        owner.SpellCooldowns[slot] = seconds;
        Spells.Effects.SendCooldown(owner, slot, seconds);
    }

    private void IncPermanentFlatParPoolMod(BbCall call) =>
        IncPermanentFlatParMod(call, "PoolMod", "IncPermanentFlatPARPoolMod");

    /// <summary>
    /// <c>BBIncPermanentFlatPARRegenMod</c> (the permanent twin of the flat regeneration native, the same <c>IncPARHelper</c> family; the client's permanent
    /// intermediate of the unit, with <c>CharInterPermanent</c>): the same parameters and rules as
    /// <c>BBIncPermanentFlatPARPoolMod</c> — the delta joins the unit's permanent flat pool or regeneration modifier of that kind when the
    /// unit's resource is <c>PARType</c>, and a unit of another resource is left alone. Batch 174 binds the regeneration half:
    /// <c>Spells/ExaltedWithBaronNashor.luaobj</c>'s <c>OnBuffActivate</c> adds 3 flat mana regeneration to the holder and its
    /// <c>OnBuffDeactivate</c> takes the same 3 back (0008-0015 and 0117-0124), which was the one native the Baron's buff still raised on.
    /// </summary>
    private void IncPermanentFlatParRegenMod(BbCall call) =>
        IncPermanentFlatParMod(call, "RegenMod", "IncPermanentFlatPARRegenMod");

    /// <summary>The shared body of the two permanent flat resource modifiers: <paramref name="suffix"/> is <c>PoolMod</c> or <c>RegenMod</c>.</summary>
    private void IncPermanentFlatParMod(BbCall call, string suffix, string function)
    {
        if (Unit(call, "TargetVar") is not { } hero) return;
        // A non-hero has no stat pipeline: its regeneration is the plain field a buff's Percent*RegenMod already scales once
        // (BbSpellHost.PermanentOnPet). Reconstruction: the same "apply once" rule there, so the Baron's buff on a minion or a
        // pet adds flat regeneration to the field instead of raising on it.
        if (hero.UnitType != UnitType.Hero)
        {
            if (suffix != "RegenMod") throw BbSpellHost.Unbound($"{function} on a non-hero unit", "M3");
            hero.ManaRegen += Sum(call, "Delta");
            return;
        }
        var parType = (BbParType)(int)(call.Value("PARType") as double? ?? 0.0);
        // The hero stat calculator's pool and regeneration modifiers are keyed by the kind of the pool
        // (Flat{MP,Energy,Souls,Shield,Other}{Pool,Regen}Mod, the client's item stat keys); a hero without that resource is
        // untouched, as for BBIncPAR (IncPARHelper applies only when the type matches).
        if (_host.PowerType(hero) != parType) return;
        var key = "Flat" + Amberline.Data.PrimaryResourceTypes.StatKeyName((Amberline.Data.PrimaryResourceType)(int)parType) + suffix;
        hero.PermanentStatMods[key] = (hero.PermanentStatMods.TryGetValue(key, out var current) ? current : 0.0) + Sum(call, "Delta");
        // Reconstruction (batch 174 part 2): only a hero has a hero stat block to refresh; a non-hero keeps the permanent modifier on the unit
        // (PermanentStatMods), which its own regeneration does not read.
        if (hero.UnitType == UnitType.Hero) Spells.HeroStats?.Refresh(hero, push: Spells.Effects.IsReferenceReady(hero));
    }

    // ------------------------------------------------------------------------------------------------------------
    // M3: the auto-attack override family
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBOverrideAutoAttack</c>: the hero's basic attacks fire the spell in <c>SpellSlot</c> of <c>SlotType</c> (ExtraSlots start at
    /// absolute slot 42) at <c>AutoAttackSpellLevel</c>. The client body maps the slot and calls the spellbook's
    /// auto-attack setter (the 0x22 packet), which the sink replays; <c>CancelAttack</c> (default true) drops the swing in progress.
    /// The missile speed follows the new attack spell, as the client's attack timing does.
    /// </summary>
    private void OverrideAutoAttack(BbCall call)
    {
        var owner = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBOverrideAutoAttack has no owner");
        var number = (int)Sum(call, "SpellSlot");
        var type = (BbSlotType)(int)(call.Value("SlotType") as double? ?? 0.0);
        var slot = type switch
        {
            BbSlotType.InventorySlots => number + 4,
            BbSlotType.ExtraSlots => number + SpellSystem.ExtraSpellSlotStart,
            _ => number,
        };
        // The client's override native clamps the level to 6 and defaults it to 1.
        var level = call.Param("AutoAttackSpellLevel") is null && call.Value("AutoAttackSpellLevelVar") is null
            ? 1 : (int)Math.Min(6.0, Sum(call, "AutoAttackSpellLevel"));
        if (type != BbSlotType.ExtraSlots)
            throw BbSpellHost.Unbound($"BBOverrideAutoAttack for slot type {type}", "M3");
        var spellName = _host.ExtraSpellName(owner, number)
            ?? throw new BbRuntimeException($"BBOverrideAutoAttack: {owner.ChampionName} has no extra spell {number + 1}");
        // The champion spell whose chain armed the override gives the empowered swing its death recap name (Vayne's Q bonus, Kayle's E). Only an
        // attack spell with a script of its own is named after it: Final Hour's UltAttack is a plain swing with another animation.
        var origin = _host.HasFrame && _host.Frame is { SpellSlot: >= 0 and <= 3, SpellRank: >= 1 } frame && _host.Router.HasAttackScript(spellName)
            ? (frame.SpellSlot, frame.SpellRank) : (-1, 0);
        owner.ScriptAttackOverride = new ScriptAttackOverride(slot, spellName, Math.Max(1, level), origin.Item1, origin.Item2);
        // The client flies a missile only for CastType 1, 2 and 3 (the spell data's cast-type field): a CastType 0 attack
        // spell lands when its cast frame runs and its MissileSpeed is unused data. Righteous Fury lists 780 on a CastType 0 spell
        // (the client hits at the cast frame), so a speed here delayed the hit and its passive by the flight of an arrow that is
        // never drawn.
        var attackSpell = Spells.Data.Spell(spellName);
        owner.MissileSpeed = BbChampionModule.CastType(attackSpell) is 1 or 2 or 3 ? attackSpell.MissileSpeed : 0.0;
        if (Spells.Effects.IsReferenceReady(owner)) Spells.Effects.SetAutoAttackSpellSlot(owner, slot);
        if (Flag(call, "CancelAttack", true)) Spells.Combat.InterruptBasicAttack(owner);
    }

    private void RemoveOverrideAutoAttack(BbCall call)
    {
        var owner = Unit(call, "OwnerVar") ?? throw new BbRuntimeException("BBRemoveOverrideAutoAttack has no owner");
        if (owner.ScriptAttackOverride is null) return;
        owner.ScriptAttackOverride = null;
        // Back to the character's own attack spell: its missile speed, and slot -1 for the client.
        owner.MissileSpeed = AttackMissileSpeed(owner);
        if (Spells.Effects.IsReferenceReady(owner)) Spells.Effects.SetAutoAttackSpellSlot(owner, -1);
        if (Flag(call, "CancelAttack", true)) Spells.Combat.InterruptBasicAttack(owner);
    }

    /// <summary>
    /// The missile speed of the unit's own basic attack: the attack spell of the character it is now (<c>{character}BasicAttack</c>), which flies a
    /// missile only for CastType 1, 2 and 3 (a CastType 0 attack lands at its cast frame, as <see cref="OverrideAutoAttack"/> reads it). Nidalee's
    /// human attack flies at 1750, <c>Nidalee_CougarBasicAttack</c> (CastType 0) does not fly.
    /// </summary>
    internal double AttackMissileSpeed(Entity unit)
    {
        var name = unit.StatCharacterName + "BasicAttack";
        if (!Spells.Data.HasSpell(name)) return unit.MissileSpeed;
        var spell = Spells.Data.Spell(name);
        return BbChampionModule.CastType(spell) is 1 or 2 or 3 ? spell.MissileSpeed : 0.0;
    }

    private void CancelAutoAttack(BbCall call)
    {
        // The stop-attack native only parses Target and Reset (default false); the server stops the swing, and Reset also makes the next
        // attack available at once.
        if (Unit(call, "TargetVar") is not { } unit) return;
        Spells.Combat.InterruptBasicAttack(unit);
        if (Flag(call, "Reset", false)) unit.AttackCooldown = 0.0;
    }

    private void PlayAnimation(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit || call.Value("AnimationName") is not string name) return;
        if (!Spells.Effects.IsReferenceReady(unit)) return;
        // The animation native parses AnimationName, ScaleTime, Loop, Blend and Lock; ScaleTime is the playback time the clip is scaled to. The
        // optional booleans default to Loop false, Blend true and Lock true (8 of the 93 shipped calls omit Lock; BBUnlockAnimation,
        // the unlock native, is what releases it and sets the CanAttack bit again).
        var loop = Flag(call, "Loop", false);
        Spells.Effects.PlayAnimation(unit, name, loop: loop, lockAnimation: Flag(call, "Lock", true),
            blend: Flag(call, "Blend", true), duration: (float)Sum(call, "ScaleTime"));
        // Reconstruction (batch 476): a looped play by a channel's hook is that channel's loop (BbScriptChannels.NoteLoop).
        if (loop) _host.Channels.NoteLoop(unit);
    }

    private void UnlockAnimation(BbCall call)
    {
        if (Unit(call, "OwnerVar") is not { } unit || !Spells.Effects.IsReferenceReady(unit)) return;
        // The unlock native clears the animation lock (the 0x2B unlock the dashes already use).
        Spells.Effects.StopAnimation(unit, Flag(call, "Blend", true));
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBForEachUnitInTargetArea and its siblings. Each registered label is a thunk that pushes a flag and calls the shared body: the
    // Visible variant pushes 0, the plain one 0x400000 (the IgnoreVisibilityCheck target flag, unnamed in BbConstants). The labels:
    // ForEachUnitInTargetArea, ForNClosest and Random each have a Visible variant; every body queries through the shared target gate
    // and applies the buff filter.
    // ------------------------------------------------------------------------------------------------------------

    private void ForEachUnitInTargetArea(BbCall call, bool random, bool nearest = false, bool visibleOnly = false)
    {
        var attacker = Unit(call, "AttackerVar");
        var centerValue = call.Table("CenterVarTable", create: false)[call.Value("CenterVar")];
        var center = centerValue switch
        {
            Entity unit => unit.Position,
            BbTable point => BbSpellHost.Ground(point),
            _ => throw new BbRuntimeException("BBForEachUnitInTargetArea has no center"),
        };
        var range = Sum(call, "Range");
        var flags = (call.Value("Flags") as string ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var buffFilter = call.Value("BuffNameFilter") as string;
        var inclusive = Flag(call, "InclusiveBuffFilter", true);
        var iterator = call.Value("IteratorVar") ?? throw new BbRuntimeException("BBForEachUnitInTargetArea has no IteratorVar");

        var picked = BbSpellTargeting.Area(Spells, attacker, center, range, flags);
        if (buffFilter is not null)
            picked = picked.Where(unit => unit.Buffs.Any(buff =>
                string.Equals(buff.Name, buffFilter, StringComparison.OrdinalIgnoreCase)) == inclusive).ToList();
        if (visibleOnly)
        {
            // BBForNClosestVisibleUnitsInTargetArea (Shaco's Jack-in-the-Box looks for its victims with it): the candidates the Attacker's
            // team can see, by the same team sight the targeting rules and BBCanSeeTarget use (an enemy standing in fog or in a bush
            // the team cannot see into, or stealthed, is not picked; an ally always is). The thunk pushes 0, so the shared
            // gate runs its visibility test (interface slot 6). Reconstruction: the client's slot 6 tests only when the
            // attacker is the local hero, so the server's body is not in the exe; "visible" is read as visible to the Attacker, the
            // AttackerVar every shipped call names.
            var viewer = attacker ?? throw new BbRuntimeException($"{call.Function} has no Attacker to see with");
            picked = picked.Where(unit => Spells.CanSeeTarget(viewer, unit)).ToList();
        }
        if (nearest)
        {
            // BBForNClosestUnitsInTargetArea: the MaximumUnitsToPick candidates nearest to the centre, nearest first. The client
            // runs std::partial_sort with its comparator, the XZ squared distance from Center to the unit's
            // centre, strict <, and visits the first min(Max, n). Reconstruction: equal distances by net id (partial_sort's heap order
            // is not by net id).
            var closest = (int)Math.Max(0.0, Sum(call, "MaximumUnitsToPick"));
            picked = picked.OrderBy(unit => BbSpellHost.Distance(unit.Position, center)).ThenBy(unit => unit.NetId).Take(closest).ToList();
        }
        if (random)
        {
            // BBForEachUnitInTargetAreaRandom: MaximumUnitsToPick units chosen at random from the same candidates
            // (Starcall's CanCast asks for one). The client builds the index vector 0..n-1, takes
            // min(Max, n), and each step draws an index, erases it and runs the blocks for its unit: a draw without
            // replacement in draw order, which this partial Fisher-Yates is. The random stream is the server's; the match random
            // keeps the draw reproducible with the match seed.
            // The units are visited in the order they were drawn, whatever the limit: a script that takes the first
            // unit that passes its tests (Pyroclasm's DoOnce, limit 10) bounces to a random unit of the area, not to
            // the first of the candidate list when there are fewer candidates than the limit ("Pyroclasm bounces
            // randomly between enemies", game_character_tips_Brand). A single candidate needs no draw.
            var limit = (int)Math.Max(0.0, Sum(call, "MaximumUnitsToPick"));
            var take = Math.Min(limit, picked.Count);
            for (var index = 0; index < Math.Min(take, picked.Count - 1); index++)
            {
                var swap = index + Spells.Random.NextIndex(picked.Count - index);
                (picked[index], picked[swap]) = (picked[swap], picked[index]);
            }
            if (take < picked.Count) picked.RemoveRange(take, picked.Count - take);
        }
        foreach (var unit in picked)
        {
            call.Scope.Vars[iterator] = unit;
            call.ExecuteSubBlocks();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBForEachUnitInTargetAreaAddBuff (the plain one pushes 0x400000, the Visible variant pushes 0; both call
    // the add body). Shipped callers: BeaconAura, BeaconAuraAP and OdinVanguardAura.
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ForEachUnitInTargetArea"/> with <see cref="SpellBuffAdd"/> as its body and no sub-blocks: every admitted unit of the
    /// area gets the named buff. The area half is the same (centre, range, flags, and the <c>BuffNameFilter</c>/<c>InclusiveBuffFilter</c>
    /// pair of the sibling native); the add half is the same as <see cref="SpellBuffAdd"/> under the <c>Buff*</c> names the shipped calls
    /// pass (<c>BuffName</c>, <c>BuffAddType</c>, <c>BuffType</c>, <c>BuffMaxStack</c>, <c>BuffNumberOfStacks</c>, <c>BuffDuration</c>,
    /// <c>BuffVarsTable</c>, <c>TickRate</c>, <c>IsHiddenOnClient</c>) and <c>BuffAttackerVar</c>, the unit credited as the child buff's
    /// attacker — the hero behind the beacon's aura, not the beacon that spreads it. The client's add body parses Attacker, Center,
    /// Range, Flags, BuffNameFilter, InclusiveBuffFilter and BuffAttacker, then <c>BuffNumberOfStacks</c> (default 1), BuffDuration,
    /// BuffAddType, TickRate, IsHiddenOnClient, <c>BuffName</c> (default: the running script), BuffType, BuffMaxStack and
    /// <c>BuffVarsTable</c> (default "NextBuffVars"); it queries the area through the shared target gate, applies the buff filter, and an
    /// unresolved buff name logs "No buff found named '%s'" and adds nothing.
    /// </summary>
    private void ForEachUnitInTargetAreaAddBuff(BbCall call)
    {
        var attacker = Unit(call, "AttackerVar");
        var centerValue = call.Table("CenterVarTable", create: false)[call.Value("CenterVar")];
        var center = centerValue switch
        {
            Entity unit => unit.Position,
            BbTable point => BbSpellHost.Ground(point),
            _ => throw new BbRuntimeException("BBForEachUnitInTargetAreaAddBuff has no center"),
        };
        var flags = (call.Value("Flags") as string ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var picked = BbSpellTargeting.Area(Spells, attacker, center, Sum(call, "Range"), flags);
        // The pair reads as it does in BBForEachUnitInTargetArea (the same buff filter): a unit is kept when it carries BuffNameFilter and
        // InclusiveBuffFilter is true (the default), or when it does not and the filter is false. No shipped call of this native names a
        // BuffNameFilter (the three pass InclusiveBuffFilter alone, where the key is inert), so nothing is filtered here.
        if (call.Value("BuffNameFilter") is string buffFilter)
        {
            var inclusive = Flag(call, "InclusiveBuffFilter", true);
            picked = picked.Where(unit => unit.Buffs.Any(buff =>
                string.Equals(buff.Name, buffFilter, StringComparison.OrdinalIgnoreCase)) == inclusive).ToList();
        }

        // Without a BuffName the buff is named after the running script, like BBSpellBuffAdd; the attacker is the unit the call credits.
        var name = call.Value("BuffName") as string ?? _host.Frame.ScriptName;
        var namedAttacker = Unit(call, "BuffAttackerVar");
        // The add body reads BuffNumberOfStacks with the default 1 (a named count of 0, or a variable that holds nothing, adds none).
        var stacks = HasNumber(call, "BuffNumberOfStacks") ? (int)Math.Round(Math.Clamp(Sum(call, "BuffNumberOfStacks"), 0.0, 1000.0)) : 1;
        if (stacks < 1) return;
        // The add body reads BuffVarsTable with the default "NextBuffVars"; every unit's buff gets its own copy of that table.
        var varsName = call.Value("BuffVarsTable") ?? "NextBuffVars";
        var source = BbBaseLibrary.GetTable(call.Scope, varsName, create: false);
        foreach (var unit in picked)
        {
            var vars = new BbTable();
            foreach (var entry in source.Entries) vars[entry.Key] = entry.Value;
            _host.Buffs.Add(new BbBuffRequest(unit, namedAttacker ?? attacker ?? unit, name,
                (int)(call.Value("BuffAddType") as double? ?? 0.0), (int)(call.Value("BuffType") as double? ?? 0.0),
                (int)(call.Value("BuffMaxStack") as double? ?? 1.0), Sum(call, "BuffDuration"),
                call.Value("TickRate") as double? ?? 0.0,
                // No shipped call of this native names CanMitigateDuration or StacksExclusive; the defaults of BBSpellBuffAdd stand.
                Flag(call, "CanMitigateDuration", false), vars, stacks, Flag(call, "StacksExclusive", true),
                Flag(call, "IsHiddenOnClient", false)));
        }
    }
}
