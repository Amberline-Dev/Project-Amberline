using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Network.Spectator;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 927 (owner report, 21 November 2026: "Spectator mode lacks all projectiles, e.g. Ryze Q, tower shot, Ryze E, caster minion
/// projectile"): a headless ten-bot match recorded for a spectator, its seats' packets against the stream read back with S1's decoder
/// (the chunk files the grid serves unchanged). It pins the server's half; it passed before batch 927 too (record 2026-11-21-batch-927).
/// </summary>
public static class SpectatorMissileChecks
{
    private const byte BasicAttackOpcode = 0x0D;
    private const byte MissileReplicationOpcode = 0x41;

    // The missile's net id in each packet that creates one: 0x0D [op][attacker][target][extra u8][missile]; the cast answer
    // [op][caster][sync i32][cast info], the 0x41 [op][caster][7 positions][speed][life][bounced][cast info]; the cast info's
    // MissileNetId is at +19 (SpellPackets.BuildCastInfo).
    private static uint? MissileOf(ReadOnlySpan<byte> packet)
    {
        int at;
        if (packet[0] == BasicAttackOpcode) at = 10;
        else if (packet[0] == SpellPackets.CastSpellAnswerOpcode) at = 9 + 19;
        else if (packet[0] == MissileReplicationOpcode) at = 0x62 + 19;
        else return null;
        return packet.Length >= at + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(packet[at..]) : null;
    }

    [Check("network.spectate-missiles-reach-the-stream")]
    public static void MissilesReachTheStream()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-missiles-").FullName;
        try
        {
            var key = SpectatorChunk.NewChunkKey();
            var folder = Path.Combine(root, "spectator");
            MatchRuntime? live = null;
            // Each missile a seat was told of, with its attacker's kind (a turret shot, a caster minion's bolt and a champion's spell missile
            // are told apart), and every 0x0D a seat got as it got it.
            var seatMissiles = new Dictionary<(byte Opcode, uint Missile), string>();
            var seatAttacks = new HashSet<string>();
            string KindOf(uint netId) => live?.World.Get(netId) switch
            {
                { UnitType: UnitType.Turret } => "turret",
                { MinionType: MinionType.Caster } => "caster-minion",
                { MinionType: not null } => "other-minion",
                { UnitType: UnitType.Hero } => "hero",
                null => "gone",
                _ => "other",
            };
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(927, Path.Combine(root, "match"), 4.0)
            {
                SpectatorStream = folder,
                SpectatorKey = key,
                OnStep = runtime => live = runtime,
                PacketSink = (_, packet) =>
                {
                    var bytes = packet.Plaintext.Span;
                    if (bytes.IsEmpty || MissileOf(bytes) is not uint missile || missile == 0) return;
                    seatMissiles.TryAdd((bytes[0], missile), KindOf(BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..])));
                    if (bytes[0] == BasicAttackOpcode) seatAttacks.Add(Convert.ToHexString(bytes));
                },
            });
            TestAssert.Equal("cap", report.Outcome, "the match ran to its cap");

            var index = SpectatorRecording.Read(folder);
            var chunkKey = Encoding.ASCII.GetBytes(key);
            var stream = index.Chunks.SelectMany(chunk => SpectatorChunkChecks.Client.Unpack(
                SpectatorChunkChecks.Client.Open(File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, chunk.Id)), chunkKey))).ToArray();

            // The replay the client needs, while replaying, the
            // unit's enter-visibility flag (set by the reveal, never cleared: the leave handler is a no-op in a replay) and a team bit
            // (EnterTeamVisibility / LeaveTeamVisibility). So each attacker must hold both when its missile packet plays.
            var enter = VisionPackets.EnterTeamVisibility(1, 0).Plaintext.Span[0];
            var leave = VisionPackets.LeaveTeamVisibility(1, 0).Plaintext.Span[0];
            var revealed = new HashSet<uint>();
            var masks = new Dictionary<uint, int>();
            // A view may take a missile as its cast answer or as a later 0x41, and drops targets it never held, so presence is by missile id;
            // a 0x0D needs the attacker and the target held by every view that gets it, so its bytes are a seat's.
            var streamMissiles = new HashSet<uint>();
            var foreignAttacks = new List<string>();
            var unseenAttackers = new List<string>();
            foreach (var (time, _, packet, _) in stream)
            {
                var unit = packet.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)) : 0u;
                if (packet[0] == VisionPackets.EnterVisibilityOpcode) revealed.Add(unit);
                else if (packet.Length == 6 && (packet[0] == enter || packet[0] == leave))
                    masks[unit] = packet[0] == enter ? masks.GetValueOrDefault(unit) | 1 << packet[5] : masks.GetValueOrDefault(unit) & ~(1 << packet[5]);
                else if (MissileOf(packet) is uint missile and not 0)
                {
                    streamMissiles.Add(missile);
                    if (packet[0] == BasicAttackOpcode && !seatAttacks.Contains(Convert.ToHexString(packet)) && foreignAttacks.Count < 5)
                        foreignAttacks.Add($"{missile} at {time:0.00}");
                    if ((!revealed.Contains(unit) || masks.GetValueOrDefault(unit) == 0) && unseenAttackers.Count < 5)
                        unseenAttackers.Add($"0x{packet[0]:X2} by {unit} at {time:0.00}");
                }
            }
            TestAssert.Equal(0, unseenAttackers.Count, "every attacker is revealed and holds a team bit when its missile plays: " +
                string.Join("; ", unseenAttackers));

            foreach (var (kind, opcode, what) in new[]
                     {
                         ("turret", BasicAttackOpcode, "a turret shot"),
                         ("caster-minion", BasicAttackOpcode, "a caster minion's bolt"),
                         ("hero", SpellPackets.CastSpellAnswerOpcode, "a champion's spell missile"),
                     })
            {
                var seen = seatMissiles.Where(entry => entry.Key.Opcode == opcode && entry.Value == kind).Select(entry => entry.Key.Missile).ToArray();
                TestAssert.True(seen.Length > 0, $"a seat was sent {what}");
                var missing = seen.Where(missile => !streamMissiles.Contains(missile)).ToArray();
                TestAssert.Equal(0, missing.Length, $"every {what} a seat got is in the stream ({seen.Length} seen): " +
                    string.Join(", ", missing.Take(5)));
            }
            var absent = seatMissiles.Keys.Select(entry => entry.Missile).Distinct().Where(missile => !streamMissiles.Contains(missile)).ToArray();
            TestAssert.Equal(0, absent.Length, "every missile a seat got is in the stream: " + string.Join(", ", absent.Take(5)));
            TestAssert.Equal(0, foreignAttacks.Count, "each 0x0D in the stream is a seat's copy, byte for byte: " + string.Join("; ", foreignAttacks));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
