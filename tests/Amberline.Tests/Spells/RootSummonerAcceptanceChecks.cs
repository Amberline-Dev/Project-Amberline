using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

public static class RootSummonerAcceptanceChecks
{
    [Check("runtime-p1-assist-item-growth")]
    public static void AssistItemGrowthThroughCombat()
    {
        // Batch 149 step 1: the assist growth is the shipped scripts' own now. Spells/MejaisCheck.luaobj,
        // LeviathanCheck.luaobj and MuramasaCheck.luaobj's BuffOnAssist add one *Stats stack to whoever assists, and
        // Items/3083.luaobj's ItemOnAssist grows the copy's InstanceVars (ExtraHP / ExtraRegen) for a hero victim.
        // The hand-written Snowball3041/3141/3138 counters and the WarmogHealth/WarmogRegen fields are gone.
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var supporter = Hero(runtime, Team.Order, "SummonerHeal");
        var killer = Hero(runtime, Team.Order, "SummonerDot");
        var victim = Hero(runtime, Team.Chaos, "SummonerDot");
        supporter.Inventory = [new(3041, 1, 0), new(3141, 1, 0), new(3138, 1, 0),
            new(3083, 1, 0), null, null];
        // The check buffs (MejaisCheck and its two siblings) are what the ItemOnKill/Assist stacks land on, and
        // Items/3041.luaobj's UpdateSelfBuffActions only re-adds them on its own one-second BBExecutePeriodically
        // (ExecuteImmediately false): the first spell update binds the copy and seeds that clock, a later one a
        // second on fires it. Advance is one spell update, so the clock gets its two polls here the way a match's
        // item-buff cadence does.
        Advance(runtime, 1.0);
        Advance(runtime, 1.0);
        runtime.HeroStats.Refresh(supporter);
        runtime.Combat.RecordSupportAssist(supporter, killer);
        runtime.Combat.ApplyHit(supporter.NetId, victim.NetId, 1, DamageType.True);
        TestAssert.True(runtime.Combat.Kill(victim, killer.NetId));
        TestAssert.False(runtime.Combat.Kill(victim, killer.NetId), "dead victim cannot pay twice");
        TestAssert.Equal(1, supporter.ChampionAssists, "damage and support credit deduplicate");
        TestAssert.Equal(1, supporter.Buffs.Single(b => b.Name == "MejaisStats").Stacks, "Mejai assist stack");
        TestAssert.Equal(1, supporter.Buffs.Single(b => b.Name == "MuramasaStats").Stacks, "Occult assist stack");
        TestAssert.Equal(1, supporter.Buffs.Single(b => b.Name == "LeviathanStats").Stacks, "Leviathan assist stack");
        Advance(runtime, 0.01);
        runtime.HeroStats.Refresh(supporter);
        var warmog = runtime.Spells.ItemScripts!.LoadoutOf(supporter)!.Item(3)!;
        TestAssert.Near(45, (double)warmog.InstanceVars["ExtraHP"]!, 1e-9, "Warmog assist health growth");
        TestAssert.Near(.30, (double)warmog.InstanceVars["ExtraRegen"]!, 1e-9, "Warmog assist regeneration growth");
        // Script wins (batch 149 step 1): the same assist also lands one LeviathanStats stack and that buff's own
        // BuffOnUpdateStats gives the owner max health (IncFlatHPPoolMod 32), so the hero's total max-health delta
        // is not the Warmog half alone. The Warmog half is read back on its own by taking the copy's counter away
        // and re-sampling, the way the Warmog regeneration check does.
        // Batch 252 (restated from 0.01 s): batch 246 put the item's bare UpdateSelfBuffStats on the holder's 0.25 s poll line (BbSelfUpdate),
        // so each counter change is re-sampled a quarter second on.
        warmog.InstanceVars["ExtraHP"] = 0.0;
        Advance(runtime, 0.25);
        runtime.HeroStats.Refresh(supporter);
        var withoutWarmog = supporter.MaxHealth;
        warmog.InstanceVars["ExtraHP"] = 45.0;
        Advance(runtime, 0.25);
        runtime.HeroStats.Refresh(supporter);
        TestAssert.Near(45, supporter.MaxHealth - withoutWarmog, 1e-9, "Warmog assist health reaches the hero");
    }

    [Check("spells-p1-teleport-shared-target-and-turret")]
    public static void SharedTeleportTarget()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var first = Hero(runtime, Team.Order, "SummonerTeleport");
        var second = Hero(runtime, Team.Order, "SummonerTeleport");
        var enemy = Hero(runtime, Team.Chaos, "SummonerDot");
        var target = Unit(runtime, UnitType.Minion, EntityKind.Minion);
        target.StealthHidden = true;
        TestAssert.True(Cast(runtime, first, target), "first channel starts");
        Advance(runtime, 2.0);
        TestAssert.True(Cast(runtime, second, target), "overlapping channel starts");
        Advance(runtime, 2.1);
        TestAssert.Equal("SummonerTeleport", first.SummonerSpells[0], "first channel completes");
        TestAssert.Equal("TeleportCancel", second.SummonerSpells[0], "second channel remains");
        // script = retail: the hand-written cast saved and cleared StealthHidden on every target and restored
        // it when the last channel released the unit. The shipped script never touches a target's own stealth
        // state — its Destealth add is gated on BBIfHasBuff SharedWardBuff, so only ward targets are revealed
        // (that reveal and its Duration 1.0 + BuffDuration tail are pinned by spells-live-teleport-ward-*).
        // A non-ward target carries the Teleport_Target lock alone.
        TestAssert.True(target.StealthHidden, "the script reveals ward targets only");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Destealth"),
            "no Destealth row on a non-ward shared target");
        // Teleport_Target is added BUFF_RENEW_EXISTING, so the second cast's own 0.1 + BuffDuration carries the
        // protection past the first channel's completion instead of the engine's longest-deadline bookkeeping.
        var health = target.Health;
        runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 100, DamageType.True);
        TestAssert.Equal(health, target.Health, "shared target stays protected after first completion");
        Advance(runtime, 2.0);
        TestAssert.True(target.StealthHidden, "the non-ward target's own stealth state is never changed");
        TestAssert.False(target.Buffs.Any(buff => buff.Payload.Invulnerable),
            "the renewed lock runs to its own 0.1 + BuffDuration past the last channel");

        var turret = Unit(runtime, UnitType.Turret, EntityKind.Turret);
        first.SummonerCooldowns[0] = 0;
        TestAssert.True(Cast(runtime, first, turret), "turret channel starts");
        health = turret.Health;
        runtime.Combat.ApplyHit(enemy.NetId, turret.NetId, 100, DamageType.True);
        TestAssert.Near(health - 100, turret.Health, 1e-9, "Teleport turret visual grants no immunity");
        TestAssert.Near(4.0, turret.Buffs.Single(buff => buff.Name == "Teleport_Turret").ExpiresAt, 1e-9,
            "the turret row carries its plain BuffDuration (no target row's 0.1 tail)");
        runtime.SpellBuffs.Add(first, "IncomingSnare", BuffType.Snare, 1, enemy.NetId);
        Advance(runtime, 0.01);
        TestAssert.Equal("TeleportCancel", first.SummonerSpells[0],
            "snare does not interrupt the shipped Teleport channel");
        TestAssert.True(turret.Buffs.Any(buff => buff.Name == "Teleport_Turret"),
            "snare preserves the channel visual");
        // The interrupt is the shipped channel's own buff type: TargetExecute adds it Stun-typed, so an incoming
        // stun replaces that row the way stuns replace stuns, and its OnBuffDeactivate writes the interrupted
        // cooldown at the replacement itself — exactly 180 here, one step before the Advance below runs it down.
        runtime.SpellBuffs.Add(first, "IncomingStun", BuffType.Stun, 1, enemy.NetId);
        TestAssert.Near(180, first.SummonerCooldowns[0], 1e-9, "interrupted cooldown");
        Advance(runtime, 0.01);
        TestAssert.Equal("SummonerTeleport", first.SummonerSpells[0], "incoming control interrupts channel");
        // script = retail: the interrupt no longer strips the target lock (the engine's ReleaseTeleportTarget is
        // gone with CastTeleport). Teleport_DeathRemoval takes the lock off only on a TeleportCancel cast or the
        // caster's death; an interrupted channel leaves it to its own timer.
        TestAssert.True(turret.Buffs.Any(buff => buff.Name == "Teleport_Turret"),
            "the turret lock outlives the interrupted channel on its own timer");

        Advance(runtime, 1.1);
        first.SummonerCooldowns[0] = 0;
        first.SummonerSpells[1] = "SummonerBoost";
        TestAssert.True(Cast(runtime, first, target), "channel before Cleanse");
        // Cleanse dispels the same Stun-typed channel row, and its OnBuffDeactivate writes the interrupted
        // cooldown the same way at the dispel itself.
        TestAssert.True(Cast(runtime, first, first, 1), "Cleanse removes own channel stun");
        TestAssert.Near(180, first.SummonerCooldowns[0], 1e-9, "removed channel uses interrupted cooldown");
        Advance(runtime, 0.01);
        TestAssert.Equal("SummonerTeleport", first.SummonerSpells[0], "removed channel restores slot");
        TestAssert.True(target.Buffs.Any(buff => buff.Payload.Invulnerable),
            "the lock outlives the dispelled channel end on its own timer");

        // Both locks retire on their own deadlines, never at a channel end: Teleport_Turret's plain
        // BuffDuration (4.0 from the turret cast) is due first, while the channel's Teleport_Target still
        // holds its 0.1 + BuffDuration from the dispelled cast.
        Advance(runtime, 3.0);
        TestAssert.False(turret.Buffs.Any(buff => buff.Name == "Teleport_Turret"),
            "the turret lock retires at its own BuffDuration deadline");
        TestAssert.True(target.Buffs.Any(buff => buff.Payload.Invulnerable),
            "the later lock keeps its own later deadline");
    }

    [Check("spells-p1-fortify-rally-damage-lifecycle")]
    public static void DefensiveAndBeaconLifecycle()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default);
        var caster = Hero(runtime, Team.Order, "SummonerFortify");
        var enemy = Hero(runtime, Team.Chaos, "SummonerDot");
        var turret = Unit(runtime, UnitType.Turret, EntityKind.Turret);
        turret.AttackSpeedMod = 1;
        turret.AttackPeriod = 1;
        turret.AttackRange = 700;
        turret.AttackDamage = 100;
        TestAssert.True(Cast(runtime, caster, caster), "Fortify starts");
        TestAssert.True(runtime.Combat.QueueBasicAttack(turret, enemy), "Fortify turret can still attack");
        TestAssert.Near(.5, turret.AttackCooldown, 1e-9, "Fortify accelerates server attack cadence");
        // SummonerFortify.luaobj adds BUFF_Invulnerability (with SetPhysicalImmune/SetMagicImmune), so the immunity is
        // total: true damage is blocked too. The hand-written cast's CombatEnhancer plus the two statuses let true damage
        // through, which was the deviation.
        runtime.Combat.ApplyHit(enemy.NetId, turret.NetId, 100, DamageType.Physical);
        runtime.Combat.ApplyHit(enemy.NetId, turret.NetId, 100, DamageType.Magic);
        runtime.Combat.ApplyHit(enemy.NetId, turret.NetId, 100, DamageType.True);
        TestAssert.Equal(1000.0, turret.Health, "Fortify blocks physical, magic and true damage");
        Advance(runtime, 6.01);
        TestAssert.Near(1, turret.AttackSpeedMod, 1e-9, "Fortify attack speed expires");
        TestAssert.Near(1, turret.AttackPeriod, 1e-9, "Fortify server attack period restores");
        runtime.Combat.ApplyHit(enemy.NetId, turret.NetId, 100, DamageType.Physical);
        TestAssert.Equal(900.0, turret.Health, "Fortify immunity expires");

        caster.SummonerSpells[0] = "SummonerRally";
        caster.SummonerCooldowns[0] = 0;
        TestAssert.True(Cast(runtime, caster, caster), "Rally starts");
        var beacon = runtime.World.Entities.Values.Single(unit => unit.IsSummonerBeacon);
        runtime.Combat.Kill(caster, enemy.NetId);
        TestAssert.True(beacon.Alive, "Rally survives caster death");
        runtime.Combat.ApplyHit(enemy.NetId, beacon.NetId, 100, DamageType.Magic);
        TestAssert.Equal(beacon.MaxHealth, beacon.Health, "Rally magic immunity");
        runtime.Combat.ApplyHit(enemy.NetId, beacon.NetId, 100, DamageType.Physical,
            sourceKind: DamageSourceKind.Basic);
        TestAssert.Near(beacon.MaxHealth - 100 / 1.15, beacon.Health, 1e-8,
            "Rally takes armor-mitigated health damage instead of ward hit points");
        Advance(runtime, 14.99);
        TestAssert.True(beacon.Alive, "Rally remains through original lifetime");
        Advance(runtime, 0.02);
        TestAssert.False(beacon.Alive, "Rally ends at original lifetime after caster death");
    }

    private static Entity Hero(MatchRuntime runtime, Team team, string summoner)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, new Position2(5000, 5000))
        {
            ChampionName = "Annie", Name = "AcceptanceHero", Alive = true, Created = true,
            Level = 1, SummonerSpells = [summoner, "SummonerFlash"],
        };
        runtime.World.Register(hero);
        runtime.HeroStats.Refresh(hero);
        hero.Health = hero.MaxHealth;
        hero.Mana = hero.MaxMana;
        return hero;
    }

    private static Entity Unit(MatchRuntime runtime, UnitType type, EntityKind kind)
    {
        var unit = new Entity(runtime.World.Allocate(type), kind, type, Team.Order,
            new Position2(5100, 5000))
        {
            Name = type == UnitType.Turret ? "Turret_T1_C1" : "Minion_Basic",
            Alive = true, Created = true, Health = 1000, MaxHealth = 1000,
        };
        runtime.World.Register(unit);
        return unit;
    }

    private static bool Cast(MatchRuntime runtime, Entity hero, Entity target, int slot = 0)
    {
        var packet = new PacketBuffer(34);
        packet.WriteBytes(new byte[5]);
        packet.WriteByte(checked((byte)((slot << 1) | 1)));
        for (var i = 0; i < 2; i++)
        {
            packet.WriteSingle(target.Position.X);
            packet.WriteSingle(0);
            packet.WriteSingle(target.Position.Z);
        }
        packet.WriteUInt32(target.NetId);
        return runtime.Spells.HandleCastRequest(hero, packet.ToArray(), runtime.World.Clock.Now());
    }

    private static void Advance(MatchRuntime runtime, double seconds)
    {
        // Preserve the production spell-before-buff phase without unrelated
        // lane, income or fountain simulation affecting these assertions.
        runtime.Spells.Tick(seconds);
        foreach (var entity in runtime.World.Entities.Values.ToArray())
            runtime.Buffs.TickBuffs(entity, seconds);
    }
}
