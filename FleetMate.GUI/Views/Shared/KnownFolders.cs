using System.IO;
using System.Runtime.InteropServices;

namespace FleetMate.GUI.Views.Shared;

/// <summary>Windows known folders .NET has no SpecialFolder for.</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// The user's Downloads folder, wherever it has been moved to, so a save
    /// lands where a browser download would rather than on a synced Desktop.
    /// </summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0 && Directory.Exists(path))
                    return path;
            }
            catch (Exception)
            {
                // Fall through to the conventional location.
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        [MarshalAs(UnmanagedType.LPWStr)] out string pszPath);
}
