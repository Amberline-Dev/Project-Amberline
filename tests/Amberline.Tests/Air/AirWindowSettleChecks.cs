using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static class AirWindowSettleChecks
{
    [Check("server.air-window-settle-restore")]
    public static void RestoresAHiddenAirOnceAFewSecondsAfterTheGameEndsAndNeverAgain()
    {
        // The native stop arrives while AIR is still hidden: its own end-of-game restore did not run.
        var stranded = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, stranded.Observe(400, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nativeStopped: true, nowMilliseconds: 1000), "the settle clock starts at the native stop");
        TestAssert.Equal(AirWindowAction.None, stranded.Observe(400, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nativeStopped: true, nowMilliseconds: 4999), "no restore before the settle time");
        TestAssert.Equal(AirWindowAction.RestoreHidden, stranded.Observe(400, ownedWindow: true, iconic: false,
            visible: false, airFocused: false, nativeStopped: true, nowMilliseconds: 5000),
            "still hidden 4 s after the stop: restore it once");
        TestAssert.Equal(AirWindowAction.None, stranded.Observe(null, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 9000), "one restore per game, even if the window hides again");

        // Only the process exit is seen (no native stop): the clock starts at the first post-game sample.
        var exited = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, exited.Observe(401, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 0), "hidden during the game: no command");
        TestAssert.Equal(AirWindowAction.None, exited.Observe(null, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 1000), "the process exit starts the settle clock");
        TestAssert.Equal(AirWindowAction.None, exited.Observe(null, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 4999));
        TestAssert.Equal(AirWindowAction.RestoreHidden, exited.Observe(null, ownedWindow: true, iconic: false,
            visible: false, airFocused: false, nowMilliseconds: 5000), "hidden 4 s after the exit: restore it once");

        // AIR restored itself: nothing to do, and a later user hide is not undone.
        var restored = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, restored.Observe(402, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nativeStopped: true, nowMilliseconds: 0));
        TestAssert.Equal(AirWindowAction.None, restored.Observe(402, ownedWindow: true, iconic: false, visible: true,
            airFocused: true, nativeStopped: true, nowMilliseconds: 4000), "visible when the check runs: left alone");
        TestAssert.Equal(AirWindowAction.None, restored.Observe(null, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 60000), "checked once: a later tray hide is not undone");

        // A live game with no native stop never settles, however long AIR stays hidden in it.
        var live = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, live.Observe(403, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 0));
        TestAssert.Equal(AirWindowAction.None, live.Observe(403, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nowMilliseconds: 60000), "hidden in a live game is the tray-only state");

        // Without a verifiable window the check waits: it is not spent on an unowned sample.
        var unowned = new AirWindowTransition();
        TestAssert.Equal(AirWindowAction.None, unowned.Observe(404, ownedWindow: true, iconic: false, visible: false,
            airFocused: false, nativeStopped: true, nowMilliseconds: 0));
        TestAssert.Equal(AirWindowAction.None, unowned.Observe(404, ownedWindow: false, iconic: false, visible: false,
            airFocused: false, nativeStopped: true, nowMilliseconds: 5000), "no verifiable window: no restore yet");
        TestAssert.Equal(AirWindowAction.RestoreHidden, unowned.Observe(404, ownedWindow: true, iconic: false,
            visible: false, airFocused: false, nativeStopped: true, nowMilliseconds: 5500),
            "the first owned sample after the settle time restores it");
    }
}
