using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The spawn family of the BuildingBlocks natives (Phase 3, W22): <c>BBSpawnMinion</c> and <c>BBCloneUnitPet</c> (<c>BBSpawnPet</c>, Annie's
/// Tibbers, is <c>BbPetSpawn</c>). Every unit is created through the pet and minion service that already owns its kind
/// (<see cref="BbPetServices"/>, <see cref="CreateNidaleeTrap"/>); only a unit no service owns takes the generic path.
/// <para>
/// Client evidence. BBCloneUnitPet is a real parse ending in the creation packet 0x3C (<c>DoSpawnPet</c>):
/// it reads <c>UnitToClone</c>, <c>Pos</c>, <c>GoldRedirectTarget</c>, <c>Owner</c> (the unit slot), <c>Duration</c> (the shared number
/// getter), <c>CopyItems</c> (default true), <c>ShowMinimapIcon</c> (default true), <c>Level</c>, <c>HealthBonus</c>, <c>DamageBonus</c> (plain
/// integers, the integer getter builds no "ByLevel" key), <c>Buff</c> and <c>AiScript</c> (default "Pet.lua"). BBSpawnMinion's handler is a bare return, so its parameters come from the 122 shipped call sites
/// (Reconstruction): <c>Name</c>, <c>Skin</c>, <c>AiScript</c>, <c>PosVar/PosVarTable</c>, <c>Team</c> and <c>TeamVar/TeamVarTable</c>
/// (a team number from <c>BBGetTeamID</c>, which wins), the status flags <c>Stunned Rooted Silenced Invulnerable MagicImmune
/// IgnoreCollision IsWard Placemarker</c>, <c>VisibilitySize</c>, <c>DestVar</c> and <c>GoldRedirectTargetVar</c>.
/// </para>
/// </summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>The natives of this family <see cref="ExecuteSpawn"/> runs.</summary>
    private static readonly HashSet<string> SpawnBound = new(StringComparer.Ordinal)
    {
        "BBSpawnMinion", "BBCloneUnitPet",
    };

    /// <summary>
    /// The <c>Name</c> every shipped trap spawn passes (Teemo's <c>BantamTrap</c>, Caitlyn's <c>CaitlynYordleTrap</c>, Nidalee's
    /// <c>Bushwhack</c>: a copy of one call). Teemo's trap is stealthed by its own script (a ward plus the shared <c>Stealth</c> buff,
    /// like Shaco's box). The other two shipped scripts add no stealth anywhere (neither the call, the unit's character record nor a buff).
    /// <see cref="SpawnMinion"/> stealths only Nidalee's trap (skin <c>Nidalee_Spear</c>, NOT RETAIL, owner request of 19 October 2026); Caitlyn's
    /// follows her shipped script and is visible to the enemy team (owner decision, 28 October 2026).
    /// </summary>
    internal const string TrapUnitName = "Noxious Trap";

    /// <summary>Runs <paramref name="call"/> when it belongs to this family; false lets the main switch go on.</summary>
    private bool ExecuteSpawn(BbCall call)
    {
        switch (call.Function)
        {
            case "BBSpawnMinion": SpawnMinion(call); return true;
            case "BBCloneUnitPet": CloneUnitPet(call); return true;
            default: return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // What the family cannot run
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Why a spawn block cannot be run, or null when it can. It reads only the literal parameters, so <see cref="BbChampionModule"/> can
    /// refuse a spell slot whose cast hooks hold such a block before mana or cooldown are spent (the rule it applies to every unbound
    /// native), and the handlers apply the same rule when the block runs inside a buff hook the validation does not walk.
    /// </summary>
    internal static string? SpawnRefusal(BbBlock block)
    {
        switch (block.Function)
        {
            case "BBSpawnMinion":
            {
                var skin = block.Text("Skin");
                var ai = block.Text("AiScript") ?? string.Empty;
                // VisibilitySize is accepted: the unit becomes a vision source of that radius (Entity.VisibilitySize, W6 of 19 October 2026).
                if (skin is null) return null;
                var service = skin == "ShacoBox" ? "turret.lua"
                    : skin is "YorickDecayedGhoul" or "YorickRavenousGhoul" or "YorickSpectralGhoul" ? "YorickPHPet.lua" : null;
                if (service is not null)
                    return string.Equals(ai, service, StringComparison.OrdinalIgnoreCase) ? null
                        : $"BBSpawnMinion Skin={skin} AiScript={ai}: its service expects {service}";
                return SpawnedUnitAi.Runs(ai) ? null : $"BBSpawnMinion AiScript={ai} (Skin={skin}): no AI service owns it";
            }
            case "BBCloneUnitPet":
            {
                var buff = block.Text("Buff");
                var ai = block.Text("AiScript") ?? "Pet.lua";
                if (buff is not null && CloneKindOf(buff) is null)
                    return $"BBCloneUnitPet Buff={buff}: no clone family for it";
                if (!string.Equals(ai, "Pet.lua", StringComparison.OrdinalIgnoreCase)) return $"BBCloneUnitPet AiScript={ai}";
                if (block.Number("HealthBonus") is { } health && health != 0.0 || block.Number("DamageBonus") is { } damage && damage != 0.0)
                    return "BBCloneUnitPet with a health or damage bonus";
                return null;
            }
            // Batch 452: the vision natives bind every variant (SpecificUnitsClientOnly included), so nothing here refuses them.
            default:
                return null;
        }
    }

    /// <summary>
    /// The clone family a <c>BBCloneUnitPet</c> <c>Buff</c> names: the name the pet-spawn packet carries is the spell or buff the clone belongs to
    /// (Shaco's Hallucinate, Yorick's YorickRARevive, LeBlanc's LeblancMI, Wukong's MonkeyKingDecoyDummy, Mordekaiser's MordekaiserCotgPetSlow, which
    /// the script file spells MordekaiserCOTGPetSlow). Reconstruction: the family is what the controller buff the script adds next is looked up by.
    /// </summary>
    internal static CloneKind? CloneKindOf(string buff) => buff switch
    {
        "Hallucinate" => CloneKind.ShacoClone,
        "YorickRARevive" => CloneKind.YorickRevenant,
        "LeblancMI" => CloneKind.LeblancImage,
        "MonkeyKingDecoyDummy" => CloneKind.MonkeyKingDecoy,
        _ when string.Equals(buff, "MordekaiserCotgPetSlow", StringComparison.OrdinalIgnoreCase) => CloneKind.MordekaiserGhost,
        _ => null,
    };

    /// <summary>Raises the refusal of <see cref="SpawnRefusal"/> for the block that runs.</summary>
    private static void Refuse(BbCall call)
    {
        if (SpawnRefusal(call.Block) is { } reason) throw BbSpellHost.Unbound(reason, "W22");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shared readers
    // ------------------------------------------------------------------------------------------------------------

    private BbPetServices Pets => Spells.ScriptPets ?? throw BbSpellHost.Unbound("a spawn in a match without pet services", "W22");

    /// <summary>The pet's owner: the hook's <c>Owner</c> slot (both parses read "Owner" through the unit getter).</summary>
    private Entity SpawnOwner(BbCall call) =>
        call.Scope.Owner as Entity ?? (_host.HasFrame ? _host.Frame.Owner : null) ??
        throw new BbRuntimeException($"{call.Function} has no Owner unit");

    /// <summary>A position parameter (<c>PosVar</c> in <c>PosVarTable</c>): a point, or a unit whose position is meant (the DisconnectTimer spawn passes "Owner").</summary>
    private static Position2 SpawnPosition(BbCall call, string key)
    {
        var name = call.Value(key) ?? throw new BbRuntimeException($"{call.Function} has no {key}");
        return call.Table(key + "Table", create: false)[name] switch
        {
            BbTable point => BbSpellHost.Ground(point),
            Entity unit => unit.Position,
            _ => throw new BbRuntimeException($"{call.Function}: {key} '{name}' names no position"),
        };
    }

    /// <summary>
    /// The team of a spawned minion. Reconstruction (the native is a bare return in the client): <c>TeamVar</c> names a variable that holds a
    /// team number (<c>BBGetTeamID</c>) and wins over <c>Team</c>; <c>TEAM_CASTER</c> is the casting unit's team, <c>TEAM_OWNER</c> the
    /// Owner's, <c>TEAM_TARGET</c> the Target's; a concrete team is that team; <c>TEAM_UNKNOWN</c> with no variable falls back to the
    /// Owner's team.
    /// </summary>
    private static Team SpawnTeam(BbCall call, Entity owner)
    {
        if (call.Value("TeamVar") is { } variable &&
            call.Table("TeamVarTable", create: false)[variable] is double number &&
            TeamMapping.TryFromNative((int)number, out var fromVariable))
            return fromVariable;
        var caster = call.Scope.Vars["Caster"] as Entity;
        if (call.Value("Team") is not double team) return owner.Team;
        return (BbTeam)(int)team switch
        {
            BbTeam.TEAM_CASTER => (caster ?? owner).Team,
            BbTeam.TEAM_OWNER => owner.Team,
            BbTeam.TEAM_TARGET => (call.Scope.Vars["Target"] as Entity ?? owner).Team,
            _ => TeamMapping.TryFromNative((int)team, out var fixedTeam) ? fixedTeam : owner.Team,
        };
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBSpawnMinion
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBSpawnMinion</c>. Which service creates the unit follows the character record (<c>Skin</c>) and its AI script, the pair every
    /// shipped call site fixes:
    /// <list type="bullet">
    /// <item><c>ShacoBox</c> + <c>turret.lua</c>: <see cref="ShacoBoxService.SpawnForScript"/> (the box's own buff scripts add the rest).</item>
    /// <item><c>Nidalee_Spear</c> (the Noxious Trap): <see cref="CreateNidaleeTrap"/> and its replication template.</item>
    /// <item>any other <c>idle.lua</c> unit (<c>TestCube*</c>, <c>SpellBook1</c>, wards, walls, the Rally beacon, traps): the generic unit.</item>
    /// <item><c>OriannaBall</c> (a placemarker): the generic unit, announced to the clients through the ball projection (<see cref="AnnounceBall"/>).</item>
    /// <item><c>Yorick{Decayed,Ravenous,Spectral}Ghoul</c> + <c>YorickPHPet.lua</c>: the generic unit of kind
    /// <see cref="EntityKind.YorickGhoul"/>, a <see cref="CloneKind.YorickGhoul"/> pet (batch 164 dropped the hand-written
    /// <c>YorickGhoulService</c>: the <c>YorickSummon*</c> block is the only thing that asks for a ghoul, and its record is
    /// what builds it, like every other <c>BBSpawnMinion</c> unit).</item>
    /// <item><c>UncontrollablePet.lua</c> (Malzahar's voidlings), <c>Minion.lua</c> (Heimerdinger's turrets) and <c>Aggro.lua</c> (Wukong's Nimbus clones): the generic
    /// unit with the AI script on <see cref="Entity.AiScript"/>, run by <see cref="TibbersAiSystem"/> (the Pet.lua loop without the owner's orders, and the lane
    /// minion's target decisions without a lane), its swing the unit's own attack script (<c>IBbHookSink.OnUnitAttackHit</c>).</item>
    /// <item>refused by <see cref="SpawnRefusal"/>: an AI script no service runs and a foreign <c>turret.lua</c>. A non-zero <c>VisibilitySize</c> is the sight
    /// radius of the unit (<see cref="Entity.VisibilitySize"/>).</item>
    /// </list>
    /// The unit goes into <c>DestVar</c>; <c>GoldRedirectTargetVar</c> names the hero its kills credit (the unit's owner, which
    /// <c>CombatService.ResolveHeroOwner</c> follows).
    /// </summary>
    private void SpawnMinion(BbCall call)
    {
        Refuse(call);
        var owner = SpawnOwner(call);
        var skin = call.Value("Skin") as string ?? throw new BbRuntimeException("BBSpawnMinion has no Skin");
        var name = call.Value("Name") as string ?? skin;
        var position = SpawnPosition(call, "PosVar");
        var team = SpawnTeam(call, owner);
        var flags = new SpawnFlags(
            Flag(call, "Stunned", false), Flag(call, "Rooted", false), Flag(call, "Silenced", false), Flag(call, "Invulnerable", false),
            Flag(call, "MagicImmune", false), Flag(call, "IgnoreCollision", false), Flag(call, "IsWard", false), Flag(call, "Placemarker", false));
        // NOT RETAIL, owner request of 19 October 2026 (batch 113), for Nidalee's trap only (Skin Nidalee_Spear): the shipped Bushwhack script leaves its
        // trap in plain sight (no stealth in the call, in the character record or in any buff), yet the owner's recollection of the 2011 game is a trap
        // that enemies do not see until true sight (a Vision Ward, Oracle's Elixir, a turret's detection) reveals it or it springs. It is the same state
        // the shared Stealth buff sets for Shaco's box and Teemo's mushroom (SetStealthed), carried on the unit's script status with the call's flags, so
        // GetStealthed, the sight rule, true sight and the missile and targeting gates all read it. Caitlyn's CaitlynYordleTrap passes the same Name and
        // is not stealthed: her shipped script shows the trap to the enemy team (owner decision, 28 October 2026).
        if (skin == "Nidalee_Spear" && name == TrapUnitName && !flags.IsWard && !flags.Placemarker) flags = flags with { Stealthed = true };
        var visibility = Sum(call, "VisibilitySize");
        if (!(visibility >= 0.0) || !double.IsFinite(visibility))
            throw new BbRuntimeException($"BBSpawnMinion VisibilitySize {visibility} (Skin={skin}) is not a radius");
        var redirect = Unit(call, "GoldRedirectTargetVar");
        var now = Spells.CurrentTime;

        Entity unit;
        if (skin == "ShacoBox")
        {
            RequireOwnerTeam(call, team, owner, skin);
            unit = Pets.Boxes.SpawnForScript(owner, position, now);
            ApplyFlags(unit, flags);
        }
        else if (skin == "Nidalee_Spear")
        {
            unit = CreateNidaleeTrap(owner, position);
            unit.Team = team;
            ApplyFlags(unit, flags);
            // The trap's replication template carries the invulnerable, magic-immune, rooted and ignore-collision state of the call
            // (the shipped Bushwhack passes exactly those flags).
            if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendNidaleeTrap(unit, team);
            // Bushwhack.luaobj's BBSpawnMinion table passes VisibilitySize 0 and no sight spell (instructions 0383-0401), so the trap is
            // placed with no placement sight.
        }
        else
            unit = SpawnGeneric(owner, name, skin, position, team, flags, redirect, visibility, call.Value("AiScript") as string);
        unit.VisibilitySize = visibility;

        // GoldRedirectTarget: the hero the unit's kills credit. The services set the owner themselves (the box, the ghouls and the trap
        // belong to the caster); a different hero only changes the credit chain.
        if (redirect is { UnitType: UnitType.Hero } hero && unit.OwnerNetId == 0) unit.OwnerNetId = hero.NetId;
        StoreOut(call, "Dest", unit);
    }

    /// <summary>
    /// The Noxious Trap (<c>Nidalee_Spear</c>): a monster-type unit the replication layer knows as <see cref="EntityKind.NidaleeTrap"/>
    /// (its own spawn template and stat block). Health 1, and not <c>Protected</c>: the shipped scripts make it invulnerable and untargetable
    /// (the call's flags, <c>CharScriptNidalee_Spear</c>, the buff's statuses) and kill it with a 4000-damage self hit when the buff ends, which
    /// <c>CombatService.ApplyHit</c> would refuse on a protected unit.
    /// </summary>
    private Entity CreateNidaleeTrap(Entity owner, Position2 at)
    {
        var trap = new Entity(Spells.World.Allocate(UnitType.Monster), EntityKind.NidaleeTrap, UnitType.Monster, owner.Team, at)
        {
            Name = "Noxious Trap", Visible = true, Created = true,
            Health = 1.0, MaxHealth = 1.0, Speed = 0.0, Radius = 0.0, AttackRange = 0.0,
            OwnerNetId = owner.NetId,
        };
        Spells.World.Register(trap);
        return trap;
    }

    /// <summary>The service-owned units take their team from the owner they are created for; a call that names another team is not run.</summary>
    private static void RequireOwnerTeam(BbCall call, Team team, Entity owner, string skin)
    {
        if (team != owner.Team) throw BbSpellHost.Unbound($"{call.Function} {skin} on a team other than its owner's", "W22");
    }

    private readonly record struct SpawnFlags(bool Stunned, bool Rooted, bool Silenced, bool Invulnerable, bool MagicImmune,
        bool IgnoreCollision, bool IsWard, bool Placemarker, bool Stealthed = false)
    {
        /// <summary>
        /// The radius at or below which a unit of the IgnoreCollision flag still passes mobile bodies: the helper cubes (<c>TestCube</c> 1,
        /// <c>TestCubeRender</c> 1) and a trap (<c>Nidalee_Spear</c>, 0 here) that scripts spawn with the flag.
        /// </summary>
        internal const double MarkerRadius = 10.0;

        /// <summary>The status bits the flags set on a unit of <paramref name="radius"/>.</summary>
        public List<BbSetStatus> Statuses(double radius)
        {
            var statuses = new List<BbSetStatus>();
            if (Stunned) statuses.Add(BbSetStatus.SetStunned);
            if (Rooted) statuses.Add(BbSetStatus.SetRooted);
            if (Silenced) statuses.Add(BbSetStatus.SetSilenced);
            if (Invulnerable) statuses.Add(BbSetStatus.SetInvulnerable);
            if (MagicImmune) statuses.Add(BbSetStatus.SetMagicImmune);
            if (Stealthed) statuses.Add(BbSetStatus.SetStealthed);
            // Reconstruction, corrected 19 October 2026 (W27c): the stock 126 client does not collide by the spawn packet's IgnoreCollision bit
            // (its 0x80 case passes the bit only to the update's put-on-floor switch, Entity.SpawnIgnoreCollision), so a unit collides by the pathfinding radius of its own
            // character data; Anivia's ice blocks (100), Jarvan's walls (90) and his standard (13.6) are solid bodies there. Only a unit
            // that is too small to push anything (the helper cubes, a trap) keeps the pass-through the server used for every flagged unit.
            if (IgnoreCollision && radius <= MarkerRadius) statuses.Add(BbSetStatus.SetGhosted);
            return statuses;
        }
    }

    /// <summary>
    /// The call's status flags (Stunned, Rooted, Silenced, Invulnerable, MagicImmune, IgnoreCollision): retail sets the new unit's status bits
    /// from the native's flags, so they go on the unit's script status (<see cref="BbBuffRuntime.SetSpawnStatuses"/>), held until a script
    /// writes the rest value or the unit dies. They take no buff slot.
    /// </summary>
    private void ApplyFlags(Entity unit, SpawnFlags flags) =>
        _host.Buffs.SetSpawnStatuses(unit, flags.Statuses(unit.PathfindingRadius ?? unit.Radius));

    /// <summary>
    /// The generic unit: a minion of the character record <paramref name="skin"/> (its stats through <see cref="GameData"/>) with the
    /// call's flags. A placemarker is registered so scripts can address, face, buff and cast from it, and kept out of target selection
    /// (<see cref="Entity.Protected"/>). It is announced like any unit (<see cref="Entity.Announced"/>), except Orianna's ball, which
    /// <see cref="AnnounceBall"/> sends. Reconstruction: the stock client has no placemarker bit on the spawn packet (the spawn handler's case 0x80 of
    /// the S2C switch reads none) and no string for a placemarker field, so the no-render and force-render state the scripts
    /// set is what tells the clients a placemarker apart (the replicated action state, bits 13 and 14).
    /// </summary>
    private Entity SpawnGeneric(Entity? owner, string name, string skin, Position2 position, Team team, SpawnFlags flags, Entity? redirect,
        double visibility, string? ai = null, UnitType unitType = UnitType.Monster)
    {
        var data = Spells.Data.Champion(skin);
        // Reconstruction (owner decision, 24 September 2026, "Q cannot finish inside terrain"): retail's landing for a ball placed in terrain is not recovered; the ball does not rest in terrain. A landing the Q child already
        // put on floor is kept; the spawn of the no-missile branch (a ball placed within 75 of the old one) takes the same backing off.
        if (skin == BallSkin) position = BbChampionModule.FloorLanding(Spells, owner?.Position ?? position, position);
        // Yorick's ghouls (the YorickSummon* BBSpawnMinion): the unit kind every ghoul rule keys on, and the same body-clear
        // placement the other summons take (the Q aims 25 units ahead and the E 250 past the target, both into terrain; playtest
        // B2 YV6, 26 Sept 2026).
        // Reconstruction: the YorickSummon* scripts BBSpawnMinion at the cast point as given (retail's native handling of a point in terrain is not recovered); the projection onto the nearest valid
        // floor is the owner's 24 September 2026 placement rule.
        // What this path gives a ghoul that the retired YorickGhoulService did not, all from its record: HealthRegen
        // (Data*BaseStaticHPRegen 2.00), the gold and XP of its death (both '0', as the service left them), ChampionName
        // "Yorick*Ghoul" instead of the placeholder "Annie" (its basic attack is CastType 0, so AttackMissileSpeed is still
        // 0 and MissileSpeed is unchanged), AiState "idle" instead of the entity default "march", and UnitAttackTiming's
        // AttackSpeedMod and BasicAttackAnimationProbabilities. The record wins (owner, 20 October 2026).
        var ghoul = skin is "YorickDecayedGhoul" or "YorickRavenousGhoul" or "YorickSpectralGhoul";
        if (ghoul) position = Spells.Movement.Navigation.ClosestSummonPlacement(position, data.PathfindingRadius);
        var credited = redirect is { UnitType: UnitType.Hero } hero ? hero.NetId : 0u;
        // Batch 451: a wave minion is a minion (AffectMinions and the minion kill of the capture trees read the unit type). Batch 452
        // (Reconstruction): a point turret is a turret object (GetTurret answers turrets, OdinScriptBehaviorTrees.xml:1832), so it takes the
        // turret unit type, and the Turret script (DATA/Scripts/Turret.luaobj) runs for it, not for a monster. Its client object is a minion
        // (batch 457, below).
        var unit = new Entity(Spells.World.Allocate(unitType),
            unitType == UnitType.Turret ? EntityKind.Turret : flags.IsWard ? EntityKind.Ward : ghoul ? EntityKind.YorickGhoul
                : unitType == UnitType.Minion ? EntityKind.Minion : EntityKind.Monster,
            unitType, team, position)
        {
            Name = skin,
            ChampionName = skin,
            OwnerNetId = credited,
            WardOwnerNetId = flags.IsWard ? credited : 0u,
            IsWard = flags.IsWard,
            Health = data.BaseHp,
            MaxHealth = data.BaseHp,
            HealthRegen = data.HpRegen,
            // The unit is spawned with its record's pool filled from Data*BaseMP (the ward scripts tick BBIncPAR PAR_SHIELD -1 a second
            // against it). Every record has a pool: the loader reads PARType with the default "MP" (mana) and
            // BaseMP with the default 100.0, which GameData's BaseMp already applies (batch 320).
            Mana = data.BaseMp,
            MaxMana = data.BaseMp,
            AttackDamage = data.BaseDamage,
            Armor = data.Armor,
            SpellBlock = data.SpellBlock,
            Speed = data.MoveSpeed,
            Radius = data.PathfindingRadius,
            CombatRadius = data.CombatRadius,
            SelectionRadius = data.SelectionRadius,
            AttackRange = data.AttackRange,
            AcquisitionRange = data.AcquisitionRange,
            GoldReward = data.GoldGivenOnDeath,
            ExperienceReward = data.ExpGivenOnDeath,
            AiState = "idle",
            Protected = flags.Placemarker,
            Placemarker = flags.Placemarker,
            // Set before the spawn packet below, which carries it (SpawnMinion.visibilitySize).
            VisibilitySize = visibility,
            Created = false,
            Visible = false,
            NodeId = 0x40,
        };
        // The ball is shown in its owner's skin (the 0x80 SkinId of the hand-written ball).
        if (skin == BallSkin) unit.SkinId = (redirect ?? owner ?? unit).SkinId;
        unit.MinionUse = MinionUseOf(data.Raw);
        // Batch 660: the stock CharacterData loader gives every character its record's attack timing, whatever AI it runs. Before this only a
        // unit with a fighting or pet script took it, so the encounter units (ai null: the Crystal Scar's wave minions) kept period 0 and windup
        // 0 and swung on every step ("Dominion minions attack WAY TOO FAST", community report, 3 November 2026).
        UnitAttackTiming.Apply(unit, data);
        unit.MissileSpeed = AttackMissileSpeed(unit);
        // Reconstruction: every other call of BBSpawnMinion names its own character record as Name, where the wire skin of a late observer is
        // entity.Name; only the Rally beacon is asked for a display name of its own (SummonerRally: Name 'Beacon', Skin 'SummonerBeacon'), and
        // the rest of the engine reads a beacon as Name 'Beacon', ChampionName 'SummonerBeacon' (ProductionMatchComposition's template,
        // Entity.IsSummonerBeacon). The ward wire flag of the call stands (its hit count and vision rules read the beacon, not the ward).
        if (skin == BeaconSkin)
        {
            unit.Name = name;
            unit.IsSummonerBeacon = true;
        }
        // A fighting script (UncontrollablePet, Minion, Aggro) or one of the two pet families
        // master's binding names takes the controller's clock and the owner credit below;
        // batch 163's idle.lua arm keeps the spawn call's own clock and stands where it was put.
        var petFamily = SpawnedUnitAi.PetFamilyOf(ai);
        if ((SpawnedUnitAi.Fights(ai) || petFamily is not null) && !flags.Placemarker)
        {
            // A unit with an AI script of its own fights for its owner: the hero the call credits (GoldRedirectTarget), else the hook's Owner.
            // UncontrollablePet.lua is Pet.lua without the owner's orders and Minion.lua is the lane minion's target decisions without a lane
            // (TibbersAiSystem); YorickPHPet.lua is the ghoul's own YorickPHPet.luaobj (batch 146). The life of a unit the engine runs a pet
            // host for is its script buff's, not a clock of its own.
            unit.AiScript = SpawnedUnitAi.Canonical(ai);
            if (unit.OwnerNetId == 0 && owner is not null) unit.OwnerNetId = owner.NetId;
            unit.ExpiresAt = double.PositiveInfinity;
            if (petFamily is { } family)
                unit.PetState = new PetState(family, unit.OwnerNetId) { BornAt = Spells.CurrentTime };
        }
        else if (SpawnedUnitAi.IsIdle(ai) && !flags.Placemarker)
        {
            // Batch 163: Idle.luaobj is this unit's AI now (the ward, the wall piece, the blood drop
            // and the trap the call made). It takes no target and has no lifetime of its own, so
            // only the script name goes on the unit — the spawn call's clock and its stats stay
            // exactly as they were — and the host runs the chunk for it
            // (TibbersAiSystem.Host, Idle.luaobj's SetState(AI_HARDIDLE) and empty callbacks). A
            // placemarker is kept out of the AI path for the same reason it is kept out of the
            // spawn packets and target selection: it is a position to cast from, not a unit.
            unit.AiScript = SpawnedUnitAi.Idle;
        }
        Spells.World.Register(unit);
        ApplyFlags(unit, flags);
        unit.SpawnIgnoreCollision = flags.IgnoreCollision;
        if (skin == BallSkin) AnnounceBall(unit, owner ?? unit);
        else
        {
            // Batch 457: a turret spawned here (the Odin point turrets) has no scene object the client could bind a CreateTurret to, so it is
            // announced as the minion of its character, like any spawned unit (Entity.ReplicatedAsMinion); the server keeps running it as a turret.
            unit.ReplicatedAsMinion = unitType == UnitType.Turret;
            // Reconstruction: retail's health bar for a placemarker is not recovered. The bar is hidden on the wire (ShowHealthBar 0xD7,
            // the creation burst of BuildBuffs) so a helper with no body of its own shows none.
            if (flags.Placemarker) unit.HealthBarHidden = true;
            Spells.Effects.SendMinionSpawn(unit, name, skin, flags.IgnoreCollision);
            unit.Created = true;
            unit.Announced = true;
            Spells.Effects.SendInitialVisibility(unit);
            Spells.Effects.SendStats(unit);
        }
        // Retail runs the character script's CharOnActivate when the character is created: the ball's gives it OrianaGhost, OrianaGhostMinion and the
        // tracker, TestCube's hides it.
        _host.Router.ActivateSpawned(unit, skin);
        return unit;
    }

    /// <summary>
    /// Batch 499: the use a minion may make of a spawned unit (<see cref="Entity.MinionUse"/>), from its character file's <c>Useable</c> section
    /// as <c>UseableComponent</c> reads it (batch 470): <c>IsUseable</c>, <c>MinionUseable</c> (1 when
    /// absent), <c>MinionUseSpell</c>, <c>AllyCanUse</c> and <c>EnemyCanUse</c>. On map 8 the capture point guardian qualifies
    /// (OdinNeutralGuardian.inibin: IsUseable 1, EnemyCanUse 1, MinionUseSpell OdinMinionSpellAttack, CastRange 400); the greater relic does not
    /// (OdinCenterRelic.inibin: MinionUseable "0").
    /// </summary>
    private (string Spell, double Range, bool AllyCanUse, bool EnemyCanUse)? MinionUseOf(InibinData raw)
    {
        if (!Flag("Useable*IsUseable", false) || !Flag("Useable*MinionUseable", true) ||
            !raw.TryGetValue(InibinReader.KeyHash("Useable*MinionUseSpell"), out var name) || name.Kind != InibinValueKind.String ||
            name.AsString() is not { Length: > 0 } spell) return null;
        return (spell, Spells.Data.Spell(spell).CastRange, Flag("Useable*AllyCanUse", false), Flag("Useable*EnemyCanUse", false));

        // As the hero's use reads a flag (ProductionGameplayDispatcher.HeroUseSpell): the boolean section, a "0"/"1" string, or a number.
        bool Flag(string key, bool absent) => !raw.TryGetValue(InibinReader.KeyHash(key), out var value) ? absent
            : value.Kind == InibinValueKind.Boolean ? value.Boolean
            : value.Kind == InibinValueKind.String ? InibinValue.AtofPrefix(value.AsString()) != 0.0
            : value.IsNumeric && value.AsDouble() != 0.0;
    }

    /// <summary>
    /// Batch 446: a unit an encounter's squad spawns (<c>SpawnSquadFromEncounter</c>), at <paramref name="position"/> on <paramref name="team"/>.
    /// It takes the generic spawn of a script unit with no owner, so its create packets and its character script's CharOnActivate are the
    /// ones <c>BBSpawnMinion</c> gives. Reconstruction: the shipped encounter names no owner, so the unit fights for no one and carries no
    /// credit (<see cref="SpawnGeneric"/> with a null owner).
    /// </summary>
    internal Entity SpawnEncounterUnit(string skin, Position2 position, Team team, UnitType unitType = UnitType.Monster) =>
        SpawnGeneric(null, skin, skin, position, team, default, null, 0.0,
            unitType: skin is "OdinOrderTurretShrine" or "OdinChaosTurretShrine" ? UnitType.Turret : unitType);

    /// <summary>The character record of Orianna's ball, the one placemarker the clients are shown.</summary>
    private const string BallSkin = "OriannaBall";

    /// <summary>The character record of Rally's banner (SummonerRally: <c>Name</c> 'Beacon', <c>Skin</c> 'SummonerBeacon').</summary>
    private const string BeaconSkin = "SummonerBeacon";

    /// <summary>
    /// Announces the ball to the clients through the ball projection of the replication layer (<c>ISpellEffectSink.SendOriannaBall</c>): the
    /// layer builds the 0x80 minion template (the ball's stats, ghosted and force-render-particles action state), the C2 re-entry and the leave
    /// for each observer by sight, follows the unit (its position, no-render state and life) and drops the projection when the unit ends. Reconstruction:
    /// the spawn packet (0x80) has no placemarker bit, so retail's server sent the ball as an ordinary minion; this engine keeps the placemarker out of
    /// every entity wire path (<see cref="Entity.Created"/> stays false) and sends the ball by its own projection, which the hand-written ball used.
    /// </summary>
    private void AnnounceBall(Entity ball, Entity owner)
    {
        var data = Spells.Data.Champion(BallSkin);
        Spells.Effects.SendOriannaBall(new OriannaBallReplication(ball.NetId, ball.Position, TeamMapping.ToNative(ball.Team),
            checked((float)data.BaseHp), checked((float)data.BaseDamage), checked((float)data.MoveSpeed), checked((float)data.Armor),
            checked((float)data.SpellBlock), ball.OwnerNetId != 0 ? ball.OwnerNetId : owner.NetId, ball.SkinId), teleport: false, teleportId: 0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // BBCloneUnitPet (Shaco's clone, Yorick's revenant)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>BBCloneUnitPet</c>. The clone is built by <see cref="SharedCloneService.Spawn"/>, the one the hand-written Shaco and
    /// Yorick use; the pet family follows the call's <c>Buff</c> (Hallucinate: Shaco's clone; YorickRARevive: the revenant) because the
    /// family is named by the controller buff the script adds next (<c>HallucinateFull</c>, <c>YorickRAPetBuff2</c>), which Pet.lua's tick
    /// looks for. The clone copies <c>UnitToClone</c>: its model, level, maximum health, mana, damage, defences, speed, ranks and
    /// inventory; Reconstruction: it starts at the source's current health (Hallucinate adds no health; Omen of Death heals the revenant
    /// itself right after). <c>HealthBonus</c>/<c>DamageBonus</c> are 0 in every shipped call and are refused otherwise. LeBlanc's image
    /// (<c>LeblancMI</c>, controller <c>LeblancMIFull</c>), Wukong's decoy (<c>MonkeyKingDecoyDummy</c>, no controller) and Mordekaiser's ghost
    /// (<c>MordekaiserCotgPetSlow</c>, controller <c>MordekaiserCOTGPetBuff2</c>; the source is a fallen enemy) are the families of W26c (<see cref="CloneFamilies"/>);
    /// any other buff name is refused. The clone is on the team of its owner. Reconstruction: when the script of the <c>Buff</c> name defines buff hooks
    /// (<c>MordekaiserCOTGPetSlow</c>: a 20 percent speed cut), the server adds that buff to the clone for the call's <c>Duration</c> (a value of 0
    /// is the clone's life, which a controller buff ends); a spell script with only spell hooks (Hallucinate, LeblancMI, YorickRARevive) adds nothing.
    /// </summary>
    private void CloneUnitPet(BbCall call)
    {
        Refuse(call);
        var owner = SpawnOwner(call);
        var source = Unit(call, "UnitToCloneVar") ?? throw new BbRuntimeException("BBCloneUnitPet has no UnitToClone");
        // Reconstruction: a dead owner clones nothing (the source may be a corpse: Yorick's revenant clones a fallen champion). Death retires the
        // Hallucinate applicator, whose deactivation would otherwise clone the dead Shaco; the hand-written Shaco returned at the same point.
        if (!owner.Alive) return;
        var buff = call.Value("Buff") as string ?? string.Empty;
        var kind = CloneKindOf(buff) ?? throw BbSpellHost.Unbound($"BBCloneUnitPet Buff={buff}", "W22");
        if (Sum(call, "HealthBonus") != 0.0 || Sum(call, "DamageBonus") != 0.0)
            throw BbSpellHost.Unbound("BBCloneUnitPet with a health or damage bonus through a variable", "W22");
        var display = string.IsNullOrEmpty(source.ChampionName) ? source.Name : source.ChampionName;
        // The point is a script's random draw (Hallucinate: 50 to 250 units around Shaco), which can fall in a wall. The owner's summon-placement rule
        // (24 September 2026: Annie R, Shaco W, the Yorick ghouls) moves a blocked summon position to the nearest valid client-grid point with body
        // clearance, and the hand-written Shaco did the same for his clone (CorrectTeleportDestination); an open point is unchanged.
        var at = Spells.Movement.Navigation.ClosestSummonPlacement(SpawnPosition(call, "PosVar"), Spells.Movement.MovementRadius(source));
        var duration = Sum(call, "Duration");
        var clone = Pets.Clones.Spawn(owner, source, at, kind, source.Health, buff, duration,
            display, Flag(call, "ShowMinimapIcon", true));
        if (_host.Buffs.HasBuffHooks(buff))
            _host.Buffs.Add(new BbBuffRequest(clone, owner, buff, (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)BbBuffType.BUFF_Internal, 1,
                duration > 0.0 ? duration : 25000.0, 0.0, false, new BbTable()));
        StoreOut(call, "Dest", clone);
    }
}
