using Amberline.Network.Sessions;
using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 179 (review round 2): the Recall checks drive the production composition (the nested Match: MatchRuntime.CreateProduction,
    // the real summoner module and channel host) and assert both the server state and the packets the caster's client receives.
    // Timing: the press starts a 0.5 s windup (Recall.inibin CastTime); the channel begins at the cast frame and runs ChannelDuration
    // 8.0 s, so the teleport home lands 8.5 s after the press.

    private const double RecallWindupSeconds = 0.5;
    private const double RecallChannelSeconds = 8.0;

    [Check("spells-recall-inibin-channel-flags")]
    public static void RecallInibinChannelFlags()
    {
        // What Recall.inibin says, read through GameData: the channel is cancellable by a player's orders and casts, the 0.5 s windup
        // is not, the spell is not an instacast, and the channel is ChannelDuration 8.0 long.
        var recall = GameData.Default.Spell("Recall");
        TestAssert.False(recall.CantCancelWhileChanneling, "Recall.inibin has no CantCancelWhileChanneling");
        TestAssert.True(recall.CantCancelWhileWindingUp, "Recall.inibin has CantCancelWhileWindingUp");
        TestAssert.False(recall.IsInstant, "Recall is a SpellCaster, not an instacast");
        TestAssert.Near(0.5, recall.CastTime, 1e-9, "Recall CastTime");
        TestAssert.False(GameData.Default.SpellHasUncancellableChannel("Recall"), "the engine's channel is cancellable");
        TestAssert.Near(8.0, GameData.Default.SpellChannelDuration("Recall"), 1e-9, "Recall ChannelDuration");
    }

    [Check("spells-recall-full-channel-teleports-home-for-both-teams")]
    public static void RecallFullChannelTeleportsHomeForBothTeams()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(5000, 5000);
            var caster = match.Captures[0];
            match.Runtime.Delivery.Flush(); match.Clear();
            var spawn = MapDefinitions.TeamSpawn(team);

            match.Cast(0, SpellSystem.RecallSlot, hero);
            TestAssert.True(ContainsCastTimes(caster, (float)RecallWindupSeconds, 0f, 8.5f),
                $"{team}: the cast packet carries DesignerCastTime 0.5 and DesignerTotalTime 8.5");
            // The cast frame: the windup ends and the channel begins with its effect and aura.
            match.Runtime.Tick(RecallWindupSeconds);
            match.Runtime.Delivery.Flush();
            var effect = match.Runtime.Spells.RecallEffectId(hero);
            TestAssert.True(effect != 0 && match.Runtime.Spells.RecallRemaining(hero) > 0.0, $"{team}: the channel begins at the cast frame");
            TestAssert.Equal(1, EffectCreatesNamed(caster, effect, "TeleportHome"), $"{team}: the caster's client gets TeleportHome");
            // Still channelling at 8.4 s after the press (the channel is 7.9 s in).
            for (var step = 0; step < 79; step++) match.Runtime.Tick(0.1);
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, $"{team}: still channelling at 8.4 s");
            TestAssert.True(RecallDistance(hero.Position, spawn) > 1000.0, $"{team}: not yet home at 8.4 s");
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(0, CountOpcode(caster, VisionPackets.TeleportOpcode), $"{team}: no teleport before the channel ends");
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();

            // The channel ends at 8.5 s: the teleport home, the camera on the fountain, the arrival effect, and the recall is over.
            TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, $"{team}: the channel is over");
            TestAssert.True(RecallDistance(hero.Position, spawn) < 25.0, $"{team}: the hero stands at its team's fountain");
            TestAssert.True(CountOpcode(caster, VisionPackets.TeleportOpcode) > 0, $"{team}: the teleport packet is sent");
            TestAssert.True(ContainsFloats(caster, VisionPackets.SetCameraPositionOpcode, (float)hero.Position.X) &&
                ContainsFloats(caster, VisionPackets.SetCameraPositionOpcode, (float)hero.Position.Z),
                $"{team}: the camera moves to the fountain");
            TestAssert.Equal(1, CountCreatesNamed(caster, "teleportarrive"), $"{team}: the arrival effect is created once");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"), $"{team}: the Recall buff is gone");
            // Recall.luaobj removes TeleportHome only in ChannelingCancelStop; a completed channel leaves the particle to its own lifetime (as
            // the hand-written completion did, which never sent a kill either). The engine no longer tracks the effect.
            TestAssert.Near(0.0, match.Runtime.Spells.RecallEffectId(hero), 0.0, $"{team}: the channel no longer holds TeleportHome");
            TestAssert.Equal(0, CountKill(caster, effect), $"{team}: no removal is sent on completion (the script's success path has none)");
        }
    }

    [Check("spells-recall-move-order-cancels-the-channel-and-the-windup")]
    public static void RecallMoveOrderCancelsTheChannelAndTheWindup()
    {
        // During the channel: the order cancels at once and no teleport follows.
        using (var match = new Match("Annie", Team.Order))
        {
            var hero = match.Hero(0);
            hero.Position = new Position2(7000, 7000);
            match.Hero(1).Position = new Position2(5000, 5000);
            var caster = match.Captures[0];
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, SpellSystem.RecallSlot, hero);
            match.Runtime.Tick(2.0);
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "during the channel: the channel runs before the move");
            var effect = match.Runtime.Spells.RecallEffectId(hero);
            match.Move(0, hero.Position + new Position2(0, 300));
            match.Runtime.Tick(0.1);
            TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "a move order cancels the channel");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"), "and the Recall buff");
            TestAssert.Equal(1, CountKill(caster, effect), "and the TeleportHome effect is removed");
            match.Runtime.Tick(9.0);
            TestAssert.True(RecallDistance(hero.Position, MapDefinitions.TeamSpawn(Team.Order)) > 1000.0, "no teleport home after a cancel");
            TestAssert.Equal(0, CountOpcode(caster, VisionPackets.TeleportOpcode), "and no teleport packet");
        }

        // During the 0.5 s windup: the held order is applied 0.1 s after the windup ends (batch 47), and it cancels the channel that began at
        // the cast frame. Nothing teleports.
        using (var match = new Match("Annie", Team.Order))
        {
            var hero = match.Hero(0);
            hero.Position = new Position2(7000, 7000);
            match.Hero(1).Position = new Position2(5000, 5000);
            var caster = match.Captures[0];
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, SpellSystem.RecallSlot, hero);
            match.Runtime.Tick(0.2);
            TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "during the windup: the recall runs");
            match.Move(0, hero.Position + new Position2(0, 300));
            match.Runtime.Tick(0.6);
            TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "a move order inside the windup cancels the recall after it");
            match.Runtime.Tick(9.0);
            TestAssert.True(RecallDistance(hero.Position, MapDefinitions.TeamSpawn(Team.Order)) > 1000.0, "no teleport home after the windup cancel");
            TestAssert.Equal(0, CountOpcode(caster, VisionPackets.TeleportOpcode), "and no teleport packet");
        }
    }

    [Check("spells-recall-attack-order-cancels-the-channel")]
    public static void RecallAttackOrderCancelsTheChannel()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(2.0);
        TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "during the channel: the channel runs before the attack order");
        match.Attack(0, enemy);
        match.Runtime.Tick(0.1);
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "an attack order cancels the channel");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"), "and the Recall buff");
    }

    [Check("spells-recall-damage-cancels-only-non-periodic-hits")]
    public static void RecallDamageCancelsOnlyNonPeriodicHits()
    {
        // Recall.luaobj's BuffOnTakeDamage sets WillRemove for any hit whose source type is not DAMAGESOURCE_PERIODIC; the cancel comes
        // from BuffOnUpdateActions on the next step.
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = new Position2(5000, 5000);
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(2.0);
        match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10.0, sourceIsPeriodic: true);
        match.Runtime.Tick(0.1);
        TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "a periodic hit does not cancel the channel");
        var recallBuff = match.Runtime.Spells.SummonerScripts!.Buffs!.Find(hero, "Recall");
        TestAssert.True(recallBuff is not null && recallBuff.InstanceVars["WillRemove"] is not true, $"a periodic hit does not set WillRemove (buff found: {recallBuff is not null})");
        match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10.0, sourceIsPeriodic: false);
        TestAssert.True(recallBuff is not null && recallBuff.InstanceVars["WillRemove"] is true,
            $"a non-periodic hit sets WillRemove (BuffOnTakeDamage ran; WillRemove={recallBuff?.InstanceVars["WillRemove"]})");
        // BuffOnUpdateActions runs on the 0.25 s actions grid (TickRate 0, BbBuffRuntime): the stop lands at the next grid line.
        match.Runtime.Tick(0.3);
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "a non-periodic hit cancels the channel at the next actions line");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"), "and the Recall buff");
    }

    [Check("spells-recall-spell-cast-cancels-the-channel")]
    public static void RecallSpellCastCancelsTheChannel()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var caster = match.Captures[0];
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(2.0);
        var effect = match.Runtime.Spells.RecallEffectId(hero);
        TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "during the channel: the channel runs before the cast");
        match.Cast(0, 0, enemy);
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "an accepted spell cast (Annie Q) cancels the channel");
        TestAssert.Equal(1, CountKill(caster, effect), "and the TeleportHome effect is removed");
    }

    [Check("spells-recall-death-ends-the-channel")]
    public static void RecallDeathEndsTheChannel()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var caster = match.Captures[0];
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(2.0);
        var effect = match.Runtime.Spells.RecallEffectId(hero);
        TestAssert.True(match.Runtime.Spells.RecallRemaining(hero) > 0.0, "during the channel: the recall runs before the death");
        TestAssert.True(match.Runtime.Combat.Kill(hero, enemy.NetId), "the hero dies");
        match.Runtime.Tick(0.1);
        TestAssert.Near(0.0, match.Runtime.Spells.RecallRemaining(hero), 0.0, "death ends the channel");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Recall"), "and the Recall buff");
        TestAssert.Equal(1, CountKill(caster, effect), "and the TeleportHome effect is removed");
    }

    [Check("spells-recall-second-press-is-refused")]
    public static void RecallSecondPressWhileRecallingIsRefused()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var caster = match.Captures[0];
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, SpellSystem.RecallSlot, hero);
        match.Runtime.Tick(2.0);
        var before = match.Runtime.Spells.RecallRemaining(hero);
        TestAssert.False(match.TryCast(0, SpellSystem.RecallSlot, hero), "a second B press while recalling is refused");
        TestAssert.Near(before, match.Runtime.Spells.RecallRemaining(hero), 1e-9, "and it does not restart the recall");
        match.Runtime.Delivery.Flush();
        TestAssert.Equal(1, CountCastAnswers(caster, (float)RecallWindupSeconds, 8.5f), "only the first press is announced");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a real Recall for <paramref name="hero"/> through the production summoner module: the same entry point a press of B
    /// takes (SpellSystem.HandleRecall), so the windup runs to the cast frame and the channel begins there. Batch 179 review.
    /// </summary>
    internal static void StartRecallWindup(MatchRuntime runtime, Entity hero)
    {
        var owner = runtime.Spells.LoadoutHooks?.OwnerFor(hero) ?? throw new InvalidOperationException("the hero has no loadout owner");
        var module = runtime.Spells.SummonerScripts ?? throw new InvalidOperationException("the production match binds no summoner module");
        var binding = module.RecallBinding(owner) ?? throw new InvalidOperationException("no Recall binding");
        module.BeginRecall(hero, binding, new Position3(hero.Position.X, 0.0, hero.Position.Z), 0.5);
    }

    private static double RecallDistance(Position2 a, Position2 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static int CountOpcode(Capture capture, byte opcode) => capture.Packets.Count(p => Opcode(p) == opcode);

    /// <summary>The cast packets (0xBD) whose DesignerCastTime, ExtraCastTime and DesignerTotalTime are these floats in that order.</summary>
    private static int CountCastAnswers(Capture capture, float cast, float total) => capture.Packets.Count(p =>
        Opcode(p) == SpellPackets.CastSpellAnswerOpcode && ContainsFloats(p.Plaintext.Span, cast, 0f, total));

    private static bool ContainsCastTimes(Capture capture, float cast, float extra, float total) => capture.Packets.Any(p =>
        Opcode(p) == SpellPackets.CastSpellAnswerOpcode && ContainsFloats(p.Plaintext.Span, cast, extra, total));

    private static bool ContainsFloats(Capture capture, byte opcode, float value) => capture.Packets.Any(p =>
        Opcode(p) == opcode && ContainsFloats(p.Plaintext.Span, value));

    private static bool ContainsFloats(ReadOnlySpan<byte> bytes, params float[] values)
    {
        for (var start = 0; start + 4 * values.Length <= bytes.Length; start++)
        {
            var matched = true;
            for (var index = 0; index < values.Length && matched; index++)
                matched = BinaryPrimitives.ReadSingleLittleEndian(bytes[(start + 4 * index)..]) == values[index];
            if (matched) return true;
        }
        return false;
    }

    /// <summary>A group effect create (0x8C) for <paramref name="id"/> whose asset is the shipped effect's name hash.</summary>
    private static int EffectCreatesNamed(Capture capture, uint id, string name) => capture.Packets.Count(p =>
        Opcode(p) == FxPackets.CreateGroupOpcode && p.EntityReferences.Any(r => r.NetId == id && r.Role == LeaguePacketEntityRole.Create) &&
        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name));

    private static int CountCreatesNamed(Capture capture, string name) => capture.Packets.Count(p =>
        Opcode(p) == FxPackets.CreateGroupOpcode && p.Plaintext.Length >= 10 &&
        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name));

    /// <summary>The effect removal (0x3D) of <paramref name="id"/>.</summary>
    private static int CountKill(Capture capture, uint id) => capture.Packets.Count(p =>
        Opcode(p) == FxPackets.KillOpcode && p.Plaintext.Length >= 9 &&
        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[5..]) == id);
}
