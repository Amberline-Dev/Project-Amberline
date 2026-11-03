using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The construction half of the Pet.lua clones: Shaco's hallucination, Yorick's revenant, LeBlanc's
/// image, Wukong's decoy and Mordekaiser's ghost, the units <c>BBCloneUnitPet</c> asks for. Their AI
/// is the shipped <c>Pet.luaobj</c> on <see cref="PetAiService"/>, so this service only builds the
/// unit and copies the source's combat record onto it.
/// </summary>
public sealed class SharedCloneService
{
    private readonly WorldState _world;
    private readonly CombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly GameData _data;

    public SharedCloneService(WorldState world, MovementSystem movement,
        CombatService combat, ISpellEffectSink effects, GameData? data = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(movement);
        if (!ReferenceEquals(world, movement.World))
            throw new ArgumentException("MovementSystem must belong to the clone world", nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _data = data ?? GameData.Default;
    }

    public Entity Spawn(Entity owner, Entity source, Position2 position, CloneKind kind,
        double health, string buffName, double duration,
        string displayName, bool showMinimapIcon = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(health) || health < 0.0) throw new ArgumentOutOfRangeException(nameof(health));
        if (!double.IsFinite(duration) || duration < 0.0) throw new ArgumentOutOfRangeException(nameof(duration));
        var entityKind = kind switch
        {
            CloneKind.ShacoClone => EntityKind.ShacoClone,
            CloneKind.YorickRevenant => EntityKind.YorickRevenant,
            // LeBlanc's image, Wukong's decoy and Mordekaiser's ghost (W26c): Pet.lua clones that are neither Shaco's nor Yorick's.
            CloneKind.LeblancImage or CloneKind.MonkeyKingDecoy or CloneKind.MordekaiserGhost => EntityKind.ClonePet,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var movementBaseline = kind == CloneKind.YorickRevenant
            ? _data.StatsAtLevel(_data.Champion(source.StatCharacterName), source.Level).MoveSpeed
            : source.Speed;
        // The clone belongs to the team of its owner: the same unit as the source's for Shaco, LeBlanc, Wukong and Yorick (who clones a fallen
        // ally), and the owner's side for Mordekaiser's ghost, whose source is a fallen enemy.
        // Batch 467: the source's penetration without its buffs' part. The clone's own copies of the items put their buffs on the clone
        // again, and a non-hero's buffs now count (BuffLifecycle.RecomputeNpcPenetration); the copied part is the rest.
        var penetration = Amberline.Simulation.Buffs.BuffLifecycle.PenetrationOutsideBuffs(source);
        var clone = new Entity(_world.Allocate(UnitType.Monster), entityKind,
            UnitType.Monster, owner.Team, position)
        {
            // Pet.lua uses the source champion's model/name.  The display name
            // belongs to the spawn metadata (Hallucinate/YorickRARevive).
            Name = source.ChampionName,
            SkinId = source.SkinId,
            // Python clone entities retain the default empty champion_name;
            // the source model is carried by Name/spawn metadata.
            ChampionName = string.Empty,
            OwnerNetId = owner.NetId,
            Level = source.Level,
            Health = health,
            MaxHealth = source.MaxHealth,
            Mana = source.Mana,
            MaxMana = source.MaxMana,
            Created = false,
            Visible = false,
            Speed = kind == CloneKind.YorickRevenant
                ? Math.Max(0.0, movementBaseline + owner.FlatMovementSpeedMod) : source.Speed,
            // Revenants retain the template's base speed; their controller
            // synchronizes only Yorick's flat modifier. Shaco keeps his snapshot.
            MovementSpeedBaseline = movementBaseline,
            Radius = source.Radius,
            CombatRadius = source.CombatRadius,
            SelectionRadius = source.SelectionRadius,
            AttackRange = source.AttackRange,
            AttackDamage = source.AttackDamage,
            Armor = source.Armor,
            SpellBlock = source.SpellBlock,
            AttackSpeedMod = source.AttackSpeedMod,
            AttackPeriod = source.AttackPeriod,
            AttackWindup = source.AttackWindup,
            AttackTimingBaseline = CloneTimingBaseline(source),
            MissileSpeed = source.MissileSpeed,
            BasicAttackAnimationProbabilities = source.BasicAttackAnimationProbabilities,
            AbilityPower = source.AbilityPower,
            CritChance = source.CritChance,
            CritMultiplier = source.CritMultiplier,
            Dodge = source.Dodge,
            ArmorPenetrationFlat = penetration.ArmorFlat,
            ArmorPenetrationPercent = penetration.ArmorPercent,
            MagicPenetrationFlat = penetration.MagicFlat,
            MagicPenetrationPercent = penetration.MagicPercent,
            AcquisitionRange = AcquisitionRangeFor(source),
            NodeId = 0x40,
        };
        clone.PetState = new PetState(kind, owner.NetId)
        {
            CloneSourceNetId = source.NetId,
            SpawnBuffName = buffName,
            SpawnDuration = duration,
            ShowMinimapIcon = showMinimapIcon,
            BornAt = _combat.CurrentTime,
            Rank = Math.Max(1, source.SpellRanks.Count > 3 ? source.SpellRanks[3] : 1),
        };
        clone.SpellRanks = source.SpellRanks.ToList();
        clone.Inventory = source.Inventory.Select(slot => slot?.Clone()).ToArray();
        _world.Register(clone);

        var snapshot = new SpawnPetSnapshot(
            owner.NetId, clone.NetId, checked((byte)clone.NodeId), clone.Position,
            string.IsNullOrEmpty(source.Name) ? source.ChampionName : source.Name,
            source.ChampionName,
            CloneNetId: source.NetId, SkinId: clone.SkinId, Rank: 1,
            Duration: checked((float)duration), BuffName: buffName,
            CopyInventory: true, ClearFocusTarget: true,
            AiScript: "Pet.lua", ShowMinimapIcon: showMinimapIcon);
        _effects.SendPetSpawn(clone, snapshot);
        clone.Created = true;
        _effects.SendInitialVisibility(clone);
        _effects.SendStats(clone);
        if (clone.Health != clone.MaxHealth)
            _effects.SendHealth(clone, clone.Health);
        return clone;
    }

    private AttackTimingBaseline CloneTimingBaseline(Entity source)
    {
        if (source.AttackTimingBaseline is { } baseline)
            return baseline with { BaseAttackSpeed = source.AttackSpeedMod };

        var attackSpeed = double.IsFinite(source.AttackSpeedMod) && source.AttackSpeedMod > 0.0
            ? source.AttackSpeedMod : 1.0;
        if (source.UnitType == UnitType.Hero)
        {
            var data = _data.Champion(source.StatCharacterName).Raw;
            var delayOffset = Number(data, "AttackDelayOffsetPercent");
            var castFraction = Math.Max(0.0, 0.3 + Number(data, "AttackDelayCastOffsetPercent"));
            var basePeriod = 1.6 * (1.0 + delayOffset);
            return new AttackTimingBaseline(attackSpeed, basePeriod, castFraction);
        }

        var period = double.IsFinite(source.AttackPeriod) ? source.AttackPeriod : 0.0;
        var fraction = period > 0.0 && double.IsFinite(source.AttackWindup)
            ? Math.Max(0.0, source.AttackWindup / period) : 0.0;
        return new AttackTimingBaseline(attackSpeed, period * attackSpeed, fraction);
    }

    private static double Number(InibinData data, string key)
    {
        var hash = InibinReader.KeyHash("Data*" + key);
        return data.TryGetValue(hash, out var value) && value.IsNumeric ? value.AsDouble() : 0.0;
    }

    private double AcquisitionRangeFor(Entity source)
    {
        if (!string.IsNullOrWhiteSpace(source.ChampionName))
        {
            try
            {
                var raw = _data.Champion(source.ChampionName).Raw;
                var key = InibinReader.KeyHash("Data*AcquisitionRange");
                if (raw.TryGetValue(key, out var value) && value.IsNumeric)
                    return value.AsDouble();
            }
            catch (InibinException)
            {
                // Synthetic test entities can use an unshipped champion name;
                // their already-hydrated range is the authoritative value.
            }
        }
        return source.AcquisitionRange;
    }
}
