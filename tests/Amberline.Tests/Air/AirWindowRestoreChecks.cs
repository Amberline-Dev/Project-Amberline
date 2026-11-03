using System.Runtime.InteropServices;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static class AirWindowRestoreChecks
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint PmRemove = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out NativeMessage message, nint window, uint filterMin, uint filterMax,
        uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref NativeMessage message);

    [Check("server.air-copy-hidden-window-restore")]
    public static void RestoresTheHiddenWindowOfARunningCopyInsteadOfLeavingItInTheTray()
    {
        // A titled, unowned top-level window of this process created hidden stands in for an orphaned AIR copy's
        // main window, which Process.MainWindowHandle cannot see while it is hidden.
        var window = CreateWindowExW(0, "STATIC", "batch 373 hidden window", WsOverlappedWindow, 0, 0, 200, 120,
            0, 0, 0, 0);
        TestAssert.True(window != 0, "the stand-in window must be created");
        try
        {
            TestAssert.False(IsWindowVisible(window), "a window created without WS_VISIBLE starts hidden");
            TestAssert.Equal(1, AirWindowDiagnostics.RestoreHiddenWindows(new[] { Environment.ProcessId }),
                "the hidden titled window of the running copy is queued for a restore");
            // The restore is posted to this thread's queue, so pump it until the window shows.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!IsWindowVisible(window) && DateTime.UtcNow < deadline)
            {
                while (PeekMessageW(out var message, 0, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
                Thread.Sleep(10);
            }
            TestAssert.True(IsWindowVisible(window), "the restore shows the window again");
        }
        finally
        {
            DestroyWindow(window);
        }
    }
}
