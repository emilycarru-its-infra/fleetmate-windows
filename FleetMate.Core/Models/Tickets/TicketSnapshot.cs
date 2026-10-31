using System.Text.Json;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Models.Tickets;

/// <summary>
/// The last loaded ticket list, saved so the next launch can show it while
/// sign-in and the fresh load run, instead of an empty window. Kept at
/// <c>%LOCALAPPDATA%\&lt;edition&gt;\Tickets\snapshot.json</c>.
/// </summary>
public sealed class TicketSnapshot
{
    private readonly string _path;

    public TicketSnapshot(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "Tickets"), "snapshot.json");
    }

    public string FilePath => _path;

    /// <summary>The saved list, or empty when there is none or it cannot be read.</summary>
    public List<TdxTicket> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TdxTicket>();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<List<TdxTicket>>(stream) ?? new List<TdxTicket>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tickets] Saved ticket list could not be read");
            return new List<TdxTicket>();
        }
    }

    /// <summary>
    /// Write the list through a temp file. A failed write only costs the next
    /// launch its head start, so it is logged, not raised.
    /// </summary>
    public void Save(IReadOnlyList<TdxTicket> tickets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Environment.ProcessId + ".tmp";
            using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, tickets);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tickets] Ticket list snapshot not saved");
        }
    }

    /// <summary>Save in the background; the caller keeps its own list.</summary>
    public Task SaveInBackground(IReadOnlyList<TdxTicket> tickets)
    {
        var copy = tickets.ToList();
        return Task.Run(() => Save(copy));
    }
}
