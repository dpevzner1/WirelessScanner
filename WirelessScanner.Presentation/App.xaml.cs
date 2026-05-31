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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsRunningAsAdministrator())
        {
            MessageBox.Show(
                "Administrator privileges are required to access raw Wi-Fi telemetry via the Windows WLAN API.\n\n" +
                "Please restart the application by right-clicking the executable and choosing 'Run as Administrator'.",
                "Administrator Privilege Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            
            System.Windows.Application.Current.Shutdown();
            return;
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
            options.UseSqlite("Data Source=wireless_scanner.db"));

        // Logger Service
        services.AddSingleton<IAppLogger, SerilogAppLogger>();

        // Settings and API Services
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IRestApiServer, RestApiServer>();

        // Repositories and Managers
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IStatisticsCalculator, StatisticsCalculator>();
        services.AddSingleton<IExportService, ExportService>();

        // Core Adapter Services
        services.AddSingleton<INetworkAdapterProvider, WlanAdapter>();

        // Views/ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
    }
}

