using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// What the running hook knows about its cause. Natives and host getters read it instead of globals: the retail Lua gets
/// the same facts from the hook's scope and from the spell script history the stock client keeps
/// (the stock client's spell script history, see the damage native).
/// </summary>
internal sealed class BbHookFrame
{
    public BbHookFrame(string hook, string scriptName, Entity? owner)
    {
        Hook = hook;
        ScriptName = scriptName;
        Owner = owner;
    }

    public string Hook { get; }
    /// <summary>The script that runs; <c>BBSpellBuffAdd</c> without a BuffName adds a buff of this name.</summary>
    public string ScriptName { get; }
    /// <summary>The unit argument-less getters (<c>GetLevel()</c>) mean.</summary>
    public Entity? Owner { get; }
    /// <summary>The champion whose spell started this chain; its slot and rank give damage its recap origin.</summary>
    public Entity? Caster { get; init; }
    public int SpellSlot { get; init; } = -1;
    public int SpellRank { get; init; }
    /// <summary>
    /// The item an inventory copy's own hook or an item active's cast runs for (0: no item of this run). An item
    /// active's <see cref="SpellSlot"/> is the inventory slot the copy sits in, not a champion spell slot, and a
    /// copy's hook has no slot at all, so the recap identity of that damage is the item itself
    /// (<see cref="ItemOrigin"/>), which is what the hand-written path recorded and the recap catalog gates.
    /// </summary>
    public int ItemId { get; init; }
    /// <summary>The cast's resource cost (<c>GetPARCost</c>).</summary>
    public double ManaCost { get; init; }
    public Position3? CastPosition { get; init; }
    public Position3? CastEndPosition { get; init; }
    /// <summary>A spell script's own hook of a cast (SelfExecute, TargetExecute, ...): the cast is the running one BBApplyDamage resolves DEFAULT by.</summary>
    public bool CastRunning { get; init; }
    /// <summary>The kind of the damage a damage hook handles (<c>GetSourceType</c>).</summary>
    public DamageSourceKind? DamageSource { get; init; }
    public bool InDamageHook { get; init; }
    /// <summary>The damage a damage hook handles is a damage-over-time tick: <c>GetSourceType</c> answers DAMAGESOURCE_PERIODIC.</summary>
    public bool DamageIsPeriodic { get; init; }
    public BbBuffInstance? Buff { get; init; }
    public BbStatCollector? Stats { get; init; }
    /// <summary>
    /// The <c>BuffOnUpdateStats</c> sample taken while the buff being added does not exist yet (<see cref="BbBuffRuntime.Add"/>: its statuses and stat deltas are
    /// wanted before <c>OnBuffActivate</c> runs). Retail never runs a buff's hooks before its activation, so the particles such a hook creates or removes
    /// (OrianaGhost's pointer arrow, which it swaps by distance band) are skipped here: the one it made would be lost by the activation that follows,
    /// which writes its own id over it.
    /// </summary>
    public bool PreActivation { get; init; }
    /// <summary>
    /// A <c>BuffOnUpdateStats</c> run that polls a buff that already exists (after its activation and on every update) rather
    /// than the first sample taken when it is added. Natives whose effect is not a stat accumulator (<c>BBIncMaxHealth</c>)
    /// apply once, in the first sample only.
    /// </summary>
    public bool IsResample { get; init; }
    /// <summary>
    /// The <c>BuffOnUpdateStats</c> sample the buff's mutation clock is due on (see <c>BbBuffInstance.MutateNextAt</c>):
    /// this run's bare mutations accumulate and the clock moves one tick on. False for the sample taken before the buff
    /// exists and for a sample between two ticks.
    /// </summary>
    public bool MutationDue { get; init; }
    /// <summary>
    /// How many <c>BBExecutePeriodically</c> bodies this run is inside (<c>BbCall.ExecutePeriodicSubBlocks</c>). The bare
    /// mutations in there follow the periodic's own clock and are not gated by <see cref="MutationDue"/>.
    /// </summary>
    public int PeriodicDepth { get; set; }
    /// <summary>
    /// Whether a bare mutation of this run is held back (Reconstruction M4, "contributions are re-sampled; mutations are
    /// ticked"): in a <c>BuffOnUpdateStats</c> sample the permanent stat adds and the perception bubbles accumulate once
    /// per the buff's mutation clock, not once per sample, so <c>TurretBonus</c>'s three permanents under an add with
    /// <c>TickRate 60</c> land every 60 s instead of every 0.25 s. A mutation in a <c>BBExecutePeriodically</c> body is
    /// that periodic's own (TurretDecay, ResistantSkin) and a sample taken before the buff exists runs none at all (the
    /// pre-activation sample, like <c>BBSpellEffectCreate</c>). Every other hook keeps its own cadence: an
    /// <c>OnBuffActivate</c> add is that activation's, one per activation.
    /// </summary>
    public bool MutationSuppressed =>
        Hook == "BuffOnUpdateStats" && (PreActivation || (PeriodicDepth == 0 && !MutationDue));
    /// <summary>The name of the spell that was cast or that hit (<c>GetCastInfo GetSpellName</c>).</summary>
    public string? SpellName { get; init; }
    /// <summary>Set for a dry run that only reads the amount of an attack override's primary hit (<see cref="BbDamageProbe"/>).</summary>
    public BbDamageProbe? Probe { get; init; }
    /// <summary>
    /// What <c>CO_RANDOM_CHANCE_LESS_THAN</c> draws in a probe run: the swing has already rolled its critical strike, and the script's own
    /// roll (<c>VayneTumbleAttack</c>) has to agree with it, so the draw is below any chance (a crit) or above every chance (a normal hit).
    /// </summary>
    public double? ForcedRandom { get; init; }
    /// <summary><c>GetIsAttackOverride</c>: the spell is an attack the unit's auto-attack override fired, not a cast.</summary>
    public bool IsAttackOverride { get; init; }
    /// <summary>The cast range an <c>AdjustCastInfo</c> hook stored with <c>BBSetSpellCastRange</c> (null: the hook did not).</summary>
    public double? CastRange { get; set; }
    /// <summary>The unit an override attack was fired at; its <c>DAMAGESOURCE_ATTACK</c> hit is the engine's own basic attack.</summary>
    public Entity? AttackTarget { get; init; }
    /// <summary>Units this cast has already hit; <c>GetCastSpellTargetsHitPlusOne</c> reads it plus one (chain missiles count hops).</summary>
    public int TargetsHit { get; init; }
    /// <summary>The net id of the missile the hook belongs to (0 for a hook with no missile): <c>MissileNetworkID</c> of a missile spell's <c>TargetExecute</c>, <c>missileId</c> of the launch hook.</summary>
    public uint MissileNetId { get; init; }
    /// <summary>
    /// The effects this hook run created (<c>BBSpellEffectCreate</c>): the particle name and the unit it was bound to. The default
    /// hit pass skips an effect the script already drew on the same unit.
    /// </summary>
    public List<(string Name, uint Unit)> Effects { get; } = new();
    /// <summary>
    /// The <c>BBSpellBuffAdd</c> requests this run made, held until it is over: one item proc, and one recipient of an item active's
    /// cast hook, place their buffs as one admission group (<see cref="BbBuffRuntime.CommitProcAdds"/>), the way the hand-written
    /// ports reserved every slot a proc needed or applied none of it. Empty for a run that adds nothing or adds one buff at a time.
    /// </summary>
    public List<BbBuffRequest>? HeldAdds { get; set; }
    /// <summary>The buffs this run added or renewed whose after-activation stats sample waits for the run's end (BbBuffRuntime.EndRun).</summary>
    public List<BbBuffInstance>? DeferredResamples { get; set; }
    /// <summary>
    /// The <c>BBSpellEffectCreate</c> calls this run made while its adds are held (<see cref="HeldAdds"/>): the group
    /// admits or drops them with its buffs (<see cref="BbBuffRuntime.CommitProcAdds"/>), so a declined proc draws none of
    /// its trigger particles. The hand-written ports preflighted before they queued an effect; the script's own order has
    /// the particles first, so holding them keeps that decision without reordering the hook.
    /// </summary>
    public List<Action>? HeldEffects { get; set; }
    /// <summary>
    /// The <c>BBApplyAssistMarker</c> calls this run made while its adds are held (<see cref="HeldAdds"/>): one recipient's
    /// marker goes with that recipient's add group and is recorded only when the group lands (<see cref="BbBuffRuntime.CommitProcAdds"/>).
    /// Randuin's Omen marks every unit its cast hook visits, but a recipient whose Slow/BlackOmen pair is declined is left with
    /// no debuff and no credit, the way the hand-written active credited only the recipients it applied to. Empty for a run
    /// that holds nothing or marks nobody.
    /// </summary>
    public List<(Entity Target, Entity Source, double Duration)>? HeldMarkers { get; set; }
    /// <summary>One of this run's held add groups could not get every slot it needs: the proc is declined in full.</summary>
    public bool GroupDenied { get; set; }
    /// <summary>
    /// This run is an item active's own cast hook (<c>SelfExecute</c>/<c>TargetExecute</c> of an inventory spell). Its adds are one
    /// admission group per recipient, the way the hand-written actives reserved every slot a recipient needed or applied none of it
    /// (Randuin's Slow and BlackOmen pair), but its particles are not held with them: the script draws its trigger first
    /// (<c>RanduinsOmen_cas.troy</c>, <c>spectral_fury_cas.troy</c>) and the wire keeps that order.
    /// </summary>
    public bool ItemActive { get; init; }
    /// <summary>Applying the held adds of this run: they are not held again.</summary>
    public bool CommittingAdds { get; set; }

    /// <summary>
    /// The recap identity of an item's own run of this frame: the inventory copy's hook (Items/3131's
    /// <c>UpdateSelfBuffActions</c> opening LightstrikerApplicator), the active's cast (Spells/OdinEntropicClaymore's
    /// <c>SelfExecute</c>) or the child cast one of those made. Null when this run is no item's own, and when the recap
    /// catalog does not name that item (the same refusal <see cref="DeathRecapOrigin.Item"/> gives, so the hit falls
    /// through to the origin its own producer has).
    /// </summary>
    public DeathRecapOrigin? ItemOrigin
    {
        get
        {
            if (ItemId == 0 || Caster is null) return null;
            var origin = DeathRecapOrigin.Item(Caster, ItemId);
            return origin == DeathRecapOrigin.Unclassified ? null : origin;
        }
    }

    public DeathRecapOrigin? RecapOrigin
    {
        get
        {
            if (Caster is null) return null;
            // The item identity first, ahead of every other answer. An item active's SpellSlot is the inventory slot its
            // copy sits in (BbItemModule.CastItemSpell), so the champion-spell branch below would name the champion's Q,
            // W, E or R for an active used from one of those slots; and a Shaco clone's copy is UnitType.Monster
            // (SharedCloneService), so the hero gate below would drop the item a copied 3091 Wit's End procs from.
            if (ItemOrigin is { } item) return item;
            // The identity a chain an item's own window opened carries (3184 Entropic Claymore's EntropyBurning, 3131
            // Lightstriker's LightstrikerApplicator: their buffs deal the item's damage and BBApplyDamage states no recap
            // of its own), which is the one the hand-written path recorded and the recap catalog gates. It is read before
            // the hero gate and before the champion-spell branch for the two reasons above: a clone's window keeps its
            // item, and a window an active opened from inventory slot 0..3 does not become the champion's spell.
            if (Buff?.RecapOrigin is { } carried) return carried;
            // A hero's spell only: a pet that stands in a hook's Caster slot (the revenant of YorickRARemovePet) has no spell slots of its own.
            if (Caster.UnitType != UnitType.Hero) return null;
            // The summoner spellbook owns the frame when its script is one of the two slots' spells. Its slot numbers are the
            // indices 0..1 of its own book and collide with the champion's Q and W, so a summoner cast (and every buff that
            // inherited its chain) would otherwise be recapped as the champion's first or second spell: Ignite's pulses as
            // Annie's W, Fortify's turret splash as Vayne's Q. The recap names the summoner script that started the chain
            // (SummonerDot, SummonerSmite); one the recap catalog does not name (SummonerFortify's splash) has no spell
            // origin of its own and returns null so the hit keeps the origin its own producer gives it.
            if (SummonerLoadout.CanonicalNames.Contains(ScriptName, StringComparer.OrdinalIgnoreCase))
            {
                var summoner = DeathRecapOrigin.SummonerSpell(Caster, ScriptName);
                return summoner == DeathRecapOrigin.Unclassified ? null : summoner;
            }
            if (SpellSlot is >= 0 and <= 3 && SpellRank >= 1) return DeathRecapOrigin.ChampionSpell(Caster, SpellSlot, SpellRank);
            // Reconstruction: the damage of a hero's own buff script that is also the script of one of its spells (Blitzcrank's StaticField
            // aura strikes from BuffOnUpdateActions, no spell chain) is named after that spell at its learned rank, as the hand-written
            // passive did: the recap names the script that called BBApplyDamage.
            if (Buff is null) return null;
            var names = Amberline.Data.GameData.Default.Champion(Caster.ChampionName).SpellNames;
            for (var slot = 0; slot < Math.Min(4, names.Count); slot++)
                if (string.Equals(names[slot], ScriptName, StringComparison.OrdinalIgnoreCase) && slot < Caster.SpellRanks.Count &&
                    Caster.SpellRanks[slot] >= 1)
                    return DeathRecapOrigin.ChampionSpell(Caster, slot, Caster.SpellRanks[slot]);
            // A hero's buff script the recap catalog names (OrianaSpellSword, the Clockwork Windup proc) is the origin of the damage it deals.
            return DeathRecapOrigin.BuffProducer(Caster, ScriptName);
        }
    }
}
