using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WirelessScanner.Responder;

/// <summary>
/// WirelessScanner responder protocol "WSR1" (DR-0014 stage C). One TCP and one UDP port (default 5410).
///
/// TCP control/throughput: server sends "WSR1" + 16-byte nonce + name length (1) + name; the client answers
/// HMAC-SHA256(key, nonce ‖ "client") (32 bytes) + command (1) + durationMs (int32 LE) + sessionId (int64 LE). A wrong MAC closes the
/// connection without a reply. Commands: Ping, Download (server streams random data for the duration), Upload (server counts and
/// discards; after the client half-closes it returns the byte count), OpenUdp (returns a session id), UdpStats (session counters).
///
/// UDP reflector: fixed 52-byte header + padding + 16-byte truncated HMAC-SHA256 over everything before it. Only packets with a valid
/// MAC for an open session are answered, and a reply is never larger than its request (no amplification).
/// Header (LE): magic "WSRU" | type (1 probe, 2 reply) | 3 reserved | session (8) | seq (4) | t1 client send µs (8) |
/// t2 server receive µs (8) | t3 server send µs (8) | server unique-received count (4) | reserved (4).
/// Clocks are not synchronised: one-way DELAY VARIATION is computed from differences of (t2 − t1) and (t4 − t3), where the constant
/// clock offset cancels; round trip = (t4 − t1) − (t3 − t2).
/// </summary>
public static class ResponderProtocol
{
    public const int DefaultPort = 5410;
    public static readonly byte[] Magic = "WSR1"u8.ToArray();
    public static readonly byte[] UdpMagic = "WSRU"u8.ToArray();
    public const int NonceLength = 16;
    public const int MacLength = 32;
    public const int UdpHeaderLength = 52;
    public const int UdpMacLength = 16;
    public const int MinUdpPacket = UdpHeaderLength + UdpMacLength;
    public const int MaxUdpPacket = 1400;
    public const int MaxDurationMs = 60_000;

    public enum Command : byte { Ping = 1, Download = 2, Upload = 3, OpenUdp = 4, UdpStats = 5 }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
    public static string KeyToText(byte[] key) => Convert.ToBase64String(key);
    public static byte[] KeyFromText(string text)
    {
        var k = Convert.FromBase64String(text.Trim());
        if (k.Length < 16) throw new FormatException("Responder key is too short.");
        return k;
    }

    public static byte[] ClientProof(byte[] key, byte[] nonce)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(nonce.Concat("client"u8.ToArray()).ToArray());
    }

    /// <summary>Microseconds on a monotonic clock (Stopwatch); only differences on the same machine are meaningful.</summary>
    public static long NowMicros() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    // ------------------------------------------------------------------ UDP packets

    public readonly record struct UdpPacket(byte Type, long Session, int Seq, long T1, long T2, long T3, int ServerCount);

    public static byte[] BuildUdp(byte[] key, UdpPacket p, int totalLength)
    {
        totalLength = Math.Clamp(totalLength, MinUdpPacket, MaxUdpPacket);
        var buf = new byte[totalLength];
        UdpMagic.CopyTo(buf, 0);
        buf[4] = p.Type;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8), p.Session);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16), p.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20), p.T1);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(28), p.T2);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(36), p.T3);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(44), p.ServerCount);
        Sign(key, buf);
        return buf;
    }

    public static void Sign(byte[] key, byte[] buf)
    {
        var mac = HMACSHA256.HashData(key, buf.AsSpan(0, buf.Length - UdpMacLength));
        mac.AsSpan(0, UdpMacLength).CopyTo(buf.AsSpan(buf.Length - UdpMacLength));
    }

    public static bool TryParseUdp(byte[] key, ReadOnlySpan<byte> buf, out UdpPacket packet)
    {
        packet = default;
        if (buf.Length < MinUdpPacket || buf.Length > MaxUdpPacket || !buf[..4].SequenceEqual(UdpMagic)) return false;
        var mac = HMACSHA256.HashData(key, buf[..^UdpMacLength]);
        if (!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, UdpMacLength), buf[^UdpMacLength..])) return false;
        packet = new UdpPacket(buf[4], BinaryPrimitives.ReadInt64LittleEndian(buf[8..]), BinaryPrimitives.ReadInt32LittleEndian(buf[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[20..]), BinaryPrimitives.ReadInt64LittleEndian(buf[28..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[36..]), BinaryPrimitives.ReadInt32LittleEndian(buf[44..]));
        return true;
    }

    // ------------------------------------------------------------------ TCP helpers

    public static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException("Connection closed by the other side.");
            read += n;
        }
    }

    public sealed class Session(long id, DateTime expiresUtc)
    {
        public long Id { get; } = id;
        public DateTime ExpiresUtc { get; } = expiresUtc;
        public readonly object Gate = new();
        public readonly HashSet<int> Seen = new();
        public int Unique, Duplicates, Reordered, RepliesSent, MaxSeq = -1;
        public long WindowStartMicros;
        public int WindowCount;
    }

    /// <summary>Client side of the TCP handshake. Returns the connected stream positioned after the command header, and the server name.</summary>
    public static async Task<(TcpClient Client, NetworkStream Stream, string ServerName)> ConnectAsync(string host, int port, string? sourceIp, byte[] key,
        Command command, int durationMs, long sessionId, CancellationToken ct)
    {
        var client = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            if (!string.IsNullOrEmpty(sourceIp)) client.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(sourceIp), 0));
            await client.ConnectAsync(host, port, ct);
            var stream = client.GetStream();
            var head = new byte[4 + NonceLength + 1];
            await ReadExactAsync(stream, head, head.Length, ct);
            if (!head.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Not a WirelessScanner responder.");
            var nonce = head.AsSpan(4, NonceLength).ToArray();
            var nameBuf = new byte[head[^1]];
            await ReadExactAsync(stream, nameBuf, nameBuf.Length, ct);
            var req = new byte[MacLength + 1 + 4 + 8];
            ClientProof(key, nonce).CopyTo(req, 0);
            req[MacLength] = (byte)command;
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(MacLength + 1), durationMs);
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(MacLength + 5), sessionId);
            await stream.WriteAsync(req, ct);
            var ok = new byte[2];
            try { await ReadExactAsync(stream, ok, 2, ct); }
            catch (EndOfStreamException) { throw new UnauthorizedAccessException("The responder rejected the key (or is busy)."); }
            if (ok[0] != (byte)'O' || ok[1] != (byte)'K') throw new InvalidDataException("Unexpected responder reply.");
            return (client, stream, Encoding.UTF8.GetString(nameBuf));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
