using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Batch 136 (19 October 2026): the <c>isHidden</c> byte of the 0xBF buff add. The stock client's HUD buff bar draws an
/// instance only when its type is not <c>BUFF_Internal</c>, its hidden byte is clear and it is active, and draws the script's
/// <c>BuffTextureName</c> as its icon (a script with none gets the client's blank default). Retail's server sent the add's
/// <c>IsHiddenOnClient</c> as that byte; this server sent 0 for every buff, so Brand's <c>BrandPassiveSound</c> (an empty script, added with
/// <c>IsHiddenOnClient = true</c>) was drawn as a blank square beside his Blaze icon. Written 19 October 2026 for the batch; the area checks run.
/// </summary>
public static class BuffHiddenOnClientChecks
{
    private sealed class Capture : IReplicationTransport
    {
        public readonly List<PlaintextPacket> Packets = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private static IEnumerable<BuffAdd> Adds(IEnumerable<PlaintextPacket> packets, uint holder)
    {
        foreach (var packet in packets)
            if (SpellPackets.TryReadBuffAdd(packet.Plaintext.Span, out var add) && add.TargetNetId == holder) yield return add;
    }

    private static BuffAdd Only(IEnumerable<BuffAdd> adds, string name, string where)
    {
        var hash = SpellPackets.BuffNameHash(name);
        var found = adds.Where(add => add.BuffNameHash == hash).ToList();
        TestAssert.True(found.Count >= 1, $"{where}: the client was sent {name}");
        return found[^1];
    }

    /// <summary>
    /// Script dodge sources compose as a product: IncFlatDodgeMod stores (1 - delta) x the current factor,
    /// so two buffs of 20% and 30% dodge 44%, where a sum gives 50%.
    /// </summary>
    [Check("buffs.dodge-script-sources-compose-as-a-product")]
    public static void TwoScriptDodgeBuffsComposeAsAProduct()
    {
        using var runtime = Production("Annie", out var annie, out _, out _);
        var baseline = runtime.HeroStats.Calculate(annie).Dodge;
        runtime.Buffs.AddBuff(annie, "DodgeOne", BuffType.Internal, 30.0, annie.NetId, new BuffPayload { Dodge = 0.2 });
        TestAssert.Near(0.2, runtime.HeroStats.Calculate(annie).Dodge - baseline, 1e-9, "one script source is its own delta");
        runtime.Buffs.AddBuff(annie, "DodgeTwo", BuffType.Internal, 30.0, annie.NetId, new BuffPayload { Dodge = 0.3 });
        TestAssert.Near(0.44, runtime.HeroStats.Calculate(annie).Dodge - baseline, 1e-9, "two sources: 1 - 0.8 x 0.7, not 0.5");
    }

    private static MatchRuntime Production(string champion, out Entity hero, out Entity other, out Capture live)
    {
        var roster = new List<SessionParticipantDefinition>
        {
            new(1, 0, "Holder", champion, Team.Order), new(2, 1, "Other", "Annie", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0, roster: roster);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        live = new Capture();
        for (var index = 0; index < roster.Count; index++)
        {
            var id = index + 1;
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        hero = runtime.Session.Participants[0].Hero!;
        other = runtime.Session.Participants[1].Hero!;
        // The holder's own client, attached before the match starts so its stream holds the live adds of the character script's activation.
        runtime.Delivery.AddObserver(new ReplicationObserver(101, Team.Order, hero.NetId, live, ready: true, view: runtime.Session.Participants[0].View));
        TestAssert.True(runtime.Session.Start(), "start");
        return runtime;
    }

    /// <summary>
    /// Brand's character script adds <c>BrandPassive</c> (the HUD carrier, <c>BuffTextureName</c> BrandBlaze.dds) and <c>BrandPassiveSound</c> (an empty
    /// script, <c>IsHiddenOnClient = true</c>). The client is told the first as visible and the second as hidden, in the live add and in the replay an
    /// observer gets on late sight or a reconnect.
    /// </summary>
    [Check("replication.buff-add-carries-the-scripts-is-hidden-on-client-live-and-in-the-replay")]
    public static void BuffAddCarriesTheScriptsIsHiddenOnClient()
    {
        using var runtime = Production("Brand", out var brand, out var other, out var live);
        runtime.Tick(1.0);
        runtime.Delivery.Flush();
        TestAssert.True(brand.Buffs.Any(buff => buff.Name == "BrandPassiveSound"), "the character script added the sound aura");
        TestAssert.True(brand.Buffs.Single(buff => buff.Name == "BrandPassiveSound").HiddenOnClientByte == 1, "its state says hidden");
        TestAssert.True(brand.Buffs.Single(buff => buff.Name == "BrandPassive").HiddenOnClientByte == 0, "the passive carrier is not hidden");

        var liveAdds = Adds(live.Packets, brand.NetId).ToList();
        TestAssert.Equal((byte)0, Only(liveAdds, "BrandPassive", "live add").IsHidden, "BrandPassive is sent visible (it has the Blaze icon)");
        TestAssert.Equal((byte)1, Only(liveAdds, "BrandPassiveSound", "live add").IsHidden, "BrandPassiveSound is sent hidden (it has no icon)");
        TestAssert.Equal((byte)BuffType.Aura, Only(liveAdds, "BrandPassiveSound", "live add").BuffType, "its type is untouched");

        // A client that sees Brand for the first time (an empty ledger) is replayed his buffs with the same bytes.
        brand.Position = new Position2(7000, 7000);
        other.Position = new Position2(7300, 7000);
        var late = new Capture();
        var observer = new ReplicationObserver(102, Team.Chaos, other.NetId, late, view: new ReplicationView());
        observer.View.Known.Add(brand.NetId);
        observer.View.Known.Add(other.NetId);
        runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();
        var replay = Adds(late.Packets, brand.NetId).ToList();
        TestAssert.Equal((byte)0, Only(replay, "BrandPassive", "replay").IsHidden, "the replay sends BrandPassive visible");
        TestAssert.Equal((byte)1, Only(replay, "BrandPassiveSound", "replay").IsHidden, "the replay sends BrandPassiveSound hidden");
    }

    /// <summary>
    /// A buff the lifecycle swaps the stat payload of (a script buff whose stats hook changed) keeps the add's flag, so the replay of a later sight still
    /// hides it; a plain add leaves the byte clear.
    /// </summary>
    [Check("buffs.hidden-on-client-survives-a-stat-payload-swap")]
    public static void HiddenOnClientSurvivesAStatPayloadSwap()
    {
        using var runtime = Production("Annie", out var annie, out _, out _);
        var plain = runtime.Buffs.AddBuff(annie, "Haste", BuffType.Haste, 5.0, annie.NetId, new BuffPayload());
        var hidden = runtime.Buffs.AddBuff(annie, "Slow", BuffType.Slow, 5.0, annie.NetId, new BuffPayload { HiddenOnClient = true });
        TestAssert.True(plain is not null && hidden is not null, "both buffs were added");
        TestAssert.Equal((byte)0, plain!.HiddenOnClientByte, "an add that does not say IsHiddenOnClient is not hidden");
        TestAssert.Equal((byte)1, hidden!.HiddenOnClientByte, "an add that says it is hidden");
        TestAssert.True(runtime.Buffs.ReplaceScriptPayload(annie, hidden, new BuffPayload { AttackSpeedMod = 0.1 }), "the payload was swapped");
        TestAssert.Equal((byte)1, hidden.HiddenOnClientByte, "the swap keeps the flag");
        TestAssert.True(runtime.Buffs.ReplaceScriptPayload(annie, plain, new BuffPayload { AttackSpeedMod = 0.1 }), "the other payload was swapped");
        TestAssert.Equal((byte)0, plain.HiddenOnClientByte, "and the swap does not invent one");
    }
}
