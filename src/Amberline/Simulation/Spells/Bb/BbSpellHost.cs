using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Binds the interpreter's host boundary to the production services: units are <see cref="Entity"/> handles, positions are
/// <see cref="BbTable"/> points, spell/buff/damage/effect calls go to <see cref="SpellSystem"/>'s
/// <c>ISpellCombatService</c>, <c>ISpellBuffService</c> and <c>ISpellEffectSink</c>. The 45 primitive methods are bound where
/// the Annie closure (or a neighbouring read) needs them; the others raise <see cref="BbRuntimeException"/> with the
/// milestone that owns them, so an unbound call is a loud error and never a silent no-op.
/// </summary>
internal sealed class BbSpellHost : IBbHost
{
    private readonly Stack<BbHookFrame> _frames = new();
    private readonly BbNativeHandlers _natives;

    public BbSpellHost(SpellSystem system, Func<Entity, int, string> spellNameOf)
    {
        SpellNameOf = spellNameOf ?? throw new ArgumentNullException(nameof(spellNameOf));
        Spells = system ?? throw new ArgumentNullException(nameof(system));
        _natives = new BbNativeHandlers(this);
        Moves = new BbScriptMoves(this);
        Channels = new BbScriptChannels(this);
    }

    public SpellSystem Spells { get; }

    /// <summary>The unit's resource-bar colours, applied by what a stats hook collected (<see cref="BbStatCollector.SetParColor"/>).</summary>
    internal void ApplyParColorOverride(Entity unit, ParColorOverride? value) => _natives.ApplyParColorOverride(unit, value);

    /// <summary>Batch 446: an encounter's unit, spawned through the BBSpawnMinion path (<see cref="BbNativeHandlers.SpawnEncounterUnit"/>).</summary>
    internal Entity SpawnEncounterUnit(string skin, Position2 position, Team team, UnitType unitType = UnitType.Monster) =>
        _natives.SpawnEncounterUnit(skin, position, team, unitType);

    /// <summary>
    /// <c>BBSpellCast</c> of an extra-slot child (Sivir's Spiral Blade casts <c>SpiralBladeMissile</c> from its <c>SelfExecute</c>): the
    /// module that owns the champion's projectiles runs it. Null for a host with no such module.
    /// </summary>
    public Action<BbChildCast>? ChildCaster { get; internal set; }

    private short _fadeId;

    /// <summary>The next character-fade id: the client keeps one counter for every fade a script pushes.</summary>
    internal short NextFadeId() => unchecked(++_fadeId);

    private uint _characterDataId;

    /// <summary>
    /// The next character-data change id: the client keeps one counter that <c>BBPushCharacterData</c> and
    /// <c>BBPopAllCharacterData</c> both advance; the push hands the new value to the script as the layer's id.
    /// </summary>
    internal uint NextCharacterDataId() => unchecked(++_characterDataId);

    /// <summary>
    /// The spell in the hero's <paramref name="index"/>th extra slot (<c>Data*ExtraSpell{index + 1}</c> of the character inibin, the
    /// slots the stock client seeds at 42), or null when the character has none there.
    /// </summary>
    /// <summary>
    /// The spell a <c>BBSpellCast</c> names: the caster's live name for the stored slot, else the spell its character record lists for it
    /// (<c>Data*Spell1..4</c> for a spell slot, <c>Data*ExtraSpell1..16</c> for an extra slot). A hero's record is its champion's; a spawned unit's is
    /// the character it was spawned as (<c>Entity.Name</c>: a pet, a ward, the box or dummy minion a script casts from).
    /// </summary>
    internal string? ChildSpellName(Entity unit, BbSlotType type, int number)
    {
        if (type == BbSlotType.ExtraSlots && unit.UnitType == UnitType.Hero) return ExtraSpellName(unit, number);
        var stored = type == BbSlotType.SpellSlots ? number : SpellSystem.ExtraSpellSlotStart + number;
        if ((uint)stored < (uint)unit.ActiveSpellNames.Count && unit.ActiveSpellNames[stored] is { Length: > 0 } live) return live;
        var character = unit.UnitType == UnitType.Hero ? unit.StatCharacterName : unit.Name;
        if (string.IsNullOrEmpty(character) || !Spells.Data.HasCharacterData(character)) return null;
        try
        {
            var record = Amberline.Data.InibinReader.Load(Spells.Data.Paths, $"Characters/{character}/{character}.inibin");
            var key = type == BbSlotType.SpellSlots ? $"Data*Spell{number + 1}" : $"Data*ExtraSpell{number + 1}";
            return record.TryGetValue(Amberline.Data.InibinReader.KeyHash(key), out var value) && value.Kind == Amberline.Data.InibinValueKind.String &&
                   value.AsString() is { Length: > 0 } name && name != "BaseSpell" ? name : null;
        }
        catch (Amberline.Data.InibinException) { return null; }
    }

    internal string? ExtraSpellName(Entity hero, int index)
    {
        // A script can swap an extra slot (BBSetSpell with SlotType ExtraSlots; Janna's Howling Gale swaps the spell of its wave dummy each
        // charge level): the live name over the character's. The character is the one the unit is now (a pushed layer that carried its
        // spells: Nidalee's Takedown overrides her attack with the cougar record's ExtraSpell1).
        var stored = SpellSystem.ExtraSpellSlotStart + index;
        if ((uint)stored < (uint)hero.ActiveSpellNames.Count && hero.ActiveSpellNames[stored] is { Length: > 0 } live) return live;
        return hero.UnitType == UnitType.Hero &&
               Spells.Data.Champion(hero.StatCharacterName).Raw.TryGetValue(Amberline.Data.InibinReader.KeyHash($"Data*ExtraSpell{index + 1}"), out var value) &&
               value.Kind == Amberline.Data.InibinValueKind.String ? value.AsString() : null;
    }

    /// <summary>The champion's current source spell name for a slot (the module owns the table).</summary>
    public Func<Entity, int, string> SpellNameOf { get; }
    public BbBuffRuntime Buffs { get; internal set; } = null!;
    public BbHookRouter Router { get; internal set; } = null!;
    /// <summary>The displacements this host's scripts started (<c>BBMove</c> and its siblings); the module ticks them.</summary>
    internal BbScriptMoves Moves { get; }
    /// <summary>The channelled spells this host's scripts run (<c>ChannelDuration</c> and the <c>Channeling*</c> hooks); the module ticks them.</summary>
    internal BbScriptChannels Channels { get; }
    public BbHookFrame Frame => _frames.Count > 0 ? _frames.Peek()
        : throw new BbRuntimeException("no script hook is running");
    public bool HasFrame => _frames.Count > 0;

    /// <summary>The effect ids a script removed (<c>BBSpellEffectRemove</c>) or the buff runtime took down; a stale id in a script variable is never killed twice.</summary>
    internal HashSet<uint> KilledEffects { get; } = new();

    /// <summary>Batch 398 (Reconstruction): the unbound placemarker effects a script never removes, by the cube they were sent for. The cube's death
    /// kills them (BbBuffRuntime.OnUnitDeath): the client's bound emitter ends with its unit.</summary>
    internal Dictionary<uint, List<uint>> PlacemarkerEffects { get; } = new();

    /// <summary>
    /// Batch 726: the persistent effects (<c>BBSpellEffectCreate</c> whose id the script removes later) by the table that keeps their id, a channel's
    /// or a buff stack's <c>InstanceVars</c>. Weak keys: a table that never ends (a character's, an item's, a frame's locals) takes its entries with it.
    /// ponytail: an id created straight into NextBuffVars (Amumu's Bandage Toss) is owned by that table, not by the buff's copy of it, so that buff's
    /// end does not retire it; every shipped buff that gets one removes it in its OnBuffDeactivate. Own the copy too if a script ever leaves one.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BbTable, List<uint>> _ownedEffects = new();

    internal void OwnEffect(BbTable vars, uint id)
    {
        var ids = _ownedEffects.GetOrCreateValue(vars);
        ids.RemoveAll(KilledEffects.Contains);
        ids.Add(id);
    }

    /// <summary>
    /// Batch 726 (owner report, 9 November 2026: an enemy entering vision still showed a recall effect): the instance whose <paramref name="vars"/>
    /// kept these ids has ended (a channel's stop, a buff stack's deactivation), so no script can remove what it left. Recall.luaobj removes
    /// TeleportHome only in <c>ChannelingCancelStop</c>; its <c>ChannelingSuccessStop</c> removes nothing, and the effect stayed in the late-sight
    /// replay for the rest of the match. A left effect is retired (<see cref="ISpellEffectSink.RetireEffect"/>): it leaves the replay, a copy a
    /// late sight replayed is killed, and a client that saw it created lets it run out on its own clock, the script sending no removal.
    /// An id the instance handed on (<c>BBSetVarInTable</c> into <c>NextBuffVars</c>: Malzahar's Call of the Void at its deactivation,
    /// Mordekaiser's Children of the Grave) belongs to the live instance that holds it now, which can still remove it.
    /// Reconstruction: retail's late-observer replay is not recovered; this bounds the replay by the life of the script instances that hold the id.
    /// </summary>
    internal void RetireOwnedEffects(BbTable vars)
    {
        if (!_ownedEffects.TryGetValue(vars, out var ids)) return;
        _ownedEffects.Remove(vars);
        foreach (var id in ids)
        {
            if (KilledEffects.Contains(id)) continue;
            if (LiveInstanceTables().FirstOrDefault(table => table.Entries.Any(entry => entry.Value is double held && held == id)) is { } heir)
                OwnEffect(heir, id);
            else if (KilledEffects.Add(id)) Spells.Effects.RetireEffect(id);
        }
    }

    // The InstanceVars of the channels and buff stacks this host still runs (an ended instance has left both collections).
    private IEnumerable<BbTable> LiveInstanceTables() =>
        Channels.Active.Select(channel => channel.InstanceVars)
            .Concat(Buffs.Instances.SelectMany(instance => instance.Stacks.Select(stack => stack.Vars).Append(instance.InstanceVars)));

    public void Push(BbHookFrame frame) => _frames.Push(frame);
    public void Pop() => _frames.Pop();

    /// <summary>A <c>BBExecutePeriodically</c> body is running on the hook that is running (see <see cref="BbHookFrame.PeriodicDepth"/>).</summary>
    public void EnterPeriodic()
    {
        if (HasFrame) Frame.PeriodicDepth++;
    }

    public void ExitPeriodic()
    {
        if (HasFrame && Frame.PeriodicDepth > 0) Frame.PeriodicDepth--;
    }

    public IBbNativeHandler? NativeHandler(BbNativeOwner owner) => _natives;

    // ------------------------------------------------------------------------------------------------------------
    // Unit handles and positions
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The unit a handle names; a null handle is the hook's own unit where retail's Lua omits the argument.</summary>
    internal Entity UnitOrSelf(object? unit) => unit switch
    {
        Entity entity => entity,
        null => Frame.Owner ?? throw new BbRuntimeException("a unit-less call has no script unit here"),
        _ => throw new BbRuntimeException($"expected a unit, got {BbRuntimeNames.Of(unit)}"),
    };

    /// <summary>A unit handle or null for nil and for the <c>Nothing</c> slot.</summary>
    internal static Entity? UnitOrNull(object? unit) => unit as Entity;

    /// <summary>A script point at <paramref name="position"/>, <paramref name="height"/> above the terrain (a missile's flight height, a raised unit's).</summary>
    internal BbTable Point(Position2 position, double height = 0.0) =>
        BbTable.Point(position.X, Spells.Movement.Navigation.HeightAt(position) + height, position.Z);

    /// <summary>
    /// Batch 713: how far above the terrain a position operand stands: a point's y over the ground at its x and z, a unit's
    /// <see cref="Entity.Height"/>. The script positions are the server's 3D points (the stock <c>BBGetUnitPosition</c> stores the unit's position
    /// vector), so the height a missile end or a raised unit carries reaches the spawn and the cast that read it.
    /// </summary>
    internal double HeightAbove(object? operand) => operand switch
    {
        BbTable table when table["y"] is double y => y - Spells.Movement.Navigation.HeightAt(Ground(table)),
        Entity unit => unit.Height,
        _ => 0.0,
    };

    /// <summary>
    /// A position variable. The client's shared position getter reads a vector from the variable and, when the variable
    /// names a game object instead, answers that object's position: Ezreal's Arcane Shift passes the unit it picked
    /// as the <c>LocationVar</c> of <c>BBFaceDirection</c>, and its <c>BBSpellCast</c> passes the caster as <c>PosVar</c>.
    /// </summary>
    internal static Position2 Ground(object? point) => point switch
    {
        BbTable table => new Position2(BbRuntimeNames.Number(table["x"]), BbRuntimeNames.Number(table["z"])),
        Entity unit => unit.Position,
        _ => throw new BbRuntimeException($"expected a position, got {BbRuntimeNames.Of(point)}"),
    };

    public double GetTeamID(object? unit) => TeamMapping.ToNative(UnitOrSelf(unit).Team);
    // The type tests behind CO_IS_TYPE_AI / _HERO / _TURRET (and their NOT forms, BuildingBlocksBase protos 0.18 to 0.26) are the
    // The script helper's natives IsObjectAI, IsObjectHero and IsTurretAI. Each answers false for nil or a
    // value that is no game object (the nil test, then the object lookup), and otherwise reads one bit of the
    // object's type flags: 0x400 obj_AI_Base (hero, minion, monster, pet, ward, turret; the base-AI type test), 0x1000
    // obj_AI_Hero, 0x2000 obj_AI_Turret. The dampener/nexus structures and missiles carry none of them (APBonusDamageToTowers
    // branches Target IS_TYPE_TURRET, then Target IS_NOT_AI for the buildings). Batch 269: a nil handle no longer stands for the
    // hook's own unit here.
    public bool IsObjectAI(object? unit) => unit is Entity { UnitType: UnitType.Hero or UnitType.Minion or UnitType.Monster or UnitType.Turret };
    public bool IsObjectHero(object? unit) => unit is Entity { UnitType: UnitType.Hero };
    // The combat type of a unit is its character data's IsMelee flag (the combat-type field is 2 minus bit 0 of the character record,
    // IsMelee/IsRanged compare it with 1 and 2): the inibin key Data*IsMelee ("Yes"/"No"), which every hero and
    // lane minion file carries (Blue_Minion_MechMelee, the super minion, says No at range 170). The loader reads
    // it with the default "No" and sets the bit only for "Yes" (_stricmp), so a record without the key is ranged: TeemoMushroom,
    // the inhibitors, the nexus and the shop are the shipped ones. SetCharacterData sets the type from the data it is given on every
    // push, so a pushed layer's record decides it (Nidalee_Cougar says Yes, Nidalee No: Frozen Mallet's and the red buff's melee slow).
    // A unit whose name has no record (the emulator's Turret_* names, whose OrderTurret*/ChaosTurret* records all say No, or a test
    // body) reads the loader's default. The native soft-asserts and answers false for nil or a value that is no game object,
    // so CO_IS_RANGED holds for it.
    public bool IsMelee(object? unit) => unit is Entity entity && IsMeleeUnit(entity);

    private readonly Dictionary<string, bool> _combatTypes = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsMeleeUnit(Entity entity)
    {
        var name = entity.ScriptCharacterData?.SkinName ?? (entity.UnitType == UnitType.Hero ? entity.ChampionName : entity.Name);
        if (string.IsNullOrEmpty(name)) return false;
        if (!_combatTypes.TryGetValue(name, out var melee))
        {
            melee = Spells.Data.OptionalChampion(name) is { } record &&
                    record.Raw.TryGetValue(Amberline.Data.InibinReader.KeyHash("Data*IsMelee"), out var value) &&
                    // Every shipped value is the string Yes or No (191 records).
                    value.Kind == Amberline.Data.InibinValueKind.String &&
                    string.Equals(value.AsString(), "Yes", StringComparison.OrdinalIgnoreCase);
            _combatTypes[name] = melee;
        }
        return melee;
    }
    public bool IsTurretAI(object? unit) => unit is Entity { UnitType: UnitType.Turret };
    public bool IsDead(object? unit) => !UnitOrSelf(unit).Alive;
    public string? GetUnitSkinName(object? unit)
    {
        var entity = UnitOrSelf(unit);
        return entity.UnitType == UnitType.Hero ? entity.ChampionName : entity.Name;
    }

    public void SetScaleSkinCoef(object? scale, object? unit) => throw Unbound("SetScaleSkinCoef", "M3");

    // ------------------------------------------------------------------------------------------------------------
    // IBbGeometryHost
    // ------------------------------------------------------------------------------------------------------------

    public object? GetPosition(object? unit) => Point(UnitOrSelf(unit).Position);

    public object? GetNormalizedPositionDelta(object? target, object? attacker, bool flag)
    {
        var from = UnitOrSelf(attacker).Position;
        var to = UnitOrSelf(target).Position;
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return length <= 1e-9 ? BbTable.Point(0.0, 0.0, 0.0) : BbTable.Point(dx / length, 0.0, dz / length);
    }

    public void SetPosition(object? unit, object? position) => throw Unbound("SetPosition", "M3");

    public object? Make3DPoint(object? x, object? y, object? z) =>
        BbTable.Point(BbRuntimeNames.Number(x), BbRuntimeNames.Number(y), BbRuntimeNames.Number(z));

    // Reconstruction (18 October 2026 audit, decided for edge to edge on 19 October 2026 by the Orianna conversion): the three Distance*
    // globals BuildingBlocksBase.luaobj calls are not registered by the stock client (no string in the exe), so their bodies are
    // server-only. The only client code with the same names is the shared sphere helper
    // the helper BBDistanceBetweenObjectsSq(a, b, margin) = d^2 - (ra + rb + margin)^2 (batch 331: the same body in the client, each
    // radius the bounding radius = GameplayCollisionRadius x skin scale, i.e. CombatRadius), which returns a
    // squared surplus used for range tests (the within-distance test is below 0, the Target targeter's range check of a
    // spell with CastRangeUseBoundingBoxes, the spell's cast reach, SpellSystem.UnitCastReach), not a distance a script could read. The unit overload takes the gap between the two bodies, the
    // reading the helper's radii suggest and the one Orianna's numbers need: OrianaGhost picks the ball up at "Distance <= 25" (a unit
    // whose centre must come within 25 of the ball's would be a pixel-exact walk-over, where the ball is picked up on touch), and the
    // hand-written Orianna the owner played followed the same gap (pickup 25 + both radii, leash 1125 + both radii). The other consumers
    // compare with a range literal, except KarmaSpiritBond, which halves it to place a point between two units, and Rocket Grab
    // (RocketGrabMissile: the hand's return takes Distance / 1350, which with the gap is the time of the pull that ends where the bodies
    // touch). A gap below zero (overlapping bodies) reads 0. Reconstruction: the native's value is a squared surplus, not a distance a script can read, so the unit overload is our reading; the point and unit-to-point
    // overloads stay centre distances.
    public double DistanceBetweenObjects(object? a, object? b)
    {
        var first = UnitOrSelf(a);
        var second = UnitOrSelf(b);
        return Math.Max(0.0, Distance(first.Position, second.Position) - first.CombatRadius - second.CombatRadius);
    }
    public double DistanceBetweenObjectAndPoint(object? unit, object? point) => Distance(UnitOrSelf(unit).Position, Ground(point));
    public double DistanceBetweenPoints(object? a, object? b) => Distance(Ground(a), Ground(b));

    public bool IsTargetInFrontOfMe(object? me, object? target) => FacingDot(UnitOrSelf(me), UnitOrSelf(target)) > 0.0;
    public bool IsTargetBehindMe(object? me, object? target) => FacingDot(UnitOrSelf(me), UnitOrSelf(target)) < 0.0;

    private static double FacingDot(Entity me, Entity target)
    {
        var dx = target.Position.X - me.Position.X;
        var dz = target.Position.Z - me.Position.Z;
        return me.Facing.X * dx + me.Facing.Z * dz;
    }

    internal static double Distance(Position2 a, Position2 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------------------------------------------------------
    // IBbBuffHost
    // ------------------------------------------------------------------------------------------------------------

    // The buff-table reads land the running hook's held adds first (BbBuffRuntime.LandHeldAdds, batch 703).
    public bool HasBuffOfType(object? unit, double? buffType)
    {
        Buffs.LandHeldAdds();
        return buffType is { } type && UnitOrSelf(unit).Buffs.Any(buff => buff.Type == BbBuffRuntime.ServerBuffType(type));
    }

    public double SpellBuffCount(object? unit, string? buffName, object? caster)
    {
        Buffs.LandHeldAdds();
        var casterId = UnitOrNull(caster)?.NetId;
        var entity = UnitOrSelf(unit);
        double count = entity.Buffs
            .Where(buff => string.Equals(buff.Name, buffName, StringComparison.OrdinalIgnoreCase) &&
                           (casterId is null || buff.CasterNetId == casterId))
            .Sum(buff => Math.Max(1, buff.Stacks));
        return count;
    }

    /// <summary>
    /// Whether the unit carries the shared ward marker: the bare <c>SharedWardBuff</c> query SummonerTeleport's own
    /// <c>BBIfHasBuff</c> makes of the target before its <c>Destealth</c> add (it names the marker with
    /// <c>AttackerVar</c> nothing, so no caster filter). A capacity gate must ask the same question the script does, or
    /// it reserves rows the script never adds. Batch 149 step 3: Spells/SightWard, VisionWard and WriggleLantern add the
    /// marker to the wards they spawn (one <c>BBSpellBuffAdd</c> in their SelfExecute, a 180 s Internal row beside the
    /// ward's own invisibility row), so the row is the whole answer and the placed-ward stand-in this used to fall back
    /// on is gone. The stand-in's two terms answered for that one marker at once — the row and the ward flag — and the
    /// flag alone still answered true for a ward-flagged unit that carries none (a placement whose own add group was
    /// refused), where the script reads the row only, adds no <c>Destealth</c>, and the gate reserved one anyway.
    /// </summary>
    internal static bool CarriesSharedWardBuff(Entity entity) =>
        entity.Buffs.Any(buff => string.Equals(buff.Name, "SharedWardBuff", StringComparison.OrdinalIgnoreCase));

    /// <summary>A dry probe run (<see cref="BbDamageProbe"/>) may only read: anything that changes the world ends it. A live one notes the effect.</summary>
    internal void NoProbe()
    {
        if (!HasFrame || Frame.Probe is not { } probe) return;
        if (!probe.Live) throw new BbProbeAbort();
        probe.EffectsRan = true;
    }

    public void SpellBuffAdd(object? target, object? attacker, string buffName, double addType, double maxStack,
        double duration, BbTable? vars)
    {
        // BBBreakSpellShields ahead of the primary hit (Takedown pops the shield, then hits): the run made as the hit lands pops it (the popped
        // shield zeroes the hit, and the swing's own run of the script then finds none); the run made before the roll leaves it alone.
        if (HasFrame && Frame.Probe is { } probe && buffName == BbBuffRuntime.SpellShieldMarker && !probe.PopsShields) return;
        if (!(HasFrame && Frame.Probe is not null && buffName == BbBuffRuntime.SpellShieldMarker)) NoProbe();
        Buffs.AddFromHost(UnitOrSelf(target), UnitOrNull(attacker) ?? UnitOrSelf(target), buffName, addType, maxStack, duration, vars);
    }

    public void SpellBuffRemove(object? target, string? buffName, object? attacker)
    {
        NoProbe();
        Buffs.LandHeldAdds();
        Buffs.Remove(UnitOrSelf(target), buffName, UnitOrNull(attacker));
    }

    public void SpellBuffRemoveStacks(object? target, string? buffName, object? attacker, double count)
    {
        NoProbe();
        Buffs.LandHeldAdds();
        Buffs.RemoveStacks(UnitOrSelf(target), buffName, UnitOrNull(attacker), (int)Math.Ceiling(Math.Min(count, 1000.0)));
    }

    public double? CurrentBuffEndsAt => HasFrame && Frame.Buff is { } running ? running.LifeEndsAt : null;

    // ------------------------------------------------------------------------------------------------------------
    // IBbCombatHost
    // ------------------------------------------------------------------------------------------------------------

    public double GetSourceType() => HasFrame && Frame is { InDamageHook: true, DamageIsPeriodic: true }
        ? (double)BbDamageSource.DAMAGESOURCE_PERIODIC
        : HasFrame && Frame is { InDamageHook: true, DamageSource: { } kind } ? DamageSourceNumber(kind) : -1.0;

    /// <summary>The DAMAGESOURCE_* number of a server damage category (the inverse of <see cref="BbNativeHandlers"/>' mapping).</summary>
    internal static double DamageSourceNumber(DamageSourceKind kind) => kind switch
    {
        DamageSourceKind.Basic => (double)BbDamageSource.DAMAGESOURCE_ATTACK,
        DamageSourceKind.Spell => (double)BbDamageSource.DAMAGESOURCE_SPELL,
        DamageSourceKind.SpellAoe => (double)BbDamageSource.DAMAGESOURCE_SPELLAOE,
        DamageSourceKind.SpellPersist => (double)BbDamageSource.DAMAGESOURCE_SPELLPERSIST,
        DamageSourceKind.Proc => (double)BbDamageSource.DAMAGESOURCE_PROC,
        DamageSourceKind.Raw => (double)BbDamageSource.DAMAGESOURCE_RAW,
        DamageSourceKind.InternalRaw or DamageSourceKind.Internal => (double)BbDamageSource.DAMAGESOURCE_INTERNALRAW,
        _ => (double)BbDamageSource.DAMAGESOURCE_DEFAULT,
    };

    // ------------------------------------------------------------------------------------------------------------
    // IBbStatHost
    // ------------------------------------------------------------------------------------------------------------

    public double GetLevel(object? unit) => UnitOrSelf(unit).Level;
    public double GetArmor(object? unit) => UnitOrSelf(unit).Armor;
    public double GetSpellBlock(object? unit) => UnitOrSelf(unit).SpellBlock;
    public double GetTotalAttackDamage(object? unit) => UnitOrSelf(unit).AttackDamage;

    public void IncStat(BbIncStat stat, double delta, object? unit)
    {
        NoProbe();
        // IncPermanent*Mod: one accumulator shared with the item modifiers (the accumulator serves BBIncStat and
        // BBIncPermanentStat alike); it keeps its value until a script subtracts it again.
        if (PermanentModifier(stat) is { } key)
        {
            // A bare permanent is a mutation, not a contribution: in a stats sample it accumulates on the buff's mutation
            // clock (BbHookFrame.MutationSuppressed), so TurretBonus's +6 / +1.5 / +1.5 under a TickRate 60 add lands
            // every 60 s where the 0.25 s sample grid would pile it up four times a second.
            if (HasFrame && Frame.MutationSuppressed) return;
            var hero = UnitOrSelf(unit);
            // The sight radius modifiers are the unit's own (any unit has a perception radius; the client keeps them in the permanent
            // intermediate of the unit, the permanent accumulator with CharInterPermanent). They are read by VisibilityRules.BubbleRadiusMods.
            if (key is VisibilityRules.PermanentFlatBubbleKey or VisibilityRules.PermanentPercentBubbleKey)
            {
                hero.PermanentStatMods[key] = (hero.PermanentStatMods.TryGetValue(key, out var bubble) ? bubble : 0.0) + delta;
                return;
            }
            if (hero.UnitType != UnitType.Hero)
            {
                PermanentOnPet(hero, stat, key, delta);
                return;
            }
            hero.PermanentStatMods[key] = (hero.PermanentStatMods.TryGetValue(key, out var current) ? current : 0.0) + delta;
            if (Spells.HeroStats is { } stats) stats.Refresh(hero, push: Spells.Effects.IsReferenceReady(hero));
            return;
        }
        // BuffOnUpdateStats is sampled into a payload (BbBuffRuntime): the collector is the only other stat sink in M1.
        if (HasFrame && Frame.Stats is { } collector)
        {
            var target = UnitOrSelf(unit);
            if (!ReferenceEquals(target, Frame.Owner))
            {
                // A stat of another unit (YorickSummonSpectral's attack damage and speed for its ghoul, the buff's caster): kept apart and applied to that
                // unit by an internal buff of its own (BbBuffRuntime.ApplyForeign).
                collector.AddForeign(target, stat, delta);
                return;
            }
            collector.Add(stat, delta);
            return;
        }
        // Outside the stats hook an Inc*Mod call is transient in retail: the stat accumulators are rebuilt from the buffs'
        // BuffOnUpdateStats on its own 0.25 s grid (BbBuffRuntime.StatsUpdateInterval), so an activation call changes nothing
        // that outlives the frame. Reconstruction: the call
        // is accepted only when the running buff's own stats hook asserts the same accessor (Radiance, Righteous Fury and
        // Spell Flux repeat theirs in OnBuffActivate/OnBuffDeactivate); anything else would be a stat the engine silently drops.
        if (HasFrame && Frame.Buff is { } running && StatsHookCalls(running.Script, "BBIncStat", "Stat", stat.ToString())) return;
        // Reconstruction (batch 332): the Demacian Standard's OnBuffActivate cuts its sight by 50 percent (IncPercentBubbleRadiusMod -0.5), and its own
        // stats hook never asserts that accessor. The call is the transient activation call above: it changes nothing that outlives the frame, while the
        // flat -500 of the same hook is permanent and kept. Accepted so the rest of the activation hook (statuses, the untargetable team) runs.
        if (stat.ToString() == "IncPercentBubbleRadiusMod") return;
        throw Unbound($"{stat} outside BuffOnUpdateStats", "M3");
    }

    /// <summary>
    /// <c>BBIncPermanentStat</c> on a unit that is not a hero (the ghouls' Logic buffs scale their ghoul with Yorick's level, attack damage and health;
    /// the revenant's buff takes its health regeneration): a non-hero unit has no stat pipeline, so the amount is added to the unit once, as
    /// the hand-written ghoul code added the same amounts. The stats go out to the clients at once. Reconstruction: the accessors the shipped
    /// pet scripts use are the ones mapped; a health pool raises the current health with it (a pet spawned full stays full), a percent
    /// regeneration modifier scales the unit's regeneration, and a movement bonus joins the baseline the lifecycle recomputes speed from.
    /// </summary>
    private void PermanentOnPet(Entity pet, BbIncStat stat, string key, double delta)
    {
        switch (key)
        {
            // A non-hero unit's resistances are recomputed from a baseline plus its buffs: the permanent amount joins the baseline.
            case "FlatArmorMod": BuffLifecycle.IncreaseNpcBaseResistances(pet, delta, 0.0); break;
            case "FlatSpellBlockMod": BuffLifecycle.IncreaseNpcBaseResistances(pet, 0.0, delta); break;
            case "FlatHPPoolMod":
                pet.MaxHealth += delta;
                pet.Health = Math.Min(pet.MaxHealth, pet.Health + delta);
                break;
            case "FlatPhysicalDamageMod": pet.AttackDamage += delta; break;
            // Spells/ResistantSkin.luaobj's OnBuffActivate writes GameTime x 0.00625 as IncPermanentFlatHPRegenMod (0475-0483);
            // on the Worm, a non-hero, that is its baseline regeneration (the field the lifecycle's Percent*RegenMod scales).
            case "FlatHPRegenMod": pet.HealthRegen += delta; break;
            // The turret's own assist bonus (PersonalTurretAssistBonus, batch 159 step 2): a percent amount on the
            // unit's base attacks-per-second, the same additive family a buff's IncPercentAttackSpeedMod joins.
            case "PercentAttackSpeedMod": BuffLifecycle.IncreaseNpcBaseAttackSpeed(pet, delta); break;
            case "FlatMagicDamageMod": pet.AbilityPower += delta; break;
            case "FlatMovementSpeedMod":
                // The speed is recomputed from the baseline and the buffs' factors (a Q ghoul's 25 percent applies to the bonus too).
                pet.MovementSpeedBaseline = (pet.MovementSpeedBaseline ?? pet.Speed) + delta;
                Spells.Buffs.RefreshMovementSpeed(pet, push: false);
                break;
            case "PercentHPRegenMod": pet.HealthRegen *= Math.Max(0.0, 1.0 + delta); break;
            // Batch 560: Twisted Treeline's RabidWolf keeps CrestOfNaturesFury on itself (CharScriptRabidWolf's UpdateSelfBuffActions), whose
            // OnBuffActivate/OnBuffDeactivate add and take back -0.1 IncPermanentPercentCooldownMod on the Owner. The client's accessor
            // (the accessor registered for the AI units) finds the unit in the base-AI manager, so any AI unit takes it
            // into its own permanent intermediate. The unit's own casts read it (BbChampionModule.StartChildCooldown) and so does
            // GetPercentCooldownMod. Nothing is sent: the minion's LOCAL1 table has no mPercentCooldownMod,
            // only the hero's.
            case "PercentCooldownMod": pet.CooldownMod += delta; return;
            default: throw Unbound($"{stat} on a non-hero unit", "M3");
        }
        if (Spells.Effects.IsReferenceReady(pet)) Spells.Effects.SendStats(pet);
    }

    /// <summary>Whether the script's <c>BuffOnUpdateStats</c> hook calls <paramref name="function"/> with the accessor <paramref name="name"/>.</summary>
    internal static bool StatsHookCalls(BbScript script, string function, string key, string name) =>
        script.Event("BuffOnUpdateStats")?.Blocks.SelectMany(Flatten).Any(block => block.Function == function &&
            block.Param(key) is BbValue.Reference reference && reference.Name == name) == true;

    private static IEnumerable<BbBlock> Flatten(BbBlock block) =>
        new[] { block }.Concat(block.SubBlocks.SelectMany(Flatten));

    /// <summary>
    /// The hero stat calculator's modifier name for an <c>IncPermanent*</c> accessor, or null for any other accessor.
    /// Only the modifiers the calculator reads are mapped; the rest of the family stays loudly unbound.
    /// </summary>
    private static string? PermanentModifier(BbIncStat stat)
    {
        const string prefix = "IncPermanent";
        var name = stat.ToString();
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = name[prefix.Length..];
        return PermanentKeys.Contains(key) ? key : throw Unbound($"{stat} (the stat pipeline has no {key})", "M3");
    }

    private static readonly HashSet<string> PermanentKeys = new(StringComparer.Ordinal)
    {
        "FlatArmorMod", "PercentArmorMod", "FlatSpellBlockMod", "PercentSpellBlockMod", "FlatHPPoolMod", "PercentHPPoolMod",
        "FlatHPRegenMod", "PercentHPRegenMod", "FlatMagicDamageMod", "PercentMagicDamageMod", "FlatPhysicalDamageMod",
        "PercentPhysicalDamageMod", "FlatMovementSpeedMod", "PercentMovementSpeedMod", "FlatAttackRangeMod",
        "PercentAttackRangeMod", "PercentAttackSpeedMod", "FlatCritChanceMod", "FlatCritDamageMod", "PercentCritDamageMod",
        "FlatArmorPenetrationMod", "FlatMagicPenetrationMod", "PercentHardnessMod", "PercentLifeStealMod", "PercentSpellVampMod",
        // Batch 149 step 2: the active windows' own IncPermanentPercentCooldownMod (ShurelyasCrest, DeathfireGrasp,
        // YoumusBlade and RanduinsOmen take their cooldown from OnBuffActivate and give it back in OnBuffDeactivate).
        // It is the same PercentCooldownMod accumulator the item rows feed, so it joins the item totals.
        "PercentCooldownMod",
        VisibilityRules.PermanentFlatBubbleKey, VisibilityRules.PermanentPercentBubbleKey,
    };

    public double GetStat(BbGetStat stat, object? unit)
    {
        var entity = UnitOrSelf(unit);
        return stat switch
        {
            BbGetStat.GetArmor => entity.Armor,
            BbGetStat.GetSpellBlock => entity.SpellBlock,
            BbGetStat.GetLevel => entity.Level,
            BbGetStat.GetTotalAttackDamage => entity.AttackDamage,
            BbGetStat.GetMovementSpeed => entity.Speed,
            // Items, runes and buffs raise ability power only through flat modifiers, so the unit's AP is the flat total.
            BbGetStat.GetFlatMagicDamageMod => entity.AbilityPower,
            BbGetStat.GetFlatPhysicalDamageMod => FlatPhysicalDamageMod(entity),
            BbGetStat.GetBaseAttackDamage => entity.AttackDamage - BonusAttackDamage(entity),
            // Ardor.luaobj's BuffOnUpdateActions reads it, subtracts its own prior contribution and adds PercentMod times what
            // is left: the bonus pool over the champion's base at its level, the same reading as GetFlatPhysicalDamageMod.
            BbGetStat.GetPercentAttackSpeedMod => BonusAttackSpeedPercent(entity),
            // The percentage and chance modifiers the stat pipeline already keeps on the unit, as the fractions the scripts multiply with:
            // PercentCooldownMod is negative for cooldown reduction (the scripts compute cooldown * (1 + mod), 84 uses).
            BbGetStat.GetPercentCooldownMod => entity.CooldownMod,
            BbGetStat.GetFlatCritChanceMod => entity.CritChance,
            // The critical strike multiplier is 2 plus this modifier (VayneTumbleAttack adds the 2 back: CritDamage = GetFlatCritDamageMod + 2).
            BbGetStat.GetFlatCritDamageMod => entity.CritMultiplier - 2.0,
            BbGetStat.GetPercentLifeStealMod => entity.LifeSteal,
            BbGetStat.GetPercentSpellVampMod => entity.SpellVamp,
            // Reconstruction (batch 149 step 2): the unit's percent spell-block modifier, the accumulator
            // IncPercentSpellBlockMod writes. Only its buff half is readable here (payload.SpellBlockMult); the gear half
            // (item and rune PercentSpellBlockMod) is a HeroStats input the entity does not keep. Exactly two shipped
            // scripts ask for it — SpiritVisage's and GrievousWound's spell-vamp quirk, which both seed a share their
            // own update hook re-derives from the real spell vamp a tick later — so the missing half moves one first
            // sample of that share and nothing else. GetPercentHPRegenMod stays unbound: no shipped script reads it.
            BbGetStat.GetPercentSpellBlockMod => entity.Buffs.Sum(buff => buff.Payload.SpellBlockMult),
            BbGetStat.GetPercentHardnessMod => entity.Hardness,
            BbGetStat.GetDodge => entity.Dodge,
            BbGetStat.GetFlatMovementSpeedMod => entity.FlatMovementSpeedMod,
            // mFlatHPPoolMod: the flat health the sources add (Vladimir's Blood Gorged turns it into ability power); any other unit has its buffs' part.
            BbGetStat.GetFlatHPPoolMod => entity.UnitType == UnitType.Hero ? entity.FlatHpPoolMod : entity.Buffs.Sum(buff => buff.Payload.MaxHp),
            BbGetStat.GetFlatBubbleRadiusMod => VisibilityRules.BubbleRadiusMods(entity).Flat,
            BbGetStat.GetPercentBubbleRadiusMod => VisibilityRules.BubbleRadiusMods(entity).Percent,
            // The flat attack range the unit's script buffs add (IncFlatAttackRangeMod); the Odin turrets' CharOnActivate reads it (map 8).
            // Reconstruction: a hero's IncPermanentFlatAttackRangeMod goes to the stat calculator, which the entity does not keep apart.
            BbGetStat.GetFlatAttackRangeMod => entity.Buffs.Sum(buff => buff.Payload.AttackRange),
            _ => throw Unbound(stat.ToString(), "M2"),
        };
    }

    /// <summary>
    /// Attack damage above the champion's base at its level (items, runes and buffs). A hero's base is its current form's
    /// (<c>StatCharacterName</c>: Nidalee's cougar has its own base damage, which the stat calculator uses).
    /// </summary>
    internal double BonusAttackDamage(Entity entity) => entity.UnitType == UnitType.Hero
        ? entity.AttackDamage - Spells.Data.StatsAtLevel(Spells.Data.Champion(entity.StatCharacterName), entity.Level).AttackDamage
        : 0.0;

    /// <summary>
    /// <c>GetFlatPhysicalDamageMod</c>, the flat physical modifier APBonusDamageToTowers subtracts from AP / 2.5. Reconstruction (batch 203):
    /// a Hallucinate clone copies its owner's attack damage at spawn (SharedCloneService) and has no champion base of its own, so its modifier
    /// is its attack damage above Shaco's base at the clone's level. The retail clone's own modifier is not in the shipped data.
    /// </summary>
    internal double FlatPhysicalDamageMod(Entity entity) => entity.Kind == EntityKind.ShacoClone
        ? entity.AttackDamage - Spells.Data.StatsAtLevel(Spells.Data.Champion("Shaco"), entity.Level).AttackDamage
        : BonusAttackDamage(entity);

    /// <summary>
    /// The attack-speed fraction above the champion's base at its level (items, runes and buffs), the pool
    /// <c>Ardor.luaobj</c> scales. <c>Entity.AttackSpeedMod</c> is the absolute attacks-per-second the stat calculator
    /// built as base + this pool, so the subtraction reads the pool back exactly.
    /// </summary>
    internal double BonusAttackSpeedPercent(Entity entity) => entity.UnitType == UnitType.Hero
        ? entity.AttackSpeedMod - Spells.Data.StatsAtLevel(Spells.Data.Champion(entity.ChampionName), entity.Level).AttackSpeedMod
        : 0.0;

    public bool GetStatus(BbGetStatus status, object? unit)
    {
        var entity = UnitOrSelf(unit);
        return status switch
        {
            BbGetStatus.IsObjectHero => entity.UnitType == UnitType.Hero,
            BbGetStatus.IsObjectAI => IsObjectAI(entity),
            BbGetStatus.IsTurretAI => entity.UnitType == UnitType.Turret,
            BbGetStatus.IsMelee => IsMelee(entity),
            BbGetStatus.IsRanged => !IsMelee(entity),
            BbGetStatus.GetInvulnerable => entity.AnyBuffOrStatus(buff => buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable),
            BbGetStatus.GetStealthed => entity.AnyBuffOrStatus(buff => buff.Payload.Stealthed),
            BbGetStatus.GetTargetable => !entity.AnyBuffOrStatus(buff => buff.Payload.Untargetable),
            BbGetStatus.GetGhostProof => entity.GhostProof,
            // The render-state bits the setters hold (SetNoRender, SetGhosted, SetForceRenderParticles): the end-game latch counts as NoRender too.
            BbGetStatus.GetNoRender => entity.NoRender || entity.AnyBuffOrStatus(buff => buff.Payload.NoRender),
            BbGetStatus.GetGhosted => entity.AnyBuffOrStatus(buff => buff.Payload.Ghosted || buff.Payload.Stealthed),
            BbGetStatus.GetForceRenderParticles => entity.AnyBuffOrStatus(buff => buff.Payload.ForceRenderParticles),
            // The CharacterState bits the control scripts drive (CharacterState.cs): the same rule as the cast and attack gates.
            BbGetStatus.GetCanMove => CharacterState.Of(entity).CanMove,
            BbGetStatus.GetCanCast => CharacterState.Of(entity).CanCast,
            BbGetStatus.GetCanAttack => CharacterState.Of(entity).CanAttack,
            BbGetStatus.GetTaunted => CharacterState.Of(entity).Taunted,
            BbGetStatus.GetFeared => CharacterState.Of(entity).Feared,
            BbGetStatus.GetSleep => CharacterState.Of(entity).Asleep,
            BbGetStatus.GetSuppressed => CharacterState.Of(entity).Suppressed,
            BbGetStatus.GetNearSight => entity.Buffs.Any(buff => buff.Type == BuffType.NearSight),
            // The bot scripts' IsMoving (BotController): an unfinished route.
            BbGetStatus.IsMoving => entity.Waypoints.Count > 0 &&
                (entity.Waypoints.Count > 1 || Distance(entity.Position, entity.Waypoints[0]) > 3.0) ||
                entity.Target is Position2 goal && Distance(entity.Position, goal) > 3.0,
            _ => throw Unbound(status.ToString(), "M2"),
        };
    }

    public void SetStatus(BbSetStatus status, object? unit, object? value)
    {
        NoProbe();
        // A character script's stats sample (UpdateSelfBuffStats) collects a status like an Inc*Mod; the sample's own run writes
        // it onto the unit when the hook ends (BbHookRouter.RunUpdateSelfBuffStats), which is the one place the unit-state readers
        // (visibility, targeting, the movement and attack gates) find it. An item copy's sample has no such writer — its payload is
        // the stat calculator's alone — so a status there stays loud rather than dropped silently.
        if (HasFrame && Frame.Hook == "UpdateSelfBuffStats" && Frame.ItemId != 0)
            throw Unbound($"{status} in an item script's stats sample", "phase 3");
        // Inside a BuffOnUpdateStats sample the status is collected into the buff payload (BbBuffRuntime), like an Inc*Mod.
        if (HasFrame && Frame.Stats is { } collector)
        {
            // A status on another unit (SkarnerImpale's SetCanAttack on its Attacker, written by the victim's stats hook) is kept apart and
            // held by an internal buff of that unit (BbBuffRuntime.ApplyForeign), as another unit's stats are. Reconstruction (batch 318):
            // the shipped files do not show how retail holds another unit's status across the hook's samples; the carrier is the stand-in.
            var statTarget = UnitOrSelf(unit);
            if (!ReferenceEquals(statTarget, Frame.Owner))
            {
                collector.SetForeign(statTarget, status, BbRuntime.Truthy(value));
                return;
            }
            collector.Set(status, BbRuntime.Truthy(value));
            return;
        }
        // Outside it the flag is rebuilt from the stats hooks every update (retail resets and re-collects them), so the
        // activation/deactivation calls of Rune Prison, Root and Intervention add nothing. Reconstruction, same rule as Inc*Mod.
        if (HasFrame && Frame.Buff is { } running && StatsHookCalls(running.Script, "BBSetStatus", "Status", status.ToString())) return;
        // The status bit of a unit (E02): written from a buff's activation (XenZhaoSweep, Poppy's charge), any other hook of a buff (Jarvan's
        // Cataclysm, Caitlyn's trap, Renekton's dashes), a spell's own hooks (Leona's Zenith Blade, Talon's Shadow Assault, Maokai's saplings), a missile or
        // channel end (Pantheon's jump) and on units other than the running hook's (a spawned pillar, wall or sapling). BbBuffRuntime.WriteStatus
        // holds it with the running buff or the unit's script status, and releases it everywhere on the unit when the script writes the rest value.
        if (BbBuffRuntime.CanApplyStatus(status))
        {
            var target = UnitOrSelf(unit);
            Buffs.WriteStatus(target, status, BbRuntime.Truthy(value), HasFrame ? Frame.Buff : null, HasFrame ? Frame.Hook : "");
            return;
        }
        // Clearing a status on another unit than the running hook's (HallucinateApplicator clears SetStealthed on the clone it has just made, which
        // the native copied from a stealthed Shaco): the payload model holds a status only through a buff, and a new unit holds none, so there is
        // nothing to clear. Setting one on another unit has no holder and stays unbound.
        if (!BbRuntime.Truthy(value) && HasFrame && !ReferenceEquals(UnitOrSelf(unit), Frame.Owner)) return;
        // ResistantSkin.luaobj's OnBuffActivate clears the Baron's control statuses on itself (SetFeared, SetNearSight, SetSleep, SetTaunted,
        // SetCharmed, SetNetted; blocks 0338-0432, SrcValue false, TargetVar Owner). This engine keeps those controls as native buffs, not as
        // status bits, so the clear removes the buffs of that type from this unit only; a charm clear finds nothing (BuffType has no Charm).
        // PropelSpellCaster marks its placemarker with the call-for-help statuses; the placemarker is neither targetable nor a camp member,
        // so no call for help reads them.
        if (!BbRuntime.Truthy(value) && status is (BbSetStatus.SetFeared or BbSetStatus.SetNearSight or BbSetStatus.SetSleep
            or BbSetStatus.SetTaunted or BbSetStatus.SetCharmed or BbSetStatus.SetNetted))
        {
            if (ControlBuffType(status) is { } controlType) Buffs.RemoveType(UnitOrSelf(unit), (int)controlType);
            return;
        }
        if (status is BbSetStatus.SetCallForHelpSuppresser or BbSetStatus.SetIgnoreCallForHelp or BbSetStatus.SetSuppressCallForHelp) return;
        throw Unbound(status.ToString(), "M3");
    }

    // ------------------------------------------------------------------------------------------------------------
    // IBbResourceHost
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The native control buff a clear of a control status removes (BuffType), or null for a status this engine keeps no buff for.</summary>
    private static BuffType? ControlBuffType(BbSetStatus status) => status switch
    {
        BbSetStatus.SetFeared => BuffType.Fear,
        BbSetStatus.SetNearSight => BuffType.NearSight,
        BbSetStatus.SetSleep => BuffType.Sleep,
        BbSetStatus.SetTaunted => BuffType.Taunt,
        BbSetStatus.SetNetted => BuffType.Net,
        _ => null,
    };

    public bool HasPARType(object? unit, double? parType) =>
        parType is { } type && UnitOrSelf(unit) is var entity && PowerType(entity) == (BbParType)(int)type;

    /// <summary>
    /// The resource a unit carries: heroes by character data, other units only when their own character record names one. The
    /// names are the client's <c>PARTypeHelper</c> map (Mana, Energy, Souls, Shield, Other, each its <c>PrimaryAbilityResourceType</c>),
    /// so a script's <c>PARType</c> matches only the pool the character really has: Riven's character script raises <c>PAR_OTHER</c>
    /// on a "Shield" champion, which the client's PAR helpers refuse as another pool.
    /// </summary>
    internal BbParType? PowerType(Entity entity)
    {
        if (entity.UnitType == UnitType.Hero)
            // The script constants are the client's PrimaryAbilityResourceType numbers; the stat character is the pushed form while a form is up.
            return (BbParType)(int)Spells.Data.Champion(entity.StatCharacterName).PrimaryResource;
        // A non-hero has the pool of its character record (the ward scripts tick BBIncPAR PAR_SHIELD -1 a second against theirs, so a placed
        // ward's shield bar is its remaining life). The loader reads Data*PARType with the default "MP", the
        // PARTypeHelper map's MANA name, so a record that names none is a mana unit (batch 320; PrimaryResource parses an absent key as
        // Mana). The character name of a non-hero is its own Name (see ChildSpellName above); a body the engine made up has no record and
        // no pool.
        return Spells.Data.OptionalChampion(entity.Name) is { } record ? (BbParType)(int)record.PrimaryResource : null;
    }

    /// <summary>
    /// <c>BBGetPAROrHealth</c>. The health getters ignore <c>PARType</c> (<c>BBGetPAROrHealth Function=GetHealth PARType=PAR_MANA</c> is just health: every
    /// health cost of Vladimir and Dr. Mundo reads it that way). <c>GetPARCurrent</c>, <c>GetPARMax</c> and <c>GetPARPercent</c> answer 0 when the type
    /// asked is not the unit's own (AB 2649241 to 2649283), and the percent is 0 for a pool whose maximum is 0 or less; a unit with no character type at
    /// all answers as a mana unit.
    /// </summary>
    public object? GetResource(BbGetResource function, object? unit, double? parType)
    {
        var entity = UnitOrSelf(unit);
        var ownType = PowerType(entity) ?? BbParType.PAR_MANA;
        var matches = ownType == (BbParType)(int)(parType ?? 0.0);
        return function switch
        {
            BbGetResource.GetHealth => entity.Health,
            BbGetResource.GetMaxHealth => entity.MaxHealth,
            BbGetResource.GetHealthPercent => entity.MaxHealth > 0.0 ? entity.Health / entity.MaxHealth : 0.0,
            BbGetResource.GetPAR => matches ? entity.Mana : 0.0,
            BbGetResource.GetMaxPAR => matches ? entity.MaxMana : 0.0,
            BbGetResource.GetPARPercent => matches && entity.MaxMana > 0.0 ? entity.Mana / entity.MaxMana : 0.0,
            _ => throw Unbound(function.ToString(), "M2"),
        };
    }

    public double GetGold(BbCall call) => UnitOrSelf(call.Var("OwnerVar")).Gold;

    private ItemCatalog? _items;

    /// <summary>The spell name <c>BBGetSlotSpellInfo GetSlotSpellName</c> answers for an inventory slot (batch 149 step 4):
    /// the held item's own <c>SpellName</c> from its <c>Items/*.inibin</c> — <c>DeathfireGrasp</c>, <c>HextechSweeper</c>,
    /// <c>QuicksilverSash</c>, <c>shurelyascrest</c> …, exactly the name the shipped actives compare every slot against before
    /// they write that slot's cooldown. A slot with no item, or an item with no active spell, holds "".</summary>
    private string InventorySpellName(Entity hero, int index)
    {
        if ((uint)index >= (uint)hero.Inventory.Count || hero.Inventory[index] is not { } held) return "";
        return (_items ??= new ItemCatalog(Spells.Data.Paths)).Get(held.ItemId)?.SpellName ?? "";
    }
    /// <summary>
    /// <c>BBReincarnateHero</c> (<c>SummonerRevive</c> stands its owner up): the hero is a non-dead hero again, at its
    /// fountain with a fresh life, which is what the hand-written Revive asks the combat service for. A unit that is not
    /// a hero, or a hero already standing, is left alone.
    /// </summary>
    public void ReincarnateNonDeadHero(object? unit)
    {
        if (UnitOrSelf(unit) is { UnitType: UnitType.Hero } hero && !hero.Alive)
            Spells.Combat.RespawnImmediately(hero);
    }

    // ------------------------------------------------------------------------------------------------------------
    // IBbSpellHost
    // ------------------------------------------------------------------------------------------------------------

    public object? GetSlotSpellInfo(BbSlotSpellInfo function, object? owner, object? slot, double? spellbook, double? slotType)
    {
        var hero = UnitOrSelf(owner);
        var index = slot is double d ? (int)d : throw new BbRuntimeException("GetSlotSpellInfo: slot is not a number");
        var type = (BbSlotType)(int)(slotType ?? 0.0);
        var book = (BbSpellbook)(int)(spellbook ?? 0.0);
        var champion = type == BbSlotType.SpellSlots && book == BbSpellbook.SPELLBOOK_CHAMPION;
        var summonerBook = type == BbSlotType.SpellSlots && book == BbSpellbook.SPELLBOOK_SUMMONER && hero.UnitType == UnitType.Hero;
        // The inventory rows (batch 149 step 4): the spell name of the copy in the slot, the cooldown the slot is
        // running, and the level its spell runs at. Every shipped active loops the six slots this way to find the
        // copies of itself before it writes their cooldown (DeathfireGrasp's six BBIf blocks, QuicksilverSash's
        // BBWhile), so the answers have to be the held item's own.
        var inventory = type == BbSlotType.InventorySlots && book == BbSpellbook.SPELLBOOK_CHAMPION;
        return function switch
        {
            BbSlotSpellInfo.GetSlotSpellLevel when champion && (uint)index < hero.SpellRanks.Count => (double)hero.SpellRanks[index],
            BbSlotSpellInfo.GetSlotSpellCooldownTime when champion && (uint)index < hero.SpellCooldowns.Count => hero.SpellCooldowns[index],
            // The summoner spellbook (YorickRADelay lifts a short summoner cooldown to four seconds).
            BbSlotSpellInfo.GetSlotSpellCooldownTime when summonerBook && (uint)index < hero.SummonerCooldowns.Count => hero.SummonerCooldowns[index],
            BbSlotSpellInfo.GetSlotSpellName when champion => SpellNameOf(hero, index),
            // The slot record holds the display name ('Fortify'); the shipped scripts compare the answer to the script name
            // (FortifyCheck's `GetSlotSpellName == 'SummonerFortify'`). A name the loadout does not know ('TeleportCancel',
            // the placeholder a running Teleport channel leaves in the slot) is answered as it is stored. Guarded by the
            // summoner book so an inventory slot's own name below cannot be shadowed by the summoner spell in the same
            // index (batch 149 step 4: QuicksilverSash and HextechGunblade find their copies by name and never matched
            // slot 0 or 1 while this row answered the summoner spell for them).
            BbSlotSpellInfo.GetSlotSpellName when summonerBook && (uint)index < hero.SummonerSpells.Count =>
                SummonerLoadout.TryCanonicalFromDisplay(hero.SummonerSpells[index]) ?? hero.SummonerSpells[index],
            // Reconstruction (batch 203): a unit with no summoner book names no summoner spell. The Hallucinate clone is one: the shipped
            // APBonusDamageToTowers reaches it (HallucinateApplicator), and its OnBuffActivate asks the summoner slots for SummonerFortify, which
            // that clone must answer as empty (FortifyCheck is not given to it) rather than fail the script.
            BbSlotSpellInfo.GetSlotSpellName when type == BbSlotType.SpellSlots && book == BbSpellbook.SPELLBOOK_SUMMONER && hero.UnitType != UnitType.Hero => "",
            BbSlotSpellInfo.GetSlotSpellName when inventory => InventorySpellName(hero, index),
            BbSlotSpellInfo.GetSlotSpellCooldownTime when inventory && (uint)index < hero.ItemCooldowns.Count => hero.ItemCooldowns[index],
            // Batch 331: the script helper's slot-spell-level native maps an inventory slot
            // to 4.. and an extra slot to 42.. and answers 0 for any slot outside 0-3 before it reads a spellbook, so an inventory or extra slot
            // answers 0 whatever it holds (the book's init leaves the extra slots at level 0 too). The one shipped reader is
            // GalioRighteousGustMissile (ExtraSlots 3, an empty slot of Galio's).
            BbSlotSpellInfo.GetSlotSpellLevel when inventory || type == BbSlotType.ExtraSlots => 0.0,
            _ => throw Unbound($"{function} for slot {index}", "M2"),
        };
    }

    public void SetSlotSpellCooldownTime(object? owner, object? slot, double? spellbook, double? slotType, object? cooldown)
    {
        NoProbe();
        var hero = UnitOrSelf(owner);
        var index = slot is double d ? (int)d : throw new BbRuntimeException("SetSlotSpellCooldownTime: slot is not a number");
        if ((BbSlotType)(int)(slotType ?? 0.0) == BbSlotType.SpellSlots && (BbSpellbook)(int)(spellbook ?? 0.0) == BbSpellbook.SPELLBOOK_SUMMONER &&
            hero.UnitType == UnitType.Hero && (uint)index < hero.SummonerCooldowns.Count)
        {
            // The summoner spellbook: the cooldown the slot record holds and the 0x8A with the summoner bit (YorickRADelay).
            var summonerSeconds = Math.Max(0.0, BbRuntimeNames.Number(cooldown));
            hero.SummonerCooldowns[index] = summonerSeconds;
            Spells.Effects.SendCooldown(hero, index, summonerSeconds, summoner: true);
            return;
        }
        if ((BbSlotType)(int)(slotType ?? 0.0) == BbSlotType.InventorySlots &&
            (BbSpellbook)(int)(spellbook ?? 0.0) == BbSpellbook.SPELLBOOK_CHAMPION && (uint)index < hero.ItemCooldowns.Count)
        {
            // The inventory rows (batch 149 step 4): the slot's own record and the 0x8A with the slot + 4 wire number,
            // the same pair the item-use path writes (ItemService.SetCooldown).
            var itemSeconds = Math.Max(0.0, BbRuntimeNames.Number(cooldown));
            hero.ItemCooldowns[index] = itemSeconds;
            Spells.Effects.SendCooldown(hero, index + BbNativeHandlers.InventorySlotStart, itemSeconds);
            return;
        }
        if ((BbSlotType)(int)(slotType ?? 0.0) != BbSlotType.SpellSlots || (BbSpellbook)(int)(spellbook ?? 0.0) != BbSpellbook.SPELLBOOK_CHAMPION ||
            (uint)index >= hero.SpellCooldowns.Count)
            throw Unbound($"SetSlotSpellCooldownTime for slot {index}", "M2");
        // DIAGNOSTICS: the /cooldowns playtest cheat has no retail counterpart; every cooldown the engine itself sets honours it, so a script's
        // own (Power Fist's, Ryze's) must too.
        var seconds = hero.CooldownsDisabled ? 0.0 : Math.Max(0.0, BbRuntimeNames.Number(cooldown));
        hero.SpellCooldowns[index] = seconds;
        Spells.Effects.SendCooldown(hero, index, seconds);
    }

    /// <summary>
    /// <c>BBSetSpellCastRange</c>: only the <c>AdjustCastInfo</c> hook of a spell script (Buster Shot, Flash) uses it, to say how far the cast
    /// reaches this time; the router reads the stored range back when the hook has run (<see cref="BbHookRouter.RunAdjustCastInfo"/>).
    /// </summary>
    public void SetSpellCastRange(double? range)
    {
        if (!HasFrame || Frame.Hook != "AdjustCastInfo") throw Unbound("SetSpellCastRange outside AdjustCastInfo", "W25c");
        Frame.CastRange = range;
    }
    public void SetCanCastWhileDisabled(bool canCast) => throw Unbound("SetCanCastWhileDisabled", "M2");

    public object? GetCastInfo(BbCastInfo info, object? unit)
    {
        var frame = Frame;
        return info switch
        {
            BbCastInfo.GetPARCost => frame.ManaCost,
            BbCastInfo.GetCastSpellTargetPos when frame.CastPosition is { } at => BbTable.Point(at.X, at.Y, at.Z),
            BbCastInfo.GetSourceType => GetSourceType(),
            BbCastInfo.GetCasterID when frame.Caster is { } caster => (double)caster.NetId,
            BbCastInfo.GetSpellSlot => (double)frame.SpellSlot,
            BbCastInfo.GetSpellName when frame.SpellName is { } spellName => spellName,
            BbCastInfo.GetIsAttackOverride => frame.IsAttackOverride,
            BbCastInfo.GetCastSpellTargetsHitPlusOne => (double)(frame.TargetsHit + 1),
            // The client stores the spell level zero-based (SpellCastInfo), so "level plus one" is the rank the ByLevel tables index.
            BbCastInfo.GetCastSpellLevelPlusOne when frame.SpellRank >= 1 => (double)frame.SpellRank,
            _ => throw Unbound(info.ToString(), "M2"),
        };
    }

    // ------------------------------------------------------------------------------------------------------------
    // IBbGameHost and IBbOrderHost
    // ------------------------------------------------------------------------------------------------------------

    public double GetTime() => Spells.CurrentTime;
    /// <summary>
    /// Lua's <c>math.random()</c> (batch 222): one step of the match's CRT state, drawn as <c>LuaMathLibrary</c> draws it,
    /// <c>(rand() % RAND_MAX) / RAND_MAX</c>. Retail's BuildingBlocks random runs in Lua, so it reads the scripts' own stream. A hook
    /// frame's forced value (a crit roll) takes no step. ponytail: the LCG step is repeated from LuaMathLibrary's private CrtRand; one
    /// shared CrtState method when that file is next touched.
    /// </summary>
    public double Random()
    {
        if (HasFrame && Frame.ForcedRandom is { } forced) return forced;
        var crt = Spells.World.LuaRandom;
        crt.HoldRand = unchecked(crt.HoldRand * 214013u + 2531011u);
        return (double)(((crt.HoldRand >> 16) & 0x7fff) % 0x7fff) / 0x7fff;
    }
    public void Say(object? unit, string? text) { /* no chat output in the headless engine: retail Say is a debug aid */ }
    public void Alert(string? text) { /* Lua _ALERT: a debug aid with no gameplay effect */ }
    /// <summary>
    /// <c>BBIssueOrder</c> (the base function hands over the target's position and the target): the orders the shipped move scripts end
    /// with. <c>AI_ATTACKTO</c> (a pulled or kicked hero turns on its target: Rocket Grab's landing, Resonating Strike, Shadow Dance) and
    /// <c>AI_MOVETO</c> are given to heroes through the hero order service, and <c>AI_HOLD</c> stands a hero (batch 318). Reconstruction: the orders of other units (a taunted minion or
    /// pet answers through its own AI) and the other order kinds raise "not bound".
    /// </summary>
    public void IssueOrder(object? whom, double? order, object? position, object? target)
    {
        NoProbe();
        var unit = UnitOrNull(whom);
        if (unit is null) return;
        var kind = order is { } number ? (BbAiOrder)(int)number : BbAiOrder.AI_ORDER_NONE;
        // Reconstruction (batch 318; SkarnerImpale's TargetExecute orders its own caster AI_HOLD on the cast): the hold of a hero stands him where he
        // is and lets him attack only what is in range (HeroHold, the state the player's hold key sets). Retail's Hero.lua OnOrder has no hold branch
        // (ProductionMatchComposition sends the key natively); a move to his own point is the stand, and the flag keeps him from chasing.
        if (unit.UnitType == UnitType.Hero && kind == BbAiOrder.AI_HOLD)
        {
            if (unit.Alive)
            {
                Spells.HeroOrders?.StartMove(unit, unit.Position);
                unit.HeroHold = true;
                // Reconstruction (batch 450): the hold ends the hero's running channel as a stop (source Move, as an order's). The shipped
                // OdinCaptureChannel damage branch clears the channel buff and then issues AI_HOLD (DATA/Spells/OdinCaptureChannel.luaobj,
                // dump OdinCaptureChannel.txt:378-383), and no other step ends a channel that damage interrupts.
                Spells.StopChanneling(unit, "order");
            }
            return;
        }
        // Reconstruction (batch 428, the Maokai sapling): a spawned unit's AI_MOVETO (MaokaiSaplingMine issues it every quarter second from its second spring, to
        // the foe it sprang at) walks the unit the way a pet walks: the route of MovementSystem.SetMoveOrder, published to its clients (TibbersAiSystem). Before
        // this the order threw, so the sapling never ran at its foe (owner, native, 28 October 2026). Retail's order service for a non-hero is not recovered.
        if (unit.UnitType != UnitType.Hero && kind == BbAiOrder.AI_MOVETO)
        {
            if (!unit.Alive) return;
            // Reconstruction (batch 428): a rooted or non-moving unit keeps still (ControlOf reads the same CanMove the movement system reads).
            if (!BuffLifecycle.ControlOf(unit).CouldMove) return;
            var route = Spells.Movement.SetMoveOrder(unit.NetId, Ground(position));
            if (Spells.Effects.IsReferenceReady(unit))
                Spells.Effects.SendWaypoints(unit, route.Count > 0 ? route : Spells.Movement.InstallStop(unit), Spells.World.Clock.SyncId());
            return;
        }
        // Reconstruction (batch 449): AI_ORDER_NONE on a hero is a no-op here. Shipped scripts issue it to their owner (OdinCaptureImmobile's
        // deactivate, DATA/Spells/OdinCaptureImmobile.luaobj; batch 450: the channel's own damage stop issues AI_HOLD, not this order). The retail order service is not recovered: a move to his own point
        // would be a Move stop for a running channel (BbChannels.cs:134), so the clear does not move him or end the channel.
        if (unit.UnitType == UnitType.Hero && kind == BbAiOrder.AI_ORDER_NONE) return;
        if (unit.UnitType != UnitType.Hero || kind is not (BbAiOrder.AI_ATTACKTO or BbAiOrder.AI_MOVETO))
            throw Unbound($"IssueOrder {kind} for a {unit.UnitType}", "W22");
        if (!unit.Alive) return;
        if (kind == BbAiOrder.AI_MOVETO)
        {
            Spells.HeroOrders?.StartMove(unit, Ground(position));
            return;
        }
        // Reconstruction (playtest B3 #10, the hand-written Resonating Strike; retail's rule is not recovered): a cast the player queued during the move is his next order and
        // replaces the script's attack-to; combat runs before the spell phase, so the swing's 0x0D would precede the cast.
        if (unit.PendingCast is { Request.IsSummoner: false }) return;
        // Reconstruction: the script orders the attack unconditionally (retail's order-service refusal is native and not recovered); the emulator's order service takes no order on an unseen, untargetable
        // or dead target (the same three tests the hand-written Rocket Grab applies).
        if (UnitOrNull(target) is { Alive: true } victim && Spells.CanSeeTarget(unit, victim) && Spells.Combat.IsTargetable(victim, unit))
            Spells.HeroOrders?.StartAttack(unit, victim);
    }
    public void DefUpdateAura(object? center, double? range, double? unitScan, string? buffName) => throw Unbound("DefUpdateAura", "M3");

    internal static BbRuntimeException Unbound(string what, string milestone) =>
        new($"{what} is not bound by the script engine (owner: {milestone})");
}
