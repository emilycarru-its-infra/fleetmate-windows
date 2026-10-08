using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FleetMate.Core.Knowledge;

using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>
/// Development › Skills: the shared skills, hooks and standards every
/// repository inherits, and this PC's own, so what the agents are told to do
/// is visible (macOS parity).
/// </summary>
public partial class DevelopmentView
{
    private bool _skillsHooked;

    private void RenderSkills()
    {
        if (AppInstance?.Skills is not { } store) return;
        if (!_skillsHooked)
        {
            _skillsHooked = true;
            store.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (SkillsSegment.IsChecked == true) RenderSkills();
            });
            // Local skills change as the person edits them: reread on first view.
            _ = store.ReloadLocalAsync();
        }

        var selectedId = (SkillsList.SelectedItem as SkillEntry)?.Id;
        var groups = SkillCatalog.Grouped(store.Entries, SkillsSearchBox.Text);
        var rows = groups.SelectMany(g => g.Entries).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SkillEntry.Group)));
        SkillsList.ItemsSource = view;
        if (selectedId != null) SkillsList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);

        SkillsStatus.Text = store.IsSyncing ? "Fetching the shared agents…"
            : store.SyncError is { } error ? $"Shared agents unavailable: {error}"
            : store.SyncedAt is { } at ? $"Up to date with main · {at:t}"
            : store.IsHubConfigured ? "Fetching the shared agents…"
            : "Showing this PC's skills. Set the Skills repository in Settings › Authentication to add the shared ones.";

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = store.Entries.Count == 0 ? "No skills yet. They appear once the first fetch finishes." : "Nothing matches this filter.";
    }

    private void OnSkillsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) RenderSkills();
    }

    private void OnSkillSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SkillsList.SelectedItem is not SkillEntry entry) return;
        ShowDetail(SkillDetail);
        SkillName.Text = entry.Name;
        var kind = entry.Kind switch { SkillKind.Skill => "Skill", SkillKind.Hook => "Hook", _ => "Standard" };
        SkillKindLabel.Text = entry.Origin == SkillOrigin.Local ? $"{kind} · this PC" : kind;
        SkillPath.Text = entry.Path;
        SkillSummary.Text = entry.Summary;
        SkillSummary.Visibility = entry.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkillFiles.Text = entry.Files.Count > 0 ? "Ships with " + string.Join(", ", entry.Files) : "";
        SkillFiles.Visibility = entry.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkillInvocation.Text = $"In an agent session: /{entry.Name}";
        SkillInvocation.Visibility = entry.Kind == SkillKind.Skill ? Visibility.Visible : Visibility.Collapsed;
        SkillBody.Content = MarkdownDocument.Viewer(entry.Body, fontSize: 13);
    }
}
