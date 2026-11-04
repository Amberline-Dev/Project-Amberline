namespace Amberline.Tests;

/// <summary>Named unions of existing selectors; execution and isolation stay in the runner.</summary>
internal static class CheckScopes
{
    public static IReadOnlyDictionary<string, string[]> All { get; } = Create();

    private static Dictionary<string, string[]> Create()
    {
        var scopes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["core"] = ["core-*", "clock"],
            ["data"] = ["data.*", "skins.*", "map4.*", "map3.*", "map1.*"],
            ["scripting"] = ["scripting.*", "data.inibin*", "data.luaobj*", "tools.audit.*"],
            ["combat"] = ["combat*", "death-recap.*", "stats*", "buffs.*", "runtime.progression-*", "runtime-p1-*", "progression.*"],
            ["buffs"] = ["buffs.*", "stats*", "runtime.gameplay-hooks"],
            ["items"] = ["items*", "buffs.*", "stats*", "runtime-p1-*"],
            ["movement"] = ["movement*", "navigation*", "minions*", "spells-live-dash-orders-and-fog-cleanup", "spells-live-forced-move-fog-reentry-dash", "spells-live-fear-fog-reentry-walk", "spells-live-pounce-midcast-keeps-dash", "spells-live-janna-r-turret-body-clearance"],
            ["minions"] = ["minions*", "neutral.*", "pets*", "movement*", "navigation*"],
            ["network"] = ["network.*", "death-recap.*", "protocol.*", "packet-*", "replication*", "session*", "transport.*", "vision-*"],
            ["server"] = ["server.*", "session*", "network.*"],
            ["spells"] = ["spells-*", "spells.*", "runtime*", "buffs.*", "pets*"],
            ["summoners"] = ["spells-summoners-*", "spells-p1-*", "spells-live-revive-*"],
            ["tools"] = ["tools.*"],
            ["runner"] = ["runner.*"],
            ["polish"] = ["polish.*"],
            // DIAGNOSTICS (owner, 25 October 2026): test and development aids, such as the Dominion relic-bubble gate check (batch 476).
            ["diagnostics"] = ["diagnostics.*"],
            // Dominion (map 8, batches 442-457): capture, score, waves, relics, announcer and Garrison, and the map 8 headless matches.
            ["dominion"] = ["dominion.*", "scripting.behaviour-tree-*", "scripting.odin-*", "server.dominion-*", "tools.match.dominion-*"],
        };
        // Some champion regressions are named after an ability or pet instead
        // of the champion. Keep those explicit so a focused run does not miss them.
        var champions = new Dictionary<string, string[]>
        {
            ["annie"] = ["*annie*", "*tibbers*"],
            ["orianna"] = ["*orianna*"],
            ["leesin"] = ["*leesin*", "spells-live-q-*", "spells-live-point-blank-q-*", "spells-live-safeguard-*", "spells-live-kick-*", "spells-live-dash-orders-and-fog-cleanup", "spells-launched-control-*"],
            ["nidalee"] = ["*nidalee*", "spells-live-pounce-*", "spells-live-dash-orders-and-fog-cleanup"],
            ["yorick"] = ["*yorick*", "*revenant*", "pets-live-ghoul-*", "spells-live-clone-single-native-creation"],
            ["shaco"] = ["*shaco*", "spells-live-clone-single-native-creation"],
            ["ezreal"] = ["*ezreal*"],
            ["vayne"] = ["*vayne*", "spells-live-dash-orders-and-fog-cleanup"],
            ["janna"] = ["*janna*", "spells-launched-control-*"],
            ["blitzcrank"] = ["*blitz*", "spells-live-power-fist-*", "spells-launched-control-*"],
        };
        foreach (var champion in champions)
            scopes.Add(champion.Key, [.. champion.Value, "spells-champion-*", "spells-live-instant-cast-timings", "runtime.gameplay-hooks"]);
        return scopes;
    }
}
