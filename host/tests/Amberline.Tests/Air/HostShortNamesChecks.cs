using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL (batch 923): a host whose game folder's files have no 8.3 short names gets a card under Needs your attention (A-FILES-07): Play on this PC
/// stops at its loading screen, players are not affected, and nothing is moved for the host. The short-names answer is a fake per folder
/// (<see cref="HostDashboard.ShortNames"/>), so no real drive without them is needed.
/// </summary>
public static class HostShortNamesChecks
{
    [Check("server.host-short-names-card", TimeoutSeconds = 120)]
    public static void AGameFolderWithoutShortNamesGetsACard() => CardAsync().GetAwaiter().GetResult();

    private static async Task CardAsync()
    {
        var home = HostModeChecks.Home.Create("league-host-short-names");
        // A folder that does not read like projects\...\releases\<version>: the game needs short names there on both lines.
        var plain = Directory.CreateDirectory(Path.Combine(home.Root, "Games", "plain")).FullName;
        try
        {
            var card = HostFolders.ShortNamesCard(plain, _ => "off");
            TestAssert.Equal(("A-FILES-07", HostFolders.NoShortNames, (string?)null), (card?.Code, card?.Message, card?.Action), "the card");
            TestAssert.True(card!.WhatToDo.StartsWith(PlatformLine.ShortNamesAdvice, StringComparison.Ordinal) &&
                card.WhatToDo.EndsWith("Your players are not affected.", StringComparison.Ordinal), card.WhatToDo);
            TestAssert.True(card.Subject.Length == 16 && !card.Subject.Contains("plain", StringComparison.OrdinalIgnoreCase), "the subject names the folder: " + card.Subject);
            TestAssert.True(HostHidden.Hideable("A-FILES-07"), "the card has Hide");
            foreach (var answer in new[] { "on", "unknown" })
                TestAssert.Equal(null, HostFolders.ShortNamesCard(plain, _ => answer), $"a folder whose files answer {answer}");
            // The 1.0.0.126 line's managed layout serves a layout-shaped folder on any drive; the 1.0.0.132 line's loose game needs them there too.
            var shaped = Path.Combine(home.Root, "projects", "lol_game_client", "releases", "0.0.0.51");
            TestAssert.Equal(PlatformLine.NeedsShortNames(shaped), HostFolders.ShortNamesCard(shaped, _ => "off") is not null, "a layout-shaped folder");

            // On the dashboard: the card shows for the host's game folder, Hide sets it aside, and a pass where the files have them forgets the entry.
            var settings = new PlatformSettings(plain, home.Loop.Paths.ServerExecutable, AirRoot: home.Air);
            await using (var dashboard = new HostDashboard(home.Loop, settings, () => [], () => []) { ShortNames = folder => OffFor(folder, plain) })
            {
                var shown = dashboard.Status(DateTimeOffset.Now).Attention.SingleOrDefault(item => item.Code == "A-FILES-07");
                TestAssert.Equal(card, shown, "the dashboard's card");
                dashboard.Hidden.Add("A-FILES-07", card.Subject);
                TestAssert.False(dashboard.Status(DateTimeOffset.Now).Attention.Any(item => item.Code == "A-FILES-07"), "Hide");
            }
            await using (var fixedNow = new HostDashboard(home.Loop, settings, () => [], () => []) { ShortNames = _ => "on" })
            {
                TestAssert.False(fixedNow.Status(DateTimeOffset.Now).Attention.Any(item => item.Code == "A-FILES-07"), "no card once the files have them");
                TestAssert.False(fixedNow.Hidden.Contains("A-FILES-07", card.Subject), "the hidden entry is forgotten");
            }
        }
        finally
        {
            try { Directory.Delete(home.Root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string OffFor(string folder, string game) => string.Equals(Path.GetFullPath(folder), game, StringComparison.OrdinalIgnoreCase) ? "off" : "on";
}
