namespace Amberline.Platform;

/// <summary>What <see cref="UserRemoval.Delete"/> did, and the tidy-up steps it could not save just now (their names, for the page).</summary>
internal sealed record DeleteResult(DeleteOutcome Outcome, IReadOnlyList<string> Unsaved);

/// <summary>
/// NOT RETAIL (batch 748; batch 751 made it shared): the host's Delete, Disable and Enable on Users (<see cref="HostInvite"/>) and a user's own
/// New password and Delete my account (<see cref="HostAccountPage"/>) end a user's ways in through this one class, so the two pages cannot
/// drift. Retail's logins were Riot's to close. No line it writes names the user: the support log, which a host may paste in public, quotes them.
/// </summary>
internal sealed class UserRemoval(HostLoop loop, PlatformAccounts accounts, PlatformGames? games, PlatformSessions sessions, AirRemoteAccess remoteAccess,
    AirXmppHost? chat)
{
    /// <summary>
    /// The login goes for good while history keeps its names (<see cref="DeletedAccount"/>); a user in a lobby or a game is refused
    /// (<see cref="PlatformGames.DeleteAccount"/>; without the games, the store's own rule). Its sessions end first, in memory, so nothing below
    /// can leave it logged in; then <paramref name="activityLine"/> goes to the feed, and its remote sign-in, chat streams and the Cheat
    /// commands list are tidied in steps of their own, so one that cannot be saved just now does not keep the others.
    /// </summary>
    internal DeleteResult Delete(PlatformAccount user, string activityLine)
    {
        var outcome = games?.DeleteAccount(user.Id) ?? accounts.Delete(user.Id);
        if (outcome != DeleteOutcome.Deleted) return new(outcome, []);
        sessions.EndAccount(user.Id);
        loop.Activity.Add(activityLine);
        var unsaved = new List<string>();
        void Tidy(string what, Action step)
        {
            try { step(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsUnreadableException)
            {
                Console.Error.WriteLine($"Amberline deleted a user but could not save {what}: {ex.Message}");
                unsaved.Add(what);
            }
        }
        Tidy("their remote sign-in", () => remoteAccess.Revoke(user.Id));
        Tidy("their chat contacts", () => chat?.Forget(user.Id));
        Tidy("the Cheat commands list", () => ClearCheats(user.Id));
        return new(DeleteOutcome.Deleted, unsaved);
    }

    /// <summary>
    /// Signs a user out of the client: the sessions end first, in memory, so a token file that cannot be written never leaves the user logged
    /// in; the remote sign-in token is then revoked on a step of its own. False: that revoke could not be saved just now (it is logged, and the
    /// token is gone from memory all the same; after a restart the tunnel still refuses a disabled or deleted login's token, AirRemoteAccess.Allowed).
    /// </summary>
    internal bool LogOut(long id)
    {
        sessions.EndAccount(id);
        return Revoke(remoteAccess, id);
    }

    /// <summary>
    /// The remote sign-in token's revoke as its own step (also Advanced's Set a new password and Make them log in again, batch 751 final review):
    /// false when the file could not be saved just now; it is logged without the user's name.
    /// </summary>
    internal static bool Revoke(AirRemoteAccess? remoteAccess, long id)
    {
        try { remoteAccess?.Revoke(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Amberline could not save a user's remote sign-in: {ex.Message}");
            return false;
        }
        return true;
    }

    // Owner, 4 November 2026: Disable also clears the user's Cheat commands tick. It runs after every way in has ended, so a settings file
    // that cannot be read (the page then says so) never leaves a session open; Enable clears a tick such a failure left, so a returning
    // user never has cheats the host did not tick again; Delete clears it as the login goes.
    internal void ClearCheats(long id) => HostMode.Change(loop.Paths.Settings, saved => saved.CheatLogins().Contains(id)
        ? saved with { CheatAccounts = [.. saved.CheatLogins().Where(other => other != id)], AllowCheats = false } : saved);
}
