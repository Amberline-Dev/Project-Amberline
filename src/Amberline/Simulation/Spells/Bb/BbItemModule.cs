using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The items' script owner (batch 144 step 1): every shipped <c>DATA/Items/&lt;id&gt;.luaobj</c> the 1.0.0.126 client
/// offers runs from its own code, bound to one inventory copy of one hero through <see cref="BbLoadoutOwner.SetItem"/>
/// the way a talent is bound to its rank. The client treats an item script as a buff script (its
/// <c>BuffHashMap</c> indexes <c>Data\Items\*.lua</c> beside <c>Data\Spells\*.lua</c>, and
/// <c>OnServerBuyItem</c> / <c>OnItemRemoved</c> are the server's purchase and sale hooks), so the copy's
/// buff starts when it is bought and its activation runs once per copy.
/// <para>
/// Like <see cref="BbSummonerModule"/> this is not a <see cref="BbChampionModule"/>: every hero of every champion buys
/// items, the ten hand-written ones included, and no item script names a champion. The module owns its own
/// <see cref="BbSpellHost"/>, <see cref="BbHookRouter"/> and <see cref="BbBuffRuntime"/> so its per-copy tables and the
/// buffs its scripts add cannot collide with a champion module's slots 0..3.
/// </para>
/// <para>
/// Batch 144 step 1 converts the purchase group and batch 145 step 2 the aura group. Which ids those are is
/// <see cref="IsConverted"/>; an item outside that list keeps the hand-written path
/// (<c>ItemPassiveService</c>, <c>HeroStats</c>) and is never bound here, so nothing is applied twice while the groups
/// are converted one at a time.
/// </para>
/// <para>
/// Batch 145 step 2 also binds the copy a clone family member wears. <c>Pet.lua</c> copies the owner's inventory onto
/// Shaco's clone, Yorick's revenant and the <c>ClonePet</c> kinds (LeBlanc's image, Wukong's decoy, Mordekaiser's
/// ghost), and the aura scripts' pet branch — <c>BBIf Owner CO_IS_TYPE_HERO</c> … <c>BBElse</c>, then
/// <c>BBGetPetOwner</c> and the friend buff that skips the owner — is written for exactly that holder. The engine's
/// loadout owns heroes only (<see cref="BbLoadoutHooks.OwnerFor"/>), so this module keeps its own owner per clone.
/// </para>
/// </summary>
public sealed class BbItemModule
{
    /// <summary>
    /// The purchase group (batch 144 step 1), the aura group (batch 145 step 2), the on-hit groups
    /// (batch 147 step 3) and the groups of batch 149: the items whose shipped script is nothing but the stat
    /// or the named stat buff it keeps alive from <c>UpdateSelfBuffStats</c> / <c>UpdateSelfBuffActions</c> /
    /// <c>OnActivate</c>, the periodic aura or active window those same hooks re-add, or a callback the native
    /// already raises (<c>ItemOnHitUnit</c>, <c>ItemOnBeingHit</c>, <c>ItemOnSpellCast</c>, the kill/death pair).
    /// What an item's use does (<c>SelfExecute</c> / <c>TargetExecute</c>) is a cast and runs from its own shipped
    /// <c>Spells/&lt;SpellName&gt;.luaobj</c> through <see cref="CastItemSpell"/> / <see cref="CastChild"/> (batch 149
    /// step 4); <see cref="IsConverted"/> is only about the <c>Items/&lt;id&gt;.luaobj</c> copy and says nothing about
    /// whether the use is script work.
    /// Batch 149 step 5 takes what is left: the window periodics of <c>UpdateSelfBuffActions</c> (and of
    /// <c>UpdateSelfBuffStats</c> where the shipped script puts the clock there) and the twelve 4xxx records whose
    /// only hook is the copy's own <c>BuffOnUpdateStats</c> (<see cref="StatHooks"/>). Nothing is hand-written for
    /// an item any more except what no script decides: inventory, the used slot's cooldown, the packets and the
    /// Reconstruction capacity preflight (<c>ItemService.CapacityPreflightBuff</c>; retail's outcome on a full table is not recovered). 3066 Innervating Locket, the one item
    /// script with <c>BBDefUpdateAura</c>, is not in this build: removed in patch 1.0.0.107 (14 December 2010), only its
    /// leftover script ships (no <c>Items/3066.inibin</c>), so it is neither converted nor hand-written.
    /// </summary>
    private static readonly HashSet<int> Converted = new()
    {
        1062, 1063, 3005, 3020, 3024, 3031, 3035, 3067, 3089, 3101, 3108, 3115, 3134, 3135, 3136, 3145, 3158, 3165,
        // Batch 145 step 2, the aura group: 3001 Abyssal Scepter, 3037 Mana Manipulator, 3050 Stark's Fervor,
        // 3097 Emblem of Valor, 3099 Soul Shroud, 3105 Aegis of the Legion, 3110 Frozen Heart, 3152 Will of the
        // Ancients. (3066 Innervating Locket is not in this build; see the summary above.)
        3001, 3037, 3050, 3097, 3099, 3105, 3110, 3152,
        // Batch 147 step 3, the on-hit group: the single <c>ItemOnHitUnit</c> of 3071 The Black Cleaver
        // (BlackCleaver with ArmorReduction), 3077 Tiamat (the splash), 3123 (Mourning) and 3185 (OdinLightbringer),
        // and the single <c>ItemOnBeingHit</c> of 3082 Warden's Mail (Slow then Cripple). That callback is all this
        // group converts: 3123's use is still the hand-written GrievousWound active in ItemService, and the shipped
        // Spells/ExecutionersCalling.luaobj it should come from is a buff script that waits with the item-use group.
        3071, 3077, 3082, 3123, 3185,
        // Batch 147 step 3b, the item-slow group: 3022 Frozen Mallet, 3044 Phage and 3184 Entropic
        // Claymore. Their ItemOnHitUnit is the shipped slow pair: a self-bound Internal_30Slow /
        // Internal_40Slow row (empty Spells/Internal_*Slow.luaobj, the name carries the magnitude) plus
        // the owner-bound ItemSlow marker, whose Spells/ItemSlow.luaobj BuffOnUpdateStats is a
        // BBIfHasBuff cascade over those row names that applies IncPercentMultiplicativeMovementSpeedMod
        // (-0.5 … -0.15) to the marker's own holder. 3022 picks 30 for a ranged owner (or one under
        // JudicatorRighteousFury) and 40 for a melee one, 2.0 s; 3044 and 3184 roll 25 % for 30, 2.5 s.
        // 3184's active window is dispatched too now (batch 149 steps 2 and 4): the add is
        // Spells/OdinEntropicClaymore.luaobj's own SelfExecute, so that script's BuffOnHitUnit —
        // EntropyBurning and the same slow pair on every hit of a hero owner against AI that is not a
        // turret — runs from the shipped script and the hand-written window in ItemPassiveService is
        // gone.
        3022, 3044, 3184,
        // Batch 147 step 3c, the spell-cast group: 3057 Sheen, 3078 Trinity Force, 3100 Lich Bane and
        // 3124 Guinsoo's Rageblade. Their ItemOnSpellCast arms the spellblade pair — NextBuffVars carry the
        // BaseDamage / AbilityPower the cast captures, the buff it adds (Spells/Sheen.luaobj for 3057 and 3078,
        // Spells/LichBane.luaobj for 3100) is the one whose BuffOnHitUnit spends it, and Spells/SheenDelay.luaobj
        // is the internal row that holds the proc off. 3057 writes IsSheen=true (x1.0 base AD, a 1.2 s row),
        // 3078 IsSheen=false (x1.5, 1.3 s), 3100 adds flat AbilityPower instead. 3124's ItemOnSpellCast,
        // ItemOnHitUnit, ItemOnBeingDodged and ItemOnMiss all add one stack of the same Rageblade buff
        // (Spells/Rageblade.luaobj: ±0.04 attack speed and ±6 AP per stack), on top of the ±0.04 / ±7 its own
        // OnBuffActivate / OnDeactivate keep for the copy.
        3057, 3078, 3100, 3124,
        // Batch 147 step 4, the damage group: 3116 Rylai's Crystal Scepter, 3117 Boots of Mobility, 3155
        // Hexdrinker and 3187 Hextech Sweeper. Their callbacks are the damage stages of the copy itself
        // (ItemOnPreDamage of the copy that is hit, ItemOnPreDealDamage and ItemOnDealDamage of the one that
        // hits) plus 3117's own BuffOnAllowAdd. 3187's UpdateSelfBuffActions adds its HextechSweeper self
        // buff every nine seconds; its active is Spells/HextechSweeper.luaobj's own SelfExecute and
        // TargetExecute (batch 149 step 4).
        3116, 3117, 3155, 3187,
        // Batch 149 step 1, the kill/death group: the snowball trio and its two finishers plus the cape. 3041
        // Mejai's Soulstealer, 3138 Leviathan and 3141 the Sword of the Occult keep their own stats buff
        // (MejaisStats / LeviathanStats / MuramasaStats) under a check buff UpdateSelfBuffActions re-adds every
        // second, cap themselves at twenty stacks with a third buff, and trim the stacks on ItemOnDeath. 3072
        // The Bloodthirster and 3083 Warmog's Armor grow their InstanceVars counters from ItemOnKill (3083
        // also ItemOnAssist, hero kills only) and read them back in UpdateSelfBuffStats. 3068 Sunfire Cape is
        // UpdateSelfBuffActions renewing the SunfireCloakParticle self buff that owns the whole aura — the
        // 400-range periodic damage and the SunfireCapeAura marker of Spells/SunfireCloakParticle.luaobj —
        // and ItemOnDeath taking it away.
        3041, 3068, 3072, 3083, 3138, 3141,
        // Batch 149 step 2, the active-window group: 3065 Spirit Visage, 3069 Shurelya's Crest, 3128 Deathfire
        // Grasp, 3142 Youmuu's Ghostblade and 3143 Randuin's Omen. Each keeps its named internal buff alive from
        // UpdateSelfBuffActions (a nine-second BBExecutePeriodically re-adding a ten-second window: SpiritVisage,
        // ShurelyasCrest, DeathfireGrasp, YoumusBlade, RanduinsOmen), and 3065 also opens that window from
        // OnActivate when the owner is not already wearing it. The window buffs carry the whole item: SpiritVisage
        // owns its cooldown, HP regeneration and the life-steal/spell-vamp share (BuffOnUpdateStats) plus the
        // 1.15 heal of BuffOnHeal; the other four take their cooldown (and Youmuu's +20 armor penetration) from
        // OnBuffActivate's BBIncPermanentStat and give it back in OnBuffDeactivate; RanduinsOmen's BuffOnBeingHit
        // is the retaliation pair. The windows are what this step converts — the actives (SelfExecute / the
        // DeathfireGrasp TargetExecute) and the SpectralFury melee extension stayed hand-written with the
        // item-use group and are the shipped scripts' own now (batch 149 step 4).
        //
        // Batch 149 step 4, the same window shape: 3180 Odyn's Veil. Items/3180.luaobj re-adds its
        // Spells/OdynsVeil buff every nine seconds (BUFF_RENEW_EXISTING, StacksExclusive, ten seconds,
        // CanMitigateDuration false), and that buff is the whole passive — OnBuffActivate zeroes
        // CharVars.StoredDamage and BuffOnPreDamage takes its tenth off every magic hit, adds what it
        // prevented to that counter and caps it at 200. The active's SelfExecute detonates for
        // 200 + CharVars.StoredDamage and zeroes the counter again, so it is meaningless without the
        // window that fills it: the copy goes with the cast (the hand-written 10 % reduction in
        // CombatService and Entity.OdynStoredDamage were its twin and went with it).
        3065, 3069, 3128, 3142, 3143, 3180,
        // Batch 149 step 5, the window periodics: every remaining script that keeps one named buff alive from a
        // BBExecutePeriodically inside UpdateSelfBuffActions (or inside UpdateSelfBuffStats — 3028 Chalice of
        // Harmony, 3132 Heart of Gold and 3181 the Odin blood item write their clock there) and lets that buff
        // carry the whole passive. The Tear trio (3003 Archangel's Staff, 3004 Manamune, 3070 the Tear itself)
        // keep TearOfTheGoddessTrack — CharVars.TearBonusMana and its two-charge CooldownResevoir — and 3004 the
        // Manamune pair beside it; 3010 Catalyst and 3027 Rod of Ages add CatalystHeal on a level-up and 3027
        // grows its three bonuses from a sixty-second clock; 3026 Guardian Angel keeps WillRevive, whose
        // BuffOnPreDamage is the revive; 3102 Banshee's Veil keeps BansheesVeil, whose BuffOnBeingSpellHit /
        // BuffOnAllowAdd / BuffOnPreDamage / BuffOnUpdateStats are the spell shield; 3109 Force of Nature,
        // 3183 Prilisa's Blessing and the tenacity pair (3111 Mercury Treads, 3170 / 3172 / 3173 and their
        // Hardening window) are the same shape. What the on-hit items kept hand-written in batch 147 is here
        // too: 3075 Thornmail, 3091 Wit's End, 3106 Madred's Razors, 3114 Malady, 3126 Madred's Bloodrazor,
        // 3131 Sword of the Divine, 3154 Wriggle's Lantern, 3178 Ionic Spark, 3186 the Odin bloodrazor and 3181
        // bloodburster — their window buff's BuffOnHitUnit / BuffOnBeingHit is the proc.
        3003, 3004, 3010, 3026, 3027, 3028, 3070, 3075, 3091, 3093, 3096, 3098,
        3102, 3106, 3109, 3111, 3114, 3126, 3131, 3132, 3154, 3170, 3172, 3173,
        3178, 3181, 3183, 3186,
        // Batch 149 step 5, the 4xxx records: the twelve masterwork stat rows the client hands out as buffs, one
        // BBIncStat each from the copy's own BuffOnUpdateStats (4108 is the cooldown one). 4008 is the only one
        // with a body: it reads GetHealthPercent and, at half health or below, adds one HP regeneration and ten
        // armor.
        4008, 4108, 4109, 4129, 4201, 4202, 4228, 4230, 4233, 4236, 4240, 4241,
    };

    /// <summary>Whether <paramref name="itemId"/> runs from its shipped script instead of the hand-written item code.</summary>
    public static bool IsConverted(int itemId) => Converted.Contains(itemId);

    private readonly BbScriptSet _scripts;
    private readonly GameData _data;
    private BbSpellHost? _host;
    private BbHookRouter? _router;
    private BbLoadoutHooks? _loadout;
    private readonly Dictionary<Entity, int[]> _boundSlots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, string> _statSignatures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, BbLoadoutOwner> _petOwners = new(ReferenceEqualityComparer.Instance);
    // The child lines a ward placement cast (BbLineMissile); cancelled with the hero that cast them.
    private readonly List<BbLineMissile> _lines = new();
    // The child homing flights an item's active casts (BbSpellProjectile: DeathfireGraspSpell, HextechGunbladeSpell,
    // ShurelyasSpell); cancelled the same way.
    private readonly List<BbSpellProjectile> _flights = new();

    public BbItemModule(GameData? data = null)
    {
        _data = data ?? GameData.Default;
        _scripts = new BbScriptSet(ClosureOf(_data.Paths), _data.Paths);
    }

    /// <summary>
    /// The item closure: one script per shipped <c>Items/&lt;id&gt;.luaobj</c>, named after its file stem, which is the
    /// item id. The buffs those scripts name (<c>GlacialShroud</c>, <c>ZhonyasRing</c>, <c>DoranT2Health</c> ...) are not
    /// closure members: <see cref="BbBuffRuntime"/> resolves them from the shipped <c>Spells/&lt;name&gt;.luaobj</c> the
    /// first time one is added, which is the client's own layout (one BuffHashMap over both directories).
    /// </summary>
    private static BbClosure ClosureOf(GameDataPaths paths)
    {
        var directory = paths.Data("Items");
        var scripts = new List<BbClosureScript>();
        var diagnostics = new List<BbDiagnostic>();
        if (Directory.Exists(directory))
        {
            foreach (var file in CaseInsensitivePaths.EnumerateFiles(directory, "*.luaobj")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var loaded = BbScriptLoader.Load(paths, $"Items/{name}.luaobj");
                diagnostics.AddRange(loaded.Diagnostics);
                if (loaded.Script is { } script)
                    scripts.Add(new BbClosureScript(name, $"Items/{name}.luaobj", BbScriptRole.Spell, "Items", script));
            }
        }
        return new BbClosure("Items", scripts, Array.Empty<BbDanglingReference>(),
            Array.Empty<string>(), diagnostics);
    }

    /// <summary>Every item script the shipped data holds, by item id.</summary>
    public IReadOnlyList<string> ScriptNames => _scripts.Closure.ScriptNames.ToArray();

    /// <summary>Binds the module to a match. Call once per match.</summary>
    public IBbHookSink Bind(SpellSystem system, BbLoadoutHooks loadout, Action<BbScriptFailure>? failureSink = null)
    {
        if (_host is not null) throw new InvalidOperationException("the module is already bound to a match");
        _loadout = loadout;
        var host = new BbSpellHost(system, ItemName);
        var router = new BbHookRouter(host, _scripts);
        host.Router = router;
        router.FailureSink = failureSink;
        router.AvatarVarsOf = hero => loadout.AvatarVars(hero) ?? loadout.OwnerFor(hero)?.AvatarVars ?? new BbTable();
        router.ItemCopyAllowAdd = AllowBuffAdd;
        // A ward placement casts its ItemPlacementMissile child from SelfExecute (batch 149 step 3), and an active that
        // names its sub-spell with BBSetSpell + BBSpellCast its homing child (batch 149 step 4).
        host.ChildCaster = CastChild;
        host.Buffs = new BbBuffRuntime(host, _scripts, router);
        _host = host;
        _router = router;
        return router;
    }

    public bool IsBound => _host is not null;

    /// <summary>The script buff instances this module's copies added (the same handle the champion modules keep).</summary>
    internal BbBuffRuntime? Buffs => _host?.Buffs;

    /// <summary>Hook runs that raised and were skipped (production safety); empty in a healthy match.</summary>
    public IReadOnlyList<BbScriptFailure> ScriptFailures => _router?.Failures ?? Array.Empty<BbScriptFailure>();

    private BbHookRouter RouterOrThrow => _router ?? throw new InvalidOperationException("BbItemModule.Bind was not called");

    /// <summary>The shipped-script name of the copy in an inventory slot (its item id), or "" when it has none.</summary>
    public string ItemName(Entity hero, int slot) =>
        (uint)slot < (uint)hero.Inventory.Count && hero.Inventory[slot] is { } held && ScriptFor(held.ItemId) is { } script
            ? script.Name : "";

    /// <summary>
    /// The script of one item: only a converted id that has a shipped <c>Items/&lt;id&gt;.luaobj</c>. Anything else is
    /// not this module's business and keeps the hand-written path.
    /// </summary>
    private BbScript? ScriptFor(int itemId) =>
        IsConverted(itemId) ? _scripts.Find(itemId.ToString(CultureInfo.InvariantCulture)) : null;

    // ------------------------------------------------------------------------------------------------------------
    // The (hero, inventory copy) binding
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the unit is one of the clone families <c>Pet.lua</c> copies its owner's inventory onto: Shaco's clone,
    /// Yorick's revenant, and the <c>ClonePet</c> kind (LeBlanc's image, Wukong's decoy, Mordekaiser's ghost). They are
    /// <c>UnitType.Monster</c> units of those kinds — the same set the hand-written aura sync took as sources.
    /// </summary>
    public static bool IsPetHolder(Entity unit) =>
        unit.Kind is EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet;

    /// <summary>
    /// The script owner of one unit's inventory: the hero's loadout, or a private owner for a clone family member
    /// (<see cref="IsPetHolder"/>) — <see cref="BbLoadoutHooks.OwnerFor"/> answers only for heroes, and the aura
    /// scripts' pet branch is written for the clone's own copy of the item. A unit that is neither is not this module's
    /// business and binds nothing.
    /// </summary>
    private BbLoadoutOwner? OwnerOf(Entity unit)
    {
        if (_loadout?.OwnerFor(unit) is { } owner) return owner;
        if (unit.UnitType == UnitType.Hero || !IsPetHolder(unit)) return null;
        if (!_petOwners.TryGetValue(unit, out var pet)) _petOwners[unit] = pet = new BbLoadoutOwner();
        return pet;
    }

    /// <summary>
    /// The same owner, for the callers that need to reach one copy's <c>InstanceVars</c> (the growth counters
    /// Items/3072 and 3083's <c>ItemOnKill</c> writes). The inventory is the public face of the same thing; this is
    /// the script side of it.
    /// </summary>
    public BbLoadoutOwner? LoadoutOf(Entity unit) => OwnerOf(unit);

    /// <summary>
    /// The unit's own <c>CharVars</c> in this module's hook router, the table every hook of a copy bound to
    /// <paramref name="unit"/> runs with (<see cref="BbChampionModule.CharVars"/>'s item twin). Batch 149 step 4's
    /// counters live there because the shipped scripts put them there: Odyn's Veil keeps the damage its window
    /// prevented in <c>StoredDamage</c> and Youmuu's Ghostblade its melee extensions in <c>SpectralCount</c>.
    /// </summary>
    public BbTable CharVars(Entity unit) => RouterOrThrow.CharVars(unit);

    // ------------------------------------------------------------------------------------------------------------
    // The active slot cast (batch 149 steps 3 and 4)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The shipped <c>Spells/&lt;name&gt;.luaobj</c> of an item's active, resolved the way the buff runtime resolves a
    /// buff it names (the closure holds the <c>Items/</c> scripts; the cast scripts live under <c>Spells/</c>).
    /// </summary>
    private BbScript? ActiveScript(string spellName) =>
        spellName.Length == 0 ? null : _scripts.FindOrShipped(spellName);

    /// <summary>
    /// The script's own gate of an item's active (<c>CanCast</c>): <c>RegenerationPotion</c> refuses above 99 % health
    /// and <c>FlaskOfCrystalWater</c> above 99 % mana, both through <c>OwnerVar=Target</c> — a cast hook runs with no
    /// target of its own, so <see cref="BbSpellHost.UnitOrSelf"/> answers the casting hero. A script that has none, or
    /// that stored no <c>false</c>, allows the cast. The engine keeps the client's own answer string
    /// (<c>full_health</c> / <c>full_mana</c>): the script is the gate, the string is the engine's public vocabulary
    /// and no shipped file names it.
    /// </summary>
    public bool CanCastItemSpell(Entity hero, string spellName, int inventorySlot) =>
        ActiveScript(spellName) is not { } script || RouterOrThrow.RunCanCast(script, hero, inventorySlot, 1, 0.0);

    /// <summary>
    /// The active slot cast of one item: its <c>SelfExecute</c> and/or its <c>TargetExecute</c> (the champion order
    /// when a script defines both). The potions' and elixirs' add to the drinking hero, the three ward scripts'
    /// placement, and the whole active group of batch 149 step 4 — Zhonya's Hourglass, Deathfire Grasp, Hextech
    /// Gunblade, Bilgewater Cutlass, Executioner's Calling, Randuin's Omen, Shurelya's Crest, Quicksilver Sash,
    /// Youmuu's Ghostblade, Lightstriker, Prilisa's Blessing, Odyn's Veil, Entropic Claymore and Hextech Sweeper.
    /// There is no inventory copy binding for these ids, so the cast runs with no <c>BbLoadoutBinding</c>: what the
    /// scripts read is <c>CharVars</c> (the potion counters and Youmuu's SpectralCount) and each buff's own
    /// <c>InstanceVars</c>. <paramref name="inventorySlot"/> is the spell-slot key of the cast's own
    /// <c>SpellVars</c> (the slot the item sits in), and <paramref name="itemId"/> the item the use belongs to —
    /// the recap identity of the damage this cast and its chains deal (<see cref="BbHookFrame.ItemId"/>), which the
    /// inventory slot cannot give (an elixir bought in the shop never reaches a slot) and which an active used from
    /// slot 0..3 would otherwise take for a champion spell.
    /// </summary>
    public void CastItemSpell(Entity hero, string spellName, int inventorySlot, Position3 position, Position3 end,
        Entity? target, int itemId = 0)
    {
        if (ActiveScript(spellName) is not { } script) return;
        var at = target ?? hero;
        if (script.Event("SelfExecute") is not null)
            RouterOrThrow.RunSelfExecute(script, hero, inventorySlot, 1, 0.0, position, end, at, itemActive: true, itemId: itemId);
        if (script.Event("TargetExecute") is not null)
            RouterOrThrow.RunTargetExecute(script, hero, inventorySlot, 1, 0.0, position, end, at, itemActive: true, itemId: itemId);
    }

    /// <summary>
    /// Whether the cast of one item's active breaks the caster's stealth (batch 149 step 4). The rule is the one the
    /// stealth buffs' own <c>BuffOnSpellCast</c> applies to a champion cast (batch 164 retired <c>SpellSystem</c>'s
    /// hand-written <c>BreakCastStealth</c>): the cast script's <c>CastingBreaksStealth</c> global — true removes the
    /// stealth, false keeps it, and an unset flag removes it unless <c>DoesntTriggerSpellCasts</c> is true.
    /// <para>
    /// Batch 164 keeps this reader rather than raising <c>RunSpellCast</c> for an item active: the engine's cast-hook
    /// boundary is a champion-slot cast (<c>MatchGameplayHooks.OnSpellCast</c> / <c>BbItemModule.OnSpellCast</c>), an
    /// item's own active raises <c>ItemOnSpellCast</c> of the copies and no <c>BuffOnSpellCast</c>, so the stealth
    /// buffs' hooks never see it. The rule is still the shipped script's own values; only the runner is hand-written.
    /// </para>
    /// <para>
    /// Of the shipped actives Zhonya's Hourglass, Randuin's Omen, Shurelya's Crest, Prilisa's Blessing, Deathfire
    /// Grasp, Bilgewater Cutlass, Hextech Gunblade, Mourning and Hextech Sweeper say true; Quicksilver Sash,
    /// Lightstriker and Wriggle's Lantern say false; Youmuu's Ghostblade leaves it unset beside
    /// <c>DoesntTriggerSpellCasts</c> and keeps the stealth, while Odyn's Veil and Entropic Claymore set neither and
    /// therefore break it. That is exactly the hand-written spell-name list <c>ItemService.SendCast</c> carried, which
    /// is gone; a name with no shipped script is treated as an unset flag on a cast that does trigger casts (it breaks).
    /// </para>
    /// </summary>
    public bool CastingBreaksStealth(string spellName)
    {
        if (ActiveScript(spellName) is not { } script) return true;
        if (script.Global("CastingBreaksStealth") is BbValue.Bool flag) return flag.Value;
        return script.Global("DoesntTriggerSpellCasts") is not BbValue.Bool { Value: true };
    }

    /// <summary>
    /// <c>BBSpellCast</c> of an extra-slot child an item's own cast made (batch 149 step 3/4). The three shapes the
    /// item spells use: <c>CastType</c> 3 a line (WriggleLantern's ward placement casts <c>ItemPlacementMissile</c>
    /// from extra slot 7), 1 a missile that homes on <c>TargetVar</c> and runs the child's <c>TargetExecute</c> on
    /// arrival (<c>DeathfireGraspSpell</c>, <c>HextechGunbladeSpell</c> and <c>ShurelyasSpell</c>, each cast by its
    /// own item's <c>BBSetSpell</c> + <c>BBSpellCast</c> of extra slot 7 with <c>FireWithoutCasting</c>: the missile
    /// leaves at once and flies at the child's own <c>MissileSpeed</c>, 1600), and 0 no missile at all. The same
    /// switch as a champion child (<c>BbChampionModule.CastChild</c>); a chain (2) belongs to champion kits and
    /// raises, which loses that one hook run and is reported.
    /// <para>
    /// <c>RunChildSpellCast</c> is not raised: the container scripts say <c>DoesntTriggerSpellCasts = true</c>, and
    /// running it would let an active's sub-spell proc Sheen's spellblade pair. A child's own cast is announced like a
    /// champion child's (its own cast in slot 42 + n with the child's hash, a missile id and its designer times),
    /// except that a homing one arrives as its missile packet and is retired with a destroy packet (see
    /// <see cref="SendHomingMissile"/>).
    /// </para>
    /// </summary>
    private void CastChild(BbChildCast child)
    {
        var host = _host ?? throw new InvalidOperationException("BbItemModule.Bind was not called");
        var system = host.Spells;
        var caster = child.Caster;
        var name = host.ChildSpellName(caster, child.SlotType, child.ExtraIndex)
            ?? throw new BbRuntimeException($"{caster.Name} has no extra spell {child.ExtraIndex + 1}");
        var script = ActiveScript(name) ?? throw new BbRuntimeException($"{name} has no shipped script");
        var data = _data.Spell(name, 5);
        var castType = BbChampionModule.CastType(data);
        var rank = child.Level ?? 1;
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(data));
        if (castType != 3 && castType is not (0 or 1))
            throw BbSpellHost.Unbound($"an item child cast of CastType {castType} ({name})", "child casts");
        var launch = Ground(system, child.CastPosition ?? caster.Position);
        // The child's own cast time, or none when the call fires without casting (every item child does).
        var wait = child.FireWithoutCasting ? 0.0 : data.CastDelay;
        var context = new ChampionCastContext(data, rank, 0.0, 0.0);
        SpellCastInfo Announce(uint missileId, Position3 at, Position3 end, IReadOnlyList<SpellCastTarget> targets) =>
            new(SpellSystem.ProtocolHash(name), caster.NetId, rank, caster.NetId, missileId, at, end, targets, 0.0f, 0.0f,
                checked((byte)child.Slot), launch, 1.0f, (float)data.CastDelay, 0.0f, (float)data.TotalTime, 0.0f,
                // The native mShouldOverrideCastPosition bit (0x08) when the child leaves from a point other than its caster: without it the
                // client snaps the caster to the packet's launch point (>400 units away) and starts the missile at the caster's joint. See
                // BbChampionModule.OverrideCastPositionFlag for the native evidence.
                Flags: child.OverrideCastPosition ? BbChampionModule.OverrideCastPositionFlag : (byte)0);
        if (castType == 3)
        {
            var missileId = system.World.Allocate(UnitType.Missile);
            var at = Ground(system, child.Position);
            var info = Announce(missileId, at, Ground(system, child.End), Array.Empty<SpellCastTarget>());
            if (system.Effects.IsReferenceReady(caster)) system.Effects.SendCast(caster, info);
            var line = new BbLineMissile(Forget, host, script, name, caster, child.Frame.SpellSlot, rank, info,
                wait, child.End, data, flags, launchFrom: child.CastPosition, itemId: child.Frame.ItemId);
            _lines.Add(line);
            system.EnqueueSharedProjectile(line);
            return;
        }
        if (castType == 1)
        {
            // A homing child needs its unit; without one there is nothing to fly to (the client's missile would have no
            // target either). The item actives cast it with TargetVar of the use, so this is a script error if it is gone.
            if (child.Target is not { Alive: true } target)
                throw new BbRuntimeException($"{name} is CastType 1 and its target is gone");
            var missileId = system.World.Allocate(UnitType.Missile);
            var at = Ground(system, target.Position);
            var info = Announce(missileId, at, at, [new SpellCastTarget(target.NetId, at, 0)]);
            // The sub-missile goes out as its missile packet and is retired with a destroy packet: the client never
            // cast this spell, so it has no cast of its own to build the object from (Reconstruction: the hand-written
            // TargetedItemMissiles of the Deathfire Grasp and Hextech Gunblade actives sent exactly this pair, and the
            // item checks pin it). A champion child of the same BBSpellCast announces its own cast answer instead and
            // its client retires that missile on impact.
            SendHomingMissile(system, caster, info, launch, at, data.MissileSpeed);
            var flight = new BbSpellProjectile(Forget, host, script, caster, child.Frame.SpellSlot, rank, context, info,
                target.NetId, missileId, targetFlags: flags,
                atWindupEnd: () => host.Router.RunSelfExecute(script, caster, child.Frame.SpellSlot, rank, 0.0, at, at, target,
                    itemId: child.Frame.ItemId),
                castTime: wait, child: true, launchFrom: child.CastPosition, announcedByMissilePacket: true,
                itemId: child.Frame.ItemId);
            _flights.Add(flight);
            system.EnqueueSharedProjectile(flight);
            return;
        }

        // CastType 0: no missile; the effect lands when the child's cast time has passed (at once here).
        var type = BbChampionModule.Targeting(data);
        var unitTarget = type is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_TargetOrLocation && child.Target is { Alive: true } chosen
            ? chosen : null;
        var aimPoint = unitTarget is not null ? Ground(system, unitTarget.Position) : Ground(system, child.Position);
        var landing = Announce(0, aimPoint, unitTarget is not null ? aimPoint : Ground(system, child.End),
            unitTarget is not null ? [new SpellCastTarget(unitTarget.NetId, aimPoint, 0)] : Array.Empty<SpellCastTarget>());
        if (system.Effects.IsReferenceReady(caster)) system.Effects.SendCast(caster, landing);
        IReadOnlyList<Entity> Victims(SpellSystem sys) => type switch
        {
            BbTargetType.TTYPE_Self => [caster],
            BbTargetType.TTYPE_Target => unitTarget is { Alive: true } ? [unitTarget] : Array.Empty<Entity>(),
            BbTargetType.TTYPE_SelfAOE => BbSpellTargeting.Area(sys, caster, caster.Position, data.CastRadius, flags),
            BbTargetType.TTYPE_Area => BbSpellTargeting.Area(sys, caster, child.Position, data.CastRadius, flags),
            _ => Array.Empty<Entity>(),
        };
        void Land(SpellSystem sys)
        {
            host.Router.RunSelfExecute(script, caster, child.Frame.SpellSlot, rank, 0.0, aimPoint, aimPoint, unitTarget,
                itemId: child.Frame.ItemId);
            foreach (var unit in Victims(sys))
                host.Router.RunTargetExecute(script, caster, child.Frame.SpellSlot, rank, 0.0, aimPoint, aimPoint, unit,
                    itemId: child.Frame.ItemId);
        }
        if (wait <= 1e-9)
        {
            Land(system);
            return;
        }
        var windup = new BbSpellProjectile(Forget, host, script, caster, child.Frame.SpellSlot, rank, context, landing, 0, 0,
            caster.Position, default, aimPoint, windupTargets: sys => Victims(sys),
            atWindupEnd: () => host.Router.RunSelfExecute(script, caster, child.Frame.SpellSlot, rank, 0.0, aimPoint, aimPoint, unitTarget,
                itemId: child.Frame.ItemId),
            castTime: wait, child: true, itemId: child.Frame.ItemId);
        _flights.Add(windup);
        system.EnqueueSharedProjectile(windup);
    }

    internal void Forget(BbSpellProjectile flight) => _flights.Remove(flight);

    /// <summary>The point the projectile layer aims at, on the navigation grid.</summary>
    private static Position3 Ground(SpellSystem system, Position2 point) =>
        new(point.X, system.Movement.Navigation.HeightAt(point), point.Z);

    /// <summary>
    /// The missile packet of a homing item child: the object leaves the launcher at once and travels at the child's
    /// own <c>MissileSpeed</c> (the client takes the rest from the cast info the packet carries).
    /// </summary>
    private static void SendHomingMissile(SpellSystem system, Entity caster, SpellCastInfo info, Position3 from,
        Position3 to, double speed)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        var direction = length > 1e-9 ? new Position3(dx / length, 0.0, dz / length) : new Position3(0.0, 0.0, 1.0);
        system.Effects.SendMissile(caster, info, from, direction,
            new Position3(direction.X * speed, 0.0, direction.Z * speed), from, to);
    }

    internal void Forget(BbLineMissile line) => _lines.Remove(line);

    /// <summary>
    /// Brings the hero's bindings in line with its inventory. A copy that only changed slot keeps its
    /// <c>InstanceVars</c> (<see cref="BbLoadoutOwner.MoveItem"/>); a buy runs the script's own activation and a sale
    /// its deactivation. Called once per item update, before the poll.
    /// </summary>
    public void SyncInventory(Entity hero)
    {
        if (OwnerOf(hero) is not { } owner) return;
        var now = new int[Amberline.Simulation.Inventory.SlotCount];
        for (var slot = 0; slot < now.Length; slot++)
            now[slot] = hero.Inventory[slot] is { } held && ScriptFor(held.ItemId) is not null ? held.ItemId : 0;
        if (!_boundSlots.TryGetValue(hero, out var before))
        {
            _boundSlots[hero] = before = new int[Amberline.Simulation.Inventory.SlotCount];
            for (var slot = 0; slot < before.Length; slot++) before[slot] = owner.Item(slot)?.Id ?? 0;
        }
        if (before.AsSpan().SequenceEqual(now)) return;

        // A move first: the copy keeps its InstanceVars (its periodic clocks and counters travel with it).
        for (var to = 0; to < now.Length; to++)
        {
            if (now[to] == 0 || now[to] == before[to]) continue;
            for (var from = 0; from < now.Length; from++)
            {
                if (from == to || before[from] != now[to]) continue;
                // The source has to be free or to hold exactly what the destination held: anything else is a sale and a
                // buy of two different copies, which both start clean.
                if (now[from] != 0 && now[from] != before[to]) continue;
                // Batch 149 step 1: the tooltip rows a copy wrote are keyed by the spellbook slot it sat in
                // (BBSetSpellToolTipVar, InventorySlots → +4). Both slots lose their writer here — the copy rewrites
                // its row at the slot it moved to on its next UpdateSelfBuffStats, and a slot the move handed to
                // another item is that item's to write — so neither keeps a row for an item that is gone.
                hero.ScriptSpellTooltips.Remove(TooltipSlotOf(from));
                hero.ScriptSpellTooltips.Remove(TooltipSlotOf(to));
                owner.MoveItem(from, to);
                before[to] = now[to];
                before[from] = now[from];
                break;
            }
        }
        for (var slot = 0; slot < now.Length; slot++)
        {
            if (before[slot] == 0 || before[slot] == now[slot]) continue;
            if (owner.Item(slot) is { } leaving)
            {
                Run(hero, leaving, "OnDeactivate");
                Run(hero, leaving, "OnBuffDeactivate");
            }
            owner.RemoveItem(slot);
            // The copy's tooltip rows go with it (see the move above): an emptied slot shows no item tooltip.
            hero.ScriptSpellTooltips.Remove(TooltipSlotOf(slot));
            before[slot] = 0;
        }
        for (var slot = 0; slot < now.Length; slot++)
        {
            if (now[slot] == 0 || before[slot] == now[slot]) continue;
            if (ScriptFor(now[slot]) is not { } script) continue;
            var binding = owner.SetItem(script, now[slot], slot);
            before[slot] = now[slot];
            // The client treats the item script as its own buff script: buying the copy is what activates it.
            Run(hero, binding, "OnActivate");
            Run(hero, binding, "OnBuffActivate");
        }
    }

    /// <summary>
    /// The holder left the game (or a clone died): its copies and their clocks go with it. A hero's owner is the
    /// loadout's own; a clone's is this module's private one.
    /// </summary>
    public void Clear(Entity hero)
    {
        _boundSlots.Remove(hero);
        _statSignatures.Remove(hero);
        if (_petOwners.Remove(hero, out var pet)) pet.Clear();
        else _loadout?.OwnerFor(hero)?.Clear();
    }

    /// <summary>
    /// Batch 204: a respawn makes due at once the periodic re-add of a buff that does not persist through death, on each
    /// item copy. The clock survives the death, and the periodic keeps consuming its due ticks while the holder is a
    /// corpse: Items/3089's re-add of ZhonyasRing is refused on a corpse (BbBuffRuntime.Add), so the buff came back only
    /// at the next due tick after the respawn, 7 s later in the batch 204 probe, and Deathcap's 1.30 was off meanwhile.
    /// The living holder's first poll now runs the body, as the talents' self-buff poll does (BbLoadoutHooks.PollSelfBuffs).
    /// Periodics that re-add a persistent buff keep their clock.
    /// <para>
    /// Reconstruction: the shipped files do not say when a periodic clock starts again after a death. This follows the
    /// talent path, which puts a missing live buff back on the living hero's first poll.
    /// </para>
    /// </summary>
    public void OnRespawn(Entity hero)
    {
        if (OwnerOf(hero) is not { } owner || _host is null) return;
        var now = _host.GetTime();
        foreach (var binding in owner.ActiveBindings)
            foreach (var (hook, block) in binding.Script.Walk())
            {
                if (hook is not ("UpdateSelfBuffActions" or "UpdateSelfBuffStats") || block.Function != "BBExecutePeriodically") continue;
                if (block.Text("TrackTimeVarTable") != "InstanceVars" || block.Text("TrackTimeVar") is not { } key) continue;
                if (!block.SubBlocks.Any(child => child.Function == "BBSpellBuffAdd" && child.Text("BuffName") is { } buff &&
                        _scripts.FindOrShipped(buff)?.Global("PersistsThroughDeath") is not BbValue.Bool { Value: true })) continue;
                // One period back from now: the body is due on the living holder's first poll, and then every period.
                binding.InstanceVars[key] = now - (block.Number("TimeBetweenExecutions") ?? 0.0);
            }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The per-copy poll
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One spell update of one holder's copies, dead or alive (the scripts guard on <c>CO_IS_NOT_DEAD</c> themselves,
    /// and a copy of <c>DoranT2Health</c> must not lapse while its holder is a corpse). <c>UpdateSelfBuffStats</c> is
    /// sampled into <see cref="Entity.ItemScriptStats"/> first, then <c>UpdateSelfBuffActions</c> runs against the stat
    /// the hero holds after that sample — the same order one buff update has.
    /// <para>
    /// A clone's sample is dropped: the hero stat calculator only knows heroes (<c>HeroStatsService.EnsureHero</c>
    /// raises for anything else) and the hand-written inventory totals never reached a clone either. The sample still
    /// has to be taken — 3001 Abyssal Scepter's and 3110 Frozen Heart's aura loops live inside
    /// <c>UpdateSelfBuffStats</c> — so only the merge into the hero is skipped.
    /// </para>
    /// <para>
    /// Batch 246: <paramref name="due"/> is the holder's self-buff line (<see cref="BbSelfUpdate"/>). The inventory sync runs every
    /// update; a bare hook runs on the line, a hook that owns a <c>BBExecutePeriodically</c> every update. The stat sample is one merge
    /// of every copy, so it is taken whole whenever one copy's stat hook owns a periodic (3001's and 3110's aura loops): a bare stat
    /// hook sampled between its lines reads the same state and changes nothing the signature does not already hold.
    /// </para>
    /// </summary>
    public void Poll(Entity hero, bool due = true)
    {
        if (_router is null || _host is null) return;
        SyncInventory(hero);
        if (OwnerOf(hero) is not { } owner) return;
        var sample = due;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount && !sample; slot++)
            sample = owner.Item(slot) is { Active: true } binding && StatHooks.Any(hook => BbSelfUpdate.CarriesPeriodic(binding.Script, hook));
        var merged = new BbStatCollector();
        var any = false;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount && sample; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding) continue;
            any = true;
            if (StatHooks.Any(hook => binding.Script.Event(hook) is not null))
                merged.Merge(SampleStats(hero, binding));
        }
        if (any && hero.UnitType == UnitType.Hero) ApplyItemStats(hero, merged);
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding) continue;
            if (BbSelfUpdate.Runs(binding.Script, "UpdateSelfBuffActions", due)) Run(hero, binding, "UpdateSelfBuffActions");
        }
    }

    /// <summary>
    /// One copy's stat sample: its <c>UpdateSelfBuffStats</c> and then its <c>BuffOnUpdateStats</c>, the two hooks a
    /// copy can write stats through. The client indexes <c>Data\Items\*.lua</c> in the same buff hash map as
    /// <c>Data\Spells\*.lua</c>, so an item script is a buff script and 3004 Manamune (its growth tooltip) and the
    /// twelve 4xxx records write the buff hook while the rest write the self one. Both land in one collector: one
    /// copy is one sample (batch 149 step 5).
    /// <para>
    /// IsResample: the poll is never the first sample of anything, so natives that apply once stay out of it. A hook
    /// that raised leaves a half-filled collector: that copy's sample is dropped whole (production safety, the same
    /// contract as <c>BbHookRouter.RunUpdateSelfBuffStats</c>).
    /// </para>
    /// </summary>
    private BbStatCollector SampleStats(Entity hero, BbLoadoutBinding binding)
    {
        var collector = new BbStatCollector();
        var hook = "UpdateSelfBuffStats";
        try
        {
            foreach (var name in StatHooks)
            {
                if (binding.Script.Event(name) is null) continue;
                hook = name;
                var frame = new BbHookFrame(name, binding.Script.Name, hero)
                {
                    Caster = hero, Stats = collector, IsResample = true, ItemId = binding.Id,
                };
                RouterOrThrow.Run(binding.Script, name, NewScope(hero, binding), frame, rethrow: true);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            RouterOrThrow.Fail(binding.Script.Name, hook, ex);
            return new BbStatCollector();
        }
        return collector;
    }

    /// <summary>The stat hooks of one copy, in the order one sample runs them (see <see cref="SampleStats"/>).</summary>
    private static readonly string[] StatHooks = ["UpdateSelfBuffStats", "BuffOnUpdateStats"];

    /// <summary>
    /// The summed sample of every copy, kept on the hero the way a character script's own is and read by the hero stat
    /// calculator as one more buff payload. A different result refreshes the hero and tells its client.
    /// </summary>
    private void ApplyItemStats(Entity hero, BbStatCollector merged)
    {
        foreach (var colours in merged.ParColorCalls) _host!.ApplyParColorOverride(hero, colours);
        var signature = merged.Signature();
        if (_statSignatures.TryGetValue(hero, out var last) ? last == signature : signature.Length == 0) return;
        _statSignatures[hero] = signature;
        hero.ItemScriptStats = signature.Length == 0 ? null : BbBuffRuntime.PayloadOf(merged);
        _host!.Spells.HeroStats?.Refresh(hero, push: _host.Spells.Effects.IsReferenceReady(hero));
    }

    /// <summary>
    /// The scope one item hook runs in: the copy is the Owner everywhere, its own <c>InstanceVars</c> the table the
    /// script reads for "this instance", and <c>Level</c> the copy's rank (1 — no shipped item indexes a
    /// <c>*ByLevel</c> table, but the level slot is filled the way a spell's is).
    /// </summary>
    private BbScope NewScope(Entity hero, BbLoadoutBinding binding) => ScopeOf(hero, hero, hero, hero, binding);

    /// <summary>
    /// The same scope with the unit slots filled in, plus <c>Slot</c>: the inventory slot the copy sits in. Items/3072
    /// and 3083 write their two tooltip rows through <c>BBSetSpellToolTipVar SlotNumberVar='Slot'</c>, and
    /// <c>BBNativeHandlers.Sum</c> adds the table value to the row's literal 0. A binding that is not in a slot has no
    /// number to give and the row's slot stays 0, which is what a missing <c>Slot</c> reads as anyway.
    /// </summary>
    private BbScope ScopeOf(Entity hero, Entity? attacker, Entity? target, Entity? caster, BbLoadoutBinding binding)
    {
        var scope = RouterOrThrow.NewScope(hero, attacker, target, caster, binding.Rank, binding: binding);
        scope.Vars["Slot"] = (double)binding.Slot;
        return scope;
    }

    /// <summary>
    /// The spellbook slot an inventory slot's tooltip rows are keyed by: the same map
    /// <see cref="BbNativeHandlers.StoredSlot"/> gives <c>BBSetSpellToolTipVar</c> for <c>InventorySlots</c>.
    /// </summary>
    private static int TooltipSlotOf(int inventorySlot) =>
        BbNativeHandlers.StoredSlot("item tooltip rows", BbSlotType.InventorySlots, inventorySlot);

    /// <summary>Runs one item hook of one copy in the hero's scope; a hook the script does not define does nothing.</summary>
    private void Run(Entity hero, BbLoadoutBinding binding, string hook)
    {
        if (binding.Script.Event(hook) is null) return;
        RouterOrThrow.Run(binding.Script, hook, NewScope(hero, binding), new BbHookFrame(hook, binding.Script.Name, hero)
        { Caster = hero, ItemId = binding.Id });
    }

    // ------------------------------------------------------------------------------------------------------------
    // The on-hit / on-being-hit callbacks (batch 147 step 3)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ItemOnHitUnit</c> of every copy the holder wears, as the basic attack lands on <paramref name="target"/>:
    /// the same callback <see cref="BbHookRouter.RunHitUnit"/> gives a champion's own script and its buffs, with the
    /// inventory copy as Owner and its <c>InstanceVars</c> the table. It runs in the damage chain (pre-mitigation,
    /// the victim still alive), so a copy can change <c>DamageAmount</c> as the champion hooks may and a debuff it
    /// adds is not a corpse debuff. Copies run in slot order and each reads the amount the one before left.
    /// </summary>
    public double OnHitUnit(Entity holder, Entity target, double amount,
        Combat.BasicAttackOutcome outcome = Combat.BasicAttackOutcome.Normal) =>
        RunHitChain("ItemOnHitUnit", holder, holder, target, amount, DamageType.Physical, outcome, victimIsOwner: false);

    /// <summary>
    /// <c>ItemOnBeingHit</c> of every copy the holder wears, for a basic attack that landed on it
    /// (<see cref="BbHookRouter.RunBeingHit"/>'s slots: Owner and Target the holder, Attacker the unit that struck).
    /// </summary>
    public double OnBeingHit(Entity holder, Entity attacker, double amount, DamageType type,
        Combat.BasicAttackOutcome outcome) =>
        RunHitChain("ItemOnBeingHit", holder, attacker, holder, amount, type, outcome, victimIsOwner: true);

    /// <summary>
    /// Runs one item hit hook of every copy the holder wears and returns the damage the chain leaves. The unit slots
    /// follow the champion hook the callback mirrors: <c>ItemOnHitUnit</c> is the swing's own (<c>RunHitUnit</c>'s),
    /// <c>ItemOnBeingHit</c> the one the hit lands on (<c>RunBeingHit</c>'s). A copy whose script does not define the
    /// hook, or a holder this module binds nothing for (a lane minion, a camp monster), changes nothing.
    /// </summary>
    private double RunHitChain(string hook, Entity holder, Entity attacker, Entity target, double amount,
        DamageType type, Combat.BasicAttackOutcome outcome, bool victimIsOwner)
    {
        if (_router is null || OwnerOf(holder) is not { } owner) return amount;
        var damage = amount;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding || binding.Script.Event(hook) is null) continue;
            var scope = victimIsOwner
                ? ScopeOf(holder, attacker, holder, holder, binding)
                : ScopeOf(holder, holder, target, holder, binding);
            BbHookRouter.PrepareHit(scope, damage, type, outcome);
            // The frame of the champion hook this one stands beside: the hit is the source of the damage the hook sees.
            var before = damage;
            var frame = victimIsOwner
                ? new BbHookFrame(hook, binding.Script.Name, holder)
                {
                    Caster = holder, DamageSource = DamageSourceKind.Basic, InDamageHook = true, ItemId = binding.Id,
                }
                : new BbHookFrame(hook, binding.Script.Name, holder) { Caster = holder, ItemId = binding.Id };
            RouterOrThrow.Run(binding.Script, hook, scope, frame);
            // A proc whose adds are one admission group is declined in full when it cannot get every slot it needs
            // (BbBuffRuntime.CommitProcAdds), so its DamageAmount write goes with its buffs.
            damage = frame.GroupDenied ? before : BbHookRouter.ChangedHit(scope, damage);
        }
        return damage;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The damage-stage callbacks (batch 147 step 4)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ItemOnPreDamage</c> of every copy <paramref name="victim"/> wears, for a hit about to reach its health
    /// (the stage <c>BuffOnPreDamage</c> of its script buffs uses: Owner and Target the victim, Attacker the unit
    /// that hit it). The hook reads <c>DamageAmount</c> / <c>DamageType</c> / <c>GetSourceType</c> and may write
    /// <c>DamageAmount</c> back. Copies run in slot order and each reads the amount the one before left.
    /// </summary>
    public double OnPreDamage(Entity victim, Entity? source, double amount, DamageType type,
        DamageSourceKind? kind, bool isPeriodic = false) =>
        RunDamageChain("ItemOnPreDamage", victim, source, victim, amount, type, kind, isPeriodic, victimIsOwner: true);

    /// <summary>
    /// <c>ItemOnPreDealDamage</c> of every copy the hitter wears, before the hit is worked out (the stage
    /// <c>CharOnPreDealDamage</c> / <c>BuffOnPreDealDamage</c> use): Owner and Attacker the hitter, Target the unit
    /// hit, the same damage variables as <see cref="OnPreDamage"/>.
    /// </summary>
    public double OnPreDealDamage(Entity attacker, Entity target, double amount, DamageType type,
        DamageSourceKind? kind, bool isPeriodic = false) =>
        RunDamageChain("ItemOnPreDealDamage", attacker, attacker, target, amount, type, kind, isPeriodic, victimIsOwner: false);

    /// <summary>
    /// <c>ItemOnDealDamage</c> of every copy the hitter wears, once the pre-deal hooks have run (the stage
    /// <c>BuffOnDealDamage</c> of its script buffs uses). Items/3116.luaobj registers one slow body under both this
    /// hook and <see cref="OnPreDealDamage"/>, so a Rylai hit runs it twice and the second run only renews the pair
    /// the first added.
    /// </summary>
    public double OnDealDamage(Entity attacker, Entity target, double amount, DamageType type,
        DamageSourceKind? kind, bool isPeriodic = false) =>
        RunDamageChain("ItemOnDealDamage", attacker, attacker, target, amount, type, kind, isPeriodic, victimIsOwner: false);

    /// <summary>
    /// Runs one item damage hook of every copy <paramref name="holder"/> wears and returns the damage the chain
    /// leaves. The unit slots follow the champion hook the callback mirrors: the pre-deal and deal hooks are the
    /// source's own, <c>ItemOnPreDamage</c> the one the hit lands on. A copy whose script does not define the hook,
    /// or a holder this module binds nothing for, changes nothing.
    /// </summary>
    private double RunDamageChain(string hook, Entity holder, Entity? attacker, Entity target, double amount,
        DamageType type, DamageSourceKind? kind, bool isPeriodic, bool victimIsOwner)
    {
        if (_router is null || OwnerOf(holder) is not { } owner) return amount;
        var damage = amount;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding || binding.Script.Event(hook) is null) continue;
            var scope = victimIsOwner
                ? ScopeOf(holder, attacker, holder, holder, binding)
                : ScopeOf(holder, holder, target, holder, binding);
            scope.Vars["DamageAmount"] = damage;
            scope.Vars["DamageType"] = BbHookRouter.DamageTypeNumber(type);
            var before = damage;
            var frame = new BbHookFrame(hook, binding.Script.Name, holder)
            {
                Caster = holder, DamageSource = kind, InDamageHook = true, DamageIsPeriodic = isPeriodic, ItemId = binding.Id,
            };
            RouterOrThrow.Run(binding.Script, hook, scope, frame);
            // Batch 147 step 4: a proc whose adds are one admission group is declined in full when it cannot get every
            // slot it needs (BbBuffRuntime.CommitProcAdds), so its DamageAmount write goes with its buffs.
            // Items/3155.luaobj's trigger would otherwise take 300 off the hit and leave neither shield nor cooldown
            // marker behind on a full table.
            damage = frame.GroupDenied ? before : BbHookRouter.ChangedHit(scope, damage);
        }
        return damage;
    }

    /// <summary>
    /// <c>BuffOnAllowAdd</c> of every copy <paramref name="target"/> wears, before a buff is placed on it (the same
    /// question the script buffs of that unit are asked): Owner and Target the unit the buff would land on, Attacker
    /// the unit adding it, <c>Type</c> / <c>BuffName</c> / <c>Duration</c> / <c>MaxStack</c> the add. The hook sets
    /// <c>BBSetReturnValue false</c> to refuse it. Items/3117.luaobj is the only shipped item that defines it: a
    /// hostile control on its wearer starts the five-second BootsOfMobilityDebuff first.
    /// </summary>
    public bool AllowBuffAdd(Entity target, Entity adder, string name, ref double duration, double scriptBuffType,
        double maxStack)
    {
        if (_router is null || OwnerOf(target) is not { } owner) return true;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding || binding.Script.Event("BuffOnAllowAdd") is null) continue;
            if (!RouterOrThrow.RunAllowAddOf(binding, target, adder, name, ref duration, scriptBuffType, maxStack)) return false;
        }
        return true;
    }

    /// <summary>
    /// A buff this module's scripts added left its holder (<c>OnBuffDeactivate</c>). Routed by the host
    /// (<c>SpellSystem.OnItemBuffRemoved</c>) the way a champion module's and the summoner module's are.
    /// </summary>
    public void OnBuffRemoved(Entity unit, BuffState buff) => _host?.Buffs.OnRemoved(unit, buff);

    // ------------------------------------------------------------------------------------------------------------
    // The spell-cast and avoided-attack callbacks (batch 147 step 3c)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ItemOnSpellCast</c> of every copy the caster wears, at the start of a champion-slot cast (after cost and
    /// cooldown, the moment <see cref="BbHookRouter.RunSpellCast"/> gives <c>CharOnSpellCast</c> and
    /// <c>BuffOnSpellCast</c>). Owner and Attacker are the caster, Target the unit the cast is aimed at, and
    /// <c>SpellVars</c> carry the cast spell's own flags — Items/3057, 3078, 3100 and 3124 all open with
    /// <c>BBIf SpellVars.DoesntTriggerSpellCasts</c> and do nothing for a spell that says so (Sivir's Q and W,
    /// Ashe's Frost Shot ...). Copies run in slot order, one admission each: the proc group that shares one
    /// free slot is only <c>ItemOnHitUnit</c> / <c>ItemOnBeingHit</c>.
    /// </summary>
    public void OnSpellCast(Entity caster, int slot, string? spellName)
    {
        if (_router is null || OwnerOf(caster) is not { } owner) return;
        for (var index = 0; index < Amberline.Simulation.Inventory.SlotCount; index++)
        {
            if (owner.Item(index) is not { Active: true } binding || binding.Script.Event("ItemOnSpellCast") is null) continue;
            var scope = ScopeOf(caster, caster, RouterOrThrow.CastTarget, caster, binding);
            scope.SpellVars = RouterOrThrow.CastSpellVars(caster, slot, spellName);
            RouterOrThrow.Run(binding.Script, "ItemOnSpellCast", scope, new BbHookFrame("ItemOnSpellCast", binding.Script.Name, caster)
            {
                Caster = caster, SpellSlot = slot, SpellName = spellName, ItemId = binding.Id,
            });
        }
    }

    /// <summary>
    /// <c>ItemOnBeingDodged</c> and <c>ItemOnMiss</c> of every copy the attacker wears, for a swing that was avoided.
    /// Owner and Attacker are the attacker, Target the unit the swing was aimed at, <c>HitResult</c> the outcome.
    /// Reconstruction: the two hooks split the outcome the way their names say — a dodge runs
    /// <c>ItemOnBeingDodged</c> and a miss (a blind) <c>ItemOnMiss</c>, one hook per avoided swing, the moment the
    /// avoided swing would have landed (the same impact <c>CharOnMiss</c> / <c>CharOnDodge</c> use) rather than the
    /// roll at its start, where the engine only knows that the swing misses. Only Items/3124 defines either hook and
    /// its two bodies are the same Rageblade stack, so the split is not observable there.
    /// </summary>
    public void OnAttackAvoided(Entity attacker, Entity target, Combat.BasicAttackOutcome outcome)
    {
        if (_router is null || OwnerOf(attacker) is not { } owner) return;
        var hook = outcome == Combat.BasicAttackOutcome.Dodge ? "ItemOnBeingDodged" : "ItemOnMiss";
        for (var index = 0; index < Amberline.Simulation.Inventory.SlotCount; index++)
        {
            if (owner.Item(index) is not { Active: true } binding || binding.Script.Event(hook) is null) continue;
            var scope = ScopeOf(attacker, attacker, target, attacker, binding);
            scope.Vars["HitResult"] = BbHookRouter.HitResultOf(outcome);
            RouterOrThrow.Run(binding.Script, hook, scope, new BbHookFrame(hook, binding.Script.Name, attacker)
            {
                Caster = attacker, ItemId = binding.Id,
            });
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The kill / assist / death callbacks (batch 149 step 1)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ItemOnKill</c> of every copy the <paramref name="killer"/> wears, for the unit it just killed. The slots are
    /// the credit hooks' own (<see cref="BbHookRouter.RunCreditHook"/>: Owner and Attacker the holder that earned the
    /// kill, Target the corpse), and the copy is the Owner with its <c>InstanceVars</c> the table. Items/3072 grows
    /// its two counters from this hook with no test of what died.
    /// </summary>
    public void OnItemKill(Entity? killer, Entity victim)
    {
        if (killer is null) return;
        RunCreditChain("ItemOnKill", killer, victim);
    }

    /// <summary>
    /// <c>ItemOnAssist</c> of every copy the <paramref name="assistant"/> wears, for the unit it helped kill. Same
    /// slots as <see cref="OnItemKill"/>. Items/3083 is the only shipped item that defines it and its body is the
    /// hero branch of its own <c>ItemOnKill</c>; the script does not guard on the victim's type here either.
    /// </summary>
    public void OnItemAssist(Entity assistant, Entity victim) => RunCreditChain("ItemOnAssist", assistant, victim);

    /// <summary>
    /// Runs one credit hook of every copy <paramref name="holder"/> wears. The hook names are the items' own
    /// (<c>ItemOnKillBuildingBlocks</c> / <c>ItemOnAssistBuildingBlocks</c>), which sit beside the buffs'
    /// <c>BuffOnKill</c> / <c>BuffOnAssist</c>. Order is the combat chain's: a copy's own callback is one of the
    /// death's item events (CombatService runs it before the kill reward), and the buffs' credit hooks run with the
    /// reward afterwards. The two do not share state in any shipped script — Items/3072 and 3083 only touch their
    /// copy's <c>InstanceVars</c>, and the snowball trio only ever sees its check buff's stacks — so nothing here
    /// depends on which of the two lands first.
    /// </summary>
    private void RunCreditChain(string hook, Entity holder, Entity victim)
    {
        if (_router is null || OwnerOf(holder) is not { } owner) return;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding || binding.Script.Event(hook) is null) continue;
            RouterOrThrow.Run(binding.Script, hook, ScopeOf(holder, holder, victim, holder, binding),
                new BbHookFrame(hook, binding.Script.Name, holder) { Caster = holder, ItemId = binding.Id });
        }
    }

    /// <summary>
    /// <c>ItemOnDeath</c> of every copy the dying <paramref name="hero"/> wears. Owner and Target are the hero, Attacker
    /// the unit that killed it (nil when the engine does not say — a summoner spell's execute, a fountain), Caster the
    /// hero: the slots <c>RunBuffHook("BuffOnDeath", …)</c> gives a buff of that unit. Items/3041, 3138 and 3141 only
    /// read their own stack counters and Items/3068 only names the self buff to take away, so no shipped body reads
    /// the attacker and the engine does not thread one through
    /// (<see cref="Combat.CombatTypes.OnItemDeath(Entity)"/>). Reconstruction: when a killer is known it fills
    /// <c>Attacker</c> for a future script that wants it.
    /// </summary>
    public void OnItemDeath(Entity hero, Entity? killer = null)
    {
        // A ward placement missile the hero cast but that has not left yet goes with the hero (the same rule the
        // champion module gives a hero's own lines).
        foreach (var line in _lines.Where(item => ReferenceEquals(item.Caster, hero)).ToArray())
            line.CancelBeforeLaunch(_host!.Spells);
        if (_router is null || OwnerOf(hero) is not { } owner) return;
        for (var slot = 0; slot < Amberline.Simulation.Inventory.SlotCount; slot++)
        {
            if (owner.Item(slot) is not { Active: true } binding || binding.Script.Event("ItemOnDeath") is null) continue;
            RouterOrThrow.Run(binding.Script, "ItemOnDeath", ScopeOf(hero, killer, hero, hero, binding),
                new BbHookFrame("ItemOnDeath", binding.Script.Name, hero) { Caster = hero, ItemId = binding.Id });
        }
    }
}
