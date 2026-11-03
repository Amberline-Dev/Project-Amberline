using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Batch 157 part 3 (22 October 2026 playtest): "Mana Manipulator VFX does not loop properly. Stark's Fervor loops
/// properly while Mana Manipulator just disappears, appears, disappears, appears."
///
/// The eight aura items of the batch-145 group (3001, 3037, 3050, 3097, 3099, 3105, 3110, 3152) each run one
/// <c>BBExecutePeriodically</c> block that re-adds their aura buff every period with <c>BUFF_RENEW_EXISTING</c>
/// (3110's own <c>FrozenHeart</c> row is <c>BUFF_REPLACE_EXISTING</c>, Duration 10). The periods are 1.0 s, or 0.9 s for
/// 3001 and 3110; the durations are 1.0 (3152's WillOfTheAncients, 3105's AegisoftheLegionAuraFriend and 3001), 1.1
/// (Mana Manipulator) and 1.2 (3050, 3099, 3105's self arm) — the script's own slack over its period is 0.0 to 0.2 s.
///
/// What the client does with that is in its buff code, and it is what makes the two items differ:
/// the buff manager's packet handlers (three of them) share a timer restart, and only NpcBuffReplace
/// (0x32) ends in the client's torch restart: that walks the instance's four particle slots,
/// deactivates every one and re-creates them. A buff whose shipped script carries an <c>AutoBuffActivateEffect</c>
/// paints that particle from the buff instance, so a renewal sent as 0x32 kills it and lights it again — once a second
/// for these items. <c>Spells/ManaManipulatorAuraSelf.luaobj</c> carries
/// <c>AutoBuffActivateEffect='ZettasManaManipulator_itm.troy'</c>; Stark's Fervor's
/// <c>RallyingBannerAuraSelf</c>/<c>AuraFriend</c> carry <c>''</c> and draw their troy from their own
/// <c>OnBuffActivate</c> with <c>BBSpellEffectCreate</c>, which no renewal touches. Exactly three of the family hand
/// the client a torch (3037's ManaManipulatorAuraSelf, 3105's AegisoftheLegionAuraSelf, 3001's AbyssalScepterAuraSelf)
/// and those are the items that blink.
///
/// The other half is the copy. Both silent renewal handlers return at once when the slot's stack list is empty, so a
/// client copy that runs out is never coming back: a re-add that reaches it after that is a no-op. The scripts leave no
/// room at all on four rows, and the emulator's <c>UpdateSelfBuffStats</c> poll swallowed the wire renewal outright
/// (3001 and 3110 are re-added from that hook), so those copies expired at the script's duration and stayed gone. A
/// replacing add of a buff the unit already carries had the same shape on the wire: <c>BuffLifecycle.AddBuff</c> removed
/// the live instance and added its replacement, which is a RemoveBuff packet and a fresh BuffAdd packet every period,
/// and the client's copy of 3110's FrozenHeart blinked for as long as the item was held.
///
/// These checks count the buff add/renew/remove (0xBF/0x32/0x1F/0x7F) and particle create/destroy (0x8C/0x3D) packets
/// per unit over time and compare them with what the shipped scripts ask for: one <c>BUFF_RENEW_EXISTING</c> re-add per
/// period is one continuous life of one instance (one add, renewals after it, never a remove and a second add), the
/// renewal must not re-present the client's own activation particle, and the client's copy must still be live — one
/// client frame on — when the next re-add reaches it.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static readonly int[] AuraItems = [3001, 3037, 3050, 3097, 3099, 3105, 3110, 3152];

    /// <summary>How long each item is held for in these checks: seven periods and a half of the aura's life.</summary>
    private const double RunSeconds = 8.0;

    /// <summary>What one shipped aura item script asks the wire for, one row per aura buff of its periodic block.</summary>
    private sealed record AuraAsk(
        string Name, double Duration, BbBuffAddType AddType, bool Torch, List<string> Particles);

    /// <summary>
    /// The <c>BBSpellBuffAdd</c> rows of the item's two periodic self-buff hooks — the aura family's shared root —
    /// with the buff's own template and the particles the scripts draw. The torch is the four
    /// <c>AutoBuffActivateEffect</c> slots the client's torch restart walks; a script that draws its particle
    /// itself (a <c>BBSpellEffectCreate</c> of its own <c>OnBuffActivate</c>) is never on that list.
    /// </summary>
    private static List<AuraAsk> AuraAsks(int item, List<string> log)
    {
        var path = $"Items/{item}.luaobj";
        var script = BbScriptLoader.Load(GameDataPaths.Default, path).Script
            ?? throw new InvalidOperationException($"{path} did not load");
        var asks = new List<AuraAsk>();
        foreach (var (hook, block) in script.Walk())
        {
            if (hook is not ("UpdateSelfBuffStats" or "UpdateSelfBuffActions")) continue;
            if (!string.Equals(block.Function, "BBSpellBuffAdd", StringComparison.Ordinal)) continue;
            var name = block.Text("BuffName") ?? script.Name;
            var addType = AddTypeOf(block);
            var duration = block.Number("Duration") ?? 0.0;
            var buff = ShippedScript(name);
            var torch = ClientTorch(buff);
            var particles = ParticlesOf(buff);
            Learn(name);
            Learn(ClientName(name));
            Learn(particles.ToArray());
            asks.Add(new AuraAsk(name, duration, addType, torch, particles));
            log.Add($"  asks {name} duration={duration:0.00} add={addType} torch={(torch ? "yes" : "no")} " +
                    $"particles=[{string.Join(", ", particles)}]");
        }
        foreach (var effect in ParticlesOf(script))
        {
            Learn(effect);
            log.Add($"  asks particle {effect} (its own activation)");
        }
        return asks;
    }

    /// <summary>
    /// The add's <c>BuffAddType</c>: a named constant of the shipped script's preload table
    /// (<c>BbValue.Reference</c>), which is what the whole corpus uses, with a plain number as the fallback.
    /// </summary>
    private static BbBuffAddType AddTypeOf(BbBlock block) => block.Param("BuffAddType") switch
    {
        BbValue.Number { Value: var raw } => (BbBuffAddType)(int)raw,
        BbValue.Reference { Name: var name } when BbConstants.TryGetValue(name, out var value) => (BbBuffAddType)(int)value,
        _ => BbBuffAddType.BUFF_REPLACE_EXISTING,
    };

    /// <summary>The four <c>AutoBuffActivateEffect</c> slots of a buff's shipped template, any one of which is a client torch.</summary>
    private static bool ClientTorch(BbScript? script) => script is not null &&
        Enumerable.Range(1, 4).Any(slot => script.Global(slot == 1 ? "AutoBuffActivateEffect" : $"AutoBuffActivateEffect{slot}")
            is BbValue.Text { Value.Length: > 0 });

    /// <summary>The <c>BBSpellEffectCreate</c> names of one script, without the .troy suffix the wire hash drops.</summary>
    private static List<string> ParticlesOf(BbScript? script)
    {
        var names = new List<string>();
        if (script is null) return names;
        foreach (var (_, block) in script.Walk())
            if (string.Equals(block.Function, "BBSpellEffectCreate", StringComparison.Ordinal) &&
                block.Text("EffectName") is { } effect)
                names.Add(effect.EndsWith(".troy", StringComparison.OrdinalIgnoreCase) ? effect[..^5] : effect);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One aura buff's traffic on one unit: the add and every renewal of that slot, and the particles of the run.</summary>
    private sealed class AuraWire
    {
        public List<string> Lines { get; } = [];
        public Dictionary<(uint Unit, string Name), List<(double At, string Kind, float Duration)>> Refreshes { get; } = new();
        public List<(double At, string Name)> Creates { get; } = [];
        public List<(double At, uint Id)> Kills { get; } = [];

        /// <summary>The buff name a slot was added under, so the silent renewals (which carry no name) can be filed.</summary>
        private readonly Dictionary<(uint Unit, byte Slot), string> _names = new();

        public void Absorb(PlaintextPacket packet, double at)
        {
            var payload = packet.Plaintext.Span;
            switch (payload[0])
            {
                case 0xBF:
                {
                    var unit = U32(packet, 1);
                    var name = WireName(U32(packet, 9));
                    _names[(unit, payload[5])] = name;
                    if (!Refreshes.TryGetValue((unit, name), out var rows)) Refreshes[(unit, name)] = rows = [];
                    rows.Add((at, "ADD", F32(packet, 17)));
                    Lines.Add($"{at:0.0} ADD    {name} unit={Hex(unit)} slot={payload[5]} duration={F32(packet, 17):0.00}");
                    break;
                }
                case 0x32:
                    Renew(packet, at, "REPLACE", F32(packet, 10));
                    break;
                case 0x1F:
                    Renew(packet, at, "COUNT", F32(packet, 7));
                    break;
                case 0x7F:
                {
                    var unit = U32(packet, 1);
                    var name = WireName(U32(packet, 6));
                    _names.Remove((unit, payload[5]));
                    if (Refreshes.TryGetValue((unit, name), out var rows)) rows.Add((at, "REMOVE", 0f));
                    Lines.Add($"{at:0.0} REMOVE {name} unit={Hex(unit)} slot={payload[5]}");
                    break;
                }
                case 0x8C:
                {
                    var name = WireName(U32(packet, 6));
                    Creates.Add((at, name));
                    Lines.Add($"{at:0.0} CREATE {name} id={Hex(U32(packet, 25))} bind={Hex(U32(packet, 29))}");
                    break;
                }
                case 0x3D:
                {
                    var id = U32(packet, 5);
                    Kills.Add((at, id));
                    Lines.Add($"{at:0.0} KILL   id={Hex(id)}");
                    break;
                }
            }
        }

        private void Renew(PlaintextPacket packet, double at, string kind, float duration)
        {
            var unit = U32(packet, 1);
            var slot = packet.Plaintext.Span[5];
            if (!_names.TryGetValue((unit, slot), out var name))
            {
                Lines.Add($"{at:0.0} {kind}   (unfiled) unit={Hex(unit)} slot={slot}");
                return;
            }
            Refreshes[(unit, name)].Add((at, kind, duration));
            Lines.Add($"{at:0.0} {kind}   {name} unit={Hex(unit)} slot={slot} duration={duration:0.00}");
        }
    }

    /// <summary>The wire of one aura item on seat 0's own view, at the simulation's own 0.1 s step.</summary>
    private static AuraWire RecordAura(Match match, double seconds)
    {
        const double step = 0.1;
        var wire = new AuraWire();
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += step)
        {
            match.Runtime.Tick(step);
            match.Runtime.Delivery.Flush();
            foreach (var packet in match.Captures[0].Packets) wire.Absorb(packet, elapsed);
            match.Clear();
        }
        return wire;
    }

    private static (List<AuraAsk> Asks, AuraWire Wire, List<string> Log) RunAuraItem(int item, double seconds = RunSeconds)
    {
        var log = new List<string> { $"======== item {item} ========" };
        var asks = AuraAsks(item, log);
        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Orianna");
        match.Hero(0).Inventory = [new InventorySlot(item), null, null, null, null, null];
        match.Clear();
        var wire = RecordAura(match, seconds);
        log.AddRange(wire.Lines);
        return (asks, wire, log);
    }


    private static string Timeline(IEnumerable<(double At, string Kind, float Duration)> rows) =>
        string.Join(" ", rows.Select(row => $"{row.At:0.0}:{row.Kind}"));
}
