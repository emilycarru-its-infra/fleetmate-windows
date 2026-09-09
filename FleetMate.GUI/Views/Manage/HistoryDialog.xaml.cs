using System.Windows;
using System.Windows.Input;
using FleetMate.Core.Models.Manage;

namespace FleetMate.GUI.Views.Manage;

/// <summary>Recall a previous command: load it into the runner, or rerun it as-is.</summary>
public partial class HistoryDialog : Window
{
    public enum Outcome { None, Load, Rerun, Cleared }

    public Outcome Result { get; private set; } = Outcome.None;
    public CommandHistoryEntry? Chosen { get; private set; }

    public HistoryDialog(IReadOnlyList<CommandHistoryEntry> history)
    {
        InitializeComponent();
        HistoryList.ItemsSource = history;
        if (history.Count > 0) HistoryList.SelectedIndex = 0;
        RerunButton.IsEnabled = history.Count > 0;
    }

    private void Finish(Outcome outcome)
    {
        Chosen = HistoryList.SelectedItem as CommandHistoryEntry;
        if (Chosen == null && outcome != Outcome.Cleared) return;
        Result = outcome;
        DialogResult = true;
    }

    private void OnLoad(object sender, RoutedEventArgs e) => Finish(Outcome.Load);
    private void OnRerun(object sender, RoutedEventArgs e) => Finish(Outcome.Rerun);
    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Finish(Outcome.Load);

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Clear the command history?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        Result = Outcome.Cleared;
        DialogResult = true;
    }
}
