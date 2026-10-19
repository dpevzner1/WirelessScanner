using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>Saturation test view. Charts are drawn on WPF Canvas, like the rest of the app's charts.</summary>
public partial class SaturationTestView : UserControl
{
    private SaturationTestViewModel? _vm;

    public SaturationTestView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.ChartDataChanged -= Redraw;
            _vm = DataContext as SaturationTestViewModel;
            if (_vm != null) _vm.ChartDataChanged += Redraw;
        };
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm == null) return;
        if (_vm.IsRunningStep && _vm.LiveReport is { } live)
        {
            SaturationCharts.Draw(LiveChart, live, live.Targets.Select(t => t.Bssid).Take(SaturationCharts.Palette.Length).ToList());
        }
        if (_vm.IsResults && _vm.Report is { } report && _vm.SelectedResultTarget is { } sel)
        {
            SaturationCharts.Draw(ResultChart, report, new List<string> { sel.Bssid });
        }
    }
}

/// <summary>RSSI timeline drawing shared by the standalone saturation screen and the Live Survey stage (DR-0011).</summary>
public static class SaturationCharts
{
    /// <summary>First three = the validated identity colours used by the spectrum (DR-0010); targets beyond 3 are also direct-labelled.</summary>
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0x39, 0x87, 0xE5), Color.FromRgb(0xD9, 0x59, 0x26), Color.FromRgb(0x19, 0x9E, 0x70), Color.FromRgb(0xFF, 0xBB, 0x33),
        Color.FromRgb(0xCC, 0x66, 0xFF), Color.FromRgb(0xFF, 0x33, 0x66), Color.FromRgb(0x99, 0xFF, 0x33), Color.FromRgb(0xFF, 0xFF, 0x66)
    };

    public static void Draw(Canvas canvas, SaturationReport r, IReadOnlyList<string> bssids)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 50 || h < 50) return;
        const double left = 40, right = 8, top = 8, bottom = 22;
        double pw = w - left - right, ph = h - top - bottom;
        var th = r.Config.Thresholds;
        var cycles = r.Cycles;
        double maxT = Math.Max(r.Config.Interval.TotalSeconds * 4, cycles.Count > 0 ? cycles[^1].ElapsedMs / 1000.0 : 1);

        var observed = cycles.SelectMany(c => c.Observations).Where(o => o.Status == ObservationStatus.Observed && o.Rssi.HasValue
            && bssids.Contains(o.Bssid, StringComparer.OrdinalIgnoreCase)).Select(o => o.Rssi!.Value).ToList();
        int yMax = Math.Min(-20, (observed.Count > 0 ? observed.Max() : -40) + 5);
        int yMin = Math.Max(-100, Math.Min((observed.Count > 0 ? observed.Min() : -90) - 5, th.UnusableDbm - 5));
        double X(double s) => left + s / maxT * pw;
        double Y(double dbm) => top + (yMax - dbm) / (double)(yMax - yMin) * ph;

        void Band(double from, double to, byte a, Color c) =>
            Add(canvas, new Rectangle { Width = pw, Height = Math.Max(0, Y(to) - Y(from)), Fill = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)) }, left, Y(from));
        Band(th.GoodDbm, th.FairDbm, 0x22, Color.FromRgb(0xFF, 0xBB, 0x33));
        Band(th.FairDbm, th.UnusableDbm, 0x28, Color.FromRgb(0xFF, 0x77, 0x00));
        Band(th.UnusableDbm, yMin, 0x30, Color.FromRgb(0xFF, 0x33, 0x00));

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x18, 0x00));
        var labelBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0x44, 0x00));
        for (int db = (yMax / 10) * 10; db >= yMin; db -= 10)
        {
            canvas.Children.Add(new Line { X1 = left, X2 = left + pw, Y1 = Y(db), Y2 = Y(db), Stroke = gridBrush, StrokeThickness = 1 });
            Add(canvas, new TextBlock { Text = db.ToString(), Foreground = labelBrush, FontSize = 10 }, 4, Y(db) - 7);
        }
        for (int i = 0; i <= 4; i++)
        {
            double s = maxT * i / 4;
            var label = new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss"), Foreground = labelBrush, FontSize = 10 };
            Add(canvas, label, i == 0 ? X(s) : i == 4 ? X(s) - 46 : X(s) - 23, h - 18);
        }

        // markers
        var markerBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        foreach (var m in r.Markers)
        {
            double x = X(m.ElapsedMs / 1000.0);
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + ph, Stroke = markerBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            Add(canvas, new TextBlock { Text = m.Text, Foreground = markerBrush, FontSize = 10 }, x + 3, top);
        }

        for (int i = 0; i < bssids.Count; i++)
        {
            var color = bssids.Count == 1 ? Color.FromRgb(0xFF, 0x88, 0x00) : Palette[i % Palette.Length];
            var brush = new SolidColorBrush(color);
            var poly = new Polyline { Stroke = brush, StrokeThickness = 2 };
            foreach (var c in cycles)
            {
                var o = c.Observations.FirstOrDefault(x => string.Equals(x.Bssid, bssids[i], StringComparison.OrdinalIgnoreCase));
                if (o == null) continue;
                double x = X(c.ElapsedMs / 1000.0);
                if (o.Status == ObservationStatus.Observed && o.Rssi is int rssi)
                {
                    poly.Points.Add(new Point(x, Y(rssi)));
                }
                else
                {
                    // Break the line on any non-observed cycle; missing gets a red tick (not a fake value).
                    if (poly.Points.Count > 0) { canvas.Children.Add(poly); poly = new Polyline { Stroke = brush, StrokeThickness = 2 }; }
                    if (o.Status == ObservationStatus.Missing)
                        canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top + ph - 8, Y2 = top + ph, Stroke = Brushes.Red, StrokeThickness = 2 });
                }
            }
            if (poly.Points.Count > 0) canvas.Children.Add(poly);
            if (bssids.Count > 1)
            {
                var label = r.Targets.FirstOrDefault(t => t.Bssid == bssids[i]);
                Add(canvas, new TextBlock { Text = $"■ {label?.Ssid} {bssids[i][^5..]}", Foreground = brush, FontSize = 10, Background = Brushes.Black },
                    left + pw - 150 * (1 + i % 4), top + ph - 30 - (i / 4) * 13);
            }
        }
    }

    private static void Add(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }
}
