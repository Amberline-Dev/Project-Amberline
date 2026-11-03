using System;
using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-mark-death-lethal-lee-q-does-not-attach")]
    public static void LethalSonicWaveDoesNotAttachACorpseMark()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var lee = match.Hero(0);
            var victim = match.Hero(1);
            victim.Position = lee.Position + new Position2(350, 0);
            victim.Health = 1.0;
            match.CastGround(0, victim.Position);
            Advance(match, .8);
            TestAssert.False(victim.Alive, "production Sonic Wave lethally hit the recipient");
            TestAssert.False(victim.Buffs.Any(buff => buff.Name is
                "BlindMonkQOne" or "BlindMonkQOneChaos"),
                "a new nonpersistent Q mark cannot be added after committed death");
        }
    }

    [Check("spells-live-mark-death-recipient-cleanup-and-rapid-revive")]
    public static void RecipientDeathRetiresExistingMarksBeforeRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            foreach (var useSummonerRevive in new[] { false, true })
            foreach (var (champion, names) in new[]
                     {
                         ("LeeSin", new[] { team == Team.Order ? "BlindMonkQOne" :
                             "BlindMonkQOneChaos", "BlindMonkEOne" }),
                         ("Nidalee", new[] { "BushwhackDebuff" }),
                         ("Vayne", new[] { "VayneSilveredDebuff",
                             "VayneSilverParticle1", "VayneHunted" }),
                     })
            {
                using var match = new Match(champion, team);
                var caster = match.Hero(0);
                var recipient = match.Hero(1);
                foreach (var name in names)
                    match.Runtime.Buffs.AddBuff(recipient, name,
                        name == "VayneSilverParticle1" || name == "VayneHunted"
                            ? BuffType.Internal : BuffType.CombatDehancer,
                        12.0, name == "VayneSilverParticle1" ? recipient.NetId : caster.NetId,
                        canMitigateDuration: false);
                TestAssert.True(names.All(name => recipient.Buffs.Any(buff => buff.Name == name)),
                    $"{team}/{champion}: marks are active on the living recipient");
                TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
                match.Runtime.Delivery.Flush();
                TestAssert.True(names.All(name => recipient.Buffs.All(buff => buff.Name != name)),
                    $"{team}/{champion}: recipient death retires every exact mark alias");
                match.Runtime.Combat.ScheduleRespawn(recipient);
                if (useSummonerRevive)
                {
                    recipient.SummonerSpells[0] = "SummonerRevive";
                    TestAssert.True(match.TryCast(1, 0, recipient, summoner: true),
                        "dead-only Revive uses the production cast path");
                }
                else TestAssert.True(match.Runtime.Combat.RespawnImmediately(recipient));
                TestAssert.True(names.All(name => recipient.Buffs.All(buff => buff.Name != name)),
                    $"{team}/{champion}: rapid revive cannot inherit previous-life marks");
                if (champion == "Vayne" && useSummonerRevive)
                    AssertRevivedRecipientReconnectHasNoMarks(match, recipient, names);
            }
        }
    }

    private static void AssertRevivedRecipientReconnectHasNoMarks(
        Match match, Entity recipient, string[] removedAliases)
    {
        var runtime = match.Runtime;
        TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "the real Revive cast supplies a positive post-respawn snapshot buff");
        TestAssert.True(runtime.Session.Disconnect(2));
        TestAssert.True(runtime.Delivery.RemoveObserver(2));
        var reconnect = runtime.Session.AuthenticateReconnect(102,
            runtime.Session.BuildKeyCheck(2));
        TestAssert.True(reconnect is not null);
        TestAssert.True(runtime.Session.Receive(102, 1, [0xC5]).Any(output =>
            Opcode(output.Packet) == 0x57));
        runtime.Session.Receive(102, 1, [0xC6, 0, 0, 0, 0]);
        var spawnAt = reconnect!.SpawnAt ?? throw new InvalidOperationException(
            "marked recipient reconnect handshake did not arm spawn");
        runtime.World.Clock.SetVirtual(spawnAt);
        runtime.Session.RunSchedulers();
        runtime.Session.Receive(102, 1, [0xC6, 0, 0, 0, 0]);
        runtime.Session.RunSchedulers();
        TestAssert.True(reconnect.Spawned && reconnect.Loaded && reconnect.Ready);
        runtime.Delivery.AddObserver(new ReplicationObserver(
            reconnect.PlayerId, reconnect.Team, recipient.NetId, match.Captures[1],
            ready: reconnect.Ready, view: reconnect.View, clientId: reconnect.ClientId));
        match.Clear();
        runtime.Delivery.Flush();
        var ownerAdds = match.Captures[1].Packets.Where(packet =>
            Opcode(packet) == SpellPackets.BuffAddOpcode && packet.Plaintext.Length >= 13 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == recipient.NetId)
            .Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]))
            .ToHashSet();
        TestAssert.True(ownerAdds.Contains(SpellPackets.BuffNameHash("SummonerReviveSpeedBoost")),
            "reconnect positively restores the post-revive buff");
        TestAssert.False(removedAliases.Any(alias =>
            ownerAdds.Contains(SpellPackets.BuffNameHash(alias))),
            "reconnect does not replay marks from the recipient's previous life");
    }

    [Check("spells-live-mark-death-real-trap-caster-recipient-isolation")]
    public static void RealBushwhackMarkSurvivesCasterDeathButNotRecipientDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            recipient.Position = new Position2(12000, 12000);
            match.Cast(0, 1, caster);
            match.Runtime.Tick(.5);
            var trap = match.Runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == caster.NetId);
            recipient.Position = trap.Position;
            match.Runtime.Tick(1.0);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "BushwhackDebuff" &&
                buff.CasterNetId == caster.NetId), "real W trap hit installed the attached mark");
            TestAssert.True(match.Runtime.Combat.Kill(caster, recipient.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "BushwhackDebuff"),
                "caster death alone does not sweep a living recipient's independent mark");
            TestAssert.True(match.Runtime.Combat.Kill(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == "BushwhackDebuff"),
                "recipient death removes the real trap mark");
        }
    }

    [Check("spells-live-mark-death-real-vayne-basic-ring-cleanup")]
    public static void RealSilverBoltsMarkAndRingRetireWithRecipient()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Vayne", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            LearnSilverBolts(match, caster);
            Advance(match, .02);
            TestAssert.True(match.Runtime.Combat.ApplyHit(caster.NetId, recipient.NetId,
                10.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "VayneSilveredDebuff" &&
                buff.CasterNetId == caster.NetId), "actual basic hit installed W's first stack");
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
                "the first W stack owns its target ring");
            TestAssert.True(match.Runtime.Combat.Kill(caster, recipient.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "VayneSilveredDebuff"),
                "caster death alone does not sweep the living recipient's W stack");
            TestAssert.True(match.Runtime.Combat.Kill(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name is
                "VayneSilveredDebuff" or "VayneSilverParticle1"),
                "recipient death runs W removal callback and retires the shared ring");
        }
    }

    [Check("spells-live-mark-death-real-lee-q-caster-recipient-isolation")]
    public static void RealSonicWaveMarkOutlivesCasterButNotRecipient()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            recipient.Position = caster.Position + new Position2(350, 0);
            match.CastGround(0, recipient.Position);
            Advance(match, .8);
            var name = team == Team.Order ? "BlindMonkQOne" : "BlindMonkQOneChaos";
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == name &&
                buff.CasterNetId == caster.NetId), "real nonlethal Q installed its mark");
            TestAssert.True(match.Runtime.Combat.Kill(caster, recipient.NetId));
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == name),
                "caster death closes the recast window without sweeping the live target's mark");
            TestAssert.True(match.Runtime.Combat.Kill(recipient));
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == name),
                "recipient death retires the attached mark");
        }
    }

    [Check("spells-live-mark-death-yorick-first-death-cleans-mark")]
    public static void YorickFirstDeathClearsAttachedMarkBeforeTemporaryLife()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team, ally: true);
            var yorick = match.Hero(0);
            var enemy = match.Hero(1);
            var recipient = match.Hero(2);
            match.Cast(0, 3, recipient);
            match.Runtime.Tick(.3);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "the persistent revival controller is active before first death");
            match.Runtime.Buffs.AddBuff(recipient, "BushwhackDebuff", BuffType.CombatDehancer,
                12.0, enemy.NetId, canMitigateDuration: false);
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "BushwhackDebuff"));
            TestAssert.True(match.Runtime.Combat.Kill(recipient, enemy.NetId));
            TestAssert.True(recipient.Alive && recipient.YorickRevival is not null,
                "the ordinary death entered Yorick temporary life");
            TestAssert.False(recipient.Buffs.Any(buff => buff.Name == "BushwhackDebuff"),
                "the attached mark was retired before the temporary life began");
            TestAssert.True(recipient.Buffs.Any(buff => buff.Name == "YorickRADelay"),
                "the independent revival state remains active");
            TestAssert.True(yorick.Alive, "caster lifecycle is independent");
        }
    }
}
