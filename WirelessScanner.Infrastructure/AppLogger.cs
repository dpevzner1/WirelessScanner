using System;
using System.IO;
using Serilog;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public static class AppLogger
{
    public static event Action<string>? LogAdded;

    static AppLogger()
    {
        // Ensure logs directory exists
        Directory.CreateDirectory("logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/wireless_scanner_log.txt", 
                rollingInterval: RollingInterval.Day, 
                retainedFileCountLimit: 28) // 28 days = 4 weeks retention
            .CreateLogger();
    }

    public static void Info(string message)
    {
        Log.Information(message);
        LogAdded?.Invoke($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
    }

    public static void Warn(string message)
    {
        Log.Warning(message);
        LogAdded?.Invoke($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
    }

    public static void Error(string message, Exception? ex = null)
    {
        if (ex != null)
        {
            Log.Error(ex, message);
            LogAdded?.Invoke($"[ERROR] {DateTime.Now:HH:mm:ss} - {message} : {ex.Message}");
        }
        else
        {
            Log.Error(message);
            LogAdded?.Invoke($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
        }
    }

    public static void PurgeLogs()
    {
        try
        {
            Log.CloseAndFlush(); // Release file locks
            
            if (Directory.Exists("logs"))
            {
                var files = Directory.GetFiles("logs");
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }

            // Re-initialize
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/wireless_scanner_log.txt", 
                    rollingInterval: RollingInterval.Day, 
                    retainedFileCountLimit: 28)
                .CreateLogger();

            Info("Logs purged and logger re-initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error purging logs: {ex.Message}");
        }
    }
}

public class SerilogAppLogger : IAppLogger
{
    public void Info(string message) => AppLogger.Info(message);
    public void Warn(string message) => AppLogger.Warn(message);
    public void Error(string message, Exception? ex = null) => AppLogger.Error(message, ex);
}
