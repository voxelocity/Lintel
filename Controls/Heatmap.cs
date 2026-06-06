using System.Windows;
using System.Windows.Media;

namespace Lintel.Controls;

/// <summary>
/// A GitHub-style contribution grid — columns are weeks, rows are weekdays, each cell shaded
/// by its value. Used by the Claude usage and GitHub commit widgets.
/// </summary>
public sealed class Heatmap : FrameworkElement
{
    private double[] _values = Array.Empty<double>();   // oldest .. newest (last = most recent day)
    private Color _accent = Color.FromRgb(0x39, 0xD3, 0x53);
    private const int Rows = 7;
    private const double Cell = 11, Gap = 3;

    /// <summary>Feed daily values ending today. <paramref name="accent"/> is the "full" colour.</summary>
    public void SetData(IReadOnlyList<double> daily, Color accent)
    {
        _values = daily.ToArray();
        _accent = accent;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private int Weeks => Math.Max(1, (int)Math.Ceiling(_values.Length / (double)Rows));

    protected override Size MeasureOverride(Size availableSize)
        => new(Weeks * (Cell + Gap) - Gap, Rows * (Cell + Gap) - Gap);

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length == 0) return;

        double max = 0;
        foreach (var v in _values) if (v > max) max = v;

        var empty = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)); empty.Freeze();

        // Lay out so the newest day lands in the bottom-right cell.
        int n = _values.Length;
        for (int i = 0; i < n; i++)
        {
            int fromEnd = n - 1 - i;
            int col = Weeks - 1 - (fromEnd / Rows);
            int row = Rows - 1 - (fromEnd % Rows);

            double v = _values[i];
            Brush b;
            if (max <= 0 || v <= 0) b = empty;
            else
            {
                double t = Math.Sqrt(v / max);                 // emphasise low activity a little
                byte a = (byte)Math.Clamp(60 + t * 195, 40, 255);
                var c = Color.FromArgb(a, _accent.R, _accent.G, _accent.B);
                b = new SolidColorBrush(c);
            }

            double x = col * (Cell + Gap);
            double y = row * (Cell + Gap);
            dc.DrawRoundedRectangle(b, null, new Rect(x, y, Cell, Cell), 2.5, 2.5);
        }
    }
}
