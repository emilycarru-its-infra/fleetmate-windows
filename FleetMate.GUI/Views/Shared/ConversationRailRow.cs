using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Shared;

/// <summary>A neutral author avatar outside the card, with continuous root/reply connectors in the gutter.</summary>
public sealed class ConversationRailRow : Decorator
{
    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(nameof(Author), typeof(string), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsReplyProperty = DependencyProperty.Register(nameof(IsReply), typeof(bool), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsFirstReplyProperty = DependencyProperty.Register(nameof(IsFirstReply), typeof(bool), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ContinuesProperty = DependencyProperty.Register(nameof(Continues), typeof(bool), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RailBrushProperty = DependencyProperty.Register(nameof(RailBrush), typeof(Brush), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(SystemColors.ControlDarkBrush, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AvatarBrushProperty = DependencyProperty.Register(nameof(AvatarBrush), typeof(Brush), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(SystemColors.ControlLightBrush, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InitialsBrushProperty = DependencyProperty.Register(nameof(InitialsBrush), typeof(Brush), typeof(ConversationRailRow),
        new FrameworkPropertyMetadata(SystemColors.GrayTextBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public ConversationRailRow()
    {
        SetResourceReference(RailBrushProperty, "SystemControlForegroundBaseLowBrush");
        SetResourceReference(AvatarBrushProperty, "SystemControlBackgroundBaseLowBrush");
        SetResourceReference(InitialsBrushProperty, "SystemControlForegroundBaseMediumBrush");
    }

    public Brush RailBrush { get => (Brush)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }
    public Brush AvatarBrush { get => (Brush)GetValue(AvatarBrushProperty); set => SetValue(AvatarBrushProperty, value); }
    public Brush InitialsBrush { get => (Brush)GetValue(InitialsBrushProperty); set => SetValue(InitialsBrushProperty, value); }
    public string Author { get => (string)GetValue(AuthorProperty); set => SetValue(AuthorProperty, value); }
    public bool IsReply { get => (bool)GetValue(IsReplyProperty); set => SetValue(IsReplyProperty, value); }
    public bool IsFirstReply { get => (bool)GetValue(IsFirstReplyProperty); set => SetValue(IsFirstReplyProperty, value); }
    public bool Continues { get => (bool)GetValue(ContinuesProperty); set => SetValue(ContinuesProperty, value); }
    private double Gutter => IsReply ? 72 : 44;

    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(Math.Max(0, constraint.Width - Gutter), constraint.Height));
        return new Size((Child?.DesiredSize.Width ?? 0) + Gutter, Math.Max(44, Child?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(Gutter, 0, Math.Max(0, arrangeSize.Width - Gutter), arrangeSize.Height));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var line = RailBrush;
        var fill = AvatarBrush;
        var foreground = InitialsBrush;
        var pen = new Pen(line, 1.5);
        var center = IsReply ? 44d : 16d;
        if (IsReply && IsFirstReply)
        {
            var elbow = new StreamGeometry();
            using (var geometry = elbow.Open())
            {
                geometry.BeginFigure(new Point(16, 0), false, false);
                geometry.LineTo(new Point(16, 10), true, false);
                geometry.BezierTo(new Point(16, 18), new Point(20, 22), new Point(28, 22), true, false);
                geometry.LineTo(new Point(30, 22), true, false);
            }
            drawing.DrawGeometry(null, pen, elbow);
        }
        else if (IsReply) drawing.DrawLine(pen, new Point(center, 0), new Point(center, 8));
        if (Continues) drawing.DrawLine(pen, new Point(center, 36), new Point(center, ActualHeight));
        drawing.DrawEllipse(fill, null, new Point(center, 22), 14, 14);
        var initials = new FormattedText(Initials(Author), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(initials, new Point(center - initials.Width / 2, 22 - initials.Height / 2));
    }

    public static string Initials(string? author)
    {
        var words = (author ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        var first = StringInfo.GetNextTextElement(words[0]);
        return (words.Length == 1 ? first : first + StringInfo.GetNextTextElement(words[^1])).ToUpperInvariant();
    }
}
