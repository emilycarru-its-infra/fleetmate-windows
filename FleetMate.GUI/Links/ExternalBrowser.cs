using System.Diagnostics;
using FleetMate.Core.Links;
using Serilog;

namespace FleetMate.GUI.Links;

/// <summary>
/// Opens web pages in the default browser. Every "Open in Browser" goes through
/// here so Azure DevOps pages carry <c>fleetmate=browser</c> and a managed link
/// extension does not hand them straight back (<see cref="BrowserLink"/>).
/// </summary>
public static class ExternalBrowser
{
    /// <summary>Open <paramref name="url"/>; false when there is none or the shell refused it.</summary>
    public static bool Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var target = BrowserLink.ForBrowser(url.Trim());
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url} in the browser", target);
            return false;
        }
    }

    public static bool Open(Uri? url) => url != null && Open(url.AbsoluteUri);
}
