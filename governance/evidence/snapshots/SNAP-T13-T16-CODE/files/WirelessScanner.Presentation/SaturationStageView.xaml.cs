using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WirelessScanner.Presentation;

/// <summary>Centre stage of the saturation test in Live Survey and in History (DR-0011). Charts via <see cref="SaturationCharts"/>.</summary>
public partial class SaturationStageView : UserControl
{
    private SaturationTestViewModel? _vm;

    public SaturationStageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.ChartDataChanged -= Redraw;
            _vm = DataContext as SaturationTestViewModel;
            if (_vm != null) _vm.ChartDataChanged += Redraw;
            Redraw();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new System.Action(Redraw)); };
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    public void Redraw()
    {
        if (_vm == null) return;
        if (_vm.IsRunningStep && _vm.LiveReport is { } live)
            SaturationCharts.Draw(LiveChart, live, live.Targets.Select(t => t.Bssid).Take(SaturationCharts.Palette.Length).ToList());
        if (_vm.IsResults && _vm.Report is { } report && _vm.SelectedResultTarget is { } sel)
            SaturationCharts.Draw(ResultChart, report, new List<string> { sel.Bssid });
    }
}
