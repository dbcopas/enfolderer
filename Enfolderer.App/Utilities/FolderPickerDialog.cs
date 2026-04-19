using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Native Windows folder picker using the COM IFileOpenDialog with FOS_PICKFOLDERS.
/// Gives the modern Explorer-style folder browser without requiring WinForms.
/// </summary>
internal static class FolderPickerDialog
{
    public static string? Show(Window owner, string title)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();

        dialog.GetOptions(out uint options);
        dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
        dialog.SetTitle(title);

        var hwnd = new WindowInteropHelper(owner).Handle;
        var hr = dialog.Show(hwnd);

        if (hr != 0) // cancelled or error
            return null;

        dialog.GetResult(out IShellItem item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
        return path;
    }

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(); // unused
        void SetFileTypeIndex(); // unused
        void GetFileTypeIndex(); // unused
        void Advise(); // unused
        void Unadvise(); // unused
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(); // unused
        void SetFolder(); // unused
        void GetFolder(); // unused
        void GetCurrentSelection(); // unused
        void SetFileName(); // unused
        void GetFileName(); // unused
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel(); // unused
        void SetFileNameLabel(); // unused
        void GetResult(out IShellItem ppsi);
        // remaining methods not needed
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(); // unused
        void GetParent(); // unused
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        // remaining methods not needed
    }
}
