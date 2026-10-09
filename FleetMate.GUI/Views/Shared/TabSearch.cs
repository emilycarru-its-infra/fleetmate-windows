using System.Windows.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The list filter a tab hands to the toolbar search field, so each tab shows
/// one search field instead of its own filter box beside the global one.
/// </summary>
/// <param name="Box">
/// The tab's own filter box, kept but collapsed. The toolbar field writes its
/// text here, so the tab filters exactly as it did, and reads it back when the
/// tab changes it (a cleared filter, a deep link).
/// </param>
/// <param name="Prompt">The field's placeholder while it filters this tab.</param>
/// <param name="Submit">What Enter does, for a filter that runs on Enter rather than as you type.</param>
public sealed record TabSearchScope(TextBox Box, string Prompt, Action? Submit = null);

/// <summary>A page whose list the toolbar search field can filter.</summary>
public interface ITabSearch
{
    /// <summary>The filter for what the tab shows now, or null when it has none.</summary>
    TabSearchScope? SearchScope { get; }

    /// <summary>Raised when <see cref="SearchScope"/> changes — a segment or view switch.</summary>
    event EventHandler? SearchScopeChanged;
}

/// <summary>Which search the toolbar field runs.</summary>
public enum ToolbarSearchMode
{
    /// <summary>Filter the current tab's list.</summary>
    Tab,

    /// <summary>Search everything.</summary>
    All,
}

/// <summary>The toolbar field's scope rules, kept apart from WPF so they can be tested.</summary>
public static class ToolbarSearchScopes
{
    /// <summary>The scope a tab opens in: its own filter when it has one, otherwise everything.</summary>
    public static ToolbarSearchMode ForTab(bool tabHasFilter) =>
        tabHasFilter ? ToolbarSearchMode.Tab : ToolbarSearchMode.All;

    /// <summary>Ctrl+F filters the tab; a tab with no list filter can only search everything.</summary>
    public static ToolbarSearchMode ForFind(bool tabHasFilter) => ForTab(tabHasFilter);

    /// <summary>The chip's click: the other scope, or everything when the tab has no filter.</summary>
    public static ToolbarSearchMode Toggle(ToolbarSearchMode current, bool tabHasFilter) =>
        tabHasFilter && current == ToolbarSearchMode.All ? ToolbarSearchMode.Tab : ToolbarSearchMode.All;

    /// <summary>The chip's label: the tab's name while filtering it, "All" while searching everything.</summary>
    public static string ChipLabel(ToolbarSearchMode mode, string tabName) =>
        mode == ToolbarSearchMode.Tab ? tabName : "All";

    /// <summary>The field's placeholder.</summary>
    public static string Placeholder(ToolbarSearchMode mode, string? tabPrompt) =>
        mode == ToolbarSearchMode.Tab && !string.IsNullOrWhiteSpace(tabPrompt) ? tabPrompt! : "Search everything";
}
