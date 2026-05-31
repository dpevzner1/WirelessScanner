using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    private static readonly Color[] AmberPalette = {
        Color.FromRgb(0xFF, 0x66, 0x00), // Primary Orange
        Color.FromRgb(0xFF, 0x99, 0x00), // Amber/Gold
        Color.FromRgb(0xFF, 0x33, 0x00), // Red-Orange
        Color.FromRgb(0xFF, 0xBB, 0x33), // Yellow-Orange
        Color.FromRgb(0xE6, 0x51, 0x00), // Dark Orange
        Color.FromRgb(0xF5, 0x7C, 0x00), // Medium Orange
        Color.FromRgb(0xFF, 0xAA, 0x55)  // Light Peach
    };

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        
        _viewModel = viewModel;
        DataContext = _viewModel;
        
        // Subscribe to PropertyChanged to redraw the spectrum when access points update or view swaps
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        
        var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (hwndSource != null)
        {
            hwndSource.AddHook(WndProc);
        }
    }

    private const int WM_DEVICECHANGE = 0x0219;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            // Windows notifications of PNP hardware changes (USB adapter hot-plugged / removed)
            _ = _viewModel.LoadAdaptersAsync();
        }
        return IntPtr.Zero;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.AccessPoints) || 
            e.PropertyName == nameof(MainWindowViewModel.FilteredAccessPoints) || 
            e.PropertyName == nameof(MainWindowViewModel.ShowSpectrumGraph))
        {
            Dispatcher.BeginInvoke(new Action(RedrawSpectrum));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedSessionSampleCount) ||
                 e.PropertyName == nameof(MainWindowViewModel.SelectedSessionSamples))
        {
            Dispatcher.BeginInvoke(new Action(RedrawHistoryGraph));
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawSpectrum();
    }

    private void OnHistoryCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawHistoryGraph();
    }

    private void RedrawHistoryGraph()
    {
        if (CanvasHistoryGraph == null) return;
        CanvasHistoryGraph.Children.Clear();

        if (PanelHistoryLegend != null)
        {
            PanelHistoryLegend.Children.Clear();
        }

        if (_viewModel.SelectedHistorySession == null || _viewModel.SelectedSessionSampleCount == 0) return;

        double W = CanvasHistoryGraph.ActualWidth;
        double H = CanvasHistoryGraph.ActualHeight;
        if (W <= 0 || H <= 0) return;

        double marginLeft = 40;
        double marginRight = 60; // Make margin larger to fit the direct labels on the right
        double marginTop = 20;
        double marginBottom = 30;

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x1F, 0x00));
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x33, 0x00));

        // Draw horizontal RSSI lines
        int[] rssiLevels = { -40, -50, -60, -70, -80, -90 };
        foreach (var r in rssiLevels)
        {
            double pct = (r + 100) / 70.0;
            double y = (H - marginBottom) - pct * (H - marginBottom - marginTop);
            var line = new Line { X1 = marginLeft, Y1 = y, X2 = W - marginRight, Y2 = y, Stroke = gridBrush, StrokeThickness = 0.5 };
            CanvasHistoryGraph.Children.Add(line);
            var text = new TextBlock { Text = $"{r}", Foreground = labelBrush, FontSize = 9, FontFamily = new FontFamily("Consolas") };
            Canvas.SetLeft(text, 10);
            Canvas.SetTop(text, y - 6);
            CanvasHistoryGraph.Children.Add(text);
        }

        // Draw samples grouped by BSSID
        var samplesByBssid = _viewModel.SelectedSessionSamples.GroupBy(s => s.BSSID).ToList();
        var startTime = _viewModel.SelectedHistorySession.StartTime;
        var endTime = _viewModel.SelectedSessionSamples.Max(s => s.Timestamp);
        var totalSeconds = (endTime - startTime).TotalSeconds;

        if (totalSeconds <= 0) totalSeconds = 1;

        // Draw vertical dotted time-division grid lines
        var gridDottedPen = new DoubleCollection { 4, 4 };
        var startLine = new Line { X1 = marginLeft, Y1 = marginTop, X2 = marginLeft, Y2 = H - marginBottom, Stroke = gridBrush, StrokeThickness = 0.5, StrokeDashArray = gridDottedPen };
        CanvasHistoryGraph.Children.Add(startLine);

        double midX = marginLeft + (W - marginLeft - marginRight) / 2.0;
        if (totalSeconds > 10)
        {
            var midLine = new Line { X1 = midX, Y1 = marginTop, X2 = midX, Y2 = H - marginBottom, Stroke = gridBrush, StrokeThickness = 0.5, StrokeDashArray = gridDottedPen };
            CanvasHistoryGraph.Children.Add(midLine);

            var endLine = new Line { X1 = W - marginRight, Y1 = marginTop, X2 = W - marginRight, Y2 = H - marginBottom, Stroke = gridBrush, StrokeThickness = 0.5, StrokeDashArray = gridDottedPen };
            CanvasHistoryGraph.Children.Add(endLine);
        }

        // Draw bottom axis line
        var axisLine = new Line 
        { 
            X1 = marginLeft, 
            Y1 = H - marginBottom, 
            X2 = W - marginRight, 
            Y2 = H - marginBottom, 
            Stroke = gridBrush, 
            StrokeThickness = 1 
        };
        CanvasHistoryGraph.Children.Add(axisLine);

        // Draw start, mid, and end time labels
        var startText = new TextBlock 
        { 
            Text = startTime.ToLocalTime().ToString("HH:mm:ss"), 
            Foreground = labelBrush, 
            FontSize = 9, 
            FontFamily = new FontFamily("Consolas") 
        };
        Canvas.SetLeft(startText, marginLeft);
        Canvas.SetTop(startText, H - marginBottom + 4);
        CanvasHistoryGraph.Children.Add(startText);

        if (totalSeconds > 10)
        {
            var midTime = startTime.AddSeconds(totalSeconds / 2);
            var midText = new TextBlock 
            { 
                Text = midTime.ToLocalTime().ToString("HH:mm:ss"), 
                Foreground = labelBrush, 
                FontSize = 9, 
                FontFamily = new FontFamily("Consolas") 
            };
            Canvas.SetLeft(midText, midX - 20);
            Canvas.SetTop(midText, H - marginBottom + 4);
            CanvasHistoryGraph.Children.Add(midText);

            var endText = new TextBlock 
            { 
                Text = endTime.ToLocalTime().ToString("HH:mm:ss"), 
                Foreground = labelBrush, 
                FontSize = 9, 
                FontFamily = new FontFamily("Consolas") 
            };
            Canvas.SetLeft(endText, W - marginRight - 45);
            Canvas.SetTop(endText, H - marginBottom + 4);
            CanvasHistoryGraph.Children.Add(endText);
        }

        // Draw X Axis label
        var axisTitleText = new TextBlock 
        { 
            Text = "CAPTURE TIMELINE (HH:MM:SS)", 
            Foreground = labelBrush, 
            FontSize = 9, 
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas") 
        };
        axisTitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(axisTitleText, marginLeft + (W - marginLeft - marginRight) / 2.0 - axisTitleText.DesiredSize.Width / 2.0);
        Canvas.SetTop(axisTitleText, H - marginBottom + 16);
        CanvasHistoryGraph.Children.Add(axisTitleText);

        foreach (var group in samplesByBssid)
        {
            var sortedSamples = group.OrderBy(x => x.Timestamp).ToList();
            double avgRssi = sortedSamples.Any() ? sortedSamples.Average(x => x.RSSI) : -100;
            
            Color color;
            if (avgRssi >= -55)
            {
                color = Color.FromRgb(0x00, 0xFF, 0x66); // Excellent: Emerald Green
            }
            else if (avgRssi >= -70)
            {
                color = Color.FromRgb(0xFF, 0xBB, 0x33); // Fair/Good: Vibrant Amber
            }
            else
            {
                color = Color.FromRgb(0xFF, 0x33, 0x00); // Weak/Poor: Crimson Red
            }

            var brush = new SolidColorBrush(color);
            bool isChecked = _viewModel.IsHistoryKeyChecked(group.Key);

            if (isChecked)
            {
                var polyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round
                };

                var polygon = new Polygon
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x15, color.R, color.G, color.B)),
                    Stroke = Brushes.Transparent
                };

                int step = 1;
                if (sortedSamples.Count > 1000)
                {
                    step = sortedSamples.Count / 1000;
                    if (step < 1) step = 1;
                }

                double lastX = marginLeft;
                double lastY = H - marginBottom;
                double firstX = marginLeft;
                bool hasPoints = false;

                for (int i = 0; i < sortedSamples.Count; i += step)
                {
                    var s = sortedSamples[i];
                    double sec = (s.Timestamp - startTime).TotalSeconds;
                    double x = marginLeft + (sec / totalSeconds) * (W - marginLeft - marginRight);
                    
                    int rssi = Math.Clamp(s.RSSI, -100, -30);
                    double pct = (rssi + 100) / 70.0;
                    double y = (H - marginBottom) - pct * (H - marginBottom - marginTop);
                    
                    if (!hasPoints)
                    {
                        firstX = x;
                        polygon.Points.Add(new Point(firstX, H - marginBottom));
                        hasPoints = true;
                    }

                    polyline.Points.Add(new Point(x, y));
                    polygon.Points.Add(new Point(x, y));
                    lastX = x;
                    lastY = y;
                }

                if (hasPoints)
                {
                    polygon.Points.Add(new Point(lastX, H - marginBottom));
                    CanvasHistoryGraph.Children.Add(polygon);
                }
                CanvasHistoryGraph.Children.Add(polyline);

                // Draw direct SSID label at the end of the line
                var firstSample = sortedSamples.FirstOrDefault();
                if (firstSample != null)
                {
                    string labelText = string.IsNullOrEmpty(firstSample.SSID) ? "<Hidden SSID>" : firstSample.SSID;

                    var labelBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                        BorderBrush = brush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 2, 4, 2),
                        Child = new TextBlock
                        {
                            Text = labelText,
                            Foreground = brush,
                            FontSize = 9,
                            FontWeight = FontWeights.Bold,
                            FontFamily = new FontFamily("Consolas")
                        }
                    };

                    labelBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double lblWidth = labelBorder.DesiredSize.Width;
                    double lblHeight = labelBorder.DesiredSize.Height;

                    double lblX = lastX + 5;
                    if (lblX + lblWidth > W) lblX = W - lblWidth - 5; // keep inside canvas
                    double lblY = lastY - lblHeight / 2.0;

                    Canvas.SetLeft(labelBorder, lblX);
                    Canvas.SetTop(labelBorder, lblY);
                    CanvasHistoryGraph.Children.Add(labelBorder);
                }
            }

            // Populate Legend Item (always shown, toggleable)
            if (PanelHistoryLegend != null)
            {
                var firstSampleForLegend = group.FirstOrDefault();
                string ssid = firstSampleForLegend?.SSID ?? "<Unknown SSID>";
                if (string.IsNullOrEmpty(ssid)) ssid = "<Hidden SSID>";
                string bssid = group.Key;

                var legendItem = new StackPanel 
                { 
                    Orientation = Orientation.Horizontal, 
                    Margin = new Thickness(0, 4, 0, 4),
                    ToolTip = $"BSSID: {bssid}\nSSID: {ssid}"
                };

                // CheckBox for filtering
                var cb = new CheckBox
                {
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Subscribe after setting IsChecked to prevent trigger loop during initialization
                cb.Checked += (s, e) =>
                {
                    _viewModel.SetHistoryKeyChecked(bssid, true);
                };
                cb.Unchecked += (s, e) =>
                {
                    _viewModel.SetHistoryKeyChecked(bssid, false);
                };

                var colorMarker = new Border 
                { 
                    Width = 12, 
                    Height = 12, 
                    Background = brush, 
                    CornerRadius = new CornerRadius(2), 
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var ssidText = new TextBlock 
                { 
                    Text = ssid, 
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00)), 
                    FontSize = 11, 
                    FontWeight = FontWeights.Bold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140
                };
                var bssidText = new TextBlock 
                { 
                    Text = bssid, 
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x33, 0x00)), 
                    FontSize = 9 
                };

                textPanel.Children.Add(ssidText);
                textPanel.Children.Add(bssidText);

                legendItem.Children.Add(cb);
                legendItem.Children.Add(colorMarker);
                legendItem.Children.Add(textPanel);

                PanelHistoryLegend.Children.Add(legendItem);
            }
        }
    }

    private void RedrawSpectrum()
    {
        // Clear children from canvases
        Canvas24Ghz.Children.Clear();
        Canvas5Ghz.Children.Clear();

        // If spectrum graph is not showing, do not waste resources drawing
        if (!_viewModel.ShowSpectrumGraph) return;

        double w24 = Canvas24Ghz.ActualWidth;
        double h24 = Canvas24Ghz.ActualHeight;
        double w5 = Canvas5Ghz.ActualWidth;
        double h5 = Canvas5Ghz.ActualHeight;

        if (w24 <= 0 || h24 <= 0 || w5 <= 0 || h5 <= 0) return;

        // Draw background grid lines and axis labels
        DrawBandGrid(Canvas24Ghz, is24Ghz: true);
        DrawBandGrid(Canvas5Ghz, is24Ghz: false);

        if (_viewModel.FilteredAccessPoints == null) return;

        var itemsToDraw = _viewModel.FilteredAccessPoints;

        // Draw curves for active access points
        foreach (var ap in itemsToDraw)
        {
            if (ap.Band.Contains("2.4"))
            {
                DrawApCurve(Canvas24Ghz, ap, is24Ghz: true);
            }
            else if (ap.Band.Contains("5"))
            {
                DrawApCurve(Canvas5Ghz, ap, is24Ghz: false);
            }
        }
    }

    private void DrawBandGrid(Canvas canvas, bool is24Ghz)
    {
        double W = canvas.ActualWidth;
        double H = canvas.ActualHeight;

        double marginLeft = 50;
        double marginRight = 20;
        double marginTop = 30;
        double marginBottom = 30;

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x1F, 0x00)); // very muted amber
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x33, 0x00)); // muted amber

        // 1. Draw horizontal RSSI reference lines
        int[] rssiLevels = { -40, -50, -60, -70, -80, -90 };
        foreach (var r in rssiLevels)
        {
            double pct = (r + 100) / 70.0;
            double y = (H - marginBottom) - pct * (H - marginBottom - marginTop);

            var line = new Line
            {
                X1 = marginLeft,
                Y1 = y,
                X2 = W - marginRight,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.5,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            canvas.Children.Add(line);

            var text = new TextBlock
            {
                Text = $"{r} dBm",
                Foreground = labelBrush,
                FontSize = 9,
                FontFamily = new FontFamily("Consolas")
            };
            Canvas.SetLeft(text, 5);
            Canvas.SetTop(text, y - 6);
            canvas.Children.Add(text);
        }

        // 2. Draw vertical channel/frequency ticks
        if (is24Ghz)
        {
            // Channels 1 to 14
            for (int ch = 1; ch <= 14; ch++)
            {
                double x = marginLeft + (ch - 1) * (W - marginLeft - marginRight) / 13.0;

                // Vertical gridline
                var line = new Line
                {
                    X1 = x,
                    Y1 = marginTop,
                    X2 = x,
                    Y2 = H - marginBottom,
                    Stroke = gridBrush,
                    StrokeThickness = 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 2 }
                };
                canvas.Children.Add(line);

                // Channel text
                var text = new TextBlock
                {
                    Text = $"CH {ch}",
                    Foreground = labelBrush,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas")
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(text, x - text.DesiredSize.Width / 2.0);
                Canvas.SetTop(text, H - marginBottom + 5);
                canvas.Children.Add(text);
            }
        }
        else
        {
            // Channels 36 to 165
            int[] channels = { 36, 48, 64, 100, 116, 132, 149, 165 };
            foreach (var ch in channels)
            {
                double freq = 5000 + 5 * ch;
                double x = marginLeft + (freq - 5150) * (W - marginLeft - marginRight) / 700.0;

                // Vertical gridline
                var line = new Line
                {
                    X1 = x,
                    Y1 = marginTop,
                    X2 = x,
                    Y2 = H - marginBottom,
                    Stroke = gridBrush,
                    StrokeThickness = 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 2 }
                };
                canvas.Children.Add(line);

                // Channel text
                var text = new TextBlock
                {
                    Text = $"CH {ch}\n({freq})",
                    Foreground = labelBrush,
                    FontSize = 8,
                    TextAlignment = TextAlignment.Center,
                    FontFamily = new FontFamily("Consolas")
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(text, x - text.DesiredSize.Width / 2.0);
                Canvas.SetTop(text, H - marginBottom + 2);
                canvas.Children.Add(text);
            }
        }
    }

    private void DrawApCurve(Canvas canvas, AccessPoint ap, bool is24Ghz)
    {
        double W = canvas.ActualWidth;
        double H = canvas.ActualHeight;

        double marginLeft = 50;
        double marginRight = 20;
        double marginTop = 30;
        double marginBottom = 30;

        double centerX = 0;
        double halfWidth = 0;

        if (is24Ghz)
        {
            // Channels 1 to 14
            int ch = Math.Clamp(ap.Channel, 1, 14);
            centerX = marginLeft + (ch - 1) * (W - marginLeft - marginRight) / 13.0;

            double widthMhz = ap.ChannelWidth ?? 20;
            // 20 MHz corresponds to 4 channel steps (spacing is 5 MHz)
            halfWidth = (widthMhz / 10.0) * (W - marginLeft - marginRight) / 13.0;
        }
        else
        {
            // Frequencies 5150 to 5850 MHz
            double freq = 5000 + 5 * ap.Channel;
            centerX = marginLeft + (freq - 5150) * (W - marginLeft - marginRight) / 700.0;

            double widthMhz = ap.ChannelWidth ?? 20;
            halfWidth = (widthMhz / 2.0) * (W - marginLeft - marginRight) / 700.0;
        }

        // Clamp RSSI to [-100, -30] for height mapping
        int rssi = Math.Clamp(ap.RSSI, -100, -30);
        double pct = (rssi + 100) / 70.0;
        double peakY = (H - marginBottom) - pct * (H - marginBottom - marginTop);
        double bottomY = H - marginBottom;

        // Construct smooth bell curve using a Bezier Path
        var path = new Path();
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(centerX - halfWidth, bottomY),
            IsClosed = false
        };

        var bezier = new BezierSegment
        {
            Point1 = new Point(centerX - halfWidth / 2.0, peakY),
            Point2 = new Point(centerX + halfWidth / 2.0, peakY),
            Point3 = new Point(centerX + halfWidth, bottomY)
        };
        figure.Segments.Add(bezier);
        geometry.Figures.Add(figure);
        path.Data = geometry;

        // Apply theme color brushes based on signal strength (RSSI) tiers to avoid confusion/blockage
        Color strokeColor;
        if (ap.RSSI >= -55)
        {
            strokeColor = Color.FromRgb(0x00, 0xFF, 0x66); // Excellent: Emerald Green
        }
        else if (ap.RSSI >= -70)
        {
            strokeColor = Color.FromRgb(0xFF, 0xBB, 0x33); // Fair/Good: Vibrant Amber
        }
        else
        {
            strokeColor = Color.FromRgb(0xFF, 0x33, 0x00); // Weak/Poor: Crimson Red
        }
        var fillColor = Color.FromArgb(0x18, strokeColor.R, strokeColor.G, strokeColor.B); // ~9% opacity

        path.Fill = new SolidColorBrush(fillColor);
        path.Stroke = new SolidColorBrush(strokeColor);
        path.StrokeThickness = 2.0;

        canvas.Children.Add(path);

        // Draw text label and indicator dot for strong signals
        if (ap.RSSI >= -85)
        {
            string labelText = string.IsNullOrEmpty(ap.SSID) ? "<Hidden SSID>" : ap.SSID;
            var label = new TextBlock
            {
                Text = $"{labelText} ({ap.RSSI}dBm)",
                Foreground = new SolidColorBrush(strokeColor),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas")
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, centerX - label.DesiredSize.Width / 2.0);
            
            double labelY = Math.Max(5, peakY - 18);
            Canvas.SetTop(label, labelY);
            canvas.Children.Add(label);

            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(strokeColor)
            };
            Canvas.SetLeft(dot, centerX - 2);
            Canvas.SetTop(dot, peakY - 2);
            canvas.Children.Add(dot);
        }
    }

    private void OnHistoryGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
        {
            _viewModel.SelectedHistorySessions = grid.SelectedItems
                .OfType<WirelessScanner.Domain.CaptureSession>()
                .ToList();
        }
    }
}

public class BindingProxy : System.Windows.Freezable
{
    protected override System.Windows.Freezable CreateInstanceCore() => new BindingProxy();

    public object Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly System.Windows.DependencyProperty DataProperty =
        System.Windows.DependencyProperty.Register("Data", typeof(object), typeof(BindingProxy), new System.Windows.PropertyMetadata(null));
}