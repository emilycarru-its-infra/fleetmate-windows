using FleetMate.Core.Services.Repos;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Development.Repos;

// The rows the Repos lists show. Text comes from Core's RepoWorkspaceText so the
// WPF and WinUI apps read the same; these add only what XAML binds to. Each
// row's ToString is what a screen reader says for its list item.

/// <summary>A host heading in the grouped sidebar: Azure DevOps, GitHub, Other.</summary>
public sealed record SidebarSectionRow(string Id, string Title, int Count, bool IsExpanded)
{
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public string CountText => Count.ToString("N0");
    public override string ToString() => $"{Title}, {Count} {(Count == 1 ? "repository" : "repositories")}, {(IsExpanded ? "expanded" : "collapsed")}";
}

/// <summary>A project (Azure DevOps) or owner (GitHub) folder in the grouped sidebar.</summary>
public sealed record SidebarGroupRow(string Id, string Title, string Tooltip, bool IsExpanded)
{
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C";
    public override string ToString() => $"{Title}, {(IsExpanded ? "expanded" : "collapsed")}";
}

/// <summary>One tracked repository: name, then branch, sync and change state.</summary>
public sealed record SidebarRepoRow(RepoRecord Record, RepoStatus? Status, bool IsFetching, bool ShowScope, int Depth, bool IsEditing = false)
{
    public bool IsHidden => Record.IsHidden;
    public string Id => Record.Id;
    public string Name => Record.Key.Name;
    public string Scope => Record.Key.Scope;
    public bool IsLocal => Record.Local != null;
    public bool HasError => Status?.Error != null;
    /// <summary>A hidden repository, listed only while editing, is dimmed.</summary>
    public double RowOpacity => IsHidden ? 0.55 : 1.0;
    public string EyeGlyph => IsHidden ? "\uED1A" : "\uE890";
    public string HideLabel => IsHidden ? "Show in Sidebar" : "Hide from Sidebar";
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Tooltip => RepoWorkspaceText.SidebarTooltip(Record, Status);
    public string Metadata => RepoWorkspaceText.SidebarMetadata(Record, Status);
    public Brush MetadataBrush => HasError ? RepoBrushes.Warning : RepoBrushes.Secondary;
    public Visibility ScopeVisibility => DevelopmentUi.Show(ShowScope);
    public Visibility EditVisibility => DevelopmentUi.Show(IsEditing);
    public override string ToString() => $"{Record.Key.DisplayName}, {Metadata}{(IsHidden ? ", hidden" : "")}";
}

/// <summary>Picks the sidebar row template by the row's type.</summary>
public sealed partial class SidebarRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Section { get; set; }
    public DataTemplate? Group { get; set; }
    public DataTemplate? Repository { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        SidebarSectionRow => Section,
        SidebarGroupRow => Group,
        _ => Repository,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>One change in the Changes list. A path with staged and unstaged edits has a row for each side.</summary>
public sealed record ChangeRow(GitStatusEntry Entry, string Root)
{
    public string Id => Entry.Id;
    public bool Staged => Entry.Staged;
    /// <summary>The stage box's state; a CheckBox binds to a nullable bool.</summary>
    public bool? StagedState => Entry.Staged;
    public string Letter => Entry.Letter;
    public string FileName => Path.GetFileName(Entry.RelativePath);
    public string Folder => RepoWorkspaceText.ChangeFolder(Entry);
    public string Side => Entry.Staged ? "staged" : "";
    public string StageLabel => Entry.Staged ? "Unstage" : "Stage";
    public string StageHint => $"{StageLabel} {Entry.RelativePath}";
    public bool CanDiscard => !Entry.Staged;
    public string Tooltip => RepoWorkspaceText.ChangeTooltip(Entry);
    public string AbsolutePath => Path.Combine(Root, Entry.RelativePath.Replace('/', '\\'));

    public Brush LetterBrush => Entry.Kind switch
    {
        GitEntryKind.Added or GitEntryKind.Untracked => RepoBrushes.Added,
        GitEntryKind.Deleted or GitEntryKind.Conflicted => RepoBrushes.Removed,
        GitEntryKind.Renamed or GitEntryKind.Copied => RepoBrushes.Series(3),
        _ => RepoBrushes.Series(0),
    };

    public override string ToString() => $"{Entry.RelativePath}, {Entry.Kind}{(Staged ? ", staged" : "")}";
}

/// <summary>A branch, tag or HEAD badge on a commit.</summary>
public sealed record RefBadge(string Name, Brush Background, Brush Border, Brush Foreground);

/// <summary>One commit in History, with its slice of the lane graph.</summary>
public sealed record HistoryRow(GitCommit Commit, GraphRow? Graph, int LaneCount)
{
    public string Sha => Commit.Sha;
    public string Subject => Commit.Subject;
    public string Byline => RepoWorkspaceText.CommitByline(Commit);

    public IReadOnlyList<RefBadge> Refs => Commit.Refs
        .Where(r => r.Kind != GitRefKind.Head)
        .Select(r => r.Kind switch
        {
            GitRefKind.Tag => new RefBadge(r.Name, Transparent, RepoBrushes.Series(3), RepoBrushes.Text),
            GitRefKind.RemoteBranch => new RefBadge(r.Name, Transparent, RepoBrushes.Secondary, RepoBrushes.Secondary),
            _ => new RefBadge(RepoWorkspaceText.RefLabel(r), RepoBrushes.Accent, RepoBrushes.Accent, RepoBrushes.OnAccent),
        })
        .ToList();

    public Visibility RefsVisibility => DevelopmentUi.Show(Commit.Refs.Any(r => r.Kind != GitRefKind.Head));

    private static Brush Transparent => new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public override string ToString() => $"{Subject}, {Byline}";
}

/// <summary>One visible line of the file tree.</summary>
public sealed record FileRow(RepoFileRow Row, bool IsUnsaved)
{
    public string Path => Row.Node.Path;
    public string Name => Row.Node.Name;
    public bool IsFolder => Row.Node.IsFolder;
    public Thickness Indent => new(Row.Depth * 14, 0, 0, 0);
    public string Chevron => IsFolder ? (Row.IsExpanded ? "\uE70D" : "\uE76C") : "";
    public string Icon => IsFolder ? "\uE8B7" : "\uE8A5";
    public Brush IconBrush => IsFolder ? RepoBrushes.Accent : RepoBrushes.Secondary;
    public Visibility UnsavedVisibility => DevelopmentUi.Show(IsUnsaved);

    public override string ToString() =>
        IsFolder ? $"{Name} folder, {(Row.IsExpanded ? "expanded" : "collapsed")}" : Name + (IsUnsaved ? ", unsaved edits" : "");
}

/// <summary>One content-search match, grouped under its file.</summary>
public sealed record GrepRow(RepoGrepMatch Match)
{
    public string Path => Match.Path;
    public string LineLabel => Match.Line.ToString();
    public string Text => Match.Text.Trim();
    public override string ToString() => $"{Match.Path} line {Match.Line}: {Text}";
}

/// <summary>A file's content-search matches, the grouped list's header.</summary>
public sealed partial class GrepGroup : List<GrepRow>
{
    public GrepGroup(string path, IEnumerable<GrepRow> rows) : base(rows) => Path = path;
    public string Path { get; }
    public string Header => $"{Path}  ({Count})";
}
