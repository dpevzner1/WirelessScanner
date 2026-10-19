using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>
/// RF spectrum / waterfall (DR-0010). Geometry comes from <see cref="SpectrumLayout"/> (unit-tested); this class only
/// turns it into WPF shapes. Identity colour follows the network (max 3, validated palette); everything else is a
/// dashed context outline, so the view is colourful without becoming a rainbow.
/// </summary>
public partial class SpectrumView : UserControl
{
    private const double MinDbm = -100, MaxDbm = -30;
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xFF, 0x88, 0x00));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0xAA, 0x44, 0x00));
    private static readonly Brush FaintBrush = Frozen(Color.FromRgb(0x44, 0x1F, 0x00));
    private static readonly Brush RefBrush = Frozen(Color.FromRgb(0x7A, 0x4A, 0x20));
    private static readonly Brush DfsBrush = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ContextBrush = Frozen((Color)ColorConverter.ConvertFromString(NetworkExplorerViewModel.ContextColor));
    private static readonly Brush ContextLightBrush = Frozen(Color.FromRgb(0x8A, 0x7F, 0x72));
    private static readonly Color LoadColor = Color.FromRgb(0xFF, 0x66, 0x00);
    private static readonly Color WaterColor = (Color)ColorConverter.ConvertFromString(NetworkExplorerViewModel.IdentityColors[0]);
    private static readonly FontFamily Mono = new("Consolas");

    private MainWindowViewModel? _vm;
    private bool _pending;

    public SpectrumView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void Attach(MainWindowViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.SpectrumChanged -= RequestRedraw;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.SpectrumChanged += RequestRedraw;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        Redraw();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.FilteredAccessPoints) or nameof(MainWindowViewModel.AccessPoints)) RequestRedraw();
    }

    /// <summary>Coalesce bursts of change notifications into one redraw on the UI thread.</summary>
    private void RequestRedraw()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(new Action(() => { _pending = false; Redraw(); }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>Bands to show: one band, or "All" = 2.4 + 5 GHz, plus 6 GHz when anything is heard there.</summary>
    internal static IReadOnlyList<SpectrumLayout.BandAxis> BandsFor(string band, IEnumerable<AccessPoint> heard) => band switch
    {
        "2.4 GHz" => new[] { SpectrumLayout.Band24 },
        "5 GHz" => new[] { SpectrumLayout.Band5 },
        "6 GHz" => new[] { SpectrumLayout.Band6 },
        _ => heard.Any(a => a.Band == "6 GHz")
            ? new[] { SpectrumLayout.Band24, SpectrumLayout.Band5, SpectrumLayout.Band6 }
            : new[] { SpectrumLayout.Band24, SpectrumLayout.Band5 }
    };

    public void Redraw()
    {
        Plot.Children.Clear();
        if (_vm == null || !IsVisible && IsLoaded) return;
        double w = Plot.ActualWidth, h = Plot.ActualHeight;
        if (w < 120 || h < 80) return;

        var all = _vm.AccessPoints?.ToList() ?? new List<AccessPoint>();
        var shown = _vm.FilteredAccessPoints?.ToList() ?? all;
        var bands = BandsFor(_vm.SpectrumBand, all);
        bool waterfall = _vm.IsWaterfallView;

        LegendText.Text = waterfall
            ? "Waterfall: strongest signal per channel, newest scan on top (last 60 fresh scans, all networks) · brighter = stronger"
            : _vm.IsSelectionAutoFocus
                ? "Colour = 3 strongest networks (auto) · dashed = other networks · click a shape to select it"
                : "Colour = first 3 selected networks (in selection order) · solid outline = more selected · click to toggle";

        double stripH = h / bands.Count;
        for (int i = 0; i < bands.Count; i++)
        {
            var area = new Rect(0, i * stripH, w, stripH);
            if (waterfall) DrawWaterfall(bands[i], area);
            else DrawSpectrum(bands[i], area, all, shown);
            if (i > 0) Plot.Children.Add(new Line { X1 = 8, X2 = w - 8, Y1 = area.Top, Y2 = area.Top, Stroke = FaintBrush, StrokeThickness = 1 });
        }
    }

    // ------------------------------------------------------------------ spectrum
    private void DrawSpectrum(SpectrumLayout.BandAxis axis, Rect area, List<AccessPoint> all, List<AccessPoint> shown)
    {
        const double left = 46, right = 14, header = 22, axisH = 16, loadH = 9, gap = 4;
        var plot = new Rect(area.Left + left, area.Top + header + 4, Math.Max(10, area.Width - left - right),
            Math.Max(10, area.Height - header - 4 - gap - loadH - axisH - 6));
        double X(double mhz) => plot.Left + (mhz - axis.LowMhz) / (axis.HighMhz - axis.LowMhz) * plot.Width;
        double Y(double dbm) => plot.Bottom - (Math.Clamp(dbm, MinDbm, MaxDbm) - MinDbm) / (MaxDbm - MinDbm) * plot.Height;

        var inBand = all.Where(a => a.Band == axis.Band && a.Channel > 0).ToList();
        var load = SpectrumLayout.ChannelLoad(axis, inBand);
        int leastBusy = SpectrumLayout.LeastBusyChannel(axis, load);

        // header
        AddText($"{axis.Band}", area.Left + 10, area.Top + 4, TextBrush, 12, FontWeights.Bold);
        AddText($"{inBand.Count} AP{(inBand.Count == 1 ? "" : "s")} heard · least busy: ch {leastBusy}" +
                (axis.Band == "2.4 GHz" ? " (of 1/6/11)" : axis.Band == "5 GHz" ? " (non-DFS first)" : " (PSC)"),
            area.Left + 74, area.Top + 6, MutedBrush, 11);

        // DFS
        if (axis.DfsLowMhz is double dl && axis.DfsHighMhz is double dh)
        {
            Add(new Rectangle { Width = X(dh) - X(dl), Height = plot.Height, Fill = DfsBrush, IsHitTestVisible = false, ToolTip = "DFS channels (radar detection may force a channel change)" }, X(dl), plot.Top);
            AddText("DFS", X(dl) + 4, plot.Top + 2, MutedBrush, 10);
        }

        // dBm grid (left labels) and reference thresholds (right labels)
        // short plots (several bands + capture log) get fewer labels so text never piles up
        int step = plot.Height >= 110 ? 10 : plot.Height >= 60 ? 20 : 40;
        for (int db = -40; db >= -90; db -= step)
        {
            Plot.Children.Add(new Line { X1 = plot.Left, X2 = plot.Right, Y1 = Y(db), Y2 = Y(db), Stroke = FaintBrush, StrokeThickness = 0.6 });
            AddText($"{db}", area.Left + 8, Y(db) - 7, MutedBrush, 10, family: Mono);
        }
        // -67 and -70 are 3 dB apart: label one above and one below its line so they never collide
        foreach (var (db, name, below) in new[] { (-67, "−67 good", false), (-70, "−70 fair", true), (-80, "−80 weak", true) })
        {
            Plot.Children.Add(new Line { X1 = plot.Left, X2 = plot.Right, Y1 = Y(db), Y2 = Y(db), Stroke = RefBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 5, 4 }, IsHitTestVisible = false,
                ToolTip = name });
            if (plot.Height < 90) continue;
            var t = AddText(name, 0, 0, MutedBrush, 9.5);
            t.IsHitTestVisible = false;
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, plot.Right - t.DesiredSize.Width - 2);
            Canvas.SetTop(t, below ? Y(db) + 1 : Y(db) - t.DesiredSize.Height);
        }

        // channel ticks
        foreach (var ch in axis.TickChannels)
        {
            double x = X(SpectrumLayout.ChannelFrequencyMhz(axis.Band, ch));
            if (x < plot.Left - 1 || x > plot.Right + 1) continue;
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = plot.Top, Y2 = plot.Bottom, Stroke = FaintBrush, StrokeThickness = 0.5, StrokeDashArray = new DoubleCollection { 2, 3 } });
            var t = AddText(ch.ToString(), 0, 0, MutedBrush, 9.5, family: Mono);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, x - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, plot.Bottom + gap + loadH + 3);
        }

        // channel load strip (one hue, opacity = load) + least-busy marker
        double maxLoad = Math.Max(1.0, load.Values.DefaultIfEmpty(0).Max());
        double spacing = axis.Band == "2.4 GHz" ? 5 : 20;
        foreach (var (ch, l) in load)
        {
            double f = SpectrumLayout.ChannelFrequencyMhz(axis.Band, ch);
            double x0 = X(f - spacing / 2) + 0.5, x1 = X(f + spacing / 2) - 0.5;
            var cell = new Rectangle
            {
                Width = Math.Max(1, x1 - x0), Height = loadH, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(l <= 0 ? 0x14 : 0x30 + 0xCF * Math.Min(1, l / maxLoad)), LoadColor.R, LoadColor.G, LoadColor.B)),
                ToolTip = $"ch {ch}: load {l:0.0} (sum of signal weights of overlapping APs){(ch == leastBusy ? " · least busy" : "")}"
            };
            if (ch == leastBusy)
            {
                // least-busy channel: outlined cell (the header names it too, so it is not colour-only)
                cell.Stroke = TextBrush;
                cell.StrokeThickness = 1.5;
            }
            Add(cell, x0, plot.Bottom + gap);
        }

        // AP masks: context first, focus on top; weaker first so strong shapes stay visible
        var shapes = shown.Where(a => a.Band == axis.Band && a.Channel > 0)
            .Select(a => (Ap: a, Slot: _vm!.FocusSlot(a.SSID), Sel: !_vm.Selection.IsEmpty && _vm.Selection.IsExplicitlySelected(a.SSID, a.BSSID)))
            .OrderBy(s => s.Slot >= 0).ThenBy(s => s.Ap.RSSI).ToList();
        var placed = new List<Rect>();
        foreach (var (ap, slot, sel) in shapes)
        {
            var (lo, hi) = SpectrumLayout.Occupancy(ap);
            double xl = X(Math.Max(lo, axis.LowMhz)), xr = X(Math.Min(hi, axis.HighMhz));
            double ramp = Math.Min(Math.Max(2, (xr - xl) * 0.12), 10);
            double top = Y(ap.RSSI), bottom = plot.Bottom;
            var poly = new Polygon
            {
                Points = new PointCollection { new(xl, bottom), new(xl + ramp, top), new(xr - ramp, top), new(xr, bottom) },
                StrokeLineJoin = PenLineJoin.Round, Cursor = Cursors.Hand,
                ToolTip = $"{Name(ap)}\n{ap.BSSID} · {ap.VendorOUI}\nch {ap.Channel} · {ap.Band} · {(ap.ChannelWidth is int cw ? cw + " MHz" : "width ?")}\n" +
                          $"RSSI {ap.RSSI} dBm · quality {ap.Quality}% · {ap.SecurityType}\nClick to {(sel ? "remove from" : "add to")} the selection"
            };
            if (slot >= 0)
            {
                var c = (Color)ColorConverter.ConvertFromString(NetworkExplorerViewModel.IdentityColors[slot]);
                poly.Stroke = new SolidColorBrush(c);
                poly.StrokeThickness = 2;
                poly.Fill = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
            }
            else if (sel)
            {
                poly.Stroke = ContextLightBrush;
                poly.StrokeThickness = 1.5;
                poly.Fill = Brushes.Transparent;
            }
            else
            {
                poly.Stroke = ContextLightBrush;
                poly.StrokeThickness = 1;
                poly.StrokeDashArray = new DoubleCollection { 3, 3 };
                poly.Fill = Brushes.Transparent;
                poly.Opacity = 0.75;
            }
            var hit = ap;
            poly.MouseLeftButtonUp += (_, e) => { _vm?.ToggleSpectrumAp(hit); e.Handled = true; };
            Plot.Children.Add(poly);

            // labels: focused always, others only with "Label all"; skip/shift on collision
            if (slot < 0 && !_vm!.LabelAll) continue;
            var label = new TextBlock { FontSize = 10, FontFamily = Mono, IsHitTestVisible = false };
            if (slot >= 0)
            {
                var c = (Color)ColorConverter.ConvertFromString(NetworkExplorerViewModel.IdentityColors[slot]);
                label.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = new SolidColorBrush(c) });
            }
            label.Inlines.Add(new System.Windows.Documents.Run($"{Name(ap)} {ap.RSSI}") { Foreground = slot >= 0 ? TextBrush : MutedBrush });
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = label.DesiredSize;
            double lxx = Math.Clamp((xl + xr) / 2 - size.Width / 2, plot.Left, Math.Max(plot.Left, plot.Right - size.Width));
            double ly = top - size.Height - 1;
            var r = new Rect(lxx, ly, size.Width, size.Height);
            int tries = 0;
            while (placed.Any(p => p.IntersectsWith(r)) && tries++ < 6)
            {
                r.Y -= size.Height;
            }
            if (r.Top < plot.Top - 2 || placed.Any(p => p.IntersectsWith(r)))
            {
                if (slot < 0) continue;              // context labels are dropped rather than overlapping
                r.Y = Math.Max(plot.Top, r.Y);
            }
            placed.Add(r);
            Add(label, r.X, r.Y);
        }

        if (inBand.Count == 0)
            AddText("Nothing heard on this band in the last scan.", plot.Left + 10, plot.Top + plot.Height / 2 - 8, MutedBrush, 11, style: FontStyles.Italic);
    }

    // ------------------------------------------------------------------ waterfall
    private void DrawWaterfall(SpectrumLayout.BandAxis axis, Rect area)
    {
        const double left = 46, right = 14, header = 22, axisH = 16;
        var plot = new Rect(area.Left + left, area.Top + header + 4, Math.Max(10, area.Width - left - right), Math.Max(10, area.Height - header - 4 - axisH - 6));
        double X(double mhz) => plot.Left + (mhz - axis.LowMhz) / (axis.HighMhz - axis.LowMhz) * plot.Width;
        var history = _vm!.WaterfallHistory;
        int rows = MainWindowViewModel.WaterfallDepth;
        double rowH = plot.Height / rows;

        AddText(axis.Band, area.Left + 10, area.Top + 4, TextBrush, 12, FontWeights.Bold);
        AddText($"{history.Count} of {rows} scans", area.Left + 74, area.Top + 6, MutedBrush, 11);
        AddText("now", area.Left + 8, plot.Top - 2, MutedBrush, 10);
        AddText($"−{rows}", area.Left + 8, plot.Bottom - 12, MutedBrush, 10);
        Add(new Rectangle { Width = plot.Width, Height = plot.Height, Stroke = FaintBrush, StrokeThickness = 1 }, plot.Left, plot.Top);

        if (axis.DfsLowMhz is double dl && axis.DfsHighMhz is double dh)
            Add(new Rectangle { Width = X(dh) - X(dl), Height = plot.Height, Fill = DfsBrush, IsHitTestVisible = false }, X(dl), plot.Top);

        double spacing = axis.Band == "2.4 GHz" ? 5 : 20;
        for (int r = 0; r < history.Count && r < rows; r++)
        {
            var bins = SpectrumLayout.StrongestPerChannel(axis, history[r]);
            foreach (var (ch, rssi) in bins)
            {
                if (rssi is not int v) continue;
                double f = SpectrumLayout.ChannelFrequencyMhz(axis.Band, ch);
                double x0 = X(f - spacing / 2), x1 = X(f + spacing / 2);
                double k = SpectrumLayout.Intensity(v);
                var cell = new Rectangle
                {
                    Width = Math.Max(1, x1 - x0 - 0.5), Height = Math.Max(1, rowH - 0.3),
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(0x18 + 0xE7 * k), WaterColor.R, WaterColor.G, WaterColor.B)),
                    ToolTip = $"ch {ch} · strongest {v} dBm · scan −{r}"
                };
                Add(cell, x0, plot.Top + r * rowH);
            }
        }
        foreach (var ch in axis.TickChannels)
        {
            double x = X(SpectrumLayout.ChannelFrequencyMhz(axis.Band, ch));
            if (x < plot.Left - 1 || x > plot.Right + 1) continue;
            var t = AddText(ch.ToString(), 0, 0, MutedBrush, 9.5, family: Mono);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, x - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, plot.Bottom + 3);
        }
        if (history.Count == 0)
            AddText("Waiting for fresh scans…", plot.Left + 10, plot.Top + plot.Height / 2 - 8, MutedBrush, 11, style: FontStyles.Italic);
    }

    // ------------------------------------------------------------------ helpers
    private static string Name(AccessPoint ap) => string.IsNullOrEmpty(ap.SSID) ? "<Hidden SSID>" : ap.SSID;

    private void Add(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        Plot.Children.Add(e);
    }

    private TextBlock AddText(string text, double x, double y, Brush brush, double size, FontWeight? weight = null, FontFamily? family = null, FontStyle? style = null)
    {
        var t = new TextBlock { Text = text, Foreground = brush, FontSize = size, FontWeight = weight ?? FontWeights.Normal, FontStyle = style ?? FontStyles.Normal };
        if (family != null) t.FontFamily = family;
        Add(t, x, y);
        return t;
    }
}
