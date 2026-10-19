using System.Net;
using WirelessScanner.Responder;

// WirelessScanner LAN responder (DR-0014 stage C). Run it on a WIRED machine on the same network as the Wi-Fi PC, then pair it in
// WirelessScanner: SETTINGS -> Test servers -> LAN responder (address, port, key).
//   WirelessScanner.Responder [--port 5410] [--bind 0.0.0.0] [--name "Lab Pi"] [--key-file path] [--show-key] [--new-key]

static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("WirelessScanner responder: authenticated TCP throughput + UDP reflector for two-way Wi-Fi tests.");
    Console.WriteLine("  --port N        TCP and UDP port (default 5410)");
    Console.WriteLine("  --bind IP       address to listen on (default all)");
    Console.WriteLine("  --name TEXT     name shown in WirelessScanner reports (default: computer name)");
    Console.WriteLine("  --key-file P    where the pairing key is stored (default: per-user application data)");
    Console.WriteLine("  --show-key      print the pairing key and exit");
    Console.WriteLine("  --new-key       replace the pairing key (paired apps must be updated)");
    return 0;
}

int port = int.TryParse(Arg(args, "--port"), out var p) && p is > 0 and < 65536 ? p : ResponderProtocol.DefaultPort;
var bind = Arg(args, "--bind") is string b ? IPAddress.Parse(b) : IPAddress.Any;
string keyFile = Arg(args, "--key-file") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WirelessScanner", "responder.key");

bool created = false;
if (args.Contains("--new-key") || !File.Exists(keyFile))
{
    Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
    File.WriteAllText(keyFile, ResponderProtocol.KeyToText(ResponderProtocol.NewKey()));
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    created = true;
}
var key = ResponderProtocol.KeyFromText(File.ReadAllText(keyFile));

if (args.Contains("--show-key") || created)
{
    Console.WriteLine($"Pairing key ({(created ? "new" : "existing")}, stored in {keyFile}):");
    Console.WriteLine($"  {ResponderProtocol.KeyToText(key)}");
    Console.WriteLine("Enter it in WirelessScanner: SETTINGS -> Test servers -> LAN responder. Keep it private.");
    if (args.Contains("--show-key")) return 0;
}

await using var server = new ResponderServer(key, port, bind, Arg(args, "--name"), m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"));
server.Start();
var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
    .Select(a => a.Address.ToString()).ToList();
Console.WriteLine($"Addresses of this machine: {string.Join(", ", addresses)} (port {server.Port}). Allow TCP and UDP {server.Port} in the firewall for the local network only.");
Console.WriteLine("Press Ctrl+C to stop.");
var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
await done.Task;
return 0;
