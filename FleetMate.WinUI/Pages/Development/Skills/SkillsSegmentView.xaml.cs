using FleetMate.Core.Development;
using FleetMate.Core.Knowledge;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Skills;

/// <summary>
/// Development › Skills: the shared skills, hooks and standards every
/// repository inherits, and this PC's own, so what the agents are told to do is
/// visible (macOS parity). The catalog and its grouping are FleetMate.Core's.
/// </summary>
public sealed partial class SkillsSegmentView : UserControl, IDevelopmentSegmentView
{
    private string _search = "";
    private bool _shown;
    private bool _loadedLocal;
    private string? _selectedId;

    public SkillsSegmentView() => InitializeComponent();

    public DevelopmentSegment Segment => DevelopmentSegment.Skills;
    public bool IsBusy => false;
    public event EventHandler? BusyChanged { add { } remove { } }

    public void Attach(DevelopmentPage page)
    {
        if (App.Services.Skills is { } store)
            // Raised on the thread pool.
            store.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (_shown) Render(); });
    }

    public void SetShown(bool shown)
    {
        _shown = shown;
        if (!shown) return;
        // Local skills change as the person edits them: reread on first view.
        if (!_loadedLocal && !App.Services.DemoMode && App.Services.Skills is { } store)
        {
            _loadedLocal = true;
            _ = store.ReloadLocalAsync();
        }
        Render();
    }

    public void ApplySearch(string text)
    {
        _search = text;
        if (_shown) Render();
    }

    public Task RefreshAsync() =>
        App.Services.DemoMode || App.Services.Skills is not { } store ? Task.CompletedTask : store.ReloadLocalAsync();

    private void Render()
    {
        if (App.Services.Skills is not { } store)
        {
            StatusText.Text = "Skills are unavailable.";
            return;
        }

        var groups = SkillCatalog.Grouped(store.Entries, _search)
            .Select(g => new SkillGroup(g.Entries[0].Group, g.Entries))
            .ToList();
        GroupedSkills.Source = groups;
        List.ItemsSource = GroupedSkills.View;
        var rows = groups.SelectMany(g => g).ToList();
        if (_selectedId != null) List.SelectedItem = rows.FirstOrDefault(r => r.Id == _selectedId);

        StatusText.Text = store.IsSyncing ? "Fetching the shared agents…"
            : store.SyncError is { } error ? $"Shared agents unavailable: {error}"
            : store.SyncedAt is { } at ? $"Up to date with main · {at:t}"
            : store.IsHubConfigured ? "Fetching the shared agents…"
            : "Showing this PC's skills. Set the Skills repository in Settings › Authentication to add the shared ones.";

        EmptyText.Visibility = DevelopmentUi.Show(rows.Count == 0);
        EmptyText.Text = store.Entries.Count == 0 ? "No skills yet. They appear once the first fetch finishes." : "Nothing matches this filter.";
    }

    /// <summary>Select the first entry: the QA capture's detail view.</summary>
    public void SelectFirst()
    {
        if (GroupedSkills.Source is IEnumerable<SkillGroup> groups && groups.SelectMany(g => g).FirstOrDefault() is { } first)
            List.SelectedItem = first;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not SkillEntry entry) return;
        _selectedId = entry.Id;
        Placeholder.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;

        NameText.Text = entry.Name;
        var kind = entry.Kind switch { SkillKind.Skill => "Skill", SkillKind.Hook => "Hook", _ => "Standard" };
        KindText.Text = entry.Origin == SkillOrigin.Local ? $"{kind} · this PC" : kind;
        PathText.Text = entry.Path;
        SummaryText.Text = entry.Summary;
        SummaryText.Visibility = DevelopmentUi.Show(entry.Summary.Length > 0);
        FilesText.Text = entry.Files.Count > 0 ? "Ships with " + string.Join(", ", entry.Files) : "";
        FilesText.Visibility = DevelopmentUi.Show(entry.Files.Count > 0);
        InvocationText.Text = $"In an agent session: /{entry.Name}";
        InvocationText.Visibility = DevelopmentUi.Show(entry.Kind == SkillKind.Skill);
        Body.Show(entry.Body, 14);
        DevelopmentContext.Select("skill", new[]
        {
            new FleetMate.Core.Services.Terminal.ContextSelection("skill", entry.Id,
                new Dictionary<string, string?> { ["name"] = entry.Name, ["path"] = entry.Path }),
        });
    }
}

/// <summary>A heading in the Skills list: "Skills · Shared with every repository".</summary>
public sealed class SkillGroup(string key, IEnumerable<SkillEntry> entries) : List<SkillEntry>(entries)
{
    public string Key { get; } = key;
}
