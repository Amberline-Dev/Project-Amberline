using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;

namespace Amberline.Tests;

/// <summary>
/// Data loaded once per process and shared between matches (and, in a check worker,
/// between checks) must never change. See docs/testing.md, "Shared data".
/// </summary>
public static class SharedDataChecks
{
    // The ten hand-written champions fill the ten seats; Soraka (script-driven, appended later) is not part of this ten-seat world.
    private static readonly string[] Champions = ChampionCatalog.PlayableNames.Take(10).ToArray();

    [Check("data.shared-instances-unchanged-after-match")]
    public static void SharedGridAndGameDataSurviveAProductionMatchUnchanged()
    {
        // An unshared decode of the same file is the reference for the shared arrays.
        var reference = Fingerprint(NavigationGrid.LoadFile(
            MapDefinitions.NavigationGridPath(GameData.Default.Paths), NavigationGridSettings.Default));
        AssertFingerprintsNoticeEdits();
        var first = NavigationGrid.LoadDefault();
        var second = NavigationGrid.LoadDefault();
        TestAssert.False(ReferenceEquals(first, second), "every LoadDefault call returns its own grid instance");
        foreach (var field in ArrayFields())
            TestAssert.True(ReferenceEquals(field.GetValue(first), field.GetValue(second)),
                $"{field.Name} is shared, not copied");
        TestAssert.Equal(reference, Fingerprint(first), "the shared arrays equal an unshared decode");

        // Counters and planner seams belong to the instance, never to the shared base.
        first.FindPathOverride = (from, to) => [from, to];
        _ = first.FindPath(new Position2(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z), new Position2(7000, 7000));
        TestAssert.Equal(1L, first.FindPathCalls);
        TestAssert.Equal(0L, second.FindPathCalls, "a sibling grid does not see the call");
        TestAssert.True(second.FindPathOverride is null && NavigationGrid.LoadDefault().FindPathOverride is null,
            "a sibling or later grid does not inherit the override");
        first.FindPathOverride = null;

        var dataBefore = Fingerprint(GameData.Default);
        using var runtime = StartTenSeatMatch();
        for (var step = 0; step < 440; step++) runtime.Tick(0.25);
        TestAssert.True(runtime.Waves!.WaveIndex >= 1 && runtime.Navigation.FindPathCalls > 0,
            "the match spawned waves and planned routes on the shared terrain");

        TestAssert.Equal(reference, Fingerprint(runtime.Navigation), "the match's own grid");
        TestAssert.Equal(reference, Fingerprint(first), "a grid loaded before the match");
        TestAssert.Equal(reference, Fingerprint(NavigationGrid.LoadDefault()), "a grid loaded after the match");
        TestAssert.Equal(0L, NavigationGrid.LoadDefault().FindPathCalls, "a new grid starts with fresh counters");
        TestAssert.Equal(dataBefore, Fingerprint(GameData.Default), "GameData.Default champion and spell data");
    }

    // The proof is only worth something if the fingerprints see an in-place edit, so edit
    // private copies (never the shared instances) and expect the fingerprints to move.
    private static void AssertFingerprintsNoticeEdits()
    {
        var grid = NavigationGrid.LoadFile(
            MapDefinitions.NavigationGridPath(GameData.Default.Paths), NavigationGridSettings.Default);
        var untouched = Fingerprint(grid);
        foreach (var field in ArrayFields())
        {
            var array = (Array)field.GetValue(grid)!;
            var element = array.GetValue(0)!;
            array.SetValue(element switch
            {
                byte b => (object)(byte)(b ^ 1),
                short s => (object)(short)(s ^ 1),
                int i => (object)(i ^ 1),
                float f => (object)(f + 1.0f),
                _ => (object)((double)element + 1.0),
            }, 0);
            TestAssert.True(untouched != Fingerprint(grid), $"an edit of {field.Name} changes the grid fingerprint");
            array.SetValue(element, 0);
        }
        TestAssert.Equal(untouched, Fingerprint(grid), "the edits were undone");

        var data = new GameData();
        var spell = data.Spell(data.Champion("Annie").SpellNames[0], 5);
        var unchanged = Fingerprint(data);
        var mana = (double[])spell.ManaCosts;
        mana[0] += 1.0;
        TestAssert.True(unchanged != Fingerprint(data), "an edit of a loaded spell array changes the GameData fingerprint");
        mana[0] -= 1.0;
        TestAssert.Equal(unchanged, Fingerprint(data), "the edit was undone");
    }

    private static MatchRuntime StartTenSeatMatch()
    {
        var roster = Champions.Select((champion, index) =>
            new SessionParticipantDefinition((ulong)index + 1, (uint)index, $"Shared {index + 1}", champion,
                index < 5 ? Team.Order : Team.Chaos, IsBot: index != 5)).ToArray();
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        const long connection = 101;
        TestAssert.True(session.Authenticate(connection, session.BuildKeyCheck(roster[5].PlayerId)) is not null);
        TestAssert.True(session.Spawn(connection).Count > 0);
        TestAssert.True(session.MarkReady(connection));
        session.BuildMapReadyPackets(connection);
        TestAssert.True(session.Start(), "the production roster starts");
        return runtime;
    }

    // Every array the grid holds, found by reflection so a future array field is covered too.
    private static FieldInfo[] ArrayFields()
    {
        var fields = typeof(NavigationGrid).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType.IsArray).ToArray();
        // Batch 61 (13ccc412) added the stock client's four region arrays (short[] _regionA/_regionB, float[] _regionFraction/
        // _regionTable): read once from the nav file by ReadClientRegions, shared by CopyClientRegionsFrom, never written after.
        // Batch 613: they live in NavigationGrid.ClientRegions.cs (the movement port), which the public export leaves out.
        var regions = fields.Count(field => field.Name.StartsWith("_region", StringComparison.Ordinal));
        TestAssert.True(fields.Length - regions >= 9 && regions is 0 or 4,
            "the grid keeps its nine cell arrays (and the four client region arrays where the movement port ships)");
        return fields;
    }

    private static string Fingerprint(NavigationGrid grid)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(grid.Metadata.ToString()));
        foreach (var field in ArrayFields().OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            switch (field.GetValue(grid))
            {
                case byte[] bytes: hash.AppendData(bytes); break;
                case short[] shorts: hash.AppendData(MemoryMarshal.AsBytes(shorts.AsSpan())); break;
                case int[] ints: hash.AppendData(MemoryMarshal.AsBytes(ints.AsSpan())); break;
                case float[] floats: hash.AppendData(MemoryMarshal.AsBytes(floats.AsSpan())); break;
                case double[] doubles: hash.AppendData(MemoryMarshal.AsBytes(doubles.AsSpan())); break;
                default: throw new InvalidOperationException($"unexpected grid array {field.Name}");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // A deep dump of what the ten champions and their spells hold, including the arrays
    // behind the read-only views, so an in-place edit of a shared list shows up.
    private static string Fingerprint(GameData data)
    {
        var text = new StringBuilder();
        foreach (var name in Champions)
        {
            var champion = data.Champion(name);
            Dump(champion, text);
            foreach (var spell in champion.SpellNames.Where(spell => spell.Length > 0))
            {
                Dump(data.Spell(spell), text);
                Dump(data.Spell(spell, 5), text);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Dump(object? value, StringBuilder text)
    {
        switch (value)
        {
            case null:
                text.Append("null;");
                break;
            case string or bool or char or Enum or InibinValue or IFormattable:
                text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
                break;
            case InibinData inibin:
                foreach (var key in inibin.Keys.Order())
                    text.Append(key.ToString("x8", CultureInfo.InvariantCulture)).Append('=').Append(inibin[key]).Append(';');
                break;
            case IDictionary dictionary:
                foreach (var key in dictionary.Keys.Cast<object>().OrderBy(key => key.ToString(), StringComparer.Ordinal))
                {
                    text.Append(key).Append(':');
                    Dump(dictionary[key], text);
                }
                break;
            case IEnumerable items:
                text.Append('[');
                foreach (var item in items) Dump(item, text);
                text.Append(']');
                break;
            default:
                foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                             .Where(property => property.GetIndexParameters().Length == 0)
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    text.Append(property.Name).Append('=');
                    Dump(property.GetValue(value), text);
                }
                break;
        }
    }
}
