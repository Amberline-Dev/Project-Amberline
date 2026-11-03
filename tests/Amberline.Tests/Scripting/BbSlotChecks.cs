using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, spell-slot family (19 October 2026): <c>BBSealSpellSlot</c>, <c>BBSetSpell</c>, <c>BBSetTargetingType</c>, the
/// character-data natives and the <c>BBSpellCast</c> children of CastType 0 and 1. Each check runs a one-hook probe script through the
/// production router of a script-driven hero (Soraka or Taric, whose real scripts the child casts then run) and reads what the
/// natives did to the simulation and to the effect sink. The expected values come from the client bodies the natives are bound to,
/// and from the shipped data.
/// </summary>
public static class BbSlotChecks
{
    // ------------------------------------------------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Records the slot and character-data calls the natives make on the effect sink, in order.</summary>
    public class SinkRecorder : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public List<string> Calls { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is "SendSpellData" or "SendSpellTargetingType" or "SendPushCharacterData" or "SendPopCharacterData" or "SendPopAllCharacterData" or "SendSpellName")
                Calls.Add(targetMethod.Name + "(" + string.Join(",", (args ?? []).Select(arg => arg is Entity unit ? unit.NetId.ToString("X8") : arg?.ToString())) + ")");
            try { return targetMethod.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    internal sealed class Probe : IDisposable
    {
        public Probe(string champion)
        {
            Match = new BbM3.Match(champion);
            Match.Learn(1);
            var field = typeof(SpellSystem).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var proxy = DispatchProxy.Create<ISpellEffectSink, SinkRecorder>();
            var recorder = (SinkRecorder)(object)proxy;
            recorder.Target = field.GetValue(Match.Runtime.Spells)!;
            recorder.Calls = Calls;
            field.SetValue(Match.Runtime.Spells, proxy);
        }

        public BbM3.Match Match { get; }
        public List<string> Calls { get; } = [];
        public Entity Hero => Match.Hero;

        /// <summary>Runs <paramref name="blocks"/> as a TargetExecute hook of a probe spell cast by the hero (Owner) at <paramref name="target"/>.</summary>
        public void Run(Entity? target, params BbBlock[] blocks)
        {
            var script = new BbScript("SlotProbe", "Spells/SlotProbe.luaobj", [], [new BbEventTable("TargetExecute", blocks)], []);
            var at = new Position3(Hero.Position.X, 0, Hero.Position.Z);
            Match.Module.Router!.RunTargetExecute(script, Hero, 0, 1, 0.0, at, at, target ?? Hero);
        }

        public IReadOnlyList<BbScriptFailure> Failures => Match.Module.ScriptFailures;

        /// <summary>Runs the blocks and returns the failure the hook raised (the engine contains it and records it).</summary>
        public string Fails(Entity? target, params BbBlock[] blocks)
        {
            var before = Failures.Count;
            Run(target, blocks);
            TestAssert.Equal(before + 1, Failures.Count, "the probe hook failed once");
            return Failures[^1].Message;
        }

        public void Dispose() => Match.Dispose();
    }

    internal static BbBlock B(string function, params (string Key, BbValue Value)[] fields) =>
        new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), []);

    internal static (string, BbValue) F(string key, string text) => (key, new BbValue.Text(text));
    internal static (string, BbValue) F(string key, double number) => (key, new BbValue.Number(number));
    internal static (string, BbValue) F(string key, bool flag) => (key, new BbValue.Bool(flag));
    internal static (string, BbValue) Owner => F("TargetVar", "Owner");

    internal static BbBlock Seal(int slot, bool state, double slotType = 0, double book = 0) => B("BBSealSpellSlot", Owner,
        F("SlotType", slotType), F("SpellSlot", slot), F("State", state), F("SpellbookType", book));

    internal static BbBlock SetSpell(string name, int slot, double slotType = 0, double book = 0) => B("BBSetSpell", Owner,
        F("SlotType", slotType), F("SlotNumber", slot), F("SlotBook", book), F("SpellName", name));

    internal static BbBlock SetTargeting(int slot, double type) => B("BBSetTargetingType", Owner,
        F("SlotType", 0), F("SlotNumber", slot), F("SlotBook", 0), F("TargetType", type));

    internal static BbBlock Push(string skin, bool spells, string idVar) => B("BBPushCharacterData", Owner,
        F("SkinName", skin), F("OverrideSpells", spells), F("IDVar", idVar), F("IDVarTable", "CharVars"));

    internal static BbBlock Pop(string idVar) => B("BBPopCharacterData", Owner, F("IDVar", idVar), F("IDVarTable", "CharVars"));

    private static byte[] Bytes(PlaintextPacket packet) => packet.Plaintext.ToArray();

    // ------------------------------------------------------------------------------------------------------------
    // The slot parser and the packets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-slot-numbers-follow-the-client-parser")]
    public static void SlotRangesAreTheClientsAndOutsideThemIsRefused()
    {
        // The client's slot setter: spells 0-3, inventory 0-37 stored from 4, extra 0-9 stored from 42; any other slot type is a spell slot.
        TestAssert.Equal([0, 3], new[] { BbNativeHandlers.StoredSlot("t", BbSlotType.SpellSlots, 0), BbNativeHandlers.StoredSlot("t", BbSlotType.SpellSlots, 3) }, "spell slots");
        TestAssert.Equal([4, 41], new[] { BbNativeHandlers.StoredSlot("t", BbSlotType.InventorySlots, 0), BbNativeHandlers.StoredSlot("t", BbSlotType.InventorySlots, 37) }, "inventory slots");
        TestAssert.Equal([42, 51], new[] { BbNativeHandlers.StoredSlot("t", BbSlotType.ExtraSlots, 0), BbNativeHandlers.StoredSlot("t", BbSlotType.ExtraSlots, 9) }, "extra slots");
        TestAssert.Equal(2, BbNativeHandlers.StoredSlot("t", (BbSlotType)7, 2), "an unknown slot type reads as a spell slot, as the client's else branch does");
        foreach (var (type, number) in new[]
                 {
                     (BbSlotType.SpellSlots, 4), (BbSlotType.SpellSlots, -1), (BbSlotType.InventorySlots, 38), (BbSlotType.InventorySlots, -1),
                     (BbSlotType.ExtraSlots, 10), (BbSlotType.ExtraSlots, -1),
                 })
            TestAssert.Throws<BbRuntimeException>(() => BbNativeHandlers.StoredSlot("BBSealSpellSlot", type, number), $"{type} {number} is out of range");
        // The packets the natives send, byte for byte: 0x35 = header, slot byte (0x80 for the summoner book), name, NUL; 0x1A = header, slot byte, type.
        var id = 0x40000001u;
        TestAssert.Equal([0x35, 0x01, 0x00, 0x00, 0x40, 0x2A, (byte)'W', (byte)'i', (byte)'s', (byte)'h', 0x00],
            Bytes(SpellPackets.ChangeSlotSpellName(id, 42, "Wish", isSummoner: false)), "BBSetSpell packet");
        TestAssert.Equal([0x1A, 0x01, 0x00, 0x00, 0x40, 0x81, 0x06], Bytes(SpellPackets.ChangeSlotSpellType(id, 1, 6, isSummoner: true)), "BBSetTargetingType packet");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSealSpellSlot
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-seal-spell-slot-gates-the-cast-and-clears-the-cast-bit")]
    public static void ASealedSlotCannotBeCastAndItsBitIsClear()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        TestAssert.True(match.CanCast(1, match.Ally).Allowed, "Astral Blessing is castable on an ally before the seal");
        probe.Run(null, Seal(1, true));
        var refused = match.CanCast(1, match.Ally);
        TestAssert.True(!refused.Allowed && refused.Failure == SpellCastFailure.Disabled, "a sealed slot is Disabled: " + refused.Reason);
        TestAssert.False(match.Cast(1, match.Ally), "the cast request is refused");
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Hero);
        TestAssert.Equal(uint.MaxValue & ~2u, snapshot.CanCastBits1, "slot 1's bit is clear in mCanCastBits1 and the others are open");
        TestAssert.Equal(uint.MaxValue, snapshot.CanCastBits2, "the summoner word is untouched");
        probe.Run(null, Seal(1, false));
        TestAssert.True(match.CanCast(1, match.Ally).Allowed, "unsealed again");
        TestAssert.Equal(uint.MaxValue, match.Runtime.HeroStats.Snapshot(match.Hero).CanCastBits1, "the bit is open again");

        // The summoner spellbook has its own word and its own gate (Zhonya's stasis, Yorick's ghouls, Gate, Xerath's Locus, Impale seal both slots).
        probe.Run(null, Seal(0, true, book: 1), Seal(1, true, book: 1));
        var summoner = match.Runtime.HeroStats.Snapshot(match.Hero);
        TestAssert.Equal(uint.MaxValue & ~3u, summoner.CanCastBits2, "both summoner bits are clear in mCanCastBits2");
        TestAssert.Equal(uint.MaxValue, summoner.CanCastBits1, "the champion word is untouched");
        var at = new Position3(match.Hero.Position.X, 0, match.Hero.Position.Z);
        TestAssert.False(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(0, true, at, at, 0), match.Now), "a sealed summoner slot is refused");
        TestAssert.False(match.Runtime.Spells.HandleCastRequest(match.Hero, new SpellCastRequest(1, true, at, at, 0), match.Now), "the other one too");
        probe.Run(null, Seal(0, false, book: 1), Seal(1, false, book: 1));
        TestAssert.Equal(uint.MaxValue, match.Runtime.HeroStats.Snapshot(match.Hero).CanCastBits2, "unsealed");

        // Inventory slots are stored from 4 (DeathDefiedBuff seals the six item slots).
        probe.Run(null, Seal(2, true, slotType: 1));
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 6) && !match.Hero.SpellSeals.IsSealed(false, 2), "inventory slot 2 is stored slot 6");
        TestAssert.Equal(uint.MaxValue & ~(1u << 6), match.Runtime.HeroStats.Snapshot(match.Hero).CanCastBits1, "and its bit is clear");
        probe.Run(null, Seal(2, false, slotType: 1));

        // The client hard-asserts outside the ranges; the engine refuses and changes nothing.
        TestAssert.True(probe.Fails(null, Seal(4, true)).Contains("out of range", StringComparison.Ordinal), "spell slot 4");
        TestAssert.True(probe.Fails(null, Seal(38, true, slotType: 1)).Contains("out of range", StringComparison.Ordinal), "inventory slot 38");
        TestAssert.True(probe.Fails(null, Seal(10, true, slotType: 2)).Contains("out of range", StringComparison.Ordinal), "extra slot 10");
        TestAssert.Equal(uint.MaxValue, match.Runtime.HeroStats.Snapshot(match.Hero).CanCastBits1, "no bit changed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSetSpell / BBSetTargetingType
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-set-spell-swaps-the-slot-keeps-the-rank-and-validates-the-name")]
    public static void SetSpellSwapsTheNameKeepsTheRankAndRefusesUnknownNames()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = match.Hero;
        TestAssert.Equal("Starcall", match.Module.SpellName(hero, 0), "the character's Q");
        hero.SpellCooldowns[0] = 5.0;
        probe.Run(null, SetSpell("AstralBlessing", 0));
        TestAssert.Equal("AstralBlessing", match.Module.SpellName(hero, 0), "the module reads the live name");
        TestAssert.Equal("AstralBlessing", match.Runtime.Spells.ChampionSpellName(hero, 0), "so does the spell system");
        TestAssert.Equal(1, hero.SpellRanks[0], "the rank stays (0x35, not 0x73)");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "the spell-data setter ends with mCurrentCooldown = mStartCooldown (0)");
        TestAssert.Equal([$"SendSpellData({hero.NetId:X8},AstralBlessing,0)"], probe.Calls.ToArray(), "one rank-preserving slot-name send");
        // The slot now casts as the swapped spell: an ally target, no area (Starcall took none).
        TestAssert.True(match.CanCast(0, match.Ally).Allowed, "the swapped Q is Astral Blessing: castable on an ally");
        TestAssert.False(match.CanCast(0, match.Foe).Allowed, "and refuses an enemy by the new spell's flags");
        probe.Run(null, SetSpell("starcall", 0));
        TestAssert.Equal("starcall", match.Module.SpellName(hero, 0), "names are matched ignoring case, as the client's hash does");
        TestAssert.True(match.CanCast(0, null).Allowed || match.CanCast(0, null).Failure != SpellCastFailure.Target, "back to the self-area Q");

        // Extra slots are stored from 42 and keep their own names.
        probe.Run(null, SetSpell("Starcall", 0, slotType: 2));
        TestAssert.Equal("Starcall", hero.ActiveSpellNames[42], "extra slot 0 is stored slot 42");

        // The refusals: nothing changes and nothing is sent.
        var sent = probe.Calls.Count;
        TestAssert.True(probe.Fails(null, SetSpell("NoSuchSpellAnywhere", 0)).Contains("not in the shipped spell data", StringComparison.Ordinal), "an unknown name");
        TestAssert.True(probe.Fails(null, SetSpell("Wish", 4)).Contains("out of range", StringComparison.Ordinal), "a slot outside the book");
        TestAssert.True(probe.Fails(null, SetSpell("Wish", 2, book: 1)).Contains("summoner spellbook holds", StringComparison.Ordinal), "the summoner book holds two slots");
        TestAssert.Equal(sent, probe.Calls.Count, "no packet for any refusal");
        TestAssert.Equal("starcall", match.Module.SpellName(hero, 0), "the name is unchanged");
    }

    [Check("scripting.bb-set-targeting-type-overrides-the-slot-and-refuses-types-above-ten")]
    public static void SetTargetingTypeOverridesTheSlot()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = match.Hero;
        TestAssert.False(match.CanCast(1, null).Allowed, "Astral Blessing is a unit-target spell: no target, no cast");
        TestAssert.True(match.Runtime.Spells.TargetsUnit(hero, 1), "and the input gate reads a unit target");
        probe.Run(null, SetTargeting(1, 0));
        TestAssert.Equal(0, hero.SpellTargetingOverrides[1], "TTYPE_Self is stored for slot 1");
        TestAssert.Equal([$"SendSpellTargetingType({hero.NetId:X8},1,0,False)"], probe.Calls.ToArray(), "the 0x1A send");
        TestAssert.True(match.CanCast(1, null).Allowed, "the overridden slot casts on its caster with no target");
        TestAssert.False(match.Runtime.Spells.TargetsUnit(hero, 1), "and the input gate no longer expects a unit");
        probe.Run(null, SetTargeting(1, 1));
        TestAssert.False(match.CanCast(1, null).Allowed, "back to a unit target");

        var sent = probe.Calls.Count;
        TestAssert.True(probe.Fails(null, SetTargeting(1, 11)).Contains("0-10", StringComparison.Ordinal), "a type of 11 asserts in the client");
        TestAssert.True(probe.Fails(null, SetTargeting(1, -1)).Contains("0-10", StringComparison.Ordinal), "a negative type");
        TestAssert.Equal(sent, probe.Calls.Count, "nothing was sent");
        TestAssert.Equal(1, hero.SpellTargetingOverrides[1], "the override is unchanged");
        probe.Run(null, SetTargeting(1, 10));
        TestAssert.Equal(10, hero.SpellTargetingOverrides[1], "10 is the highest legal type (TYPEE_Numof is 11)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Character data
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-character-data-keeps-one-layer-per-unit-and-replays-the-spellbook")]
    public static void PushAndPopCharacterDataKeepOneLayerAndRebuildTheSpellbook()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = match.Hero;
        var cougarSpells = GameData.Default.Champion("Nidalee_Cougar").SpellNames;
        hero.SpellCooldowns[0] = 6.0;
        hero.SpellTargetingOverrides[1] = 0;
        probe.Run(null, Seal(2, true), Push("Nidalee_Cougar", true, "CougarID"));
        var id = (uint)(double)match.Module.CharVars(hero)["CougarID"]!;
        TestAssert.Equal(1u, id, "the first push gets the first id of the shared counter");
        TestAssert.True(hero.ScriptCharacterData is { SkinName: "Nidalee_Cougar", OverrideSpells: true } layer && layer.Id == id, "the layer is recorded");
        TestAssert.Equal(cougarSpells.Take(4).ToArray(), hero.ActiveSpellNames.Take(4).ToArray(), "the spellbook takes the pushed character's four spells");
        TestAssert.Equal(cougarSpells[0], match.Module.SpellName(hero, 0), "and the module reads them");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "SetSpellData restarts the slot's cooldown at StartCooldown (0)");
        // Batch 292 (N14): the push runs the spellbook init (keep ranks), which only calls the spell-data setter on the
        // slots the new character names. That writes the spell, the rank, the cooldown and the toggle state, not the spellbook's sealed masks
        // nor mOverrideTargettingType. Before batch 292 the push cleared both.
        TestAssert.True(hero.SpellSeals.IsSealed(false, 2) && hero.SpellTargetingOverrides.GetValueOrDefault(1, -1) == 0,
            "the seal and the targeting override stay");
        TestAssert.Equal([$"SendPushCharacterData({hero.NetId:X8},{id},Nidalee_Cougar,True)"], probe.Calls.ToArray(), "the 0x9F send");

        // A layer that carries spells stays alone (it re-initialises the book): a second push on it is a script error.
        TestAssert.True(probe.Fails(null, Push("OriannaNoBall", false, "Other")).Contains("stays alone", StringComparison.Ordinal), "a second push");
        TestAssert.True(probe.Fails(null, Push("NoSuchCharacterAnywhere", false, "Other")).Contains("not a shipped character", StringComparison.Ordinal),
            "an unknown model is refused too");
        TestAssert.Equal(1, probe.Calls.Count, "no further send");

        // A pop of an id the unit does not hold changes nothing; its own id pops the layer and restores the character's own spells.
        match.Module.CharVars(hero)["WrongID"] = 999.0;
        probe.Run(null, Pop("WrongID"));
        TestAssert.True(hero.ScriptCharacterData is not null && probe.Calls.Count == 1, "a stale id pops nothing");
        probe.Run(null, Pop("CougarID"));
        TestAssert.True(hero.ScriptCharacterData is null, "the layer is gone");
        TestAssert.Equal("Starcall", match.Module.SpellName(hero, 0), "the character's own Q is back");
        TestAssert.Equal(2, probe.Calls.Count, "one pop send");
        TestAssert.Equal($"SendPopCharacterData({hero.NetId:X8},{id})", probe.Calls[1], "the 0x6A send carries the layer id");

        // PopAll pops whatever layer the unit holds and burns an id like the client's counter does. Batch 256: its wire is the body's own 5-byte
        // 0x6A, one send whatever the stack held, not a 9-byte pop per layer.
        probe.Run(null, Push("OriannaNoBall", false, "BallID"));
        TestAssert.Equal(2.0, match.Module.CharVars(hero)["BallID"], "the second push is id 2");
        probe.Run(null, B("BBPopAllCharacterData", Owner));
        TestAssert.True(hero.ScriptCharacterData is null, "PopAll cleared the layer");
        TestAssert.Equal($"SendPopAllCharacterData({hero.NetId:X8})", probe.Calls[^1], "through the 5-byte pop-all");
        TestAssert.Equal(4, probe.Calls.Count, "one send for the pop-all");
        probe.Run(null, Push("OriannaNoBall", false, "BallID2"));
        TestAssert.Equal(4.0, match.Module.CharVars(hero)["BallID2"], "PopAll advanced the shared counter once (ids 1, 2, [3], 4)");
        TestAssert.Equal(0, probe.Failures.Count(failure => failure.Hook != "TargetExecute" || failure.Script != "SlotProbe"), "only the probe's refusals failed");
    }

    [Check("scripting.bb-character-data-model-layers-stack-like-the-clients-stack")]
    public static void ModelOnlyLayersStackAndPopInOrder()
    {
        // The client's CharacterDataStack is a real stack (push appends, a pop by id erases that entry, the top one is shown): Udyr's four stances push
        // their model on each other and never pop. A model-only layer therefore stacks on another model-only layer.
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = match.Hero;
        probe.Run(null, Push("OriannaNoBall", false, "First"), Push("Udyr", false, "Second"), Push("OriannaNoBall", false, "Third"));
        var first = (uint)(double)match.Module.CharVars(hero)["First"]!;
        var second = (uint)(double)match.Module.CharVars(hero)["Second"]!;
        var third = (uint)(double)match.Module.CharVars(hero)["Third"]!;
        TestAssert.Equal(new[] { first, second, third }, hero.CharacterLayers.Select(layer => layer.Id).ToArray(), "three layers, oldest first");
        TestAssert.Equal(third, hero.ScriptCharacterData!.Id, "the last push is the top");
        TestAssert.Equal(3, probe.Calls.Count(call => call.StartsWith("SendPushCharacterData", StringComparison.Ordinal)), "each push goes out");
        // The same model on top again sends nothing (an entry nothing could tell apart).
        probe.Run(null, Push("OriannaNoBall", false, "Again"));
        TestAssert.Equal((double)third, match.Module.CharVars(hero)["Again"], "returns the top layer's id");
        TestAssert.Equal(3, probe.Calls.Count, "and sends nothing");
        // A pop of the top reveals the layer under it; a pop of a buried layer leaves the top alone.
        probe.Run(null, Pop("Third"));
        TestAssert.Equal(second, hero.ScriptCharacterData!.Id, "the layer under shows again");
        probe.Run(null, Pop("First"));
        TestAssert.Equal(new[] { second }, hero.CharacterLayers.Select(layer => layer.Id).ToArray(), "a buried layer is removed on its own");
        // A layer that carries spells cannot go on top of a model.
        TestAssert.True(probe.Fails(null, Push("Nidalee_Cougar", true, "Spells")).Contains("stays alone", StringComparison.Ordinal), "a spell layer stays alone");
        // PopAll clears the whole stack with the body's one 5-byte 0x6A (batch 256; before, one 9-byte pop per layer).
        probe.Run(null, Push("OriannaNoBall", false, "Fourth"));
        var before = probe.Calls.Count;
        probe.Run(null, B("BBPopAllCharacterData", Owner));
        TestAssert.True(hero.ScriptCharacterData is null && !hero.CharacterLayers.Any(), "PopAll cleared every layer");
        TestAssert.Equal(new[] { $"SendPopAllCharacterData({hero.NetId:X8})" }, probe.Calls.Skip(before).ToArray(), "one pop-all for the two layers");
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpellCast children of CastType 0 and 1
    // ------------------------------------------------------------------------------------------------------------

    internal static BbBlock Child(int index, string? target) => B("BBSpellCast", ("CasterVar", new BbValue.Text("Owner")),
        target is null ? ("TargetVar", new BbValue.Text("Nothing")) : ("TargetVar", new BbValue.Text(target)),
        F("SlotNumber", index), F("SlotType", 2), F("OverrideForceLevel", 1), F("OverrideCoolDownCheck", true), F("FireWithoutCasting", true));

    [Check("scripting.bb-spell-cast-child-of-cast-type-0-lands-at-once")]
    public static void AChildOfCastType0RunsItsHooksWithoutAMissile()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = match.Hero;
        // Starcall is CastType 0 and a self-area cast: as a child in extra slot 0 it hits the units around the caster, with no missile.
        TestAssert.Equal(0, BbChampionModule.CastType(GameData.Default.Spell("Starcall", 5)), "Starcall is CastType 0");
        probe.Run(null, SetSpell("Starcall", 0, slotType: 2));
        var casts = match.Log.Casts.Count;
        var health = match.Foe.Health;
        probe.Run(null, Child(0, null));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "the child is announced once");
        var cast = match.Log.Casts[^1];
        TestAssert.Equal(SpellSystem.ProtocolHash("Starcall"), cast.SpellHash, "with the child's hash");
        TestAssert.Equal((byte)42, cast.Slot, "in extra slot 0 (stored 42)");
        TestAssert.Equal(0u, cast.MissileNetId, "and no missile: the client builds an obj_SpellMissile only for CastType 1, 2 and 3");
        TestAssert.Equal(1, cast.Rank, "at the forced level");
        match.Advance(1.0);
        TestAssert.True(match.Foe.Health < health, "the child's TargetExecute hit the enemy in range");
        TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-9, "a child has no cooldown of its own");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }

    [Check("scripting.bb-spell-cast-child-of-cast-type-1-flies-to-its-target")]
    public static void AChildOfCastType1FliesAndRunsItsTargetExecuteOnArrival()
    {
        using var probe = new Probe("Taric");
        var match = probe.Match;
        TestAssert.Equal(1, BbChampionModule.CastType(GameData.Default.Spell("Dazzle", 5)), "Dazzle is CastType 1");
        probe.Run(null, SetSpell("Dazzle", 0, slotType: 2));
        var casts = match.Log.Casts.Count;
        var health = match.Foe.Health;
        probe.Run(match.Foe, Child(0, "Target"));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "the child is announced once");
        var cast = match.Log.Casts[^1];
        TestAssert.Equal(SpellSystem.ProtocolHash("Dazzle"), cast.SpellHash, "with the child's hash");
        TestAssert.Equal((byte)42, cast.Slot, "in extra slot 0");
        TestAssert.True(cast.MissileNetId != 0u, "with a missile id");
        TestAssert.Equal([match.Foe.NetId], cast.Targets.Select(target => target.NetId).ToArray(), "naming the unit it flies to");
        TestAssert.Equal(health, match.Foe.Health, "nothing lands before the missile arrives");
        match.Advance(2.0);
        TestAssert.True(match.Foe.Health < health, "the child's TargetExecute damaged the target on arrival");
        TestAssert.True(match.Log.Buffs.Any(line => line.Contains(" add Foe Stun", StringComparison.Ordinal)), "and stunned it");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }
}
