using System;
using Amberline.Core;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Batch 149 step 5: the arm a check used to reach through <c>HeroItemState</c> is the shipped script's own now.
/// Sword of the Divine's and Ionic Spark's fourth hit is counted in the window buff's
/// <c>InstanceVars.AttackCounter</c> (Spells/LightstrikerApplicator.luaobj, Spells/LightningRodApplicator.luaobj,
/// both zeroing it in their own <c>OnBuffActivate</c>), and the window is opened by the copy's
/// <c>UpdateSelfBuffActions</c> on the poll that binds it (Items/3131, Items/3178, <c>ExecuteImmediately</c> true).
/// The hand-written <c>HeroItemState.Item3131Stacks</c> / <c>Item3178Stacks</c> are gone.
/// </summary>
internal static class ItemScriptState
{
    /// <summary>Writes one window buff's own instance variable, the way the script's own hook would.</summary>
    public static void ArmWindowCounter(MatchRuntime runtime, Entity hero, string window, string variable, double value)
    {
        var instance = runtime.Spells.ItemScripts!.Buffs!.Find(hero, window)
            ?? throw new InvalidOperationException($"{hero.Name}: the {window} window is not open");
        instance.InstanceVars[variable] = value;
    }

    /// <summary>Reads one window buff's own instance variable; 0.0 when the window is not open.</summary>
    public static double WindowCounter(MatchRuntime runtime, Entity hero, string window, string variable) =>
        runtime.Spells.ItemScripts!.Buffs!.Find(hero, window)?.InstanceVars[variable] is double value ? value : 0.0;
}
