using System.Diagnostics;
using System.Text;

namespace FleetMate.Core.Services.Repos;

/// <summary>
/// Fictional repositories for demo mode and screenshots: three made-up
/// projects with made-up people, files and months of history, built with
/// <c>git fast-import</c> in a folder of their own, linked in a registry of
/// their own. The person's registry, settings and clones are never read or
/// touched. Each has an <c>origin/main</c> so Insights works, and the first is
/// left on a feature branch with work in progress so Changes has something to show.
/// </summary>
public static class RepoDemoFixture
{
    private static readonly (string Name, string Email)[] People =
    {
        ("Avery Quinn", "avery@example.com"), ("Jordan Blake", "jordan@example.com"),
        ("Sam Rivera", "sam@example.com"), ("Riley Chen", "riley@example.com"), ("Morgan Patel", "morgan@example.com"),
    };

    private sealed record Project(string Name, string Owner, string[] Files, string[] Subjects, int Commits);

    private static readonly Project[] Projects =
    {
        new("harbor-api", "fabrikam-demo",
            new[] { "src/Harbor.Api/Program.cs", "src/Harbor.Api/Routes/Berths.cs", "src/Harbor.Api/Routes/Vessels.cs",
                "src/Harbor.Core/Tides.cs", "tests/Harbor.Tests/TidesTests.cs", "docs/routes.md", "AGENTS.md", "README.md" },
            new[] { "Add the berth schedule route", "Read tide tables from the cache", "Return 404 for an unknown vessel",
                "Cover the spring tide edge case", "Document the vessel routes", "Trim the startup log", "Page the berth list",
                "Rename the tide window setting", "Retry the tide feed once on timeout", "Sort vessels by arrival" },
            46),
        new("lantern-web", "fabrikam-demo",
            new[] { "app/index.html", "app/scripts/lantern.js", "app/scripts/map.js", "app/styles/site.css",
                "app/styles/print.css", "README.md" },
            new[] { "Draw the lantern map at full width", "Fix the print margins", "Load map tiles lazily",
                "Tighten the header spacing", "Add a dark theme palette", "Label the legend for screen readers" },
            28),
        new("tidepool-tools", "contoso-sample",
            new[] { "scripts/sample.ps1", "scripts/report.ps1", "config/defaults.json", "README.md" },
            new[] { "Add a dry-run switch to the report", "Read defaults from config", "Quote paths with spaces",
                "Write the summary as JSON" },
            14),
    };

    /// <summary>The folder demo repositories are built in: under the temp folder, never the person's code.</summary>
    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "FleetMate-Demo", "Repos");

    /// <summary>
    /// Builds the fictional repositories in <paramref name="folder"/> (replacing
    /// any earlier copy) and returns a manager whose registry tracks them.
    /// </summary>
    public static async Task<RepoManager> CreateAsync(string? folder = null, DateTimeOffset? now = null)
    {
        folder ??= DefaultFolder;
        var clock = now ?? DateTimeOffset.Now;
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        var code = Path.Combine(folder, "Code");
        var manager = new RepoManager(new RepoRegistryStore(Path.Combine(folder, "Registry", "repos.json")));
        manager.UpdateSettings(s =>
        {
            s.CloneRoot = code;
            s.ScanRoots = new List<string> { code };
            s.AutoTrackNewClones = false;
            s.RootsChosen = true;
        });
        for (var i = 0; i < Projects.Length; i++)
        {
            var project = Projects[i];
            var path = Path.Combine(code, "GitHub", project.Owner, project.Name);
            await BuildAsync(project, path, clock, seed: 17 + i, workInProgress: i == 0);
            await manager.LinkAsync(path, tracked: true);
        }
        return manager;
    }

    private static async Task BuildAsync(Project project, string path, DateTimeOffset now, int seed, bool workInProgress)
    {
        Directory.CreateDirectory(path);
        var copy = new GitWorkingCopy(path);
        foreach (var args in new[]
        {
            new[] { "init", "-q", "-b", "main" },
            new[] { "config", "user.name", People[0].Name }, new[] { "config", "user.email", People[0].Email },
            new[] { "config", "commit.gpgsign", "false" }, new[] { "config", "core.autocrlf", "false" },
            new[] { "remote", "add", "origin", $"https://github.com/{project.Owner}/{project.Name}.git" },
        })
            await Required(copy.GitAsync(args));

        await FastImportAsync(path, Stream(project, now, seed));
        await Required(copy.GitAsync(new[] { "reset", "-q", "--hard", "main" }));
        await Required(copy.GitAsync(new[] { "branch", "-D", "side" }));
        await Required(copy.GitAsync(new[] { "update-ref", RepoInsightsModel.HistoryReference, "main" }));
        // A tag and an older remote-tracking state make the History badges real.
        await Required(copy.GitAsync(new[] { "tag", "v1.0", "main~6" }));
        if (!workInProgress) return;

        await Required(copy.SwitchBranchAsync("feature/tide-alerts", create: true));
        copy.WriteFile("src/Harbor.Core/Alerts.cs", Encoding.UTF8.GetBytes(
            "namespace Harbor.Core;\n\npublic sealed record TideAlert(string Berth, double Metres)\n{\n    public bool IsHigh => Metres > 4.2;\n}\n"));
        await Required(copy.GitAsync(new[] { "add", "src/Harbor.Core/Alerts.cs" }));
        await Required(copy.CommitAsync("Add tide alerts", null, false, false, new HashSet<string>()));
        copy.WriteFile("README.md", Encoding.UTF8.GetBytes(Readme(project) + "\n## Alerts\n\nHigh tides raise an alert for each berth.\n"));
        copy.WriteFile("src/Harbor.Core/Tides.cs", Encoding.UTF8.GetBytes(
            Lines("src/Harbor.Core/Tides.cs", 18, seed) + "    // Alerts read the same window.\n"));
        await Required(copy.GitAsync(new[] { "add", "README.md" }));
        copy.WriteFile("notes/next-steps.md", Encoding.UTF8.GetBytes("- Wire alerts to the berth board\n- Add a quiet-hours setting\n"));
    }

    /// <summary>A fast-import stream: main's history, with one merged side branch so the lane graph has a fork.</summary>
    private static string Stream(Project project, DateTimeOffset now, int seed)
    {
        var random = new Random(seed);
        var contents = project.Files.ToDictionary(f => f, f => f == "README.md" ? Readme(project) : Lines(f, 6, seed));
        var stream = new StringBuilder();
        var start = now.AddDays(-150).ToUnixTimeSeconds();
        var span = now.AddHours(-3).ToUnixTimeSeconds() - start;
        var mark = 0;
        var branchFrom = project.Commits * 2 / 3;
        int? sideTip = null;
        for (var i = 0; i < project.Commits; i++)
        {
            var when = start + span * i / project.Commits + random.Next(0, 3600 * 6);
            // Most commits land on weekday working hours, as a team's would.
            var stamp = DateTimeOffset.FromUnixTimeSeconds(when).ToLocalTime();
            var hour = 9 + random.Next(0, 9);
            stamp = new DateTimeOffset(stamp.Year, stamp.Month, stamp.Day, hour, random.Next(0, 60), 0, stamp.Offset);
            if (stamp.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && random.Next(0, 4) > 0) stamp = stamp.AddDays(2);
            if (stamp > now) stamp = now.AddMinutes(-30 - i);
            var person = People[(i * 3 + random.Next(0, 2)) % People.Length];
            var subject = project.Subjects[i % project.Subjects.Length] + (i >= project.Subjects.Length ? $" ({i / project.Subjects.Length + 1})" : "");
            if (i == 0) subject = "Start " + project.Name;

            var changed = i == 0 ? project.Files : Enumerable.Range(0, random.Next(1, 4)).Select(_ => project.Files[random.Next(project.Files.Length)]).Distinct().ToArray();
            foreach (var file in changed.Where(f => i > 0 && f != "README.md"))
            {
                // Now and then a commit drops an old line too, so Insights shows removals.
                if (random.Next(0, 4) == 0 && contents[file].Split('\n').Length > 6)
                {
                    var lines = contents[file].Split('\n').ToList();
                    lines.RemoveAt(lines.Count / 2);
                    contents[file] = string.Join('\n', lines);
                }
                contents[file] += Line(file, i, random);
            }

            var onSide = i == branchFrom + 1 || i == branchFrom + 2;
            mark++;
            stream.Append(onSide ? "commit refs/heads/side\n" : "commit refs/heads/main\n");
            stream.Append($"mark :{mark}\n");
            var who = $"{person.Name} <{person.Email}> {stamp.ToUnixTimeSeconds()} {stamp.ToString("zzz").Replace(":", "")}";
            stream.Append($"author {who}\ncommitter {who}\n");
            Data(stream, subject + "\n");
            if (i == branchFrom + 1) stream.Append($"from :{branchFrom + 1}\n");
            if (i == branchFrom + 3 && sideTip is { } tip) stream.Append($"from :{mark - 3}\nmerge :{tip}\n");
            foreach (var file in changed)
            {
                stream.Append($"M 100644 inline {file}\n");
                Data(stream, contents[file]);
            }
            stream.Append('\n');
            if (onSide) sideTip = mark;
        }
        return stream.ToString();
    }

    private static void Data(StringBuilder stream, string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        stream.Append($"data {bytes}\n").Append(text).Append('\n');
    }

    private static string Readme(Project project) =>
        $"# {project.Name}\n\nA sample project for FleetMate's demo mode. Nothing here is real.\n";

    private static string Lines(string file, int count, int seed)
    {
        var random = new Random(seed + file.Length);
        var text = new StringBuilder(Header(file));
        for (var i = 0; i < count; i++) text.Append(Line(file, i, random));
        return text.ToString();
    }

    private static string Header(string file) => Path.GetExtension(file) switch
    {
        ".cs" => $"namespace Sample;\n\n// {Path.GetFileNameWithoutExtension(file)}\n",
        ".js" => "'use strict';\n\n",
        ".css" => "/* sample styles */\n",
        ".html" => "<!doctype html>\n<title>Lantern</title>\n",
        ".ps1" => "param([switch]$DryRun)\n\n",
        ".json" => "{ \"sample\": true }\n",
        _ => $"# {Path.GetFileNameWithoutExtension(file)}\n\n",
    };

    private static string Line(string file, int index, Random random) => Path.GetExtension(file) switch
    {
        ".cs" => $"    // step {index}: window {random.Next(2, 9)}.{random.Next(0, 9)} m\n",
        ".js" => $"const step{index} = {random.Next(1, 400)};\n",
        ".css" => $".panel-{index} {{ margin: {random.Next(0, 24)}px; }}\n",
        ".html" => $"<p>Section {index}</p>\n",
        ".ps1" => $"Write-Verbose 'step {index}'\n",
        ".json" => "",
        _ => $"- Note {index}\n",
    };

    private static async Task Required(Task<GitResult> run)
    {
        var result = await run;
        if (result.ExitCode != 0) throw RepoException.GitFailed("git", result.StandardError.Trim());
    }

    private static async Task Required(Task<string> run) => await run;

    private static async Task Required(Task run) => await run;

    private static async Task FastImportAsync(string path, string stream)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-C", path, "fast-import", "--quiet" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw RepoException.GitFailed("fast-import", "git did not start");
        var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
        var error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await stdin.WriteAsync(stream);
        await stdin.FlushAsync();
        stdin.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw RepoException.GitFailed("fast-import", (await error).Trim());
    }
}
