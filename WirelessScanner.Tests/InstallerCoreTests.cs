using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using WirelessScanner.Setup.Core;

namespace WirelessScanner.Tests;

/// <summary>Installer lifecycle logic (T09, DR-0007). Exercised in temp folders; never touches the real system.</summary>
[TestFixture]
public class InstallerCoreTests
{
    private string _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ws_installer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var zip = Path.Combine(_root, name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var w = new StreamWriter(archive.CreateEntry(path).Open());
            w.Write(content);
        }
        return zip;
    }

    [TestCase(null, "1.1.0", false, InstallKind.Fresh)]
    [TestCase("1.0.0", "1.1.0", true, InstallKind.Upgrade)]
    [TestCase("1.1.0", "1.1.0", true, InstallKind.Reinstall)]
    [TestCase("1.2.0", "1.1.0", true, InstallKind.Downgrade)]
    [TestCase("garbage", "1.1.0", true, InstallKind.Upgrade)]
    [TestCase("v1.10.0", "1.9.0", true, InstallKind.Downgrade)]
    public void VersionPolicy_ClassifiesInstallKind(string? installed, string payload, bool isInstalled, InstallKind expected)
    {
        Assert.That(VersionPolicy.Classify(installed, payload, isInstalled), Is.EqualTo(expected));
    }

    [Test]
    public void Assemble_JoinsPartsInOrder_AndVerifiesIntegrity()
    {
        var zip = Zip("app.zip", ("WirelessScanner.Presentation.exe", "exe"), ("lib/a.dll", new string('x', 5000)));
        var bytes = File.ReadAllBytes(zip);
        var src = Path.Combine(_root, "release");
        Directory.CreateDirectory(src);
        int half = bytes.Length / 2; // split at the midpoint (compressed size varies)
        File.WriteAllBytes(Path.Combine(src, "WirelessScanner01.bin"), bytes.Take(half).ToArray());
        File.WriteAllBytes(Path.Combine(src, "WirelessScanner02.bin"), bytes.Skip(half).ToArray());
        var info = new PayloadInfo { Version = "1.1.0", Bytes = bytes.Length, Sha256 = PayloadAssembler.Sha256Of(zip), Parts = { "WirelessScanner01.bin", "WirelessScanner02.bin" } };
        info.Save(src);

        var loaded = PayloadInfo.TryLoad(src)!;
        var outZip = Path.Combine(_root, "out.zip");
        Assert.That(PayloadAssembler.Assemble(src, loaded, outZip), Is.EqualTo(info.Sha256));
        Assert.That(File.ReadAllBytes(outZip), Is.EqualTo(bytes));

        // corrupt a part -> rejected
        var corrupt = File.ReadAllBytes(Path.Combine(src, "WirelessScanner02.bin"));
        corrupt[10] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(src, "WirelessScanner02.bin"), corrupt);
        Assert.Throws<InvalidDataException>(() => PayloadAssembler.Assemble(src, loaded, outZip));

        // missing part -> actionable message
        File.Delete(Path.Combine(src, "WirelessScanner01.bin"));
        var ex = Assert.Throws<FileNotFoundException>(() => PayloadAssembler.Assemble(src, loaded, outZip));
        Assert.That(ex!.Message, Does.Contain("WirelessScanner01.bin"));
    }

    [Test]
    public void Extract_RemovesStaleFilesOnUpgrade_ButNeverUnmanagedFiles()
    {
        var install = Path.Combine(_root, "WirelessScanner");
        var v1 = Zip("v1.zip", ("WirelessScanner.Presentation.exe", "v1"), ("old.dll", "old"), ("keep.dll", "k1"));
        PackageExtractor.Extract(v1, install, "1.0.0");
        File.WriteAllText(Path.Combine(install, "wireless_scanner.db"), "user data"); // not installed by setup
        Assert.That(InstallManifest.TryLoad(install)!.Files, Is.EquivalentTo(new[] { "WirelessScanner.Presentation.exe", "old.dll", "keep.dll" }));

        var v2 = Zip("v2.zip", ("WirelessScanner.Presentation.exe", "v2"), ("keep.dll", "k2"), ("new/x.dll", "x"));
        var r = PackageExtractor.Extract(v2, install, "1.1.0");
        Assert.Multiple(() =>
        {
            Assert.That(r.RemovedStale, Is.EqualTo(new[] { "old.dll" }));
            Assert.That(File.Exists(Path.Combine(install, "old.dll")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(install, "keep.dll")), Is.EqualTo("k2"));
            Assert.That(File.Exists(Path.Combine(install, "new", "x.dll")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(install, "wireless_scanner.db")), Is.EqualTo("user data"), "unmanaged file untouched");
            Assert.That(InstallManifest.TryLoad(install)!.Version, Is.EqualTo("1.1.0"));
        });
    }

    [Test]
    public void Extract_RejectsZipSlipEntries()
    {
        var install = Path.Combine(_root, "WirelessScanner");
        var evil = Zip("evil.zip", ("ok.txt", "ok"), ("../escaped.txt", "bad"));
        var r = PackageExtractor.Extract(evil, install, "1.1.0");
        Assert.Multiple(() =>
        {
            Assert.That(r.SkippedUnsafe, Is.EqualTo(new[] { "../escaped.txt" }));
            Assert.That(File.Exists(Path.Combine(_root, "escaped.txt")), Is.False);
            Assert.That(File.Exists(Path.Combine(install, "ok.txt")), Is.True);
        });
    }

    [Test]
    public void PreserveLegacyData_CopiesDbLogsApiKey_WithoutOverwriting()
    {
        var install = Path.Combine(_root, "WirelessScanner");
        var data = Path.Combine(_root, "ProgramData", "WirelessScanner");
        Directory.CreateDirectory(Path.Combine(install, "logs"));
        Directory.CreateDirectory(Path.Combine(install, "API"));
        File.WriteAllText(Path.Combine(install, "wireless_scanner.db"), "legacy");
        File.WriteAllText(Path.Combine(install, "logs", "l.txt"), "log");
        File.WriteAllText(Path.Combine(install, "API", "api_key.md"), "key");

        var first = DataSafety.PreserveLegacyData(install, data);
        File.WriteAllText(Path.Combine(install, "wireless_scanner.db"), "changed");
        var second = DataSafety.PreserveLegacyData(install, data);
        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(3));
            Assert.That(second, Is.Empty);
            Assert.That(File.ReadAllText(Path.Combine(data, "wireless_scanner.db")), Is.EqualTo("legacy"));
            Assert.That(File.Exists(Path.Combine(data, "API", "api_key.md")), Is.True);
        });
    }

    [Test]
    public void ShortcutPlanner_CreatesWanted_AndRemovesUnwantedExisting()
    {
        var plan = ShortcutPlanner.Plan("d.lnk", "s.lnk", wantDesktop: false, wantStartMenu: true, exists: p => p == "d.lnk");
        Assert.That(plan.Select(c => (c.Path, c.Create)), Is.EqualTo(new[] { ("d.lnk", false), ("s.lnk", true) }));
        var none = ShortcutPlanner.Plan("d.lnk", "s.lnk", false, false, _ => false);
        Assert.That(none, Is.Empty, "nothing to delete when shortcuts do not exist");
    }

    [Test]
    public void ValidateInstallDir_RejectsDangerousLocations()
    {
        var pf = Path.Combine(_root, "Program Files");
        Directory.CreateDirectory(pf);
        var full = Path.Combine(_root, "full");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "someone-else.txt"), "x");
        Assert.Multiple(() =>
        {
            Assert.That(InstallPaths.ValidateInstallDir("", new[] { pf }), Is.Not.Null);
            Assert.That(InstallPaths.ValidateInstallDir(Path.GetPathRoot(_root)!, new[] { pf }), Does.Contain("root"));
            Assert.That(InstallPaths.ValidateInstallDir(pf, new[] { pf }), Does.Contain("dedicated"));
            Assert.That(InstallPaths.ValidateInstallDir(full, new[] { pf }), Does.Contain("not empty"));
            Assert.That(InstallPaths.ValidateInstallDir(Path.Combine(pf, "WirelessScanner"), new[] { pf }), Is.Null);
        });
    }
}
