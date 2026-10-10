using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// Settings for finding and cloning repositories. Stored in the registry file
/// so the CLI and the app read and write the same values. A key missing from
/// the file keeps its default, so an older or hand-edited file keeps loading.
/// </summary>
public sealed class RepoSettings
{
    /// <summary>Folders <c>discover</c> scans for git checkouts. <c>~</c> and %VARIABLES% are expanded.</summary>
    public List<string> ScanRoots { get; set; } = new() { @"~\Developer" };
    /// <summary>How many folder levels below each root to look.</summary>
    public int ScanDepth { get; set; } = 4;
    /// <summary>Folder names never descended into. Hidden folders are always skipped.</summary>
    public List<string> SkipDirectories { get; set; } = new()
        { ".worktrees", "node_modules", ".build", "build", "bin", "obj", "packages", "DerivedData", "Pods", "vendor" };
    /// <summary>
    /// Root of the default clone layout: <c>&lt;root&gt;\AzDevOps\&lt;Project&gt;\&lt;Repo&gt;</c>
    /// and <c>&lt;root&gt;\GitHub\&lt;owner&gt;\&lt;repo&gt;</c>.
    /// </summary>
    public string CloneRoot { get; set; } = @"~\Developer";
    /// <summary>GitHub owners listed in the catalog beyond the ones the signed-in user belongs to.</summary>
    public List<string> GitHubOwners { get; set; } = new();
    /// <summary>
    /// Branches <c>commit</c> and <c>push</c> refuse without <c>--allow-main</c>.
    /// A repository's own default branch is always included.
    /// </summary>
    public List<string> ProtectedBranches { get; set; } = new() { "main", "master" };
    /// <summary>Upper bound on git processes a batch command runs at once.</summary>
    public int Concurrency { get; set; } = 6;

    public static RepoSettings Default => new();

    /// <summary>Expands a leading <c>~</c> and environment variables.</summary>
    public static string Expand(string path)
    {
        var value = Environment.ExpandEnvironmentVariables(path.Trim());
        if (value == "~" || value.StartsWith("~/") || value.StartsWith(@"~\"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = value.Length == 1 ? home : Path.Combine(home, value[2..]);
        }
        return value;
    }

    /// <summary>The expanded, absolute form of a path, without a trailing separator.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(Expand(path));
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }
}

/// <summary>One local checkout FleetMate knows about.</summary>
public sealed record RepoRegistryEntry
{
    public required RepoKey Key { get; init; }
    /// <summary>Absolute path of the checkout.</summary>
    public required string Path { get; init; }
    /// <summary>
    /// A tracked repository is one of the team's core repositories: batch
    /// commands act on tracked repositories by default.
    /// </summary>
    public bool Tracked { get; init; }
    public string? RemoteUrl { get; init; }
    public string? DefaultBranch { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// The on-disk registry document:
/// <code>
/// {
///   "version": 1,
///   "settings": { "scanRoots": ["~\\Developer"], "cloneRoot": "~\\Developer", ... },
///   "repos": {
///     "github:example-org/example-repo": {
///       "key": { "provider": "github", "owner": "example-org", "name": "example-repo" },
///       "path": "C:\\Users\\me\\Developer\\GitHub\\example-org\\example-repo",
///       "tracked": true, "remoteUrl": "...", "defaultBranch": "main", "addedAt": "..."
///     }
///   }
/// }
/// </code>
/// </summary>
public sealed class RepoRegistryDocument
{
    public int Version { get; set; } = 1;
    public RepoSettings Settings { get; set; } = new();
    /// <summary>Registry id (<see cref="RepoKey.Id"/>) → entry.</summary>
    public Dictionary<string, RepoRegistryEntry> Repos { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<RepoRegistryEntry> Entries => Repos.Values.OrderBy(e => e.Key.Id, StringComparer.Ordinal).ToList();

    [JsonIgnore]
    public IReadOnlyList<RepoRegistryEntry> TrackedEntries => Entries.Where(e => e.Tracked).ToList();
}

/// <summary>
/// Reads and writes the shared registry (<c>repos.json</c>) and catalog cache
/// (<c>repos-catalog.json</c>) in FleetMate's local application data folder.
/// Plain JSON files, so the CLI and the app see each other's changes.
///
/// Every mutation is a load-modify-save of the whole file with an atomic
/// replace, which keeps the window for two writers racing to milliseconds.
/// </summary>
public sealed class RepoRegistryStore
{
    public string RegistryPath { get; }
    public string CatalogPath { get; }

    public static string SupportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate");

    public RepoRegistryStore(string? registryPath = null, string? catalogPath = null)
    {
        RegistryPath = registryPath ?? Path.Combine(SupportFolder, "repos.json");
        CatalogPath = catalogPath ?? Path.Combine(Path.GetDirectoryName(RegistryPath)!, "repos-catalog.json");
    }

    // ── Registry ────────────────────────────────────────────────────────

    public RepoRegistryDocument Load()
    {
        if (!File.Exists(RegistryPath)) return new RepoRegistryDocument();
        var document = JsonSerializer.Deserialize<RepoRegistryDocument>(File.ReadAllText(RegistryPath), Json)
            ?? new RepoRegistryDocument();
        document.Settings ??= new RepoSettings();
        document.Repos ??= new();
        return document;
    }

    public void Save(RepoRegistryDocument document) =>
        WriteAtomically(RegistryPath, JsonSerializer.Serialize(document, Json));

    /// <summary>Applies <paramref name="change"/> to the current document and saves it.</summary>
    public T Update<T>(Func<RepoRegistryDocument, T> change)
    {
        var document = Load();
        var result = change(document);
        Save(document);
        return result;
    }

    public void Update(Action<RepoRegistryDocument> change) => Update<bool>(d => { change(d); return true; });

    // ── Catalog cache ───────────────────────────────────────────────────

    public RepoCatalog? LoadCatalog()
    {
        try
        {
            return File.Exists(CatalogPath)
                ? JsonSerializer.Deserialize<RepoCatalog>(File.ReadAllText(CatalogPath), Json)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void SaveCatalog(RepoCatalog catalog) =>
        WriteAtomically(CatalogPath, JsonSerializer.Serialize(catalog, Json));

    // ── Private ─────────────────────────────────────────────────────────

    private static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The JSON shape shared with FleetMate for Mac: camelCase, ISO dates, provider codes.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
