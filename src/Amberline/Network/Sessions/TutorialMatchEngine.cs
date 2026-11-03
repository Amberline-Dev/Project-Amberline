using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;

namespace Amberline.Network.Sessions;

/// <summary>
/// Batch 563: the engine half of the Basic Tutorial's natives (<see cref="LevelScriptHost.TutorialEngine"/>). A native whose client packet
/// is proven from the 126 packet switch goes out as that packet (<see cref="TutorialPackets"/>, and the camera, fog, input-lock and ping
/// packets the server already sends); every other one is logged as <c>tutorial.native</c> with what it needs, so the script runs on.
/// </summary>
internal sealed class TutorialMatchEngine(ReplicationDelivery delivery, Func<double> now, ISessionLogSink? log, Func<Entity, uint>? clientOf = null)
{
    /// <summary>
    /// Reconstruction: PlayVOAudioEvent's <c>folderName</c>. The script names only the event (<c>PlayTutorialAudioEvent('Tutorial_Welcome',
    /// ...)</c>); the shipped bank files them under the event group <c>Tutorial</c> of <c>vo/tutorial</c>
    /// (DATA/Sounds/FMOD/LoL_Audio_en_US.fev), so the group is the folder.
    /// </summary>
    public const string AudioFolder = "Tutorial";

    public void Handle(TutorialCall call)
    {
        switch (call.Native)
        {
            case "ShowObjectiveText": Send(TutorialPackets.Text(TutorialPackets.ShowObjectiveTextOpcode, call.Text ?? "")); break;
            case "RefreshObjectiveText": Send(TutorialPackets.Text(TutorialPackets.RefreshObjectiveTextOpcode, call.Text ?? "")); break;
            case "HideObjectiveText": Send(TutorialPackets.Header(TutorialPackets.HideObjectiveTextOpcode)); break;
            case "ShowAuxiliaryText": Send(TutorialPackets.Text(TutorialPackets.ShowAuxiliaryTextOpcode, call.Text ?? "")); break;
            case "RefreshAuxiliaryText": Send(TutorialPackets.Text(TutorialPackets.RefreshAuxiliaryTextOpcode, call.Text ?? "")); break;
            case "HideAuxiliaryText": Send(TutorialPackets.Header(TutorialPackets.HideAuxiliaryTextOpcode)); break;
            case "CloseShop": Send(TutorialPackets.Header(TutorialPackets.CloseShopOpcode)); break;
            case "OpenTutorialPopup": Send(TutorialPackets.Text(TutorialPackets.OpenTutorialPopupOpcode, call.Text ?? "")); break;
            case "PlayTutorialAudioEvent": Send(TutorialPackets.PlayVoAudioEvent(AudioFolder, call.Text ?? "", call.Flag, call.Id)); break;
            // Case 0x82 of the 126 packet handler flips the client's fog switch (the fog flag toggles between 1 and 0).
            case "ToggleFogOfWar": Send(SpawnPackets.ToggleFow()); break;
            case "ToggleInputLockingFlag": Send(TutorialPackets.ToggleInputLockingFlag(AiGlobals.InputLockMask(call.Number))); break;
            case "MuteVolumeCategory": Send(TutorialPackets.MuteVolumeCategory((byte)call.Number, call.Flag)); break;
            case "MoveCameraToPoint" when call.Unit is { } hero && call.To is { } to:
                Send(CombatPackets.MoveCameraToPoint(hero.NetId, to, ProtocolEncoding.Float32(Math.Max(0.0, call.Number), nameof(call.Number)), call.From));
                break;
            // Reconstruction: a script ping is the hero's own ping, the attack category, of the point (PingMinimapPosition) or of the unit.
            case "PingMinimapPosition" or "PingMinimapUnit" when call.Unit is { } pinger && call.To is { } at:
                Send(PingPackets.MapPingNotification(pinger.NetId, at, call.Other?.NetId ?? 0, MapPingCategory.Offense));
                break;
            // Batch 580: the persistent switch of the hero's idle acquisition (Entity.CanAutoAcquireTargets, HeroLuaAiEngine.Acquire).
            case "SetCanAutoAcquireTargets" when call.Unit is { } unit:
                unit.CanAutoAcquireTargets = call.Flag;
                break;
            // Batch 580: the 126 handlers of these packets are cited at their opcodes (TutorialPackets, VisionPackets.LockCamera).
            case "ToggleUIHighlight" when call.Values is [var element, var type, var number, var subCategory]:
                Send(TutorialPackets.ToggleUiHighlight(Byte(element), Byte(type), Byte(number), Byte(subCategory), call.Flag));
                break;
            case "HighlightHUDElement" when call.Values is [var type, var number] && type is >= 0 and < 9:
                Send(TutorialPackets.HighlightHudElement(Byte(type), Byte(number)));
                break;
            case "CreateUnitHighlight" or "RemoveUnitHighlight" when call.Unit is { } highlighted:
                Send(TutorialPackets.UnitHighlight(highlighted.NetId, call.Native == "CreateUnitHighlight"));
                break;
            case "LockCamera" when call.Unit is { } hero:
                Send(VisionPackets.LockCamera(hero.NetId, call.Flag));
                break;
            // Reconstruction: ResumeGame is ResumePacket (0x0A, case 10 of the 126 switch: the client id at +5, not delayed), sent for
            // the tutorial player. The script calls it only in EventEndGame, which no shipped code calls, and nothing pauses the tutorial.
            case "ResumeGame" when call.Unit is { } player:
                Send(PausePackets.Resume(clientOf?.Invoke(player) ?? 0, delayed: false));
                break;
            default:
                Log(call, "no engine answer");
                break;
        }
    }

    private void Send(PlaintextPacket packet) => delivery.Enqueue(packet, global: true);

    private static byte Byte(double value) => (byte)Math.Clamp(value, 0.0, 255.0);

    private void Log(TutorialCall call, string why) =>
        log?.Write("GAMEPLAY", "INFO", "tutorial.native", new Dictionary<string, object?>
        {
            ["native"] = call.Native, ["text"] = call.Text, ["number"] = call.Number, ["flag"] = call.Flag,
            ["unit"] = call.Unit?.NetId, ["why"] = why, ["at"] = now(),
        });
}
