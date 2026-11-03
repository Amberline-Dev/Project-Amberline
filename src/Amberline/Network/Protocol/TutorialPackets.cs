using Amberline.Core;

namespace Amberline.Network.Protocol;

/// <summary>
/// Batch 563: the Basic Tutorial's client packets (LEVELS/Map3 TutorialScript). Every opcode is a case of the 126 packet switch
/// whose handler shows what it is; the layouts are the client's packet structs (<c>ShowObjectiveText</c> and its siblings).
/// Each starts with the opcode and the 4-byte net id (<c>field0..4</c>); the tutorial's packets name no unit there, so it is 0.
/// </summary>
public static class TutorialPackets
{
    /// <summary>Case 0x3b: copies 0x80 bytes from +5 and calls the tooltip's <c>getObjectiveSize</c> show path.</summary>
    public const byte ShowObjectiveTextOpcode = 0x3B;
    /// <summary>Case 0x0e: copies 0x80 bytes from +5 into <c>refreshObjective</c>.</summary>
    public const byte RefreshObjectiveTextOpcode = 0x0E;
    /// <summary>Case 0xaa: header only, the tooltip's <c>closeObjective</c>.</summary>
    public const byte HideObjectiveTextOpcode = 0xAA;
    /// <summary>Case 0xa8: the tooltip's <c>getAuxiliarySize</c> show path.</summary>
    public const byte ShowAuxiliaryTextOpcode = 0xA8;
    /// <summary>Case 0xb9: <c>refreshAuxiliary</c>.</summary>
    public const byte RefreshAuxiliaryTextOpcode = 0xB9;
    /// <summary>Case 0xaf: header only, <c>closeAuxiliary</c>.</summary>
    public const byte HideAuxiliaryTextOpcode = 0xAF;
    /// <summary>
    /// Case 0xbb: the string at +5 opens the HUD popup with the caption <c>game_messagebox_caption_tutorial</c> and an OK
    /// button; OpenTutorialPopup <c>messageboxTextStringID[128]</c>.
    /// </summary>
    public const byte OpenTutorialPopupOpcode = 0xBB;
    /// <summary>
    /// The popup's OK handler sends 5 bytes, <c>0xd5</c> and a zero net id, on channel 1:
    /// OnTutorialPopupClosed.
    /// </summary>
    public const byte PopupClosedClientOpcode = 0xD5;
    /// <summary>
    /// Case 0x7d: <c>folderName</c> at +5 and <c>eventID</c> at offset 0x45 go to the audio player; a non-zero
    /// <c>callbackType</c> at offset 0x85 makes a finished callback for the audio event.
    /// PlayVOAudioEvent; the event's id follows at offset 0x86.
    /// </summary>
    public const byte PlayVoAudioEventOpcode = 0x7D;
    /// <summary>
    /// The finished callback sends 9 bytes on channel 1: <c>0x05</c>, a zero net id and the event's id
    /// (TutorialAudioEventFinished <c>audioEventNetworkID</c>).
    /// </summary>
    public const byte AudioEventFinishedClientOpcode = 0x05;
    /// <summary>
    /// Case 0xa3: the u32 at +5 is XOR-ed into the tutorial manager's input-locking flags (the <c>mInputLockingFlags</c> field):
    /// ToggleInputLockingFlag.
    /// </summary>
    public const byte ToggleInputLockingFlagOpcode = 0xA3;
    /// <summary>
    /// Case 0x29: the byte at +5 below 7 (<c>NUM_VOLUME_CATEGORIES</c>) goes to the volume category mute setter, which logs "Tried to set mute unsuccessfully for
    /// category: %s" when it fails: MuteVolumeCategory, the mute flag bit 0 of +6.
    /// </summary>
    public const byte MuteVolumeCategoryOpcode = 0x29;
    /// <summary>
    /// Batch 580. Case 0x52 (ToggleUIHighlight: <c>elementID</c> +5, <c>elementType</c> +6, <c>elementNumber</c> +7,
    /// <c>elementSubCategory</c> +8, <c>flag</c> bit 0 of +9). The element picks the HUD component: 1 the HUD (type 4-6 the tactical map,
    /// else the main HUD; on or off), 2 the shop (the component that the close-shop case closes; with type, number, subcategory),
    /// 3 the titan bar (the highlight-titan-bar element's case 0x14; with type).
    /// </summary>
    public const byte ToggleUiHighlightOpcode = 0x52;
    /// <summary>
    /// Case 0x43 (HighlightHUDElement: <c>elementType</c> +5 below 9, <c>elementNumber</c> +6) blinks one HUD element once
    /// (the "BlinkElement" function).
    /// </summary>
    public const byte HighlightHudElementOpcode = 0x43;
    /// <summary>
    /// Case 0x5c (CreateUnitHighlight: <c>unitNetworkID</c> +5): the unit is looked up and added to the client's
    /// highlighted units.
    /// </summary>
    public const byte CreateUnitHighlightOpcode = 0x5C;
    /// <summary>Case 0xbc (RemoveUnitHighlight, the unit at +5): the client removes it from the highlighted units.</summary>
    public const byte RemoveUnitHighlightOpcode = 0xBC;
    /// <summary>
    /// WorldSendCameraServer (0x30, 34 bytes): <c>cameraPos</c> +5 and <c>cameraDir</c> +0x11 (three floats each), the client id
    /// +0x1d, a change counter +0x21. The client's camera sends it when the view moves.
    /// </summary>
    public const byte CameraClientOpcode = 0x30;
    /// <summary>
    /// OnShopOpened (0x60, 5 bytes): the item shop's open path sends it before "reopenItemShop".
    /// </summary>
    public const byte ShopOpenedClientOpcode = 0x60;
    /// <summary>
    /// Case 0x0f (CloseShop, header only): when the item shop is open, its slot 0x24 closes it.
    /// </summary>
    public const byte CloseShopOpcode = 0x0F;

    public const int TextBytes = 128;
    public const int AudioNameBytes = 64;

    public static PlaintextPacket Text(byte opcode, string text)
    {
        if (opcode is not (ShowObjectiveTextOpcode or RefreshObjectiveTextOpcode or ShowAuxiliaryTextOpcode or RefreshAuxiliaryTextOpcode
            or OpenTutorialPopupOpcode))
            throw new ArgumentOutOfRangeException(nameof(opcode));
        ArgumentNullException.ThrowIfNull(text);
        ProtocolEncoding.RequireAsciiLength(text, TextBytes - 1, nameof(text));
        using var writer = ProtocolEncoding.Writer(5 + TextBytes, out var stream);
        writer.Write(opcode);
        writer.Write(0u);
        ProtocolEncoding.WriteFixedAscii(writer, text, TextBytes, TextBytes - 1);
        writer.Flush();
        return Packet(ProtocolEncoding.Finish(stream));
    }

    public static PlaintextPacket Header(byte opcode) =>
        opcode is HideObjectiveTextOpcode or HideAuxiliaryTextOpcode or CloseShopOpcode
            ? Packet([opcode, 0, 0, 0, 0])
            : throw new ArgumentOutOfRangeException(nameof(opcode));

    public static PlaintextPacket PlayVoAudioEvent(string folder, string eventId, bool callback, uint audioEventId)
    {
        ProtocolEncoding.RequireAsciiLength(folder, AudioNameBytes - 1, nameof(folder));
        ProtocolEncoding.RequireAsciiLength(eventId, AudioNameBytes - 1, nameof(eventId));
        using var writer = ProtocolEncoding.Writer(5 + 2 * AudioNameBytes + 1 + 4, out var stream);
        writer.Write(PlayVoAudioEventOpcode);
        writer.Write(0u);
        ProtocolEncoding.WriteFixedAscii(writer, folder, AudioNameBytes, AudioNameBytes - 1);
        ProtocolEncoding.WriteFixedAscii(writer, eventId, AudioNameBytes, AudioNameBytes - 1);
        writer.Write((byte)(callback ? 1 : 0));
        writer.Write(audioEventId);
        writer.Flush();
        return Packet(ProtocolEncoding.Finish(stream));
    }

    public static PlaintextPacket ToggleInputLockingFlag(uint flags)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write(ToggleInputLockingFlagOpcode);
        writer.Write(0u);
        writer.Write(flags);
        writer.Flush();
        return Packet(ProtocolEncoding.Finish(stream));
    }

    public static PlaintextPacket MuteVolumeCategory(byte category, bool mute)
    {
        if (category >= 7) throw new ArgumentOutOfRangeException(nameof(category));
        return Packet([MuteVolumeCategoryOpcode, 0, 0, 0, 0, category, (byte)(mute ? 1 : 0)]);
    }

    public static PlaintextPacket ToggleUiHighlight(byte element, byte type, byte number, byte subCategory, bool on) =>
        Packet([ToggleUiHighlightOpcode, 0, 0, 0, 0, element, type, number, subCategory, (byte)(on ? 1 : 0)]);

    public static PlaintextPacket HighlightHudElement(byte type, byte number) =>
        type < 9 ? Packet([HighlightHudElementOpcode, 0, 0, 0, 0, type, number]) : throw new ArgumentOutOfRangeException(nameof(type));

    public static PlaintextPacket UnitHighlight(uint unitNetId, bool create)
    {
        using var writer = ProtocolEncoding.Writer(9, out var stream);
        writer.Write(create ? CreateUnitHighlightOpcode : RemoveUnitHighlightOpcode);
        writer.Write(0u);
        writer.Write(unitNetId);
        writer.Flush();
        // The client looks the unit up, so the packet goes only where the unit has been created.
        return ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, ProtocolEncoding.Finish(stream), encrypt: true,
            LeaguePacketReliability.Reliable, [new LeaguePacketEntityReference(LeaguePacketEntityRole.Target, unitNetId)]);
    }

    /// <summary>
    /// The ground point a 34-byte camera report looks at: the camera position followed along its direction down to <paramref name="groundY"/>
    /// (the stock camera sends a fixed height and a downward direction). Null for any other payload.
    /// </summary>
    public static Position2? CameraFocus(ReadOnlySpan<byte> payload, Func<Position2, double> groundY)
    {
        if (payload.Length != 34 || payload[0] != CameraClientOpcode) return null;
        Span<float> v = stackalloc float[6];
        for (var index = 0; index < 6; index++)
            v[index] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(5 + 4 * index, 4));
        var (x, y, z, dx, dy, dz) = (v[0], v[1], v[2], v[3], v[4], v[5]);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(dz))
            return null;
        var eye = new Position2(x, z);
        if (dy > -1e-3f) return eye;
        var t = (groundY(eye) - y) / dy;
        return new Position2(x + dx * t, z + dz * t);
    }

    /// <summary>The event id of a 9-byte TutorialAudioEventFinished, or null for any other payload.</summary>
    public static uint? AudioEventFinishedId(ReadOnlySpan<byte> payload) =>
        payload.Length >= 9 && payload[0] == AudioEventFinishedClientOpcode
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(5, 4))
            : null;

    private static PlaintextPacket Packet(byte[] bytes) =>
        ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, bytes, encrypt: true, LeaguePacketReliability.Reliable);
}
