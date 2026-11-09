using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The client-only natives of the wave 2 engine bundle (W27b, 19 October 2026; the new-champion wave plan, items E05 to E24): the camera position,
/// the animation pause, the colour fades, the voice override and the resource-bar colour override. Each one sends a presentation packet and keeps
/// only the state that packet replays to a late observer. Each one is its own section below, with the client parse it was bound from.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    /// <summary>
    /// <c>BBSetCameraPosition</c>: <c>Owner</c> (a hero; the script passes the key as "Owner", not
    /// "OwnerVar") and <c>Position</c> (<c>PositionVar</c>, a point); the body sends <c>SetCameraPosition</c> (<c>0x76</c>, the point's three floats) to
    /// that hero's own client. Pantheon's Grand Skyfall moves the camera to the landing point and Urgot's swap to where he stands after it; Recall's
    /// success stop does the same to the fountain, which <c>SummonersRecall</c> already sends through the same sink call.
    /// </summary>
    private void SetCameraPosition(BbCall call)
    {
        if (Unit(call, "Owner") is not { UnitType: UnitType.Hero } owner) return;
        Spells.Effects.SendCamera(owner, SpawnPosition(call, "PositionVar"));
    }

    /// <summary>
    /// <c>BBPauseAnimation</c> (the client registers a bare return, so the parameters are the two shipped call sites'):
    /// <c>Unit</c> and <c>Pause</c>; the client has <c>PauseAnimation</c> (<c>0x74</c>, a unit and a boolean) and the sink already sends it for a
    /// stasis. Cassiopeia's Petrifying Gaze freezes its victim's clip while it lasts and Skarner's Impale freezes the suppressed unit's.
    /// Reconstruction: sent when the script asks, not replayed to an observer who sees the unit later.
    /// </summary>
    private void PauseAnimation(BbCall call)
    {
        if (Unit(call, "UnitVar") is not { } unit || !Spells.Effects.IsReferenceReady(unit)) return;
        Spells.Effects.PauseAnimation(unit, Flag(call, "Pause", true));
    }

    /// <summary>
    /// <c>BBFadeInColorFadeEffect</c> (<c>ColorRed</c>, <c>ColorGreen</c>, <c>ColorBlue</c>, <c>FadeTime</c>, <c>MaxWeight</c>, <c>SpecificToTeam</c>) and
    /// <c>BBFadeOutColorFadeEffect</c> (<c>FadeTime</c>, <c>SpecificToTeam</c>): the client registers both as bare returns,
    /// so the parameters are the shipped call sites' (Nocturne's Paranoia tints the screen blue for its own team and red for the
    /// other). They send <c>ColorRemapFX</c> (<c>0xE4</c>), which the stock client's switch filters by the team field: 0 reaches every
    /// client, a team only that team's. <c>SpecificToTeam</c> is the <c>TEAM_*</c> constant, which is the native team id (100, 200). A client that
    /// is not in the match when the tint is sent misses it (Reconstruction: nothing in the client keeps the tint for a late joiner, and the
    /// shipped fades last four seconds).
    /// </summary>
    private void ColorFade(BbCall call, bool fadeIn)
    {
        var team = (int)Sum(call, "SpecificToTeam");
        var fadeTime = (float)Sum(call, "FadeTime");
        var color = fadeIn
            ? ParColorOverride.Pack(Channel(call, "ColorRed"), Channel(call, "ColorGreen"), Channel(call, "ColorBlue"), 0)
            : 0u;
        Spells.Effects.SendColorRemap(fadeIn, fadeTime, team, color, fadeIn ? (float)Sum(call, "MaxWeight") : 0f);
    }

    /// <summary>A colour channel: the shared number getter, cut to a byte the way the packet body stores it.</summary>
    private static byte Channel(BbCall call, string key) => unchecked((byte)(int)Sum(call, key));

    /// <summary>
    /// <c>BBSetVoiceOverride</c> (<c>TargetVar</c> and the string <c>OverrideSuffix</c>) and <c>BBResetVoiceOverride</c>
    /// (<c>TargetVar</c>): the body stores the suffix on the unit (or clears it) and sends <c>ChangeCharacterVoice</c>
    /// (<c>0x9E</c>: a reset byte, then the suffix as a 64-byte string), whose receiver sets or clears the unit's voice string the same way. Riven's
    /// Blade of the Exile sets "Ult" when it activates and resets it when it ends. The server keeps the suffix in <see cref="Entity.VoiceOverride"/>
    /// so that a client that learns the unit later is told too. The packet goes out on every call, as the retail body sends it, even when the unit
    /// already has the suffix (batch 221). Reconstruction: what the retail server does for a unit that comes into view later is not in the client.
    /// </summary>
    private void SetVoiceOverride(BbCall call, bool reset)
    {
        if (Unit(call, "TargetVar") is not { } unit) return;
        var suffix = reset ? null : call.Value("OverrideSuffix") as string;
        if (!reset && string.IsNullOrEmpty(suffix)) return;
        unit.VoiceOverride = suffix;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendVoiceOverride(unit, suffix);
    }

    /// <summary>
    /// <c>BBSetPARColorOverride</c> (<c>SpellSlotOwnerVar</c> and the eight channels <c>ColorR/G/B/A</c> and <c>FadeR/G/B/A</c>, each
    /// the shared number getter cut to a byte) and <c>BBClearPARColorOverride</c> (<c>SpellSlotOwnerVar</c>): the body sends
    /// <c>ChangePARColorOverride</c> (<c>0x99</c>) with the owner's id, the enabling byte and the two colours (bar, then fade; blue, green, red,
    /// alpha), and the unit's receiver stores both colours on its resource bar or drops them. Renekton's Ruthless Predator calls them from
    /// <c>BuffOnUpdateStats</c> (red at fifty fury, and in the other branch a set that its clear undoes at once), Rumble's Flamespitter and
    /// Tryndamere's character script from an event. The retail body sends one packet per call, so Renekton's hook sends two packets on every stats
    /// poll while the condition holds (a set and its clear). A call from a stats hook is recorded and applied, call by call, when the sample is
    /// (<see cref="BbStatCollector.SetParColor"/>; a sample that is not applied sends nothing), and any other call applies at once; each sends its
    /// packet, a repeat of the colours the unit already has included (batch 241). Reconstruction: the late-observer replay of the state.
    /// </summary>
    private void SetParColorOverride(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is not { } owner) return;
        ChangeParColor(owner, new ParColorOverride(
            ParColorOverride.Pack(Channel(call, "ColorR"), Channel(call, "ColorG"), Channel(call, "ColorB"), Channel(call, "ColorA")),
            ParColorOverride.Pack(Channel(call, "FadeR"), Channel(call, "FadeG"), Channel(call, "FadeB"), Channel(call, "FadeA"))));
    }

    private void ClearParColorOverride(BbCall call)
    {
        if (Unit(call, "SpellSlotOwnerVar") is { } owner) ChangeParColor(owner, null);
    }

    private void ChangeParColor(Entity owner, ParColorOverride? value)
    {
        if (_host.HasFrame && _host.Frame.Stats is { } collector && ReferenceEquals(owner, _host.Frame.Owner)) collector.SetParColor(value);
        else ApplyParColorOverride(owner, value);
    }

    /// <summary>Makes the colours the unit's own state and tells the clients, one packet per call.</summary>
    internal void ApplyParColorOverride(Entity owner, ParColorOverride? value)
    {
        owner.ParColorOverride = value;
        if (Spells.Effects.IsReferenceReady(owner)) Spells.Effects.SendParColorOverride(owner, value);
    }
}
