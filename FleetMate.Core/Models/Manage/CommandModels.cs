namespace FleetMate.Core.Models.Manage;

/// <summary>
/// How much a command can hurt. Safe commands read state; caution commands
/// change state or affect a signed-in user; destructive commands delete data,
/// restart machines, reset services, or reach the whole fleet.
/// </summary>
public enum CommandTrustLevel
{
    Safe,
    Caution,
    Destructive
}

public static class CommandTrustLevelExtensions
{
    public static string Label(this CommandTrustLevel level) => level switch
    {
        CommandTrustLevel.Safe => "Safe",
        CommandTrustLevel.Caution => "Caution",
        CommandTrustLevel.Destructive => "Destructive",
        _ => level.ToString()
    };

    public static string WarningTitle(this CommandTrustLevel level) => level switch
    {
        CommandTrustLevel.Caution => "Run caution command?",
        CommandTrustLevel.Destructive => "Run destructive command?",
        _ => "Run command?"
    };

    public static string WarningMessage(this CommandTrustLevel level) => level switch
    {
        CommandTrustLevel.Caution =>
            "This command may change machine state or affect signed-in users. Test on a small target set first.",
        CommandTrustLevel.Destructive =>
            "This command can delete data, restart machines, reset services, or make fleet-wide changes. Confirm your target set before running it.",
        _ => "This command is marked safe."
    };

    /// <summary>YAML value: lower-case name.</summary>
    public static string ToYaml(this CommandTrustLevel level) => level.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out CommandTrustLevel level)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "safe": level = CommandTrustLevel.Safe; return true;
            case "caution": level = CommandTrustLevel.Caution; return true;
            case "destructive": level = CommandTrustLevel.Destructive; return true;
            default: level = CommandTrustLevel.Safe; return false;
        }
    }
}

/// <summary>
/// Pattern-based trust for commands with no explicit trust metadata: custom
/// commands typed in the runner, and older libraries. Patterns are Windows
/// and PowerShell idioms; the match is case-insensitive substring.
/// </summary>
public static class TrustInference
{
    internal static readonly string[] DestructivePatterns =
    {
        "remove-item -recurse", "remove-item -r ", "-recurse -force", "rm -r", "rd /s", "rmdir /s", "del /s", "del /q",
        "format-volume", "clear-disk", "remove-partition", "diskpart", "cipher /w",
        "shutdown /r", "shutdown /s", "shutdown -r", "shutdown -s", "restart-computer", "stop-computer",
        "remove-localuser", "net user", "/delete",
        "manage-bde -off", "disable-bitlocker", "bcdedit",
        "reg delete", "remove-itemproperty",
        "clear-eventlog", "wevtutil cl",
        "remove-appxpackage", "uninstall-package", "msiexec /x", "msiexec /uninstall",
        "dsregcmd /leave", "remove-computer",
        "taskkill /f", "stop-process -force", "stop-process -f",
        "remove-printer",
        "managedsoftwareupdate --uninstall", "cimian uninstall"
    };

    internal static readonly string[] CautionPatterns =
    {
        "managedsoftwareupdate --installonly", "managedsoftwareupdate --auto", "managedsoftwareupdate -a",
        "managedsoftwareupdate --checkonly --force",
        "restart-service", "stop-service", "start-service", "set-service",
        "usoclient", "wuauclt", "install-windowsupdate", "get-windowsupdate -install",
        "gpupdate", "logoff", "tsdiscon", "rundll32 user32.dll,lockworkstation",
        "setsuspendstate", "powercfg /h", "powercfg -h",
        "set-itemproperty", "reg add", "new-itemproperty",
        "new-localuser", "add-localgroupmember", "remove-localgroupmember",
        "netsh", "set-dnsclientserveraddress", "restart-netadapter", "disable-netadapter", "enable-netadapter",
        "set-timezone", "w32tm /resync",
        "add-printer", "set-printconfiguration",
        "rum.exe", "remoteupdatemanager", "--action=install",
        "startset", "outset",
        "schtasks /run", "start-scheduledtask", "start-process"
    };

    public static CommandTrustLevel Infer(string command)
    {
        var lower = (command ?? "").ToLowerInvariant();
        if (DestructivePatterns.Any(lower.Contains)) return CommandTrustLevel.Destructive;
        if (CautionPatterns.Any(lower.Contains)) return CommandTrustLevel.Caution;
        return CommandTrustLevel.Safe;
    }
}

/// <summary>One entry of the command library.</summary>
public class ManagedCommand
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";
    public CommandTrustLevel TrustLevel { get; set; } = CommandTrustLevel.Safe;
    /// <summary>True when the library file stated the trust level rather than it being inferred.</summary>
    public bool TrustWasExplicit { get; set; }

    public ManagedCommand() { }

    public ManagedCommand(string label, string command, CommandTrustLevel trustLevel = CommandTrustLevel.Safe, bool trustWasExplicit = true)
    {
        Label = label;
        Command = command;
        TrustLevel = trustLevel;
        TrustWasExplicit = trustWasExplicit;
    }
}

public class CommandCategory
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<ManagedCommand> Commands { get; set; } = new();

    public CommandCategory() { }

    public CommandCategory(string name, IEnumerable<ManagedCommand>? commands = null)
    {
        Name = name;
        if (commands != null) Commands = commands.ToList();
    }
}

/// <summary>A command the operator ran, kept for recall and rerun.</summary>
public class CommandHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;

    public CommandHistoryEntry() { }

    public CommandHistoryEntry(string label, string command)
    {
        Label = label;
        Command = command;
    }
}

public enum CommandAuditSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>A finding from the static library audit.</summary>
public record CommandAuditIssue(CommandAuditSeverity Severity, string Category, string Label, string Message);
