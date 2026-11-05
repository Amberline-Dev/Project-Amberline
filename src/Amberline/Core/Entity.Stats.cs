using System.Collections.Generic;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Core;

public sealed partial class Entity
{
    /// <summary>Typed six-slot equipment state owned by a hero entity.</summary>
    public IReadOnlyList<InventorySlot?> Inventory { get; set; } = Amberline.Simulation.Inventory.Empty();

    /// <summary>Typed active buffs; lifecycle and replication remain separate.</summary>
    public IList<BuffState> Buffs { get; } = new List<BuffState>();

    /// <summary>
    /// The statuses scripts set on this unit with no buff to hold them (batch 201; <c>BbBuffRuntime</c> writes it): one payload that
    /// carries their flags, as a buff would. A status is a bit of the unit's state (retail: CanAttack, CanMove and the other
    /// CharacterState bits are not buffs), so this is not a buff. It takes no slot in <see cref="Buffs"/>, is never ticked, never
    /// sent to a client and never counted by the table. The status readers see it beside the buffs through
    /// <see cref="AnyBuffOrStatus"/> and <see cref="BuffsAndScriptStatus"/>. Null while no such status is set.
    /// </summary>
    public BuffState? ScriptStatus { get; set; }

    /// <summary>Whether a buff, or the script status of the unit (batch 201), satisfies <paramref name="test"/>.</summary>
    public bool AnyBuffOrStatus(Func<BuffState, bool> test) =>
        Buffs.Any(test) || ScriptStatus is { } status && test(status);

    /// <summary>The buffs of the unit, then its script status when one is set (batch 201).</summary>
    public IEnumerable<BuffState> BuffsAndScriptStatus()
    {
        foreach (var buff in Buffs) yield return buff;
        if (ScriptStatus is { } status) yield return status;
    }

    /// <summary>
    /// Permanent stat modifiers a BuildingBlocks script added through <c>IncPermanent*Mod</c>, keyed like the item
    /// modifiers the hero stat calculator reads (<c>FlatSpellBlockMod</c>, <c>FlatArmorMod</c> ...). They persist until
    /// the script subtracts them again (Soraka's Consecration adds +16 on activation and -16 on deactivation).
    /// </summary>
    public Dictionary<string, double> PermanentStatMods { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The stat deltas a BuildingBlocks character script's <c>UpdateSelfBuffStats</c> hook asked for with <c>BBIncStat</c> (Vayne's Night Hunter
    /// movement speed), sampled by the script engine every spell update. The hero stat calculator reads it next to the hero's buffs; it holds
    /// no wire buff of its own.
    /// </summary>
    public BuffPayload? CharacterScriptStats { get; set; }

    /// <summary>
    /// The stat deltas the shipped item scripts' <c>UpdateSelfBuffStats</c> hooks asked for with <c>BBIncStat</c>
    /// (Sorcerer's Shoes' <c>IncFlatMagicPenetrationMod 20</c>), sampled once per inventory copy every item update and
    /// summed across the copies. The hero stat calculator reads it next to the hero's buffs under the name
    /// <c>ItemScript</c>; it holds no wire buff of its own, exactly like <see cref="CharacterScriptStats"/>.
    /// </summary>
    public BuffPayload? ItemScriptStats { get; set; }

    /// <summary>The non-healing part of the maximum health the last stat refresh applied (the sum of the buffs' <c>NonHealingMaxHp</c>).</summary>
    public double NonHealingMaxHp { get; set; }

    /// <summary>
    /// Spell tooltip variables a BuildingBlocks character script wrote with <c>BBSetSpellToolTipVar</c> (Ryze's mana-scaled
    /// damage, Kayle's attack-damage line): spellbook slot to the first three floats, replicated like the item tooltips.
    /// </summary>
    public Dictionary<int, double[]> ScriptSpellTooltips { get; } = new();

    /// <summary>
    /// The animation overrides a BuildingBlocks script set with <c>BBOverrideAnimation</c> (a stance's Run2, Idle2 ...), original clip name in lower
    /// case to the replacement, in the order they were added. The client keeps the whole map (packet 0x6E replaces it), so a late observer gets all of it.
    /// </summary>
    public Dictionary<string, string> ScriptAnimationOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The fade layers a script pushed with <c>BBPushCharacterFade</c> and has not popped, in push order: id, opacity and seconds of the push (Deceive's
    /// 0.2, the sleeping Jack in the Box's 0.2 over 2 s). A late observer replays the translucent ones in <c>BuildBuffs</c>. Reconstruction: a push of
    /// full opacity (Deceive's end, the box's wake) supersedes the layers under it, so they are dropped; the client's own layering is not recovered.
    /// </summary>
    public List<(short Id, float Opacity, float Seconds)> ScriptFades { get; } = new();

    /// <summary>
    /// A script hid the unit's health bar (<c>BBShowHealthBar</c> with <c>Show</c> false: the unit's hide-health-bar flag, which the 0xD7 packet
    /// sets on every client); a late observer is told in <c>BuildBuffs</c>.
    /// </summary>
    public bool HealthBarHidden { get; set; }

    /// <summary>
    /// Batch 512 (Reconstruction): a capture point's guardian, whose PAR pool is the capture meter that the point's tree writes
    /// (<c>CapturePoint_SetValue</c>, BehaviorLeaves.Capture). A script's <c>BBIncPAR</c> leaves such a pool alone.
    /// </summary>
    public bool ParIsCaptureMeter { get; set; }

    /// <summary>
    /// Batch 694: the PAR a script added to a capture meter (<see cref="ParIsCaptureMeter"/>) since the point's last tick, which the point's tree
    /// takes into its progress (BehaviorTreeQuestHost.Tick) so that its <c>CapturePoint_SetValue</c> stays the meter's one writer.
    /// </summary>
    public double CaptureMeterParPending { get; set; }

    /// <summary>
    /// <c>BBSetNotTargetableToTeam</c>: the unit's own team cannot target it (Monkey King's decoy),
    /// and the other teams cannot (Jarvan's Demacian Standard). The client's <c>IsTargetableToTeam</c> flags (<c>SPELL_NonTargetableAlly</c>,
    /// <c>SPELL_NonTargetableEnemy</c>) replicate them; a late observer is told in <c>BuildBuffs</c>.
    /// </summary>
    public bool NotTargetableToAlly { get; set; }
    public bool NotTargetableToEnemy { get; set; }

    /// <summary>Whether <paramref name="observer"/>'s team may not target this unit (the observer's team against the unit's, by <see cref="Team"/>).</summary>
    public bool NotTargetableTo(Entity? observer) =>
        observer is not null && (observer.Team == Team ? NotTargetableToAlly : NotTargetableToEnemy);

    /// <summary>The resource-bar colours a script set (<c>BBSetPARColorOverride</c>, packet 0x99); null while the bar has its own. A late observer is told in <c>BuildBuffs</c> (Reconstruction: retail's late-observer replay is not recovered).</summary>
    public ParColorOverride? ParColorOverride { get; set; }

    /// <summary>The voice suffix a script set (<c>BBSetVoiceOverride</c>, packet 0x9E; Riven's ultimate says "Ult"); null while the champion has its own voice. A late observer is told in <c>BuildBuffs</c> (Reconstruction: retail's late-observer replay is not recovered).</summary>
    public string? VoiceOverride { get; set; }

    /// <summary>The spell this unit's basic attacks fire since a script overrode its auto attack (Kayle's Righteous Fury, Sivir's Ricochet).</summary>
    public ScriptAttackOverride? ScriptAttackOverride { get; set; }

    /// <summary>Persistent hero-level item counters.</summary>
    public HeroItemState ItemState { get; set; } = new();

    /// <summary>
    /// Equipment snapshot from the last stat refresh. This is intentionally
    /// stored rather than derived from <see cref="Inventory" /> so equipment
    /// transition logic can compare the old set with the current six slots.
    /// </summary>
    public IReadOnlySet<int> EquippedItemIds => _equippedItemIds;

    /// <summary>Replace the equipment snapshot after applying a stat refresh.</summary>
    public void SetEquippedItemIds(IEnumerable<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        _equippedItemIds.Clear();
        _equippedItemIds.UnionWith(itemIds);
    }

    private readonly HashSet<int> _equippedItemIds = new();
}
