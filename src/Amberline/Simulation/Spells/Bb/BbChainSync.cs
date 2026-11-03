using Amberline.Core;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Sends a chain missile's hop list when the client has the missile. The client's chain missile reads its hops from
/// <c>ChainMissileSync</c> with the missile's own net ID as sender, and it registers for that only once it exists, at
/// the cast frame: the client's spell-caster init sets <c>mTimeCast = now + extraTimeForCast + designerCastTime</c> at the
/// moment the client receives the cast, so the missile appears one windup after the cast packet arrives, and a packet that arrives
/// earlier is dropped (see <see cref="Amberline.Network.Protocol.SpellPackets.ChainMissileSync"/>). The list therefore leaves one
/// windup after the cast (the attack's windup for a chain attack) plus a margin, so that it arrives after the missile whatever the
/// frame phase on the client.
/// <para>
/// Reconstruction: the retail server's send count is not recovered (native server code); it is believed to send the list once, when its own missile is created. This one sends twice
/// (<see cref="FirstMargin"/> and <see cref="SecondMargin"/> after the windup): the cast frame on the client is only known to
/// within a frame and the link's jitter, and the handler rewrites the same slots, so the second copy costs nothing
/// when the first one landed and rescues the chain when it came first. A copy for a missile the client no longer has is dropped by
/// the client (and by the replication ledger once the missile is retired).
/// </para>
/// </summary>
internal sealed class BbChainSync : ISharedSpellProjectile
{
    /// <summary>Seconds after the windup of the first and the second copy.</summary>
    public const double FirstMargin = 0.05;
    public const double SecondMargin = 0.15;

    private readonly Entity _owner;
    private readonly uint _missileNetId;
    private readonly IReadOnlyList<uint> _hops;
    private readonly bool _attack;
    private double _elapsed;
    private readonly double _first;
    private readonly double _second;
    private bool _firstSent;

    public BbChainSync(Entity owner, uint missileNetId, IReadOnlyList<uint> hops, double windup, bool attack = false)
    {
        _attack = attack;
        _owner = owner;
        _missileNetId = missileNetId;
        _hops = hops;
        _first = Math.Max(0.0, windup) + FirstMargin;
        _second = Math.Max(0.0, windup) + SecondMargin;
    }

    public SharedProjectileSnapshot Snapshot => new("ChainMissileSync", _owner.NetId, 0, _missileNetId, 0.0, true, _owner.Position, 0.0);

    public bool Tick(SpellSystem system, double dt)
    {
        _elapsed += dt;
        if (!_firstSent && _elapsed + 1e-9 >= _first)
        {
            _firstSent = true;
            Send(system);
        }
        if (_elapsed + 1e-9 < _second) return true;
        Send(system);
        return false;
    }

    private void Send(SpellSystem system)
    {
        if (system.Effects.IsReferenceReady(_owner)) system.Effects.SendChainMissileSync(_owner, _missileNetId, _hops, _attack);
    }
}
