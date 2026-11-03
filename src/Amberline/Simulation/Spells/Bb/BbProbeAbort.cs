using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>Thrown to leave a <see cref="BbDamageProbe"/> run: at the primary hit (the amount is kept) or at the first thing that has an effect.</summary>
internal sealed class BbProbeAbort : Exception
{
}
