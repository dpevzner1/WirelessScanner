using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

/// <summary>
/// Connection-test probes for Windows (DR-0014). Every probe is bound to the Wi-Fi interface's IPv4 address, so a PC that also has
/// Ethernet still measures the Wi-Fi path (strong-host send model). Timing uses Stopwatch (QPC) around each probe; ICMP uses
/// IcmpSendEcho2Ex with an explicit source address (the .NET Ping class cannot bind a source). Network failures become outcomes.
/// </summary>
public sealed class WindowsConnectionProber : IConnectionProber
{
    private readonly INetworkAdapterProvider _adapters;
    private readonly IAppLogger _logger;

    public WindowsConnectionProber(INetworkAdapterProvider adapters, IAppLogger logger)
    {
        _adapters = adapters;
        _logger = logger;
    }

    // ------------------------------------------------------------------ context

    internal static NetworkInterface? FindInterface(Guid id) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == id);

    public async Task<ConnectionContext> GetContextAsync(Guid interfaceId, CancellationToken ct = default)
    {
        var nic = FindInterface(interfaceId);
        if (nic == null) return new ConnectionContext { InterfaceId = interfaceId, Problem = "The Wi-Fi adapter was not found." };
        var ip = nic.GetIPProperties();
        string? v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        var gateways = ip.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)).Select(g => g.Address.ToString()).ToList();
        var dns = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Distinct().ToList();
        int? index = null;
        try { index = ip.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { }
        var others = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.Id != nic.Id && n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .Select(n => $"{n.Name} ({n.Description})").ToList();

        ClientConnectionInfo? conn = null;
        WlanLinkReading? link = null;
        try { conn = await _adapters.GetConnectionInfoAsync(interfaceId); } catch (Exception ex) { _logger.Warn($"Connection info failed: {ex.Message}"); }
        try { link = await _adapters.ReadLinkAsync(interfaceId); } catch (Exception ex) { _logger.Warn($"Link read failed: {ex.Message}"); }
        bool wifi = (conn?.Connected ?? link?.Connected ?? false) && v4 != null && nic.OperationalStatus == OperationalStatus.Up;
        return new ConnectionContext
        {
            WifiConnected = wifi,
            InterfaceId = interfaceId,
            InterfaceName = nic.Description,
            InterfaceIndex = index,
            Ssid = conn?.Ssid ?? link?.Ssid,
            Bssid = conn?.Bssid ?? link?.Bssid,
            Channel = conn?.Channel ?? link?.Channel,
            Band = conn?.Band,
            PhyType = conn?.PhyType ?? link?.PhyType,
            Rssi = link?.Rssi,
            RxRateMbps = conn?.RxRateMbps ?? link?.RxRateMbps,
            TxRateMbps = conn?.TxRateMbps ?? link?.TxRateMbps,
            LocalIPv4 = v4,
            Gateways = gateways,
            DnsServers = dns,
            OtherActiveInterfaces = others,
            Problem = wifi ? null : v4 == null ? "The Wi-Fi adapter has no IPv4 address." : "The Wi-Fi adapter is not connected to a network."
        };
    }

    public Task<WlanLinkReading?> ReadLinkAsync(Guid interfaceId) => _adapters.ReadLinkAsync(interfaceId);

    private NetworkInterface? _statsNic;
    private Guid _statsId;

    public (long BytesIn, long BytesOut)? InterfaceBytes(Guid interfaceId)
    {
        try
        {
            if (_statsNic == null || _statsId != interfaceId) { _statsNic = FindInterface(interfaceId); _statsId = interfaceId; }
            if (_statsNic == null) return null;
            var s = _statsNic.GetIPStatistics();
            return (s.BytesReceived, s.BytesSent);
        }
        catch (NetworkInformationException) { _statsNic = null; return null; }
    }

    // ------------------------------------------------------------------ ICMP (IcmpSendEcho2Ex with source address)

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct IP_OPTION_INFORMATION
    {
        public byte Ttl, Tos, Flags, OptionsSize;
        public IntPtr OptionsData;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr icmpHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, uint sourceAddress, uint destinationAddress,
        IntPtr requestData, ushort requestSize, ref IP_OPTION_INFORMATION requestOptions, IntPtr replyBuffer, uint replySize, uint timeout);

    private const uint IpSuccess = 0, IpDestNetUnreachable = 11002, IpDestHostUnreachable = 11003, IpDestProtUnreachable = 11004,
        IpDestPortUnreachable = 11005, IpReqTimedOut = 11010, IpTtlExpiredTransit = 11013, IpTtlExpiredReassem = 11014;

    private static uint ToUInt(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);

    public Task<ProbeReply> IcmpAsync(string target, string source, int ttl, int timeoutMs, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!IPAddress.TryParse(target, out var dst) || dst.AddressFamily != AddressFamily.InterNetwork) return ProbeReply.Fail(ProbeOutcome.Error, "not an IPv4 address");
        IntPtr h = IcmpCreateFile();
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return ProbeReply.Fail(ProbeOutcome.Error, "ICMP handle unavailable");
        const int dataSize = 32;
        IntPtr data = Marshal.AllocHGlobal(dataSize);
        int replySize = 128 + dataSize + 8 + 64;
        IntPtr reply = Marshal.AllocHGlobal(replySize);
        try
        {
            for (int i = 0; i < dataSize; i++) Marshal.WriteByte(data, i, (byte)('a' + i % 23));
            var opt = new IP_OPTION_INFORMATION { Ttl = (byte)Math.Clamp(ttl, 1, 255) };
            var sw = Stopwatch.StartNew();
            uint n = IcmpSendEcho2Ex(h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ToUInt(source), ToUInt(target), data, dataSize, ref opt, reply, (uint)replySize, (uint)Math.Max(100, timeoutMs));
            double ms = sw.Elapsed.TotalMilliseconds;
            if (n == 0)
            {
                int err = Marshal.GetLastWin32Error();
                return err == IpReqTimedOut || err == 0 ? ProbeReply.Fail(ProbeOutcome.Timeout) : ProbeReply.Fail(MapIcmp((uint)err), $"ICMP error {err}");
            }
            uint from = (uint)Marshal.ReadInt32(reply, 0);
            uint status = (uint)Marshal.ReadInt32(reply, 4);
            string fromText = new IPAddress(from).ToString();
            return status switch
            {
                IpSuccess => new ProbeReply(ProbeOutcome.Ok, ms, fromText),
                IpTtlExpiredTransit or IpTtlExpiredReassem => new ProbeReply(ProbeOutcome.TtlExpired, ms, fromText),
                _ => new ProbeReply(MapIcmp(status), null, fromText, $"ICMP status {status}")
            };
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
            Marshal.FreeHGlobal(data);
            IcmpCloseHandle(h);
        }
    }, ct);

    private static ProbeOutcome MapIcmp(uint status) => status switch
    {
        IpReqTimedOut => ProbeOutcome.Timeout,
        IpDestNetUnreachable or IpDestHostUnreachable or IpDestProtUnreachable or IpDestPortUnreachable => ProbeOutcome.Unreachable,
        1231 or 1232 or 1214 => ProbeOutcome.Unreachable,          // ERROR_NETWORK_UNREACHABLE / HOST_UNREACHABLE / INVALID_NETNAME
        _ => ProbeOutcome.Error
    };

    // ------------------------------------------------------------------ TCP connect

    public async Task<ProbeReply> TcpConnectAsync(string target, int port, string source, int timeoutMs, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.LingerState = new LingerOption(true, 0);   // close with RST: no TIME_WAIT build-up from thousands of probes
            socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
        }
        catch (SocketException ex) { return ProbeReply.Fail(ProbeOutcome.Unreachable, $"source address not available ({ex.SocketErrorCode})"); }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target), port), cts.Token);
            return new ProbeReply(ProbeOutcome.Ok, sw.Elapsed.TotalMilliseconds, target);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // The host answered with a reset (path works, port closed). Windows retries the SYN before reporting it, so the
            // elapsed time is not a round trip: recorded as Refused without an RTT.
            return ProbeReply.Fail(ProbeOutcome.Refused, $"host reachable, port {port} closed (reset after {sw.Elapsed.TotalMilliseconds:0} ms incl. SYN retries)");
        }
        catch (SocketException ex)
        {
            return ProbeReply.Fail(ex.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.AddressNotAvailable or SocketError.NetworkDown
                ? ProbeOutcome.Unreachable : ex.SocketErrorCode == SocketError.TimedOut ? ProbeOutcome.Timeout : ProbeOutcome.Error, ex.SocketErrorCode.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeReply.Fail(ProbeOutcome.Timeout);
        }
    }

    // ------------------------------------------------------------------ DNS (one A query over UDP, bound)

    internal static byte[] BuildDnsQuery(ushort id, string name)
    {
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid DNS name.", nameof(name));
            q.Add((byte)bytes.Length);
            q.AddRange(bytes);
        }
        q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        return q.ToArray();
    }

    /// <summary>Returns (ok, rcode, answers) for a response to query id; null when the buffer is not a matching response.</summary>
    internal static (int RCode, int Answers)? ParseDnsResponse(byte[] r, int length, ushort id)
    {
        if (length < 12 || r[0] != (byte)(id >> 8) || r[1] != (byte)id || (r[2] & 0x80) == 0) return null;
        return (r[3] & 0x0F, (r[6] << 8) | r[7]);
    }

    public async Task<ProbeReply> DnsQueryAsync(string server, string name, string source, int timeoutMs, CancellationToken ct = default)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { udp.Bind(new IPEndPoint(IPAddress.Parse(source), 0)); }
        catch (SocketException ex) { return ProbeReply.Fail(ProbeOutcome.Unreachable, $"source address not available ({ex.SocketErrorCode})"); }
        ushort id = (ushort)Random.Shared.Next(1, 65535);
        var query = BuildDnsQuery(id, name);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var buf = new byte[1500];
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendToAsync(query, SocketFlags.None, new IPEndPoint(IPAddress.Parse(server), 53), cts.Token);
            while (true)
            {
                var res = await udp.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                if (ParseDnsResponse(buf, res.ReceivedBytes, id) is not { } parsed) continue;
                double ms = sw.Elapsed.TotalMilliseconds;
                return parsed.RCode == 0 ? new ProbeReply(ProbeOutcome.Ok, ms, server, $"{parsed.Answers} answer(s)")
                    : ProbeReply.Fail(ProbeOutcome.DnsError, parsed.RCode switch { 2 => "SERVFAIL", 3 => "NXDOMAIN", 5 => "REFUSED", var c => $"rcode {c}" });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ProbeReply.Fail(ProbeOutcome.Timeout); }
        catch (SocketException ex)
        {
            return ProbeReply.Fail(ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.NetworkUnreachable or SocketError.HostUnreachable
                ? ProbeOutcome.Unreachable : ProbeOutcome.Error, ex.SocketErrorCode.ToString());
        }
    }

    // ------------------------------------------------------------------ HTTPS (cold request: DNS + TCP + TLS + first byte)

    public async Task<ProbeReply> HttpsAsync(string url, string source, int timeoutMs, CancellationToken ct = default)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                if (addresses.Length == 0) throw new HttpRequestException("name not resolved");
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    s.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                    await s.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch { s.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WirelessScanner-ConnectionTest/1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            double ms = sw.Elapsed.TotalMilliseconds;
            return (int)resp.StatusCode < 400 ? new ProbeReply(ProbeOutcome.Ok, ms, url, $"HTTP {(int)resp.StatusCode}")
                : ProbeReply.Fail(ProbeOutcome.HttpError, $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ProbeReply.Fail(ProbeOutcome.Timeout); }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException) { return ProbeReply.Fail(ProbeOutcome.HttpError, "TLS failure"); }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException se)
        {
            return ProbeReply.Fail(se.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData ? ProbeOutcome.DnsError
                : se.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable ? ProbeOutcome.Unreachable : ProbeOutcome.Error, se.SocketErrorCode.ToString());
        }
        catch (HttpRequestException ex) { return ProbeReply.Fail(ProbeOutcome.HttpError, ex.Message); }
    }
}
