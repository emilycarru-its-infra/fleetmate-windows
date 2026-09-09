using System.Text.Json.Serialization;

namespace FleetMate.Core.Models;

/// <summary>
/// Why a SecureShell call ended the way it did. Distinguishes the cases an
/// operator treats differently: a host that is off, a host that refused the
/// key, a command that ran but failed, and a run that was stopped.
/// </summary>
public enum SecureShellOutcome
{
    /// <summary>Connected, command ran, exit code 0.</summary>
    Success,
    /// <summary>Connected, command ran, non-zero exit code.</summary>
    CommandFailed,
    /// <summary>No TCP connection: host down, port closed, DNS failure, or connect timeout.</summary>
    Unreachable,
    /// <summary>Connected, but the command exceeded its timeout.</summary>
    Timeout,
    /// <summary>Connected, but the server rejected the key or username.</summary>
    AuthFailed,
    /// <summary>Connected, but the host key did not match and could not be cleaned.</summary>
    HostKeyRejected,
    /// <summary>The caller cancelled the run.</summary>
    Cancelled,
    /// <summary>Anything else.</summary>
    Error
}

/// <summary>
/// Result of a SecureShell command execution
/// </summary>
public class SecureShellResult
{
    /// <summary>
    /// Classified outcome; set by the service from the exit code and any exception.
    /// </summary>
    public SecureShellOutcome Outcome { get; set; } = SecureShellOutcome.Error;

    /// <summary>
    /// Target host (IP address or hostname)
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Device name if resolved from ReportMate
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Username used for connection
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Command that was executed
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Exit code from the command (0 = success)
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Standard output from the command
    /// </summary>
    public string Stdout { get; set; } = string.Empty;

    /// <summary>
    /// Standard error from the command
    /// </summary>
    public string Stderr { get; set; } = string.Empty;

    /// <summary>
    /// Duration of command execution
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Timestamp when command started
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Exception if connection or command failed
    /// </summary>
    [JsonIgnore]
    public Exception? Error { get; set; }

    /// <summary>
    /// Error message if Error is set
    /// </summary>
    public string? ErrorMessage => Error?.Message;

    /// <summary>
    /// Whether the command executed successfully (exit code 0 and no error)
    /// </summary>
    [JsonIgnore]
    public bool Success => ExitCode == 0 && Error == null;

    /// <summary>
    /// Whether the connection was established (may have failed command)
    /// </summary>
    public bool Connected { get; set; }
}

/// <summary>
/// Result of batch SecureShell execution
/// </summary>
public class SecureShellBatchResult
{
    /// <summary>
    /// Individual results for each host
    /// </summary>
    public List<SecureShellResult> Results { get; set; } = new();

    /// <summary>
    /// Total execution time for all hosts
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Number of successful executions
    /// </summary>
    public int SuccessCount => Results.Count(r => r.Success);

    /// <summary>
    /// Number of failed executions
    /// </summary>
    public int FailedCount => Results.Count(r => !r.Success);

    /// <summary>
    /// Total hosts processed
    /// </summary>
    public int TotalCount => Results.Count;
}

/// <summary>
/// Result of SecureShell connection test
/// </summary>
public class SecureShellTestResult
{
    public string Host { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ServerVersion { get; set; }
}
