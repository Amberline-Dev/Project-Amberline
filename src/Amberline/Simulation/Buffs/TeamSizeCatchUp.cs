using Amberline.Core;

namespace Amberline.Simulation.Buffs;

/// <summary>
/// Patch-126 team-size catch-up (batch 159 part 3). Both halves are shipped scripts now; nothing here ticks them.
/// <para>
/// <c>Spells/ChampionChampionDelta.luaobj</c> is the controller: every champion character script adds it permanently in
/// <c>CharOnActivate</c> (<c>BUFF_REPLACE_EXISTING</c>, 25000 s, <c>NonDispellable</c>). Its
/// <c>BuffOnUpdateStats</c> runs a <c>BBExecutePeriodically</c> of 10 s (<c>ExecuteImmediately = false</c>), counts both
/// teams with <c>BBGetNumberOfHeroesOnTeam</c> (<c>ConnectedFromStart = false</c>, <c>IncludeBots = true</c>), and adds
/// <c>PositiveChampionDelta</c> as a 21 s <c>BUFF_RENEW_EXISTING</c> (<c>MaxStack 1</c>) when its owner's team is smaller,
/// else clears it.
/// </para>
/// <para>
/// <c>Spells/PositiveChampionDelta.luaobj</c> is the ramp: its <c>OnBuffActivate</c> records the game time and its
/// <c>BuffOnUpdateStats</c> writes <c>0.1 x T</c> as <c>IncPercentRespawnTimeMod</c> and <c>0.05 x T</c> as
/// <c>IncPercentEXPBonus</c>, with <c>T = min(now - start, 90) / 90</c>. Both rows reach the hero through the buff payload
/// the stat calculator sums (<see cref="BuffPayload.RespawnTimePercent"/> and <see cref="BuffPayload.ExperiencePercent"/>).
/// </para>
/// <para>
/// A hero whose champion has a <c>BbChampionModule</c> gets the controller from its own character script. A hero whose
/// champion has none (the ten hand-written ones) has no character-script host, so
/// <c>ProductionMatchComposition.HeroCreated</c> adds the same controller through the match's unit-character module, whose
/// buff runtime loads the shipped <c>Spells/ChampionChampionDelta.luaobj</c> by name: the instance, its periodic, its team
/// comparison and the ramp are all the script's, on one path for both kinds of hero. The old hand-written controller row,
/// its tick and its two ramp readers are gone; what is left here is the naming and the arithmetic the checks pin.
/// </para>
/// <para>
/// The one rule neither script states is the training dummy: <c>BBGetNumberOfHeroesOnTeam</c>'s <c>IncludeBots</c> counts
/// every hero of the team, and the /dummy champions are a debug addition that must not change a real team's size
/// (DIAGNOSTICS).
/// </para>
/// </summary>
public static class TeamSizeCatchUp
{
    public const string ControllerBuff = "ChampionChampionDelta";
    public const string DeltaBuff = "PositiveChampionDelta";
    /// <summary>CharScript BBSpellBuffAdd Duration: permanent for any match length.</summary>
    public const double ControllerSeconds = 25000.0;
    public const double CheckSeconds = 10.0;
    public const double DeltaSeconds = 21.0;
    public const double RampSeconds = 90.0;
    public const double RespawnPerRamp = 0.1;
    public const double ExperiencePerRamp = 0.05;
}