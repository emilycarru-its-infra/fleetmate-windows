namespace FleetMate.Core.Services.Repos;

/// <summary>
/// A git checkout found on disk. <see cref="FromWorkspace"/> marks one found
/// only because a VS Code workspace file lists it: a proposal for the person to
/// accept, never registered or tracked without asking.
/// </summary>
public sealed record DiscoveredCheckout(string Path, string? RemoteUrl, RepoKey? Key, bool FromWorkspace = false)
{
    /// <summary>
    /// Keys in the clone's own config that make git run a program
    /// (<c>core.fsmonitor</c>, <c>filter.lfs.process</c>, …). Such a clone is
    /// flagged and starts unselected wherever it is offered.
    /// </summary>
    public List<string> CommandConfig { get; init; } = new();

    /// <summary>The clone's settings can run commands.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsCommands => CommandConfig.Count > 0;
}

/// <summary>
/// Walks scan roots for git checkouts.
///
/// A folder is a checkout when it holds <c>.git</c> (a directory, or a file
/// for a submodule). The walk stops descending at a checkout, skips hidden
/// folders, links and junctions, the built-in skip list (<c>node_modules</c>,
/// <c>bin</c>, <c>obj</c>, …) and the configured one, and never goes deeper than
/// the configured depth below a root. A skipped name is still reported when the
/// folder is itself a checkout, so a repository called <c>build</c> is found;
/// its contents are never walked. Linked worktrees are not reported: they belong
/// to the checkout they were added from.
///
/// VS Code multi-root workspaces (<c>*.code-workspace</c>) sitting directly in a
/// scan root or the profile folder contribute the checkouts their
/// <c>folders</c> name, but only folders that resolve inside a scan root, and
/// only as proposals (<see cref="ScanResult.WorkspaceCheckouts"/>).
///
/// Nothing here runs git: a clone's origin is read from its config file, so a
/// scan never executes anything a clone's own configuration names.
/// </summary>
public static class RepoDiscovery
{
    /// <summary>
    /// Folder names never descended into, whatever the settings say: build
    /// output, package caches and worktree folders hold no checkouts of their
    /// own, and walking them is what makes a scan slow.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInSkip = new[]
    {
        ".worktrees", "node_modules", ".build", "build", "bin", "obj", "dist", "target",
        "DerivedData", "Pods", "Carthage", "vendor", "packages", "__pycache__", "venv",
        ".vs", "TestResults", "AppPackages",
    };

    /// <summary>
    /// Where people commonly keep clones, relative to the profile folder. On a
    /// first run these are looked in and whatever they hold is proposed.
    /// </summary>
    public static readonly IReadOnlyList<string> CommonLocations = new[]
    {
        @"~\Developer", @"~\DevOps", @"~\Projects", @"~\src", @"~\code", @"~\git",
        @"~\repos", @"~\GitHub", @"~\Documents\GitHub", @"~\source\repos",
    };

    /// <summary>The common locations that exist on this machine, as stored (<c>~\…</c>).</summary>
    public static List<string> ExistingCommonLocations() =>
        CommonLocations.Where(l => Directory.Exists(RepoSettings.Expand(l))).ToList();

    /// <summary>
    /// What one scan found: checkouts the walk reached, in walk order; checkouts
    /// only a workspace file names, not reached by the walk; and the workspace
    /// files read.
    /// </summary>
    public sealed record ScanResult(List<string> Checkouts, List<string> WorkspaceCheckouts, List<string> WorkspaceFiles);

    /// <summary>Paths of checkouts the walk reaches under <paramref name="roots"/>, found without spawning a process.</summary>
    public static List<string> FindCheckouts(IEnumerable<string> roots, int maxDepth, IEnumerable<string> skip) =>
        Scan(roots, maxDepth, skip).Checkouts;

    /// <summary>Walks <paramref name="roots"/> for checkouts and VS Code workspace files.</summary>
    public static ScanResult Scan(IEnumerable<string> roots, int maxDepth, IEnumerable<string> skip)
    {
        var skipSet = new HashSet<string>(skip.Concat(BuiltInSkip), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaceFiles = new List<string>();
        string home;
        try { home = RepoSettings.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); }
        catch (Exception) { home = ""; }

        void Note(string path)
        {
            string key;
            try { key = RepoSettings.Normalize(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { key = path; }
            if (seen.Add(key)) found.Add(path);
        }

        void Walk(string directory, int depth)
        {
            if (IsCheckout(directory))
            {
                Note(directory);
                return;
            }
            if (depth >= maxDepth) return;
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(directory).EnumerateDirectories()
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }
            foreach (var child in children)
            {
                if (child.Name.StartsWith('.')) continue;
                // Links and junctions lead to loops and duplicates; hidden and
                // system folders are not where people keep checkouts.
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || child.Attributes.HasFlag(FileAttributes.Hidden)
                    || child.Attributes.HasFlag(FileAttributes.System)) continue;
                var underHome = home.Length > 0 && string.Equals(directory.TrimEnd('\\', '/'), home, StringComparison.OrdinalIgnoreCase);
                if (skipSet.Contains(child.Name) || (underHome && (child.Name is "AppData" or "Library")))
                {
                    if (IsCheckout(child.FullName)) Note(child.FullName);
                    continue;
                }
                Walk(child.FullName, depth + 1);
            }
        }

        var walkedRoots = new List<string>();
        foreach (var root in roots)
        {
            string expanded;
            try { expanded = RepoSettings.Normalize(root); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (IsUncOrDevicePath(expanded) || !Directory.Exists(expanded)) continue;
            walkedRoots.Add(expanded);
            Walk(expanded, 0);
        }

        // Workspace files are read only at the top of a scan root or the
        // profile folder, and a folder they list counts only when it really
        // lies inside a scan root.
        var resolvedRoots = walkedRoots.Select(ResolveLinks).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var walked = new HashSet<string>(found.Select(p => ResolveLinks(p) ?? p), StringComparer.OrdinalIgnoreCase);
        var proposals = new List<string>();
        var proposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var places = home.Length > 0 ? walkedRoots.Append(home) : walkedRoots;
        foreach (var place in places.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                workspaceFiles.AddRange(Directory.EnumerateFiles(place, "*.code-workspace", SearchOption.TopDirectoryOnly)
                    .Where(f => !workspaceFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        foreach (var file in workspaceFiles)
        {
            foreach (var folder in WorkspaceFolders(file))
            {
                if (ResolveLinks(folder) is not { } resolved || !IsInside(resolved, resolvedRoots)) continue;
                if (EnclosingCheckout(resolved) is not { } checkout || !IsInside(checkout, resolvedRoots)) continue;
                if (walked.Contains(checkout) || !proposed.Add(checkout)) continue;
                proposals.Add(checkout);
            }
        }

        return new ScanResult(found, proposals, workspaceFiles);
    }

    /// <summary>UNC shares (<c>\\server\share</c>, <c>//server</c>) and <c>\\?\</c> or <c>\\.\</c> device paths.</summary>
    internal static bool IsUncOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
        || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="path"/> is one of <paramref name="roots"/> or below one, on a separator boundary.</summary>
    internal static bool IsInside(string path, IEnumerable<string> roots)
    {
        var sep = Path.DirectorySeparatorChar;
        return roots.Any(root =>
        {
            var prefix = root.EndsWith(sep) ? root : root + sep;
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The full path with every symbolic link and junction along it followed, or
    /// null for a UNC or device path or one that cannot be resolved.
    /// </summary>
    internal static string? ResolveLinks(string path)
    {
        try
        {
            if (IsUncOrDevicePath(path)) return null;
            var full = Path.GetFullPath(path);
            if (IsUncOrDevicePath(full)) return null;
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null) return null;
                    current = Path.GetFullPath(target.FullName);
                    if (IsUncOrDevicePath(current)) return null;
                }
            }
            return current.Length > (Path.GetPathRoot(current) ?? "").Length ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// True for a main checkout or submodule; false for a linked worktree,
    /// whose <c>.git</c> file points into another repository's <c>worktrees</c> folder.
    /// </summary>
    internal static bool IsCheckout(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        if (Directory.Exists(dotGit)) return true;
        if (!File.Exists(dotGit)) return false;
        try
        {
            var pointer = File.ReadAllText(dotGit).Replace('\\', '/');
            return !pointer.Contains("/worktrees/");
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The checkout <paramref name="path"/> is in: itself, or the nearest parent
    /// that is one (a workspace often opens a subfolder of a repository).
    /// </summary>
    internal static string? EnclosingCheckout(string path)
    {
        DirectoryInfo? current;
        try { current = new DirectoryInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
        {
            if (current.Exists && IsCheckout(current.FullName)) return current.FullName.TrimEnd('\\', '/');
        }
        return null;
    }

    /// <summary>
    /// Absolute paths of the folders a <c>.code-workspace</c> file lists. Paths
    /// are relative to the file unless absolute; the file is JSON with comments
    /// and trailing commas allowed, as VS Code writes it.
    /// </summary>
    public static List<string> WorkspaceFolders(string file)
    {
        try { return WorkspaceFolders(File.ReadAllText(file), Path.GetDirectoryName(Path.GetFullPath(file)) ?? ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    internal static List<string> WorkspaceFolders(string json, string relativeTo)
    {
        var result = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!document.RootElement.TryGetProperty("folders", out var folders)
                || folders.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var folder in folders.EnumerateArray())
            {
                if (folder.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !folder.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var raw = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // A workspace file is someone else's text: no ~ or %VARIABLE%
                // expansion, and never a network share or device path.
                if (IsUncOrDevicePath(raw)) continue;
                try
                {
                    var local = raw.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var full = Path.GetFullPath(Path.IsPathRooted(local) ? local : Path.Combine(relativeTo, local));
                    if (!IsUncOrDevicePath(full)) result.Add(full);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        catch (System.Text.Json.JsonException) { }
        return result;
    }

    /// <summary>
    /// Finds checkouts and reads each one's origin from its config file, with no
    /// git process. Workspace-file proposals are included, marked, only when
    /// <paramref name="includeWorkspaceFolders"/> is set.
    /// </summary>
    public static Task<List<DiscoveredCheckout>> DiscoverAsync(RepoSettings settings, CancellationToken ct = default) =>
        DiscoverAsync(settings, includeWorkspaceFolders: false, ct);

    public static Task<List<DiscoveredCheckout>> DiscoverAsync(RepoSettings settings, bool includeWorkspaceFolders, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scan = Scan(settings.ScanRoots, settings.ScanDepth, settings.SkipDirectories);
            var found = scan.Checkouts.Select(path => Checkout(path, fromWorkspace: false));
            if (includeWorkspaceFolders) found = found.Concat(scan.WorkspaceCheckouts.Select(path => Checkout(path, fromWorkspace: true)));
            return found.ToList();
        }, ct);

    private static DiscoveredCheckout Checkout(string path, bool fromWorkspace)
    {
        var config = ReadConfig(path);
        var origin = config != null ? OriginFromConfig(config) : null;
        return new DiscoveredCheckout(path, origin, RepoRemoteUrl.Parse(origin), fromWorkspace)
        {
            CommandConfig = CommandConfig(path),
        };
    }

    /// <summary>
    /// The <c>origin</c> remote's URL, read from the checkout's git config
    /// (following a <c>gitdir:</c> file for a submodule), or null.
    /// </summary>
    public static string? ReadOrigin(string checkout) =>
        ReadConfig(checkout) is { } config ? OriginFromConfig(config) : null;

    /// <summary>The checkout's git directory: <c>.git</c>, or where a <c>gitdir:</c> file points.</summary>
    internal static string? GitDirectory(string checkout)
    {
        try
        {
            var dotGit = Path.Combine(checkout, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;
            var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
            if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            var target = line["gitdir:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (target.Length == 0 || IsUncOrDevicePath(target)) return null;
            return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(checkout, target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The clone's <c>config</c> and <c>config.worktree</c> as text, or null when neither can be read.</summary>
    public static string? ReadConfig(string checkout)
    {
        if (GitDirectory(checkout) is not { } gitDir) return null;
        var texts = new List<string>();
        foreach (var name in new[] { "config", "config.worktree" })
        {
            try
            {
                var file = Path.Combine(gitDir, name);
                if (File.Exists(file)) texts.Add(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    /// <summary>
    /// Section → keys that hold data only. <c>*</c> allows every key in the
    /// section. Keys are lowercased; whether a subsection is required is in
    /// <see cref="IsDataOnlyKey"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, HashSet<string>> DataOnlyKeys = new Dictionary<string, HashSet<string>>
    {
        ["core"] = new() { "repositoryformatversion", "filemode", "bare", "logallrefupdates", "ignorecase", "precomposeunicode",
                           "symlinks", "autocrlf", "eol", "safecrlf", "longpaths", "quotepath", "checkstat", "trustctime",
                           "sparsecheckout", "sparsecheckoutcone", "untrackedcache", "splitindex" },
        ["remote"] = new() { "url", "fetch", "pushurl", "push", "tagopt", "prune", "prunetags", "mirror", "skipdefaultupdate" },
        ["branch"] = new() { "remote", "merge", "rebase", "pushremote", "description" },
        ["user"] = new() { "*" }, ["pull"] = new() { "*" }, ["color"] = new() { "*" },
        ["column"] = new() { "*" }, ["advice"] = new() { "*" }, ["i18n"] = new() { "*" },
        ["fetch"] = new() { "prune", "prunetags", "writecommitgraph", "parallel" },
        ["init"] = new() { "defaultbranch" },
        ["extensions"] = new() { "objectformat", "refstorage" },
        ["submodule"] = new() { "url", "active", "branch", "fetchrecursesubmodules", "ignore", "shallow" },
        ["push"] = new() { "default", "autosetupremote", "followtags" },
        ["merge"] = new() { "ff", "conflictstyle" },
        ["rebase"] = new() { "autostash", "autosquash", "updaterefs" },
        ["gc"] = new() { "auto", "autodetach" },
    };

    /// <summary>Sections whose keys need a subsection; every other section without <c>*</c> must not have one.</summary>
    private static readonly HashSet<string> SubsectionSections = new() { "remote", "branch", "submodule" };

    /// <summary>
    /// Whether a config key is known to hold only data. Everything else is
    /// flagged: an allowlist, so a key git learns to run tomorrow, or one this
    /// list never heard of, counts as able to run commands.
    /// </summary>
    internal static bool IsDataOnlyKey(string section, string? subsection, string key, string value)
    {
        switch (section)
        {
            case "core" when subsection == null && key == "fsmonitor":
                return value.ToLowerInvariant() is "false" or "no" or "off" or "0";
            case "lfs" when subsection == null && key == "repositoryformatversion":
                return true;
            case "lfs" when subsection != null && key is "access" or "locksverify":
                return true;
            case "submodule" when subsection == null && key == "active":
                return true;
            case "submodule" when subsection != null && key == "update":
                return value is "checkout" or "rebase" or "merge" or "none";
            case "branch" when subsection != null
                && (key.StartsWith("vscode-", StringComparison.Ordinal) || key.StartsWith("github-pr-", StringComparison.Ordinal)):
                return true;
        }
        if (!DataOnlyKeys.TryGetValue(section, out var keys)) return false;
        var any = keys.Contains("*");
        if (SubsectionSections.Contains(section) != (subsection != null) && !any) return false;
        return any || keys.Contains(key);
    }

    private static readonly System.Text.RegularExpressions.Regex HeaderPattern =
        new(@"^\[([A-Za-z0-9.-]+)(?: ""([^""\x5C]*)"")?\]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static readonly System.Text.RegularExpressions.Regex EntryPattern =
        new(@"^[ \t]*([A-Za-z][A-Za-z0-9-]*)[ \t]*=[ \t]*([^""\x5C;#]*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex BareKeyPattern =
        new(@"^[ \t]*([A-Za-z][A-Za-z0-9-]*)[ \t]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Why a clone's config needs review, read strictly, or empty when it is
    /// plain data. Fails closed: anything outside the plain form is flagged
    /// rather than interpreted the way git might.
    /// <list type="bullet">
    /// <item>Bytes: 0x20–0x7E, tab and LF only; CR only before LF or at the end. Anything else (non-ASCII, invalid UTF-8, NUL, control characters) flags the whole file at once.</item>
    /// <item>Lines split on LF alone; one trailing CR dropped. Blank and comment lines skipped.</item>
    /// <item>Headers <c>[section]</c>, <c>[section "sub"]</c> (no quote or backslash inside) or legacy <c>[section.sub]</c>.</item>
    /// <item>Entries <c>key = value</c> with no quote, backslash, <c>;</c> or <c>#</c> in the value, or a bare boolean key.</item>
    /// <item>Keys on <see cref="DataOnlyKeys"/>; <c>submodule.&lt;x&gt;.update</c> only as checkout, rebase, merge or none; <c>core.fsmonitor</c> only as false.</item>
    /// </list>
    /// </summary>
    public static List<string> ConfigReview(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b is >= 0x20 and <= 0x7E or 0x0A or 0x09) continue;
            if (b == 0x0D && (i == bytes.Length - 1 || bytes[i + 1] == 0x0A)) continue;
            return new List<string> { "(characters outside plain ASCII)" };
        }

        var found = new List<string>();
        void Flag(string reason) { if (!found.Contains(reason)) found.Add(reason); }
        var section = "";
        string? subsection = null;
        var start = 0;
        while (start <= bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)0x0A, start);
            if (end < 0) end = bytes.Length;
            var length = end - start;
            if (length > 0 && bytes[end - 1] == 0x0D) length--;
            var line = System.Text.Encoding.ASCII.GetString(bytes, start, length);
            start = end + 1;

            // A trailing backslash continues the line in git's reading; never
            // let one through, whatever the patterns below would say.
            if (line.EndsWith('\\')) { Flag("(continuation line)"); continue; }
            var trimmed = line.Trim(' ', '\t');
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;

            var header = HeaderPattern.Match(trimmed);
            if (header.Success)
            {
                var name = header.Groups[1].Value;
                var dot = name.IndexOf('.');
                if (header.Groups[2].Success)
                {
                    section = name.ToLowerInvariant();
                    subsection = header.Groups[2].Value;
                }
                else if (dot >= 0)
                {
                    section = name[..dot].ToLowerInvariant();
                    subsection = name[(dot + 1)..].ToLowerInvariant();
                }
                else
                {
                    section = name.ToLowerInvariant();
                    subsection = null;
                }
                continue;
            }

            string key, value;
            var entry = EntryPattern.Match(line);
            if (entry.Success)
            {
                key = entry.Groups[1].Value.ToLowerInvariant();
                value = entry.Groups[2].Value.Trim(' ', '\t');
            }
            else if (BareKeyPattern.Match(line) is { Success: true } bare)
            {
                key = bare.Groups[1].Value.ToLowerInvariant();
                value = "true";
            }
            else
            {
                Flag("(line not in the plain form)");
                continue;
            }
            if (section.Length == 0) { Flag("(key outside a section)"); continue; }
            if (!IsDataOnlyKey(section, subsection, key, value))
                Flag(subsection != null ? $"{section}.{subsection}.{key}" : $"{section}.{key}");
        }
        return found;
    }

    /// <summary><see cref="ConfigReview"/> for text, as tests and callers holding a string use it.</summary>
    public static List<string> CommandConfigKeys(string text) => ConfigReview(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Why the clone's own files could make git run something: config keys not
    /// known to be data-only, hooks, a <c>commondir</c> pointing elsewhere, and
    /// anything that could not be read. Fails closed: what cannot be read is
    /// itself a reason. Empty for a clone with nothing to review.
    /// </summary>
    public static List<string> CommandConfig(string checkout)
    {
        var reasons = new List<string>();
        void Add(string reason) { if (!reasons.Contains(reason)) reasons.Add(reason); }
        if (GitDirectory(checkout) is not { } gitDir)
        {
            var dotGit = Path.Combine(checkout, ".git");
            if (File.Exists(dotGit) || Directory.Exists(dotGit)) Add("(unreadable .git)");
            return reasons;
        }
        var gitDirs = new List<string> { gitDir };
        var commondir = Path.Combine(gitDir, "commondir");
        if (File.Exists(commondir))
        {
            Add("commondir");
            try
            {
                var target = File.ReadAllText(commondir).Trim().Replace('/', Path.DirectorySeparatorChar);
                if (target.Length > 0 && !IsUncOrDevicePath(target))
                    gitDirs.Add(Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        foreach (var dir in gitDirs)
        {
            foreach (var name in new[] { "config", "config.worktree" })
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Add($"{name} (unreadable)");
                    continue;
                }
                foreach (var key in ConfigReview(bytes)) Add(key);
            }
            // Windows has no execute bit and Git for Windows runs any hook
            // file through sh, so every non-sample file counts.
            try
            {
                var hooks = Path.Combine(dir, "hooks");
                if (Directory.Exists(hooks))
                    foreach (var hook in Directory.EnumerateFiles(hooks).Select(Path.GetFileName).OfType<string>()
                                 .Where(h => !h.EndsWith(".sample", StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(h => h, StringComparer.Ordinal))
                        Add($"hooks/{hook}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Add("hooks (unreadable)");
            }
        }
        return reasons;
    }

    /// <summary>
    /// A git config value as git reads it: quotes removed, <c>\\</c>, <c>\"</c>,
    /// <c>\n</c>, <c>\t</c> and <c>\b</c> unescaped, and a <c>#</c> or <c>;</c>
    /// comment outside quotes dropped. Git writes a Windows path as <c>C:\\Users</c>.
    /// </summary>
    internal static string ConfigValue(string raw)
    {
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[++i];
                value.Append(next switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => next });
                continue;
            }
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '#' or ';') break;
            value.Append(c);
        }
        return value.ToString().Trim();
    }

    /// <summary>The <c>url</c> of <c>[remote "origin"]</c> in a git config file's text.</summary>
    internal static string? OriginFromConfig(string text)
    {
        var inOrigin = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var header = close > 0 ? line[1..close].Trim() : "";
                inOrigin = (header.StartsWith("remote", StringComparison.OrdinalIgnoreCase) && header[6..].Trim() == "\"origin\"")
                    || header.Equals("remote.origin", StringComparison.OrdinalIgnoreCase);
                var rest = line[(close + 1)..].Trim();
                if (!inOrigin || rest.Length == 0) continue;
                line = rest;
            }
            if (!inOrigin) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            var value = ConfigValue(line[(equals + 1)..]);
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}

/// <summary>Bounded concurrency for batch git work.</summary>
public static class Bounded
{
    /// <summary>
    /// Maps <paramref name="items"/> through <paramref name="transform"/> with at
    /// most <paramref name="limit"/> running at once, preserving order.
    /// </summary>
    public static async Task<List<TResult>> MapAsync<T, TResult>(IReadOnlyList<T> items, int limit, Func<T, Task<TResult>> transform)
    {
        if (items.Count == 0) return new();
        using var gate = new SemaphoreSlim(Math.Max(1, limit));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try { return await transform(item); }
            finally { gate.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }
}
