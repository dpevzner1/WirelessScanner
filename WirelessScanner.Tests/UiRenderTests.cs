using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>
/// Loads the real XAML of the saturation screen on an STA thread (catches XAML runtime errors that the compiler
/// cannot), and renders each step to PNG when WS_UI_RENDER_OUT is set (visual review without driving the desktop).
/// </summary>
[TestFixture]
public class UiRenderTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Render(FrameworkElement element, string? outDir, string name)
    {
        element.Measure(new Size(1280, 760));
        element.Arrange(new Rect(0, 0, 1280, 760));
        element.UpdateLayout();
        Pump();
        if (outDir == null) return;
        var bmp = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32);
        var bg = new System.Windows.Shapes.Rectangle { Width = 1280, Height = 760, Fill = Brushes.Black };
        bg.Measure(new Size(1280, 760)); bg.Arrange(new Rect(0, 0, 1280, 760));
        bmp.Render(bg);
        bmp.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(outDir, name));
        enc.Save(fs);
    }

    [Test]
    public void SaturationView_AllThreeSteps_LoadAndRender()
    {
        string? outDir = Environment.GetEnvironmentVariable("WS_UI_RENDER_OUT");
        if (outDir != null) Directory.CreateDirectory(outDir);

        RunSta(() =>
        {
            var provider = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider();
            var repo = new MemorySaturationRepository();
            var runner = new SaturationTestRunner(provider, repo, new SaturationAnalyzer(), new NullLogger());
            var vm = new SaturationTestViewModel(provider, runner, repo, new SaturationAnalyzer(), new SaturationExportService())
            {
                Ui = a => a(), Confirm = (_, _) => true, KeepAwake = _ => { }, IsOnBattery = () => false
            };
            var view = new SaturationTestView { DataContext = vm, Background = Brushes.Black };

            // Step 1: setup with discovered candidates and a validation message
            vm.LoadAdaptersAsync().GetAwaiter().GetResult();
            vm.DiscoverAsync().GetAwaiter().GetResult();
            vm.Candidates.First(c => c.Ssid == "PlantOps").IsSelected = true;
            vm.SelectedScope = vm.ScopeOptions.Single(s => s.Scope == SaturationScope.SingleSsid);
            vm.Latitude = "41.878113";
            vm.Longitude = "-87.629799";
            vm.Building = "Plant 2";
            Render(view, outDir, "ui_1_setup_invalid.png");
            vm.Location = "Stock room B";
            Render(view, outDir, "ui_1_setup_ready.png");
            Assert.That(vm.CanStart, Is.True, vm.ValidationMessage);

            // Step 2: running with live progress built from a realistic report
            var report = ReportPreviewTests.Realistic();
            vm.CurrentStep = SaturationTestViewModel.Step.Running;
            vm.RunStatus = "RUNNING — Stock room soak @ Stock room B";
            var progress = new SaturationProgress(report.Config, SaturationTestStatus.Running, report.StartUtc, report.ActiveSeconds, 600,
                report.Cycles[^1], report.Cycles, report.Markers);
            vm.ApplyProgress(progress, report);
            Render(view, outDir, "ui_2_running.png");

            // Step 3: results
            vm.ShowResults(report);
            Render(view, outDir, "ui_3_results.png");
            Assert.That(vm.ResultTargets, Has.Count.EqualTo(2));
        });
    }
}
