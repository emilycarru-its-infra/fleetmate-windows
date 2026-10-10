using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FleetMate.Core.Services.Agent;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// Keeps codex and claude current in the background: a check shortly after
/// launch, another whenever six hours have passed, and one before an agent
/// session starts if the last is that old. Updates run with the tool that
/// installed each CLI, never interactively, so a session opens straight to
/// the agent instead of on its updater. Results go to the app log and to a
/// neutral status line in Settings › Terminal.
/// </summary>
public sealed class AgentCliUpdateModel : INotifyPropertyChanged
{
    private readonly Func<bool> _isEnabled;
    private readonly AgentCliUpdater _updater = new();
    private CancellationTokenSource? _loop;
    private AgentCliUpdateState _state = AgentCliUpdateState.Load();
    private bool _isRunning;

    public AgentCliUpdateModel(Func<bool> isEnabled) => _isEnabled = isEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentCliUpdateState State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnPropertyChanged(); }
    }

    /// <summary>The Settings switch, on unless turned off.</summary>
    public bool IsEnabled => _isEnabled();

    /// <summary>Begin the launch check and the schedule.</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // Let launch settle before spawning npm or winget.
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                while (!token.IsCancellationRequested)
                {
                    UpdateIfStale();
                    await Task.Delay(TimeSpan.FromMinutes(30), token);
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    /// <summary>Update in the background if enabled and the last run is old enough. Any thread.</summary>
    public void UpdateIfStale()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsEnabled && !IsRunning && State.IsStale()) _ = RunAsync(checkOnly: false);
        });
    }

    /// <summary>Check (and, unless <paramref name="checkOnly"/>, update) now. UI thread.</summary>
    public async Task RunAsync(bool checkOnly)
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            var previous = State;
            var next = await Task.Run(() => _updater.RunAsync(checkOnly, previous));
            next.Save();
            State = next;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
