using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lintel.Controls;

/// <summary>
/// A horizontal panel whose children glide to their new slots whenever the order changes —
/// the FLIP-style motion behind the "widgets dynamically move around" feel. Children are
/// laid out left-to-right and vertically centred.
/// </summary>
public sealed class AnimatedBarPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(AnimatedBarPanel),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>Lay children left-to-right (Horizontal, default) or top-to-bottom (Vertical, side bars).</summary>
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(AnimatedBarPanel),
            new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    private bool Vert => Orientation == Orientation.Vertical;

    /// <summary>If set, a thin vertical line is drawn in the gap between adjacent widgets (Mond theme).</summary>
    public static readonly DependencyProperty DividerBrushProperty =
        DependencyProperty.Register(nameof(DividerBrush), typeof(Brush), typeof(AnimatedBarPanel),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? DividerBrush { get => (Brush?)GetValue(DividerBrushProperty); set => SetValue(DividerBrushProperty, value); }

    /// <summary>An element that should not be auto-animated (because it's being dragged).</summary>
    public UIElement? DragExempt { get; set; }

    /// <summary>Global toggle — Potato mode turns the reflow animation off.</summary>
    public static bool AnimationsEnabled = true;

    private readonly Dictionary<UIElement, double> _lastX = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        double along = 0, cross = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(Vert ? new Size(availableSize.Width, double.PositiveInfinity)
                               : new Size(double.PositiveInfinity, availableSize.Height));
            if (Vert) { along += child.DesiredSize.Height + Spacing; cross = Math.Max(cross, child.DesiredSize.Width); }
            else { along += child.DesiredSize.Width + Spacing; cross = Math.Max(cross, child.DesiredSize.Height); }
        }
        if (InternalChildren.Count > 0) along -= Spacing;
        return Vert
            ? new Size(double.IsInfinity(availableSize.Width) ? cross : availableSize.Width, along)
            : new Size(along, double.IsInfinity(availableSize.Height) ? cross : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double pos = 0;
        foreach (UIElement child in InternalChildren)
        {
            double cw = child.DesiredSize.Width;
            double ch = child.DesiredSize.Height;
            if (Vert)
            {
                double x = (finalSize.Width - cw) / 2.0;
                child.Arrange(new Rect(x, pos, cw, ch));
                AnimateToSlot(child, pos);
                pos += ch + Spacing;
            }
            else
            {
                double y = (finalSize.Height - ch) / 2.0;
                child.Arrange(new Rect(pos, y, cw, ch));
                AnimateToSlot(child, pos);
                pos += cw + Spacing;
            }
        }
        if (DividerBrush != null) InvalidateVisual();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (DividerBrush == null || InternalChildren.Count < 2) return;

        var pen = new Pen(DividerBrush, 1); pen.Freeze();
        double pos = 0;
        if (Vert)
        {
            double w = RenderSize.Width;
            double inset = Math.Max(4, w * 0.24);
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                pos += InternalChildren[i].DesiredSize.Height;
                if (i < InternalChildren.Count - 1)
                {
                    double lineY = Math.Round(pos + Spacing / 2.0) + 0.5;
                    dc.DrawLine(pen, new Point(inset, lineY), new Point(w - inset, lineY));
                    pos += Spacing;
                }
            }
            return;
        }
        double h = RenderSize.Height;
        double hinset = Math.Max(4, h * 0.24);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            pos += InternalChildren[i].DesiredSize.Width;
            if (i < InternalChildren.Count - 1)
            {
                double lineX = Math.Round(pos + Spacing / 2.0) + 0.5;   // crisp 1px line centred in the gap
                dc.DrawLine(pen, new Point(lineX, hinset), new Point(lineX, h - hinset));
                pos += Spacing;
            }
        }
    }

    private void AnimateToSlot(UIElement child, double newPos)
    {
        var tt = EnsureTransform(child);
        var axis = Vert ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        if (!AnimationsEnabled)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.X = 0; tt.Y = 0;
            _lastX[child] = newPos;
            return;
        }

        if (_lastX.TryGetValue(child, out double oldPos))
        {
            double delta = oldPos - newPos;
            if (Math.Abs(delta) > 0.5 && !ReferenceEquals(child, DragExempt))
            {
                tt.BeginAnimation(axis, new DoubleAnimation
                {
                    From = delta,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }
        else
        {
            // First appearance: fade in.
            child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        _lastX[child] = newPos;
    }

    private static TranslateTransform EnsureTransform(UIElement child)
    {
        if (child.RenderTransform is TranslateTransform existing) return existing;
        var trans = new TranslateTransform();
        child.RenderTransform = trans;
        return trans;
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement e) _lastX.Remove(e);
    }
}
