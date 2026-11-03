using System.Net;
using System.Net.Sockets;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

public static class AirProfileChecks
{
    [Check("server.air-mastery-refund-row-index")]
    public static void TalentRowsUseGroupRelativeIndexesForStockRefundGuard()
    {
        var catalog = new MasteryCatalog(GameData.Default.Paths);
        var groups = ((ArrayCollection)AirDtos.SummonerCatalog()["talentTree"])
            .Cast<AsObject>().ToArray();
        TestAssert.Equal(3, groups.Length);
        foreach (var group in groups)
        {
            var groupId = (int)group["tltGroupId"];
            var rows = ((ArrayCollection)group["talentRows"]).Cast<AsObject>().ToArray();
            var rowIds = catalog.Definitions.Where(definition => definition.GroupId == groupId)
                .Select(definition => definition.Row).Distinct().Order().ToArray();
            TestAssert.Equal(rowIds.Length, rows.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                TestAssert.Equal(rowIds[index], (int)rows[index]["tltRowId"]);
                TestAssert.Equal(index, (int)rows[index]["index"]);
            }

            var ranks = new Dictionary<int, int>();
            var needed = 4;
            var firstRow = catalog.Definitions.Where(definition =>
                definition.GroupId == groupId && definition.Row == rowIds[0]).ToArray();
            foreach (var definition in firstRow)
            {
                var spent = Math.Min(needed, definition.MaxRank);
                if (spent > 0) ranks[definition.Id] = spent;
                needed -= spent;
            }
            TestAssert.Equal(0, needed);
            var upper = catalog.Definitions.First(definition =>
                definition.GroupId == groupId && definition.Row == rowIds[1] &&
                definition.PrerequisiteId is null);
            ranks[upper.Id] = 1;
            _ = Amberline.Core.MasteryPage.Create(catalog, ranks);

            // Stock decreaseTalentRank passes highestRow.index - 1 to an
            // inclusive row-position sum. At 4 lower + 1 upper, that sum
            // must exclude the upper point and block a lower-row refund.
            var earlierPoints = ranks.Where(pair => firstRow.Any(definition => definition.Id == pair.Key))
                .Sum(pair => pair.Value);
            TestAssert.Equal(4, earlierPoints);
            TestAssert.Equal(1, (int)rows[1]["index"]);
            TestAssert.True(earlierPoints <= (int)rows[1]["pointsToActivate"]);
            var lower = firstRow[0].Id;
            var invalidRefund = new Dictionary<int, int>(ranks) { [lower] = ranks[lower] - 1 };
            TestAssert.Throws<ArgumentException>(() =>
                Amberline.Core.MasteryPage.Create(catalog, invalidRefund));
            ranks.Remove(upper.Id);
            ranks[lower]--;
            _ = Amberline.Core.MasteryPage.Create(catalog, ranks);
        }
    }

    [Check("server.air-empty-rune-mastery-backing-isolation")]
    public static void EmptyRunePagesAndMasteriesHaveIndependentFlexBackingArrays()
    {
        var account = new PlatformAccount(1, 1_000_001, "runeowner", "Rune Owner", "", "",
            new MatchPlayer("Rune Owner", "Annie", Amberline.Core.Team.Order));
        var login = AirDtos.LoginData(account);
        var all = (AsObject)login["allSummonerData"];
        var pages = ((ArrayCollection)((AsObject)all["spellBook"])["spellBookPages"])
            .Cast<AsObject>().ToArray();
        TestAssert.Equal(2, pages.Length);
        var collections = pages.Select(page => (ArrayCollection)page["slotEntries"])
            .Append((ArrayCollection)((AsObject)all["summonerTalentsAndPoints"])["summonerAssociatedTalents"])
            .ToArray();
        var backing = collections.Select(collection =>
        {
            TestAssert.Equal(0, collection.Count);
            var output = new BackingArrayCapture();
            collection.WriteExternal(output);
            return output.Backing!;
        }).ToArray();
        TestAssert.False(ReferenceEquals(backing[0], backing[1]));
        TestAssert.False(ReferenceEquals(backing[0], backing[2]));
        TestAssert.False(ReferenceEquals(backing[1], backing[2]));

        var decoded = (AsObject)RoundTrip(login);
        var decodedAll = (AsObject)decoded["allSummonerData"];
        foreach (AsObject page in (ArrayCollection)((AsObject)decodedAll["spellBook"])["spellBookPages"])
            TestAssert.Equal(0, ((ArrayCollection)page["slotEntries"]).Count);
        TestAssert.Equal(0, ((ArrayCollection)((AsObject)decodedAll["summonerTalentsAndPoints"])["summonerAssociatedTalents"]).Count);
    }

    [Check("server.air-mastery-point-budget-and-persistence")]
    public static void MasteryBudgetAllowsLevelPointsAndSavedRanksSurviveReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-mastery-" + Guid.NewGuid().ToString("N"));
        try
        {
            long maxedId;
            int talentId;
            using (var accounts = new PlatformAccounts(root))
            {
                var maxed = accounts.RegisterPublic("mastery_maxed", "testing-password", "Mastery Maxed", maxed: true);
                var fresh = accounts.RegisterPublic("mastery_fresh", "testing-password", "Mastery Fresh", maxed: false);
                maxedId = maxed.Id;
                var available = new MasteryCatalog(GameData.Default.Paths).Definitions
                    .Where(definition => definition.MinLevel <= 1 && definition.PointsToActivate == 0 && definition.PrerequisiteId is null)
                    .Take(2).ToArray();
                talentId = available[0].Id;
                var initial = (AsObject)RoundTrip(AirDtos.Talents(maxed));
                TestAssert.Equal(30d, initial["talentPoints"]);
                TestAssert.Equal(30d, initial["unusedTalentPoints"]);
                TestAssert.Equal(1d, AirDtos.Talents(fresh)["talentPoints"]);
                TestAssert.Throws<ArgumentException>(() => accounts.SaveLoadout(fresh.Id, fresh.Loadout with
                {
                    Masteries = new Dictionary<int, int> { [available[0].Id] = 1, [available[1].Id] = 1 }
                }));

                var sessions = new PlatformSessions(accounts);
                var session = sessions.Login("mastery-rpc", maxed.Username, sessions.IssueTicket(maxed), "1.45.11_10_04_15_28");
                TestAssert.True(sessions.AuthenticateChannel("mastery-rpc", maxed.Username, session.Token));
                var manager = CreateNonLaunchingManager(root);
                try
                {
                    var rpc = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1"));
                    var saved = (AsObject)RoundTrip(rpc.Route("mastery-rpc", "summonerService", "changeTalentRankings",
                        [(double)maxed.SummonerId, new Dictionary<string, object> { [talentId.ToString()] = 1 }])!);
                    TestAssert.Equal(30d, saved["talentPoints"]);
                    TestAssert.Equal(29d, saved["unusedTalentPoints"]);
                    TestAssert.Equal(1, accounts.Get(maxed.Id).Loadout.Masteries![talentId]);
                    var profile = (AsObject)rpc.Route("mastery-rpc", "summonerService", "getAllSummonerDataByAccount", [(double)maxed.Id])!;
                    TestAssert.Equal(29d, ((AsObject)profile["summonerTalentsAndPoints"])["unusedTalentPoints"]);
                    var fullRanks = new Dictionary<string, object>
                    {
                        ["103"] = 3, ["106"] = 3, ["126"] = 1, ["102"] = 4, ["105"] = 4,
                        ["146"] = 1, ["101"] = 3, ["100"] = 3, ["104"] = 3,
                        ["110"] = 3, ["133"] = 1, ["129"] = 1
                    };
                    saved = (AsObject)rpc.Route("mastery-rpc", "summonerService", "changeTalentRankings",
                        [(double)maxed.SummonerId, fullRanks])!;
                    TestAssert.Equal(30d, saved["talentPoints"]);
                    TestAssert.Equal(0d, saved["unusedTalentPoints"]);
                    TestAssert.Equal(30, accounts.Get(maxed.Id).Loadout.Masteries!.Values.Sum());
                }
                finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }

            using var reopened = new PlatformAccounts(root);
            TestAssert.Equal(3, reopened.Get(maxedId).Loadout.Masteries![talentId]);
            TestAssert.Equal(30, reopened.Get(maxedId).Loadout.Masteries!.Values.Sum());
            TestAssert.Equal(30d, AirDtos.Talents(reopened.Get(maxedId))["talentPoints"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-public-summoner-rpc-contract")]
    public static void PublicSummonerLookupsUseTypedPublicDtos()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-public-summoner-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            var owner = accounts.RegisterPublic("public_owner", "testing-password", "Public Owner", maxed: true);
            var foreign = accounts.RegisterPublic("public_foreign", "testing-password", "Public Foreign", maxed: true);
            var mastery = new MasteryCatalog(GameData.Default.Paths).Definitions.First(definition =>
                definition.MinLevel <= foreign.Loadout.SummonerLevel && definition.PointsToActivate == 0 && definition.PrerequisiteId is null);
            accounts.SaveLoadout(foreign.Id, foreign.Loadout with { Masteries = new Dictionary<int, int> { [mastery.Id] = 1 } });
            foreign = accounts.Get(foreign.Id);
            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("public-rpc", owner.Username, sessions.IssueTicket(owner), "1.45.11_10_04_15_28");
            TestAssert.True(sessions.AuthenticateChannel("public-rpc", owner.Username, session.Token));

            var manager = CreateNonLaunchingManager(root);
            try
            {
                var rpc = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1"));
                var byName = (AsObject)rpc.Route("public-rpc", "summonerService", "getSummonerByName", [foreign.Summoner])!;
                AssertPublicSummoner(byName, foreign);

                var publicList = (ArrayCollection)rpc.Route("public-rpc", "summonerService", "getPublicSummonersByAccountIds",
                    [AirWire.List([(double)foreign.Id])])!;
                TestAssert.Equal(1, publicList.Count);
                AssertPublicSummoner((AsObject)publicList[0], foreign);
                var decodedList = (ArrayCollection)RoundTrip(publicList);
                AssertPublicSummoner((AsObject)decodedList[0], foreign);

                var decoded = (AsObject)RoundTrip(byName);
                AssertPublicSummoner(decoded, foreign);
                TestAssert.Equal("com.riotgames.platform.summoner.PublicSummoner", decoded.TypeName);

                var self = (AsObject)rpc.Route("public-rpc", "summonerService", "getSummoner", [(double)owner.SummonerId])!;
                TestAssert.Equal("com.riotgames.platform.summoner.Summoner", self.TypeName);
                TestAssert.Equal(true, self["isMe"]);
                TestAssert.True(self.ContainsKey("tutorialFlag"));
                TestAssert.True(self.ContainsKey("helpFlag"));
            }
            finally
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-summoner-preferences-persistence")]
    public static void OwnSummonerPreferencesPersistAcrossAccountStoreReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlatformAccount account;
            using (var accounts = new PlatformAccounts(root))
            {
                account = accounts.RegisterPublic("profile", "testing-password", "Profile");
                accounts.SaveSummonerPreferences(account.Id, account.SummonerId, 17, tutorialFlag: true, helpFlag: true);
                TestAssert.Equal(17, accounts.Get(account.Id).ProfileIconId);
            }

            using var reopened = new PlatformAccounts(root);
            TestAssert.Equal(17, reopened.Get(account.Id).ProfileIconId);
            TestAssert.True(reopened.Get(account.Id).TutorialFlag);
            TestAssert.True(reopened.Get(account.Id).HelpFlag);
            reopened.SaveSummonerPreferences(account.Id, account.SummonerId, 18);
            TestAssert.True(reopened.Get(account.Id).TutorialFlag, "an icon-only update retains prompt preferences");
            TestAssert.True(reopened.Get(account.Id).HelpFlag);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-profile-icon-validation")]
    public static void ProfileIconSaveRejectsForeignIdentityAndUnsupportedIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-profile-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var accounts = new PlatformAccounts(root);
            var first = accounts.RegisterPublic("profileone", "testing-password", "Profile One");
            var second = accounts.RegisterPublic("profiletwo", "testing-password", "Profile Two");

            TestAssert.Throws<ArgumentException>(() => accounts.SaveSummonerPreferences(first.Id, second.SummonerId, 1));
            TestAssert.Throws<ArgumentException>(() => accounts.SaveSummonerPreferences(999, first.SummonerId, 1));
            TestAssert.Throws<ArgumentOutOfRangeException>(() => accounts.SaveSummonerPreferences(first.Id, first.SummonerId, -1));
            TestAssert.Throws<ArgumentOutOfRangeException>(() => accounts.SaveSummonerPreferences(first.Id, first.SummonerId, 29));
            TestAssert.Equal(0, accounts.Get(first.Id).ProfileIconId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPublicSummoner(AsObject value, PlatformAccount account)
    {
        TestAssert.Equal("com.riotgames.platform.summoner.PublicSummoner", value.TypeName);
        TestAssert.Equal((double)account.SummonerId, value["summonerId"]);
        TestAssert.Equal((double)account.Id, value["acctId"]);
        TestAssert.Equal(account.Summoner, value["name"]);
        TestAssert.Equal(AirGameDtos.InternalName(account), value["internalName"]);
        TestAssert.Equal(account.ProfileIconId, value["profileIconId"]);
        TestAssert.Equal((double)account.Loadout.SummonerLevel, value["summonerLevel"]);
        TestAssert.True(((ArrayCollection)value["summonerAssociatedTalents"]).Count > 0);
        foreach (var privateField in new[]
        {
            "isMe", "tutorialFlag", "helpFlag", "socialNetworkUserIds", "revisionId", "revisionDate", "lastGameDate", "nameChangeFlag"
        })
            TestAssert.False(value.ContainsKey(privateField), $"public summoner must not expose {privateField}");
    }

    private static object RoundTrip(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(value);
        using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), new SerializationContext());
        return reader.ReadAmf3Item()!;
    }

    private sealed class BackingArrayCapture : IDataOutput
    {
        public object[]? Backing { get; private set; }
        public void WriteObject(object value) => Backing = (object[])value;
        public void WriteBoolean(bool value) => throw new NotSupportedException();
        public void WriteByte(byte value) => throw new NotSupportedException();
        public void WriteBytes(byte[] buffer) => throw new NotSupportedException();
        public void WriteDouble(double value) => throw new NotSupportedException();
        public void WriteFloat(float value) => throw new NotSupportedException();
        public void WriteInt16(short value) => throw new NotSupportedException();
        public void WriteUInt16(ushort value) => throw new NotSupportedException();
        public void WriteUInt24(int value) => throw new NotSupportedException();
        public void WriteInt32(int value) => throw new NotSupportedException();
        public void WriteUInt32(uint value) => throw new NotSupportedException();
        public void WriteUtf(string value) => throw new NotSupportedException();
        public void WriteUtfBytes(string value) => throw new NotSupportedException();
    }

    private static GameManager CreateNonLaunchingManager(string root)
    {
        var dataRoot = Path.Combine(root, "game-data");
        Directory.CreateDirectory(dataRoot);
        var executable = Path.Combine(root, "server-placeholder.exe");
        File.WriteAllBytes(executable, []);
        return new GameManager(new HostSettings(dataRoot, executable, FirstGamePort: EphemeralPort(), MaxGames: 1, BindAddress: "127.0.0.1"),
            Path.Combine(root, "games"));
    }

    private static int EphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
