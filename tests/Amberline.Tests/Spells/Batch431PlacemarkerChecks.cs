using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

/// <summary>Batch 431: Xerath's R warning (the launcher cube's effect) reaches both clients unbound and lasts until the hit.</summary>
public static partial class ChampionLiveRegressionChecks
{
    // Batch 431 (Part 1, reproduce): Xerath's R launcher cube creates Xerath_E_cas_green on itself when its buff starts (Xerath_E_cas_red for the
    // other team) and the buff's OnBuffDeactivate removes it when the cube dies in the shot's hit (the shot's self-damage). The cube is a placemarker:
    // its character script is CharScriptTestCube (NoRender). The warning must reach both clients unbound at the cube's point and last until the hit.
    [Check("scripting.bb-xerath-r-warning-is-sent-unbound-to-both-clients-until-the-hit")]
    public static void XerathRWarningIsSentUnboundUntilTheHit()
    {
        using var match = new Match("Xerath", Team.Order, enemyChampion: "Orianna");
        match.Clear();
        TestAssert.True(match.TryCastGround(0, 3, match.Hero(1).Position), "Xerath R is cast at the foe");
        var green = SpellPackets.BuffNameHash("Xerath_E_cas_green");
        var red = SpellPackets.BuffNameHash("Xerath_E_cas_red");
        var seen = new int[match.Captures.Count];
        var warnings = new List<(int Observer, uint Id, JsonElement Effect)>();
        var killedAt = new Dictionary<uint, double>();
        double? death = null;
        for (var tick = 0; tick < 90; tick++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            var now = match.Runtime.Combat.CurrentTime;
            for (var observer = 0; observer < match.Captures.Count; observer++)
            {
                var packets = match.Captures[observer].Packets;
                for (; seen[observer] < packets.Count; seen[observer]++)
                {
                    var packet = packets[seen[observer]];
                    var opcode = packet.Plaintext.Span[0];
                    if (opcode == 0x8C)
                    {
                        foreach (var group in Decode(packet).GetProperty("groups").EnumerateArray())
                        {
                            if (group.GetProperty("effect_name_hash").GetUInt32() != (observer == 0 ? green : red)) continue;
                            foreach (var effect in group.GetProperty("effects").EnumerateArray())
                                warnings.Add((observer, effect.GetProperty("net_assigned_id").GetUInt32(), effect));
                        }
                    }
                    else if (opcode == 0x3D && observer == 0)
                        killedAt.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]), now);
                    else if (opcode == 0xA6 && observer == 0)
                        death ??= now;
                }
            }
        }
        TestAssert.True(warnings.Any(warning => warning.Observer == 0), "the caster's client gets the warning (Xerath_E_cas_green)");
        TestAssert.True(warnings.Any(warning => warning.Observer == 1), "the foe's client gets the warning (Xerath_E_cas_red)");
        foreach (var (observer, _, effect) in warnings)
        {
            TestAssert.Equal(0u, effect.GetProperty("bind_netid").GetUInt32(),
                $"observer {observer}: the warning is sent unbound at the cube's point (bind 0, batch 397 rule for a NoRender placemarker)");
            TestAssert.Equal(0u, effect.GetProperty("target_netid").GetUInt32(), $"observer {observer}: its target is unbound too (target 0)");
        }
        TestAssert.True(death is not null, "the launcher cube dies in the shot's hit (0xA6)");
        foreach (var (_, id, _) in warnings.Where(warning => warning.Observer == 0))
        {
            var killed = killedAt.TryGetValue(id, out var at) ? at : -1.0;
            TestAssert.True(killed >= death!.Value - 1e-6,
                $"the warning {id} lives until the hit: killed at {killed:F3}, the hit at {death.Value:F3}");
        }
    }
}
