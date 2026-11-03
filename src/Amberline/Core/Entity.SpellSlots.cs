namespace Amberline.Core;

/// <summary>
/// Which spellbook slots of a unit a script sealed with <c>BBSealSpellSlot</c> (the client's seal setter sets the slot's
/// sealed flag and asks the spellbook owner to <c>SyncStats</c>). One bit per stored slot (0-3 spells, 4-41 inventory spells, 42-51 extra spells; the
/// spellbook has 52, <c>MAX_SPELLBOOK_SLOTS</c> 0x34) for each of the unit's two spellbooks, champion and summoner.
/// <para>
/// A sealed slot is a slot the owner cannot cast: the bit of its slot is cleared in the replicated cast-bit word
/// (<c>mCanCastBits1</c> of the main spellbook, <c>mCanCastBits2</c> of the second, summoner one), which is what
/// the client's can-use check tests before it asks the owner (<c>USE_Disabled</c>), and the server's cast
/// gate refuses it the same way (<c>SpellSystem</c>, the summoner and item casts). Reconstruction: the client's <c>SyncStats</c> is an
/// empty stub and the server body is not in the exe; that the cast bits are rebuilt from the seals is the reading every hand-written
/// <c>mCanCastBits1</c> special case already encoded (Vayne's Tumble and passive, until her conversion on 19 October 2026; Blitzcrank's Power Fist, Nidalee's Takedown, Yorick's
/// Spectral, Shaco's Deceive: all are the <c>BBSealSpellSlot</c> calls of the shipped scripts).
/// </para>
/// </summary>
public sealed class SpellSlotSeals
{
    /// <summary><c>MAX_SPELLBOOK_SLOTS</c>: the spellbook has 52 slots.</summary>
    public const int SlotCount = 52;

    private ulong _champion;
    private ulong _summoner;
    private ulong _byCast;

    /// <summary>Whether the stored <paramref name="slot"/> of the chosen spellbook is sealed.</summary>
    public bool IsSealed(bool summonerBook, int slot) =>
        (uint)slot < SlotCount && ((summonerBook ? _summoner : _champion) >> slot & 1UL) != 0;

    /// <summary>
    /// Seals or unseals a slot; true when the state changed. <paramref name="byCast"/> marks a seal a spell's own cast set (the
    /// <c>SelfExecute</c> or <c>TargetExecute</c> of Deceive, Tumble, the cards): a buff the cast added opens it, see <see cref="ReleaseCastSeals"/>.
    /// </summary>
    public bool Set(bool summonerBook, int slot, bool sealedState, bool byCast = false)
    {
        if ((uint)slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var bit = 1UL << slot;
        ref var word = ref (summonerBook ? ref _summoner : ref _champion);
        var was = (word & bit) != 0;
        if (was == sealedState) return false;
        word = sealedState ? word | bit : word & ~bit;
        if (!summonerBook) _byCast = sealedState && byCast ? _byCast | bit : _byCast & ~bit;
        return true;
    }

    /// <summary>
    /// Opens the champion-book slots a spell's own cast sealed; true when one was open. Reconstruction: the engine calls it at respawn, after the
    /// shipped <c>CharOnResurrect</c> has run. The cast seals the slot and the buffs it added open it again (Deceive: <c>DeceiveFade</c> adds
    /// <c>Deceive</c>, whose deactivation unseals), but death retires the buffs and refuses the add of a new one to the corpse, so a death inside
    /// the 0.05 s fade would leave the slot sealed for the match.
    /// Retail's own character scripts reopen the slots they sealed at respawn (<c>CharOnResurrect</c> of Vayne) or never meet the case.
    /// </summary>
    public bool ReleaseCastSeals()
    {
        var released = _champion & _byCast;
        _byCast = 0;
        if (released == 0) return false;
        _champion &= ~released;
        return true;
    }

    /// <summary>
    /// The bits (bit <c>n</c> = slot <c>n</c>, slots 0-31: the replicated cast-bit word is 32 bits wide) the seals clear in the cast-bit
    /// word of the chosen spellbook.
    /// </summary>
    public uint ClearedCastBits(bool summonerBook) => unchecked((uint)(summonerBook ? _summoner : _champion));
}

public sealed partial class Entity
{
    /// <summary>The slots a script sealed (<c>BBSealSpellSlot</c>); the cast gate, the item and summoner casts and the cast-bit word read it.</summary>
    public SpellSlotSeals SpellSeals { get; } = new();

    /// <summary>
    /// Targeting types a script set with <c>BBSetTargetingType</c> (the slot's override targeting type; the stock
    /// <c>GetTargetingType</c> answers it before the spell data's own type): champion spellbook slot to <c>ttype_e</c> 0-10. A slot with
    /// no entry has the spell's own <c>TargettingType</c>.
    /// </summary>
    public Dictionary<int, int> SpellTargetingOverrides { get; } = new();

    /// <summary>
    /// The offset targets a script set with <c>BBSetSpellOffsetTarget</c> (the unit a
    /// <c>TTYPE_OffsetAOE</c> or offset-line spell is centred on in the client's targeter): champion spellbook slot to the unit's net id. Orianna's
    /// Dissonance and Shockwave follow her ball with it.
    /// </summary>
    public Dictionary<int, uint> SpellOffsetTargets { get; } = new();

    /// <summary>
    /// The mana cost increments a script set with <c>BBSetPARCostInc</c> (the slot's mana cost increment, its first float):
    /// champion spellbook slot 0-3 to the amount added to the spell's own cost (a negative one lowers it). Setting 0 removes the entry. The cast cost
    /// (<c>SpellSystem.SlotResourceCost</c>), the cast gate and the displayed costs of the stat block (<c>mCurManaCost0-3</c>) read it.
    /// </summary>
    public Dictionary<int, double> SpellParCostIncrements { get; } = new();

    /// <summary>
    /// The cost multipliers a script set with <c>BBSetPARMultiplicativeCostInc</c> (the slot's multiplicative mana cost increment, the second float of the
    /// slot record, 0 on a fresh slot): champion spellbook slot 0-3 to the amount added to 1 in the factor the cost is multiplied by (-0.1 is ten percent
    /// cheaper, -1 is free). Setting 0 removes the entry. Applied after <see cref="SpellParCostIncrements"/> by <c>SpellSystem.SlotResourceCost</c> and the
    /// displayed costs of the stat block.
    /// </summary>
    public Dictionary<int, double> SpellParCostMultipliers { get; } = new();

    /// <summary>
    /// The per-slot costs a windup was priced with, held from its press until its cast frame pays them (<c>SpellSystem.ChargeChampionCast</c>) or the
    /// windup ends unpaid (<c>SpellSystem.RefundChampionCast</c>, a hero's death). <c>HeroStatsService.Snapshot</c> replicates these instead of the live
    /// costs, so a cost change the cast's own hooks make at the press (Visionary's reset) reaches the clients once the cast is paid.
    /// Reconstruction (27 October 2026, owner finding 78): the shipped scripts say when the reset runs, not when retail's stat block carries it.
    /// </summary>
    public IReadOnlyList<double>? HeldManaCosts { get; set; }

    /// <summary>
    /// The top character-data layer a script pushed with <c>BBPushCharacterData</c> and has not popped, or null: the model the unit shows (and, with
    /// <c>OverrideSpells</c>, the spells it plays). A layer that carries spells is always alone; model-only layers may stack
    /// (<see cref="BuriedCharacterLayers"/>), see <c>BbNativeHandlers</c>.
    /// </summary>
    public ScriptCharacterLayer? ScriptCharacterData { get; set; }

    /// <summary>
    /// The model-only layers under <see cref="ScriptCharacterData"/>, oldest first. The client's native character-data stack (<c>CharacterDataStack</c>)
    /// keeps every push until a pop removes it and shows the top one; Udyr's four stances push their model on each other and never pop. The
    /// server keeps the ids so that a pop of one reveals the next, and the replication ledger holds the same layers.
    /// </summary>
    public List<ScriptCharacterLayer> BuriedCharacterLayers { get; } = new();

    /// <summary>Every layer the unit holds, oldest first: the client's character-data stack as the server has sent it.</summary>
    public IEnumerable<ScriptCharacterLayer> CharacterLayers =>
        ScriptCharacterData is { } top ? BuriedCharacterLayers.Append(top) : BuriedCharacterLayers;

    /// <summary>
    /// The character whose record gives the unit its base stats, attack timing, animation odds and acquisition range: the pushed layer when it
    /// carried its spells (<c>BBPushCharacterData</c> with <c>OverrideSpells</c>: Nidalee's <c>Nidalee_Cougar</c> form), else the champion itself.
    /// Reconstruction: the client re-reads the unit's character record on a push (the client's character-data setter); a layer that only
    /// changes the model (Orianna's <c>OriannaNoBall</c>, <c>OverrideSpells</c> false) leaves the champion's own numbers.
    /// </summary>
    public string StatCharacterName => ScriptCharacterData is { OverrideSpells: true } layer ? layer.SkinName : ChampionName;

    /// <summary>
    /// When a slot a script cast a child through with the cooldown check on (<c>BBSpellCast</c> with <c>OverrideCoolDownCheck=false</c>) is ready
    /// again, as game time, by stored slot (a spell slot as it is, an extra slot from 42). Only the slots that have no cooldown of their own
    /// are here: a hero's champion slots keep theirs in <see cref="SpellCooldowns"/>. A slot with no entry is ready.
    /// </summary>
    public Dictionary<int, double> ScriptSlotReadyAt { get; } = new();
}

/// <summary>A character-data layer a script pushed on a unit: the id the client stack knows it by, the model and whether it carried its spells.</summary>
public sealed record ScriptCharacterLayer(uint Id, string SkinName, bool OverrideSpells);
