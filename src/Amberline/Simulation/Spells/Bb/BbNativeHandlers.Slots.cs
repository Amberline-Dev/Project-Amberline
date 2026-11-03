using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The spell-slot natives: <c>BBSetSpell</c>, <c>BBSetTargetingType</c>, <c>BBSealSpellSlot</c> and the character-data family
/// (<c>BBPushCharacterData</c>, <c>BBPopCharacterData</c>, <c>BBPopAllCharacterData</c>). Each is bound from the stock client's own body
/// (the same bodies exist in the second client build). The client carries the shared <c>LuaBuildingBlockHelper.cpp</c>
/// code that, on the server, parses the
/// block, builds the packet and applies the change to the server's own copy of the unit. The stock client asserts on bad data these
/// bodies would send it, so every native checks before it sends: a spell name the shipped spell data lacks, a targeting type above 10, a slot
/// number outside the spellbook, a second pushed character layer on one unit.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>The spellbook slot an inventory slot number maps to (slot type 1 adds 4).</summary>
    internal const int InventorySlotStart = 4;

    /// <summary>Inventory slot numbers 0-37 are legal: stored slot + 4 stays below 42, where the extra slots start.</summary>
    private const int InventorySlotCount = 38;

    /// <summary>Extra slot numbers 0-9 are legal: stored slot 42 + n stays below 52, <c>MAX_SPELLBOOK_SLOTS</c>.</summary>
    private const int ExtraSlotCount = 10;

    /// <summary>The client's <c>ChangeSlotSpellName</c> holds <c>char spellName[64]</c>: 63 characters and the terminator.</summary>
    private const int MaxSlotSpellNameLength = 63;

    /// <summary>The highest <c>ttype_e</c> the client builds a <c>SpellTargeting</c> for; <c>TYPEE_Numof</c> is 11 and a larger value asserts.</summary>
    private const int MaxTargetingType = 10;

    /// <summary>
    /// The stored spellbook slot of a (<paramref name="type"/>, <paramref name="number"/>) pair, exactly the client's parser
    /// (the client's slot parser; the same ranges appear in its inventory helper): slot type 1 (<c>InventorySlots</c>) is stored from 4 and
    /// accepts 0-37, type 2 (<c>ExtraSlots</c>) is stored from 42 and accepts 0-9, any other type is a plain spell slot 0-3. Anything
    /// outside is an error the client logs as "Spell slot number out of range!" (and asserts "Out of range int lua for a spell slot!" in
    /// <c>BBSealSpellSlot</c>); this engine refuses it.
    /// </summary>
    internal static int StoredSlot(string native, BbSlotType type, int number)
    {
        var stored = type switch
        {
            BbSlotType.InventorySlots => number is >= 0 and < InventorySlotCount ? number + InventorySlotStart : -1,
            BbSlotType.ExtraSlots => number is >= 0 and < ExtraSlotCount ? number + SpellSystem.ExtraSpellSlotStart : -1,
            _ => number is >= 0 and < SpellSystem.ChampionSlotCount ? number : -1,
        };
        if (stored < 0)
            throw new BbRuntimeException($"{native}: spell slot number {number} is out of range for slot type {type} " +
                                         "(the stock client asserts \"Out of range int lua for a spell slot!\")");
        return stored;
    }

    private static BbSlotType SlotTypeOf(BbCall call, string native, bool required)
    {
        if (call.Value("SlotType") is double number) return (BbSlotType)(int)number;
        return required ? throw new BbRuntimeException($"{native} has no SlotType") : BbSlotType.SpellSlots;
    }

    /// <summary>Whether the spellbook parameter (<c>SlotBook</c> or <c>SpellbookType</c>, default SPELLBOOK_CHAMPION) names the summoner book.</summary>
    private static bool SummonerBook(BbCall call, string key) =>
        call.Value(key) is double book && (BbSpellbook)(int)book == BbSpellbook.SPELLBOOK_SUMMONER;

    private static bool HasNumber(BbCall call, string key) => call.Has(key) || call.Has(key + "Var") || call.Has(key + "ByLevel");

    // ------------------------------------------------------------------------------------------------------------
    // BBSetSpell: ChangeSlotSpellName 0x35
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSetSpell</c>: Target, <c>SlotType</c>/<c>SlotNumber</c>/<c>SlotBook</c> (the slot parser) and <c>SpellName</c>. The client's body
    /// builds <c>0x35</c> (a unit header, the slot byte with the summoner bit 0x80, the zero-terminated name) and applies it to the server's own
    /// spellbook (the client's change-spell routine: the slot's <c>SetSpellData</c> with the slot's
    /// current level, so the rank stays; the same rank-preserving packet Lee Sin's stage swaps use, batch 12). The engine keeps the live name in
    /// <see cref="Entity.ActiveSpellNames"/> (<see cref="ISpellEffectSink.SendSpellData"/> is that sender), which <c>BbChampionModule.SpellName</c>,
    /// <c>BBGetSlotSpellInfo</c>, <c>BBSpellCast</c> of an extra slot and the reconnect burst read. <c>SetSpellData</c> also ends with
    /// <c>mCurrentCooldown = mStartCooldown</c> (<c>SpellData*StartCooldown</c>, 0 in every shipped spell), so a swapped champion slot has no
    /// cooldown left; the client does the same on receiving the packet and the scripts set the cooldown they want after the swap.
    /// Reconstruction: the name must be in the shipped spell data (<see cref="GameData.HasSpell"/>, ignoring case: the client hashes the
    /// lower-cased name), because a packet for a spell the client cannot load is a hard assert plus an exception there. The summoner spellbook
    /// (only <c>SummonerTeleport</c> swaps it) is <see cref="SetSummonerSpell"/>.
    /// </summary>
    private void SetSpell(BbCall call)
    {
        // The native reads its unit through the unit getter; no unit, no packet.
        if (Unit(call, "TargetVar") is not { } unit) return;
        var name = call.Value("SpellName") as string;
        if (string.IsNullOrWhiteSpace(name)) throw new BbRuntimeException("BBSetSpell has no SpellName");
        var summoner = SummonerBook(call, "SlotBook");
        var slot = StoredSlot("BBSetSpell", SlotTypeOf(call, "BBSetSpell", required: false), (int)Sum(call, "SlotNumber"));
        if (name.Length > MaxSlotSpellNameLength || !name.All(char.IsAscii))
            throw new BbRuntimeException($"BBSetSpell: '{name}' does not fit ChangeSlotSpellName (63 ASCII characters)");
        if (!Spells.Data.HasSpell(name))
            throw new BbRuntimeException($"BBSetSpell: '{name}' is not in the shipped spell data; the stock client asserts on a slot name it cannot load");

        var startCooldown = ChampionSpellSupport.RawNumber(Spells.Data.Spell(name), "SpellData*StartCooldown");
        if (summoner)
        {
            SetSummonerSpell(unit, name, slot, startCooldown);
            return;
        }
        // A windup cast of this slot that is at its cast frame is retired first (the client asserts on the replacement otherwise).
        if (_host.Router.WindupCastFrame is { } frame && frame.Hero == unit.NetId && frame.Slot == slot)
        {
            _host.Router.WindupCastFrame = null;
            if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendFinishCast(unit);
        }
        while (unit.ActiveSpellNames.Count <= slot) unit.ActiveSpellNames.Add(string.Empty);
        unit.ActiveSpellNames[slot] = name;
        if (unit.UnitType == UnitType.Hero && slot < unit.SpellCooldowns.Count) unit.SpellCooldowns[slot] = startCooldown;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendSpellData(unit, name, slot);
    }

    /// <summary>
    /// <c>BBSetSpell</c> on the summoner spellbook (<c>SlotBook</c> SPELLBOOK_SUMMONER): the only shipped caller is <c>SummonerTeleport</c>, which
    /// swaps the cast for <c>TeleportCancel</c> and back to <c>summonerteleport</c>. The hero's two summoner slots are <see cref="Entity.SummonerSpells"/>
    /// (a loadout name, or the pseudo name <c>TeleportCancel</c> the summoner code recognises); the swap keeps the cooldown rule of every
    /// <c>SetSpellData</c> (the slot's cooldown becomes <c>StartCooldown</c>, 0) and goes out as <c>0x35</c> with the summoner bit, like the
    /// hand-written Teleport's own swap does. A name that is one of the thirteen loadout spells is stored in its canonical spelling (the
    /// script writes it in lower case); the second spellbook holds two slots.
    /// </summary>
    private void SetSummonerSpell(Entity unit, string name, int slot, double startCooldown)
    {
        if (unit.UnitType != UnitType.Hero || slot >= unit.SummonerSpells.Count)
            throw new BbRuntimeException($"BBSetSpell: the summoner spellbook holds {(unit.UnitType == UnitType.Hero ? unit.SummonerSpells.Count : 0)} spells, slot {slot} is outside it");
        string canonical;
        try { canonical = SummonerLoadout.CanonicalFromDisplay(name); }
        catch (ArgumentException) { canonical = name; }
        unit.SummonerSpells[slot] = canonical;
        while (unit.SummonerCooldowns.Count <= slot) unit.SummonerCooldowns.Add(0.0);
        unit.SummonerCooldowns[slot] = startCooldown;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendSpellName(unit, name, slot, summoner: true);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetTargetingType: ChangeSlotSpellType 0x1A
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSetTargetingType</c>: Target, the slot parameters of <see cref="SetSpell"/> and <c>TargetType</c> (a <c>ttype_e</c>, the integer
    /// the integer getter reads). The client's body builds <c>0x1A</c> (a unit header, the slot byte with the summoner bit, the type byte) and applies
    /// it through <c>SetSpellTargetting</c>, which stores it as the slot's override (<c>mOverrideTargettingType</c>; the stock
    /// <c>GetTargetingType</c> answers it before the spell's own type) and asserts <c>mSpellTargeting.get()</c> for a type of 11 or more, where
    /// <c>SpellTargetingFactory</c> builds nothing. The engine stores the override in <see cref="Entity.SpellTargetingOverrides"/> (the module and
    /// <c>SpellSystem</c> read it) and refuses a type outside 0-10 before it sends. The summoner spellbook is not bound: no shipped script sets its targeting
    /// (all 16 call sites are the champion book), and nothing in the summoner code would read an override.
    /// </summary>
    private void SetTargetingType(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        if (!HasNumber(call, "TargetType")) throw new BbRuntimeException("BBSetTargetingType has no TargetType");
        var type = (int)Sum(call, "TargetType");
        if (type is < 0 or > MaxTargetingType)
            throw new BbRuntimeException($"BBSetTargetingType: targeting type {type} is not a ttype_e (0-{MaxTargetingType}); the stock client asserts on it");
        if (SummonerBook(call, "SlotBook"))
            throw BbSpellHost.Unbound("BBSetTargetingType on the summoner spellbook", "summoner spells");
        var slot = StoredSlot("BBSetTargetingType", SlotTypeOf(call, "BBSetTargetingType", required: false), (int)Sum(call, "SlotNumber"));
        unit.SpellTargetingOverrides[slot] = type;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendSpellTargetingType(unit, slot, checked((byte)type));
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSealSpellSlot
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSealSpellSlot</c>: Target, <c>SlotType</c>, <c>SpellSlot</c> and <c>State</c> (all required) and <c>SpellbookType</c> (optional, default
    /// SPELLBOOK_CHAMPION). The client's body chooses the unit's second spellbook for <c>SpellbookType</c> 1 when the unit has one (the unit's
    /// virtual slot 139: <c>obj_AI_Hero</c> returns the second book, while <c>obj_AI_Base</c>, minion and turret
    /// return 0, so a non-hero keeps the main book), checks the slot against the ranges of <see cref="StoredSlot"/> and hard-asserts outside them, then
    /// calls the seal setter: the slot's <c>mIsSealed</c> byte is set and, when it changed, the owner's
    /// <c>SyncStats</c> runs. No packet is built by the body itself: the sealed bit reaches the client as the slot's bit in the replicated
    /// <c>mCanCastBits1</c> (main book) or <c>mCanCastBits2</c> (second book) word, which the can-use check tests (<c>USE_Disabled</c>).
    /// The engine keeps the seal in <see cref="Entity.SpellSeals"/>; <see cref="SpellSystem"/> (champion, summoner and item casts) and the hero
    /// stat snapshot read it, and a change pushes the stats like <c>SyncStats</c>.
    /// </summary>
    private void SealSpellSlot(BbCall call)
    {
        // The body only runs with a Target (and, in the client, only when every required parameter parsed).
        if (Unit(call, "TargetVar") is not { } unit) return;
        var type = SlotTypeOf(call, "BBSealSpellSlot", required: true);
        if (!HasNumber(call, "SpellSlot")) throw new BbRuntimeException("BBSealSpellSlot has no SpellSlot");
        if (call.Value("State") is not { } state) throw new BbRuntimeException("BBSealSpellSlot has no State");
        var slot = StoredSlot("BBSealSpellSlot", type, (int)Sum(call, "SpellSlot"));
        var summoner = SummonerBook(call, "SpellbookType") && unit.UnitType == UnitType.Hero;
        var byCast = _host.HasFrame && _host.Frame.Hook is "SelfExecute" or "TargetExecute";
        if (!unit.SpellSeals.Set(summoner, slot, BbRuntime.Truthy(state), byCast)) return;
        if (unit.UnitType == UnitType.Hero) Spells.SendChampionStats(unit);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBPushCharacterData, BBPopCharacterData, BBPopAllCharacterData
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBPushCharacterData</c>: Target, <c>SkinName</c>, <c>OverrideSpells</c> (optional, default false) and the output <c>ID</c>. The client's body
    /// takes the next id from a counter shared with <c>BBPopAllCharacterData</c>, pushes the layer on the unit's character-data stack, builds
    /// <c>0x9F</c> (a unit header, the id, the <c>useSpells</c> byte, the 64-byte model name) and hands the id to the script. A layer with
    /// <c>OverrideSpells</c> makes the unit's spellbook take the new character's spells (the unit's character-data setter runs
    /// the spellbook's init, see <see cref="ReinitSpellbook"/>: each named slot's cooldown restarts, ranks, seals and targeting
    /// overrides stay; Nidalee's cougar script saves her three cooldowns before the push and restores them after the pop for that reason). The
    /// engine mirrors that for a hero, naming the
    /// pushed character's <c>Data*Spell1..4</c> in <see cref="Entity.ActiveSpellNames"/>.
    /// <para>
    /// The client's <c>CharacterDataStack</c> is a real stack (push appends an entry with the id, a pop by id erases that entry, the pop-all clears it,
    /// and the top entry names the character the unit shows: the stack's accessors), so a model-only layer may be pushed
    /// on a unit that holds another: Udyr's four stances push <c>UdyrTiger</c>, <c>UdyrTurtle</c>, <c>Udyr</c> and <c>UdyrPhoenix</c> on each other and none
    /// of them ever pops. The held layer moves to <see cref="Entity.BuriedCharacterLayers"/> and the new one is the top. A layer that carries spells stays
    /// alone (it re-initialises the book, and a layer under it would have to restore the book it replaced): a push on or under one is a script error
    /// (<see cref="BbRuntimeException"/>, contained and logged as <c>script.exception</c>). The stock client runs both pop handlers for every 0x6A
    /// (batch 256, <see cref="PopAllCharacterData"/>): a pop by id also clears the client's whole stack, so after any pop the client shows the unit's
    /// base character, whatever the server's stack still holds (the retail wire is the same 9-byte pop). The layers are replicated through the
    /// sink's persistent ledger (late observers and fog repairs replay them in id order), like the hand-written Nidalee and Orianna models. The model
    /// name must be a shipped character (<see cref="GameData.HasCharacterData"/>).
    /// </para>
    /// </summary>
    private void PushCharacterData(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        var skin = call.Value("SkinName") as string;
        if (string.IsNullOrWhiteSpace(skin)) throw new BbRuntimeException("BBPushCharacterData has no SkinName");
        if (skin.Length > 63 || !skin.All(char.IsAscii))
            throw new BbRuntimeException($"BBPushCharacterData: '{skin}' does not fit ChangeCharacterData (63 ASCII characters)");
        if (!Spells.Data.HasCharacterData(skin))
            throw new BbRuntimeException($"BBPushCharacterData: '{skin}' is not a shipped character");
        var wantSpells = Flag(call, "OverrideSpells", false);
        // Reconstruction: a second push of the layer the unit already shows (the same model on top) sends nothing and returns the layer's id. Retail pushes
        // another stack entry (the stack's pop erases one entry); the picture is the same. OrianaGhostSelf.OnBuffDeactivate pushes OriannaNoBall
        // twice on one deactivation (its two spellings of the skin name, "Orianna" and "orianna", both pass CO_EQUAL), and a stance pressed again is
        // the same case for Udyr. A depth count (one pop per entry) was tried and reverted: after the pop of the ball layer that Orianna's E makes,
        // the ball stays off (ChampionLiveRegressionChecks.OriannaERecallsTheBallAndShields expects it back). Which pop reaches the second entry on
        // the client is not recovered from the shipped files, so the one-layer model stays.
        if (unit.ScriptCharacterData is { } same && string.Equals(same.SkinName, skin, StringComparison.OrdinalIgnoreCase) && same.OverrideSpells == wantSpells)
        {
            StoreOut(call, "ID", (double)same.Id);
            return;
        }
        if (unit.ScriptCharacterData is { } active)
        {
            if (active.OverrideSpells || wantSpells)
                throw new BbRuntimeException($"BBPushCharacterData: the unit already holds the pushed layer {active.SkinName} (id {active.Id}) and one of the two " +
                                             "carries spells; a layer that re-initialises the spellbook stays alone");
            unit.BuriedCharacterLayers.Add(active);
        }
        var overrideSpells = wantSpells;
        var id = _host.NextCharacterDataId();
        unit.ScriptCharacterData = new ScriptCharacterLayer(id, skin, overrideSpells);
        if (overrideSpells) ReinitSpellbook(unit, skin);
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendPushCharacterData(unit, id, skin, overrideSpells);
        // The client rebuilds its book from the 0x9F (the book's init resets every slot record), so the stat block with the mana costs, the permission
        // bits and the tooltip numbers of the new book follows it on the wire, not the other way round (the same order the 0x35 replacement had).
        if (overrideSpells) RefreshSpellbookStats(unit);
        StoreOut(call, "ID", (double)id);
    }

    /// <summary>
    /// <c>BBPopCharacterData</c>: Target and the layer <c>ID</c>. The client applies it locally as a pop of that entry of the stack and builds <c>0x6A</c>
    /// with the id (9 bytes); its handler removes the layer with that id. The engine removes the layer when the unit holds it, on top (the layer under it
    /// shows again) or buried; a pop of an id the unit does not hold (a stale or unset variable) changes nothing and sends nothing.
    /// </summary>
    private void PopCharacterData(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        if (call.Value("IDVar") is { } name)
        {
            // An unset or non-numeric id pops nothing. Orianna's OrianaGhostSelf pops "TempSkin" on the activation after a death that came before
            // any push (the buff is cleared at death, its deactivation sets GhostInitialized but pushes nothing for a dead owner), on every respawn.
            if (call.Table("IDVarTable", create: false)[name] is not double value || value < 0.0) return;
            PopLayer(unit, (uint)value);
            return;
        }
        if (!HasNumber(call, "ID")) throw new BbRuntimeException("BBPopCharacterData has no ID");
        PopLayer(unit, (uint)Sum(call, "ID"));
    }

    /// <summary>
    /// <c>BBPopAllCharacterData</c>: Target. The client's body advances the shared id counter, clears the unit's character-data stack
    /// and sends the 5-byte <c>0x6A</c> (opcode, unit) whatever the stack held. Batch 256: the engine does the same, one
    /// <see cref="ISpellEffectSink.SendPopAllCharacterData"/>; the persistent ledger drops every layer of the unit with it (before batch 256 it
    /// sent one 9-byte pop per held layer, the same client effect on a longer wire).
    /// <para>
    /// Batch 256, the client's dispatch: the unit's network handler registers a pop handler (erase the entry whose id matches) and a
    /// pop-all handler (clear) under the unit's net id and event 0x6A. Every 0x6A, 9 or 5 bytes, runs both, and the stack update
    /// re-applies the character only when the top name changes. The delivery ledger keeps that rule for its views
    /// (<c>ReplicationDelivery.SendCharacterData</c>).
    /// </para>
    /// </summary>
    private void PopAllCharacterData(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        _host.NextCharacterDataId();
        var spells = unit.CharacterLayers.Any(layer => layer.OverrideSpells);
        foreach (var layer in unit.CharacterLayers.Reverse().ToArray()) RemoveLayer(unit, layer, send: false);
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendPopAllCharacterData(unit);
        if (spells) RefreshSpellbookStats(unit);
    }

    private void PopLayer(Entity unit, uint id)
    {
        var held = unit.CharacterLayers.FirstOrDefault(layer => layer.Id == id);
        if (held is not null) RemoveLayer(unit, held);
    }

    private void RemoveLayer(Entity unit, ScriptCharacterLayer layer, bool send = true)
    {
        if (unit.ScriptCharacterData?.Id == layer.Id)
        {
            // The layer under it shows again. A buried layer is model-only, so the spellbook only needs restoring for the top one's own spells.
            unit.ScriptCharacterData = unit.BuriedCharacterLayers.Count > 0 ? unit.BuriedCharacterLayers[^1] : null;
            if (unit.ScriptCharacterData is not null) unit.BuriedCharacterLayers.RemoveAt(unit.BuriedCharacterLayers.Count - 1);
            if (layer.OverrideSpells) ReinitSpellbook(unit, null);
        }
        else unit.BuriedCharacterLayers.RemoveAll(buried => buried.Id == layer.Id);
        if (!send) return;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendPopCharacterData(unit, layer.Id);
        if (layer.OverrideSpells) RefreshSpellbookStats(unit);
    }

    /// <summary>
    /// The spellbook as a layer that carries its spells changes it: the four champion slots take the spells of <paramref name="character"/>
    /// (null: the unit's own character, the live names are cleared), keep their ranks and restart their cooldowns. Batch 292 traced the client.
    /// The 0x9F handler's stack update calls <c>ChangeCharacterData</c>, which calls
    /// the unit's <c>SetCharacterData</c>. When the spells' character changes, that runs the
    /// init overload with keep-ranks set. For each of the four champion slots and the eight extra slots the
    /// new character names, it calls <c>SetSpellData</c>, which sets the spell, keeps the rank (at
    /// most 6), sets the cooldown to now + <c>StartCooldown</c> (0 in every shipped spell) and sets the toggle state from
    /// <c>IsToggleSpell</c>. It leaves the spellbook's sealed masks and <c>mOverrideTargettingType</c> alone, so
    /// seals and targeting overrides stay. The other <c>Init</c> (which zeroes ranks, seals and every slot record) has one caller, the
    /// <c>AvatarPimpl</c> constructor, once per unit, not the push. The extra
    /// slots follow the pushed character through <see cref="Entity.StatCharacterName"/>. The toggle state is the client's display; the
    /// server has none.
    /// </summary>
    private void ReinitSpellbook(Entity unit, string? character)
    {
        if (unit.UnitType != UnitType.Hero) return;
        var names = character is null ? null : Spells.Data.Champion(character).SpellNames;
        for (var slot = 0; slot < SpellSystem.ChampionSlotCount; slot++)
        {
            while (unit.ActiveSpellNames.Count <= slot) unit.ActiveSpellNames.Add(string.Empty);
            unit.ActiveSpellNames[slot] = names is not null && slot < names.Count ? names[slot] : string.Empty;
            if (slot < unit.SpellCooldowns.Count) unit.SpellCooldowns[slot] = 0.0;
        }
        // The new character's own attack (Nidalee_Cougar hits at its cast frame, the human form throws a javelin at 1750).
        if (unit.ScriptAttackOverride is null) unit.MissileSpeed = AttackMissileSpeed(unit);
        // The unit's character changed, so its base stats, attack range and timing are re-read (Nidalee's cougar form: Entity.StatCharacterName).
        // The stat block goes out after the push or pop packet (RefreshSpellbookStats).
        // Batch 174 part 2: the hero stat block exists for heroes only. A non-hero's spellbook change (the Worm's ResistantSkin and
        // CharScriptWorm set their spells on the unit) re-reads no hero stats here; its base stats are the character record's.
        if (unit.UnitType == UnitType.Hero && Spells.HeroStats is { } stats) stats.Refresh(unit);
    }

    /// <summary>The stat block, with the mana costs and permission bits of the new book and the script's tooltip numbers, for a book that was just re-initialised.</summary>
    private void RefreshSpellbookStats(Entity unit)
    {
        if (unit.UnitType != UnitType.Hero) return;
        if (Spells.HeroStats is { } stats) stats.Push(unit);
        else Spells.SendChampionStats(unit);
    }
}
