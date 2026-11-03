using System.Reflection;
using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// Batch 445 runtime probe. The shipped capture script (<c>DATA/Spells/OdinCaptureChannel.luaobj</c>) declares a
/// <c>BuffOnPreDamage</c> hook and no spell-execute event, so it runs as a buff. The probe adds it to a champion
/// with a map-owned capture point (no champion owner, neutral team) as the caster, through the production BuildingBlocks
/// buff path, and fails on any script failure the router records or if the buff is not on the hero afterwards.
/// Map 1's world stands in for map 8 (the Map8 loader is batch 443's work).
/// </summary>
public static class BbOdinCaptureChecks
{
    [Check("scripting.bb-odin-capture-channel-runs-on-a-map-owned-caster")]
    public static void CaptureChannelRunsOnAMapOwnedCaster()
    {
        using var match = new BbM3.Match("Ryze");
        var dispatcher = (ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher;
        var module = new BbChampionModule("Ryze");
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(dispatcher, ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).Append(module).ToArray());
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        var failures = new List<BbScriptFailure>();
        ((BbHookFanout)hooks.ScriptHooks!).Add(module.Bind(match.Runtime.Spells, failures.Add));

        var hero = match.Hero;
        // Map-owned capture point: no champion name owner, no owner, the neutral side.
        var point = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Monster, UnitType.Monster, Team.Neutral,
            new Position2(7150, 7000))
        {
            Name = "OdinCenterRelic",
            ChampionName = "OdinCenterRelic",
            Health = 1000,
            MaxHealth = 1000,
        };
        match.Runtime.World.Register(point);

        // The capture buff of the shipped script, caster = the map-owned point, target = the champion.
        var state = module.Buffs!.Add(new BbBuffRequest(hero, point, "OdinCaptureChannel", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 5.0, 0.0, false, new BbTable(), 1));
        match.Advance(1.0);
        TestAssert.True(failures.Count == 0, "the capture script raised no failure: " + string.Join("; ", failures));
        TestAssert.True(state is not null, "the capture buff was added to the champion by a map-owned caster");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "OdinCaptureChannel"), "the capture buff is on the champion after the hooks ran");
    }
}
