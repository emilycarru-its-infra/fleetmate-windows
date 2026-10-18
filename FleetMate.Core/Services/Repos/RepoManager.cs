using System.Text.Json.Serialization;

namespace FleetMate.Core.Services.Repos;

[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryAction>))]
public enum DiscoveryAction
{
    /// <summary>Newly registered.</summary>
    [JsonStringEnumMemberName("linked")] Linked,
    /// <summary>Already registered at this path.</summary>
    [JsonStringEnumMemberName("alreadyLinked")] AlreadyLinked,
    /// <summary>The repository is registered at another path that still exists; this copy was left alone.</summary>
    [JsonStringEnumMemberName("duplicate")] Duplicate,
    /// <summary>No origin remote, or one that could not be parsed.</summary>
    [JsonStringEnumMemberName("noRemote")] NoRemote,
    /// <summary>Its config can run commands, so it was linked without being tracked; tracking it is the person's call.</summary>
    [JsonStringEnumMemberName("needsReview")] NeedsReview,
}

/// <summary>What Choose Folder did: what it registered, and the clones it held back for review.</summary>
public sealed record TrackFoldersResult(List<DiscoveryResult> Results, List<DiscoveredCheckout> Review);

/// <summary>
/// What <c>discover</c> did with one checkout it found. For a duplicate,
/// <c>RegisteredPath</c> is the path already registered.
/// </summary>
public sealed record DiscoveryResult(
    string Path, string? Id, string? DisplayName, bool InCatalog, DiscoveryAction Action, string? RegisteredPath);

public enum RepoBatchOperation { Fetch, Pull }

/// <summary>
/// The one entry point for repository management, shared by
/// <c>fleetmate repos</c> and the app. It joins the provider catalog, the local
/// registry and git:
/// <code>
/// var manager = new RepoManager();
/// var records = manager.Records();                 // catalog ∪ registry
/// var repo = manager.Resolve("Project/Repo");
/// var copy = manager.WorkingCopy(repo);            // GitWorkingCopy
/// var status = await manager.StatusAsync(repo);
/// </code>
/// The catalog is read from the cache written by <see cref="RefreshCatalogAsync"/>,
/// so resolving a name needs no network.
/// </summary>
public sealed class RepoManager
{
    public RepoRegistryStore Store { get; }

    /// <summary>Moves a folder somewhere it can be restored from; the Recycle Bin unless a test supplies another.</summary>
    private readonly Action<string> _recycle;

    public RepoManager(RepoRegistryStore? store = null, Action<string>? recycle = null)
    {
        Store = store ?? new RepoRegistryStore();
        _recycle = recycle ?? RecycleBin.MoveToRecycleBin;
    }

    // ── Settings and registry ───────────────────────────────────────────

    public RepoSettings Settings() => Store.Load().Settings;

    public void UpdateSettings(Action<RepoSettings> change) => Store.Update(d => change(d.Settings));

    public RepoRegistryDocument Registry() => Store.Load();

    private RepoSettings SettingsOrDefault()
    {
        try { return Settings(); }
        catch (Exception) { return RepoSettings.Default; }
    }

    // ── Catalog ─────────────────────────────────────────────────────────

    public RepoCatalog? CachedCatalog() => Store.LoadCatalog();

    /// <summary>
    /// Fetches the catalog and caches it. Registered entries pick up the
    /// default branch the provider reports.
    /// </summary>
    public async Task<RepoCatalog> RefreshCatalogAsync(RepoCatalogService service)
    {
        var catalog = await service.FetchAsync();
        // Keep the previous cache when a fetch came back empty with errors: a
        // lapsed token should not wipe what resolution relies on.
        if (catalog.Repos.Count > 0 || catalog.Errors.Count == 0)
        {
            Store.SaveCatalog(catalog);
            var byId = catalog.Repos.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            Store.Update(doc =>
            {
                foreach (var (id, entry) in doc.Repos.ToList())
                {
                    if (byId.TryGetValue(id, out var repo) && repo.DefaultBranch is { } branch && entry.DefaultBranch != branch)
                        doc.Repos[id] = entry with { DefaultBranch = branch };
                }
            });
        }
        return catalog;
    }

    /// <summary>Catalog and registry merged, one record per repository.</summary>
    public List<RepoRecord> Records() =>
        RepoRecord.Merge(CachedCatalog()?.Repos ?? new(), Store.Load().Entries);

    public RepoRecord Resolve(string argument) => RepoResolver.Resolve(argument, Records());

    /// <summary>Resolves each argument, or every tracked repository when there are none.</summary>
    public List<RepoRecord> ResolveLocal(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return Records().Where(r => r.IsTracked).ToList();
        var records = Records();
        return arguments.Select(argument =>
        {
            var record = RepoResolver.Resolve(argument, records);
            return record.IsLocal ? record : throw RepoException.NotLocal(record.Key.DisplayName);
        }).ToList();
    }

    // ── Linking ─────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="rawPath"/> as the checkout of the repository
    /// its origin names. When <paramref name="argument"/> is given, the origin
    /// must match it.
    /// </summary>
    public async Task<RepoRegistryEntry> LinkAsync(string rawPath, string? argument = null, bool tracked = true)
    {
        var path = RepoSettings.Normalize(rawPath);
        var copy = new GitWorkingCopy(path);
        if (!Directory.Exists(path) || !await copy.IsRepositoryAsync()) throw RepoException.NotAGitRepository(path);
        var origin = await copy.OriginUrlAsync();
        var originKey = RepoRemoteUrl.Parse(origin);

        RepoKey key;
        string? defaultBranch;
        if (argument != null)
        {
            // The checkout's own origin is a candidate too, so a repository
            // missing from the catalog can still be linked by name.
            var candidates = Records();
            if (originKey != null && candidates.All(c => c.Id != originKey.Id))
                candidates.Add(new RepoRecord(originKey, null, null));
            var record = RepoResolver.Resolve(argument, candidates);
            if (originKey != null && originKey.Id != record.Id)
                throw RepoException.InvalidArgument($"{path} has origin {originKey.DisplayName}, not {record.Key.DisplayName}.");
            key = record.Key;
            defaultBranch = record.DefaultBranch;
        }
        else
        {
            key = originKey ?? throw RepoException.InvalidArgument(
                $"{path} has no origin remote; name the repository to link it to.");
            defaultBranch = CachedCatalog()?.Repos.FirstOrDefault(r => r.Id == originKey.Id)?.DefaultBranch;
        }

        var entry = new RepoRegistryEntry { Key = key, Path = path, Tracked = tracked, RemoteUrl = origin, DefaultBranch = defaultBranch };
        return Store.Update(doc =>
        {
            var updated = doc.Repos.TryGetValue(key.Id, out var existing)
                ? entry with { AddedAt = existing.AddedAt, Tracked = tracked || existing.Tracked }
                : entry;
            doc.Repos[key.Id] = updated;
            return updated;
        });
    }

    /// <summary>Forgets a local checkout. The folder is left untouched.</summary>
    public RepoRecord Unlink(string argument)
    {
        var record = Resolve(argument);
        Store.Update(doc => doc.Repos.Remove(record.Id));
        return record;
    }

    public RepoRecord SetTracked(string argument, bool tracked)
    {
        var record = Resolve(argument);
        if (!record.IsLocal) throw RepoException.NotLocal(record.Key.DisplayName);
        Store.Update(doc =>
        {
            if (doc.Repos.TryGetValue(record.Id, out var entry)) doc.Repos[record.Id] = entry with { Tracked = tracked };
        });
        return Resolve(record.Id);
    }

    public RepoRecord SetHidden(string argument, bool hidden)
    {
        var record = Resolve(argument);
        if (!record.IsLocal) throw RepoException.NotLocal(record.Key.DisplayName);
        Store.Update(doc =>
        {
            if (doc.Repos.TryGetValue(record.Id, out var entry)) doc.Repos[record.Id] = entry with { Hidden = hidden };
        });
        return Resolve(record.Id);
    }

    /// <summary>
    /// Scans the configured roots and registers every checkout whose origin
    /// names a repository. Existing links are kept; a second copy of an
    /// already-linked repository is reported as a duplicate, not re-linked.
    /// <paramref name="track"/> marks every checkout found as tracked, linked
    /// before or not; <paramref name="trackNew"/> tracks only checkouts this
    /// scan registers, leaving the person's earlier choice for the rest alone.
    /// </summary>
    public async Task<List<DiscoveryResult>> DiscoverAsync(bool track = false, RepoSettings? settings = null, bool trackNew = false)
    {
        settings ??= Settings();
        var found = await RepoDiscovery.DiscoverAsync(settings);
        return Register(found, track, trackNew);
    }

    /// <summary>
    /// Checkouts under <paramref name="roots"/> without registering anything, for
    /// a preview the person picks from (setup). Checkouts only a VS Code
    /// workspace file names are included, marked <see cref="DiscoveredCheckout.FromWorkspace"/>.
    /// </summary>
    public Task<List<DiscoveredCheckout>> PreviewAsync(IEnumerable<string> roots)
    {
        var settings = SettingsOrDefault();
        settings.ScanRoots = roots.ToList();
        return RepoDiscovery.DiscoverAsync(settings, includeWorkspaceFolders: true);
    }

    /// <summary>
    /// Registers checkouts found by <see cref="PreviewAsync"/> or <see cref="RepoDiscovery"/>,
    /// under the same rules as discover. A clone whose config can run commands
    /// is never tracked here unless <paramref name="trustCommandConfig"/> says
    /// the person ticked it knowing that; it is linked untracked and reported
    /// as <see cref="DiscoveryAction.NeedsReview"/>.
    /// </summary>
    public List<DiscoveryResult> Register(IEnumerable<DiscoveredCheckout> found, bool track = false, bool trackNew = false,
        bool trustCommandConfig = false)
    {
        var catalog = (CachedCatalog()?.Repos ?? new()).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());

        return Store.Update(doc =>
        {
            var results = new List<DiscoveryResult>();
            foreach (var checkout in found)
            {
                if (checkout.Key is not { } key)
                {
                    results.Add(new DiscoveryResult(checkout.Path, null, null, false, DiscoveryAction.NoRemote, null));
                    continue;
                }
                catalog.TryGetValue(key.Id, out var catalogEntry);
                var canonical = catalogEntry?.Key ?? key;
                var inCatalog = catalogEntry != null;
                var held = checkout.RunsCommands && !trustCommandConfig;
                if (doc.Repos.TryGetValue(key.Id, out var existing))
                {
                    if (string.Equals(existing.Path, checkout.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (track && !held) doc.Repos[key.Id] = existing with { Tracked = true };
                        results.Add(new DiscoveryResult(checkout.Path, key.Id, canonical.DisplayName, inCatalog, DiscoveryAction.AlreadyLinked, null));
                        continue;
                    }
                    if (Directory.Exists(existing.Path))
                    {
                        results.Add(new DiscoveryResult(checkout.Path, key.Id, canonical.DisplayName, inCatalog, DiscoveryAction.Duplicate, existing.Path));
                        continue;
                    }
                }
                var wantsTracking = track || (existing?.Tracked ?? trackNew);
                doc.Repos[key.Id] = new RepoRegistryEntry
                {
                    Key = canonical,
                    Path = checkout.Path,
                    Tracked = wantsTracking && !held,
                    RemoteUrl = checkout.RemoteUrl,
                    DefaultBranch = catalogEntry?.DefaultBranch,
                    Hidden = existing?.Hidden ?? false,
                };
                results.Add(new DiscoveryResult(checkout.Path, key.Id, canonical.DisplayName, inCatalog,
                    wantsTracking && held ? DiscoveryAction.NeedsReview : DiscoveryAction.Linked, null));
            }
            return results;
        });
    }

    /// <summary>
    /// Adds <paramref name="folders"/> to the scan roots, marks the roots as
    /// chosen, and tracks every clone found in them. What the Repos sidebar's
    /// Choose Folder does, with no further questions; clones whose config can
    /// run commands are left unregistered and returned for review instead.
    /// </summary>
    public async Task<TrackFoldersResult> TrackFoldersAsync(IEnumerable<string> folders)
    {
        var roots = folders.Select(RepoSettings.Abbreviate).ToList();
        AddScanRoots(roots);
        var settings = Settings();
        settings.ScanRoots = roots;
        var found = await RepoDiscovery.DiscoverAsync(settings);
        // Clones whose config can run commands are not registered: they come
        // back to be ticked one by one, unless already tracked.
        var known = Store.Load().Repos;
        var review = found.Where(c => c.RunsCommands && c.Key is { } key
            && !(known.TryGetValue(key.Id, out var entry) && entry.Tracked)).ToList();
        var reviewPaths = review.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TrackFoldersResult(Register(found.Where(c => !reviewPaths.Contains(c.Path)), track: true), review);
    }

    /// <summary>
    /// Adds roots (deduplicated, stored as <c>~\…</c>) and marks them as chosen.
    /// Until roots have been chosen the list holds only the default guess, which
    /// the first folders picked replace rather than join.
    /// </summary>
    public void AddScanRoots(IEnumerable<string> folders) => UpdateSettings(settings =>
    {
        if (!settings.RootsChosen) settings.ScanRoots = new();
        foreach (var root in folders.Select(RepoSettings.Abbreviate))
        {
            if (!settings.ScanRoots.Any(r => string.Equals(RepoSettings.Abbreviate(r), root, StringComparison.OrdinalIgnoreCase)))
                settings.ScanRoots.Add(root);
        }
        settings.RootsChosen = true;
    });

    /// <summary>
    /// Clones in the watched folders the registry does not know yet, when the
    /// person has chosen the folders and left "look for new clones" on. Nothing
    /// is registered: the Repos sidebar asks, and Add or Ignore registers them
    /// tracked or untracked. Null when looking is off.
    /// </summary>
    public async Task<List<DiscoveredCheckout>?> NewClonesAsync()
    {
        var settings = Settings();
        if (!settings.TracksNewClones || settings.ScanRoots.Count == 0) return null;
        var found = await RepoDiscovery.DiscoverAsync(settings);
        var known = Store.Load().Repos;
        return found
            .Where(c => c.Key is { } key && !known.ContainsKey(key.Id))
            .GroupBy(c => c.Key!.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    // ── Removing a clone ────────────────────────────────────────────────

    /// <summary>What deleting the checkout of <paramref name="argument"/> would lose.</summary>
    public Task<CloneRemovalCheck> RemovalCheckAsync(string argument)
    {
        var record = Resolve(argument);
        if (record.Local is not { } local) throw RepoException.NotLocal(record.Key.DisplayName);
        return CloneRemovalCheck.InspectAsync(local.Path);
    }

    /// <summary>
    /// Moves the checkout of <paramref name="argument"/> to the Recycle Bin and
    /// forgets it. Refuses when anything would be lost unless
    /// <paramref name="force"/> is set; the error lists exactly what. A checkout
    /// already gone from disk is simply forgotten.
    /// </summary>
    public async Task<CloneRemovalCheck> RemoveCloneAsync(string argument, bool force = false)
    {
        var record = Resolve(argument);
        if (record.Local is not { } local) throw RepoException.NotLocal(record.Key.DisplayName);
        var path = local.Path;
        if (!Directory.Exists(path))
        {
            Store.Update(doc => doc.Repos.Remove(record.Id));
            return new CloneRemovalCheck { Path = path };
        }
        RefuseUnsafeTarget(path, Settings());
        var check = await CloneRemovalCheck.InspectAsync(path);
        if (!check.IsSafe && !force) throw RepoException.WouldLoseWork(record.Key.DisplayName, check.LossDescription);
        _recycle(path);
        Store.Update(doc => doc.Repos.Remove(record.Id));
        return check;
    }

    /// <summary>Never delete the profile folder, a drive root, a scan root or the clone root, whatever the registry says.</summary>
    internal static void RefuseUnsafeTarget(string path, RepoSettings settings)
    {
        static string N(string p)
        {
            try { return RepoSettings.Normalize(p); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return p; }
        }
        var full = N(path);
        var home = N(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var root = Path.GetPathRoot(full) ?? "";
        var protectedPaths = settings.ScanRoots.Select(N).Append(N(settings.CloneRoot)).Append(home);
        if (full.Length <= root.Length || protectedPaths.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            throw RepoException.InvalidArgument($"Refusing to delete {path}: it is not a single clone.");
        if (!RepoDiscovery.IsCheckout(full)) throw RepoException.NotAGitRepository(path);
    }

    // ── Cloning ─────────────────────────────────────────────────────────

    /// <summary>
    /// Where <see cref="CloneAsync"/> puts a repository:
    /// <c>&lt;root&gt;\AzDevOps\&lt;Project&gt;\&lt;Repo&gt;</c> or <c>&lt;root&gt;\GitHub\&lt;owner&gt;\&lt;repo&gt;</c>.
    /// </summary>
    public static string DefaultClonePath(RepoKey key, string root)
    {
        var parts = new List<string> { RepoSettings.Expand(root), key.Provider.LayoutFolder() };
        switch (key.Provider)
        {
            case RepoProvider.AzureDevOps:
                parts.Add(SafeSegment(key.Project ?? key.Owner));
                parts.Add(SafeSegment(key.Name));
                break;
            case RepoProvider.GitHub:
                parts.Add(SafeSegment(key.Owner));
                parts.Add(SafeSegment(key.Name));
                break;
            default:
                parts.Add(SafeSegment(key.Owner.Trim('/').Replace('/', '_').Replace(':', '_')));
                parts.AddRange(key.Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment));
                break;
        }
        return Path.Combine(parts.ToArray());
    }

    /// <summary>A folder name with characters Windows refuses replaced, and never "." or "..".</summary>
    private static string SafeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 || safe == "." || safe == ".." ? "_" : safe;
    }

    /// <summary>
    /// Clones a catalog repository with the user's own git credentials and
    /// registers it as tracked. Never clones over an existing folder.
    /// </summary>
    public async Task<RepoRegistryEntry> CloneAsync(string argument, string? root = null, string? destination = null,
        bool useSsh = false, CancellationToken ct = default)
    {
        var record = Resolve(argument);
        var catalog = record.Catalog ?? throw RepoException.InvalidArgument(
            $"{record.Key.DisplayName} is not in the catalog; run 'fleetmate repos catalog' first.");
        if (record.Local is { } local && Directory.Exists(local.Path))
            throw RepoException.DestinationExists($"{record.Key.DisplayName} is already at {local.Path}");
        var target = destination != null
            ? RepoSettings.Normalize(destination)
            : DefaultClonePath(record.Key, root ?? Settings().CloneRoot);
        if (Directory.Exists(target) || File.Exists(target)) throw RepoException.DestinationExists(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var url = useSsh ? catalog.SshUrl ?? catalog.CloneUrl : catalog.CloneUrl;
        var result = await GitWorkingCopy.RunGitAsync(new[] { "clone", "--", url, target }, ct: ct);
        if (!result.Succeeded) throw RepoException.GitFailed("clone", result.Message);

        var entry = new RepoRegistryEntry
            { Key = record.Key, Path = target, Tracked = true, RemoteUrl = url, DefaultBranch = catalog.DefaultBranch };
        Store.Update(doc => doc.Repos[record.Id] = entry);
        return entry;
    }

    // ── Git ─────────────────────────────────────────────────────────────

    public GitWorkingCopy WorkingCopy(RepoRecord record) =>
        record.Local is { } local ? new GitWorkingCopy(local.Path) : throw RepoException.NotLocal(record.Key.DisplayName);

    /// <summary>
    /// Branches commit and push refuse for this repository: the configured
    /// list plus the repository's own default branch.
    /// </summary>
    public IReadOnlySet<string> ProtectedBranches(RepoRecord record)
    {
        var branches = new HashSet<string>(SettingsOrDefault().ProtectedBranches);
        if (record.DefaultBranch is { } branch) branches.Add(branch);
        return branches;
    }

    public async Task<RepoStatus> StatusAsync(RepoRecord record, CancellationToken ct = default)
    {
        var name = record.Key.DisplayName;
        if (record.Local is not { } local)
            return RepoStatus.From(record.Id, name, "", null, null, null, error: RepoException.NotLocal(name).Message);
        if (!Directory.Exists(local.Path))
            return RepoStatus.From(record.Id, name, local.Path, null, null, null, error: $"checkout missing at {local.Path}");
        var copy = new GitWorkingCopy(local.Path);
        try
        {
            var snapshot = await copy.StatusAsync(ct: ct);
            var worktrees = await copy.WorktreesAsync(ct);
            var lastCommit = await copy.LastCommitDateAsync(ct);
            return RepoStatus.From(record.Id, name, local.Path, snapshot, worktrees, copy.AgentsFile, lastCommit);
        }
        catch (RepoException ex)
        {
            return RepoStatus.From(record.Id, name, local.Path, null, null, copy.AgentsFile, error: ex.Message);
        }
    }

    /// <summary>Status of many repositories, a bounded number at a time.</summary>
    public Task<List<RepoStatus>> StatusAsync(IReadOnlyList<RepoRecord> records, CancellationToken ct = default) =>
        Bounded.MapAsync(records, SettingsOrDefault().Concurrency, r => StatusAsync(r, ct));

    /// <summary>
    /// Runs fetch or pull across repositories with bounded concurrency. One
    /// repository failing never stops the others.
    /// </summary>
    public Task<List<RepoOperationResult>> RunAsync(RepoBatchOperation operation, IReadOnlyList<RepoRecord> records, CancellationToken ct = default)
    {
        var verb = operation == RepoBatchOperation.Fetch ? "fetch" : "pull";
        return Bounded.MapAsync(records, SettingsOrDefault().Concurrency, async record =>
        {
            var name = record.Key.DisplayName;
            if (record.Local is not { } local)
                return new RepoOperationResult(record.Id, name, verb, false, "", RepoException.NotLocal(name).Message);
            var copy = new GitWorkingCopy(local.Path);
            try
            {
                var output = operation == RepoBatchOperation.Fetch ? await copy.FetchAsync(ct) : await copy.PullAsync(ct);
                return new RepoOperationResult(record.Id, name, verb, true, output.Trim(), null);
            }
            catch (RepoException ex)
            {
                return new RepoOperationResult(record.Id, name, verb, false, "", ex.Message);
            }
        });
    }
}
