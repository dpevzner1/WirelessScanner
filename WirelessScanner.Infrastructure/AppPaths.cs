using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WirelessScanner.Infrastructure;

/// <summary>
/// Single source of truth for where WirelessScanner keeps persistent state (DR-0006, finding F04).
/// Default: %ProgramData%\WirelessScanner (shared by all users, survives reinstall/upgrade, never inside
/// Program Files). Override with the WIRELESSSCANNER_DATA_DIR environment variable (portable/USB use, tests).
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvVar = "WIRELESSSCANNER_DATA_DIR";
    public const string DatabaseFileName = "wireless_scanner.db";

    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
            var dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WirelessScanner");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public static string LogsDirectory => Ensure(Path.Combine(DataDirectory, "logs"));
    public static string ApiDirectory => Ensure(Path.Combine(DataDirectory, "API"));
    public static string ConnectionString => $"Data Source={DatabasePath}";

    public static string ReportsDirectory => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WirelessScanner", "Reports"));

    private static string Ensure(string d)
    {
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// One-time migration of data written by releases before DR-0006 (database next to the exe or in the
    /// working directory; logs and API key in relative folders). Copies, never moves or overwrites: the
    /// legacy files stay in place as a backup. Returns a human-readable list of actions.
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacyData(IEnumerable<string>? legacyRoots = null)
    {
        var actions = new List<string>();
        var roots = (legacyRoots ?? new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !string.Equals(r.TrimEnd('\\'), Path.GetFullPath(DataDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!File.Exists(DatabasePath))
        {
            var legacyDb = roots.Select(r => Path.Combine(r, DatabaseFileName)).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (legacyDb != null)
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var src = legacyDb + suffix;
                    if (File.Exists(src)) File.Copy(src, DatabasePath + suffix, overwrite: false);
                }
                actions.Add($"Copied legacy database {legacyDb} -> {DatabasePath} (original kept as backup).");
            }
        }

        foreach (var root in roots)
        {
            CopyFolder(Path.Combine(root, "logs"), LogsDirectory, actions, "logs");
            CopyFolder(Path.Combine(root, "API"), ApiDirectory, actions, "API key");
        }
        return actions;
    }

    private static void CopyFolder(string from, string to, List<string> actions, string label)
    {
        if (!Directory.Exists(from)) return;
        int copied = 0;
        foreach (var f in Directory.GetFiles(from))
        {
            var dst = Path.Combine(to, Path.GetFileName(f));
            if (File.Exists(dst)) continue;
            File.Copy(f, dst);
            copied++;
        }
        if (copied > 0) actions.Add($"Copied {copied} legacy {label} file(s) from {from} -> {to}.");
    }
}
