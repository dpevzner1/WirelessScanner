using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WirelessScanner.Responder;

/// <summary>
/// The LAN responder (DR-0014 stage C): authenticated TCP throughput endpoints and an authenticated UDP reflector.
/// Safety limits: at most 16 concurrent TCP connections and 8 UDP sessions, durations capped at 60 s, 200 packets/s per session,
/// unauthenticated traffic is dropped silently, replies are never larger than requests.
/// </summary>
public sealed class ResponderServer : IAsyncDisposable
{
    public const int MaxTcpConnections = 16;
    public const int MaxSessions = 8;
    public const int MaxPacketsPerSecond = 200;

    private readonly byte[] _key;
    private readonly IPAddress _bind;
    private readonly string _name;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<long, ResponderProtocol.Session> _sessions = new();
    private readonly SemaphoreSlim _tcpSlots = new(MaxTcpConnections, MaxTcpConnections);
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _tcpLoop, _udpLoop;

    public int Port { get; private set; }

    public ResponderServer(byte[] key, int port = ResponderProtocol.DefaultPort, IPAddress? bind = null, string? name = null, Action<string>? log = null)
    {
        if (key.Length < 16) throw new ArgumentException("Key too short.", nameof(key));
        _key = key;
        Port = port;
        _bind = bind ?? IPAddress.Any;
        _name = (name ?? Environment.MachineName).Length > 60 ? (name ?? Environment.MachineName)[..60] : name ?? Environment.MachineName;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _tcp = new TcpListener(_bind, Port);
        _tcp.Start();
        if (Port == 0) Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _udp = new UdpClient(new IPEndPoint(_bind, Port));
        _tcpLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _udpLoop = Task.Run(() => UdpLoopAsync(_cts.Token));
        _log($"Responder '{_name}' listening on TCP/UDP {_bind}:{Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { _log($"Accept failed: {ex.Message}"); continue; }
            if (!await _tcpSlots.WaitAsync(0, ct)) { client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct); }
                catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException or InvalidDataException) { }
                catch (Exception ex) { _log($"Connection error: {ex.Message}"); }
                finally { client.Dispose(); _tcpSlots.Release(); }
            }, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));                      // handshake must complete quickly
        var nonce = RandomNumberGenerator.GetBytes(ResponderProtocol.NonceLength);
        var name = Encoding.UTF8.GetBytes(_name);
        await stream.WriteAsync(ResponderProtocol.Magic.Concat(nonce).Append((byte)name.Length).Concat(name).ToArray(), timeout.Token);
        var req = new byte[ResponderProtocol.MacLength + 1 + 4 + 8];
        await ResponderProtocol.ReadExactAsync(stream, req, req.Length, timeout.Token);
        if (!CryptographicOperations.FixedTimeEquals(req.AsSpan(0, ResponderProtocol.MacLength), ResponderProtocol.ClientProof(_key, nonce)))
        {
            _log($"Rejected {client.Client.RemoteEndPoint}: wrong key.");
            return;                                                         // close without a reply
        }
        var cmd = (ResponderProtocol.Command)req[ResponderProtocol.MacLength];
        int durationMs = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(ResponderProtocol.MacLength + 1)), 0, ResponderProtocol.MaxDurationMs);
        long sessionId = BinaryPrimitives.ReadInt64LittleEndian(req.AsSpan(ResponderProtocol.MacLength + 5));
        var okBytes = "OK"u8.ToArray();
        switch (cmd)
        {
            case ResponderProtocol.Command.Ping:
                await stream.WriteAsync(okBytes, ct);
                break;
            case ResponderProtocol.Command.Download:
            {
                await stream.WriteAsync(okBytes, ct);
                var chunk = RandomNumberGenerator.GetBytes(64 * 1024);
                var end = DateTime.UtcNow.AddMilliseconds(durationMs);
                while (DateTime.UtcNow < end && !ct.IsCancellationRequested) await stream.WriteAsync(chunk, ct);
                break;
            }
            case ResponderProtocol.Command.Upload:
            {
                await stream.WriteAsync(okBytes, ct);
                var buf = new byte[64 * 1024];
                long total = 0;
                using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cap.CancelAfter(durationMs + 5000);
                try
                {
                    int n;
                    while ((n = await stream.ReadAsync(buf, cap.Token)) > 0) total += n;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                var count = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(count, total);
                await stream.WriteAsync(count, ct);
                break;
            }
            case ResponderProtocol.Command.OpenUdp:
            {
                foreach (var stale in _sessions.Values.Where(s => s.ExpiresUtc < DateTime.UtcNow).ToList()) _sessions.TryRemove(stale.Id, out _);
                if (_sessions.Count >= MaxSessions) return;
                long id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
                _sessions[id] = new ResponderProtocol.Session(id, DateTime.UtcNow.AddMilliseconds(durationMs + 30_000));
                var reply = new byte[2 + 8 + 4];
                okBytes.CopyTo(reply, 0);
                BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(2), id);
                BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(10), Port);
                await stream.WriteAsync(reply, ct);
                break;
            }
            case ResponderProtocol.Command.UdpStats:
            {
                if (!_sessions.TryGetValue(sessionId, out var s)) return;
                var reply = new byte[2 + 5 * 4];
                okBytes.CopyTo(reply, 0);
                lock (s.Gate)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(2), s.Unique);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(6), s.Duplicates);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(10), s.Reordered);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(14), s.RepliesSent);
                    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(18), s.MaxSeq);
                }
                await stream.WriteAsync(reply, ct);
                _sessions.TryRemove(sessionId, out _);
                break;
            }
        }
    }

    private async Task UdpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }                          // e.g. ICMP port unreachable from a gone client
            long t2 = ResponderProtocol.NowMicros();
            if (!ResponderProtocol.TryParseUdp(_key, r.Buffer, out var p) || p.Type != 1) continue;
            if (!_sessions.TryGetValue(p.Session, out var s) || s.ExpiresUtc < DateTime.UtcNow) continue;
            int count;
            lock (s.Gate)
            {
                if (t2 - s.WindowStartMicros > 1_000_000) { s.WindowStartMicros = t2; s.WindowCount = 0; }
                if (++s.WindowCount > MaxPacketsPerSecond) continue;
                if (!s.Seen.Add(p.Seq)) s.Duplicates++;
                else
                {
                    s.Unique++;
                    if (p.Seq < s.MaxSeq) s.Reordered++;
                    s.MaxSeq = Math.Max(s.MaxSeq, p.Seq);
                }
                s.RepliesSent++;
                count = s.Unique;
            }
            var reply = ResponderProtocol.BuildUdp(_key, p with { Type = 2, T2 = t2, T3 = ResponderProtocol.NowMicros(), ServerCount = count }, r.Buffer.Length);
            try { await _udp.SendAsync(reply, r.RemoteEndPoint, ct); }
            catch (SocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
        try { if (_tcpLoop != null) await _tcpLoop; } catch { }
        try { if (_udpLoop != null) await _udpLoop; } catch { }
        _tcpSlots.Dispose();
        _cts?.Dispose();
    }
}
