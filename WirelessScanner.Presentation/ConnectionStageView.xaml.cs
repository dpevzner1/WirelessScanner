using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WirelessScanner.Application;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>Centre stage of the connection test (DR-0014): stacked panels share the time axis, each with its own y scale (no dual axis).</summary>
public partial class ConnectionStageView : UserControl
{
    private ConnectionTestViewModel? _vm;

    public ConnectionStageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.ChartDataChanged -= Redraw;
            _vm = DataContext as ConnectionTestViewModel;
            if (_vm != null) _vm.ChartDataChanged += Redraw;
            Redraw();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(Redraw)); };
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    public void Redraw()
    {
        if (_vm == null) return;
        if (_vm.IsRunningStep && _vm.LiveReport is { } live) ConnectionWpfCharts.Draw(LiveChart, live);
        if (_vm.IsResults && _vm.Report is { } report) ConnectionWpfCharts.Draw(ResultChart, report);
    }
}

public static class ConnectionWpfCharts
{
    private static readonly Brush Axis = new SolidColorBrush(Color.FromRgb(0x5B, 0x52, 0x46));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xAA, 0x44, 0x00));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x39, 0x87, 0xE5));
    private static readonly Brush Signal = new SolidColorBrush(Color.FromRgb(0x19, 0x9E, 0x70));
    private static readonly Brush Fail = new SolidColorBrush(Color.FromRgb(0xD0, 0x3B, 0x3B));
    private static readonly Brush Roam = new SolidColorBrush(Color.FromRgb(0xC2, 0xB5, 0xA3));

    public static void Draw(Canvas canvas, ConnectionReport r)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 80 || h < 60) return;
        var panels = new List<(string Title, ConnectionSegment? Seg)>();
        foreach (var seg in new[] { ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns })
            if (r.Probes.Any(p => p.Segment == seg)) panels.Add((ConnectionAnalyzer.Name(seg), seg));
        if (r.LinkSamples.Any(l => l.Rssi.HasValue)) panels.Add(("Wi-Fi signal", null));
        if (panels.Count == 0) return;
        double xMax = Math.Max(1, r.ActiveSeconds);
        double ph = h / panels.Count;
        for (int i = 0; i < panels.Count; i++)
        {
            double top = i * ph;
            if (panels[i].Seg is ConnectionSegment seg) DrawProbes(canvas, r, seg, panels[i].Title, top, ph, w, xMax, i == panels.Count - 1);
            else DrawSignal(canvas, r, top, ph, w, xMax);
        }
    }

    private static void Label(Canvas c, string text, double x, double y, double size = 10, Brush? brush = null)
    {
        var t = new TextBlock { Text = text, Foreground = brush ?? Ink, FontSize = size };
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        c.Children.Add(t);
    }

    private static void Frame(Canvas c, string title, double top, double ph, double w, double yMin, double yMax, string unit, out Func<double, double> X, out Func<double, double> Y, double xMax)
    {
        const double left = 44, right = 6;
        double pt = top + 14, pb = top + ph - 6, pw = w - left - right;
        X = s => left + s / xMax * pw;
        double span = Math.Max(1e-6, yMax - yMin);
        Y = v => pb - (v - yMin) / span * (pb - pt);
        c.Children.Add(new System.Windows.Shapes.Line { X1 = left, X2 = left, Y1 = pt, Y2 = pb, Stroke = Axis, StrokeThickness = 1 });
        c.Children.Add(new System.Windows.Shapes.Line { X1 = left, X2 = left + pw, Y1 = pb, Y2 = pb, Stroke = Axis, StrokeThickness = 1 });
        Label(c, title, left + 4, top, 10.5, new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00)));
        Label(c, $"{yMax:0} {unit}", 2, pt - 6, 9.5);
        Label(c, $"{yMin:0}", 2, pb - 12, 9.5);
    }

    private static void DrawProbes(Canvas c, ConnectionReport r, ConnectionSegment seg, string title, double top, double ph, double w, double xMax, bool last)
    {
        var method = ConnectionAnalyzer.PrimaryMethod(seg, r.Probes);
        var pts = r.Probes.Where(p => p.Segment == seg && p.Method == method).ToList();
        var ok = pts.Where(p => p.Outcome == ProbeOutcome.Ok && p.RttMs.HasValue).ToList();
        double yMax = ok.Count == 0 ? 10 : Math.Max(10, (ConnectionAnalyzer.Percentile(ok.Select(p => p.RttMs!.Value).OrderBy(v => v).ToList(), 99) ?? 10) * 1.15);
        Frame(c, title, top, ph, w, 0, yMax, "ms", out var X, out var Y, xMax);
        foreach (var g in ok.GroupBy(p => p.Target))
        {
            var line = new Polyline { Stroke = Line, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in g) line.Points.Add(new Point(X(p.ElapsedMs / 1000.0), Y(Math.Min(p.RttMs!.Value, yMax))));
            c.Children.Add(line);
        }
        foreach (var p in pts.Where(p => p.Outcome != ProbeOutcome.Ok))
        {
            double x = X(p.ElapsedMs / 1000.0);
            c.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = Y(yMax), Y2 = Y(yMax * 0.8), Stroke = Fail, StrokeThickness = 1.5 });
        }
        foreach (var e in r.Events.Where(e => e.Type == ConnectionEventType.Outage && e.Segment == seg))
        {
            double x1 = X(e.StartElapsedMs / 1000.0), x2 = X((e.EndElapsedMs ?? (long)(r.ActiveSeconds * 1000)) / 1000.0);
            var band = new Rectangle { Width = Math.Max(2, x2 - x1), Height = Math.Max(4, ph - 22), Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xD0, 0x3B, 0x3B)) };
            Canvas.SetLeft(band, x1);
            Canvas.SetTop(band, top + 14);
            c.Children.Add(band);
        }
        if (last) Label(c, TimeSpan.FromSeconds(xMax).ToString(@"hh\:mm\:ss"), X(xMax) - 48, top + ph - 16, 9.5);
    }

    private static void DrawSignal(Canvas c, ConnectionReport r, double top, double ph, double w, double xMax)
    {
        var pts = r.LinkSamples.Where(l => l.Rssi.HasValue).ToList();
        double yMin = Math.Min(-90, pts.Min(l => l.Rssi!.Value) - 3), yMax = Math.Max(-30, pts.Max(l => l.Rssi!.Value) + 3);
        Frame(c, "Wi-Fi signal (lines = roams)", top, ph, w, yMin, yMax, "dBm", out var X, out var Y, xMax);
        var line = new Polyline { Stroke = Signal, StrokeThickness = 1.2 };
        foreach (var l in pts) line.Points.Add(new Point(X(l.ElapsedMs / 1000.0), Y(l.Rssi!.Value)));
        c.Children.Add(line);
        foreach (var e in r.Events.Where(e => e.Type == ConnectionEventType.Roam))
        {
            double x = X(e.StartElapsedMs / 1000.0);
            c.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = Y(yMax), Y2 = Y(yMin), Stroke = Roam, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        }
        Label(c, TimeSpan.FromSeconds(xMax).ToString(@"hh\:mm\:ss"), X(xMax) - 48, top + ph - 16, 9.5);
    }
}
