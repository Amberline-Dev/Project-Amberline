using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Batch 157 part 2 (22 October 2026 playtest): "Placing a ward gives it Morgana Q VFX. Applies to all wards."
/// Morgana's Q is <c>DarkBinding</c> (slot 0 is its missile, <c>DarkBindingMissile</c>) and the snare it lands shows
/// <c>DarkBinding_tar.troy</c> because that missile's own add has no <c>BuffName</c> key and is named after the script
/// that makes it, whose buff template carries that troy. The ward path repeats the template by accident:
/// <c>Spells/SightWard.luaobj</c> (VisionWard's, and <c>WriggleLantern.luaobj</c>'s placement) adds the placement
/// marker <c>ItemPlacementMissile</c> to the ward it spawns, and that missile script is one of the twelve Riot
/// copy-pasted Morgana's Q buff template into (<c>BuffName='Dark Binding'</c>,
/// <c>BuffTextureName='FallenAngel_DarkBinding.dds'</c>, <c>AutoBuffActivateEffect='DarkBinding_tar.troy'</c>).
/// The packet field that carries Morgana's effect is the 0xBF's name hash: the stock client resolves it to
/// <c>Spells/&lt;name&gt;.luaobj</c> and plays that script's <c>AutoBuffActivateEffect</c> for the life of the add
/// (the client's torch restart), so the ward wore DarkBinding_tar.troy — and Morgana's icon and
/// "Dark Binding" tooltip — for the marker's two seconds. No particle packet is involved at all.
///
/// These checks count the buff add/renew/remove (0xBF/0x32/0x7F) and particle create/destroy (0x8C/0x3D) packets per
/// unit over time and compare them with what the shipped scripts ask for: the ward script's own adds
/// (<c>SharedWardBuff</c> and the ward's own <c>SightWard</c>/<c>VisionWard</c>/<c>WriggleLanternWard</c>, whose
/// template is their own ICU.troy) reach the ward, the marker does not (NOT RETAIL: it stays server-side, see
/// <c>BuffPayload.ServerOnlyMarker</c>), and no buff packet may name a script that carries the copied template. The
/// item-active check runs the same root over the other actives that cast a spell script, and the last check keeps
/// Morgana's own snare on the wire so the exemption that protects it is pinned too.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const string DarkBindingBuffName = "Dark Binding";
    private const string DarkBindingTexture = "FallenAngel_DarkBinding.dds";
    private const string DarkBindingEffect = "DarkBinding_tar.troy";

    private static readonly uint[] DarkBindingHashes =
    {
        SpellPackets.BuffNameHash("DarkBinding"), SpellPackets.BuffNameHash("DarkBindingMissile"),
        SpellPackets.BuffNameHash("DarkBinding_tar"), SpellPackets.BuffNameHash("DarkBinding_buf"),
        // Batch 357: "Dark Binding" (the script's BuffName) has no script, so the wire carries DarkBindingMissile, listed above.
    };

    /// <summary>
    /// The names this file has loaded scripts for, hash to name, for the diagnostic log and the failure messages.
    /// Checks run in parallel, so both caches are concurrent.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, string> WireNames = new();
    private static readonly ConcurrentDictionary<string, BbScript?> Scripts = new(StringComparer.OrdinalIgnoreCase);

    private static string WireName(uint hash) =>
        WireNames.TryGetValue(hash, out var name) ? name : $"#{Hex(hash)}";

    /// <summary>
    /// Batch 357: the name the packets carry for a script's buff is the binding stem (<c>GameData.ClientBuffName</c>). Learned for the wire log.
    /// </summary>
    private static string ClientName(string bindingName)
    {
        var name = GameData.Default.ClientBuffName(bindingName);
        Learn(name);
        return name;
    }

    private static void Learn(params string[] names)
    {
        foreach (var name in names) WireNames.TryAdd(SpellPackets.BuffNameHash(name), name);
    }

    private static BbScript? ShippedScript(string name)
    {
        Learn(name);
        return Scripts.GetOrAdd(name, key =>
        {
            var path = $"Spells/{key}.luaobj";
            return File.Exists(GameDataPaths.Default.Data(path))
                ? BbScriptLoader.Load(GameDataPaths.Default, path).Script
                : null;
        });
    }

    /// <summary>
    /// Whether the named buff would make the stock client draw another spell's visual: its shipped script carries the
    /// copied Morgana's Q template (the same triple <c>BbBuffRuntime.CarriesForeignBuffTemplate</c> suppresses, stated
    /// here as an independent expectation). <c>DarkBinding</c> and its family are exempt: that template is their own
    /// visual, and only their own.
    /// </summary>
    private static bool CarriesDarkBindingTemplate(string name) => ShippedScript(name) is { } script &&
        !script.Name.StartsWith("DarkBinding", StringComparison.OrdinalIgnoreCase) &&
        script.Global("BuffName") is BbValue.Text { Value: var buffName } && buffName == DarkBindingBuffName &&
        script.Global("BuffTextureName") is BbValue.Text { Value: var texture } && texture == DarkBindingTexture &&
        script.Global("AutoBuffActivateEffect") is BbValue.Text { Value: var effect } && effect == DarkBindingEffect;

    /// <summary>
    /// What one shipped script asks the wire for: the buffs its <c>BBSpellBuffAdd</c> blocks name (an add with no
    /// <c>BuffName</c> is named after the script that runs it), the particles its <c>BBSpellEffectCreate</c> blocks
    /// draw, and — through the child spells its <c>BBSpellCast</c>/<c>BBSetSpell</c> blocks name — the same of those.
    /// The placement adds are the ones of the top script's <c>SelfExecute</c>, i.e. what the ward or the item active
    /// puts on its target the moment it runs.
    /// </summary>
    private sealed record ScriptAsks(List<string> PlacementAdds, List<string> Adds, List<string> Particles);

    private static ScriptAsks AsksOf(string name, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placement = new List<string>();
        var adds = new List<string>();
        var particles = new List<string>();
        if (ShippedScript(name) is not { } script || !seen.Add(script.Name)) return new ScriptAsks(placement, adds, particles);
        foreach (var (hook, block) in script.Walk())
        {
            if (string.Equals(block.Function, "BBSpellBuffAdd", StringComparison.Ordinal))
            {
                var added = block.Text("BuffName") ?? script.Name;
                adds.Add(added);
                if (hook == "SelfExecute") placement.Add(added);
            }
            else if (string.Equals(block.Function, "BBSpellEffectCreate", StringComparison.Ordinal) &&
                     block.Text("EffectName") is { } effect)
                particles.Add(effect.EndsWith(".troy", StringComparison.OrdinalIgnoreCase) ? effect[..^5] : effect);
            else if (block.Text("SpellName") is { Length: > 0 } child)
            {
                var nested = AsksOf(child, seen);
                adds.AddRange(nested.Adds);
                placement.AddRange(nested.PlacementAdds);
                particles.AddRange(nested.Particles);
            }
        }
        return new ScriptAsks(placement, adds, particles);
    }

    /// <summary>
    /// The buff and particle traffic of one run on one wire, counted per unit over time. Every buff name is resolved
    /// to the shipped script its hash names; a buff packet that names a script carrying the copied Morgana template is
    /// recorded rather than tolerated (its client side is a buff add, and that is what plays the template), and so is
    /// any DarkBinding name or hash in any field of any packet.
    /// </summary>
    private sealed class WireLog
    {
        public List<string> Lines { get; } = [];
        public List<(double At, uint Unit, string Name, byte Slot)> Adds { get; } = [];
        public List<(double At, uint Unit, string Name, byte Slot)> Removes { get; } = [];
        public List<(double At, string Name)> Creates { get; } = [];
        public List<string> Foreign { get; } = [];
        public List<string> DarkBindingFields { get; } = [];
        public int Renews { get; private set; }
        public int Kills { get; private set; }

        public IEnumerable<string> AddNamesOn(uint unit) =>
            Adds.Where(row => row.Unit == unit).Select(row => row.Name);

        public void Absorb(PlaintextPacket packet, double at)
        {
            var payload = packet.Plaintext.Span;
            for (var offset = 1; offset + 4 <= payload.Length; offset++)
            {
                var field = U32(packet, offset);
                if (DarkBindingHashes.Contains(field))
                    DarkBindingFields.Add($"{at:0.0} op={payload[0]:X2} @{offset}={WireName(field)}");
            }
            switch (payload[0])
            {
                case 0xBF:
                {
                    var unit = U32(packet, 1);
                    var name = WireName(U32(packet, 9));
                    var slot = payload[5];
                    Adds.Add((at, unit, name, slot));
                    Lines.Add($"{at:0.0} ADD    {name} unit={unit} slot={slot} duration={F32(packet, 17):0.00}");
                    if (CarriesDarkBindingTemplate(name))
                        Foreign.Add($"{at:0.0} ADD {name} unit={unit} slot={slot}");
                    break;
                }
                case 0x32:
                    Renews++;
                    Lines.Add($"{at:0.0} RENEW  unit={U32(packet, 1)} slot={payload[5]} duration={F32(packet, 10):0.00}");
                    break;
                case 0x7F:
                {
                    var unit = U32(packet, 1);
                    var name = WireName(U32(packet, 6));
                    Removes.Add((at, unit, name, payload[5]));
                    Lines.Add($"{at:0.0} REMOVE {name} unit={unit} slot={payload[5]}");
                    // A remove names the buff the same way an add does: the marker's own removal must stay off the
                    // wire with its add, or the client resolves the name and draws the template again.
                    if (CarriesDarkBindingTemplate(name))
                        Foreign.Add($"{at:0.0} REMOVE {name} unit={unit} slot={payload[5]}");
                    break;
                }
                case 0x8C:
                {
                    var name = WireName(U32(packet, 6));
                    Creates.Add((at, name));
                    Lines.Add($"{at:0.0} CREATE {name} id={Hex(U32(packet, 25))} bind={U32(packet, 29)}");
                    break;
                }
                case 0x3D:
                    Kills++;
                    Lines.Add($"{at:0.0} KILL   id={Hex(U32(packet, 5))}");
                    break;
            }
        }

        public void Summarise()
        {
            Lines.Add($"COUNT  adds={Adds.Count} renews={Renews} removes={Removes.Count} " +
                      $"creates={Creates.Count} kills={Kills}");
        }
    }

    /// <summary>One run's worth of packets on seat 0's wire, at the simulation's own 0.1 s step.</summary>
    private static WireLog Record(Match match, double seconds)
    {
        const double step = 0.1;
        var log = new WireLog();
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += step)
        {
            match.Runtime.Tick(step);
            match.Runtime.Delivery.Flush();
            foreach (var packet in match.Captures[0].Packets) log.Absorb(packet, elapsed);
            match.Clear();
        }
        log.Summarise();
        return log;
    }

    /// <summary>
    /// The root this batch is about: no buff packet may name a template-carrying script, and no packet at all may
    /// carry a DarkBinding name or hash. (Morgana's own cast and snare name that family legitimately, so her check
    /// does not use this.)
    /// </summary>
    private static void AssertNoDarkBinding(WireLog log, string what)
    {
        TestAssert.True(log.Foreign.Count == 0,
            $"{what}: no buff packet names a script that carries the copied Dark Binding template " +
            $"({string.Join(" | ", log.Foreign)})");
        TestAssert.True(log.DarkBindingFields.Count == 0,
            $"{what}: no packet field carries a DarkBinding name or hash " +
            $"({string.Join(" | ", log.DarkBindingFields)})");
    }

    /// <summary>
    /// Batch 303: a ward use reaches the ward spell's own <c>CastRange</c> (600 for SightWard, VisionWard and
    /// WriggleLantern) rather than a hand-copied 600, and spends a charge as the item inibin's <c>Consumed</c> says.
    /// </summary>
    [Check("items-live-ward-reach-and-charge-come-from-the-data")]
    public static void WardReachAndChargeComeFromTheData()
    {
        foreach (var (item, consumed) in new[] { (2044, true), (2043, true), (3154, false) })
        {
            using var match = new Match("Morgana", Team.Order);
            var hero = match.Hero(0);
            hero.Position = new Position2(6500, 7000);
            hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
            TestAssert.False(match.TryCastGround(0, 4, new Position2(7101, 7000)), $"item {item}: 601 is out of reach");
            TestAssert.True(match.TryCastGround(0, 4, new Position2(7099, 7000)), $"item {item}: 599 places the ward");
            TestAssert.Equal(consumed, hero.Inventory[0] is null, $"item {item}: the use spends its charge as Consumed says");
        }
    }

}
