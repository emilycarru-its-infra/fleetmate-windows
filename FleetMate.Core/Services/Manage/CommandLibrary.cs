using System.Text;
using FleetMate.Core.Models.Manage;
using Serilog;
using YamlDotNet.RepresentationModel;

namespace FleetMate.Core.Services.Manage;

/// <summary>
/// The YAML command library shared with ScanLab:
/// <code>
/// categories:
///   - name: System
///     commands:
///       - label: Hostname
///         command: hostname
///         trust: safe
/// </code>
/// Parsed with YamlDotNet so quoting, multi-line scalars and comments all
/// behave; serialised back in the same shape so the file stays hand-editable.
/// </summary>
public class CommandLibrary
{
    public static List<CommandCategory> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Log.Warning("Command library not found at {Path}; using built-in defaults", path);
            return DefaultCategories();
        }

        try
        {
            var parsed = Parse(File.ReadAllText(path));
            return parsed.Count == 0 ? DefaultCategories() : parsed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Command library at {Path} did not parse; using built-in defaults", path);
            return DefaultCategories();
        }
    }

    public static List<CommandCategory> Parse(string yaml)
    {
        var categories = new List<CommandCategory>();
        if (string.IsNullOrWhiteSpace(yaml)) return categories;

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return categories;

        if (!TryGet(root, "categories", out var categoriesNode) || categoriesNode is not YamlSequenceNode categorySeq)
            return categories;

        foreach (var catNode in categorySeq.Children.OfType<YamlMappingNode>())
        {
            var name = Scalar(catNode, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var category = new CommandCategory(name.Trim());
            if (TryGet(catNode, "commands", out var commandsNode) && commandsNode is YamlSequenceNode commandSeq)
            {
                foreach (var cmdNode in commandSeq.Children.OfType<YamlMappingNode>())
                {
                    var label = Scalar(cmdNode, "label");
                    var command = Scalar(cmdNode, "command");
                    if (string.IsNullOrWhiteSpace(label) || command == null) continue;

                    var trustRaw = Scalar(cmdNode, "trust");
                    var explicitTrust = CommandTrustLevelExtensions.TryParse(trustRaw, out var trust);
                    if (!explicitTrust) trust = TrustInference.Infer(command);

                    category.Commands.Add(new ManagedCommand(label.Trim(), command, trust, explicitTrust));
                }
            }
            categories.Add(category);
        }

        return categories;
    }

    public static string Serialize(IEnumerable<CommandCategory> categories)
    {
        var sb = new StringBuilder();
        sb.Append("categories:\n");
        foreach (var cat in categories)
        {
            sb.Append("  - name: ").Append(Quote(cat.Name)).Append('\n');
            sb.Append("    commands:\n");
            foreach (var cmd in cat.Commands)
            {
                sb.Append("      - label: ").Append(Quote(cmd.Label)).Append('\n');
                EmitScalar(sb, "        ", "command", cmd.Command);
                sb.Append("        trust: ").Append(cmd.TrustLevel.ToYaml()).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Single-line values are quoted inline; multi-line scripts become a literal block scalar.</summary>
    private static void EmitScalar(StringBuilder sb, string indent, string key, string value)
    {
        var normalized = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.Contains('\n'))
        {
            sb.Append(indent).Append(key).Append(": ").Append(Quote(normalized)).Append('\n');
            return;
        }

        sb.Append(indent).Append(key).Append(": |-\n");
        foreach (var line in normalized.TrimEnd('\n').Split('\n'))
        {
            sb.Append(indent).Append("  ");
            if (line.Length > 0) sb.Append(line);
            sb.Append('\n');
        }
    }

    public static void Save(IEnumerable<CommandCategory> categories, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(categories), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Add any bundled category or command the user's library lacks, matched by
    /// normalised name and label, without touching what the user has edited.
    /// Returns true when something was added.
    /// </summary>
    public static bool MergeMissing(List<CommandCategory> target, IEnumerable<CommandCategory> bundled)
    {
        var changed = false;
        foreach (var bundledCategory in bundled)
        {
            var existing = target.FirstOrDefault(c => Norm(c.Name) == Norm(bundledCategory.Name));
            if (existing == null)
            {
                target.Add(new CommandCategory(bundledCategory.Name, bundledCategory.Commands.Select(Clone)));
                changed = true;
                continue;
            }

            var labels = existing.Commands.Select(c => Norm(c.Label)).ToHashSet();
            foreach (var cmd in bundledCategory.Commands.Where(c => !labels.Contains(Norm(c.Label))))
            {
                existing.Commands.Add(Clone(cmd));
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Static checks a library must pass before it ships: unique labels per
    /// category, non-empty commands, explicit trust, and trust not weaker than
    /// what the destructive/caution patterns infer.
    /// </summary>
    public static List<CommandAuditIssue> Audit(IEnumerable<CommandCategory> categories)
    {
        var issues = new List<CommandAuditIssue>();
        var categoryNames = new HashSet<string>();

        foreach (var cat in categories)
        {
            if (!categoryNames.Add(Norm(cat.Name)))
                issues.Add(new(CommandAuditSeverity.Error, cat.Name, "", "duplicate category name"));
            if (cat.Commands.Count == 0)
                issues.Add(new(CommandAuditSeverity.Warning, cat.Name, "", "category has no commands"));

            var labels = new HashSet<string>();
            foreach (var cmd in cat.Commands)
            {
                if (!labels.Add(Norm(cmd.Label)))
                    issues.Add(new(CommandAuditSeverity.Error, cat.Name, cmd.Label, "duplicate label in category"));
                if (string.IsNullOrWhiteSpace(cmd.Command))
                    issues.Add(new(CommandAuditSeverity.Error, cat.Name, cmd.Label, "empty command"));
                if (!cmd.TrustWasExplicit)
                    issues.Add(new(CommandAuditSeverity.Warning, cat.Name, cmd.Label, "trust level not stated"));

                var inferred = TrustInference.Infer(cmd.Command);
                if (inferred > cmd.TrustLevel)
                    issues.Add(new(CommandAuditSeverity.Warning, cat.Name, cmd.Label,
                        $"marked {cmd.TrustLevel.ToYaml()} but matches a {inferred.ToYaml()} pattern"));

                if (cmd.Command.Contains("<PASSWORD>", StringComparison.OrdinalIgnoreCase) && !cmd.Command.Contains("'<PASSWORD>'"))
                    issues.Add(new(CommandAuditSeverity.Info, cat.Name, cmd.Label, "password placeholder is bare; it will be single-quoted at run time"));
            }
        }
        return issues;
    }

    public const string BundledResourceName = "FleetMate.Core.Resources.Manage.commands.windows.yaml";

    /// <summary>The raw YAML of the library that ships inside the assembly.</summary>
    public static string BundledYaml()
    {
        using var stream = typeof(CommandLibrary).Assembly.GetManifestResourceStream(BundledResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {BundledResourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The library that ships with FleetMate: the Windows counterpart of the
    /// macOS lab-operations library, with a trust level on every command.
    /// Falls back to the minimal built-in set if the resource cannot be read.
    /// </summary>
    public static List<CommandCategory> LoadBundled()
    {
        try
        {
            var parsed = Parse(BundledYaml());
            return parsed.Count == 0 ? DefaultCategories() : parsed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bundled command library did not load; using the minimal defaults");
            return DefaultCategories();
        }
    }

    /// <summary>The minimal library used when nothing else is available.</summary>
    public static List<CommandCategory> DefaultCategories() => new()
    {
        new CommandCategory("System", new[]
        {
            new ManagedCommand("Hostname", "hostname"),
            new ManagedCommand("Windows version", "(Get-CimInstance Win32_OperatingSystem) | Select-Object Caption, Version, BuildNumber | Format-List"),
            new ManagedCommand("Uptime", "(Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime | Select-Object Days, Hours, Minutes | Format-List"),
            new ManagedCommand("Serial number", "(Get-CimInstance Win32_BIOS).SerialNumber"),
            new ManagedCommand("RAM (GB)", "[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)"),
            new ManagedCommand("Disk usage (C:)", "Get-PSDrive C | Select-Object @{n='UsedGB';e={[math]::Round($_.Used/1GB,1)}}, @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Format-List"),
        }),
        new CommandCategory("Cimian Operations", new[]
        {
            new ManagedCommand("Cimian check only", "managedsoftwareupdate --checkonly"),
            new ManagedCommand("Cimian auto", "managedsoftwareupdate --auto", CommandTrustLevel.Caution),
            new ManagedCommand("Cimian ClientIdentifier", "Get-Content 'C:\\ProgramData\\ManagedInstalls\\Config.yaml' | Select-String '^\\s*ClientIdentifier'"),
        })
    };

    private static ManagedCommand Clone(ManagedCommand c) => new(c.Label, c.Command, c.TrustLevel, c.TrustWasExplicit);

    private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    private static bool TryGet(YamlMappingNode node, string key, out YamlNode value)
    {
        foreach (var (k, v) in node.Children)
        {
            if (k is YamlScalarNode { Value: { } kv } && kv == key)
            {
                value = v;
                return true;
            }
        }
        value = null!;
        return false;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        TryGet(node, key, out var v) && v is YamlScalarNode s ? s.Value : null;

    /// <summary>
    /// Single-quote when the value has anything YAML could misread; a
    /// single-quoted YAML scalar escapes only the quote itself, by doubling.
    /// </summary>
    internal static string Quote(string s)
    {
        var needs = s.Length == 0
            || s.IndexOfAny(new[] { ':', '#', '\'', '"', '|', '>', '{', '}', '[', ']', ',', '&', '*', '!', '%', '@', '`', '\n', '\r', '\t' }) >= 0
            || s.StartsWith(' ') || s.EndsWith(' ') || s.StartsWith('-') || s.StartsWith('?')
            || s is "true" or "false" or "null" or "yes" or "no" or "~"
            || double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);
        if (!needs) return s;
        return "'" + s.Replace("'", "''") + "'";
    }
}
