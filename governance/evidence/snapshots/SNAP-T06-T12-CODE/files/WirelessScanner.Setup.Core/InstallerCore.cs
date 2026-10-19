using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace WirelessScanner.Setup.Core;

// Installer lifecycle model (DR-0007, finding F06). Pure/file-system logic only; registry, shortcuts and UI live in the wizard.

public enum InstallKind
{
    Fresh,      // nothing installed
    Upgrade,    // payload newer than installed
    Reinstall,  // same version (repair)
    Downgrade   // payload older than installed: requires explicit confirmation
}

public enum MaintenanceAction
{
    Install,    // fresh install, upgrade or reinstall (needs payload)
    Modify,     // change shortcuts only (no payload needed)
    Uninstall
}

internal static class Json
{
    public static T? Read<T>(string path) where T : class
    {
        using var fs = File.OpenRead(path);
        return new DataContractJsonSerializer(typeof(T)).ReadObject(fs) as T;
    }

    public static void Write<T>(string path, T value)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, ownsStream: false, indent: true);
        new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value);
    }
}

/// <summary>Describes the application payload shipped next to the setup (written by tools/build_release.py).</summary>
[DataContract]
public sealed class PayloadInfo
{
    [DataMember(Name = "version")] public string Version { get; set; } = "0.0.0";
    [DataMember(Name = "sha256")] public string Sha256 { get; set; } = "";
    [DataMember(Name = "bytes")] public long Bytes { get; set; }
    [DataMember(Name = "parts")] public List<string> Parts { get; set; } = new();
    [DataMember(Name = "builtUtc")] public string BuiltUtc { get; set; } = "";

    public const string FileName = "WirelessScanner.payload.json";

    public static PayloadInfo? TryLoad(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        var info = Json.Read<PayloadInfo>(path);
        if (info != null) info.Parts ??= new List<string>();
        return info;
    }

    public void Save(string directory) => Json.Write(Path.Combine(directory, FileName), this);
}

public static class VersionPolicy
{
    public static Version Parse(string? v) => System.Version.TryParse((v ?? "").Trim().TrimStart('v', 'V'), out var parsed) ? parsed : new Version(0, 0, 0);

    public static InstallKind Classify(string? installedVersion, string payloadVersion, bool isInstalled)
    {
        if (!isInstalled) return InstallKind.Fresh;
        int cmp = Parse(payloadVersion).CompareTo(Parse(installedVersion));
        return cmp > 0 ? InstallKind.Upgrade : cmp == 0 ? InstallKind.Reinstall : InstallKind.Downgrade;
    }
}

public static class PayloadAssembler
{
    /// <summary>Concatenates the split parts (in name order) into a zip and verifies length and SHA-256 against the payload manifest.</summary>
    public static string Assemble(string sourceDirectory, PayloadInfo info, string outputZip)
    {
        var parts = info.Parts.Count > 0
            ? info.Parts.Select(p => Path.Combine(sourceDirectory, p)).ToList()
            : Directory.GetFiles(sourceDirectory, "WirelessScanner*.bin").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = parts.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
        if (parts.Count == 0 || missing.Count > 0)
            throw new FileNotFoundException("Installation files are missing: " + (missing.Count > 0 ? string.Join(", ", missing) : "no WirelessScanner*.bin parts") +
                ". Download every file from the release into the same folder as the setup.");

        using (var output = new FileStream(outputZip, FileMode.Create, FileAccess.Write))
        {
            foreach (var part in parts)
            {
                using var input = File.OpenRead(part);
                input.CopyTo(output);
            }
        }

        var actual = Sha256Of(outputZip);
        long length = new FileInfo(outputZip).Length;
        if (info.Bytes > 0 && length != info.Bytes)
            throw new InvalidDataException($"Reassembled package is {length:N0} bytes but {info.Bytes:N0} were expected. A part is incomplete or corrupt; download the release again.");
        if (!string.IsNullOrEmpty(info.Sha256) && !string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reassembled package failed its SHA-256 integrity check. A part is corrupt; download the release again.");
        return actual;
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>List of files the installer placed, used to remove stale files on upgrade and everything on uninstall.</summary>
[DataContract]
public sealed class InstallManifest
{
    public const string FileName = "install-manifest.json";
    [DataMember(Name = "version")] public string Version { get; set; } = "";
    [DataMember(Name = "files")] public List<string> Files { get; set; } = new();

    public static InstallManifest? TryLoad(string installDir)
    {
        var p = Path.Combine(installDir, FileName);
        if (!File.Exists(p)) return null;
        var m = Json.Read<InstallManifest>(p);
        if (m != null) m.Files ??= new List<string>();
        return m;
    }

    public void Save(string installDir) => Json.Write(Path.Combine(installDir, FileName), this);
}

public sealed class ExtractResult
{
    public int Extracted { get; set; }
    public List<string> RemovedStale { get; } = new();
    public List<string> SkippedUnsafe { get; } = new();
    public InstallManifest Manifest { get; set; } = new();
}

public static class PackageExtractor
{
    /// <summary>
    /// Extracts the package into installDir, refusing entries that escape the folder (zip-slip), then removes files that
    /// the previous manifest installed but the new package no longer contains. Never touches files it did not install.
    /// </summary>
    public static ExtractResult Extract(string zipPath, string installDir, string version, Action<string>? log = null)
    {
        var result = new ExtractResult();
        var root = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var previous = InstallManifest.TryLoad(root);
        var installed = new List<string>();

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedUnsafe.Add(entry.FullName);
                    continue;
                }
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                installed.Add(Relative(root, dest));
                result.Extracted++;
                if (result.Extracted % 25 == 0) log?.Invoke($"Extracted {result.Extracted}/{archive.Entries.Count} files...");
            }
        }

        if (previous != null)
        {
            var keep = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in previous.Files.Where(f => !keep.Contains(f)))
            {
                var p = Path.GetFullPath(Path.Combine(root, stale));
                if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                {
                    File.Delete(p);
                    result.RemovedStale.Add(stale);
                }
            }
        }

        result.Manifest = new InstallManifest { Version = version, Files = installed };
        result.Manifest.Save(root);
        return result;
    }

    public static string Relative(string root, string full) => full.Substring(root.Length).Replace('\\', '/');
}

public static class DataSafety
{
    public static readonly string[] LegacyDataItems = { "wireless_scanner.db", "wireless_scanner.db-wal", "wireless_scanner.db-shm" };
    public static readonly string[] LegacyDataFolders = { "logs", "API" };

    /// <summary>
    /// Releases before 1.1 kept the database, logs and API key inside the install folder. Before that folder is changed
    /// or removed, copy them to the data directory unless a copy already exists there. Returns the actions taken.
    /// </summary>
    public static List<string> PreserveLegacyData(string installDir, string dataDir)
    {
        var actions = new List<string>();
        if (!Directory.Exists(installDir)) return actions;
        Directory.CreateDirectory(dataDir);
        foreach (var name in LegacyDataItems)
        {
            var src = Path.Combine(installDir, name);
            var dst = Path.Combine(dataDir, name);
            if (File.Exists(src) && !File.Exists(dst))
            {
                File.Copy(src, dst);
                actions.Add($"Preserved {name} -> {dataDir}");
            }
        }
        foreach (var folder in LegacyDataFolders)
        {
            var src = Path.Combine(installDir, folder);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(dataDir, folder);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var d = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(d))
                {
                    File.Copy(f, d);
                    actions.Add($"Preserved {folder}/{Path.GetFileName(f)} -> {dst}");
                }
            }
        }
        return actions;
    }
}

public sealed class ShortcutChange
{
    public string Path { get; set; } = "";
    public bool Create { get; set; }
}

public static class ShortcutPlanner
{
    /// <summary>Desired state wins: create wanted shortcuts (refreshing targets) and delete unwanted ones that exist.</summary>
    public static List<ShortcutChange> Plan(string desktopLnk, string startMenuLnk, bool wantDesktop, bool wantStartMenu, Func<string, bool> exists)
    {
        var changes = new List<ShortcutChange>();
        void One(string path, bool want)
        {
            if (want) changes.Add(new ShortcutChange { Path = path, Create = true });
            else if (exists(path)) changes.Add(new ShortcutChange { Path = path, Create = false });
        }
        One(desktopLnk, wantDesktop);
        One(startMenuLnk, wantStartMenu);
        return changes;
    }
}

public static class InstallPaths
{
    public static string DefaultInstallDir(string programFiles) => Path.Combine(programFiles, "WirelessScanner");
    public static string DataDir(string commonAppData) => Path.Combine(commonAppData, "WirelessScanner");
    public static string MaintenanceDir(string installDir) => Path.Combine(installDir, "Setup");

    /// <summary>Rejects install locations that would be dangerous to delete on uninstall.</summary>
    public static string? ValidateInstallDir(string dir, IEnumerable<string> forbiddenRoots)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "Choose an installation folder.";
        string full;
        try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
        catch (Exception) { return "The installation folder path is not valid."; }
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full) return "Do not install to the root of a drive.";
        foreach (var root in forbiddenRoots.Where(r => !string.IsNullOrEmpty(r)))
        {
            if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
                return "Choose a dedicated folder (e.g. ...\\WirelessScanner), not a system folder.";
        }
        if (!full.EndsWith("WirelessScanner", StringComparison.OrdinalIgnoreCase) && Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()
            && InstallManifest.TryLoad(full) == null)
            return "The folder is not empty and was not created by this installer. Choose an empty or new folder.";
        return null;
    }
}
