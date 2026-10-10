using FleetMate.Core.Services.Repos;
using Xunit;

namespace FleetMate.Tests;

public class RepoChurnLogParserTests
{
    [Fact]
    public void ParsesHeadersAndNumstatLines()
    {
        var output =
            "\u001eaaa111\u001fAda Example\u001fada@example.com\u001f1767225600\n\n" +
            "10\t2\tSources/App/main.cs\n" +
            "-\t-\tAssets/logo.png\n" +
            "0\t5\tREADME.md\n" +
            "\u001ebbb222\u001fGrace Example\u001fgrace@example.com\u001f1767312000\n\n" +
            "3\t0\tdocs/guide with spaces.md\n\n";
        var commits = GitChurnLog.Parse(output);
        Assert.Equal(2, commits.Count);
        Assert.Equal("aaa111", commits[0].Sha);
        Assert.Equal("Ada Example", commits[0].Author);
        Assert.Equal("ada@example.com", commits[0].Email);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_767_225_600), commits[0].Date);
        Assert.Equal(new[] { "Sources/App/main.cs", "Assets/logo.png", "README.md" }, commits[0].Files.Select(f => f.Path));
        Assert.Equal(10, commits[0].Added);
        Assert.Equal(7, commits[0].Removed);
        Assert.True(commits[0].Files[1].IsBinary);
        Assert.Equal("docs/guide with spaces.md", commits[1].Files[0].Path);
    }

    [Fact]
    public void CommitWithNoFiles_StillCounts()
    {
        var commits = GitChurnLog.Parse("\u001eccc333\u001fAda Example\u001fada@example.com\u001f1767225600\n");
        Assert.Single(commits);
        Assert.Empty(commits[0].Files);
    }

    [Fact]
    public void SkipsMalformedHeadersAndLines()
    {
        var output =
            "\u001ebroken header\n1\t1\ta.txt\n" +
            "\u001eddd444\u001fAda\u001fada@example.com\u001fnot-a-time\n" +
            "\u001eeee555\u001fAda\u001fada@example.com\u001f1767225600\r\nx\t1\tbad.txt\r\n4\t1\tgood.txt";
        var commits = GitChurnLog.Parse(output);
        Assert.Equal(new[] { "eee555" }, commits.Select(c => c.Sha));
        Assert.Equal(new[] { "good.txt" }, commits[0].Files.Select(f => f.Path));
    }

    [Fact]
    public void EmptyOutput_IsNoCommits() => Assert.Empty(GitChurnLog.Parse(""));
}

public class RepoStatisticsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset D(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static RepoChurnCommit Commit(string sha, string author, string when, params (string Path, int Added, int Removed)[] files) =>
        new(sha, author, author.ToLowerInvariant() + "@example.com", D(when), files.Select(f => new RepoFileChurn(f.Path, f.Added, f.Removed)).ToList());

    private static readonly RepoChurnCommit[] Sample =
    {
        Commit("1", "Ada", "2026-01-05T10:00:00Z", ("Sources/a.cs", 10, 0), ("README.md", 1, 1)),
        Commit("2", "Ada", "2026-01-06T15:30:00Z", ("Sources/a.cs", 5, 2), ("Sources/b.cs", 3, 0)),
        Commit("3", "Grace", "2026-01-20T09:00:00Z", ("Tests/aTests.cs", 20, 0)),
        Commit("4", "Grace", "2025-12-01T09:00:00Z", ("old.txt", 1, 0)),
    };

    private static RepoStatsReport Report(DateTimeOffset? since, DateTimeOffset until, RepoStatsBucket? bucket = null, int top = 15,
        IEnumerable<RepoChurnCommit>? commits = null) =>
        RepoStatistics.Report(commits ?? Sample, since, until, bucket, top, Utc, DayOfWeek.Sunday);

    [Fact]
    public void Totals_CountOnlyTheRange()
    {
        var t = Report(D("2026-01-01T00:00:00Z"), D("2026-01-31T00:00:00Z"), RepoStatsBucket.Week).Total;
        Assert.Equal(3, t.Commits);
        Assert.Equal(2, t.Authors);
        Assert.Equal(39, t.Added);
        Assert.Equal(3, t.Removed);
        Assert.Equal(4, t.FilesTouched);
        Assert.Equal(D("2026-01-05T10:00:00Z"), t.FirstCommit);
        Assert.Equal(D("2026-01-20T09:00:00Z"), t.LastCommit);
    }

    [Fact]
    public void WeeklyTimeline_IsZeroFilledAndContiguous()
    {
        var report = Report(D("2026-01-01T00:00:00Z"), D("2026-01-31T00:00:00Z"), RepoStatsBucket.Week);
        // Weeks start on Sunday: Dec 28, Jan 4, 11, 18, 25.
        Assert.Equal(new[]
        {
            D("2025-12-28T00:00:00Z"), D("2026-01-04T00:00:00Z"), D("2026-01-11T00:00:00Z"),
            D("2026-01-18T00:00:00Z"), D("2026-01-25T00:00:00Z"),
        }, report.Timeline.Select(p => p.Start));
        Assert.Equal(new[] { 0, 2, 0, 1, 0 }, report.Timeline.Select(p => p.Commits));
        Assert.Equal(19, report.Timeline[1].Added);
        Assert.Equal(3, report.Timeline[1].Removed);
    }

    [Fact]
    public void DailyTimeline_HasOnePointPerDay() =>
        Assert.Equal(new[] { 1, 1, 0 },
            Report(D("2026-01-05T00:00:00Z"), D("2026-01-07T12:00:00Z"), RepoStatsBucket.Day).Timeline.Select(p => p.Commits));

    [Fact]
    public void MonthlyTimeline()
    {
        var report = Report(null, D("2026-01-31T00:00:00Z"), RepoStatsBucket.Month);
        Assert.Equal(new[] { D("2025-12-01T00:00:00Z"), D("2026-01-01T00:00:00Z") }, report.Timeline.Select(p => p.Start));
        Assert.Equal(new[] { 1, 3 }, report.Timeline.Select(p => p.Commits));
    }

    [Fact]
    public void AutomaticBucket_FollowsThePeriodLength()
    {
        Assert.Equal(RepoStatsBucket.Day, RepoStatsRange.AutomaticBucket(14));
        Assert.Equal(RepoStatsBucket.Week, RepoStatsRange.AutomaticBucket(90));
        Assert.Equal(RepoStatsBucket.Week, RepoStatsRange.AutomaticBucket(365));
        Assert.Equal(RepoStatsBucket.Month, RepoStatsRange.AutomaticBucket(900));
        Assert.Equal(RepoStatsBucket.Day, Report(D("2026-01-01T00:00:00Z"), D("2026-01-10T00:00:00Z")).Bucket);
    }

    [Fact]
    public void Contributors_ByCommitsThenName()
    {
        var report = Report(null, D("2026-01-31T00:00:00Z"));
        Assert.Equal(new[] { "Ada", "Grace" }, report.Contributors.Select(c => c.Name));
        Assert.Equal(19, report.Contributors[0].Added);
        Assert.Equal(2, report.Contributors[1].Commits);
        Assert.Equal(D("2026-01-20T09:00:00Z"), report.Contributors[1].LastCommit);
    }

    [Fact]
    public void FilesAndAreas_RankByCommitsThenChurn()
    {
        var report = Report(D("2026-01-01T00:00:00Z"), D("2026-01-31T00:00:00Z"));
        Assert.Equal("Sources/a.cs", report.Files[0].Path);
        Assert.Equal(2, report.Files[0].Commits);
        Assert.Equal(17, report.Files[0].Churn);
        // Sources/ is touched by two commits; two files in one commit count once.
        Assert.Equal(new[] { "Sources/", "Tests/", "README.md" }, report.Areas.Select(a => a.Path));
        Assert.Equal(2, report.Areas[0].Commits);
        Assert.Equal(18, report.Areas[0].Added);
    }

    [Fact]
    public void Top_LimitsFilesAndAreas()
    {
        var report = Report(null, D("2026-01-31T00:00:00Z"), top: 1);
        Assert.Single(report.Files);
        Assert.Single(report.Areas);
    }

    [Fact]
    public void Activity_ByWeekdayAndHour()
    {
        var report = Report(D("2026-01-01T00:00:00Z"), D("2026-01-31T00:00:00Z"));
        // Jan 5 2026 is a Monday (2), Jan 6 a Tuesday (3), Jan 20 a Tuesday.
        Assert.Equal(new[] { (2, 10, 1), (3, 9, 1), (3, 15, 1) }, report.Activity.Select(c => (c.Weekday, c.Hour, c.Commits)));
    }

    [Fact]
    public void EmptyHistory_GivesAZeroTimeline()
    {
        var report = Report(D("2026-01-01T00:00:00Z"), D("2026-01-03T00:00:00Z"), RepoStatsBucket.Day, commits: Array.Empty<RepoChurnCommit>());
        Assert.Equal(new RepoStatsReport.Totals(), report.Total);
        Assert.Equal(new[] { 0, 0, 0 }, report.Timeline.Select(p => p.Commits));
    }

    [Fact]
    public void AreaOfPath()
    {
        Assert.Equal("Sources/", RepoStatistics.Area("Sources/App/main.cs"));
        Assert.Equal("README.md", RepoStatistics.Area("README.md"));
    }

    [Fact]
    public void Summary_AlignsEachRepositoryWithTheCombinedTimeline()
    {
        var summary = RepoStatsSummary.Build(new[]
        {
            new RepoStatsInput("github:example/one", "example/one", Sample.Take(2).ToList(), null, null),
            new RepoStatsInput("github:example/two", "example/two", new[] { Sample[2] }, null, null),
            new RepoStatsInput("github:example/empty", "example/empty", Array.Empty<RepoChurnCommit>(), null, "checkout missing"),
        }, D("2026-01-01T00:00:00Z"), D("2026-01-31T00:00:00Z"), RepoStatsBucket.Week, zone: Utc, firstDayOfWeek: DayOfWeek.Sunday);
        Assert.Equal(5, summary.BucketStarts.Count);
        Assert.Equal(new[] { "example/one", "example/two", "example/empty" }, summary.Rows.Select(r => r.DisplayName));
        Assert.Equal(new[] { 0, 2, 0, 0, 0 }, summary.Rows[0].Timeline);
        Assert.Equal(new[] { 0, 0, 0, 1, 0 }, summary.Rows[1].Timeline);
        Assert.Equal("checkout missing", summary.Rows[2].Error);
        Assert.Equal(3, summary.Combined.Total.Commits);
    }
}

public class RepoStatsRangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-15T12:00:00Z");

    private static DateTimeOffset? Since(string value) => RepoStatsRange.ParseSince(value, Now, TimeZoneInfo.Utc);

    [Fact]
    public void RelativePeriods_StartAtMidnight()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-02-13T00:00:00Z"), Since("30d"));
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), Since("2w"));
        Assert.Equal(DateTimeOffset.Parse("2025-09-15T00:00:00Z"), Since("6M"));
        Assert.Equal(DateTimeOffset.Parse("2025-03-15T00:00:00Z"), Since("1y"));
    }

    [Fact]
    public void AbsoluteDate() => Assert.Equal(DateTimeOffset.Parse("2026-01-31T00:00:00Z"), Since("2026-01-31"));

    [Theory]
    [InlineData("")]
    [InlineData("0d")]
    [InlineData("10x")]
    [InlineData("-3d")]
    [InlineData("yesterday")]
    public void RejectsNonsense(string value) => Assert.Null(Since(value));

    [Fact]
    public void Buckets()
    {
        Assert.Equal(RepoStatsBucket.Week, RepoStatsRange.ParseBucket("WEEK"));
        Assert.Null(RepoStatsRange.ParseBucket("fortnight"));
    }
}

public sealed class RepoChurnLogGitTests : TempGitRepository
{
    [Fact]
    public async Task ChurnLog_ReadsRealHistory_WithoutMerges()
    {
        Write("a.txt", "one\ntwo\n");
        await Copy.CommitAsync("First", null, new HashSet<string>(), allowProtected: true);
        Write("a.txt", "one\n");
        Write("docs/b.md", "x\n");
        await Copy.CommitAsync("Second", null, new HashSet<string>(), allowProtected: true);

        var commits = await Copy.ChurnLogAsync();
        Assert.Equal(2, commits.Count);
        Assert.Equal(new[] { "a.txt", "docs/b.md" }, commits[0].Files.Select(f => f.Path).OrderBy(p => p));
        Assert.Equal(1, commits[0].Removed);
        Assert.Equal("Dev", commits[0].Author);

        var report = RepoStatistics.Report(commits, null);
        Assert.Equal(2, report.Total.Commits);
        Assert.Contains(report.Areas, a => a.Path == "docs/");
        await Assert.ThrowsAsync<RepoException>(() => Copy.ChurnLogAsync(reference: "--all"));
    }
}
