using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 945 (players, 28 November 2026: "Effects for taunts aren't played sometimes, both visual and/or audio effects ... Rammus E, Galio R and
/// Shen E"): the taunt BBApplyTaunt adds draws the particles of the shipped Taunt.luaobj's OnBuffActivate, chosen by the taunter's mark on the
/// taunted unit, on the taunter's and the taunted unit's clients, and the head particle ends with the taunt.
/// </summary>
public static class B945TauntEffectChecks
{
    private static readonly string[] TauntParticles =
        ["global_taunt", "galio_taunt_unit_indicator", "Global_Taunt_multi_unit", "shen_shadowDash_unit_impact"];

    /// <summary>The 0x8C creates of <paramref name="stem"/> a client got from <paramref name="after"/> on: the effect's net id and its bind.</summary>
    private static List<(uint Id, uint Bind)> Creates(WireCapture capture, double after, string stem) =>
        capture.Packets.Where(entry => entry.At >= after && EffectNamed(entry.Packet, stem))
            .Select(entry => (BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[25..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[29..])))
            .ToList();

    private static bool Killed(WireCapture capture, uint effect) => capture.Packets.Any(entry =>
        entry.Packet.Plaintext.Span[0] == FxPackets.KillOpcode && BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[5..]) == effect);

    /// <summary>Only <paramref name="expected"/> of the four taunt particles reached the client.</summary>
    private static void OnlyThese(WireCapture capture, double after, string label, params string[] expected)
    {
        foreach (var stem in TauntParticles.Except(expected))
            TestAssert.Equal(0, Creates(capture, after, stem).Count, $"{label}: no {stem} (another taunter's particle)");
    }

    /// <summary>
    /// Puncturing Taunt (E) adds PuncturingTauntArmorDebuff, then BBApplyTaunt: global_taunt.troy on the foe's head, on both clients. A recast on
    /// the still-taunted foe replaces the debuff, whose OnBuffDeactivate removes the first taunt: its particle is killed and the new taunt draws its
    /// own. The last one is killed when the taunt ends.
    /// </summary>
    [Check("spells.b945-rammus-e-draws-the-taunt-on-its-target-until-it-ends")]
    public static void RammusEDrawsTheTaunt()
    {
        using var match = new Match("Rammus");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7200.0, 7000.0);
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        var start = match.Now;
        TestAssert.True(match.Cast(2, target: match.Foe), "Puncturing Taunt is cast on the foe");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "Taunt") is not null, "the foe is taunted");
        foreach (var (label, capture) in new[] { ("Rammus's client", own), ("the foe's client", enemy) })
        {
            var drawn = Creates(capture, start, "global_taunt");
            TestAssert.Equal(1, drawn.Count, $"{label} gets global_taunt once");
            TestAssert.Equal(match.Foe.NetId, drawn[0].Bind, $"{label}: global_taunt is bound to the taunted foe");
            OnlyThese(capture, start, label, "global_taunt");
        }

        match.Refill();
        TestAssert.True(match.Cast(2, target: match.Foe), "Puncturing Taunt is cast again while the foe is taunted");
        match.Advance(0.5);
        TestAssert.True(Buff(match.Foe, "Taunt") is not null, "the foe is taunted again");
        foreach (var (label, capture) in new[] { ("Rammus's client", own), ("the foe's client", enemy) })
        {
            var drawn = Creates(capture, start, "global_taunt");
            TestAssert.Equal(2, drawn.Count, $"{label}: the second taunt draws its own global_taunt");
            TestAssert.True(Killed(capture, drawn[0].Id), $"{label}: the first taunt's particle is killed with it");
            TestAssert.True(!Killed(capture, drawn[1].Id), $"{label}: the second taunt's particle stays while it holds");
        }
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "Taunt") is null, "the taunt is over");
        foreach (var (label, capture) in new[] { ("Rammus's client", own), ("the foe's client", enemy) })
            TestAssert.True(Killed(capture, Creates(capture, start, "global_taunt")[1].Id), $"{label}: the particle ends with the taunt");
    }

    /// <summary>
    /// A lane minion Puncturing Taunt hits carries global_taunt.troy too, and the particle goes when the minion dies under the taunt (the death
    /// removes the global Taunt, batch 724, and the lifecycle kills its particle with it).
    /// </summary>
    [Check("spells.b945-a-taunted-minion-that-dies-loses-its-taunt-particle")]
    public static void TauntedMinionThatDiesLosesTheParticle()
    {
        using var match = new Match("Rammus");
        match.Learn(1);
        match.Refill();
        var minion = LaneMinion(match, new Position2(7200.0, 7000.0));
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        var start = match.Now;
        TestAssert.True(match.Cast(2, target: minion), "Puncturing Taunt is cast on the minion");
        match.Advance(0.5);
        TestAssert.True(Buff(minion, "Taunt") is not null, "the minion is taunted");
        match.Runtime.Combat.ApplyHit(match.Hero.NetId, minion.NetId, 5000.0, DamageType.True,
            DamageSourceKind.Spell);
        match.Advance(0.2);
        TestAssert.True(!minion.Alive, "the minion died under the taunt");
        foreach (var (label, capture) in new[] { ("Rammus's client", own), ("the minion's team's client", enemy) })
        {
            var drawn = Creates(capture, start, "global_taunt");
            TestAssert.Equal(1, drawn.Count, $"{label} gets global_taunt on the minion once");
            TestAssert.Equal(minion.NetId, drawn[0].Bind, $"{label}: bound to the minion");
            TestAssert.True(Killed(capture, drawn[0].Id), $"{label}: the particle is killed when the minion dies");
        }
    }

    /// <summary>
    /// Idol of Durand (R): every unit it taunts (TargetExecute on the cast, then BuffOnUpdateActions every 0.5 s for a unit without
    /// GalioIdolOfDurandMarker) carries galio_taunt_unit_indicator.troy, the foe in range at the cast and an enemy that walks in during the channel;
    /// the channel's end frees them and their particles go.
    /// </summary>
    [Check("spells.b945-galio-r-draws-the-taunt-indicator-on-each-unit-it-taunts")]
    public static void GalioRDrawsTheIndicator()
    {
        using var match = new Match("Galio");
        match.Learn(1);
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        var start = match.Now;
        TestAssert.True(match.Cast(3), "Idol of Durand is cast");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "Taunt") is not null, "the foe in range is taunted");
        match.FarFoe.Position = new Position2(match.Hero.Position.X, match.Hero.Position.Z + 400.0);
        match.Advance(0.6);
        TestAssert.True(Buff(match.FarFoe, "Taunt") is not null, "the enemy that walked in is taunted");
        foreach (var (label, capture) in new[] { ("Galio's client", own), ("the foe's client", enemy) })
        {
            var drawn = Creates(capture, start, "galio_taunt_unit_indicator");
            TestAssert.Equal(2, drawn.Count, $"{label} gets the indicator twice ({drawn.Count})");
            TestAssert.Equal(match.Foe.NetId, drawn[0].Bind, $"{label}: the first is on the foe taunted at the cast");
            TestAssert.Equal(match.FarFoe.NetId, drawn[1].Bind, $"{label}: the second is on the enemy that walked in");
            OnlyThese(capture, start, label, "galio_taunt_unit_indicator");
        }
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "Taunt") is null && Buff(match.FarFoe, "Taunt") is null, "the channel's end freed both");
        foreach (var (label, capture) in new[] { ("Galio's client", own), ("the foe's client", enemy) })
            TestAssert.True(Creates(capture, start, "galio_taunt_unit_indicator").All(drawn => Killed(capture, drawn.Id)),
                $"{label}: both indicators end with their taunts");
    }

    /// <summary>
    /// Shadow Dash (E) collides with the foe 300 along its 575-unit line: ShenShadowDash.BuffOnCollision taunts it but draws nothing itself (its
    /// SelfExecute draws shen_shadowDash_unit_impact.troy only within 150 of the start, BuffOnMoveEnd only within 200 of the end for a unit not
    /// yet taunted). The taunt draws Global_Taunt_multi_unit.troy on the foe's head and the impact, whose ShenShadowDash/taunt is the taunt's sound.
    /// </summary>
    [Check("spells.b945-shen-e-taunt-mid-dash-draws-the-taunt-and-its-impact")]
    public static void ShenEDrawsTheTauntMidDash()
    {
        using var match = new Match("Shen");
        match.Learn(1);
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.5);
        var start = match.Now;
        TestAssert.True(match.Cast(2, at: new Position2(match.Hero.Position.X + 700.0, match.Hero.Position.Z)), "Shadow Dash is cast through the foe");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Foe, "Taunt") is not null, "the foe it collided with is taunted");
        foreach (var (label, capture) in new[] { ("Shen's client", own), ("the foe's client", enemy) })
        {
            var head = Creates(capture, start, "Global_Taunt_multi_unit");
            var impact = Creates(capture, start, "shen_shadowDash_unit_impact");
            TestAssert.Equal(1, head.Count, $"{label} gets Global_Taunt_multi_unit once");
            TestAssert.Equal(match.Foe.NetId, head[0].Bind, $"{label}: Global_Taunt_multi_unit is bound to the foe");
            TestAssert.Equal(1, impact.Count, $"{label} gets the impact (and its taunt sound) once");
            TestAssert.Equal(match.Foe.NetId, impact[0].Bind, $"{label}: the impact is bound to the foe");
            OnlyThese(capture, start, label, "Global_Taunt_multi_unit", "shen_shadowDash_unit_impact");
        }
        match.Advance(1.5);
        TestAssert.True(Buff(match.Foe, "Taunt") is null, "the taunt is over");
        foreach (var (label, capture) in new[] { ("Shen's client", own), ("the foe's client", enemy) })
            TestAssert.True(Killed(capture, Creates(capture, start, "Global_Taunt_multi_unit")[0].Id), $"{label}: the head particle ends with the taunt");
    }
}
