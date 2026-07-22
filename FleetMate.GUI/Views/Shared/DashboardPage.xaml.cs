using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Tickets;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

public partial class DashboardPage : Page
{
    private readonly App? _app;
    private bool _isInitialLoadDone;
    private List<SnipeActivity> _snipeActivity = new();
    private string _activityFilter = ""; // empty = All

    // Segoe MDL2 Assets glyphs (no emoji)
    private const string GlyphTicket    = "\uE8A7"; // Tag
    private const string GlyphWorkItem  = "\uE762"; // BulletedList
    private const string GlyphDevice    = "\uE7F8"; // Laptop
    private const string GlyphAsset     = "\uE7B8"; // Package
    private const string GlyphActivity  = "\uE895"; // Sync

    public DashboardPage()
    {
        InitializeComponent();

        if (Application.Current is App app)
            _app = app;

        BuildActivityFilterChips();

        Loaded += async (_, _) =>
        {
            if (!_isInitialLoadDone)
            {
                _isInitialLoadDone = true;
                await RefreshDashboardAsync();
            }
        };
    }

    private void BuildActivityFilterChips()
    {
        var filters = new[] { ("All", ""), ("Devices", "Devices"), ("Inventory", "Inventory"),
                              ("Tickets", "Tickets"), ("Projects", "Projects"), ("Identity", "Identity") };
        foreach (var (label, tag) in filters)
        {
            var btn = new Border
            {
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = tag,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            btn.MouseLeftButtonUp += OnActivityFilterClicked;
            ActivityFilterPanel.Children.Add(btn);
        }
        UpdateFilterChipStyles();
    }

    private void UpdateFilterChipStyles()
    {
        foreach (Border chip in ActivityFilterPanel.Children)
        {
            var tag = chip.Tag as string ?? "";
            var active = tag == _activityFilter;
            chip.Background = active
                ? (Brush)FindResource("SystemControlHighlightAccentBrush")
                : new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
            if (chip.Child is TextBlock tb)
                tb.Foreground = active
                    ? Brushes.White
                    : (Brush)FindResource("SystemControlForegroundBaseHighBrush");
        }
    }

    private void OnActivityFilterClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border chip)
        {
            _activityFilter = chip.Tag as string ?? "";
            UpdateFilterChipStyles();
            PopulateActivityFeed();
        }
    }

    // ── Navigation helper ─────────────────────────────────────────

    private void NavigateToTab(string tag)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToTab(tag);
    }

    // ── Main refresh ──────────────────────────────────────────────

    private async Task RefreshDashboardAsync()
    {
        if (_app == null) return;

        LoadingPanel.Visibility = Visibility.Visible;
        HeaderProgress.Visibility = Visibility.Visible;

        var hasAnyService = _app.GraphService != null || _app.SnipeService != null ||
                            _app.TdxService != null || _app.DevOpsService != null ||
                            _app.ReportMateService != null;

        ConnectServicesPrompt.Visibility = hasAnyService ? Visibility.Collapsed : Visibility.Visible;

        if (hasAnyService)
        {
            await EnsureDataLoaded();

            PopulateKpiStrip();
            PopulateChartGrid();
            PopulateAlertPills();
            PopulateActivityFeed();
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        HeaderProgress.Visibility = Visibility.Collapsed;
    }

    // ── Ensure data is loaded ─────────────────────────────────────

    private async Task EnsureDataLoaded()
    {
        if (_app == null) return;
        var tasks = new List<Task>();

        if (_app.GraphService != null && _app.CachedDevices.Count == 0 && !_app.IsDevicesCacheValid)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var devices = await _app.GraphService.GetManagedDevicesAsync(limit: 10000);
                    Dispatcher.Invoke(() => _app.UpdateDevicesCache(devices));
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load devices"); }
            }));
        }

        if (_app.SnipeService != null && _app.CachedAssets.Count == 0 && !_app.IsAssetsCacheValid)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var assets = await _app.SnipeService.GetAssetsAsync();
                    Dispatcher.Invoke(() => _app.UpdateAssetsCache(assets));
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load assets"); }
            }));
        }

        if (_app.SnipeService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var activity = await _app.SnipeService.GetActivityAsync(limit: 50);
                    Dispatcher.Invoke(() => _snipeActivity = activity);
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load snipe activity"); }
            }));
        }

        if (_app.TdxService != null && _app.CachedTickets.Count == 0 && !_app.IsTicketsCacheValid)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var tickets = await _app.TdxService.SearchTicketsAsync(
                        new TicketSearchRequest { MaxResults = 500 }, 500);
                    Dispatcher.Invoke(() => _app.UpdateTicketsCache(tickets));
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load tickets"); }
            }));
        }

        if (_app.DevOpsService != null && _app.CachedWorkItems.Count == 0)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var items = await _app.DevOpsService.GetWorkItemsAsync(limit: 200);
                    Dispatcher.Invoke(() => _app.CachedWorkItems = items);
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load work items"); }
            }));

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var sprints = await _app.DevOpsService.GetSprintsAsync();
                    Dispatcher.Invoke(() => _app.CachedSprints = sprints);
                }
                catch (Exception ex) { Log.Warning(ex, "Dashboard: failed to load sprints"); }
            }));
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    // ── KPI Strip ─────────────────────────────────────────────────

    private void PopulateKpiStrip()
    {
        if (_app == null) return;
        KpiStrip.Children.Clear();

        var devices = _app.CachedDevices;
        var tickets = _app.CachedTickets;
        var workItems = _app.CachedWorkItems;
        var assets = _app.CachedAssets;

        if (_app.GraphService != null)
        {
            AddKpiCard("Devices", devices.Count > 0 ? devices.Count.ToString() : "--",
                "\uE7F8", "#FF2196F3", "Devices");
            var nc = devices.Count(d => !d.IsCompliant);
            if (nc > 0)
                AddKpiCard("Non-Compliant", nc.ToString(), "\uE7BA", "#FFF44336", "Devices");
            var stale = devices.Count(d => d.LastSyncDateTime.HasValue &&
                (DateTime.UtcNow - d.LastSyncDateTime.Value).TotalDays > 30);
            if (stale > 0)
                AddKpiCard("Stale (30d+)", stale.ToString(), "\uE823", "#FFFF9800", "Devices");
        }

        if (_app.TdxService != null)
        {
            var open = tickets.Count(t => t.IsOpen && !t.IsOnHold);
            AddKpiCard("Open Tickets", tickets.Count > 0 ? open.ToString() : "--",
                "\uE8A7", "#FF9C27B0", "Tickets");
        }

        if (_app.DevOpsService != null)
        {
            var active = workItems.Count(w =>
                !(w.State?.Equals("Done", StringComparison.OrdinalIgnoreCase) == true ||
                  w.State?.Equals("Closed", StringComparison.OrdinalIgnoreCase) == true ||
                  w.State?.Equals("Removed", StringComparison.OrdinalIgnoreCase) == true));
            AddKpiCard("Active Work Items", workItems.Count > 0 ? active.ToString() : "--",
                "\uE762", "#FF3F51B5", "Projects");
        }

        if (_app.SnipeService != null)
        {
            AddKpiCard("Assets", assets.Count > 0 ? assets.Count.ToString() : "--",
                "\uE7B8", "#FFFF9800", "Inventory");
        }
    }

    private void AddKpiCard(string title, string value, string glyph, string colorHex, string tab)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            MinWidth = 120,
            Margin = new Thickness(0, 0, 10, 10),
            Cursor = Cursors.Hand,
            Tag = tab,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 20,
                        Foreground = new SolidColorBrush(color),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    },
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = value,
                                FontSize = 22,
                                FontWeight = FontWeights.Bold
                            },
                            new TextBlock
                            {
                                Text = title,
                                FontSize = 11,
                                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                            }
                        }
                    }
                }
            }
        };
        card.MouseLeftButtonUp += (_, _) => NavigateToTab(tab);
        KpiStrip.Children.Add(card);
    }

    // ── Chart Grid (Assets by Category first, flowing) ────────────

    private void PopulateChartGrid()
    {
        if (_app == null) return;
        ChartGrid.Children.Clear();

        var devices = _app.CachedDevices;
        var tickets = _app.CachedTickets;
        var workItems = _app.CachedWorkItems;
        var assets = _app.CachedAssets;

        // Assets by Category FIRST
        if (_app.SnipeService != null && assets.Count > 0)
        {
            var catGroups = assets
                .GroupBy(a => a.Category?.Name ?? "Uncategorized")
                .OrderByDescending(g => g.Count())
                .Take(8).ToList();

            AddChartCard("Assets by Category", "Inventory", () =>
            {
                var catColors = new SKColor[]
                {
                    new(255, 152, 0), new(33, 150, 243), new(156, 39, 176),
                    new(0, 150, 136), new(76, 175, 80), new(244, 67, 54),
                    new(121, 85, 72), new(63, 81, 181)
                };
                var chart = new CartesianChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
                    XAxes = new Axis[]
                    {
                        new()
                        {
                            Labels = catGroups.Select(g => g.Key).ToArray(),
                            LabelsRotation = 15, TextSize = 10
                        }
                    },
                    Series = catGroups.Select((g, i) => new ColumnSeries<int>
                    {
                        Values = new[] { g.Count() },
                        Name = g.Key,
                        Fill = new SolidColorPaint(catColors[i % catColors.Length])
                    } as ISeries).ToArray()
                };
                return chart;
            });
        }

        // Fleet Health charts
        if (_app.GraphService != null && devices.Count > 0)
        {
            var osCounts = devices
                .GroupBy(d => d.OperatingSystem ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Take(6).ToList();
            var osColors = new SKColor[]
            {
                new(33, 150, 243), new(156, 39, 176), new(255, 152, 0),
                new(0, 150, 136), new(121, 85, 72), new(158, 158, 158)
            };

            AddChartCard("Platform Distribution", "Devices", () =>
            {
                var chart = new PieChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
                    Series = osCounts.Select((g, i) => new PieSeries<int>
                    {
                        Values = new[] { g.Count() },
                        Name = $"{g.Key} ({g.Count()})",
                        Fill = new SolidColorPaint(osColors[i % osColors.Length]),
                        InnerRadius = 50
                    } as ISeries).ToArray()
                };
                return chart;
            });
        }

        // Ticket charts
        if (_app.TdxService != null && tickets.Count > 0)
        {
            var open = tickets.Count(t => t.IsOpen && !t.IsOnHold);
            var onHold = tickets.Count(t => t.IsOnHold);
            var slaViolated = tickets.Count(t => t.IsSlaViolated);

            AddChartCard("Ticket Status", "Tickets", () =>
            {
                var panel = new StackPanel();
                var chart = new PieChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
                    Series = new ISeries[]
                    {
                        new PieSeries<int> { Values = new[] { open }, Name = $"Open ({open})",
                            Fill = new SolidColorPaint(new SKColor(33, 150, 243)), InnerRadius = 50 },
                        new PieSeries<int> { Values = new[] { onHold }, Name = $"On Hold ({onHold})",
                            Fill = new SolidColorPaint(new SKColor(255, 152, 0)), InnerRadius = 50 }
                    }.Where(s => ((PieSeries<int>)s).Values!.Cast<int>().First() > 0).ToArray()
                };
                panel.Children.Add(chart);
                if (slaViolated > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"{slaViolated} SLA violated",
                        FontSize = 10,
                        Foreground = Brushes.OrangeRed
                    });
                }
                return panel;
            });

            var priorityOrder = new Dictionary<string, int>
            {
                ["Low"] = 0, ["Medium"] = 1, ["High"] = 2
            };
            var priorities = tickets.Where(t => t.IsOpen)
                .GroupBy(t => t.PriorityName ?? "None")
                .OrderBy(g => priorityOrder.GetValueOrDefault(g.Key, 99)).ToList();
            var prioColors = new SKColor[]
            {
                new(76, 175, 80), new(255, 152, 0), new(244, 67, 54),
                new(156, 39, 176), new(158, 158, 158)
            };
            if (priorities.Count > 0)
            {
                AddChartCard("Tickets by Priority", "Tickets", () =>
                {
                    var chart = new CartesianChart
                    {
                        Height = 160,
                        LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
                        XAxes = new Axis[] { new() { Labels = priorities.Select(g => g.Key).ToArray(), TextSize = 10 } },
                        Series = priorities.Select((g, i) => new ColumnSeries<int>
                        {
                            Values = new[] { g.Count() },
                            Name = g.Key,
                            Fill = new SolidColorPaint(prioColors[i % prioColors.Length])
                        } as ISeries).ToArray()
                    };
                    return chart;
                });
            }
        }

        // Work Item chart
        if (_app.DevOpsService != null && workItems.Count > 0)
        {
            var stateCounts = workItems
                .GroupBy(w => w.State ?? "Unknown")
                .OrderByDescending(g => g.Count()).ToList();
            var stateColors = new SKColor[]
            {
                new(33, 150, 243), new(76, 175, 80), new(255, 152, 0),
                new(244, 67, 54), new(158, 158, 158), new(121, 85, 72)
            };

            AddChartCard("Work Items", "Projects", () =>
            {
                var panel = new StackPanel();
                var chart = new PieChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
                    Series = stateCounts.Select((g, i) => new PieSeries<int>
                    {
                        Values = new[] { g.Count() },
                        Name = $"{g.Key} ({g.Count()})",
                        Fill = new SolidColorPaint(stateColors[i % stateColors.Length]),
                        InnerRadius = 50
                    } as ISeries).ToArray()
                };
                panel.Children.Add(chart);

                var currentSprint = _app.CachedSprints.FirstOrDefault(s => s.IsCurrent);
                if (currentSprint != null)
                {
                    var si = workItems.Count(w => w.IterationPath?.EndsWith(currentSprint.Name) == true);
                    var done = workItems.Count(w =>
                        w.IterationPath?.EndsWith(currentSprint.Name) == true &&
                        w.State?.Equals("Done", StringComparison.OrdinalIgnoreCase) == true);
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"Sprint: {currentSprint.Name} - {done}/{si} done",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                    });
                }
                return panel;
            });
        }

        // ReportMate (async inline)
        if (_app.ReportMateService != null)
        {
            AddChartCard("Errors by Category", "Devices", () =>
            {
                var panel = new StackPanel();
                var loading = new TextBlock
                {
                    Text = "Loading...",
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                };
                panel.Children.Add(loading);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var devicesTask = _app.ReportMateService.GetDevicesAsync(false);
                        var errorsTask = _app.ReportMateService.GetErrorsByItemAsync(false);
                        await Task.WhenAll(devicesTask, errorsTask);
                        var rmDevices = devicesTask.Result;
                        var rmErrors = errorsTask.Result;

                        var cats = rmErrors
                            .GroupBy(e => e.Category)
                            .OrderByDescending(g => g.Sum(e => e.DeviceCount))
                            .Take(8).ToList();

                        Dispatcher.Invoke(() =>
                        {
                            panel.Children.Clear();

                            var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                            statsRow.Children.Add(new TextBlock
                            {
                                Text = $"{rmDevices.Count} managed",
                                FontSize = 14, FontWeight = FontWeights.Bold,
                                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF2196F3")),
                                Margin = new Thickness(0, 0, 12, 0)
                            });
                            statsRow.Children.Add(new TextBlock
                            {
                                Text = $"{rmErrors.Count} errors",
                                FontSize = 14, FontWeight = FontWeights.Bold,
                                Foreground = rmErrors.Count > 0 ? Brushes.OrangeRed : Brushes.Green
                            });
                            panel.Children.Add(statsRow);

                            if (cats.Count > 0)
                            {
                                var chart = new CartesianChart
                                {
                                    Height = 140,
                                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden,
                                    XAxes = new Axis[]
                                    {
                                        new()
                                        {
                                            Labels = cats.Select(g => CategoryLabel(g.Key)).ToArray(),
                                            LabelsRotation = 15, TextSize = 10
                                        }
                                    },
                                    Series = new ISeries[]
                                    {
                                        new ColumnSeries<int>
                                        {
                                            Values = cats.Select(g => g.Sum(e => e.DeviceCount)).ToArray(),
                                            Name = "Affected Devices",
                                            Fill = new SolidColorPaint(new SKColor(244, 67, 54))
                                        }
                                    }
                                };
                                panel.Children.Add(chart);
                            }
                            else
                            {
                                panel.Children.Add(new TextBlock
                                {
                                    Text = "No errors found",
                                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                                });
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Dashboard: ReportMate load failed");
                        Dispatcher.Invoke(() =>
                        {
                            panel.Children.Clear();
                            panel.Children.Add(new TextBlock
                            {
                                Text = "Failed to load ReportMate data",
                                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                            });
                        });
                    }
                });

                return panel;
            });
        }

        // Compliance chart (near bottom)
        if (_app.GraphService != null && devices.Count > 0)
        {
            var compliant = devices.Count(d => d.IsCompliant);
            var nonCompliant = devices.Count - compliant;

            AddChartCard("Compliance", "Devices", () =>
            {
                var chart = new PieChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
                    Series = new ISeries[]
                    {
                        new PieSeries<int> { Values = new[] { compliant }, Name = $"Compliant ({compliant})",
                            Fill = new SolidColorPaint(new SKColor(76, 175, 80)),
                            InnerRadius = 50 },
                        new PieSeries<int> { Values = new[] { nonCompliant }, Name = $"Non-Compliant ({nonCompliant})",
                            Fill = new SolidColorPaint(new SKColor(244, 67, 54)),
                            InnerRadius = 50 }
                    }
                };
                return chart;
            });
        }

        // Asset Status chart
        if (_app.SnipeService != null && assets.Count > 0)
        {
            var deployed = assets.Count(a =>
                a.StatusLabel?.StatusMeta?.Equals("deployed", StringComparison.OrdinalIgnoreCase) == true);
            var unassigned = assets.Count(a => a.AssignedTo == null);

            var statusGroups = assets
                .GroupBy(a => a.StatusLabel?.StatusMeta ?? a.StatusLabel?.Name ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Take(5).ToList();
            var statusColors = new SKColor[]
            {
                new(76, 175, 80), new(33, 150, 243), new(255, 152, 0),
                new(244, 67, 54), new(158, 158, 158)
            };

            AddChartCard("Asset Status", "Inventory", () =>
            {
                var panel = new StackPanel();
                var chart = new PieChart
                {
                    Height = 160,
                    LegendPosition = LiveChartsCore.Measure.LegendPosition.Right,
                    Series = statusGroups.Select((g, i) => new PieSeries<int>
                    {
                        Values = new[] { g.Count() },
                        Name = $"{g.Key} ({g.Count()})",
                        Fill = new SolidColorPaint(statusColors[i % statusColors.Length]),
                        InnerRadius = 50
                    } as ISeries).ToArray()
                };
                panel.Children.Add(chart);
                panel.Children.Add(new TextBlock
                {
                    Text = $"{deployed} deployed - {unassigned} unassigned",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
                });
                return panel;
            });
        }
    }

    private void AddChartCard(string title, string tab, Func<UIElement> contentFactory)
    {
        var titleBlock = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand
        };

        // Title with chevron for navigation hint
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "\uE76C", // ChevronRight
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        });
        titlePanel.Cursor = Cursors.Hand;
        titlePanel.MouseLeftButtonUp += (_, _) => NavigateToTab(tab);

        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Width = 360,
            Margin = new Thickness(0, 0, 12, 12),
            Cursor = Cursors.Hand,
            Tag = tab,
            Child = new StackPanel
            {
                Children = { titlePanel, contentFactory() }
            }
        };
        card.MouseLeftButtonUp += (_, _) => NavigateToTab(tab);
        ChartGrid.Children.Add(card);
    }

    // ── Alert Pills ───────────────────────────────────────────────

    private void PopulateAlertPills()
    {
        if (_app == null) return;
        AlertPillsPanel.Children.Clear();

        var devices = _app.CachedDevices;
        var tickets = _app.CachedTickets;
        var assets = _app.CachedAssets;

        if (devices.Count > 0)
        {
            var nonCompliant = devices.Count(d => !d.IsCompliant);
            if (nonCompliant > 0)
                AddAlertPill($"{nonCompliant} non-compliant", "#FFF44336", "Devices");

            var stale = devices.Count(d => d.LastSyncDateTime.HasValue &&
                (DateTime.UtcNow - d.LastSyncDateTime.Value).TotalDays > 30);
            if (stale > 0)
                AddAlertPill($"{stale} stale devices (30d+)", "#FFFF9800", "Devices");
        }

        if (tickets.Count > 0)
        {
            var slaViolated = tickets.Count(t => t.IsSlaViolated);
            if (slaViolated > 0)
                AddAlertPill($"{slaViolated} SLA violations", "#FFF44336", "Tickets");
        }

        if (assets.Count > 0)
        {
            var unassigned = assets.Count(a => a.AssignedTo == null);
            if (unassigned > 5)
                AddAlertPill($"{unassigned} unassigned assets", "#FF2196F3", "Inventory");
        }
    }

    private void AddAlertPill(string text, string colorHex, string tab)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(color)
            }
        };
        pill.MouseLeftButtonUp += (_, _) => NavigateToTab(tab);
        AlertPillsPanel.Children.Add(pill);
    }

    // ── Activity Feed ─────────────────────────────────────────────

    private void PopulateActivityFeed()
    {
        if (_app == null) return;

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var items = new List<ActivityItem>();

        // Tickets – last 24h
        foreach (var t in _app.CachedTickets
            .Where(t => t.ModifiedDate.HasValue && t.ModifiedDate.Value.ToUniversalTime() > cutoff)
            .OrderByDescending(t => t.ModifiedDate!.Value))
        {
            items.Add(new ActivityItem(GlyphTicket,
                $"#{t.Id}  {t.Title.Truncate(50)}  -  {t.StatusName ?? ""}",
                t.ModifiedDate!.Value, "Tickets", TicketId: t.Id));
        }

        // Work items – last 24h
        foreach (var w in _app.CachedWorkItems
            .Where(w => w.ChangedDate.HasValue && w.ChangedDate.Value.ToUniversalTime() > cutoff)
            .OrderByDescending(w => w.ChangedDate!.Value))
        {
            items.Add(new ActivityItem(GlyphWorkItem,
                $"#{w.Id}  {w.Title.Truncate(50)}  -  {w.State ?? ""}",
                w.ChangedDate!.Value, "Projects"));
        }

        // Devices – last 24h
        foreach (var d in _app.CachedDevices
            .Where(d => d.LastSyncDateTime.HasValue && d.LastSyncDateTime.Value.ToUniversalTime() > cutoff)
            .OrderByDescending(d => d.LastSyncDateTime!.Value))
        {
            items.Add(new ActivityItem(GlyphDevice,
                $"{d.DeviceName}  synced  -  {d.OperatingSystem ?? ""}",
                d.LastSyncDateTime!.Value, "Devices", DeviceId: d.Id));
        }

        // Assets – last 24h
        foreach (var a in _app.CachedAssets
            .Where(a => a.UpdatedAt?.DateTime != null))
        {
            var dt = ParseSnipeDate(a.UpdatedAt?.DateTime);
            if (dt.HasValue && dt.Value.ToUniversalTime() > cutoff)
            {
                items.Add(new ActivityItem(GlyphAsset,
                    $"{a.DisplayName}  -  {a.StatusLabel?.Name ?? ""}",
                    dt.Value, "Inventory"));
            }
        }

        // Snipe activity log – last 24h
        foreach (var entry in _snipeActivity)
        {
            var dt = ParseSnipeDate(entry.CreatedAt?.DateTime);
            if (dt.HasValue && dt.Value.ToUniversalTime() > cutoff)
            {
                var action = entry.ActionType ?? "activity";
                var itemName = entry.Item?.Name ?? "item";
                var admin = entry.Admin?.Name ?? "";
                var text = $"{admin} {action} {itemName}".Trim();
                items.Add(new ActivityItem(GlyphActivity, text, dt.Value, "Inventory"));
            }
        }

        // Apply tab filter
        IEnumerable<ActivityItem> filtered = string.IsNullOrEmpty(_activityFilter)
            ? items
            : items.Where(i => i.Tab == _activityFilter);

        var sorted = filtered
            .OrderByDescending(i => i.Timestamp)
            .Select(i => new { IconGlyph = i.IconGlyph, Text = i.Text, Time = FormatRelative(i.Timestamp), Tab = i.Tab, DeviceId = i.DeviceId, TicketId = i.TicketId })
            .ToList();

        ActivityFeed.ItemsSource = sorted;
    }

    // ── Event Handlers ────────────────────────────────────────────

    private void OnActivityItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is { } dc)
        {
            var tab = (string?)dc.GetType().GetProperty("Tab")?.GetValue(dc);
            var deviceId = (string?)dc.GetType().GetProperty("DeviceId")?.GetValue(dc);
            var ticketId = (int?)dc.GetType().GetProperty("TicketId")?.GetValue(dc);
            if (_app != null)
            {
                _app.PendingNavigateDeviceId = deviceId;
                _app.PendingNavigateTicketId = ticketId;
            }
            if (tab != null) NavigateToTab(tab);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static DateTime? ParseSnipeDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return null;
        return DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    private static string CategoryLabel(ErrorCategory cat) => cat switch
    {
        ErrorCategory.NotFound => "Not Found",
        ErrorCategory.HashMismatch => "Hash Mismatch",
        ErrorCategory.DownloadFailed => "Download Failed",
        ErrorCategory.MsiFailure => "MSI Failure",
        ErrorCategory.SignatureRequired => "Sig Required",
        ErrorCategory.CatalogMissing => "Catalog Missing",
        ErrorCategory.MissingChocolatey => "No Chocolatey",
        ErrorCategory.MissingSbinInstaller => "No sbin-installer",
        ErrorCategory.InstallVerificationFailed => "Verify Failed",
        ErrorCategory.MissingInstallerLocation => "No Installer Loc",
        _ => cat.ToString()
    };

    private static string FormatRelative(DateTime dt)
    {
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 2)  return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays}d ago";
        return dt.ToString("MMM d");
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        _app.InvalidateAllCaches();
        _ = _app.ReloadAllDataAsync().ContinueWith(_ =>
        {
            Dispatcher.Invoke(async () => await RefreshDashboardAsync());
        });
    }

    private void OnAuthClicked(object sender, RoutedEventArgs e)
    {
        PopulateAuthPopup();
        AuthPopup.IsOpen = !AuthPopup.IsOpen;
    }

    private void PopulateAuthPopup()
    {
        if (_app == null) return;
        AuthSystemsPanel.Children.Clear();

        var categories = Enum.GetValues<AuthCategory>();
        foreach (var category in categories)
        {
            var systems = _app.AuthManager.SystemsForCategory(category);
            if (systems.Count == 0) continue;

            // Category header
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            header.Children.Add(new ModernWpf.Controls.FontIcon { Glyph = CategoryGlyph(category), FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            header.Children.Add(new TextBlock { Text = category.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            AuthSystemsPanel.Children.Add(header);

            foreach (var system in systems)
            {
                AuthSystemsPanel.Children.Add(BuildAuthSystemCard(system));
            }
        }

        // Refresh All button
        var refreshBtn = new Button { Content = "Refresh All", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        refreshBtn.Click += async (_, _) =>
        {
            await _app.AuthManager.ProbeAllAsync(_app.GraphService, _app.TdxService, _app.SnipeService, _app.DevOpsService);
            PopulateAuthPopup();
        };
        AuthSystemsPanel.Children.Add(refreshBtn);
    }

    private Border BuildAuthSystemCard(AuthSystemStatus system)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SystemControlBackgroundAltMediumLowBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 4, 0, 4),
            BorderBrush = new SolidColorBrush(StateColor(system.State)),
            BorderThickness = new Thickness(1)
        };

        var content = new DockPanel();

        // Icon
        var icon = new ModernWpf.Controls.FontIcon
        {
            Glyph = system.SystemId.Icon(),
            FontSize = 20,
            Foreground = new SolidColorBrush(StateColor(system.State)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);

        // Action buttons on the right
        var actions = BuildAuthActions(system);
        if (actions != null)
        {
            DockPanel.SetDock(actions, Dock.Right);
            content.Children.Add(actions);
        }

        // Main content
        var main = new StackPanel();

        // Header: name + status badge
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(new TextBlock { Text = system.SystemId.DisplayName(), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) });

        var badge = new Border
        {
            Background = new SolidColorBrush(StateColor(system.State)) { Opacity = 0.15 },
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2)
        };
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        badgeContent.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(StateColor(system.State)), Margin = new Thickness(0, 0, 4, 0) });
        badgeContent.Children.Add(new TextBlock { Text = system.State.StatusLabel, FontSize = 11, Foreground = new SolidColorBrush(StateColor(system.State)) });
        badge.Child = badgeContent;
        headerRow.Children.Add(badge);
        main.Children.Add(headerRow);

        // Detail rows
        if (system.User != null)
            main.Children.Add(AuthDetailRow("Signed in as", system.User));
        if (system.LastChecked.HasValue)
            main.Children.Add(AuthDetailRow("Last verified", FormatRelative(system.LastChecked.Value)));

        content.Children.Add(main);
        card.Child = content;
        return card;
    }

    private FrameworkElement? BuildAuthActions(AuthSystemStatus system)
    {
        switch (system.SystemId)
        {
            case AuthSystemId.Tdx:
                var tdxBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    tdxBtn.Content = "Sign Out";
                    tdxBtn.Click += (_, _) => { _app?.SignOutTdxSso(); PopulateAuthPopup(); };
                }
                else
                {
                    tdxBtn.Content = "Sign In";
                    tdxBtn.Click += (_, _) => { _app?.ShowTdxSsoLogin(_ => Dispatcher.Invoke(PopulateAuthPopup)); };
                }
                return tdxBtn;

            case AuthSystemId.DevOps:
                var devOpsBtn = new Button { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
                if (system.State.IsHealthy)
                {
                    devOpsBtn.Content = "Sign Out";
                    devOpsBtn.Click += (_, _) => { _app?.SignOutDevOpsSso(); PopulateAuthPopup(); };
                }
                else
                {
                    devOpsBtn.Content = "Sign In";
                    devOpsBtn.Click += (_, _) => { _app?.ShowDevOpsSsoLogin(_ => Dispatcher.Invoke(PopulateAuthPopup)); };
                }
                return devOpsBtn;

            default:
                return null;
        }
    }

    private static TextBlock AuthDetailRow(string label, string value)
    {
        var tb = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
        tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(Colors.Gray) });
        tb.Inlines.Add(new System.Windows.Documents.Run(value) { FontFamily = new FontFamily("Consolas") });
        return tb;
    }

    private static Color StateColor(AuthTokenState state) => state.Kind switch
    {
        AuthStateKind.Valid => Colors.Green,
        AuthStateKind.Configured => Colors.Goldenrod,
        AuthStateKind.Authenticating => Colors.DodgerBlue,
        AuthStateKind.Expired => Colors.Orange,
        AuthStateKind.Failed => Colors.Red,
        AuthStateKind.ServicePrincipal => Colors.Orange,
        _ => Colors.Gray
    };

    private static string CategoryGlyph(AuthCategory cat) => cat switch
    {
        AuthCategory.Devices => "\uE7F8",
        AuthCategory.Inventory => "\uE7B8",
        AuthCategory.Tickets => "\uE8EA",
        AuthCategory.Projects => "\uE770",
        AuthCategory.Identity => "\uE8FA",
        _ => "\uE946"
    };

    private void OnDismissErrorClicked(object sender, RoutedEventArgs e)
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    public void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    // ── Data types ────────────────────────────────────────────────

    private record ActivityItem(string IconGlyph, string Text, DateTime Timestamp, string Tab, string? DeviceId = null, int? TicketId = null);
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
