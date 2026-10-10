using System.Windows.Controls;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Reporting;

/// <summary>
/// The toolbar search field filters ReportMate's devices (Ctrl+F) and Enter opens
/// the best match; Ctrl+K still searches everything, Reporting included. The
/// dashboard draws no search of its own, so the box the field writes into is
/// never shown.
/// </summary>
public sealed partial class ReportingPage : ITabSearch
{
    private readonly TextBox _searchBox = new();

    public TabSearchScope? SearchScope =>
        new(_searchBox, "Search devices by name, serial, asset or hostname", () => _ = _dashboard.OpenBestDeviceMatchAsync());

    // The dashboard has one device filter, so the scope never changes.
    public event EventHandler? SearchScopeChanged { add { } remove { } }
}
