using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-shaco-b5-box-fade-baseline-reconnect-wake")]
    public static void ShacoBoxFadeRestoresAndWakesGradually()
    {
        // 19 October 2026: the box is the shipped scripts' (JackInTheBox.luaobj pushes 0.2 over 2 s when it is added and 1.0 over 0.1 s when it
        // wakes). A late observer is told the sleeping fade from the unit's script fade ledger (Entity.ScriptFades); the fade ids are the
        // script host's counter, not fixed numbers, so the packets are matched by amount and duration.
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        enemy.Position = new Position2(11000, 11000);
        match.CastGround(0, hero.Position + new Position2(200, 0), 1);
        for (var step = 0; step < 100 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.ShacoBox); step++)
            runtime.Tick(.02);
        var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
        runtime.Delivery.Flush();
        AssertFade(match.Captures[0].Packets, .2f, 2f);
        for (var step = 0; step < 105; step++) runtime.Tick(.02);
        ReconnectShacoObserver(match);
        AssertFade(match.Captures[0].Packets, .2f, 2f);
        TestAssert.False(box.Buffs.Any(buff => buff.Name == "EndKill"), $"box still dormant before the wake probe; enemy at {enemy.Position}, box at {box.Position}");
        // Wake needs a SEEN candidate within 350 units of the box (JackInTheBox.luaobj).
        enemy.Position = box.Position + new Position2(200, 0);
        TestAssert.True(runtime.Spells.CanSeeTarget(box, enemy), "wake target has production team sight");
        match.Clear();
        for (var step = 0; step < 30 && !box.Buffs.Any(buff => buff.Name == "EndKill"); step++) runtime.Tick(.02);
        runtime.Delivery.Flush();
        TestAssert.True(box.Buffs.Any(buff => buff.Name == "EndKill"), $"box woke (box {box.NetId}, alive {box.Alive}, buffs {string.Join(",", box.Buffs.Select(b => b.Name))})");
        AssertFade(match.Captures[0].Packets, 1f, .1f);

        void AssertFade(IReadOnlyList<PlaintextPacket> packets, float amount, float duration)
        {
            TestAssert.True(packets.Any(packet => packet.Plaintext.Length == 15 && Opcode(packet) == 0xBA &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == box.NetId &&
                BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[7..]) == duration &&
                BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[11..]) == amount),
                "shipped box fade amount and duration: " + string.Join(";", packets.Where(packet => Opcode(packet) == 0xBA).Select(packet =>
                    $"{packet.Plaintext.Length}:{BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..])}:{Convert.ToHexString(packet.Plaintext.Span)}")));
        }
    }

    [Check("server.playtest-mana-and-win-commands")]
    public static void ResourceCheatAndVictoryUseProductionLifecycle()
    {
        foreach (var champion in new[] { "Annie", "LeeSin" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            long connection = 1;
            var maximum = hero.MaxMana;
            hero.Mana = 0;
            Command("/mana on");
            TestAssert.True(hero.UnlimitedResource);
            TestAssert.Equal(maximum, hero.Mana);
            TestAssert.True(runtime.HeroStats.Snapshot(hero).ManaCosts.All(cost => cost == 0));
            hero.Mana = 0; // Validate both affordability and consumption at the shared boundary.
            var slot = champion == "Annie" ? 2 : 1;
            TestAssert.True(match.TryCast(0, slot, hero));
            TestAssert.Equal(0.0, hero.Mana, "mana and energy may never underflow while unlimited");
            TestAssert.Equal(maximum, hero.MaxMana, "the cheat must not alter resource scaling");
            Command("/mana invalid");
            TestAssert.True(hero.UnlimitedResource);
            Command("/mana off");
            TestAssert.False(hero.UnlimitedResource);
            TestAssert.True(runtime.HeroStats.Snapshot(hero).ManaCosts.Any(cost => cost > 0));
            Command("/mana");
            TestAssert.True(hero.UnlimitedResource);
            TestAssert.True(runtime.Session.Disconnect(connection));
            connection = 101;
            var reconnected = runtime.Session.AuthenticateReconnect(connection, runtime.Session.BuildKeyCheck(1));
            TestAssert.True(reconnected is not null && ReferenceEquals(hero, reconnected.Hero));
            runtime.Session.Spawn(connection);
            runtime.Session.MarkReady(connection);
            reconnected!.Loaded = true;
            TestAssert.True(hero.UnlimitedResource, "reconnect retains the same match-scoped resource policy");
            runtime.Combat.Kill(hero, match.Hero(1).NetId);
            TestAssert.True(hero.UnlimitedResource, "death retains match-scoped testing preferences");
            Command("/win bad");
            TestAssert.False(runtime.Combat.GameOver);
            Command("/win");
            var result = runtime.Session.Result!;
            TestAssert.True(result is not null, "a dead player can end a testing match");
            TestAssert.Equal(team.ToString(), result!.Winner);
            TestAssert.False(result.Surrendered);
            TestAssert.False(runtime.World.Entities.Values.Single(e =>
                e.Kind == EntityKind.Nexus && e.Team != team).Alive);
            Command("/win");
            TestAssert.True(ReferenceEquals(result, runtime.Session.Result), "duplicate command keeps the original result");

            void Command(string text)
            {
                var packet = ChatPackets.Envelope(0, false, Encoding.ASCII.GetBytes(text));
                runtime.Session.Receive(connection, ChatPackets.Channel, packet.ToWireBytes(runtime.Cipher));
            }
        }
    }

    [Check("replication.playtest-shaco-fade-fog-and-reveal")]
    public static void ShacoFadeCleanupReachesKnownHiddenActors()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var observer = runtime.Delivery.Observers.Single(o => o.HeroNetId == match.Hero(1).NetId);
        var capture = match.Captures[1];
        TestAssert.True(observer.View.Known.Contains(shaco.NetId));
        // W33: the shipped scripts own the fades. Deceive's DeceiveFade pushes a 0.2 layer (0xBA) at the cast and its end pushes a fully
        // opaque layer (FadeAmount 1), which is not a pop: a fog-hidden observer misses it and must be repaired at the reveal.
        match.Clear();
        match.Cast(0, 0, shaco);
        runtime.Tick(.1);
        runtime.Delivery.Flush();
        TestAssert.True(capture.Packets.Any(p => IsFade(p, 0xBA)), "the stealth fade push reaches the observer that sees the cast");
        match.Hero(1).Position = new(12000, 2000);
        runtime.Delivery.Flush();
        runtime.Tick(.5);
        runtime.Delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(shaco.NetId));
        match.Clear();
        runtime.Tick(4);
        runtime.Delivery.Flush();
        TestAssert.False(shaco.Buffs.Any(buff => buff.Name == "Deceive"), "the stealth has ended while the observer could not see it");
        // The full-opacity push is routed to the known actor in fog like a pop (it carries no hidden position): the observer's client drops
        // the 0.2 layer while it cannot see the unit.
        TestAssert.True(capture.Packets.Any(p => IsFade(p, 0xBA) && BitConverter.ToSingle(p.Plaintext.Span[11..15]) >= 1.0f),
            "the fully opaque layer reaches the observer that cannot see Shaco");
        match.Clear();
        runtime.Delivery.Enqueue(SpellPackets.CharacterFade(shaco.NetId, 21320));
        runtime.Delivery.Flush();
        TestAssert.True(capture.Packets.Any(p => IsFade(p, 0x34)), "a fade pop is position-free fog cleanup");
        match.Clear();
        runtime.Delivery.Enqueue(SpellPackets.CharacterFade(0x4000FFFF, 21320));
        runtime.Delivery.Flush();
        TestAssert.False(capture.Packets.Any(p => p.Plaintext.Span[0] == 0x34), "unknown actors stay undisclosed");
        match.Hero(1).Position = shaco.Position + new Position2(200, 0);
        runtime.Delivery.Flush();
        TestAssert.False(capture.Packets.Any(p => IsFade(p, 0xBA) && BitConverter.ToSingle(p.Plaintext.Span[11..15]) < 1.0f),
            "the reveal snapshot replays no translucent layer: normal opacity was already repaired in fog");

        bool IsFade(PlaintextPacket packet, byte opcode) => packet.Plaintext.Length >= 7 &&
            packet.Plaintext.Span[0] == opcode && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == shaco.NetId;
    }

    [Check("replication.playtest-vayne-fade-reentry-is-idempotent")]
    public static void VayneFadeReentryDoesNotStackPushes()
    {
        using var match = new Match("Vayne", Team.Order);
        var runtime = match.Runtime;
        var vayne = match.Hero(0);
        var enemy = match.Hero(1);
        var observer = runtime.Delivery.Observers.Single(o => o.HeroNetId == match.Hero(1).NetId);
        var capture = match.Captures[1];
        string[] heldBuffs = ["VayneInquisition", "VayneSilveredBolts", "VayneTumbleBonus"];
        vayne.Position = new Position2(7000, 7000);
        enemy.Position = vayne.Position + new Position2(350, 0);
        // Learn W while the enemy sees Vayne: CharOnLevelUpSpell adds the Silver Bolts aura (19 October 2026, Vayne runs from her script).
        LearnSilverBolts(match, vayne);
        runtime.Tick(.05);
        TestAssert.Equal(1, BuffAdds("VayneSilveredBolts"), "Silver Bolts is added in sight");

        // Use the real R -> Q path. Q's stealth hides Vayne from the enemy
        // observer but its fade push still routes through the old visible set.
        match.Cast(0, 3, vayne);
        match.Clear();
        match.Cast(0, 0, vayne);
        TestAssert.True(vayne.Buffs.Any(buff => buff.Name == "VayneTumbleFade"),
            "R followed by Q installs Vayne stealth");
        TestAssert.Equal(1, FadeCount(0xBA), "Q sends one fade push to the enemy observer");
        match.Clear();

        // A reveal ward revisits the already-known Vayne while Q stealth is
        // active. Native fade entries are keyed by ID, so a second push would
        // survive the single expiry pop.
        var ward = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Ward,
            UnitType.Monster, enemy.Team, vayne.Position)
        {
            Name = "VisionWard",
            IsWard = true,
            WardOwnerNetId = enemy.NetId,
            RevealsStealth = true,
            Alive = true,
            Created = true,
            Visible = true,
            Health = 3.0,
            MaxHealth = 3.0,
            Radius = 1.0,
        };
        runtime.World.Register(ward);
        runtime.Delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(vayne.NetId),
            "a reveal ward restores Vayne while Q stealth is active");
        TestAssert.Equal(0, FadeCount(0xBA),
            "a reveal snapshot does not push an active Vayne fade twice");
        // playtest B2 YV8: the client kept these buffs through fog; a second
        // 0xBF would renew them and replay their activation sounds.
        foreach (var name in heldBuffs)
            TestAssert.Equal(0, BuffAdds(name), $"re-entry does not re-add held {name}");

        // Reconnect while the fade is active. The participant view is reset,
        // then the fresh snapshot must send exactly one active fade again.
        TestAssert.True(runtime.Session.Disconnect(2), "enemy connection detaches");
        var reconnected = runtime.Session.AuthenticateReconnect(102,
            runtime.Session.BuildKeyCheck(2));
        TestAssert.True(reconnected is not null, "enemy reconnect authenticates");
        TestAssert.False(observer.View.CharacterFades.Any(layer => layer.Actor == vayne.NetId),
            "reconnect reset clears the observer fade state");
        runtime.Session.Spawn(102);
        TestAssert.True(runtime.Session.MarkReady(102), "reconnected enemy reaches ready");
        reconnected!.Loaded = true;
        match.Clear();
        runtime.Delivery.Flush();
        TestAssert.Equal(1, FadeCount(0xBA),
            "the fresh reconnect snapshot sends one active Vayne fade");
        foreach (var name in heldBuffs)
        {
            // Batch 181: VayneTumble's own buff and VayneTumbleBonus (BuffName VayneTumble) reach the client under one name, in their
            // own slots; each live buff sharing the name is re-added once.
            var sharing = vayne.Buffs.Count(buff =>
                Amberline.Data.GameData.Default.ClientBuffName(buff.Name) == WireName(name));
            TestAssert.True(sharing >= 1, $"{name} is live at the reconnect");
            TestAssert.Equal(sharing, BuffAdds(name), $"the fresh reconnect snapshot adds {name} once");
        }

        // Hide Vayne again, let Q expire, and ensure the known hidden actor
        // receives the position-free cleanup pop.
        runtime.World.Remove(ward.NetId);
        enemy.Position = new Position2(12000, 2000);
        runtime.Delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(vayne.NetId),
            "Vayne hides again after the reveal source leaves");
        match.Clear();
        runtime.Tick(1.6);
        // 19 October 2026: VayneTumbleFade.OnBuffDeactivate pushes full opacity (BBPushCharacterFade 1.0 over 0 s) and never pops; the fade ids are the
        // script host's counter. The push is the cleanup of the activation's layer, so it reaches the known hidden observer like a pop used to.
        TestAssert.Equal(0, FadeCount(0x34), "the script never pops its fade layer");
        TestAssert.Equal(1, FadeCount(0xBA), "stealth expiry sends one full-opacity push to the known hidden observer");
        TestAssert.True(capture.Packets.Where(packet => packet.Plaintext.Length >= 15 && packet.Plaintext.Span[0] == 0xBA).All(packet =>
                BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[11..]) >= 1.0f), "and it restores full opacity");
        TestAssert.True(vayne.ScriptFades.Count > 0 && vayne.ScriptFades.All(layer => layer.Opacity >= 1.0f),
            "no translucent layer is left to replay at the next reveal");
        TestAssert.True(observer.View.CharacterFades.Count(layer => layer.Actor == vayne.NetId) == 2,
            "the observer holds the activation layer and the restoring one, as the visible observers do");

        // An R recast out of sight replaces the held buff: its 0x7F reaches
        // the known hidden observer, the new 0xBF does not, so the next
        // re-entry sends exactly that one missed add.
        vayne.SpellCooldowns[3] = 0.0;
        vayne.Mana = vayne.MaxMana;
        match.Cast(0, 3, vayne);
        TestAssert.False(observer.View.Visible.Contains(vayne.NetId), "R is recast out of sight");
        match.Clear();
        enemy.Position = vayne.Position + new Position2(350, 0);
        runtime.Delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(vayne.NetId), "Vayne re-enters sight");
        TestAssert.Equal(1, BuffAdds("VayneInquisition"), "the R missed in fog is added once");
        TestAssert.Equal(0, BuffAdds("VayneSilveredBolts"), "held Silver Bolts is not re-added");
        TestAssert.Equal(0, BuffAdds("VayneTumbleBonus"), "held Tumble bonus is not re-added");

        // A same-count renewal in fog: the observer misses its 0xBF, so its
        // client keeps the older timer. The ledger's end time differs from the
        // renewed one and re-entry re-sends that add once; the unchanged
        // Silver Bolts, counted down through the same fog, stays held.
        enemy.Position = new Position2(12000, 2000);
        runtime.Delivery.Flush();
        TestAssert.False(observer.View.Visible.Contains(vayne.NetId), "Vayne hides for the renewal");
        runtime.Tick(.5);
        var inquisition = vayne.Buffs.Single(buff => buff.Name == "VayneInquisition");
        var renewed = runtime.Buffs.AddStackingBuff(vayne, "VayneInquisition", inquisition.Type,
            inquisition.ExpiresAt + 3.0, vayne.NetId, maxStacks: 1, perStackPayload: inquisition.Payload);
        TestAssert.True(ReferenceEquals(inquisition, renewed) && inquisition.Stacks == 1,
            "the renewal keeps the live buff and its count");
        runtime.Tick(.5);
        match.Clear();
        enemy.Position = vayne.Position + new Position2(350, 0);
        runtime.Delivery.Flush();
        TestAssert.True(observer.View.Visible.Contains(vayne.NetId), "Vayne re-enters after the renewal");
        TestAssert.Equal(1, BuffAdds("VayneInquisition"), "the renewal missed in fog is re-sent once");
        TestAssert.Equal(0, BuffAdds("VayneSilveredBolts"), "the unchanged Silver Bolts is not re-added");

        int FadeCount(byte opcode) => capture.Packets.Count(packet => packet.Plaintext.Length >= 5 &&
            packet.Plaintext.Span[0] == opcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == vayne.NetId);

        int BuffAdds(string name) => capture.Packets.Count(packet => packet.Plaintext.Length >= 13 &&
            packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == vayne.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash(WireName(name)));

        // Batch 357 (restates batch 181): a buff's add carries the binding stem (Batch 357: binding stem; GameData.ClientBuffName).
        static string WireName(string name) => Amberline.Data.GameData.Default.ClientBuffName(name);
    }

    [Check("spells-summoners-playtest-fountain-targetability")]
    public static void FountainIsNeverAnAttackTargetButAllyTeleportMayTargetIt()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.SummonerSpells[0] = "SummonerTeleport";
            var shrine = runtime.World.Entities.Values.Single(e => e.Team == team && TurretRules.IsFountainTurret(e));
            TestAssert.False(runtime.Combat.IsTargetable(shrine, hero));
            TestAssert.False(runtime.Combat.IsTargetable(shrine, match.Hero(1)));
            // Batch 220 (owner decision, 25 October 2026): Teleport may target the fountain turret, because the shipped TargetExecute
            // only types its target (CO_IS_TYPE_AI, then CO_IS_TYPE_TURRET). The fountain's attack protection is unchanged, and an
            // enemy fountain is not an allied turret, which the inibin's AffectFriends keeps out.
            var foeShrine = runtime.World.Entities.Values.Single(e => e.Team != team && TurretRules.IsFountainTurret(e));
            TestAssert.False(match.TryCast(0, 0, foeShrine, summoner: true), "an enemy fountain is refused");
            TestAssert.Equal(0.0, hero.SummonerCooldowns[0]);
            TestAssert.True(match.TryCast(0, 0, shrine, summoner: true), "Teleport may target the own fountain turret");
            TestAssert.True(hero.SummonerCooldowns[0] > 0.0, "the teleport on the fountain starts its cooldown");
            hero.SummonerCooldowns[0] = 0.0; // the lane turret below needs the same slot ready
            var turret = runtime.World.Entities.Values.First(e => e.Team == team && TurretRules.IsLaneTurret(e));
            TestAssert.True(match.TryCast(0, 0, turret, summoner: true), "allied lane turrets remain legal");
            var protection = CombatPackets.TurretStats(shrine.NetId,
                new TurretStatsSnapshot(9999, 999, Protected: true, Targetable: false)).Plaintext.Span;
            // Turret's block 1 is fixed by the matched 126 replication schema.
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(protection[71..75]));
            TestAssert.Equal(CombatPackets.SFlagNontargetableAll, BinaryPrimitives.ReadUInt32LittleEndian(protection[75..79]));
        }
    }

    /// <summary>
    /// Batch 220 item 6b: Summoner Fortify's TargetExecute tests only the target's type (CO_IS_TYPE_TURRET) and adds the
    /// invulnerability row to each turret the cast reaches. Its inibin area (AffectFriends | AffectTurrets, CastRadius 25000)
    /// reaches every friendly turret on Map1, the fountain's included (owner decision, 25 October 2026).
    /// </summary>
    [Check("spells-summoner-fortify-reaches-the-fountain-turret")]
    public static void FortifyMakesTheFountainTurretInvulnerableToo()
    {
        var spell = GameData.Default.Spell("SummonerFortify");
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(spell));
        TestAssert.True(flags.Contains("AffectFriends") && flags.Contains("AffectTurrets"),
            "precondition: Fortify's inibin area is friends and turrets");
        TestAssert.Near(25000.0, spell.CastRadius, 1e-9, "precondition: Fortify's CastRadius");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.SummonerSpells[0] = "SummonerFortify";
            var shrine = runtime.World.Entities.Values.Single(e => e.Team == team && TurretRules.IsFountainTurret(e));
            var lane = runtime.World.Entities.Values.First(e => e.Team == team && TurretRules.IsLaneTurret(e));
            TestAssert.True(match.TryCast(0, 0, hero, summoner: true), "Fortify is cast");
            foreach (var turret in new[] { shrine, lane })
                TestAssert.True(turret.Buffs.Any(buff => buff.Type == BuffType.Invulnerability),
                    $"{turret.Name} takes Fortify's invulnerability");
        }
    }
}
