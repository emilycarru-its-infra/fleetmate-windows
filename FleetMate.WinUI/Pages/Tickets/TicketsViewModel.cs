using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.WinUI.Shell;
using Serilog;

namespace FleetMate.WinUI.Pages.Tickets;

/// <summary>
/// The Tickets list and board on live data: the shared board cache, its
/// filters, search, sort, fold state and widgets, and why the list is empty
/// when it is. The rules are Core's, shared with FleetMate.GUI; this holds
/// the state and raises what changed. Create and use on the UI thread.
/// </summary>
public sealed partial class TicketsViewModel : ObservableObject, IDisposable
{
    private readonly ShellServices _services;
    private readonly HashSet<int> _collapsed = new();
    private bool _reloadAfterLoad;
    private bool _initialLoadDone;

    public TicketsViewModel(ShellServices services)
    {
        _services = services;
        Filters.Changed += OnFiltersChanged;
        _assignedToMe = UserPreferences.TicketsAssignedToMe;
        Cache.Changed += OnCacheChanged;
        Session.SignInChanged += OnSignInChanged;
        Session.StatusChanged += OnSessionStatusChanged;
        Session.LaunchStepChanged += OnLaunchStepChanged;
    }

    private TicketBoardCache Cache => _services.TicketCache;
    private TdxSessionCoordinator Session => _services.TdxSession;
    public TdxService? Tdx => _services.Tdx;

    /// <summary>Fictional tickets from <see cref="DemoTickets"/>; nothing is read from or sent to TeamDynamix.</summary>
    public bool IsDemo => _services.DemoMode;

    public bool IsConfigured => Tdx != null || IsDemo;

    /// <summary>The filter selections; the Filters flyout, the list and the widgets all change these.</summary>
    public TicketFilters Filters { get; } = new();

    /// <summary>The rows the list shows: the filtered tickets as an outline, folded subtrees left out.</summary>
    public ObservableCollection<TicketListItem> Rows { get; } = new();

    /// <summary>The filtered, sorted tickets, before folding.</summary>
    public IReadOnlyList<TdxTicket> Listed { get; private set; } = Array.Empty<TdxTicket>();

    public IReadOnlyList<TdxTicket> Loaded => Cache.Tickets;

    [ObservableProperty]
    public partial IReadOnlyList<TicketBoardBlockItem> Blocks { get; private set; } = Array.Empty<TicketBoardBlockItem>();

    [ObservableProperty]
    public partial IReadOnlyList<TicketWidgetTile> Tiles { get; private set; } = Array.Empty<TicketWidgetTile>();

    [ObservableProperty]
    public partial IReadOnlyList<TicketWidgetChart> Charts { get; private set; } = Array.Empty<TicketWidgetChart>();

    [ObservableProperty]
    public partial string CountLabel { get; private set; } = "";

    /// <summary>The signed-in person, for Assigned to Me; null until found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedToMeEnabled), nameof(AssignedToMeToolTip))]
    public partial TdxPerson? Me { get; private set; }

    public bool AssignedToMeEnabled => Me != null;

    public string AssignedToMeToolTip => Me == null
        ? "Waiting to identify you in TeamDynamix"
        : "Show only tickets you are responsible for";

    private bool _assignedToMe;

    /// <summary>Assigned to Me, saved for the next launch. Turning it on switches to the list, as on Mac.</summary>
    public bool AssignedToMe
    {
        get => _assignedToMe;
        set
        {
            if (!SetProperty(ref _assignedToMe, value)) return;
            UserPreferences.SetTicketsAssignedToMe(value);
            if (TicketsToolbarRules.ViewAfterAssignedToMe(ViewMode, value) == TicketsViewMode.List) IsBoard = false;
            Apply();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewMode), nameof(ShowsBoardControls))]
    public partial bool IsBoard { get; set; } = true;

    partial void OnIsBoardChanged(bool value) => Apply();

    public TicketsViewMode ViewMode => TicketsToolbarRules.ModeOf(IsBoard, false);

    /// <summary>The column picker and the sort controls show on the board only; the list sorts by its headers.</summary>
    public bool ShowsBoardControls => TicketsToolbarRules.ShowsBoardGrouping(ViewMode);

    [ObservableProperty]
    public partial string BoardGroupBy { get; set; } = TicketBoardLayout.DefaultGroupBy;

    partial void OnBoardGroupByChanged(string value) => Apply();

    public IReadOnlyList<string> BoardGroupByOptions => TicketBoardLayout.GroupByOptions;

    public IReadOnlyList<string> SortFields => TicketListSort.Fields;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionGlyph), nameof(SortDirectionToolTip), nameof(SortDirectionName))]
    public partial bool SortAscending { get; set; }

    [ObservableProperty]
    public partial string SortField { get; set; } = "Modified";

    partial void OnSortAscendingChanged(bool value) => Apply();

    partial void OnSortFieldChanged(string value) => Apply();

    public string SortDirectionGlyph => SortAscending ? "" : "";
    public string SortDirectionToolTip => TicketsToolbarRules.SortDirectionToolTip(SortAscending);
    public string SortDirectionName => TicketsToolbarRules.SortDirectionName(SortAscending);

    /// <summary>A header click: sort by that column, or flip the direction when it already sorts.</summary>
    public void SortByColumn(TicketListColumn column)
    {
        var (field, ascending) = TicketListSort.AfterHeaderClick(SortField, SortAscending, column);
        SetSort(field, ascending);
    }

    /// <summary>The one way to change the sort, so the board's controls and the list's headers agree.</summary>
    public void SetSort(string field, bool ascending)
    {
        if (field == SortField && ascending == SortAscending) return;
        // Set both before redrawing once.
        _batching = true;
        SortField = field;
        SortAscending = ascending;
        _batching = false;
        Apply();
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortAscending = !SortAscending;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Apply();

    [ObservableProperty]
    public partial bool ShowClosed { get; set; }

    partial void OnShowClosedChanged(bool value)
    {
        RaiseFilterBadge();
        Apply();
    }

    /// <summary>The created-date window, as the Filters flyout's Time Period shows it.</summary>
    public TicketDateRangePreset Preset => Cache.Preset;

    public bool IsDefaultDateRange => Preset == TicketDateRangePreset.CurrentTerm;

    /// <summary>Choose a date window; the board is read again for it.</summary>
    public async Task SetPresetAsync(TicketDateRangePreset preset)
    {
        if (preset == Cache.Preset && _initialLoadDone && Cache.Tickets.Count > 0) return;
        Cache.Preset = preset;
        OnPropertyChanged(nameof(Preset));
        RaiseFilterBadge();
        if (!_initialLoadDone) return;
        if (!IsDemo) Cache.Clear();
        await LoadAsync();
    }

    // MARK: - Filter badge

    public int ActiveFilterCount => TicketFiltersPopoverRules.ActiveCount(Filters, ShowClosed, IsDefaultDateRange);
    public string FilterBadge => TicketFiltersPopoverRules.BadgeText(ActiveFilterCount);
    public bool HasFilterBadge => ActiveFilterCount > 0;
    public string FiltersButtonName => TicketFiltersPopoverRules.ButtonName(ActiveFilterCount);
    public string FiltersButtonToolTip => TicketFiltersPopoverRules.ButtonToolTip(ActiveFilterCount);
    public bool ShowsClearAll => TicketFiltersPopoverRules.ShowsClearAll(ActiveFilterCount);

    private void RaiseFilterBadge()
    {
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(FilterBadge));
        OnPropertyChanged(nameof(HasFilterBadge));
        OnPropertyChanged(nameof(FiltersButtonName));
        OnPropertyChanged(nameof(FiltersButtonToolTip));
        OnPropertyChanged(nameof(ShowsClearAll));
    }

    private void OnFiltersChanged()
    {
        RaiseFilterBadge();
        Apply();
    }

    /// <summary>Every category cleared, closed tickets hidden and the date window back to the current term, as the Mac's Clear Filters does.</summary>
    [RelayCommand]
    private async Task ClearAllFiltersAsync()
    {
        Filters.ClearAll();
        ShowClosed = false;
        await SetPresetAsync(TicketDateRangePreset.CurrentTerm);
        RaiseFilterBadge();
    }

    // MARK: - Loading and empty state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshEnabled))]
    public partial bool IsLoading { get; private set; }

    public bool RefreshEnabled => !IsLoading && IsConfigured;

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    public partial bool ShowsLoading { get; private set; }

    [ObservableProperty]
    public partial string LoadingText { get; private set; } = "Loading tickets…";

    /// <summary>The launch sign-in's progress, 0 to 1; null shows an indeterminate ring.</summary>
    [ObservableProperty]
    public partial double? LaunchProgress { get; private set; }

    [ObservableProperty]
    public partial TicketsEmptyState? EmptyState { get; private set; }

    public bool ShowsSignInButton => Session.ShowsSignIn;

    /// <summary>
    /// The first visit: show what the cache holds, then read the board unless
    /// it is fresh. Later visits only redraw.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_initialLoadDone)
        {
            if (Me == null) await ResolveMeAsync();
            return;
        }
        _initialLoadDone = true;
        await LoadAsync();
        await ResolveMeAsync();
    }

    /// <summary>Refresh: forget the cached board and read it again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsDemo) Cache.Clear();
        await LoadAsync();
        if (Me == null) await ResolveMeAsync();
    }

    /// <summary>Read the board unless the cache is fresh; the cached tickets show while it runs.</summary>
    public async Task LoadAsync()
    {
        // Demo mode: the fixtures were put in the cache when the services were built.
        if (IsDemo)
        {
            Me = DemoTickets.Me;
            Apply();
            return;
        }
        if (Tdx == null)
        {
            UpdateEmptyState();
            return;
        }
        if (Cache.IsValid && Cache.Tickets.Count > 0)
        {
            Apply();
            return;
        }
        if (Cache.Tickets.Count > 0) Apply();

        IsLoading = true;
        UpdateLoading();
        try
        {
            await Cache.LoadAsync();
            LoadError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Tickets load failed");
            LoadError = ex.Message;
            Session.TicketsLoaded();
        }
        finally
        {
            IsLoading = false;
            UpdateLoading();
            Apply();
        }

        // Sign-in finished while this read ran; if it came back with nothing,
        // it ran without the session, so read once more with it.
        if (_reloadAfterLoad)
        {
            _reloadAfterLoad = false;
            if (Session.IsSignedIn && (LoadError != null || Cache.Tickets.Count == 0)) await LoadAsync();
        }
    }

    /// <summary>Find the signed-in person, so Assigned to Me has someone to match.</summary>
    public async Task ResolveMeAsync()
    {
        if (IsDemo)
        {
            Me = DemoTickets.Me;
            return;
        }
        if (Tdx == null) return;
        try
        {
            Me = await Tdx.GetMeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tickets] Could not resolve the signed-in TDX person");
            Me = null;
        }
        if (AssignedToMe) Apply();
    }

    private async void OnSignInChanged()
    {
        Me = null;
        OnPropertyChanged(nameof(ShowsSignInButton));
        if (!Session.IsSignedIn)
        {
            Apply();
            return;
        }
        await ResolveMeAsync();
        if (IsLoading) _reloadAfterLoad = true;
        if (TicketsPageSync.ShouldReloadAfterSignIn(_initialLoadDone, IsLoading, Cache.IsValid, Cache.Tickets.Count))
            await LoadAsync();
        else
            Apply();
        SignedIn?.Invoke();
    }

    /// <summary>Signed in: the page opens a link held for it and reloads the open ticket.</summary>
    public event Action? SignedIn;

    private void OnSessionStatusChanged()
    {
        OnPropertyChanged(nameof(ShowsSignInButton));
        UpdateEmptyState();
    }

    private void OnLaunchStepChanged() => UpdateLoading();

    /// <summary>The shared cache changed (the launch read, a restored snapshot, a reset): redraw unless a read here will.</summary>
    private void OnCacheChanged()
    {
        if (!TicketsPageSync.ShouldRedrawOnCacheChange("Tickets", _initialLoadDone, IsLoading)) return;
        Apply();
    }

    private void UpdateLoading()
    {
        var step = Session.LaunchStep;
        ShowsLoading = TicketsPageSync.ShowLoading(IsLoading, step, Cache.Tickets.Count);
        LaunchProgress = step?.Fraction();
        LoadingText = step?.Label() ?? "Loading tickets…";
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (Tdx == null && !IsDemo)
        {
            EmptyState = new TicketsEmptyState("", "TeamDynamix Isn't Set Up",
                "Add TeamDynamix to the configuration or policy to see tickets here.",
                false, false, false, false, false);
            return;
        }
        var reason = TicketsPageSync.EmptyReason(
            loadingShown: ShowsLoading,
            ticketCount: Cache.Tickets.Count,
            listedCount: Listed.Count,
            ssoEnabled: Session.IsEnabled,
            signedIn: Session.IsSignedIn,
            loadError: LoadError,
            searching: !string.IsNullOrWhiteSpace(Search),
            assignedToMe: AssignedToMe && Me != null);
        EmptyState = reason == TicketsEmptyReason.None
            ? null
            : TicketsPageSync.EmptyState(reason, LoadError, Search, Filters.HasActiveFilters || !IsDefaultDateRange, ShowClosed);
    }

    // MARK: - The list, the board and the widgets

    private bool _batching;

    /// <summary>The options narrowing the list now.</summary>
    public TicketListOptions Options => new(Filters, ShowClosed, AssignedToMe ? Me : null, Search);

    /// <summary>The loaded tickets with every filter except <paramref name="ignoring"/>'s.</summary>
    public List<TdxTicket> Matching(TicketFilterCategory? ignoring) => TicketListQuery.Matching(Cache.Tickets, Options, ignoring);

    /// <summary>Filter, sort and redraw the list, the board and the widgets.</summary>
    public void Apply()
    {
        if (_batching) return;
        Listed = TicketListSort.Apply(Matching(null), SortField, SortAscending).ToList();
        RebuildOutline();
        if (IsBoard) RebuildBoard();
        Tiles = TicketWidgetRules.Tiles(Matching, Filters);
        Charts = TicketWidgetRules.Charts(Matching);
        CountLabel = TicketListQuery.CountLabel(Listed.Count, Cache.Tickets.Count);
        UpdateLoading();
        Redrawn?.Invoke();
    }

    /// <summary>The list and board were rebuilt; the page restores the selection and scroll.</summary>
    public event Action? Redrawn;

    private void RebuildOutline()
    {
        var rows = TicketHierarchy.Flatten(TicketHierarchy.Build(Listed), _collapsed);
        Rows.Clear();
        foreach (var row in rows) Rows.Add(new TicketListItem(row));
    }

    private void RebuildBoard()
    {
        // Only Status mode colours the header dot (the status's own); the
        // others leave it grey, as on Mac.
        var status = BoardGroupBy == "Status";
        Blocks = TicketBoardLayout.Blocks(Listed, BoardGroupBy)
            .Select(b => new TicketBoardBlockItem(b.Header, b.Columns.Select(c => new TicketBoardColumnItem(
                c.Key,
                c.Tickets.Count,
                TicketHierarchy.Flatten(TicketHierarchy.Build(c.Tickets), _collapsed).Select(r => new TicketListItem(r)).ToList(),
                TicketBrushes.Tone(status ? TicketTones.Status(c.Key) : TicketTone.Gray))).ToList()))
            .ToList();
    }

    // MARK: - Folding

    /// <summary>Fold or unfold one parent; with <paramref name="all"/>, every parent, as Alt-click does.</summary>
    public void ToggleFold(int ticketId, bool all = false)
    {
        if (all)
        {
            if (_collapsed.Contains(ticketId)) ExpandAll(); else CollapseAll();
            return;
        }
        if (!_collapsed.Remove(ticketId)) _collapsed.Add(ticketId);
        Apply();
    }

    [RelayCommand]
    public void ExpandAll()
    {
        _collapsed.Clear();
        Apply();
    }

    [RelayCommand]
    public void CollapseAll()
    {
        foreach (var id in TicketHierarchy.AllParentIds(TicketHierarchy.Build(Listed))) _collapsed.Add(id);
        Apply();
    }

    /// <summary>Unfold whatever hides <paramref name="ticketId"/>, so a link to a child never lands on nothing.</summary>
    public void Reveal(int ticketId)
    {
        var byId = Listed.ToDictionary(t => t.Id);
        if (!byId.TryGetValue(ticketId, out var ticket)) return;
        var cursor = TicketHierarchy.ParentTicketId(ticket);
        var guard = new HashSet<int>();
        var changed = false;
        while (cursor is { } parentId && guard.Add(parentId))
        {
            changed |= _collapsed.Remove(parentId);
            cursor = byId.TryGetValue(parentId, out var parent) ? TicketHierarchy.ParentTicketId(parent) : null;
        }
        if (changed) Apply();
    }

    // MARK: - Edits from the board

    /// <summary>Put an edited ticket in the cache and redraw.</summary>
    public void Replace(TdxTicket updated)
    {
        Cache.Replace(updated);
        Apply();
    }

    /// <summary>Insert a newly created ticket at the top.</summary>
    public void Insert(TdxTicket created)
    {
        Cache.Tickets.Insert(0, created);
        Apply();
    }

    public void Dispose()
    {
        Filters.Changed -= OnFiltersChanged;
        Cache.Changed -= OnCacheChanged;
        Session.SignInChanged -= OnSignInChanged;
        Session.StatusChanged -= OnSessionStatusChanged;
        Session.LaunchStepChanged -= OnLaunchStepChanged;
    }
}
