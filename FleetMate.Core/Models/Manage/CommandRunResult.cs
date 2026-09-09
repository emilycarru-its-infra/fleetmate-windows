namespace FleetMate.Core.Models.Manage;

public enum CommandRunStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Offline,
    Timeout,
    AuthFailed,
    Cancelled
}

public static class CommandRunStatusExtensions
{
    public static bool IsTerminal(this CommandRunStatus status) =>
        status is not (CommandRunStatus.Pending or CommandRunStatus.Running);

    public static string Label(this CommandRunStatus status, int? exitCode = null) => status switch
    {
        CommandRunStatus.Pending => "Pending",
        CommandRunStatus.Running => "Running",
        CommandRunStatus.Success => "Success",
        CommandRunStatus.Failed => exitCode.HasValue ? $"Exit {exitCode}" : "Failed",
        CommandRunStatus.Offline => "Offline",
        CommandRunStatus.Timeout => "Timeout",
        CommandRunStatus.AuthFailed => "SSH auth failed",
        CommandRunStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };
}

/// <summary>Per-host result of one fleet command run.</summary>
public class CommandRunResult
{
    public Guid Id { get; } = Guid.NewGuid();
    public RosterComputer Computer { get; }
    public string Ip { get; }
    public CommandRunStatus Status { get; set; } = CommandRunStatus.Pending;
    public string Output { get; set; } = "";
    public string ErrorOutput { get; set; } = "";
    public int? ExitCode { get; set; }
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }

    public CommandRunResult(RosterComputer computer, string ip, CommandRunStatus status = CommandRunStatus.Pending)
    {
        Computer = computer;
        Ip = ip;
        Status = status;
    }

    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;

    public string StatusLabel => Status.Label(ExitCode);

    /// <summary>Plain-text block for copy and hand-off.</summary>
    public string Formatted()
    {
        var lines = new List<string>
        {
            $"{Computer.DisplayName} ({Ip}) - {StatusLabel.ToLowerInvariant()}"
        };
        if (!string.IsNullOrWhiteSpace(Output)) lines.Add(Output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(ErrorOutput))
        {
            lines.Add("stderr:");
            lines.Add(ErrorOutput.TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }
}
