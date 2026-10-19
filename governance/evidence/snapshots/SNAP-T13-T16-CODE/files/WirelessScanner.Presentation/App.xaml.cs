using System;
using System.Configuration;
using System.Data;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;
using WirelessScanner.Application;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Presentation;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    /// <summary>
    /// Demo mode (WIRELESSSCANNER_DEMO=1): simulated adapter and access points, no elevation required, data kept in a
    /// separate "demo" data directory. For training, screenshots and UI verification; never for real surveys.
    /// </summary>
    public static bool IsDemoMode => Environment.GetEnvironmentVariable("WIRELESSSCANNER_DEMO") == "1";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (IsDemoMode && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVar)))
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WirelessScanner", "demo"));
        }

        if (!IsDemoMode && !IsRunningAsAdministrator())
        {
            // NFR-04: request elevation through the standard UAC prompt by relaunching this executable elevated.
            bool relaunched = false;
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && !e.Args.Contains("--elevated"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--elevated") { UseShellExecute = true, Verb = "runas" });
                    relaunched = true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // the user declined the UAC prompt
            }

            if (!relaunched)
            {
                MessageBox.Show(
                    "Administrator privileges are required to access raw Wi-Fi telemetry via the Windows WLAN API.\n\n" +
                    "Start WirelessScanner again and choose 'Yes' in the Windows permission prompt, or right-click it and choose 'Run as administrator'.",
                    "Administrator Privilege Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            
            System.Windows.Application.Current.Shutdown();
            return;
        }

        // Move pre-DR-0006 data (database/logs/API key next to the exe or in the working directory) to %ProgramData%.
        try
        {
            foreach (var action in AppPaths.MigrateLegacyData()) AppLogger.Info(action);
            AppLogger.Info($"Data directory: {AppPaths.DataDirectory}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Legacy data migration failed: {ex.Message}", ex);
        }

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        ServiceProvider = serviceCollection.BuildServiceProvider();

        // Resolve and show MainWindow from DI
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Auto-start REST API if configured
        _ = AutoStartApiServerAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Stop a running saturation test (its partial results are analysed and saved), then drain pending writes.
            if (ServiceProvider?.GetService<ISaturationTestRunner>() is { IsRunning: true } runner)
            {
                runner.StopAsync().Wait(TimeSpan.FromSeconds(15));
            }
            (ServiceProvider?.GetService<IScanScheduler>() as IAsyncDisposable)?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            ServiceProvider?.GetService<OrderedPersistenceWriter>()?.FlushAsync().Wait(TimeSpan.FromSeconds(10));
            (ServiceProvider?.GetService<INetworkAdapterProvider>() as IDisposable)?.Dispose();
            NativePower.KeepAwake(false);
        }
        catch (Exception ex)
        {
            ServiceProvider?.GetService<IAppLogger>()?.Error($"Error during shutdown drain: {ex.Message}", ex);
        }

        try
        {
            var apiServer = ServiceProvider?.GetService<IRestApiServer>();
            if (apiServer != null && apiServer.IsRunning)
            {
                apiServer.StopAsync().Wait();
            }
        }
        catch (Exception ex)
        {
            var logger = ServiceProvider?.GetService<IAppLogger>();
            logger?.Error($"Error stopping REST API Server on exit: {ex.Message}", ex);
        }

        base.OnExit(e);
    }

    private async Task AutoStartApiServerAsync()
    {
        try
        {
            var settingsRepo = ServiceProvider.GetRequiredService<ISettingsRepository>();
            var apiServer = ServiceProvider.GetRequiredService<IRestApiServer>();
            var logger = ServiceProvider.GetRequiredService<IAppLogger>();

            string? enabledStr = await settingsRepo.GetSettingAsync("ApiEnabled");
            string? portStr = await settingsRepo.GetSettingAsync("ApiPort");
            string? apiKey = await settingsRepo.GetSettingAsync("ApiKey");

            if (enabledStr == "true" && !string.IsNullOrEmpty(apiKey))
            {
                if (!int.TryParse(portStr, out int port))
                {
                    port = 5005;
                }
                logger.Info("Auto-starting REST API Server...");
                await apiServer.StartAsync(port, apiKey);
            }
        }
        catch (Exception ex)
        {
            var logger = ServiceProvider.GetService<IAppLogger>();
            logger?.Error($"Error during REST API Server auto-start: {ex.Message}", ex);
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // DB Context Factory
        services.AddDbContextFactory<WlanDbContext>(options =>
            options.UseSqlite(AppPaths.ConnectionString));

        // Logger Service
        services.AddSingleton<IAppLogger, SerilogAppLogger>();

        // Settings and API Services
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IRestApiServer, RestApiServer>();

        // One ordered persistence queue for the whole app (DR-0003): parents always written before children
        services.AddSingleton<OrderedPersistenceWriter>();

        // Repositories and Managers
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<ISessionManager>(sp => new SessionManager(
            sp.GetRequiredService<ISessionRepository>(), sp.GetRequiredService<IAppLogger>(), sp.GetRequiredService<OrderedPersistenceWriter>()));
        services.AddSingleton<IStatisticsCalculator, StatisticsCalculator>();
        services.AddSingleton<IExportService, ExportService>();

        // Signal Saturation Test (SPEC-SAT-001)
        services.AddSingleton<ISaturationAnalyzer, SaturationAnalyzer>();
        services.AddSingleton<ISaturationRepository, SaturationRepository>();
        services.AddSingleton<ISaturationExportService, SaturationExportService>();
        // One scan scheduler per app (DR-0011, F18): live view, capture and saturation test share each scan
        services.AddSingleton<IScanScheduler>(sp => new ScanScheduler(sp.GetRequiredService<INetworkAdapterProvider>(), sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<ISaturationTestRunner>(sp => new SaturationTestRunner(
            sp.GetRequiredService<IScanScheduler>(), sp.GetRequiredService<ISaturationRepository>(),
            sp.GetRequiredService<ISaturationAnalyzer>(), sp.GetRequiredService<IAppLogger>(), sp.GetRequiredService<OrderedPersistenceWriter>()));

        // Core Adapter Services
        if (IsDemoMode)
            services.AddSingleton<INetworkAdapterProvider, MockNetworkAdapterProvider>();
        else
            services.AddSingleton<INetworkAdapterProvider, WlanAdapter>();

        // Views/ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddSingleton<IGeoLocationProvider, WindowsGeoLocationProvider>();
        services.AddSingleton<SaturationTestViewModel>(sp => new SaturationTestViewModel(
            sp.GetRequiredService<INetworkAdapterProvider>(), sp.GetRequiredService<ISaturationTestRunner>(), sp.GetRequiredService<ISaturationRepository>(),
            sp.GetRequiredService<ISaturationAnalyzer>(), sp.GetRequiredService<ISaturationExportService>(), sp.GetRequiredService<IGeoLocationProvider>()));
        services.AddTransient<MainWindow>();
    }
}

