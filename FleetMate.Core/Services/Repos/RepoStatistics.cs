using System.Globalization;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Services.Repos;

// ── Raw history ─────────────────────────────────────────────────────────

/// <summary>One file a commit touched, from <c>git log --numstat</c>. Binary files report no line counts.</summary>
public sealed record RepoFileChurn(string Path, int Added, int Removed, bool IsBinary = false);

/// <summary>One commit with the lines it changed per file.</summary>
public sealed record RepoChurnCommit(string Sha, string Author, string Email, DateTimeOffset Date, IReadOnlyList<RepoFileChurn> Files)
{
    public int Added => Files.Sum(f => f.Added);
    public int Removed => Files.Sum(f => f.Removed);
}

public static class GitChurnLog
{
    /// <summary>
    /// Format for <c>git log --numstat</c>: a record separator, then sha,
    /// author (mailmap applied), email and Unix time separated by unit
    /// separators. The numstat lines follow each header.
    /// </summary>
    public const string Format = "%x1e%H%x1f%aN%x1f%aE%x1f%at";

    /// <summary>
    /// Parses <c>git log --numstat --no-renames --format=&lt;Format&gt;</c>. A
    /// numstat line is <c>added&lt;TAB&gt;removed&lt;TAB&gt;path</c>; a binary
    /// file has <c>-</c> for both counts. Malformed records are skipped, never guessed at.
    /// </summary>
    public static List<RepoChurnCommit> Parse(string output)
    {
        var commits = new List<RepoChurnCommit>();
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            var fields = lines[0].Split('\u001f');
            if (fields.Length != 4 || fields[0].Length == 0 || !long.TryParse(fields[3].Trim(), out var seconds)) continue;
            var files = new List<RepoFileChurn>();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length != 3 || parts[2].Length == 0) continue;
                if (parts[0] == "-" && parts[1] == "-")
                    files.Add(new RepoFileChurn(parts[2], 0, 0, true));
                else if (int.TryParse(parts[0], out var added) && int.TryParse(parts[1], out var removed))
                    files.Add(new RepoFileChurn(parts[2], added, removed));
            }
            commits.Add(new RepoChurnCommit(fields[0], fields[1], fields[2], DateTimeOffset.FromUnixTimeSeconds(seconds), files));
        }
        return commits;
    }

    /// <summary>
    /// Non-merge commits reachable from <paramref name="reference"/> (default
    /// HEAD) since <paramref name="since"/>, with per-file line counts. Renames
    /// are reported as a delete and an add so every path is a plain path.
    /// Read-only: no locks, no pathspecs.
    /// </summary>
    public static async Task<List<RepoChurnCommit>> ChurnLogAsync(this GitWorkingCopy copy, DateTimeOffset? since = null,
        DateTimeOffset? until = null, string? reference = null, int limit = 20_000, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "log", "--no-merges", "--numstat", "--no-renames", "--no-color",
            $"--format={Format}", "-n", Math.Max(1, limit).ToString(CultureInfo.InvariantCulture),
        };
        if (since is { } s) args.Add($"--since=@{s.ToUnixTimeSeconds()}");
        if (until is { } u) args.Add($"--until=@{u.ToUnixTimeSeconds()}");
        if (reference != null)
        {
            if (reference.StartsWith('-')) throw RepoException.InvalidArgument($"'{reference}' is not a valid ref.");
            args.Add(reference);
            args.Add("--");
        }
        var result = await copy.GitAsync(args, new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" }, ct);
        if (!result.Succeeded && result.StandardError.Contains("does not have any commits")) return new();
        if (!result.Succeeded) throw RepoException.GitFailed("log", result.Message);
        return Parse(result.StandardOutput);
    }
}

// ── Range ───────────────────────────────────────────────────────────────

/// <summary>How finely a timeline is divided.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RepoStatsBucket>))]
public enum RepoStatsBucket
{
    [JsonStringEnumMemberName("day")] Day,
    [JsonStringEnumMemberName("week")] Week,
    [JsonStringEnumMemberName("month")] Month,
}

public static class RepoStatsRange
{
    /// <summary>A bucket that gives a readable number of bars for a span of <paramref name="days"/>.</summary>
    public static RepoStatsBucket AutomaticBucket(int days) => days switch
    {
        < 45 => RepoStatsBucket.Day,
        < 400 => RepoStatsBucket.Week,
        _ => RepoStatsBucket.Month,
    };

    public static RepoStatsBucket? ParseBucket(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "day" or "d" => RepoStatsBucket.Day,
        "week" or "w" => RepoStatsBucket.Week,
        "month" or "m" => RepoStatsBucket.Month,
        _ => null,
    };

    /// <summary>
    /// Parses a <c>--since</c> value: <c>30d</c>, <c>12w</c>, <c>6m</c>,
    /// <c>1y</c>, or an ISO date (<c>2026-01-31</c>). Relative values count
    /// back from <paramref name="now"/> to the start of that day.
    /// </summary>
    public static DateTimeOffset? ParseSince(string value, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var text = value.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        var local = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.Now, zone).DateTime;
        if (text.Length >= 2 && int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            DateTime? start = text[^1] switch
            {
                'd' => local.AddDays(-count),
                'w' => local.AddDays(-7 * count),
                'm' => local.AddMonths(-count),
                'y' => local.AddYears(-count),
                _ => null,
            };
            if (start is { } s) return AtMidnight(s.Date, zone);
        }
        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? AtMidnight(date, zone)
            : null;
    }

    internal static DateTimeOffset AtMidnight(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

// ── Report ──────────────────────────────────────────────────────────────

/// <summary>
/// Statistics for one repository over a period: what agents read through
/// <c>fleetmate repos stats --json</c> and what the Insights view charts.
/// </summary>
public sealed class RepoStatsReport
{
    public sealed record Totals
    {
        public int Commits { get; init; }
        public int Authors { get; init; }
        public int Added { get; init; }
        public int Removed { get; init; }
        public int FilesTouched { get; init; }
        public DateTimeOffset? FirstCommit { get; init; }
        public DateTimeOffset? LastCommit { get; init; }
    }

    /// <summary>One bucket of the timeline; <c>Start</c> is the start of the bucket.</summary>
    public sealed record TimelinePoint(DateTimeOffset Start, int Commits, int Added, int Removed);

    public sealed record Contributor(string Name, int Commits, int Added, int Removed, DateTimeOffset? LastCommit);

    public sealed record PathActivity(string Path, int Commits, int Added, int Removed)
    {
        public int Churn => Added + Removed;
    }

    /// <summary>Commits by weekday (1 = Sunday … 7 = Saturday) and hour.</summary>
    public sealed record ActivityCell(int Weekday, int Hour, int Commits);

    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset Until { get; init; }
    public RepoStatsBucket Bucket { get; init; }
    [JsonPropertyName("totals")]
    public Totals Total { get; init; } = new();
    public List<TimelinePoint> Timeline { get; init; } = new();
    public List<Contributor> Contributors { get; init; } = new();
    public List<PathActivity> Files { get; init; } = new();
    /// <summary>Top-level folders (or root files) by activity.</summary>
    public List<PathActivity> Areas { get; init; } = new();
    public List<ActivityCell> Activity { get; init; } = new();
}

public static class RepoStatistics
{
    /// <summary>
    /// Aggregates <paramref name="commits"/> into a report. Commits outside
    /// <c>since…until</c> are ignored. The timeline has one point per bucket
    /// from the first bucket (of <paramref name="since"/>, or the oldest
    /// commit) to the bucket of <paramref name="until"/>, zero-filled so a
    /// quiet week shows as a gap, not a missing bar.
    /// </summary>
    public static RepoStatsReport Report(
        IEnumerable<RepoChurnCommit> commits,
        DateTimeOffset? since,
        DateTimeOffset? until = null,
        RepoStatsBucket? bucket = null,
        int top = 15,
        TimeZoneInfo? zone = null,
        DayOfWeek? firstDayOfWeek = null)
    {
        zone ??= TimeZoneInfo.Local;
        var firstDay = firstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var end = until ?? DateTimeOffset.Now;
        var kept = commits.Where(c => c.Date <= end && (since is not { } s || c.Date >= s)).ToList();
        var start = since ?? (kept.Count > 0 ? kept.Min(c => c.Date) : end);
        var days = Math.Max(1, (int)(end - start).TotalDays);
        var size = bucket ?? RepoStatsRange.AutomaticBucket(days);

        int commitCount = 0, added = 0, removed = 0;
        DateTimeOffset? first = null, last = null;
        var byBucket = new Dictionary<DateTimeOffset, (int Commits, int Added, int Removed)>();
        var byAuthor = new Dictionary<string, (int Commits, int Added, int Removed, DateTimeOffset Last)>();
        var byFile = new Dictionary<string, (int Commits, int Added, int Removed)>();
        var byArea = new Dictionary<string, (int Commits, int Added, int Removed)>();
        var byCell = new Dictionary<(int, int), int>();

        foreach (var commit in kept)
        {
            var a = commit.Added;
            var r = commit.Removed;
            commitCount++;
            added += a;
            removed += r;
            first = first is { } f && f < commit.Date ? f : commit.Date;
            last = last is { } l && l > commit.Date ? l : commit.Date;

            var key = BucketStart(commit.Date, size, zone, firstDay);
            var point = byBucket.GetValueOrDefault(key);
            byBucket[key] = (point.Commits + 1, point.Added + a, point.Removed + r);

            var name = commit.Author.Length == 0 ? commit.Email : commit.Author;
            if (byAuthor.TryGetValue(name, out var person))
                byAuthor[name] = (person.Commits + 1, person.Added + a, person.Removed + r, person.Last > commit.Date ? person.Last : commit.Date);
            else
                byAuthor[name] = (1, a, r, commit.Date);

            var areasThisCommit = new HashSet<string>();
            foreach (var file in commit.Files)
            {
                var entry = byFile.GetValueOrDefault(file.Path);
                byFile[file.Path] = (entry.Commits + 1, entry.Added + file.Added, entry.Removed + file.Removed);

                var area = Area(file.Path);
                var areaEntry = byArea.GetValueOrDefault(area);
                byArea[area] = (areaEntry.Commits + (areasThisCommit.Add(area) ? 1 : 0),
                    areaEntry.Added + file.Added, areaEntry.Removed + file.Removed);
            }

            var local = TimeZoneInfo.ConvertTime(commit.Date, zone);
            var cell = ((int)local.DayOfWeek + 1, local.Hour);
            byCell[cell] = byCell.GetValueOrDefault(cell) + 1;
        }

        var timeline = new List<RepoStatsReport.TimelinePoint>();
        var cursor = BucketStart(start, size, zone, firstDay);
        var lastBucket = BucketStart(end, size, zone, firstDay);
        // A guard against a pathological range: ten years of days.
        while (cursor <= lastBucket && timeline.Count < 3700)
        {
            var p = byBucket.GetValueOrDefault(cursor);
            timeline.Add(new RepoStatsReport.TimelinePoint(cursor, p.Commits, p.Added, p.Removed));
            cursor = NextBucket(cursor, size, zone);
        }

        static IEnumerable<RepoStatsReport.PathActivity> Ranked(IEnumerable<RepoStatsReport.PathActivity> items) =>
            items.OrderByDescending(p => p.Commits).ThenByDescending(p => p.Churn).ThenBy(p => p.Path, StringComparer.Ordinal);

        return new RepoStatsReport
        {
            Since = since,
            Until = end,
            Bucket = size,
            Total = new RepoStatsReport.Totals
            {
                Commits = commitCount, Authors = byAuthor.Count, Added = added, Removed = removed,
                FilesTouched = byFile.Count, FirstCommit = first, LastCommit = last,
            },
            Timeline = timeline,
            Contributors = byAuthor
                .Select(kv => new RepoStatsReport.Contributor(kv.Key, kv.Value.Commits, kv.Value.Added, kv.Value.Removed, kv.Value.Last))
                .OrderByDescending(c => c.Commits).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Files = Ranked(byFile.Select(kv => new RepoStatsReport.PathActivity(kv.Key, kv.Value.Commits, kv.Value.Added, kv.Value.Removed)))
                .Take(Math.Max(0, top)).ToList(),
            Areas = Ranked(byArea.Select(kv => new RepoStatsReport.PathActivity(kv.Key, kv.Value.Commits, kv.Value.Added, kv.Value.Removed)))
                .Take(Math.Max(0, top)).ToList(),
            Activity = byCell.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                .Select(kv => new RepoStatsReport.ActivityCell(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList(),
        };
    }

    /// <summary>The top-level folder a path belongs to; a file at the root is its own area.</summary>
    public static string Area(string path)
    {
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..(slash + 1)];
    }

    internal static DateTimeOffset BucketStart(DateTimeOffset date, RepoStatsBucket bucket, TimeZoneInfo zone, DayOfWeek firstDay)
    {
        var day = TimeZoneInfo.ConvertTime(date, zone).DateTime.Date;
        var start = bucket switch
        {
            RepoStatsBucket.Week => day.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7)),
            RepoStatsBucket.Month => new DateTime(day.Year, day.Month, 1),
            _ => day,
        };
        return RepoStatsRange.AtMidnight(start, zone);
    }

    private static DateTimeOffset NextBucket(DateTimeOffset start, RepoStatsBucket bucket, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(start, zone).DateTime.Date;
        var next = bucket switch
        {
            RepoStatsBucket.Week => day.AddDays(7),
            RepoStatsBucket.Month => day.AddMonths(1),
            _ => day.AddDays(1),
        };
        return RepoStatsRange.AtMidnight(next, zone);
    }
}

// ── Across repositories ─────────────────────────────────────────────────

/// <summary>
/// One repository's line in the cross-repository summary. <c>OpenChanges</c>
/// counts staged, unstaged and untracked paths; <c>Timeline</c> holds commits
/// per bucket, aligned with the summary's <c>BucketStarts</c>.
/// </summary>
public sealed record RepoStatsSummaryRow(
    string Id, string DisplayName, int Commits, int Authors, int Added, int Removed,
    DateTimeOffset? LastCommit, string? Branch, int Ahead, int Behind,
    int OpenChanges,
    string? Error,
    IReadOnlyList<int> Timeline);

/// <summary>One repository's input to <see cref="RepoStatsSummary.Build"/>.</summary>
public sealed record RepoStatsInput(string Id, string DisplayName, IReadOnlyList<RepoChurnCommit> Commits, RepoStatus? Status, string? Error);

/// <summary>
/// Every tracked repository over one period, for <c>fleetmate repos stats</c>
/// with no repository named and for the Insights view's All Tracked scope.
/// </summary>
public sealed class RepoStatsSummary
{
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset Until { get; init; }
    public RepoStatsBucket Bucket { get; init; }
    public List<DateTimeOffset> BucketStarts { get; init; } = new();
    public List<RepoStatsSummaryRow> Rows { get; init; } = new();
    public RepoStatsReport Combined { get; init; } = new();

    /// <summary>
    /// Builds the summary from each repository's commits and status. Rows are
    /// ordered by commits in the period, then name.
    /// </summary>
    public static RepoStatsSummary Build(
        IReadOnlyList<RepoStatsInput> inputs,
        DateTimeOffset? since,
        DateTimeOffset? until = null,
        RepoStatsBucket? bucket = null,
        int top = 15,
        TimeZoneInfo? zone = null,
        DayOfWeek? firstDayOfWeek = null)
    {
        var end = until ?? DateTimeOffset.Now;
        var combined = RepoStatistics.Report(inputs.SelectMany(i => i.Commits), since, end, bucket, top, zone, firstDayOfWeek);
        var starts = combined.Timeline.Select(p => p.Start).ToList();
        var index = starts.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var rows = inputs.Select(input =>
        {
            var report = RepoStatistics.Report(input.Commits, combined.Since ?? starts.FirstOrDefault(), end, combined.Bucket, 0, zone, firstDayOfWeek);
            var counts = new int[starts.Count];
            foreach (var point in report.Timeline)
                if (index.TryGetValue(point.Start, out var i)) counts[i] = point.Commits;
            var status = input.Status;
            return new RepoStatsSummaryRow(
                input.Id, input.DisplayName,
                report.Total.Commits, report.Total.Authors, report.Total.Added, report.Total.Removed,
                input.Commits.Count > 0 ? input.Commits.Max(c => c.Date) : status?.LastCommitAt,
                status?.Branch, status?.Ahead ?? 0, status?.Behind ?? 0,
                (status?.Staged ?? 0) + (status?.Unstaged ?? 0) + (status?.Untracked ?? 0),
                input.Error ?? status?.Error,
                counts);
        })
        .OrderByDescending(r => r.Commits).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
        return new RepoStatsSummary
        {
            Since = since, Until = end, Bucket = combined.Bucket, BucketStarts = starts, Rows = rows, Combined = combined,
        };
    }
}

public static class RepoManagerStatistics
{
    /// <summary>One repository's statistics over a period.</summary>
    public static async Task<RepoStatsReport> StatsAsync(this RepoManager manager, RepoRecord record, DateTimeOffset? since,
        DateTimeOffset? until = null, RepoStatsBucket? bucket = null, int top = 15, CancellationToken ct = default)
    {
        var end = until ?? DateTimeOffset.Now;
        var commits = await manager.WorkingCopy(record).ChurnLogAsync(since, end, ct: ct);
        return RepoStatistics.Report(commits, since, end, bucket, top);
    }

    /// <summary>
    /// Statistics across <paramref name="records"/>, a bounded number at a
    /// time. A repository that fails to read becomes a row with its error; it
    /// never hides the others.
    /// </summary>
    public static async Task<RepoStatsSummary> StatsSummaryAsync(this RepoManager manager, IReadOnlyList<RepoRecord> records,
        DateTimeOffset? since, DateTimeOffset? until = null, RepoStatsBucket? bucket = null, int top = 15, CancellationToken ct = default)
    {
        var end = until ?? DateTimeOffset.Now;
        int concurrency;
        try { concurrency = manager.Settings().Concurrency; }
        catch (Exception) { concurrency = RepoSettings.Default.Concurrency; }
        var inputs = await Bounded.MapAsync(records, concurrency, async record =>
        {
            var status = await manager.StatusAsync(record, ct);
            try
            {
                var commits = await manager.WorkingCopy(record).ChurnLogAsync(since, end, ct: ct);
                return new RepoStatsInput(record.Id, record.Key.DisplayName, commits, status, null);
            }
            catch (RepoException ex)
            {
                return new RepoStatsInput(record.Id, record.Key.DisplayName, Array.Empty<RepoChurnCommit>(), status, ex.Message);
            }
        });
        return RepoStatsSummary.Build(inputs, since, end, bucket, top);
    }
}
