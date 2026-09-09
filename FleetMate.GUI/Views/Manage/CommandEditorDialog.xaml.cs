using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Manage;

namespace FleetMate.GUI.Views.Manage;

/// <summary>Add or edit one library command, including its category and trust level.</summary>
public partial class CommandEditorDialog : Window
{
    private const string NewCategoryTag = "__new__";
    private readonly IReadOnlyList<CommandCategory> _categories;
    private readonly ManagedCommand? _existing;
    private bool _trustTouched;

    public enum Outcome { Cancelled, Saved, Deleted }

    public Outcome Result { get; private set; } = Outcome.Cancelled;
    public CommandCategory? ResultCategory { get; private set; }
    public string ResultNewCategoryName { get; private set; } = "";
    public string ResultLabel { get; private set; } = "";
    public string ResultCommand { get; private set; } = "";
    public CommandTrustLevel ResultTrust { get; private set; } = CommandTrustLevel.Safe;

    public CommandEditorDialog(IReadOnlyList<CommandCategory> categories, CommandCategory? category, ManagedCommand? existing, string? initialCommand = null)
    {
        InitializeComponent();
        _categories = categories;
        _existing = existing;
        Title = existing == null ? "Add command" : "Edit command";

        foreach (var c in categories) CategoryCombo.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c });
        CategoryCombo.Items.Add(new ComboBoxItem { Content = "New category...", Tag = NewCategoryTag });
        var index = category != null ? categories.ToList().IndexOf(category) : -1;
        CategoryCombo.SelectedIndex = index >= 0 ? index : (categories.Count > 0 ? 0 : CategoryCombo.Items.Count - 1);

        if (existing != null)
        {
            LabelBox.Text = existing.Label;
            CommandBox.Text = existing.Command;
            SelectTrust(existing.TrustLevel);
            _trustTouched = true;
            DeleteButton.Visibility = Visibility.Visible;
        }
        else
        {
            CommandBox.Text = initialCommand ?? "";
            SelectTrust(TrustInference.Infer(CommandBox.Text));
        }
        TrustCombo.SelectionChanged += (_, _) => _trustTouched = true;
        Loaded += (_, _) => LabelBox.Focus();
    }

    private void SelectTrust(CommandTrustLevel level) => TrustCombo.SelectedIndex = (int)level;

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        var isNew = CategoryCombo.SelectedItem is ComboBoxItem { Tag: NewCategoryTag };
        if (NewCategoryBox != null) NewCategoryBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCommandChanged(object sender, TextChangedEventArgs e)
    {
        // Follow the inferred trust until the operator picks one explicitly.
        if (!_trustTouched && _existing == null)
        {
            var inferred = TrustInference.Infer(CommandBox.Text);
            if (TrustCombo.SelectedIndex != (int)inferred)
            {
                var was = _trustTouched;
                SelectTrust(inferred);
                _trustTouched = was;
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var label = LabelBox.Text.Trim();
        var command = CommandBox.Text.Trim();
        if (label.Length == 0) { HintText.Text = "Give the command a label."; LabelBox.Focus(); return; }
        if (command.Length == 0) { HintText.Text = "Enter the command to run."; CommandBox.Focus(); return; }

        if (CategoryCombo.SelectedItem is ComboBoxItem { Tag: CommandCategory cat })
        {
            ResultCategory = cat;
        }
        else
        {
            var name = NewCategoryBox.Text.Trim();
            if (name.Length == 0) { HintText.Text = "Name the new category."; NewCategoryBox.Focus(); return; }
            ResultNewCategoryName = name;
        }

        ResultLabel = label;
        ResultCommand = command;
        ResultTrust = (CommandTrustLevel)Math.Max(0, TrustCombo.SelectedIndex);
        Result = Outcome.Saved;
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Delete \"{_existing?.Label}\" from the library?", "Delete command", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        Result = Outcome.Deleted;
        DialogResult = true;
    }
}
