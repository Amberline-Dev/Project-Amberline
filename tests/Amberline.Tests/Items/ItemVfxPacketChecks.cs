using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Batch 157 part 1 (22 October 2026 playtest): the shipped item scripts draw their own buff visuals from
/// <c>OnBuffActivate</c> and take them back in <c>OnBuffDeactivate</c>, so the server's packet stream is the evidence
/// of how many copies of one visual are live at a time. These checks count the particle create/destroy and buff
/// add/renew/remove packets per unit over time and compare them with what the shipped scripts ask for: one activation
/// is one <c>BBSpellEffectCreate</c> of one effect id, and a <c>BUFF_RENEW_EXISTING</c> renewal refreshes the timer
/// without running the activation hook again (BbBuffRuntime, Reconstruction M2). The playtest's Banshee's Veil was
/// "really bright, perhaps a stacked visual effect" — one add had two bansheesveil_buf live and never killed either.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static uint U32(PlaintextPacket packet, int offset) =>
        BitConverter.ToUInt32(packet.Plaintext.Span.Slice(offset, 4));

    private static float F32(PlaintextPacket packet, int offset) =>
        BitConverter.ToSingle(packet.Plaintext.Span.Slice(offset, 4));

    private static string Hex(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);


    /// <summary>
    /// The particle traffic of one name on one observer's wire: a create (0x8C of that name) parks its effect id under
    /// the unit it is bound to, a destroy (0x3D) takes one out, and <see cref="PeakPerUnit"/> is the most copies any
    /// one unit carried at once. The buff packets of the run are logged beside them (0xBF add, 0x32 renew, 0x7F remove)
    /// because the add/renew/remove of a buff is what its script's activation and deactivation answer.
    /// </summary>
    private sealed class VfxTraffic
    {
        private readonly string _name;
        private readonly uint _hash;
        private readonly Dictionary<uint, string> _names = new();
        private readonly Dictionary<uint, (double At, uint Bind)> _live = new();
        private readonly HashSet<byte> _slots = [];

        /// <param name="name">The particle the run is about (counted).</param>
        /// <param name="settledAt">Time by which the buff's own activation has run: anything drawn after this is a
        /// renewal or a re-add, and must be nothing.</param>
        /// <param name="buffs">The buffs whose add is one activation, for the log and the count.</param>
        public VfxTraffic(string name, double settledAt, params string[] buffs)
        {
            _name = name;
            _hash = SpellPackets.BuffNameHash(name);
            SettledAt = settledAt;
            foreach (var buff in buffs) _names.TryAdd(SpellPackets.BuffNameHash(buff), buff);
            _names.TryAdd(_hash, name);
        }

        /// <summary>Time after which every create is one the shipped scripts did not ask for.</summary>
        public double SettledAt { get; }
        public int Created { get; private set; }
        public int Killed { get; private set; }
        public int Renewed { get; private set; }
        public int Activations { get; private set; }
        public int CreatedAfterSettle { get; private set; }
        public int PeakPerUnit { get; private set; }
        public List<string> Lines { get; } = [];
        public string Summary =>
            $"{_name}: created={Created} killed={Killed} peak-per-unit={PeakPerUnit} still-live={_live.Count} " +
            $"activations={Activations} renewals={Renewed} created-after-settle={CreatedAfterSettle}";

        private string NameOf(uint hash) =>
            _names.TryGetValue(hash, out var known) ? known : $"#{Hex(hash)}";

        public void Absorb(PlaintextPacket packet, double at)
        {
            var payload = packet.Plaintext.Span;
            switch (payload[0])
            {
                case 0x8C:
                {
                    var named = U32(packet, 6);
                    var id = U32(packet, 25);
                    var bind = U32(packet, 29);
                    if (named != _hash) return;
                    Created++;
                    if (at > SettledAt) CreatedAfterSettle++;
                    _live[id] = (at, bind);
                    Lines.Add($"{at:0.0} CREATE {_name} id={Hex(id)} bind={bind} live={LiveOn(bind)}");
                    break;
                }
                case 0x3D:
                {
                    var id = U32(packet, 5);
                    if (_live.Remove(id, out var entry))
                    {
                        Killed++;
                        Lines.Add($"{at:0.0} KILL   id={Hex(id)} bind={entry.Bind} (lived {at - entry.At:0.0}s) live={LiveOn(entry.Bind)}");
                    }
                    break;
                }
                case 0xBF:
                {
                    var named = U32(packet, 9);
                    _slots.Add(payload[5]);
                    Lines.Add($"{at:0.0} ADD    {NameOf(named)} unit={U32(packet, 1)} slot={payload[5]} running={F32(packet, 13):0.00} duration={F32(packet, 17):0.00}");
                    if (_names.ContainsKey(named)) Activations++;
                    break;
                }
                case 0x32 when _slots.Contains(payload[5]):
                    Renewed++;
                    Lines.Add($"{at:0.0} RENEW  unit={U32(packet, 1)} slot={payload[5]} running={F32(packet, 6):0.00} duration={F32(packet, 10):0.00}");
                    break;
                case 0x7F:
                    Lines.Add($"{at:0.0} REMOVE {NameOf(U32(packet, 6))} unit={U32(packet, 1)} slot={payload[5]}");
                    _slots.Remove(payload[5]);
                    break;
            }
        }

        public void CloseTick()
        {
            foreach (var bound in _live.Values.GroupBy(entry => entry.Bind))
                PeakPerUnit = Math.Max(PeakPerUnit, bound.Count());
        }

        private int LiveOn(uint bind) => _live.Values.Count(entry => entry.Bind == bind);
    }

    /// <summary>
    /// One run's worth of packets on the given seats' wires, at the simulation's own 0.1 s step. All seats record the
    /// same window: a second <see cref="Record"/> would tick the match on and hand the later seat a different one.
    /// </summary>
    private static void Record(Match match, double seconds, params (int Seat, VfxTraffic Traffic)[] wires)
    {
        const double step = 0.1;
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += step)
        {
            match.Runtime.Tick(step);
            match.Runtime.Delivery.Flush();
            foreach (var (seat, traffic) in wires)
                foreach (var packet in match.Captures[seat].Packets) traffic.Absorb(packet, elapsed);
            foreach (var (_, traffic) in wires) traffic.CloseTick();
            match.Clear();
        }
    }


    [Check("items-live-aura-items-draw-their-activation-particle-and-take-it-back-on-sale")]
    public static void AuraItemsDrawTheirActivationParticleAndTakeItBackOnSale()
    {
        // Batch 555 (batch 111's open point: "the aura particles that the OnBuffActivate items create are not created by
        // the server"). Each Items/<id>.luaobj's own OnBuffActivate is one BBSpellEffectCreate on the Owner and its
        // OnBuffDeactivate the matching BBSpellEffectRemove; since batch 145 the copy is bound as that buff script at
        // purchase and unbound at sale (BbItemModule.SyncInventory), so the wearer's wire carries the create and, once the
        // copy and the aura buffs it re-adds are gone, the kill of every copy it drew.
        foreach (var (item, particle) in new[]
        {
            (3037, "ZettasManaManipulator_itm"), (3050, "RallyingBanner_itm"), (3097, "RallyingBanner_itm"),
            (3099, "ZettasManaManipulator_itm"), (3105, "ZettasManaManipulator_itm"), (3152, "RallyingBanner_itm"),
        })
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
            match.Clear();
            var traffic = new VfxTraffic(particle, 2.5);
            Record(match, 3.0, (0, traffic));
            TestAssert.True(traffic.Created > 0, $"item {item} draws {particle} on its wearer ({traffic.Summary})");
            hero.Inventory = [null, null, null, null, null, null];
            Record(match, 3.0, (0, traffic));
            TestAssert.Equal(traffic.Created, traffic.Killed,
                $"item {item}'s sale takes back every {particle} it drew ({traffic.Summary}): " +
                string.Join(" | ", traffic.Lines.Take(40)));
        }
    }
}
