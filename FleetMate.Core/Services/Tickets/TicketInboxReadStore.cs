using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

/// <summary>Shared read decisions only. Bodies and scan snapshots remain edition-owned.</summary>
public sealed class TicketInboxReadStore
{
    public const long MaximumDocumentBytes = 16 * 1024 * 1024;

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<int, bool> Decisions { get; set; } = new();
        public HashSet<string> Imports { get; set; } = new();
        // Optional, so files written by earlier builds still decode at version 1.
        public Dictionary<int, DateTime>? UnreadAt { get; set; }
        public Dictionary<int, DateTime>? TicketOpenedAt { get; set; }
    }

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    public TicketInboxReadStore(string directory) => _directory = directory;
    public string PathFor(Guid person) => Path.Combine(_directory, $"{person}.json");
    public static string ServiceKey(string ticketsUrl)
    {
        var uri = new Uri(ticketsUrl, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("A ticket service HTTP address is required.", nameof(ticketsUrl));
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The ticket service address must not contain credentials or parameters.", nameof(ticketsUrl));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant()))).ToLowerInvariant();
    }

    public HashSet<int> Load(Guid person) => Read(person).Decisions.Where(p => p.Value).Select(p => p.Key).ToHashSet();

    public TicketInboxReadMarks LoadMarks(Guid person) => Marks(Read(person));

    private static TicketInboxReadMarks Marks(Document document) => new(
        document.Decisions.Where(p => p.Value).Select(p => p.Key).ToHashSet(),
        document.UnreadAt == null ? new() : new(document.UnreadAt),
        document.TicketOpenedAt == null ? new() : new(document.TicketOpenedAt));

    /// <summary>Read-modify-write under a cross-process lock; never save a caller's stale snapshot.</summary>
    public HashSet<int> SetRead(Guid person, IEnumerable<int> ids, bool read)
    {
        Directory.CreateDirectory(_directory);
        using var held = AcquireLock(PathFor(person) + ".lock");
        var document = Read(person);
        var now = DateTime.UtcNow;
        foreach (var id in ids.Distinct().Where(id => id > 0))
        {
            document.Decisions[id] = read;
            if (read) document.UnreadAt?.Remove(id);
            else (document.UnreadAt ??= new())[id] = now;
        }
        Prune(document, now);
        Write(person, document);
        return document.Decisions.Where(p => p.Value).Select(p => p.Key).ToHashSet();
    }

    /// <summary>
    /// A ticket was opened: covers its comments up to <paramref name="openedUtc"/>,
    /// plus the ones already listed, whose server time may run ahead of this clock.
    /// </summary>
    public TicketInboxReadMarks RecordTicketOpened(Guid person, int ticketId, IEnumerable<int> ids, DateTime openedUtc)
    {
        Directory.CreateDirectory(_directory);
        using var held = AcquireLock(PathFor(person) + ".lock");
        var document = Read(person);
        foreach (var id in ids.Distinct().Where(id => id > 0))
        {
            document.Decisions[id] = true;
            document.UnreadAt?.Remove(id);
        }
        var opened = document.TicketOpenedAt ??= new();
        opened[ticketId] = opened.TryGetValue(ticketId, out var previous) && previous > openedUtc ? previous : openedUtc;
        Prune(document, DateTime.UtcNow);
        Write(person, document);
        return Marks(document);
    }

    /// <summary>Times outlive the comment window by a week, as on the Mac.</summary>
    private static void Prune(Document document, DateTime nowUtc)
    {
        var cutoff = nowUtc - TicketInboxMatch.Window - TimeSpan.FromDays(7);
        foreach (var times in new[] { document.UnreadAt, document.TicketOpenedAt })
        {
            if (times == null) continue;
            foreach (var stale in times.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) times.Remove(stale);
        }
    }

    /// <summary>An explicit service declaration permits one edition's positive legacy marks once.</summary>
    public void ImportLegacy(Guid person, string edition, Func<IEnumerable<int>> loadMarks)
    {
        Directory.CreateDirectory(_directory);
        using var held = AcquireLock(PathFor(person) + ".lock");
        var document = Read(person);
        if (document.Imports.Contains(edition)) return;
        foreach (var id in loadMarks().Where(id => id > 0)) document.Decisions.TryAdd(id, true);
        document.Imports.Add(edition);
        Write(person, document);
    }

    private void Write(Guid person, Document document)
    {
        var path = PathFor(person);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (bytes.LongLength > MaximumDocumentBytes) throw new InvalidDataException("Inbox read metadata exceeds the supported size.");
            File.WriteAllBytes(temp, bytes);
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                try { File.Move(temp, path, overwrite: true); break; }
                catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public IDisposable Watch(Guid person, Action changed)
    {
        Directory.CreateDirectory(_directory);
        var watcher = new FileSystemWatcher(_directory, Path.GetFileName(PathFor(person)))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += (_, _) => changed();
        watcher.Created += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.Deleted += (_, _) => changed();
        watcher.Error += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private Document Read(Guid person)
    {
        if (!File.Exists(PathFor(person))) return new();
        using var stream = new FileStream(PathFor(person), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumDocumentBytes)
            throw new InvalidDataException("Inbox read metadata exceeds the supported size.");
        var document = JsonSerializer.Deserialize<Document>(stream) ?? throw new InvalidDataException("Invalid Inbox read metadata.");
        if (document.Version != 1 || document.Decisions == null || document.Imports == null)
            throw new InvalidDataException("Unsupported Inbox read metadata version.");
        return document;
    }

    private sealed class HeldLock(FileStream stream, object gate) : IDisposable
    {
        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { Monitor.Exit(gate); }
        }
    }

    private static IDisposable AcquireLock(string path)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new());
        Monitor.Enter(gate);
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                try { return new HeldLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), gate); }
                catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
            }
        }
        catch { Monitor.Exit(gate); throw; }
    }
}
