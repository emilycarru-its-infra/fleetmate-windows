using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace FleetMate.Core.Services.Tickets;

/// <summary>Shared read decisions only. Bodies and scan snapshots remain edition-owned.</summary>
public sealed class TicketInboxReadStore
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<int, bool> Decisions { get; set; } = new();
        public HashSet<string> Imports { get; set; } = new();
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

    /// <summary>Read-modify-write under a cross-process lock; never save a caller's stale snapshot.</summary>
    public HashSet<int> SetRead(Guid person, IEnumerable<int> ids, bool read)
    {
        Directory.CreateDirectory(_directory);
        using var held = AcquireLock(PathFor(person) + ".lock");
        var document = Read(person);
        foreach (var id in ids.Distinct().Where(id => id > 0)) document.Decisions[id] = read;
        Write(person, document);
        return document.Decisions.Where(p => p.Value).Select(p => p.Key).ToHashSet();
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
            File.WriteAllText(temp, JsonSerializer.Serialize(document));
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
