using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: the player's desktop shortcut, a Windows <c>.lnk</c> written and read through the shell's own ShellLink object
/// (IShellLinkW and IPersistFile, part of Windows). The icon is a reference (<c>path,index</c>) to an executable that already holds
/// it; no icon is copied.
/// </summary>
internal static class ShellLink
{
    internal sealed record Link(string Target, string Arguments, string WorkingDirectory, string IconPath, int IconIndex, string Description);

    public static void Write(string path, Link link)
    {
        var shell = (IShellLinkW)new ShellLinkObject();
        try
        {
            shell.SetPath(link.Target);
            shell.SetArguments(link.Arguments);
            shell.SetWorkingDirectory(link.WorkingDirectory);
            shell.SetIconLocation(link.IconPath, link.IconIndex);
            shell.SetDescription(link.Description);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            ((IPersistFile)shell).Save(Path.GetFullPath(path), true);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    public static Link Read(string path)
    {
        var shell = (IShellLinkW)new ShellLinkObject();
        try
        {
            ((IPersistFile)shell).Load(Path.GetFullPath(path), 0);
            var target = new StringBuilder(1024);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            var arguments = new StringBuilder(1024);
            shell.GetArguments(arguments, arguments.Capacity);
            var directory = new StringBuilder(1024);
            shell.GetWorkingDirectory(directory, directory.Capacity);
            var icon = new StringBuilder(1024);
            shell.GetIconLocation(icon, icon.Capacity, out var index);
            var description = new StringBuilder(1024);
            shell.GetDescription(description, description.Capacity);
            return new(target.ToString(), arguments.ToString(), directory.ToString(), icon.ToString(), index, description.ToString());
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    // The methods in the order Windows declares them: every one up to SetPath must be listed for the calls to reach the right slot.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
