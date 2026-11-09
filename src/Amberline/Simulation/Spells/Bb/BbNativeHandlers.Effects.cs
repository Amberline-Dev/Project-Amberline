using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Who sees an effect a script creates (<c>BBSpellEffectCreate</c>): the team-dependent name <c>EffectNameForOtherTeam</c>, the viewer filters
/// <c>SpecificTeamOnly</c> / <c>SpecificTeamOnlyOverrideVar</c>, the own team <c>FOWTeam</c> / <c>FOWTeamOverrideVar</c> and the single client
/// <c>UseSpecificUnit</c> / <c>SpecificUnitOnlyVar</c> (W25b, 19 October 2026). The client registers the native with a parameter parser that is a
/// bare return and a handler that only wraps it, so none of this is
/// in the exe; every rule below is a Reconstruction from the 1747 shipped call sites (86 name an other-team effect, 143 filter by team, 105 by unit)
/// and from how the hand-written champions drew the same green and red pairs (Orianna, Yorick, Nidalee, Clairvoyance). The sink already routes
/// all three: <c>alliedTeam</c> with <c>enemyEffectName</c>, <c>alliedTeam</c> alone and <c>ownerHeroNetId</c>.
/// <list type="bullet">
/// <item><b>Own team</b>: <c>FOWTeamOverrideVar</c> (a team number from <c>BBGetTeamID</c>) wins over the literal <c>FOWTeam</c> (the shared team
/// getter the other natives use); <c>TEAM_CASTER</c>, <c>TEAM_OWNER</c> and <c>TEAM_TARGET</c> are those units' teams; <c>TEAM_UNKNOWN</c> is none.
/// Every Odin and Janna call site agrees: the own team is the team of the unit the effect belongs to (Monsoon passes the Owner's team, the Odin
/// bombs a fixed Order or Chaos).</item>
/// <item><b>Other-team name</b>: the own team's observers see <c>EffectName</c>, every other team's see <c>EffectNameForOtherTeam</c>
/// (the green and red variants of one particle). With no own team the <c>SpecificTeamOnly</c> team is the own team (the Odin guardian pairs
/// pass only that), and with neither the bound unit's team is (Riven's two calls). A name equal to <c>EffectName</c> (the Odin capture beam) is
/// no variant. The two ids of the retail pair (<c>EffectIDVar</c> for the own-team effect, <c>EffectID2Var</c> for the other-team effect) are one
/// effect here, so both variables hold its id and a script that removes either one ends it.</item>
/// <item><b>Viewer filter</b>: with no other-team name <c>SpecificTeamOnly</c> (or its override variable) restricts the effect to that team's
/// observers (the Instagate and Yorick pairs create a green effect for the owner's team and a red one for the other, each with its own filter;
/// <c>TEAM_CASTER</c> restricts the item auras to the caster's team). With an other-team name the filter is not applied: 17 shipped calls pair
/// one with a team filter (Absolute Zero, Smoke Bomb, both Trundle zones and Event Horizon with a single call that names Order or Chaos, and the
/// Odin bomb, guardian and capture channel), and applying it would hide a green and red particle from one whole team; the other team gets the
/// other name instead. The Odin capture beam names the same particle twice, so it is no variant and keeps its filter.</item>
/// <item><b>Single client</b>: <c>UseSpecificUnit</c> with <c>SpecificUnitOnlyVar</c> naming a unit sends the effect to that unit's player only
/// (Soraka's aura particle, the Rallying Banner on its owner, Orianna's ball indicators, Nocturne's Paranoia friend and foe marks); a pet or clone
/// belongs to its owner's player, and a unit with no player (a minion, "Nothing") restricts nothing.</item>
/// </list>
/// <c>FOWTeam</c> / <c>FOWTeamOverrideVar</c> (else a placemarker cube's team) is also the effect's fog-of-war team, which gets an effect on no current unit out of its sight (batch 473,
/// <c>ReplicationDelivery.CanRouteEffectCreate</c>). Not modelled: <c>FOWVisibilityRadius</c> and <c>SendIfOnScreenOrDiscard</c>, which the sink has no
/// counterpart for.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>The routing of one effect: the team its name is for, the name for every other team, the one hero whose client gets it.</summary>
    private readonly record struct EffectAudience(Team? Allied, string? EnemyName, uint OnlyHero);

    // Batch 516: a centre crystal's green and red come from OdinBombBuff as shipped (its team branch, FOWTeam TEAM_ORDER), now that each
    // crystal is on a team (BehaviorTreeQuestHost.RelicPlacements); batch 476e's side override is gone.
    private EffectAudience AudienceOfEffect(BbCall call, string effect, Entity? bound, Entity? target)
    {
        var only = EffectTeam(call, "SpecificTeamOnly", "SpecificTeamOnlyOverrideVar");
        var client = SpecificClient(call);
        if (call.Value("EffectNameForOtherTeam") is string other && !SameParticle(other, effect))
        {
            var own = EffectTeam(call, "FOWTeam", "FOWTeamOverrideVar")
                      ?? only
                      ?? (bound ?? target ?? call.Scope.Vars["Owner"] as Entity)?.Team;
            if (own is { } allied)
                return new EffectAudience(allied, Spells.Data.HasClientParticle(other) ? other : null, client);
        }
        return new EffectAudience(only, null, client);
    }

    /// <summary>
    /// A team parameter of the effect native: the variable (<c>...OverrideVar</c>, table <c>...OverrideVarTable</c>) wins over the literal, which
    /// may name a unit's team (<c>TEAM_CASTER</c>, <c>TEAM_OWNER</c>, <c>TEAM_TARGET</c>) or a concrete one; null for <c>TEAM_UNKNOWN</c> and for a
    /// variable that holds no team.
    /// </summary>
    private static Team? EffectTeam(BbCall call, string literalKey, string overrideKey)
    {
        if (call.Value(overrideKey) is { } name && call.Table(overrideKey + "Table", create: false)[name] is double fromVariable &&
            TeamMapping.TryFromNative((int)fromVariable, out var viaVariable)) return viaVariable;
        if (call.Value(literalKey) is not double literal) return null;
        var unit = (BbTeam)(int)literal switch
        {
            BbTeam.TEAM_CASTER => call.Scope.Vars["Caster"] as Entity,
            BbTeam.TEAM_OWNER => call.Scope.Vars["Owner"] as Entity,
            BbTeam.TEAM_TARGET => call.Scope.Vars["Target"] as Entity,
            _ => null,
        };
        if (unit is not null) return unit.Team;
        return TeamMapping.TryFromNative((int)literal, out var fixedTeam) ? fixedTeam : null;
    }

    /// <summary>The hero whose client alone gets the effect (<c>UseSpecificUnit</c>), 0 when it goes to everyone.</summary>
    private uint SpecificClient(BbCall call)
    {
        if (!Flag(call, "UseSpecificUnit", false) || Unit(call, "SpecificUnitOnlyVar") is not { } unit) return 0u;
        for (var hops = 0; unit.OwnerNetId != 0 && hops < 4 && Spells.World.Get(unit.OwnerNetId) is { } owner; hops++) unit = owner;
        return unit.UnitType == UnitType.Hero ? unit.NetId : 0u;
    }

    /// <summary>Whether two effect names are the same particle: the client looks a name up by its stem (no extension) and ignores case.</summary>
    private static bool SameParticle(string a, string b) =>
        string.Equals(Stem(a), Stem(b), StringComparison.OrdinalIgnoreCase);

    private static string Stem(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }
}
