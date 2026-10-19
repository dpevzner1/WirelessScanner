using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>SAT-AC-05/07/09: setup validation, flow, confirmation and export hooks of the saturation screen.</summary>
[TestFixture]
public class SaturationViewModelTests
{
    private static (SaturationTestViewModel Vm, SaturationTestRunner Runner, MemorySaturationRepository Repo) Build()
    {
        var provider = new WirelessScanner.Infrastructure.MockNetworkAdapterProvider();
        var repo = new MemorySaturationRepository();
        var runner = new SaturationTestRunner(provider, repo, new SaturationAnalyzer(), new NullLogger());
        var vm = new SaturationTestViewModel(provider, runner, repo, new SaturationAnalyzer(), new SaturationExportService())
        {
            Ui = a => a(),
            Confirm = (_, _) => true,
            KeepAwake = _ => { },
            IsOnBattery = () => false
        };
        return (vm, runner, repo);
    }

    [Test]
    public async Task Validation_BlocksStartUntilAdapterTargetsAndLocationAreValid()
    {
        var (vm, _, _) = Build();
        vm.Revalidate();
        Assert.That(vm.CanStart, Is.False);
        Assert.That(vm.ValidationMessage, Does.Contain("adapter"));

        await vm.LoadAdaptersAsync();
        await vm.DiscoverAsync();
        Assert.That(vm.Candidates, Has.Count.EqualTo(11)); // demo set: 5 legacy APs + 6 physical-unit demo BSSIDs (1.4, DR-0013)
        Assert.That(vm.ValidationMessage, Does.Contain("Select one network"));

        vm.Candidates[0].IsSelected = true;
        Assert.That(vm.ValidationMessage, Does.Contain("location"));

        vm.Location = "Stock room";
        Assert.Multiple(() =>
        {
            Assert.That(vm.CanStart, Is.True);
            Assert.That(vm.ValidationMessage, Is.Empty);
            Assert.That(vm.SummaryLine, Does.Contain("1 BSSID").And.Contain("every 5 s").And.Contain("≈180 cycles"));
        });

        vm.Candidates[1].IsSelected = true; // two rows under Single AP
        Assert.That(vm.CanStart, Is.False);
        Assert.That(vm.ValidationMessage, Does.Contain("exactly one"));
    }

    [Test]
    public async Task Validation_ScopeRulesCustomDurationAndThresholds()
    {
        var (vm, _, _) = Build();
        await vm.LoadAdaptersAsync();
        await vm.DiscoverAsync();
        vm.Location = "Lab";
        foreach (var c in vm.Candidates.Where(c => c.Ssid == "PlantOps")) c.IsSelected = true;

        vm.SelectedScope = vm.ScopeOptions.Single(s => s.Scope == SaturationScope.SingleSsid);
        Assert.That(vm.CanStart, Is.True, vm.ValidationMessage);

        vm.SelectedScope = vm.ScopeOptions.Single(s => s.Scope == SaturationScope.MultipleSsids);
        Assert.That(vm.ValidationMessage, Does.Contain("at least two SSIDs"));

        vm.SelectedScope = vm.ScopeOptions.Single(s => s.Scope == SaturationScope.AllVisible);
        Assert.That(vm.CanStart, Is.True);

        vm.SelectedDuration = vm.DurationOptions.Single(d => d.IsCustom);
        vm.CustomMinutes = "0";
        Assert.That(vm.ValidationMessage, Does.Contain("1–1440"));
        vm.CustomMinutes = "90";
        Assert.That(vm.CanStart, Is.True);

        vm.DropDb = "99";
        Assert.That(vm.ValidationMessage, Does.Contain("3–30 dB"));
        vm.DropDb = "10";

        var cfg = vm.BuildConfig();
        Assert.Multiple(() =>
        {
            Assert.That(cfg.Duration, Is.EqualTo(TimeSpan.FromMinutes(90)));
            Assert.That(cfg.Thresholds.DropDb, Is.EqualTo(10));
            Assert.That(cfg.Scope, Is.EqualTo(SaturationScope.AllVisible));
        });
    }

    [Test]
    public async Task Flow_SetupRunningResults_WithMarkersAndExports()
    {
        var (vm, runner, repo) = Build();
        await vm.LoadAdaptersAsync();
        await vm.DiscoverAsync();
        vm.Location = "Bench";
        vm.Candidates.First(c => c.Ssid == "PlantOps").IsSelected = true;
        vm.SelectedDuration = vm.DurationOptions.Single(d => d.Label == "Until stopped");
        vm.IntervalSeconds = 5;
        Assert.That(vm.CanStart, Is.True, vm.ValidationMessage);

        // Use a very short interval for the test by starting the runner through the VM, then let it run briefly.
        await vm.StartAsync();
        Assert.That(vm.CurrentStep, Is.EqualTo(SaturationTestViewModel.Step.Running));
        await Task.Delay(300);
        vm.MarkerText = "door closed";
        vm.AddMarkerCommand.Execute(null);

        await vm.StopAsync();
        for (int i = 0; i < 50 && vm.CurrentStep != SaturationTestViewModel.Step.Results; i++) await Task.Delay(20);

        Assert.Multiple(() =>
        {
            Assert.That(vm.CurrentStep, Is.EqualTo(SaturationTestViewModel.Step.Results));
            Assert.That(vm.Report, Is.Not.Null);
            Assert.That(vm.Report!.Status, Is.EqualTo(SaturationTestStatus.Stopped));
            Assert.That(vm.ResultTargets, Is.Not.Empty);
            Assert.That(vm.Report.Markers.Any(m => m.Text == "door closed"), Is.True);
            Assert.That(vm.Tiles, Is.Not.Empty, "live tiles were populated");
        });

        var dir = Path.Combine(Path.GetTempPath(), $"ws_vm_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            vm.SaveFileDialog = (_, name, _) => Path.Combine(dir, name);
            await vm.ExportAsync("pdf");
            await vm.ExportAsync("csv");
            await vm.ExportAsync("json");
            Assert.Multiple(() =>
            {
                Assert.That(Directory.GetFiles(dir, "*.pdf"), Has.Length.EqualTo(1));
                Assert.That(Directory.GetFiles(dir, "*.csv"), Has.Length.EqualTo(6));
                Assert.That(Directory.GetFiles(dir, "*.json"), Has.Length.EqualTo(1));
                Assert.That(vm.ExportStatus, Does.StartWith("Saved JSON"));
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        await runner.DisposeAsync();
    }

    [Test]
    public async Task Stop_RequiresConfirmation()
    {
        var (vm, runner, _) = Build();
        await vm.LoadAdaptersAsync();
        await vm.DiscoverAsync();
        vm.Location = "Bench";
        vm.Candidates[0].IsSelected = true;
        await vm.StartAsync();
        vm.Confirm = (_, _) => false;
        await vm.StopAsync();
        Assert.That(runner.IsRunning, Is.True, "declined confirmation keeps the test running");
        vm.Confirm = (_, _) => true;
        await vm.StopAsync();
        Assert.That(runner.IsRunning, Is.False);
        await runner.DisposeAsync();
    }

    [Test]
    public void Tile_StatusAlwaysHasTextLabel()
    {
        var tile = new SaturationTile("AA");
        var summary = new SaturationTargetSummary { Bssid = "AA", Ssid = "Net", Grade = "B" };
        tile.Update(summary, new SaturationObservation(1, "AA", "Net", ObservationStatus.Missing, null, null, null, null, null, null, null, null));
        Assert.That(tile.CurrentLabel, Is.EqualTo("NOT SEEN"));
        tile.Update(summary, new SaturationObservation(2, "AA", "Net", ObservationStatus.Observed, -82, 40, 36, "5 GHz", 20, 100, 0, null));
        Assert.Multiple(() =>
        {
            Assert.That(tile.CurrentLabel, Is.EqualTo("UNUSABLE"));
            Assert.That(tile.Current, Is.EqualTo("-82 dBm"));
        });
    }
}
