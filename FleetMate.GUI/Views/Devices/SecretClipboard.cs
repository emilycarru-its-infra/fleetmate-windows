using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Puts a secret on the clipboard so Windows keeps it out of clipboard history
/// and cloud clipboard sync, and takes it off again after a minute if it is
/// still there. Only a hash of the value is kept, to recognise it.
/// </summary>
public static class SecretClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(60);

    /// <summary>Tells clipboard monitors, history included, to ignore this content.</summary>
    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    /// <summary>DWORD 0: not kept in clipboard history (Win+V).</summary>
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    /// <summary>DWORD 0: not synced to other devices.</summary>
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private static DispatcherTimer? _timer;

    /// <summary>The clipboard payload: the text plus the formats that keep it private.</summary>
    public static DataObject CreateDataObject(string secret)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, secret);
        data.SetData(ExcludeFromMonitoring, new MemoryStream(Array.Empty<byte>()));
        data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }

    public static void Copy(string secret)
    {
        Clipboard.SetDataObject(CreateDataObject(secret), copy: true);
        var hash = Hash(secret);

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = ClearAfter };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            try
            {
                // Clear only our value: the person may have copied something else since.
                if (Clipboard.ContainsText() && Hash(Clipboard.GetText()) == hash) Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; leave it */ }
        };
        _timer.Start();
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
