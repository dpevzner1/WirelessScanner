using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

/// <summary>
/// REST API settings screen (live findings 2026-10-17 on 1.3.0): the status label stayed "Stopped" while the API was running,
/// EXPORT wrote silently to a machine-wide folder, and the key was shown in clear text (it ended up in a screenshot).
/// </summary>
[TestFixture]
public class ApiSettingsScreenTests
{
    private sealed class EventfulApiServer : IRestApiServer
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public string ApiKey { get; private set; } = "";
        public string? LastError { get; set; }
        public event Action? StateChanged;
        public Task StartAsync(int port, string apiKey) { Port = port; ApiKey = apiKey; IsRunning = true; StateChanged?.Invoke(); return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; StateChanged?.Invoke(); return Task.CompletedTask; }
        public void UpdateLiveAccessPoints(IEnumerable<AccessPoint> accessPoints) { }
        public void FailToStart(string reason) { LastError = reason; IsRunning = false; StateChanged?.Invoke(); }
    }

    private static (MainWindowViewModel Vm, EventfulApiServer Api) Build()
    {
        var repo = new MockSessionRepository();
        var api = new EventfulApiServer();
        var vm = new MainWindowViewModel(new CountingProvider(), new SessionManager(repo, new NullLogger()), repo, new MockExportService(), api, new MockSettingsRepository());
        return (vm, api);
    }

    [Test]
    public async Task StatusLabel_FollowsTheServer_IncludingBackgroundStartAndFailure()
    {
        var (vm, api) = Build();
        vm.ApiEnabled = false;
        Assert.That(vm.ApiServerStatusText, Does.Contain("disabled"));
        vm.ApiEnabled = true;
        Assert.That(vm.ApiServerStatusText, Does.Contain("enabled but not running"));
        await api.StartAsync(5005, "ws_live_x");            // e.g. the app's auto-start at launch, outside the screen
        Assert.That(vm.ApiServerStatusText, Does.Contain("RUNNING on http://localhost:5005/"), "1.3.0 kept showing 'Stopped' here");
        api.FailToStart("Port 5005 is already in use");
        Assert.That(vm.ApiServerStatusText, Does.Contain("NOT RUNNING").And.Contain("already in use"));
    }

    [Test]
    public void ApiKey_IsMaskedByDefault_AndCanBeShownOrCopied()
    {
        var (vm, _) = Build();
        vm.ApiKey = "ws_live_0123456789abcdef0123456789abcdef";
        string? clip = null;
        vm.CopyToClipboard = t => clip = t;
        Assert.Multiple(() =>
        {
            Assert.That(vm.DisplayedApiKey, Does.StartWith("ws_live_").And.EndWith("cdef").And.Contain("••••"));
            Assert.That(vm.DisplayedApiKey, Does.Not.Contain("0123456789abcdef0123"), "middle of the key hidden");
            Assert.That(vm.ShowApiKeyLabel, Is.EqualTo("SHOW"));
        });
        vm.ToggleShowApiKeyCommand.Execute(null);
        Assert.That(vm.DisplayedApiKey, Is.EqualTo(vm.ApiKey));
        vm.CopyApiKeyCommand.Execute(null);
        Assert.Multiple(() =>
        {
            Assert.That(clip, Is.EqualTo(vm.ApiKey));
            Assert.That(vm.ApiExportStatus, Does.Contain("copied"));
        });
    }

    [Test]
    public void Export_AsksWhereToSave_DefaultsToTheUsersDocuments_AndReportsThePathOnScreen()
    {
        var (vm, _) = Build();
        vm.ApiKey = "ws_live_0123456789abcdef0123456789abcdef";
        string? suggested = null;
        var target = Path.Combine(Path.GetTempPath(), $"ws_apikey_{Guid.NewGuid():N}.md");
        vm.ApiKeySaveDialog = s => { suggested = s; return target; };
        try
        {
            vm.ExportApiKeyCommand.Execute(null);
            Assert.Multiple(() =>
            {
                Assert.That(suggested, Does.StartWith(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "per-user folder, not ProgramData");
                Assert.That(File.Exists(target), Is.True);
                Assert.That(File.ReadAllText(target), Does.Contain("X-API-Key").And.Contain(vm.ApiKey));
                Assert.That(vm.ApiExportStatus, Does.Contain(target), "the result is shown on the settings page");
                Assert.That(vm.HasApiExportStatus, Is.True);
            });
            vm.ApiKeySaveDialog = _ => null;
            vm.ExportApiKeyCommand.Execute(null);
            Assert.That(vm.ApiExportStatus, Is.EqualTo("Export cancelled."));
        }
        finally { if (File.Exists(target)) File.Delete(target); }
    }

    [Test]
    public async Task Apply_StartsTheServer_AndNoLongerWritesTheKeyIntoTheSharedDataFolder()
    {
        var (vm, api) = Build();
        int dialogs = 0;
        vm.ApiKeySaveDialog = _ => { dialogs++; return null; };
        vm.ApiKey = "ws_live_0123456789abcdef0123456789abcdef";
        vm.ApiPort = "5099";
        vm.ApiEnabled = true;
        vm.ApplyApiSettingsCommand.Execute(null);
        for (int i = 0; i < 50 && !api.IsRunning; i++) await Task.Delay(20);
        Assert.Multiple(() =>
        {
            Assert.That(api.IsRunning && api.Port == 5099, Is.True);
            Assert.That(dialogs, Is.EqualTo(0), "apply does not export the key");
            Assert.That(vm.ApiServerStatusText, Does.Contain("RUNNING on http://localhost:5099/"));
            Assert.That(vm.ApiExportStatus, Does.Contain("server running on port 5099"));
        });
    }
}
