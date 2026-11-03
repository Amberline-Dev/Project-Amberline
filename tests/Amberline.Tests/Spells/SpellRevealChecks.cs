using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// The spell reveal routine for spells: a cast whose data sets
/// SpellRevealsChampion shows the caster to the team of its first unit target, at the cast
/// position, for Map1 Constants.var ca_RevealAttackerTimeOut (4.5 s). The expected values below
/// come from the shipped files (spell inibins, Constants.var), not from the code under test.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const double RevealSeconds = 4.5;

    [Check("spells-live-spell-reveals-champion-flag")]
    public static void SpellRevealsChampionFollowsTheShippedFlagAndItsDefault()
    {
        var data = GameData.Default;
        // The stock loader reads the key with default 1, so an absent key reveals.
        foreach (var name in new[] { "Disintegrate" /* True */, "SummonerDot" /* 1 */, "AbsoluteZero" /* 1 */,
                     "OrianaDissonanceCommand" /* 1 */, "SummonerExhaust" /* absent */, "SummonerSmite" /* absent */ })
            TestAssert.True(data.Spell(name).RevealsChampion, $"{name} reveals its caster");
        foreach (var name in new[] { "OrianaIzunaCommand" /* False */, "YorickRavenous" /* False */,
                     "JavelinToss" /* False */, "CannonBarrage" /* 0 */ })
            TestAssert.False(data.Spell(name).RevealsChampion, $"{name} does not reveal its caster");
    }

    [Check("spells-live-spell-cast-reveals-caster-to-target-team")]
    public static void FlaggedUnitTargetSpellRevealsTheCasterToTheTargetsTeamOnly()
    {
        foreach (var victimKind in new[] { "enemy hero", "neutral monster" })
        {
            using var match = new Match("Annie", Team.Order, enemyChampion: "Yorick");
            var annie = match.Hero(0);
            var victim = victimKind == "enemy hero" ? match.Hero(1) : AddMonster(match, annie.Position + new Position2(250, 0));
            var team = (int)victim.Team;
            // Batch 293 (batch 279's seat): Disintegrate's windup stop seats Annie on her lattice anchor at once, so she casts
            // from there.
            var castFrom = match.Runtime.Movement.ClientAnchor(annie.Position);
            match.Clear();

            // Annie Q (Disintegrate) is unit-targeted and reveals (SpellRevealsChampion = True).
            TestAssert.True(CastAt(match, 0, 0, victim), "Disintegrate is accepted");
            TestAssert.Near(RevealSeconds, annie.AttackRevealRemaining[team], 1e-9,
                $"{victimKind}: the victim's team sees the caster for ca_RevealAttackerTimeOut");
            TestAssert.Equal(castFrom, annie.AttackRevealPosition[team], $"{victimKind}: bubble is at the cast point");
            foreach (var other in Enum.GetValues<Team>().Where(other => other != victim.Team && (int)other < 3))
                TestAssert.Near(0.0, annie.AttackRevealRemaining[(int)other], 0,
                    $"{victimKind}: team {other} is not shown the caster");
            if (victimKind != "enemy hero") continue;
            // Batch 52 (79d621b): the stock client runs RevealSpellCaster in its own cast frame and adds the
            // 400-unit bubble itself, so no bubble packet is sent to either team; the reveal stays server sight.
            var bubble = annie.NetId ^ 0x2000_0000u;
            TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                "victim's team receives no reveal bubble packet");
            TestAssert.Equal(0, match.Captures[0].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                "caster's team receives none");
        }
    }

    [Check("spells-live-spell-cast-without-flag-reveals-nothing")]
    public static void UnflaggedUnitTargetSpellRevealsNothing()
    {
        foreach (var victimKind in new[] { "enemy hero", "neutral monster" })
        {
            using var match = new Match("Yorick", Team.Order, enemyChampion: "Annie");
            var yorick = match.Hero(0);
            var victim = victimKind == "enemy hero" ? match.Hero(1) : AddMonster(match, yorick.Position + new Position2(250, 0));
            TestAssert.True(GameData.Default.Spell("YorickRavenous").TargetsUnit, "Yorick E is unit-targeted");
            match.Clear();

            // Yorick E (YorickRavenous) is unit-targeted with SpellRevealsChampion = False.
            TestAssert.True(CastAt(match, 0, 2, victim), "Omen of Famine is accepted");
            for (var team = 0; team < 3; team++)
                TestAssert.Near(0.0, yorick.AttackRevealRemaining[team], 0, $"{victimKind}: team {team} sees nothing");
            foreach (var capture in match.Captures)
                TestAssert.Equal(0, capture.Packets.Count(packet => IsAttackBubble(packet, yorick.NetId ^ 0x2000_0000u)),
                    $"{victimKind}: no reveal bubble is sent");
        }
    }

    [Check("spells-live-summoner-cast-reveals-caster-to-target-team")]
    public static void TargetedSummonersRevealTheCasterToTheirTargetsTeam()
    {
        // SummonerDot has an explicit 1; SummonerSmite and SummonerExhaust have no key and take the default.
        foreach (var (spell, victimKind) in new[]
                     { ("SummonerDot", "enemy hero"), ("SummonerExhaust", "enemy hero"), ("SummonerSmite", "neutral monster") })
        {
            using var match = new Match("Annie", Team.Order, enemyChampion: "Yorick");
            var annie = match.Hero(0);
            annie.SummonerSpells[1] = spell;
            var victim = victimKind == "enemy hero" ? match.Hero(1) : AddMonster(match, annie.Position + new Position2(250, 0));
            TestAssert.True(CastAt(match, 0, 1, victim, summoner: true), $"{spell} is accepted");
            TestAssert.Near(RevealSeconds, annie.AttackRevealRemaining[(int)victim.Team], 1e-9,
                $"{spell} on a {victimKind} shows the caster to that team");
            TestAssert.Equal(annie.Position, annie.AttackRevealPosition[(int)victim.Team], $"{spell}: at the cast point");
        }
    }

    // The typed cast boundary shares the wire request's validation and dispatch; the wire path would
    // also refuse a camp monster the observers were never told about.
    private static bool CastAt(Match match, int seat, int slot, Entity target, bool summoner = false)
    {
        var point = new Position3(target.Position.X, 0.0, target.Position.Z);
        var accepted = match.Runtime.Spells.HandleCastRequest(match.Hero(seat),
            new SpellCastRequest(slot, summoner, point, point, target.NetId), match.Runtime.World.Clock.Now());
        match.Runtime.Delivery.Flush();
        return accepted;
    }

    private static Entity AddMonster(Match match, Position2 position)
    {
        var monster = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, position)
        {
            Name = "Wolf", Alive = true, Created = true, Visible = true,
            Health = 5000, MaxHealth = 5000, Radius = 50, Speed = 300,
        };
        match.Runtime.World.Register(monster);
        return monster;
    }

    // AddPerceptionBubble (opcode 0x7B) carries the bubble id at offset 33.
    private static bool IsAttackBubble(PlaintextPacket packet, uint id) =>
        packet.Plaintext.Length >= 37 && packet.Plaintext.Span[0] == 0x7B &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]) == id;
}
