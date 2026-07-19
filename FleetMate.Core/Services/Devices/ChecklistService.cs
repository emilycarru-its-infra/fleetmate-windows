using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FleetMate.Core.Services.Devices;

/// <summary>Status of a checklist item (mirrors the emoji vocabulary in checklist.md).</summary>
public enum ChecklistStatus { Untested, Passed, Failed, Warning }

/// <summary>One testable item parsed from checklist.md.</summary>
public sealed class ChecklistItem
{
    public string Name { get; init; } = "";
    public ChecklistStatus Status { get; init; }
    public string Description { get; init; } = "";
    public string Section { get; init; } = "";
    public string Type { get; init; } = "packages"; // "packages" | "deployment"
}

public sealed class ChecklistSummary
{
    public int PackagesTotal { get; set; }
    public int PackagesUntested { get; set; }
    public int PackagesPassed { get; set; }
    public int PackagesFailed { get; set; }
    public int PackagesWarning { get; set; }
    public int DeploymentTotal { get; set; }
    public int DeploymentUntested { get; set; }
    public int DeploymentPassed { get; set; }
    public int DeploymentFailed { get; set; }
    public int DeploymentWarning { get; set; }
    public ChecklistItem? NextUntested { get; set; }

    public int TotalItems => PackagesTotal + DeploymentTotal;
    public int TotalTested => PackagesPassed + PackagesFailed + PackagesWarning
                            + DeploymentPassed + DeploymentFailed + DeploymentWarning;
}

/// <summary>
/// Native C# port of quality/common/Checklist-Manager.ps1. Parses and updates
/// checklist.md (package testing progress). No PowerShell dependency.
/// </summary>
public sealed class ChecklistService
{
    public const string PassEmoji = "✅";        // ✅
    public const string FailEmoji = "❌";        // ❌
    public const string WarnEmoji = "⚠️";  // ⚠️

    private static readonly string EmojiAlt = $"(?:{PassEmoji}|{FailEmoji}|{WarnEmoji}|⚠)";
    // - [ ] ✅ `Name` desc   OR   - [ ] ✅ Name desc
    private static readonly Regex BacktickRe = new($@"^- \[( |x)\]( {EmojiAlt}️?)? `([^`]+)`(.*)$", RegexOptions.Compiled);
    private static readonly Regex PlainRe = new($@"^- \[( |x)\]( {EmojiAlt}️?)? ([A-Za-z0-9_\-\.\+]+)(.*)$", RegexOptions.Compiled);

    private readonly string? _pkgsInfoRoot;

    /// <param name="pkgsInfoRoot">deployment/pkgsinfo path for version/name lookups (optional).</param>
    public ChecklistService(string? pkgsInfoRoot = null) => _pkgsInfoRoot = pkgsInfoRoot;

    public List<ChecklistItem> GetItems(string checklistPath, ChecklistStatus? filter = null, string? typeFilter = null)
    {
        if (!File.Exists(checklistPath)) throw new FileNotFoundException($"Checklist not found: {checklistPath}");

        var items = new List<ChecklistItem>();
        string section = "", subsection = "";
        var inDeployment = false;

        foreach (var line in File.ReadAllLines(checklistPath))
        {
            var h2 = Regex.Match(line, "^## (.+)$");
            if (h2.Success) { section = h2.Groups[1].Value; subsection = ""; inDeployment = section == "Deployment Installer Tests"; continue; }
            var h3 = Regex.Match(line, "^### (.+)$");
            if (h3.Success) { subsection = h3.Groups[1].Value; continue; }

            var m = BacktickRe.Match(line);
            if (!m.Success) m = PlainRe.Match(line);
            if (!m.Success) continue;

            var checkbox = m.Groups[1].Value;
            var emoji = m.Groups[2].Value.Trim();
            var name = m.Groups[3].Value.Trim();
            var desc = (m.Groups[2].Value.Trim() + " " + m.Groups[4].Value.Trim()).Trim();

            var status = emoji.Contains(PassEmoji) ? ChecklistStatus.Passed
                       : emoji.Contains(FailEmoji) ? ChecklistStatus.Failed
                       : emoji.StartsWith('⚠') ? ChecklistStatus.Warning
                       : checkbox == "x" ? ChecklistStatus.Passed
                       : ChecklistStatus.Untested;

            var type = inDeployment ? "deployment" : "packages";
            var item = new ChecklistItem
            {
                Name = name,
                Status = status,
                Description = desc,
                Section = string.IsNullOrEmpty(subsection) ? section : $"{section} > {subsection}",
                Type = type,
            };

            if (filter.HasValue && item.Status != filter.Value) continue;
            if (typeFilter != null && item.Type != typeFilter) continue;
            items.Add(item);
        }

        return items;
    }

    public ChecklistItem? GetNextUntested(string checklistPath, string? typeFilter = null)
        => GetItems(checklistPath, ChecklistStatus.Untested, typeFilter).FirstOrDefault();

    /// <summary>Rewrite a checklist item's line with the new status, version, timestamp, and user; refresh progress counters.</summary>
    public bool UpdateItem(string checklistPath, string itemName, ChecklistStatus status, string? note, string? timestamp = null, string? user = null)
    {
        if (!File.Exists(checklistPath)) throw new FileNotFoundException($"Checklist not found: {checklistPath}");

        var lines = File.ReadAllLines(checklistPath).ToList();
        var versionSuffix = LookupVersion(itemName) is { } v ? $" v{v}" : "";
        var checkbox = status == ChecklistStatus.Passed ? "[x]" : "[ ]";
        var emoji = status switch
        {
            ChecklistStatus.Passed => PassEmoji + " ",
            ChecklistStatus.Failed => FailEmoji + " ",
            ChecklistStatus.Warning => WarnEmoji + " ",
            _ => "",
        };
        var ts = timestamp ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var by = string.IsNullOrEmpty(user) ? "" : $" by {user.Split(' ')[0]}";
        var stamp = status != ChecklistStatus.Untested ? $" - last tested {ts}{by}" : "";

        var escaped = Regex.Escape(itemName);
        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^- \[([x ])\]") && Regex.IsMatch(lines[i], $@"(?i){escaped}\b"))
            {
                var newLine = $"- {checkbox} {emoji}{itemName}{versionSuffix}";
                if (!string.IsNullOrEmpty(note)) newLine += $" - {note}";
                newLine += stamp;
                lines[i] = newLine;
                found = true;
                break;
            }
        }
        if (!found) return false;

        var content = UpdateProgress(string.Join("\r\n", lines));
        File.WriteAllText(checklistPath, content);
        return true;
    }

    /// <summary>Recompute the "Custom Installer Packages: x/y | Deployment Installers: a/b" and "Total Progress" counters.</summary>
    public static string UpdateProgress(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int pkgCount = 0, pkgPassed = 0, depCount = 0, depPassed = 0;
        var inDeployment = false;

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, "^## EXEs & MSI Packages directly in")) { inDeployment = true; continue; }
            if (Regex.IsMatch(line, "^## ") && inDeployment && !Regex.IsMatch(line, "^### ")) { inDeployment = false; continue; }

            var m = Regex.Match(line, @"^- \[([ x])\] ");
            if (!m.Success) continue;
            var passed = m.Groups[1].Value == "x" || line.Contains(PassEmoji);
            if (inDeployment) { depCount++; if (passed) depPassed++; }
            else { pkgCount++; if (passed) pkgPassed++; }
        }

        var total = pkgCount + depCount;
        var totalPassed = pkgPassed + depPassed;
        // Tolerate the emoji the checklist writes between the count and the pipe
        // (e.g. "35/54 ✅ | ... 34/90 ✅") and rewrite in the same style.
        content = Regex.Replace(content,
            @"\*\*Custom Installer Packages\*\*: \d+/\d+[^|]*\| \*\*Deployment Installers\*\*: \d+/\d+[^\r\n]*",
            $"**Custom Installer Packages**: {pkgPassed}/{pkgCount} {PassEmoji} | **Deployment Installers**: {depPassed}/{depCount} {PassEmoji}");
        content = Regex.Replace(content,
            @"\*\*Total Progress\*\*: \d+/\d+ items tested",
            $"**Total Progress**: {totalPassed}/{total} items tested");
        return content;
    }

    public ChecklistSummary GetSummary(string checklistPath)
    {
        var items = GetItems(checklistPath);
        var s = new ChecklistSummary();
        foreach (var it in items)
        {
            var pkg = it.Type == "packages";
            if (pkg) s.PackagesTotal++; else s.DeploymentTotal++;
            switch (it.Status)
            {
                case ChecklistStatus.Untested: if (pkg) s.PackagesUntested++; else s.DeploymentUntested++; break;
                case ChecklistStatus.Passed: if (pkg) s.PackagesPassed++; else s.DeploymentPassed++; break;
                case ChecklistStatus.Failed: if (pkg) s.PackagesFailed++; else s.DeploymentFailed++; break;
                case ChecklistStatus.Warning: if (pkg) s.PackagesWarning++; else s.DeploymentWarning++; break;
            }
        }
        s.NextUntested = items.FirstOrDefault(i => i.Status == ChecklistStatus.Untested);
        return s;
    }

    private string? LookupVersion(string itemName)
    {
        if (string.IsNullOrEmpty(_pkgsInfoRoot) || !Directory.Exists(_pkgsInfoRoot)) return null;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();

        string? best = null;
        Version? bestParsed = null;
        foreach (var file in Directory.EnumerateFiles(_pkgsInfoRoot, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var data = deserializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                if (data == null) continue;
                var name = data.TryGetValue("name", out var n) ? n?.ToString() : null;
                var display = data.TryGetValue("display_name", out var d) ? d?.ToString() : null;
                if (!string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(display, itemName, StringComparison.OrdinalIgnoreCase)) continue;
                var ver = data.TryGetValue("version", out var vv) ? vv?.ToString() : null;
                if (string.IsNullOrEmpty(ver)) continue;
                if (Version.TryParse(ver, out var parsed))
                {
                    if (bestParsed == null || parsed > bestParsed) { bestParsed = parsed; best = ver; }
                }
                else best ??= ver;
            }
            catch { /* skip unparseable pkgsinfo */ }
        }
        return best;
    }
}
