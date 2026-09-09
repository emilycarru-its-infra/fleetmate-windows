using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Models.Manage;

namespace FleetMate.GUI.ViewModels.Manage;

/// <summary>One machine's result in the results pane; output accumulates as it streams.</summary>
public partial class CommandResultViewModel : ObservableObject
{
    private readonly StringBuilder _buffer = new();

    public RosterComputer Computer { get; }
    public string Ip { get; }
    public DateTime StartTime { get; } = DateTime.Now;

    [ObservableProperty] private CommandRunStatus _status = CommandRunStatus.Pending;
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private string _errorOutput = "";
    [ObservableProperty] private DateTime? _endTime;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _output = "";

    public CommandResultViewModel(RosterComputer computer, string ip)
    {
        Computer = computer;
        Ip = ip;
    }

    public string Serial => Computer.Serial;
    public string Name => Computer.FriendlyName;
    public string Hostname => Computer.Hostname;
    public string StatusLabel => Status.Label(ExitCode);
    public bool IsTerminal => Status.IsTerminal();
    public bool HasOutput => Output.Length > 0;
    public bool HasError => ErrorOutput.Length > 0;

    public string StatusKey => Status switch
    {
        CommandRunStatus.Success => "success",
        CommandRunStatus.Failed or CommandRunStatus.AuthFailed => "failed",
        CommandRunStatus.Offline => "offline",
        CommandRunStatus.Timeout => "timeout",
        CommandRunStatus.Running => "running",
        CommandRunStatus.Cancelled => "cancelled",
        _ => "pending"
    };

    public string DurationLabel => EndTime.HasValue
        ? $"{(EndTime.Value - StartTime).TotalSeconds:0.0}s"
        : Status == CommandRunStatus.Running ? "running..." : "";

    /// <summary>First line of output, for the collapsed row.</summary>
    public string Preview
    {
        get
        {
            var text = Output.Length > 0 ? Output : ErrorOutput;
            var firstLine = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            return firstLine.Length > 160 ? firstLine[..160] + "..." : firstLine;
        }
    }

    public void AppendOutput(string chunk)
    {
        if (chunk.Length == 0) return;
        _buffer.Append(chunk);
        Output = _buffer.ToString();
    }

    public string Formatted()
    {
        var lines = new List<string> { $"{Name} ({Ip}) - {StatusLabel.ToLowerInvariant()}" };
        if (HasOutput) lines.Add(Output.TrimEnd());
        if (HasError)
        {
            lines.Add("stderr:");
            lines.Add(ErrorOutput.TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }

    partial void OnStatusChanged(CommandRunStatus value) => Raise();
    partial void OnExitCodeChanged(int? value) => Raise();
    partial void OnEndTimeChanged(DateTime? value) => Raise();
    partial void OnOutputChanged(string value) { OnPropertyChanged(nameof(HasOutput)); OnPropertyChanged(nameof(Preview)); }
    partial void OnErrorOutputChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(Preview)); }

    private void Raise()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusKey));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(DurationLabel));
    }
}
