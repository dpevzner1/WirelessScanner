using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using WirelessScanner.Setup.Core;

namespace WirelessScanner.Setup;

// WirelessScanner setup wizard (DR-0007). .NET Framework 4.8 (built into Windows 10/11): no runtime prerequisite.
// Modes: fresh install; maintenance of an existing install (upgrade / repair / downgrade, modify shortcuts, uninstall).
// Lifecycle logic lives in WirelessScanner.Setup.Core and is unit-tested; this file is UI, registry and shortcuts.

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var mode = args.Select(a => a.Trim().ToLowerInvariant()).FirstOrDefault(a => a == "/uninstall" || a == "/modify");
            Application.Run(new SetupWizardForm(mode));
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "WirelessScanner.Setup.error.log");
            File.WriteAllText(logPath, ex.ToString());
            MessageBox.Show($"WirelessScanner Setup could not start. Details written to:{Environment.NewLine}{logPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Setup startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class SetupState
{
    public string InstallDir = InstallPaths.DefaultInstallDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    public string DataDir = InstallPaths.DataDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    public bool Installed;
    public string? InstalledVersion;
    public PayloadInfo? Payload;
    public DetectedInstall Detected = new();
    public MaintenanceAction Action = MaintenanceAction.Install;
    public InstallKind Kind = InstallKind.Fresh;
    public bool DesktopShortcut = true;
    public bool StartMenuShortcut = true;
    public bool LaunchAfter = true;
    public bool DeleteUserData;
    public bool Succeeded;
    public string Summary = "";
    public bool RunningFromMaintenanceCopy;
}

internal sealed class SetupWizardForm : Form
{
    private const string RegKey = @"SOFTWARE\WirelessScanner";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WirelessScanner";
    private const string ExeName = "WirelessScanner.Presentation.exe";

    private readonly SetupState _s = new();
    private readonly List<Control> _pages = new();
    private readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(22) };
    private readonly Button _back = new() { Text = "Back", Width = 96, Height = 34 };
    private readonly Button _next = new() { Text = "Next", Width = 110, Height = 34 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 96, Height = 34 };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 58, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
    private TextBox _log = null!;
    private Label _finishedText = null!;
    private CheckBox _launchCheck = null!;
    private int _page;
    private bool _busy;
    private string _logFile = "";

    private readonly Color _bg = Color.FromArgb(10, 10, 12);
    private readonly Color _panel = Color.FromArgb(18, 18, 22);
    private readonly Color _text = Color.FromArgb(230, 230, 235);
    private readonly Color _sub = Color.FromArgb(160, 150, 130);
    private readonly Color _amber = Color.FromArgb(255, 170, 0);

    private string SetupDir => AppDomain.CurrentDomain.BaseDirectory;
    private string DesktopLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "WirelessScanner.lnk");
    private string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "WirelessScanner");
    private string StartMenuLnk => Path.Combine(StartMenuDir, "WirelessScanner.lnk");

    public SetupWizardForm(string? mode)
    {
        Text = "WirelessScanner Setup";
        Width = 820;
        Height = 580;
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _bg;
        ForeColor = _text;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* cosmetic */ }

        _title.BackColor = _bg;
        _title.ForeColor = _amber;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.Padding = new Padding(22, 10, 0, 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), BackColor = Color.FromArgb(15, 14, 18) };
        foreach (var b in new[] { _cancel, _next, _back }) { Style(b); footer.Controls.Add(b); }
        Controls.Add(_host);
        Controls.Add(footer);
        Controls.Add(_title);

        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += (_, _) => Next();
        _cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                MessageBox.Show("Please wait for the current operation to finish.", "Setup busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        DetectState(mode);
        BuildPages();
        Show(0);
    }

    // ------------------------------------------------------------------ state
    private void DetectState(string? mode)
    {
        _s.Payload = PayloadInfo.TryLoad(SetupDir);
        try
        {
            // Look at both our app key and the Apps & features entry, 64-bit view first, then WOW6432Node.
            _s.Detected = InstallDetector.Detect(new[] { ReadView(RegistryView.Registry64), ReadView(RegistryView.Registry32) }, File.Exists, InstallManifest.TryLoad);
            if (_s.Detected.Installed)
            {
                _s.Installed = true;
                _s.InstallDir = _s.Detected.InstallDir!;
                _s.InstalledVersion = _s.Detected.Version ?? "0.0.0";
            }
        }
        catch (Exception ex)
        {
            _s.Detected = new DetectedInstall();
            _s.Detected.Notes.Add($"Could not read the registry ({ex.Message}); treating as not installed.");
        }

        if (_s.Installed)
        {
            _s.DesktopShortcut = File.Exists(DesktopLnk) || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WirelessScanner.lnk"));
            _s.StartMenuShortcut = File.Exists(StartMenuLnk) || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WirelessScanner"));
            _s.RunningFromMaintenanceCopy = string.Equals(Path.GetFullPath(SetupDir).TrimEnd('\\'),
                Path.GetFullPath(InstallPaths.MaintenanceDir(_s.InstallDir)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        _s.Action = mode == "/uninstall" ? MaintenanceAction.Uninstall
            : mode == "/modify" || (_s.Installed && _s.Payload == null) ? MaintenanceAction.Modify
            : MaintenanceAction.Install;
        _s.Kind = _s.Payload != null ? VersionPolicy.Classify(_s.InstalledVersion, _s.Payload.Version, _s.Installed) : InstallKind.Fresh;
    }

    /// <summary>Reads our app key and the Apps &amp; features entry from one registry view.</summary>
    private static RegistrySnapshot ReadView(RegistryView view)
    {
        var snap = new RegistrySnapshot { View = view == RegistryView.Registry64 ? "64-bit" : "32-bit" };
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using (var app = hklm.OpenSubKey(RegKey))
            {
                snap.AppInstallDir = app?.GetValue("InstallDir") as string;
                snap.AppVersion = app?.GetValue("Version") as string;
            }
            using var u = hklm.OpenSubKey(UninstallKey);
            snap.UninstallInstallLocation = u?.GetValue("InstallLocation") as string;
            snap.UninstallDisplayVersion = u?.GetValue("DisplayVersion") as string;
            snap.UninstallString = u?.GetValue("UninstallString") as string;
            snap.DisplayIcon = u?.GetValue("DisplayIcon") as string;
        }
        catch { /* view unavailable: leave empty */ }
        return snap;
    }

    // ------------------------------------------------------------------ pages
    private void BuildPages()
    {
        _pages.Clear();
        _pages.Add(_s.Installed ? MaintenancePage() : WelcomePage());
        _pages.Add(OptionsPage());
        _pages.Add(UninstallOptionsPage());
        _pages.Add(ProgressPage());
        _pages.Add(FinishedPage());
    }

    private Control WelcomePage()
    {
        var p = BasePage("Welcome to WirelessScanner Setup");
        p.Name = "Welcome";
        p.Controls.Add(Para(
            "WirelessScanner is a Wi-Fi signal analysis, site survey and signal saturation test tool for Windows.\n\n" +
            (_s.Payload == null
                ? "The installation package (WirelessScanner.payload.json and WirelessScanner*.bin files) was not found next to this setup. Download every file of the release into one folder and run setup again."
                : $"This wizard installs version {_s.Payload.Version}.\n\nYour data (sessions, saturation tests, logs, API key) is stored separately in {_s.DataDir} and is never removed unless you choose to during uninstall.\n\nAdministrator rights are required to install and to run WirelessScanner."),
            700, 260));
        return p;
    }

    private Control MaintenancePage()
    {
        var p = BasePage("WirelessScanner is already installed");
        p.Name = "Maintenance";
        p.Controls.Add(Para($"Installed: version {_s.InstalledVersion} in {_s.InstallDir}\n" +
                            $"Found via: {_s.Detected.Source}\n" +
                            (_s.Payload != null ? $"This setup contains: version {_s.Payload.Version}" : "This copy of setup has no installation package (maintenance copy): repair and update need the downloaded release files.") +
                            (_s.Detected.Notes.Count > 0 ? "\n\n" + string.Join("\n", _s.Detected.Notes.Select(n => "• " + n)) : ""),
            700, _s.Detected.Notes.Count > 0 ? 80 + 18 * _s.Detected.Notes.Count : 80));

        string installLabel = _s.Kind switch
        {
            InstallKind.Upgrade when _s.Detected.NeedsRepair => $"Repair and update to version {_s.Payload!.Version} (program files are missing)",
            InstallKind.Upgrade => $"Update to version {_s.Payload!.Version}",
            InstallKind.Reinstall => $"Repair (reinstall version {_s.Payload!.Version})",
            InstallKind.Downgrade => $"Downgrade to OLDER version {_s.Payload!.Version} (not recommended)",
            _ => "Install"
        };
        var install = Radio(installLabel, _s.Action == MaintenanceAction.Install, () => _s.Action = MaintenanceAction.Install);
        install.Enabled = _s.Payload != null;
        p.Controls.Add(install);
        p.Controls.Add(Radio("Modify: change Desktop / Start Menu shortcuts", _s.Action == MaintenanceAction.Modify, () => _s.Action = MaintenanceAction.Modify));
        p.Controls.Add(Radio("Uninstall WirelessScanner", _s.Action == MaintenanceAction.Uninstall, () => _s.Action = MaintenanceAction.Uninstall));
        p.Controls.Add(Para("Your data is kept in " + _s.DataDir + " for every option unless you explicitly choose to delete it during uninstall.", 700, 50));
        return p;
    }

    private Control OptionsPage()
    {
        var p = BasePage("Installation options");
        p.Name = "Options";
        p.Controls.Add(Para("Installation folder:", 700, 24));
        var row = new FlowLayoutPanel { Width = 720, Height = 40, FlowDirection = FlowDirection.LeftToRight };
        var dir = new TextBox { Text = _s.InstallDir, Width = 560, Font = new Font("Segoe UI", 10), BackColor = _panel, ForeColor = _text, BorderStyle = BorderStyle.FixedSingle, Name = "Dir" };
        dir.TextChanged += (_, _) => _s.InstallDir = dir.Text.Trim();
        var browse = new Button { Text = "Browse...", Width = 110, Height = 28 };
        Style(browse);
        browse.Click += (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { SelectedPath = dir.Text };
            if (fbd.ShowDialog() == DialogResult.OK) dir.Text = Path.Combine(fbd.SelectedPath, fbd.SelectedPath.EndsWith("WirelessScanner", StringComparison.OrdinalIgnoreCase) ? "" : "WirelessScanner");
        };
        if (_s.Installed)
        {
            dir.ReadOnly = true;
            browse.Enabled = false;
        }
        row.Controls.Add(dir);
        row.Controls.Add(browse);
        p.Controls.Add(row);
        if (_s.Installed) p.Controls.Add(Para("The folder of an existing installation cannot be changed here (this prevents orphaned files). To move it, uninstall first.", 700, 40));

        p.Controls.Add(Section("Shortcuts"));
        p.Controls.Add(Check("Desktop shortcut (all users)", _s.DesktopShortcut, v => _s.DesktopShortcut = v));
        p.Controls.Add(Check("Start Menu entry (all users)", _s.StartMenuShortcut, v => _s.StartMenuShortcut = v));
        p.Controls.Add(Para("Unticking a shortcut removes it.", 700, 24));
        return p;
    }

    private Control UninstallOptionsPage()
    {
        var p = BasePage("Uninstall options");
        p.Name = "UninstallOptions";
        p.Controls.Add(Para($"WirelessScanner will be removed from {_s.InstallDir}, together with its shortcuts and its entry in Apps & features.", 700, 50));
        p.Controls.Add(Section("Your data"));
        p.Controls.Add(Para($"Capture sessions, saturation tests, logs and the REST API key are stored in:\n{_s.DataDir}\nThey are KEPT by default, so a later reinstall continues where you left off.", 700, 70));
        p.Controls.Add(Check("Also permanently delete all WirelessScanner data (cannot be undone)", false, v => _s.DeleteUserData = v));
        return p;
    }

    private Control ProgressPage()
    {
        var p = BasePage("Working...");
        p.Name = "Progress";
        _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9), BackColor = _panel, ForeColor = _amber, Width = 720, Height = 300, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(_log);
        return p;
    }

    private Control FinishedPage()
    {
        var p = BasePage("Done");
        p.Name = "Finished";
        _finishedText = Para("", 700, 200);
        p.Controls.Add(_finishedText);
        _launchCheck = Check("Launch WirelessScanner now", true, v => _s.LaunchAfter = v);
        p.Controls.Add(_launchCheck);
        return p;
    }

    // ------------------------------------------------------------------ navigation
    private IEnumerable<string> Flow()
    {
        yield return _pages[0].Name;
        if (_s.Action == MaintenanceAction.Uninstall) yield return "UninstallOptions";
        else if (_s.Payload != null || _s.Action == MaintenanceAction.Modify) yield return "Options";
        yield return "Progress";
        yield return "Finished";
    }

    private void Show(int index)
    {
        var flow = Flow().ToList();
        index = Math.Max(0, Math.Min(index, flow.Count - 1));
        _page = index;
        var page = _pages.First(c => c.Name == flow[index]);
        _host.Controls.Clear();
        _host.Controls.Add(page);
        _title.Text = $"WirelessScanner Setup  —  Step {index + 1} of {flow.Count}";
        string name = flow[index];
        _back.Enabled = index > 0 && name != "Progress" && name != "Finished";
        _next.Text = name == "Finished" ? "Finish"
            : flow[Math.Min(index + 1, flow.Count - 1)] == "Progress"
                ? (_s.Action == MaintenanceAction.Uninstall ? "Uninstall" : _s.Action == MaintenanceAction.Modify ? "Apply" : _s.Kind == InstallKind.Upgrade ? "Update" : _s.Kind == InstallKind.Reinstall ? "Repair" : "Install")
                : "Next";
        _next.Enabled = name != "Progress" && !(name == "Welcome" && _s.Payload == null);
        _cancel.Enabled = name != "Progress";
    }

    private void Next()
    {
        var flow = Flow().ToList();
        string current = flow[_page];
        if (current == "Finished")
        {
            if (_s.Succeeded && _s.LaunchAfter && _s.Action != MaintenanceAction.Uninstall)
            {
                var exe = Path.Combine(_s.InstallDir, ExeName);
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = _s.InstallDir });
            }
            Close();
            return;
        }

        if (current == "Options" && !_s.Installed)
        {
            var problem = InstallPaths.ValidateInstallDir(_s.InstallDir, new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            });
            if (problem != null)
            {
                MessageBox.Show(problem, "Installation folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (current == "Maintenance" && _s.Action == MaintenanceAction.Install && _s.Kind == InstallKind.Downgrade &&
            MessageBox.Show($"Version {_s.Payload!.Version} is OLDER than the installed {_s.InstalledVersion}. Newer data may not be readable by the older version.\n\nDowngrade anyway?",
                "Confirm downgrade", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        if (flow[_page + 1] == "Progress")
        {
            if (_s.Action == MaintenanceAction.Uninstall)
            {
                var msg = "Uninstall WirelessScanner now?" + (_s.DeleteUserData ? $"\n\nALL data in {_s.DataDir} will be permanently deleted." : $"\n\nYour data in {_s.DataDir} will be kept.");
                if (MessageBox.Show(msg, "Confirm uninstall", MessageBoxButtons.YesNo, _s.DeleteUserData ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            Show(_page + 1);
            Run();
            return;
        }
        Show(_page + 1);
    }

    // ------------------------------------------------------------------ operations
    private void Run()
    {
        _busy = true;
        Directory.CreateDirectory(Path.Combine(_s.DataDir, "logs"));
        _logFile = Path.Combine(_s.DataDir, "logs", $"setup_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        Log($"WirelessScanner Setup ({_s.Action}, {_s.Kind}) started {DateTime.UtcNow:O}");
        try
        {
            switch (_s.Action)
            {
                case MaintenanceAction.Install: DoInstall(); break;
                case MaintenanceAction.Modify: ApplyShortcuts(Path.Combine(_s.InstallDir, ExeName)); _s.Summary = "Shortcut changes applied."; break;
                case MaintenanceAction.Uninstall: DoUninstall(); break;
            }
            _s.Succeeded = true;
            Log("Completed successfully.");
        }
        catch (Exception ex)
        {
            _s.Succeeded = false;
            _s.Summary = $"The operation FAILED: {ex.Message}\n\nNothing you did not choose to delete has been removed. See the log:\n{_logFile}";
            Log("ERROR: " + ex);
        }
        finally
        {
            _busy = false;
        }

        _finishedText.Text = _s.Summary + (_s.Succeeded ? $"\n\nSetup log: {_logFile}" : "");
        _launchCheck.Visible = _s.Succeeded && _s.Action != MaintenanceAction.Uninstall;
        Show(_page + 1);
    }

    private void DoInstall()
    {
        StopRunningApp();
        if (_s.Installed)
        {
            foreach (var a in DataSafety.PreserveLegacyData(_s.InstallDir, _s.DataDir)) Log(a);
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"WirelessScanner_{Guid.NewGuid():N}.zip");
        try
        {
            Log("Reassembling and verifying the installation package...");
            var sha = PayloadAssembler.Assemble(SetupDir, _s.Payload!, tempZip);
            Log($"Package verified (SHA-256 {sha.Substring(0, 16)}...).");

            Log($"Extracting to {_s.InstallDir} ...");
            var result = PackageExtractor.Extract(tempZip, _s.InstallDir, _s.Payload!.Version, Log);
            Log($"Extracted {result.Extracted} files; removed {result.RemovedStale.Count} stale files from the previous version.");
            foreach (var u in result.SkippedUnsafe) Log($"SKIPPED unsafe archive entry: {u}");
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }

        CopyMaintenanceFiles();
        var exe = Path.Combine(_s.InstallDir, ExeName);
        ApplyShortcuts(exe);
        Register(exe);

        _s.Summary = _s.Kind switch
        {
            InstallKind.Upgrade => $"WirelessScanner was updated from {_s.InstalledVersion} to {_s.Payload.Version}.",
            InstallKind.Reinstall => $"WirelessScanner {_s.Payload.Version} was repaired (all program files reinstalled).",
            InstallKind.Downgrade => $"WirelessScanner was downgraded to {_s.Payload.Version}.",
            _ => $"WirelessScanner {_s.Payload.Version} was installed in {_s.InstallDir}."
        } + $"\n\nYour data lives in {_s.DataDir}. Administrator rights are required each time WirelessScanner starts (raw Wi-Fi telemetry).";
    }

    /// <summary>Keep a copy of setup inside the install folder so Apps &amp; features can always modify/uninstall (F06).</summary>
    private void CopyMaintenanceFiles()
    {
        var target = InstallPaths.MaintenanceDir(_s.InstallDir);
        if (string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(SetupDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(SetupDir).Where(f =>
                     f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".config", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals(PayloadInfo.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            if (Path.GetFileName(f).StartsWith("WirelessScanner", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
        }
        // the maintenance copy carries no payload parts, so it advertises no installable version
        var meta = Path.Combine(target, PayloadInfo.FileName);
        if (File.Exists(meta)) File.Delete(meta);
        Log($"Maintenance copy of setup saved to {target}.");
    }

    private void ApplyShortcuts(string exe)
    {
        // Remove per-user shortcuts written by setup 1.0 (they pointed at the same target)
        var legacyDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WirelessScanner.lnk");
        var legacyStart = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WirelessScanner");
        if (File.Exists(legacyDesktop)) { File.Delete(legacyDesktop); Log("Removed legacy per-user desktop shortcut."); }
        if (Directory.Exists(legacyStart)) { Directory.Delete(legacyStart, true); Log("Removed legacy per-user Start Menu folder."); }

        foreach (var change in ShortcutPlanner.Plan(DesktopLnk, StartMenuLnk, _s.DesktopShortcut, _s.StartMenuShortcut, File.Exists))
        {
            if (change.Create)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                CreateShortcut(change.Path, exe, _s.InstallDir, "WirelessScanner — Wi-Fi signal analysis and saturation testing");
                Log($"Shortcut created: {change.Path}");
            }
            else
            {
                File.Delete(change.Path);
                Log($"Shortcut removed: {change.Path}");
            }
        }
        if (!_s.StartMenuShortcut && Directory.Exists(StartMenuDir) && !Directory.EnumerateFileSystemEntries(StartMenuDir).Any()) Directory.Delete(StartMenuDir);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private void Register(string exe)
    {
        var maint = Path.Combine(InstallPaths.MaintenanceDir(_s.InstallDir), "WirelessScanner.Setup.exe");
        long kb = 0;
        try { kb = new DirectoryInfo(_s.InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; } catch { }

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = hklm.CreateSubKey(RegKey))
        {
            key.SetValue("InstallDir", _s.InstallDir);
            key.SetValue("Version", _s.Payload!.Version);
            key.SetValue("DataDir", _s.DataDir);
        }
        using (var u = hklm.CreateSubKey(UninstallKey))
        {
            u.SetValue("DisplayName", "WirelessScanner");
            u.SetValue("DisplayIcon", $"{exe},0");
            u.SetValue("DisplayVersion", _s.Payload.Version);
            u.SetValue("Publisher", "Demitri Pevzner");
            u.SetValue("URLInfoAbout", "https://github.com/dpevzner1/WirelessScanner");
            u.SetValue("InstallLocation", _s.InstallDir);
            u.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            u.SetValue("UninstallString", $"\"{maint}\" /uninstall");
            u.SetValue("ModifyPath", $"\"{maint}\" /modify");
            u.SetValue("NoModify", 0, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord); // repair needs the downloaded release (payload)
            u.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
        RemoveStale32BitEntries();
        Log("Registered in Apps & features (modify / uninstall point to the maintenance copy).");
        // The icon lives in the .exe; tell the shell so Explorer, Start and Apps & features drop cached icons.
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); Log("Refreshed the Windows icon cache."); }
        catch (Exception ex) { Log($"Icon cache refresh skipped: {ex.Message}"); }
    }

    /// <summary>Entries in WOW6432Node would make Apps &amp; features show WirelessScanner twice; setup only uses the 64-bit view.</summary>
    private void RemoveStale32BitEntries()
    {
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            foreach (var k in new[] { UninstallKey, RegKey })
            {
                using var existing = hklm32.OpenSubKey(k);
                if (existing == null) continue;
                existing.Dispose();
                hklm32.DeleteSubKeyTree(k, throwOnMissingSubKey: false);
                Log($"Removed stale 32-bit registry entry {k}.");
            }
        }
        catch (Exception ex) { Log($"Could not check 32-bit registry entries: {ex.Message}"); }
    }

    private void DoUninstall()
    {
        StopRunningApp();
        if (!_s.DeleteUserData)
        {
            foreach (var a in DataSafety.PreserveLegacyData(_s.InstallDir, _s.DataDir)) Log(a);
        }

        _s.DesktopShortcut = false;
        _s.StartMenuShortcut = false;
        ApplyShortcuts(Path.Combine(_s.InstallDir, ExeName));

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            hklm.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            hklm.DeleteSubKeyTree(RegKey, throwOnMissingSubKey: false);
        }
        RemoveStale32BitEntries();
        Log("Removed registry entries.");

        var maintenance = InstallPaths.MaintenanceDir(_s.InstallDir);
        if (Directory.Exists(_s.InstallDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_s.InstallDir).ToList())
            {
                if (_s.RunningFromMaintenanceCopy && string.Equals(entry.TrimEnd('\\'), maintenance.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            }
            Log($"Removed program files from {_s.InstallDir}.");
            if (_s.RunningFromMaintenanceCopy)
            {
                // A running exe cannot delete itself: remove the folder shortly after this process exits.
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{_s.InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                Log("Scheduled removal of the setup folder after exit.");
            }
            else
            {
                Directory.Delete(_s.InstallDir, true);
            }
        }

        if (_s.DeleteUserData && Directory.Exists(_s.DataDir))
        {
            // the setup log itself lives in DataDir: finish logging to %TEMP%
            var tempLog = Path.Combine(Path.GetTempPath(), Path.GetFileName(_logFile));
            File.Copy(_logFile, tempLog, true);
            _logFile = tempLog;
            Directory.Delete(_s.DataDir, true);
            Log($"Deleted all user data in {_s.DataDir}.");
        }

        _s.Summary = "WirelessScanner has been uninstalled." + (_s.DeleteUserData ? "\n\nAll user data was deleted." : $"\n\nYour data was kept in {_s.DataDir}; reinstalling restores access to it.");
    }

    private void StopRunningApp()
    {
        foreach (var p in Process.GetProcessesByName("WirelessScanner.Presentation"))
        {
            try
            {
                Log($"Closing running WirelessScanner (PID {p.Id})...");
                if (!p.CloseMainWindow() || !p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); }
            }
            catch (Exception ex) { Log($"Could not close PID {p.Id}: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ helpers
    private void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        _log?.AppendText(line + Environment.NewLine);
        try { if (!string.IsNullOrEmpty(_logFile)) File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
        Application.DoEvents();
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
        var st = shortcut?.GetType() ?? throw new InvalidOperationException("Failed to invoke CreateShortcut.");
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { $"{targetPath},0" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
        if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }

    private void Style(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = _panel;
        b.ForeColor = _text;
        b.FlatAppearance.BorderColor = _amber;
        b.FlatAppearance.BorderSize = 1;
        b.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    private FlowLayoutPanel BasePage(string header)
    {
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = _bg };
        layout.Controls.Add(new Label { Text = header, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = _amber, AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        return layout;
    }

    private Label Para(string text, int width, int height) =>
        new() { Text = text, Width = width, Height = height, Font = new Font("Segoe UI", 10), ForeColor = _sub };

    private Label Section(string text) =>
        new() { Text = text, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = _amber, Width = 700, Height = 30, Margin = new Padding(0, 14, 0, 0) };

    private RadioButton Radio(string text, bool isChecked, Action onChecked)
    {
        var r = new RadioButton { Text = text, Checked = isChecked, Font = new Font("Segoe UI", 10.5f), ForeColor = _text, Width = 700, Height = 32 };
        r.CheckedChanged += (_, _) => { if (r.Checked) onChecked(); };
        return r;
    }

    private CheckBox Check(string text, bool isChecked, Action<bool> onChanged)
    {
        var c = new CheckBox { Text = text, Checked = isChecked, Font = new Font("Segoe UI", 10.5f), ForeColor = _text, Width = 700, Height = 30 };
        c.CheckedChanged += (_, _) => onChanged(c.Checked);
        return c;
    }
}
