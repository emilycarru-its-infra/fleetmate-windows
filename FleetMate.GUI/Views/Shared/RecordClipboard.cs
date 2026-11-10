using System.Windows;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Shared;

public static class RecordClipboard
{
    public static bool Copy(string title, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        RecordLinkPayload payload;
        try { payload = RecordLinkPayload.Create(title, url); }
        catch (ArgumentException) { return false; }
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, payload.Text);
        data.SetData(DataFormats.Html, payload.Html);
        return AgentContextClipboard.SetData(data);
    }
}
