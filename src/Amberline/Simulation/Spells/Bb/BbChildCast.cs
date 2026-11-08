using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// A script's <c>BBSpellCast</c> of the caster's <paramref name="ExtraIndex"/>th extra spell (<c>Data*ExtraSpell{n+1}</c>), cast at a point.
/// <paramref name="CastHeight"/> is how far above the terrain <paramref name="CastPosition"/> stands (batch 713).
/// </summary>
internal sealed record BbChildCast(Entity Caster, int ExtraIndex, Entity? Target, Position2 Position, Position2 End, BbHookFrame Frame,
    int? Level = null, bool FireWithoutCasting = false, BbSlotType SlotType = BbSlotType.ExtraSlots, bool OverrideCooldownCheck = true,
    Position2? CastPosition = null, bool ForceCastingOrChannelling = false, bool OverrideCastPosition = false, double CastHeight = 0.0)
{
    /// <summary>The caster's stored spellbook slot: a spell slot is stored as it is, an extra slot from 42.</summary>
    public int Slot => SlotType == BbSlotType.SpellSlots ? ExtraIndex : SpellSystem.ExtraSpellSlotStart + ExtraIndex;
}
