using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Tags a downloaded file as coming from the internet: the Zone.Identifier
/// stream Windows reads before it opens a file, so SmartScreen, Office
/// Protected View and the "this file came from another computer" prompts
/// all apply to it as they would to a browser download.
/// </summary>
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    /// <summary>Zone 3 is the Internet zone.</summary>
    internal static string Content(string? hostUrl)
    {
        var text = "[ZoneTransfer]\r\nZoneId=3\r\n";
        if (Uri.TryCreate(hostUrl, UriKind.Absolute, out var host) && host.Scheme is "https" or "http")
            text += $"HostUrl={host.GetLeftPart(UriPartial.Authority)}/\r\n";
        return text;
    }

    /// <summary>
    /// Write the mark onto <paramref name="path"/>. False when the volume has
    /// no alternate streams (FAT, some network shares) or the write failed;
    /// a caller about to open the file should then refuse to.
    /// </summary>
    public static bool Apply(string path, string? hostUrl = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            File.WriteAllText($"{path}:{StreamName}", Content(hostUrl));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[motw] Could not mark {File} as downloaded", Path.GetFileName(path));
            return false;
        }
    }

    /// <summary>Whether <paramref name="path"/> carries an Internet-zone mark.</summary>
    public static bool IsMarked(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return File.ReadAllText($"{path}:{StreamName}").Contains("ZoneId=3", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
