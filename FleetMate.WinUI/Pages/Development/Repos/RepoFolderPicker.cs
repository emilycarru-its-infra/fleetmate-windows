using System.Runtime.InteropServices;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The shell's folder picker, with one or several folders, owned by the main
/// window. WinUI's FolderPicker picks one folder only; choosing where code
/// lives often means several, as FleetMate.GUI's OpenFolderDialog allows.
/// </summary>
internal static class RepoFolderPicker
{
    private const uint FosPickFolders = 0x20, FosForceFileSystem = 0x40, FosAllowMultiSelect = 0x200, FosPathMustExist = 0x800;
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>The chosen folders, or an empty list when the person cancelled.</summary>
    public static IReadOnlyList<string> Pick(string title, bool multiple, string? startFolder = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            options |= FosPickFolders | FosForceFileSystem | FosPathMustExist;
            if (multiple) options |= FosAllowMultiSelect;
            dialog.SetOptions(options);
            dialog.SetTitle(title);
            if (startFolder != null && Directory.Exists(startFolder)
                && SHCreateItemFromParsingName(startFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
                dialog.SetFolder(folder);
            var owner = App.Current.Window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : IntPtr.Zero;
            var result = dialog.Show(owner);
            if (result == Cancelled) return Array.Empty<string>();
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResults(out var items);
            items.GetCount(out var count);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SigdnFileSysPath, out var path);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths;
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private const uint SigdnFileSysPath = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetPropertyStore(int flags, ref Guid iid, out IntPtr store);
        void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr list);
        void GetAttributes(int flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr items);
    }
}
