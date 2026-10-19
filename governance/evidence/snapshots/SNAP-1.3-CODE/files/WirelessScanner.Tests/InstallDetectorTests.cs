using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Setup.Core;

namespace WirelessScanner.Tests;

/// <summary>
/// Setup finds an existing installation from the registry for update / repair / downgrade (owner request 2026-10-16).
/// Pure logic: registry views, files and the install manifest are faked.
/// </summary>
[TestFixture]
public class InstallDetectorTests
{
    private const string Dir = @"C:\Program Files\WirelessScanner";
    private static readonly string Exe = Dir + @"\WirelessScanner.Presentation.exe";

    private static DetectedInstall Detect(IEnumerable<RegistrySnapshot> views, bool exePresent = true, string? manifestVersion = null) =>
        InstallDetector.Detect(views, p => exePresent && string.Equals(p, Exe, StringComparison.OrdinalIgnoreCase),
            d => manifestVersion == null ? null : new InstallManifest { Version = manifestVersion });

    private static RegistrySnapshot Both(string version, string view = "64-bit") => new()
    {
        View = view, AppInstallDir = Dir, AppVersion = version,
        UninstallInstallLocation = Dir, UninstallDisplayVersion = version,
        UninstallString = $"\"{Dir}\\Setup\\WirelessScanner.Setup.exe\" /uninstall", DisplayIcon = $"{Exe},0"
    };

    [Test]
    public void NothingRegistered_IsFreshInstall()
    {
        var d = Detect(new[] { new RegistrySnapshot(), new RegistrySnapshot { View = "32-bit" } });
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed, Is.False);
            Assert.That(VersionPolicy.Classify(d.Version, "1.2.1", d.Installed), Is.EqualTo(InstallKind.Fresh));
        });
    }

    [Test]
    public void BothEntries_UpdateAndRepairAreOffered()
    {
        var d = Detect(new[] { Both("1.2.0"), new RegistrySnapshot { View = "32-bit" } }, manifestVersion: "1.2.0");
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed && d.InstallDir == Dir && d.Version == "1.2.0", Is.True);
            Assert.That(d.Source, Is.EqualTo("64-bit app key + 64-bit Apps & features entry"));
            Assert.That(d.Notes, Is.Empty);
            Assert.That(VersionPolicy.Classify(d.Version, "1.2.1", true), Is.EqualTo(InstallKind.Upgrade));
            Assert.That(VersionPolicy.Classify(d.Version, "1.2.0", true), Is.EqualTo(InstallKind.Reinstall), "same version = Repair");
        });
    }

    [Test]
    public void OnlyAppsAndFeaturesEntry_IsStillDetected()
    {
        // e.g. the app key was removed, or an installer that only wrote the Uninstall entry
        var d = Detect(new[] { new RegistrySnapshot { UninstallInstallLocation = Dir, UninstallDisplayVersion = "1.1.0" } });
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed, Is.True);
            Assert.That(d.InstallDir, Is.EqualTo(Dir));
            Assert.That(d.Version, Is.EqualTo("1.1.0"));
            Assert.That(d.Source, Is.EqualTo("64-bit Apps & features entry"));
        });
    }

    [Test]
    public void UninstallStringWithoutInstallLocation_ResolvesFolderAboveMaintenanceCopy()
    {
        var d = Detect(new[] { new RegistrySnapshot { UninstallString = $"\"{Dir}\\Setup\\WirelessScanner.Setup.exe\" /uninstall", UninstallDisplayVersion = "1.1.0" } });
        Assert.That(d.InstallDir, Is.EqualTo(Dir));
    }

    [Test]
    public void DisplayIconAlone_ResolvesFolder()
    {
        var d = Detect(new[] { new RegistrySnapshot { DisplayIcon = $"{Exe},0", UninstallDisplayVersion = "1.0.0" } });
        Assert.Multiple(() =>
        {
            Assert.That(d.InstallDir, Is.EqualTo(Dir));
            Assert.That(d.Version, Is.EqualTo("1.0.0"));
        });
    }

    [Test]
    public void Only32BitView_IsDetected()
    {
        var d = Detect(new[] { new RegistrySnapshot(), Both("1.1.0", "32-bit") });
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed, Is.True);
            Assert.That(d.Source, Does.Contain("32-bit"));
        });
    }

    [Test]
    public void FilesAreTrustedOverRegistry_AndDisagreementIsReported()
    {
        var reg = Both("1.1.0");
        reg.UninstallDisplayVersion = "1.0.0";
        var d = Detect(new[] { reg }, manifestVersion: "1.2.0");
        Assert.Multiple(() =>
        {
            Assert.That(d.Version, Is.EqualTo("1.2.0"));
            Assert.That(d.Notes, Has.Some.Contains("Apps & features shows version 1.0.0"));
            Assert.That(d.Notes, Has.Some.Contains("Installed files are version 1.2.0"));
        });
    }

    [Test]
    public void MissingExecutable_NeedsRepair()
    {
        var d = Detect(new[] { Both("1.2.0") }, exePresent: false, manifestVersion: "1.2.0");
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed, Is.True);
            Assert.That(d.NeedsRepair, Is.True);
            Assert.That(d.Notes, Has.Some.Contains("needs repair"));
        });
    }

    [Test]
    public void NoVersionAnywhere_TreatedAsOlder()
    {
        var d = Detect(new[] { new RegistrySnapshot { AppInstallDir = Dir } });
        Assert.Multiple(() =>
        {
            Assert.That(d.Version, Is.Null);
            Assert.That(VersionPolicy.Classify(d.Version, "1.2.1", true), Is.EqualTo(InstallKind.Upgrade));
            Assert.That(d.Notes, Has.Some.Contains("No version is recorded"));
        });
    }

    [Test]
    public void ConflictingFolders_AreReported()
    {
        var a = Both("1.2.0");
        a.UninstallInstallLocation = @"D:\Apps\WirelessScanner";
        var d = Detect(new[] { a });
        Assert.Multiple(() =>
        {
            Assert.That(d.InstallDir, Is.EqualTo(Dir), "app key wins");
            Assert.That(d.Notes, Has.Some.Contains(@"D:\Apps\WirelessScanner"));
        });
    }

    [Test]
    public void LiveMachineRegistry_ReadOnly_DetectsThisInstall()
    {
        // Read-only probe of the real registry (never writes): proves the 64-bit view holds what the wizard needs.
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var app = hklm.OpenSubKey(@"SOFTWARE\WirelessScanner");
        using var u = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WirelessScanner");
        if (app == null && u == null) Assert.Ignore("WirelessScanner is not installed on this machine.");
        var snap = new RegistrySnapshot
        {
            AppInstallDir = app?.GetValue("InstallDir") as string, AppVersion = app?.GetValue("Version") as string,
            UninstallInstallLocation = u?.GetValue("InstallLocation") as string, UninstallDisplayVersion = u?.GetValue("DisplayVersion") as string,
            UninstallString = u?.GetValue("UninstallString") as string, DisplayIcon = u?.GetValue("DisplayIcon") as string
        };
        var d = InstallDetector.Detect(new[] { snap }, System.IO.File.Exists, InstallManifest.TryLoad);
        TestContext.Out.WriteLine($"LIVE_DETECT installed={d.Installed} version={d.Version} source='{d.Source}' exe={d.ExecutablePresent} notes={d.Notes.Count}");
        Assert.Multiple(() =>
        {
            Assert.That(d.Installed, Is.True);
            Assert.That(d.ExecutablePresent, Is.True);
            Assert.That(d.Version, Is.Not.Null);
        });
    }
}
