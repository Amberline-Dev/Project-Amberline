using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 947 (community report, 28 November 2026: "Ennemies spells in fog of war replay when you go in it"). Garen's Courage (GarenCommand.luaobj)
/// creates garen_command_cas.troy on Garen and keeps its id until the buff ends (BBSpellEffectRemove at OnBuffDeactivate), so the engine holds it in
/// the late-sight replay. The particle is single-particle emitters with no e-life (GameData.EndlessParticleSeconds: 1.5 s): a client that got it at
/// the cast has shown all of it by 1.5 s, while a copy created later plays it from its start. An enemy who knew Garen, lost him, and sees him again
/// while the buff runs gets the swirl only while it is still showing; the buff's lasting particle (garen_commandingPresence_unit_buf_self) still
/// comes late, and the ally who saw the cast gets the swirl once.
/// </summary>
public static class B947FogReplayChecks
{
    private const string Swirl = "garen_command_cas";
    private const string Lasting = "garen_commandingPresence_unit_buf_self";

    [Check("spells.b947-a-finished-cast-swirl-is-not-replayed-on-a-late-sight")]
    public static void FinishedCastSwirlIsNotReplayedOnALateSight()
    {
        TestAssert.Near(1.5, GameData.Default.SingleParticleSeconds(Swirl + ".troy") ?? -1.0, 1e-6, "the swirl's single particles have shown all by 1.5 s");
        TestAssert.True(GameData.Default.SingleParticleSeconds(Lasting + ".troy") is null, "the buff's own particle keeps showing");
        // Xerath's idle lightning (126: endless emitters whose 65535 rate the reader does not take for positive, EndlessParticleSeconds 0) is his lasting
        // look; it is never taken for a finished system.
        TestAssert.True(GameData.Default.SingleParticleSeconds("XerathIdle.troy") is null, "an endless system that is not single particles only keeps its late copies");
        var failures = new List<string>();
        foreach (var (delay, late) in new[] { (0.3, 1), (2.0, 0) })
        {
            try { Run(delay, late); }
            catch (Exception failure) { failures.Add($"reveal {delay} s after the cast: {failure.Message}"); }
        }
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void Run(double delay, int lateSwirls)
    {
        using var match = new Match("Garen");
        match.Learn(3);
        match.Refill();
        match.Hero.Position = new Position2(7000, 7000);
        match.Ally.Position = new Position2(7000, 6850);
        match.Foe.Position = new Position2(7350, 7000);
        var ally = Observe(match, Team.Order, match.Ally, 9001);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        var view = match.Runtime.Delivery.Observers.First(observer => ReferenceEquals(observer.Transport, enemy)).View;
        match.Advance(0.5);
        TestAssert.True(view.Visible.Contains(match.Hero.NetId), "the enemy sees Garen first");
        match.Foe.Position = new Position2(7000, 10500);
        match.Advance(0.5);
        TestAssert.False(view.Visible.Contains(match.Hero.NetId), "the enemy lost him");
        var cast = match.Now;
        TestAssert.True(match.Cast(1), "Courage is cast");
        match.Advance(delay);
        TestAssert.True(Buff(match.Hero, "GarenCommand") is not null, "the buff that keeps the swirl's id still runs at the reveal");
        var reveal = match.Now;
        match.Foe.Position = new Position2(7350, 7000);
        match.Advance(1.0);
        TestAssert.True(view.Visible.Contains(match.Hero.NetId), "the enemy sees him again");
        int Creates(WireCapture capture, double from, string stem) => capture.Packets.Count(entry => entry.At >= from && EffectNamed(entry.Packet, stem));
        TestAssert.Equal(0, Creates(enemy, cast, Swirl) - Creates(enemy, reveal, Swirl), "nothing reached the enemy in fog");
        TestAssert.Equal(lateSwirls, Creates(enemy, reveal, Swirl), lateSwirls == 1
            ? "the swirl still showing (0.3 s of its 1.5 s) comes with the reveal"
            : "a swirl that has shown all it will show is not played again at the reveal");
        TestAssert.Equal(1, Creates(enemy, reveal, Lasting), "the buff's lasting particle comes with the reveal");
        TestAssert.Equal(1, Creates(ally, cast, Swirl), "the ally who saw the cast gets the swirl once");
    }
}
