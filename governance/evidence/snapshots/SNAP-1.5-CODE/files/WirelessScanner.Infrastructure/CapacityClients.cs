using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Responder;

namespace WirelessScanner.Infrastructure;

/// <summary>Bytes counted after a ramp-up period, so TCP slow start does not bias throughput (DR-0014 stage B).</summary>
internal sealed class SteadyCounter
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly double _rampSeconds;
    private long _bytes;
    private double? _firstCountedAt;
    private double _lastAt;

    public SteadyCounter(double rampSeconds) => _rampSeconds = rampSeconds;

    public void Add(int n)
    {
        double t = _sw.Elapsed.TotalSeconds;
        if (t < _rampSeconds) return;
        lock (this)
        {
            _firstCountedAt ??= t;
            _bytes += n;
            _lastAt = t;
        }
    }

    public (long Bytes, double Seconds) Result()
    {
        lock (this) return (_bytes, _firstCountedAt is double f ? Math.Max(0, _lastAt - f) : 0);
    }
}

/// <summary>
/// Client for the WirelessScanner LAN responder (stage C, and stage B with a LAN server): two-way UDP stream with per-direction loss,
/// reordering and delay variation, plus multi-stream TCP throughput. All sockets are bound to the Wi-Fi address.
/// </summary>
public sealed class ResponderClient : ITwoWayClient, ISpeedTestClient
{
    public const double RampSeconds = 1.0;
    private readonly ResponderSettings _settings;
    private readonly byte[] _key;

    public ResponderClient(ResponderSettings settings)
    {
        _settings = settings;
        _key = ResponderProtocol.KeyFromText(settings.Key);
    }

    public string Responder => $"{_settings.Address}:{_settings.Port}";
    public string Provider => "LAN responder";
    public string Server => Responder;

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            var (client, _, name) = await ResponderProtocol.ConnectAsync(_settings.Address, _settings.Port, null, _key, ResponderProtocol.Command.Ping, 0, 0, cts.Token);
            client.Dispose();
            return (true, $"Connected to responder '{name}' at {Responder}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, $"No answer from {Responder} within 5 s (address, port or firewall)."); }
        catch (UnauthorizedAccessException) { return (false, "The responder rejected the key. Copy it again from the responder (--show-key)."); }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or FormatException) { return (false, $"Could not connect to {Responder}: {ex.Message}"); }
    }

    public async Task<TwoWayResult> RunStreamAsync(string source, TimeSpan duration, int packetsPerSecond, int payloadBytes, CancellationToken ct)
    {
        int durationMs = (int)Math.Clamp(duration.TotalMilliseconds, 1000, ResponderProtocol.MaxDurationMs - 5000);
        int count = Math.Max(1, (int)(durationMs / 1000.0 * packetsPerSecond));
        var basic = new TwoWayResult { Responder = Responder, Seconds = durationMs / 1000.0, PacketsPerSecond = packetsPerSecond, PayloadBytes = payloadBytes };
        long session;
        int port;
        {
            var (client, stream, _) = await ResponderProtocol.ConnectAsync(_settings.Address, _settings.Port, source, _key, ResponderProtocol.Command.OpenUdp, durationMs, 0, ct);
            using (client)
            {
                var reply = new byte[12];
                await ResponderProtocol.ReadExactAsync(stream, reply, 12, ct);
                session = BinaryPrimitives.ReadInt64LittleEndian(reply);
                port = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8));
            }
        }

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
        var remote = new IPEndPoint((await Dns.GetHostAddressesAsync(_settings.Address, AddressFamily.InterNetwork, ct)).First(), port);
        udp.Connect(remote);
        var received = new Dictionary<int, (long T1, long T2, long T3, long T4)>();
        int clientDuplicates = 0, downReordered = 0, maxSeqSeen = -1;
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiver = Task.Run(async () =>
        {
            var buf = new byte[ResponderProtocol.MaxUdpPacket];
            while (!recvCts.IsCancellationRequested)
            {
                int n;
                try { n = await udp.ReceiveAsync(buf, SocketFlags.None, recvCts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                long t4 = ResponderProtocol.NowMicros();
                if (!ResponderProtocol.TryParseUdp(_key, buf.AsSpan(0, n), out var p) || p.Type != 2 || p.Session != session) continue;
                lock (received)
                {
                    if (received.ContainsKey(p.Seq)) { clientDuplicates++; continue; }
                    if (p.Seq < maxSeqSeen) downReordered++;
                    maxSeqSeen = Math.Max(maxSeqSeen, p.Seq);
                    received[p.Seq] = (p.T1, p.T2, p.T3, t4);
                }
            }
        }, recvCts.Token);

        int size = Math.Clamp(payloadBytes, ResponderProtocol.MinUdpPacket, ResponderProtocol.MaxUdpPacket);
        var start = Stopwatch.StartNew();
        double intervalMs = 1000.0 / packetsPerSecond;
        for (int seq = 0; seq < count && !ct.IsCancellationRequested; seq++)
        {
            double due = seq * intervalMs;
            double wait = due - start.Elapsed.TotalMilliseconds;
            if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
            var pkt = ResponderProtocol.BuildUdp(_key, new ResponderProtocol.UdpPacket(1, session, seq, ResponderProtocol.NowMicros(), 0, 0, 0), size);
            try { await udp.SendAsync(pkt, SocketFlags.None, ct); } catch (SocketException) { }
        }
        await Task.Delay(1000, ct);                    // late replies
        recvCts.Cancel();
        try { await receiver; } catch (OperationCanceledException) { }

        int unique = 0, dup = 0, upReordered = 0, replies = 0;
        {
            var (client, stream, _) = await ResponderProtocol.ConnectAsync(_settings.Address, _settings.Port, source, _key, ResponderProtocol.Command.UdpStats, 0, session, ct);
            using (client)
            {
                var r = new byte[20];
                await ResponderProtocol.ReadExactAsync(stream, r, 20, ct);
                unique = BinaryPrimitives.ReadInt32LittleEndian(r);
                dup = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(4));
                upReordered = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(8));
                replies = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(12));
            }
        }
        return Compute(basic, count, unique, dup, upReordered, replies, received, clientDuplicates, downReordered);
    }

    /// <summary>
    /// Per-direction metrics from the four timestamps (pure; unit-tested). Delay variation per direction = mean |Δ(one-way delay)| between
    /// consecutive packets; the unknown clock offset cancels. Loss up = 1 − unique received by the responder ÷ sent; loss down =
    /// 1 − replies received ÷ replies sent. MOS per direction: simplified E-model with one-way delay ≈ median RTT ÷ 2 (estimate).
    /// </summary>
    internal static TwoWayResult Compute(TwoWayResult basic, int sent, int unique, int dup, int upReordered, int replies,
        IReadOnlyDictionary<int, (long T1, long T2, long T3, long T4)> received, int clientDuplicates, int downReordered)
    {
        var ordered = received.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var rtt = ordered.Select(t => ((t.T4 - t.T1) - (t.T3 - t.T2)) / 1000.0).Where(v => v >= 0).OrderBy(v => v).ToList();
        static double? MeanAbsDelta(IReadOnlyList<long> d)
        {
            if (d.Count < 2) return null;
            double s = 0;
            for (int i = 1; i < d.Count; i++) s += Math.Abs(d[i] - d[i - 1]);
            return Math.Round(s / (d.Count - 1) / 1000.0, 3);
        }
        double? upJ = MeanAbsDelta(ordered.Select(t => t.T2 - t.T1).ToList());
        double? downJ = MeanAbsDelta(ordered.Select(t => t.T4 - t.T3).ToList());
        double? upLoss = sent > 0 ? Math.Round(100.0 * Math.Max(0, sent - unique) / sent, 3) : null;
        double? downLoss = replies > 0 ? Math.Round(100.0 * Math.Max(0, replies - (received.Count + clientDuplicates)) / replies, 3) : null;
        double? p50 = ConnectionAnalyzer.Percentile(rtt, 50);
        double? Mos(double? loss, double? jitter)
        {
            if (p50 is not double r || loss is not double l || jitter is not double j) return null;
            double eff = r / 2 + 2 * j + 10;
            double rf = eff < 160 ? 93.2 - eff / 40 : 93.2 - (eff - 120) / 10;
            rf = Math.Clamp(rf - 2.5 * l, 0, 100);
            return Math.Round(Math.Clamp(1 + 0.035 * rf + 0.000007 * rf * (rf - 60) * (100 - rf), 1, 4.5), 2);
        }
        return basic with
        {
            Sent = sent,
            ReceivedByResponder = unique,
            RepliesSent = replies,
            ReceivedBack = received.Count,
            UpLossPct = upLoss,
            DownLossPct = downLoss,
            RttP50Ms = p50,
            RttP95Ms = ConnectionAnalyzer.Percentile(rtt, 95),
            UpJitterMs = upJ,
            DownJitterMs = downJ,
            UpReordered = upReordered,
            DownReordered = downReordered,
            Duplicates = dup + clientDuplicates,
            MosUp = Mos(upLoss, upJ),
            MosDown = Mos(downLoss, downJ)
        };
    }

    public async Task<(long Bytes, double Seconds, string? Error)> DownloadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    {
        var counter = new SteadyCounter(RampSeconds);
        int ms = (int)Math.Clamp(duration.TotalMilliseconds + RampSeconds * 1000, 1000, ResponderProtocol.MaxDurationMs);
        string? error = null;
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            try
            {
                var (client, stream, _) = await ResponderProtocol.ConnectAsync(_settings.Address, _settings.Port, source, _key, ResponderProtocol.Command.Download, ms, 0, ct);
                using (client)
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct)) > 0) counter.Add(n);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidDataException) { error ??= ex.Message; }
        }));
        var (bytes, secs) = counter.Result();
        return (bytes, secs, bytes == 0 ? error ?? "no data received" : null);
    }

    public async Task<(long Bytes, double Seconds, string? Error)> UploadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    {
        var counter = new SteadyCounter(RampSeconds);
        int ms = (int)Math.Clamp(duration.TotalMilliseconds + RampSeconds * 1000, 1000, ResponderProtocol.MaxDurationMs);
        string? error = null;
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            try
            {
                var (client, stream, _) = await ResponderProtocol.ConnectAsync(_settings.Address, _settings.Port, source, _key, ResponderProtocol.Command.Upload, ms, 0, ct);
                using (client)
                {
                    var chunk = RandomNumberGenerator.GetBytes(64 * 1024);
                    var end = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
                    {
                        await stream.WriteAsync(chunk, ct);
                        counter.Add(chunk.Length);
                    }
                    client.Client.Shutdown(SocketShutdown.Send);
                    var total = new byte[8];
                    await ResponderProtocol.ReadExactAsync(stream, total, 8, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { error ??= ex.Message; }
        }));
        var (bytes, secs) = counter.Result();
        return (bytes, secs, bytes == 0 ? error ?? "no data sent" : null);
    }
}

/// <summary>
/// Stage B against a custom HTTP server the owner controls: GET a large file (download) and POST a stream (upload) for a fixed time.
/// No public provider is preset (DR-0014: providers that publish results must be chosen explicitly by the owner).
/// </summary>
public sealed class HttpSpeedClient : ISpeedTestClient
{
    private readonly SpeedServerSettings _settings;
    public HttpSpeedClient(SpeedServerSettings settings) => _settings = settings;
    public string Provider => "Custom HTTP server";
    public string Server => _settings.HttpDownloadUrl;

    private static HttpClient Client(string source) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                await s.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch { s.Dispose(); throw; }
        }
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<(long Bytes, double Seconds, string? Error)> DownloadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.HttpDownloadUrl)) return (0, 0, "no download URL configured");
        var counter = new SteadyCounter(ResponderClient.RampSeconds);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration + TimeSpan.FromSeconds(ResponderClient.RampSeconds));
        string? error = null;
        using var http = Client(source);
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var resp = await http.GetAsync(_settings.HttpDownloadUrl, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(stop.Token);
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await s.ReadAsync(buf, stop.Token)) > 0) counter.Add(n);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { error ??= ex.Message; }
        }));
        var (bytes, secs) = counter.Result();
        return (bytes, secs, bytes == 0 ? error ?? "no data received" : null);
    }

    private sealed class TimedStream(TimeSpan duration, SteadyCounter counter, CancellationToken ct) : Stream
    {
        private readonly byte[] _chunk = RandomNumberGenerator.GetBytes(64 * 1024);
        private readonly DateTime _end = DateTime.UtcNow + duration;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (DateTime.UtcNow >= _end || ct.IsCancellationRequested) return 0;
            int n = Math.Min(count, _chunk.Length);
            Buffer.BlockCopy(_chunk, 0, buffer, offset, n);
            counter.Add(n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async Task<(long Bytes, double Seconds, string? Error)> UploadAsync(string source, int streams, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.HttpUploadUrl)) return (0, 0, "no upload URL configured");
        var counter = new SteadyCounter(ResponderClient.RampSeconds);
        var total = duration + TimeSpan.FromSeconds(ResponderClient.RampSeconds);
        string? error = null;
        using var http = Client(source);
        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            try
            {
                using var content = new StreamContent(new TimedStream(total, counter, ct), 64 * 1024);
                using var resp = await http.PostAsync(_settings.HttpUploadUrl, content, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { error ??= ex.Message; }
        }));
        var (bytes, secs) = counter.Result();
        return (bytes, secs, bytes == 0 ? error ?? "no data sent" : null);
    }
}

/// <summary>Protects small secrets (the responder key) with Windows DPAPI for the current user. Falls back to plain text off Windows.</summary>
public static class SecretProtector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || !OperatingSystem.IsWindows()) return plain;
        var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
        var r = Transform(bytes, protect: true);
        return r == null ? plain : Prefix + Convert.ToBase64String(r);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var r = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
        return r == null ? "" : System.Text.Encoding.UTF8.GetString(r);
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DATA_BLOB();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, "WirelessScanner", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob);
            if (!ok) return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
