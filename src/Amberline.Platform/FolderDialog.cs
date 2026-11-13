using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the Windows folder window of the host's setup page: the shell's own FileOpenDialog in folder mode (IFileDialog with
/// FOS_PICKFOLDERS, the window WinForms' FolderBrowserDialog shows), part of Windows. It runs on its own STA thread with no owner
/// window, so it can open behind the browser; the page says so. One window at a time. Batch 765: the same window picks the client
/// zip as a file (<see cref="ChooseZip"/>, the filter <c>*.zip</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FolderDialog
{
    private const uint PickFolders = 0x20, ForceFileSystem = 0x40, PathMustExist = 0x800, FileMustExist = 0x1000;
    private const uint FileSystemPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);
    private static int _open;

    /// <summary>The folder the host picked, or null when they closed the window.</summary>
    /// <exception cref="InvalidOperationException">A folder window is already open.</exception>
    public static string? Choose(string title) => Pick(title, zip: false);

    /// <summary>NOT RETAIL (batch 765): the client zip the host picked (a <c>.zip</c> file), or null when they closed the window.</summary>
    /// <exception cref="InvalidOperationException">A folder or file window is already open.</exception>
    public static string? ChooseZip(string title) => Pick(title, zip: true);

    private static string? Pick(string title, bool zip)
    {
        var what = zip ? "file" : "folder";
        if (Interlocked.Exchange(ref _open, 1) == 1)
            throw new InvalidOperationException($"A {what} window is already open. It may be behind this browser window.");
        try
        {
            string? chosen = null;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                // Every failure is carried back: an exception left on this thread would end the whole Platform.
                try { chosen = Show(title, zip); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return failure is null ? chosen : throw new InvalidOperationException(zip
                ? $"The file window did not open ({failure.Message}). Choose the folders instead."
                : $"The folder window did not open ({failure.Message}). Paste the folder's address instead.");
        }
        finally { Volatile.Write(ref _open, 0); }
    }

    private static string? Show(string title, bool zip)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | ForceFileSystem | PathMustExist | (zip ? FileMustExist : PickFolders));
            if (zip) dialog.SetFileTypes(1, [new FilterSpec { Name = "Client zip (*.zip)", Spec = "*.zip" }]);
            dialog.SetTitle(title);
            var result = dialog.Show(0);
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(FileSystemPath, out var path);
                return path;
            }
            finally { Marshal.FinalReleaseComObject(item); }
        }
        finally { Marshal.FinalReleaseComObject(dialog); }
    }

    /// <summary>COMDLG_FILTERSPEC: one line of the window's file-type list.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // IModalWindow's Show, then IFileDialog's methods in their declared order (shobjidl.h), up to GetResult.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
