using System;
using System.IO;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>Isolates every test run from the real %ProgramData%\WirelessScanner data directory (DR-0006).</summary>
[SetUpFixture]
public class TestEnvironment
{
    public static string DataDir { get; private set; } = "";

    [OneTimeSetUp]
    public void Setup()
    {
        DataDir = Path.Combine(Path.GetTempPath(), $"ws_test_data_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, DataDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, null);
        try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (IOException) { }
    }
}
