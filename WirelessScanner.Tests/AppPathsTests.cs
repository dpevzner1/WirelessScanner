using System;
using System.IO;
using System.Linq;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

[TestFixture]
public class AppPathsTests
{
    [Test]
    public void Paths_AreUnderDataDirectory_IndependentOfWorkingDirectory()
    {
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath();
            Assert.Multiple(() =>
            {
                Assert.That(AppPaths.DatabasePath, Does.StartWith(TestEnvironment.DataDir));
                Assert.That(AppPaths.LogsDirectory, Does.StartWith(TestEnvironment.DataDir));
                Assert.That(AppPaths.ApiDirectory, Does.StartWith(TestEnvironment.DataDir));
                Assert.That(AppPaths.ConnectionString, Does.Contain(TestEnvironment.DataDir));
            });
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }

    [Test]
    public void Migration_CopiesLegacyDbOnce_AndNeverOverwrites()
    {
        var legacy = Path.Combine(Path.GetTempPath(), $"ws_legacy_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, AppPaths.DatabaseFileName), "legacy-db");
        File.WriteAllText(Path.Combine(legacy, "logs", "old.txt"), "log");
        try
        {
            if (File.Exists(AppPaths.DatabasePath)) File.Delete(AppPaths.DatabasePath);
            var first = AppPaths.MigrateLegacyData(new[] { legacy });
            File.WriteAllText(Path.Combine(legacy, AppPaths.DatabaseFileName), "newer-legacy-db");
            var second = AppPaths.MigrateLegacyData(new[] { legacy });
            Assert.Multiple(() =>
            {
                Assert.That(first.Any(a => a.Contains("Copied legacy database")), Is.True);
                Assert.That(File.ReadAllText(AppPaths.DatabasePath), Is.EqualTo("legacy-db"), "existing data never overwritten");
                Assert.That(second.Any(a => a.Contains("database")), Is.False);
                Assert.That(File.Exists(Path.Combine(legacy, AppPaths.DatabaseFileName)), Is.True, "original kept as backup");
                Assert.That(File.Exists(Path.Combine(AppPaths.LogsDirectory, "old.txt")), Is.True);
            });
        }
        finally
        {
            File.Delete(AppPaths.DatabasePath);
            Directory.Delete(legacy, true);
        }
    }
}
