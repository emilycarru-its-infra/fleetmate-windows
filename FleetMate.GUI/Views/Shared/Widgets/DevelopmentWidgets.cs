using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Knowledge;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Development.Repos;
using P = FleetMate.GUI.Views.Shared.Widgets.WidgetPalette;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// Development's Graphs strip, one set per sub-tab, matching the macOS app.
/// The cards and every figure on them come from FleetMate.Core
/// (<see cref="DevelopmentGraphCards"/>), which FleetMate.WinUI draws too; this
/// only draws them. Repos carries the repository Insights instead, built by the page.
/// </summary>
public static partial class WidgetCatalog
{
    private static readonly Color[] RepositoryColors = { P.Blue, P.Purple, P.Orange, P.Teal, P.Green, P.Indigo, P.Brown, P.Pink };

    /// <summary>The strip for <paramref name="segment"/>. Repos has no catalog cards: the page hosts Insights there.</summary>
    public static List<UIElement> Development(App app, DevelopmentSegment segment, Action<string, string> filter)
    {
        var cards = segment switch
        {
            DevelopmentSegment.Pulls => DevelopmentGraphCards.Pulls(app.DevelopmentPullRequests, app.IsCacheLoading("PullRequests"), DateTime.Now),
            DevelopmentSegment.Commits => DevelopmentGraphCards.Commits(app.DevelopmentCommits, app.IsCacheLoading("Commits"), DateTime.Now),
            DevelopmentSegment.Pipelines => DevelopmentGraphCards.Pipelines(app.DevelopmentRuns, app.IsCacheLoading("Runs"), DateTime.Now),
            DevelopmentSegment.Skills => DevelopmentGraphCards.Skills(app.Skills?.Entries ?? Array.Empty<SkillEntry>(), app.Skills?.IsSyncing == true),
            _ => new List<GraphCard>(),
        };
        void Open(GraphFilter? target)
        {
            if (target != null) filter(target.Category, target.Value);
        }
        return cards.Select(card => Draw(card, Open)).ToList();
    }

    private static Color ToneColor(ChartTone tone) => tone switch
    {
        ChartTone.Blue => P.Blue,
        ChartTone.Green => P.Green,
        ChartTone.Orange => P.Orange,
        ChartTone.Purple => P.Purple,
        ChartTone.Teal => P.Teal,
        ChartTone.Brown => P.Brown,
        ChartTone.Indigo => P.Indigo,
        ChartTone.Pink => P.Pink,
        ChartTone.Yellow => P.Yellow,
        _ => P.Gray,
    };

    private static Color BarColor(GraphBar bar, int index) =>
        bar.Tone is { } tone ? ToneColor(tone) : RepositoryColors[index % RepositoryColors.Length];

    private static UIElement Draw(GraphCard card, Action<GraphFilter?> open)
    {
        if (card.Kind == GraphCardKind.Kpis)
        {
            return WidgetCards.KpiStack(card.Kpis.Select(k => new KpiTile(k.Title, k.Value, "", P.Hex(ToneColor(k.Tone)), k.Loading,
                k.Open is { } target ? () => open(target) : null)));
        }

        UIElement body = card.Body switch
        {
            GraphCardBody.Loading => WidgetCards.Skeleton(),
            GraphCardBody.Empty => WidgetCards.Empty(card.Message),
            GraphCardBody.AllClear => WidgetCards.AllClear(card.Message, card.Detail),
            _ => Chart(card, open),
        };
        return WidgetCards.Card(card.Title, body, units: card.Units, loading: card.Loading);
    }

    private static UIElement Chart(GraphCard card, Action<GraphFilter?> open)
    {
        var bars = card.Bars;
        var slices = bars.Select(b => new ChartSlice(b.Label, b.Value)).ToList();
        var colors = bars.Select((b, i) => (b.Label, Color: BarColor(b, i))).GroupBy(x => x.Label).ToDictionary(g => g.Key, g => g.First().Color);
        Action<string>? click = bars.Any(b => b.Open != null)
            ? label => open(bars.FirstOrDefault(b => b.Label == label)?.Open)
            : null;

        return card.Kind switch
        {
            GraphCardKind.Donut => WidgetCards.Donut(slices, click, bars.Select((b, i) => P.Sk(BarColor(b, i))).ToList(), adaptive: true),
            GraphCardKind.Bars => WidgetCards.HorizontalBars(slices, label => colors.GetValueOrDefault(label, P.Gray), click),
            GraphCardKind.Columns => DayColumns(card.Columns.Select(c => c.Start).ToList(),
                card.Series.Select(s => (s.Name, (Brush)SeriesBrush(s.Tone), s.Values)).ToList(), card.Legend, card.Bucket),
            GraphCardKind.List => WidgetCards.List(card.Title,
                card.Rows.Select(r => new ListRow(r.Title, r.Detail, r.Open is { } target ? () => open(target) : null)).ToList(),
                visible: card.VisibleRows),
            _ => WidgetCards.Empty(card.Message),
        };
    }

    /// <summary>A single-series column chart takes the Repos accent, as before; stacked series take their tones.</summary>
    private static Brush SeriesBrush(ChartTone? tone) => tone is { } t and not ChartTone.Blue ? Frozen(ToneColor(t)) : RepoBrushes.Accent;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Columns per day (or week), stacked when there is more than one series; hover names the period and its values. The chart takes whatever height its card has.</summary>
    private static UIElement DayColumns(IReadOnlyList<DateTime> days, IReadOnlyList<(string Name, Brush Brush, IReadOnlyList<int> Values)> series,
        bool legend, RepoStatsBucket bucket = RepoStatsBucket.Day)
    {
        var chart = new TimelineChart { MinHeight = 130 };
        chart.ShowStacked(days.Select(d => new DateTimeOffset(d)).ToList(), series, bucket);
        if (!legend) return chart;
        var key = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (name, brush, _) in series)
        {
            key.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            key.Children.Add(new TextBlock { Text = name, FontSize = WidgetCards.CaptionSize, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        var panel = new DockPanel();
        DockPanel.SetDock(key, Dock.Bottom);
        panel.Children.Add(key);
        panel.Children.Add(chart);
        return panel;
    }
}
