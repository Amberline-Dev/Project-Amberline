using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Simulation.Buffs;

/// <summary>
/// The obj_AI_Base CharacterState bits the shipped control scripts drive, read off the buffs a unit carries and the statuses scripts set
/// on it with no buff (<see cref="Entity.ScriptStatus"/>, batch 201) (status word bits:
/// 0 CanAttack, 1 CanCast, 2 CanMove, 5 Taunted, 6 Feared, 7 Suppressed, 8 Asleep). One rule for every consumer: the item and
/// spell cast gate (the base unit's spell-use check), the basic-attack gate, and the pets' and
/// minions' OnCanAttack / OnCanMove release.
/// <para>
/// Which script clears which bit: Stun.luaobj and Suppression.luaobj SetStunned (CanAttack, CanCast and CanMove off:
/// the stun setter clears all three), Sleep.luaobj SetSleep (clears all three), Disarm.luaobj SetDisarmed (CanAttack
/// only), Pacified.luaobj SetPacified (CanAttack and CanCast), Net.luaobj SetNetted (CanMove: the client calls
/// SetCanMove alone), Root.luaobj SetRooted (CanMove and CanAttack: the client's rooting setter sets both,
/// the bits), Silence.luaobj SetSilenced (CanCast), Fear.luaobj SetFeared plus SetCanCast(false), Taunt.luaobj
/// SetTaunted (its own bit, nothing cleared), CantAttack.luaobj SetCanAttack(false). A stasis (Zhonya's ZhonyasRingShield
/// SetStunned), Guardian Angel's revival and an ActionDisabled payload are all hard control.
/// </para>
/// <para>
/// The stun bits are the client's: SetStunned reads ShouldStun and calls three setters with !ShouldStun. The bodies of
/// SetDisarmed and SetPacified are not recovered, so their bit sets are as the ShouldDisarm and
/// ShouldPacified natives above give them (checked 18 October). Fear.luaobj leaves the CanAttack bit alone, but minionLib
/// and Pet.lua OnFearBegin call TurnOffAutoAttack(STOPREASON_IMMEDIATELY), so a feared unit cannot attack either (the lifecycle's
/// long-standing rule). Polymorph has no global script and is read as a silence (cannot cast). A blind unit keeps every bit:
/// Blind.luaobj only cancels the swing in progress and adds a miss chance.
/// </para>
/// </summary>
internal readonly record struct CharacterState(
    bool CanAttack, bool CanCast, bool CanMove, bool Taunted, bool Feared, bool Asleep, bool Suppressed)
{
    public static CharacterState Of(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        bool attack = true, cast = true, move = true, taunted = false, feared = false, asleep = false, suppressed = false;
        foreach (var buff in unit.BuffsAndScriptStatus())
        {
            switch (buff.Type)
            {
                case BuffType.Stun when !buff.Payload.ActionsAllowed: attack = cast = move = false; break;
                case BuffType.Suppression: suppressed = true; attack = cast = move = false; break;
                case BuffType.Sleep: asleep = true; attack = cast = move = false; break;
                case BuffType.Fear: feared = true; attack = cast = false; break;
                case BuffType.Taunt: taunted = true; break;
                case BuffType.Silence or BuffType.Polymorph: cast = false; break;
                case BuffType.Snare or BuffType.Net: move = false; break;
            }
            var payload = buff.Payload;
            if (payload.Silence || payload.Silenced) cast = false;
            if (payload.MovementDisabled) move = false;
            if (payload.Disarmed) attack = false;
            if (payload.Pacified) attack = cast = false;
            if (payload.Suppressed) suppressed = true;
            if (buff.Name == "CantAttack") attack = false;
            if (buff.DisablesActions) attack = cast = move = false;
        }
        return new CharacterState(attack, cast, move, taunted, feared, asleep, suppressed);
    }

    /// <summary>
    /// The 32-bit ActionState word the client replicates for a unit (the base unit's action word, its local bit 0 for a hero), read off
    /// the buffs the unit carries. Bit layout (the client's <c>CharacterState</c>, verified against its setters and readers on
    /// 18 October 2026): 0 CanAttack, 1 CanCast, 2 CanMove, 3 Stealthed, 4 RevealSpecificUnit, 5 Taunted, 6 Feared, 7 Suppressed,
    /// 8 Asleep, 9 NearSight, 10 Ghosted (the unit ignores collision), 11 GhostProof, 12 Charmed, 13 NoRender, 14
    /// ForceRenderParticles, 15 DodgePiercing, 16 DisableAmbientGold, 18 BrushVisibilityFake. A fresh unit holds 7. The client
    /// takes the word as sent and runs no buff scripts of its own, so a status exists on the client only through these bits.
    /// <para>
    /// It follows the client's setters, not the server's gates: Fear sets bit 6 and clears CanCast but leaves CanAttack and CanMove
    /// set (Fear.luaobj: SetFeared, SetCanCast(false); the client refuses a move order of a feared unit by bit 6, the server
    /// refuses the attack), and Stealth sets Ghosted with Stealthed (SetStealthed calls SetGhosted). The layout is the client's:
    /// IsCancelBlocked reads bit 1 (CanCast) and bit 7 (Suppressed) of the ActionState. A Snare-typed buff is
    /// read as CanMove off, the same reading as <see cref="Of"/>; the Root buff (BBApplyRoot, Root.luaobj) carries the CanAttack
    /// half of the client's SetRooted in its payload (Disarmed), as a script's SetRooted does (BbBuffRuntime.ApplyStatus).
    /// </para>
    /// </summary>
    /// <summary>The NoRender bit (13) of <see cref="ActionWord"/>.</summary>
    public const uint NoRenderBit = 1u << 13;

    public static uint ActionWord(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        const uint Attack = 1u << 0, Cast = 1u << 1, Move = 1u << 2, Stealthed = 1u << 3, Taunted = 1u << 5, Feared = 1u << 6,
            Suppressed = 1u << 7, Asleep = 1u << 8, NearSight = 1u << 9, Ghosted = 1u << 10, GhostProof = 1u << 11, NoRender = 1u << 13,
            ForceRenderParticles = 1u << 14;
        var word = Attack | Cast | Move;
        foreach (var buff in unit.BuffsAndScriptStatus())
        {
            switch (buff.Type)
            {
                case BuffType.Stun when !buff.Payload.ActionsAllowed: word &= ~(Attack | Cast | Move); break;
                case BuffType.Suppression: word = (word & ~(Attack | Cast | Move)) | Suppressed; break;
                case BuffType.Sleep: word = (word & ~(Attack | Cast | Move)) | Asleep; break;
                case BuffType.Fear: word = (word | Feared) & ~Cast; break;
                case BuffType.Taunt: word |= Taunted; break;
                case BuffType.Silence or BuffType.Polymorph: word &= ~Cast; break;
                case BuffType.Snare or BuffType.Net: word &= ~Move; break;
                case BuffType.NearSight: word |= NearSight; break;
            }
            var payload = buff.Payload;
            if (payload.Silence || payload.Silenced) word &= ~Cast;
            if (payload.MovementDisabled) word &= ~Move;
            if (payload.Disarmed || buff.Name == "CantAttack") word &= ~Attack;
            if (payload.Pacified) word &= ~(Attack | Cast);
            if (payload.Suppressed) word |= Suppressed;
            // A fear carries the action-disabled payload in this server, but the client's word for it keeps CanAttack and CanMove
            // (Fear.luaobj clears only CanCast), and the walk it forces needs the move bit.
            if (buff.DisablesActions && buff.Type != BuffType.Fear) word &= ~(Attack | Cast | Move);
            if (payload.Stealthed) word |= Stealthed | Ghosted;
            if (payload.Ghosted) word |= Ghosted;
            if (payload.GhostProof) word |= GhostProof;
            if (payload.NoRender) word |= NoRender;
            if (payload.ForceRenderParticles) word |= ForceRenderParticles;
        }
        return word;
    }

    /// <summary>
    /// The combat state a unit replicates in its stat block: the ActionState word (<see cref="ActionWord"/>), then MagicImmune, Invulnerable and
    /// Targetable (1 while the unit is targetable), each read off the buffs and script statuses the unit holds.
    /// </summary>
    public readonly record struct ScriptedCombat(uint ActionState, uint MagicImmune, uint Invulnerable, uint Targetable);

    /// <summary>
    /// <see cref="ScriptedCombat"/> of <paramref name="unit"/>. The stat block used to carry protocol defaults for these four (MagicImmune 0,
    /// Invulnerable 0, Targetable 1, Attack, Cast and Move on), so a script's status on a spawned unit (a trap's Invulnerable, MagicImmune and
    /// SetTargetable false, an axe's root, a sapling's SetCanMove false) never reached the client (batch 359, owner findings 7, 59 and 80).
    /// </summary>
    public static ScriptedCombat ScriptedCombatOf(Entity unit, uint movementBase = 7u)
    {
        ArgumentNullException.ThrowIfNull(unit);
        // movementBase gives the Attack, Cast and Move bits a unit starts from (7; a box or a Noxious Trap starts from 0, as its shipped
        // record keeps it); the script's bits clear them. NoRender comes from the entity's own flag as well as from the buffs.
        const uint Movement = 7u;
        var word = ActionWord(unit);
        var actionState = (movementBase & word & Movement) | (word & ~Movement) | (unit.NoRender ? NoRenderBit : 0u);
        // Batch 513: a client is told the Ghost of Urf is targetable, so its shipped tooltip can be hovered (CombatPackets.ShowsOwnTooltip).
        return new ScriptedCombat(actionState,
            unit.AnyBuffOrStatus(buff => buff.Payload.MagicImmune) ? 1u : 0u,
            unit.AnyBuffOrStatus(buff => buff.Payload.Invulnerable) ? 1u : 0u,
            Amberline.Network.Protocol.CombatPackets.WireTargetable(unit,
                unit.AnyBuffOrStatus(buff => buff.Payload.Untargetable) ? 0u : 1u));
    }
}
