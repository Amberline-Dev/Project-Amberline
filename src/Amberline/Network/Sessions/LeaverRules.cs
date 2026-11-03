using Amberline.Data;

namespace Amberline.Network.Sessions;

/// <summary>
/// The shipped numbers that decide when disconnected time makes a leaver: DATA/CFG/defaults/GamePermanent.cfg
/// <c>[Disconnect]</c> (<c>MinPercentTimeForValidOnWin</c> .12, <c>MinPercentTimeForValidOnFail</c> .10,
/// <c>MinTimeForValidOnWinAtEndOfGame</c> 300, <c>MinTimeForValidOnFailAtEndOfGame</c> 240). Their comments name only the
/// percentage ("how much of a percentage of a game has someone spent disconnected to be considered a leave") and a time
/// "disconnected at the end of the game" before it counts. Reconstruction: the reader of these keys was in the retail server,
/// so the exact comparison (at least, on the share; at least, on the final disconnection) and the use of the team's win or
/// loss for the pair are read from those comments.
/// </summary>
public sealed record LeaverRules(
    double PercentOnWin, double PercentOnFail, double SecondsAtEndOnWin, double SecondsAtEndOnFail)
{
    // The shipped values, used only when the cfg has no [Disconnect] section.
    private static readonly LeaverRules Shipped = new(0.12, 0.10, 300.0, 240.0);

    public static LeaverRules Load(GameDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.Data("CFG/defaults/GamePermanent.cfg");
        if (!File.Exists(path)) return Shipped;
        var config = ClientTextConfig.Load(path);
        return new LeaverRules(
            config.Float("Disconnect", "MinPercentTimeForValidOnWin", Shipped.PercentOnWin),
            config.Float("Disconnect", "MinPercentTimeForValidOnFail", Shipped.PercentOnFail),
            config.Float("Disconnect", "MinTimeForValidOnWinAtEndOfGame", Shipped.SecondsAtEndOnWin),
            config.Float("Disconnect", "MinTimeForValidOnFailAtEndOfGame", Shipped.SecondsAtEndOnFail));
    }

    /// <summary>
    /// A leaver spent at least the team's share of the game disconnected, or is still disconnected at the end and has been
    /// since at least the team's end-of-game time. A seat the start barrier never waited for is disconnected from the start.
    /// </summary>
    public bool IsLeaver(bool won, double timePlayed, double timeDisconnected, bool disconnectedAtEnd,
        double secondsSinceDisconnect)
    {
        if (!(timePlayed > 0.0)) return false;
        var share = won ? PercentOnWin : PercentOnFail;
        var atEnd = won ? SecondsAtEndOnWin : SecondsAtEndOnFail;
        return timeDisconnected / timePlayed >= share ||
               disconnectedAtEnd && secondsSinceDisconnect >= atEnd;
    }
}
