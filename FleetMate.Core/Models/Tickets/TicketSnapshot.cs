using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Tickets;
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
    private const long MaximumBytes = 64 * 1024 * 1024;

    public TicketSnapshot(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "Tickets"), "snapshot.json");
    }

    /// <summary>Only confirmed service/person scopes may reuse new helper snapshots.</summary>
    public static TicketSnapshot ForScope(string serviceUrl, Guid person, TicketDateRangePreset preset,
        string? directory = null)
    {
        if (person == Guid.Empty) throw new ArgumentException("A confirmed ticket person is required.", nameof(person));
        return new TicketSnapshot(Path.Combine(directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "Tickets"),
            TicketInboxReadStore.ServiceKey(serviceUrl), person.ToString("D"), preset.ToString()));
    }

    public string FilePath => _path;

    /// <summary>The saved list, or empty when there is none or it cannot be read.</summary>
    public List<TdxTicket> Load() => Load(null);

    public List<TdxTicket> Load(TimeSpan? maximumAge)
    {
        try
        {
            if (!File.Exists(_path)) return new List<TdxTicket>();
            if (maximumAge is { } age && DateTime.UtcNow - File.GetLastWriteTimeUtc(_path) > age) return new();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumBytes) return new();
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
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, tickets);
            if (new FileInfo(temp).Length > MaximumBytes) throw new InvalidDataException("Ticket snapshot exceeds the supported size.");
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tickets] Ticket list snapshot not saved");
        }
        finally
        {
            try { if (temp != null && File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Save in the background; the caller keeps its own list.</summary>
    public Task SaveInBackground(IReadOnlyList<TdxTicket> tickets)
    {
        var copy = tickets.ToList();
        return Task.Run(() => Save(copy));
    }
}
