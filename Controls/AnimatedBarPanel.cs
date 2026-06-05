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

    /// <summary>An element that should not be auto-animated (because it's being dragged).</summary>
    public UIElement? DragExempt { get; set; }

    private readonly Dictionary<UIElement, double> _lastX = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, h = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            w += child.DesiredSize.Width + Spacing;
            h = Math.Max(h, child.DesiredSize.Height);
        }
        if (InternalChildren.Count > 0) w -= Spacing;
        return new Size(w, double.IsInfinity(availableSize.Height) ? h : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double cw = child.DesiredSize.Width;
            double ch = child.DesiredSize.Height;
            double y = (finalSize.Height - ch) / 2.0;
            child.Arrange(new Rect(x, y, cw, ch));

            AnimateToSlot(child, x);
            x += cw + Spacing;
        }
        return finalSize;
    }

    private void AnimateToSlot(UIElement child, double newX)
    {
        var tt = EnsureTransform(child);

        if (_lastX.TryGetValue(child, out double oldX))
        {
            double delta = oldX - newX;
            if (Math.Abs(delta) > 0.5 && !ReferenceEquals(child, DragExempt))
            {
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
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

        _lastX[child] = newX;
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
