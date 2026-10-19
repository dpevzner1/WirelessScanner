package com.wirelessscanner.core.responder

import com.wirelessscanner.core.analytics.ConnectionAnalyzer
import com.wirelessscanner.core.analytics.roundTo
import com.wirelessscanner.core.domain.*
import kotlinx.coroutines.*
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.net.*
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Duration
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Semaphore
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.math.abs

/**
 * Port of WirelessScanner.Responder.Core/ResponderProtocol.cs — protocol "WSR1" (DR-0014 stage C), byte-compatible with C#.
 * TCP: server sends "WSR1" + nonce(16) + nameLen(1) + name; client answers HMAC-SHA256(key, nonce ‖ "client") + cmd(1) +
 * durationMs(int32 LE) + sessionId(int64 LE); server replies "OK". UDP: 52-byte header + padding + 16-byte truncated HMAC.
 */
object ResponderProtocol {
    const val DEFAULT_PORT = 5410
    val MAGIC = "WSR1".toByteArray(Charsets.US_ASCII)
    val UDP_MAGIC = "WSRU".toByteArray(Charsets.US_ASCII)
    const val NONCE_LENGTH = 16
    const val MAC_LENGTH = 32
    const val UDP_HEADER_LENGTH = 52
    const val UDP_MAC_LENGTH = 16
    const val MIN_UDP_PACKET = UDP_HEADER_LENGTH + UDP_MAC_LENGTH
    const val MAX_UDP_PACKET = 1400
    const val MAX_DURATION_MS = 60_000

    enum class Command(val code: Byte) { Ping(1), Download(2), Upload(3), OpenUdp(4), UdpStats(5);
        companion object { fun of(b: Byte) = entries.firstOrNull { it.code == b } } }

    private val rng = SecureRandom()
    fun randomBytes(n: Int) = ByteArray(n).also { rng.nextBytes(it) }
    fun newKey() = randomBytes(32)
    fun keyToText(key: ByteArray): String = Base64.getEncoder().encodeToString(key)
    fun keyFromText(text: String): ByteArray {
        val k = try { Base64.getDecoder().decode(text.trim()) } catch (e: IllegalArgumentException) { throw IllegalArgumentException("Responder key is not valid Base64.") }
        require(k.size >= 16) { "Responder key is too short." }
        return k
    }

    fun hmac(key: ByteArray, data: ByteArray, len: Int = data.size): ByteArray {
        val m = Mac.getInstance("HmacSHA256"); m.init(SecretKeySpec(key, "HmacSHA256")); m.update(data, 0, len); return m.doFinal()
    }

    fun clientProof(key: ByteArray, nonce: ByteArray) = hmac(key, nonce + "client".toByteArray(Charsets.US_ASCII))

    /** Microseconds on a monotonic clock; only differences on the same machine are meaningful. */
    fun nowMicros() = System.nanoTime() / 1000

    data class UdpPacket(val type: Byte, val session: Long, val seq: Int, val t1: Long, val t2: Long, val t3: Long, val serverCount: Int)

    fun buildUdp(key: ByteArray, p: UdpPacket, totalLength: Int): ByteArray {
        val len = totalLength.coerceIn(MIN_UDP_PACKET, MAX_UDP_PACKET)
        val buf = ByteArray(len)
        val bb = ByteBuffer.wrap(buf).order(ByteOrder.LITTLE_ENDIAN)
        UDP_MAGIC.copyInto(buf, 0)
        buf[4] = p.type
        bb.putLong(8, p.session); bb.putInt(16, p.seq); bb.putLong(20, p.t1); bb.putLong(28, p.t2); bb.putLong(36, p.t3); bb.putInt(44, p.serverCount)
        sign(key, buf)
        return buf
    }

    fun sign(key: ByteArray, buf: ByteArray) {
        val mac = hmac(key, buf, buf.size - UDP_MAC_LENGTH)
        mac.copyInto(buf, buf.size - UDP_MAC_LENGTH, 0, UDP_MAC_LENGTH)
    }

    fun parseUdp(key: ByteArray, buf: ByteArray, length: Int = buf.size): UdpPacket? {
        if (length < MIN_UDP_PACKET || length > MAX_UDP_PACKET) return null
        for (i in 0 until 4) if (buf[i] != UDP_MAGIC[i]) return null
        val mac = hmac(key, buf, length - UDP_MAC_LENGTH).copyOf(UDP_MAC_LENGTH)
        if (!MessageDigest.isEqual(mac, buf.copyOfRange(length - UDP_MAC_LENGTH, length))) return null
        val bb = ByteBuffer.wrap(buf).order(ByteOrder.LITTLE_ENDIAN)
        return UdpPacket(buf[4], bb.getLong(8), bb.getInt(16), bb.getLong(20), bb.getLong(28), bb.getLong(36), bb.getInt(44))
    }

    fun readExact(s: InputStream, buf: ByteArray, count: Int) {
        var read = 0
        while (read < count) {
            val n = s.read(buf, read, count - read)
            if (n <= 0) throw EOFException("Connection closed by the other side.")
            read += n
        }
    }

    fun le32(v: Int) = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(v).array()
    fun le64(v: Long) = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(v).array()
    fun rd32(b: ByteArray, off: Int) = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN).getInt(off)
    fun rd64(b: ByteArray, off: Int) = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN).getLong(off)

    class Session(val id: Long, val expiresAtMs: Long) {
        val seen = HashSet<Int>()
        var unique = 0; var duplicates = 0; var reordered = 0; var repliesSent = 0; var maxSeq = -1
        var windowStartMicros = 0L; var windowCount = 0
    }

    class AuthException(msg: String) : IOException(msg)

    /** Client side of the TCP handshake. Returns the connected socket positioned after "OK", and the server name. */
    fun connect(binder: NetBinder, host: String, port: Int, key: ByteArray, command: Command, durationMs: Int, sessionId: Long, timeoutMs: Int = 10_000): Pair<Socket, String> {
        val s = binder.newSocket()
        try {
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(binder.resolve(host), port), timeoutMs)
            s.soTimeout = maxOf(timeoutMs, durationMs + 10_000)
            val input = s.getInputStream()
            val head = ByteArray(4 + NONCE_LENGTH + 1)
            readExact(input, head, head.size)
            if (!head.copyOf(4).contentEquals(MAGIC)) throw IOException("Not a WirelessScanner responder.")
            val nonce = head.copyOfRange(4, 4 + NONCE_LENGTH)
            val nameBuf = ByteArray(head.last().toInt() and 0xFF)
            readExact(input, nameBuf, nameBuf.size)
            val req = clientProof(key, nonce) + byteArrayOf(command.code) + le32(durationMs) + le64(sessionId)
            s.getOutputStream().write(req); s.getOutputStream().flush()
            val ok = ByteArray(2)
            try { readExact(input, ok, 2) } catch (e: EOFException) { throw AuthException("The responder rejected the key (or is busy).") }
            if (ok[0] != 'O'.code.toByte() || ok[1] != 'K'.code.toByte()) throw IOException("Unexpected responder reply.")
            return s to String(nameBuf, Charsets.UTF_8)
        } catch (e: Exception) { s.close(); throw e }
    }
}

/**
 * How sockets reach the network. Android binds every socket to the Wi-Fi Network (Network.bindSocket / getAllByName), so probes
 * and tests never leave over mobile data or Ethernet; the default binds to a local address.
 */
interface NetBinder {
    fun newSocket(): Socket
    fun newDatagramSocket(): DatagramSocket
    fun resolve(host: String): InetAddress
    fun openHttp(url: URL): HttpURLConnection

    class Local(private val sourceIp: String?) : NetBinder {
        override fun newSocket() = Socket().also { s -> sourceIp?.let { s.bind(InetSocketAddress(InetAddress.getByName(it), 0)) } }
        override fun newDatagramSocket() = DatagramSocket(if (sourceIp != null) InetSocketAddress(InetAddress.getByName(sourceIp), 0) else InetSocketAddress(0))
        override fun resolve(host: String): InetAddress = InetAddress.getAllByName(host).first { it is Inet4Address }
        override fun openHttp(url: URL) = url.openConnection() as HttpURLConnection
    }
}

/** Bytes counted after a ramp-up period so TCP slow start does not bias throughput. */
class SteadyCounter(private val rampSeconds: Double) {
    private val t0 = System.nanoTime()
    private var bytes = 0L
    private var firstAt: Double? = null
    private var lastAt = 0.0
    @Synchronized fun add(n: Int) {
        val t = (System.nanoTime() - t0) / 1e9
        if (t < rampSeconds) return
        if (firstAt == null) firstAt = t
        bytes += n; lastAt = t
    }
    @Synchronized fun result(): Pair<Long, Double> = bytes to (firstAt?.let { maxOf(0.0, lastAt - it) } ?: 0.0)
}

/** Port of ResponderServer.cs: authenticated TCP throughput endpoints and an authenticated UDP reflector, same safety limits. */
class ResponderServer(private val key: ByteArray, port: Int = ResponderProtocol.DEFAULT_PORT, private val bind: InetAddress? = null,
                      name: String = "WirelessScanner-Android", private val log: (String) -> Unit = {}) {
    private val name = name.take(60)
    private val sessions = ConcurrentHashMap<Long, ResponderProtocol.Session>()
    private val tcpSlots = Semaphore(MAX_TCP_CONNECTIONS)
    private var tcp: ServerSocket? = null
    private var udp: DatagramSocket? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    var port = port
        private set
    val isRunning get() = tcp?.isClosed == false

    init { require(key.size >= 16) { "Key too short." } }

    fun start() {
        val t = ServerSocket(); t.reuseAddress = true; t.bind(InetSocketAddress(bind ?: InetAddress.getByName("0.0.0.0"), port)); tcp = t
        if (port == 0) port = t.localPort
        udp = DatagramSocket(InetSocketAddress(bind ?: InetAddress.getByName("0.0.0.0"), port))
        scope.launch { acceptLoop() }
        scope.launch { udpLoop() }
        log("Responder '$name' listening on TCP/UDP ${bind?.hostAddress ?: "0.0.0.0"}:$port")
    }

    private fun acceptLoop() {
        val t = tcp ?: return
        while (!t.isClosed) {
            val client = try { t.accept() } catch (e: IOException) { if (t.isClosed) break else { log("Accept failed: ${e.message}"); continue } }
            if (!tcpSlots.tryAcquire()) { client.close(); continue }
            scope.launch {
                try { handle(client) }
                catch (e: IOException) {}
                catch (e: Exception) { log("Connection error: ${e.message}") }
                finally { try { client.close() } catch (_: IOException) {}; tcpSlots.release() }
            }
        }
    }

    private fun handle(client: Socket) {
        client.tcpNoDelay = true
        client.soTimeout = 10_000
        val input = client.getInputStream(); val out = client.getOutputStream()
        val nonce = ResponderProtocol.randomBytes(ResponderProtocol.NONCE_LENGTH)
        val nameBytes = name.toByteArray(Charsets.UTF_8)
        out.write(ResponderProtocol.MAGIC + nonce + byteArrayOf(nameBytes.size.toByte()) + nameBytes); out.flush()
        val req = ByteArray(ResponderProtocol.MAC_LENGTH + 1 + 4 + 8)
        ResponderProtocol.readExact(input, req, req.size)
        if (!MessageDigest.isEqual(req.copyOf(ResponderProtocol.MAC_LENGTH), ResponderProtocol.clientProof(key, nonce))) {
            log("Rejected ${client.remoteSocketAddress}: wrong key."); return
        }
        val cmd = ResponderProtocol.Command.of(req[ResponderProtocol.MAC_LENGTH]) ?: return
        val durationMs = ResponderProtocol.rd32(req, ResponderProtocol.MAC_LENGTH + 1).coerceIn(0, ResponderProtocol.MAX_DURATION_MS)
        val sessionId = ResponderProtocol.rd64(req, ResponderProtocol.MAC_LENGTH + 5)
        val ok = "OK".toByteArray(Charsets.US_ASCII)
        when (cmd) {
            ResponderProtocol.Command.Ping -> out.write(ok)
            ResponderProtocol.Command.Download -> {
                out.write(ok)
                val chunk = ResponderProtocol.randomBytes(64 * 1024)
                val end = System.currentTimeMillis() + durationMs
                while (System.currentTimeMillis() < end) out.write(chunk)
            }
            ResponderProtocol.Command.Upload -> {
                out.write(ok); out.flush()
                val buf = ByteArray(64 * 1024)
                var total = 0L
                client.soTimeout = durationMs + 5000
                try { while (true) { val n = input.read(buf); if (n <= 0) break; total += n } } catch (e: SocketTimeoutException) {}
                out.write(ResponderProtocol.le64(total))
            }
            ResponderProtocol.Command.OpenUdp -> {
                val now = System.currentTimeMillis()
                sessions.values.filter { it.expiresAtMs < now }.forEach { sessions.remove(it.id) }
                if (sessions.size >= MAX_SESSIONS) return
                val id = ResponderProtocol.rd64(ResponderProtocol.randomBytes(8), 0)
                sessions[id] = ResponderProtocol.Session(id, now + durationMs + 30_000)
                out.write(ok + ResponderProtocol.le64(id) + ResponderProtocol.le32(port))
            }
            ResponderProtocol.Command.UdpStats -> {
                val s = sessions[sessionId] ?: return
                val reply = synchronized(s) { ok + ResponderProtocol.le32(s.unique) + ResponderProtocol.le32(s.duplicates) + ResponderProtocol.le32(s.reordered) + ResponderProtocol.le32(s.repliesSent) + ResponderProtocol.le32(s.maxSeq) }
                out.write(reply)
                sessions.remove(sessionId)
            }
        }
        out.flush()
    }

    private fun udpLoop() {
        val u = udp ?: return
        val buf = ByteArray(ResponderProtocol.MAX_UDP_PACKET + 1)
        while (!u.isClosed) {
            val dp = DatagramPacket(buf, buf.size)
            try { u.receive(dp) } catch (e: IOException) { if (u.isClosed) break else continue }
            val t2 = ResponderProtocol.nowMicros()
            val p = ResponderProtocol.parseUdp(key, buf, dp.length) ?: continue
            if (p.type.toInt() != 1) continue
            val s = sessions[p.session] ?: continue
            if (s.expiresAtMs < System.currentTimeMillis()) continue
            val count = synchronized(s) {
                if (t2 - s.windowStartMicros > 1_000_000) { s.windowStartMicros = t2; s.windowCount = 0 }
                if (++s.windowCount > MAX_PACKETS_PER_SECOND) return@synchronized null
                if (!s.seen.add(p.seq)) s.duplicates++ else { s.unique++; if (p.seq < s.maxSeq) s.reordered++; s.maxSeq = maxOf(s.maxSeq, p.seq) }
                s.repliesSent++
                s.unique
            } ?: continue
            val reply = ResponderProtocol.buildUdp(key, p.copy(type = 2, t2 = t2, t3 = ResponderProtocol.nowMicros(), serverCount = count), dp.length)
            try { u.send(DatagramPacket(reply, reply.size, dp.socketAddress)) } catch (e: IOException) {}
        }
    }

    fun stop() {
        try { tcp?.close() } catch (_: IOException) {}
        udp?.close()
        scope.cancel()
    }

    companion object {
        const val MAX_TCP_CONNECTIONS = 16
        const val MAX_SESSIONS = 8
        const val MAX_PACKETS_PER_SECOND = 200
    }
}

/** Port of ResponderClient (CapacityClients.cs): two-way UDP stream + multi-stream TCP throughput, bound through the NetBinder. */
class ResponderClient(private val settings: ResponderSettings, private val binder: NetBinder) : TwoWayClient, SpeedTestClient {
    private val key = ResponderProtocol.keyFromText(settings.key)
    override val responder get() = "${settings.address}:${settings.port}"
    override val provider get() = "LAN responder"
    override val server get() = responder

    override suspend fun check(): Pair<Boolean, String> = withContext(Dispatchers.IO) {
        try {
            val (s, name) = ResponderProtocol.connect(binder, settings.address, settings.port, key, ResponderProtocol.Command.Ping, 0, 0, 5000)
            s.close()
            true to "Connected to responder '$name' at $responder."
        } catch (e: SocketTimeoutException) { false to "No answer from $responder within 5 s (address, port or firewall)." }
        catch (e: ResponderProtocol.AuthException) { false to "The responder rejected the key. Copy it again from the responder (--show-key)." }
        catch (e: Exception) { false to "Could not connect to $responder: ${e.message}" }
    }

    override suspend fun runStream(source: String, duration: Duration, packetsPerSecond: Int, payloadBytes: Int): TwoWayResult = withContext(Dispatchers.IO) {
        val durationMs = duration.toMillis().coerceIn(1000, (ResponderProtocol.MAX_DURATION_MS - 5000).toLong()).toInt()
        val count = maxOf(1, (durationMs / 1000.0 * packetsPerSecond).toInt())
        val basic = TwoWayResult(responder = responder, seconds = durationMs / 1000.0, packetsPerSecond = packetsPerSecond, payloadBytes = payloadBytes)
        val (session, port) = ResponderProtocol.connect(binder, settings.address, settings.port, key, ResponderProtocol.Command.OpenUdp, durationMs, 0).let { (s, _) ->
            s.use { val r = ByteArray(12); ResponderProtocol.readExact(it.getInputStream(), r, 12); ResponderProtocol.rd64(r, 0) to ResponderProtocol.rd32(r, 8) }
        }
        val udp = binder.newDatagramSocket()
        val received = HashMap<Int, LongArray>()
        var clientDuplicates = 0; var downReordered = 0; var maxSeqSeen = -1
        try {
            udp.connect(InetSocketAddress(binder.resolve(settings.address), port))
            udp.soTimeout = 200
            val receiver = launch {
                val buf = ByteArray(ResponderProtocol.MAX_UDP_PACKET)
                while (isActive) {
                    val dp = DatagramPacket(buf, buf.size)
                    try { udp.receive(dp) } catch (e: SocketTimeoutException) { continue } catch (e: IOException) { if (udp.isClosed) break else continue }
                    val t4 = ResponderProtocol.nowMicros()
                    val p = ResponderProtocol.parseUdp(key, buf, dp.length) ?: continue
                    if (p.type.toInt() != 2 || p.session != session) continue
                    synchronized(received) {
                        if (received.containsKey(p.seq)) { clientDuplicates++; return@synchronized }
                        if (p.seq < maxSeqSeen) downReordered++
                        maxSeqSeen = maxOf(maxSeqSeen, p.seq)
                        received[p.seq] = longArrayOf(p.t1, p.t2, p.t3, t4)
                    }
                }
            }
            val size = payloadBytes.coerceIn(ResponderProtocol.MIN_UDP_PACKET, ResponderProtocol.MAX_UDP_PACKET)
            val t0 = System.nanoTime()
            val intervalMs = 1000.0 / packetsPerSecond
            for (seq in 0 until count) {
                ensureActive()
                val wait = seq * intervalMs - (System.nanoTime() - t0) / 1e6
                if (wait > 1) delay(wait.toLong())
                val pkt = ResponderProtocol.buildUdp(key, ResponderProtocol.UdpPacket(1, session, seq, ResponderProtocol.nowMicros(), 0, 0, 0), size)
                try { udp.send(DatagramPacket(pkt, pkt.size)) } catch (e: IOException) {}
            }
            delay(1000)
            receiver.cancelAndJoin()
        } finally { udp.close() }
        val stats = ResponderProtocol.connect(binder, settings.address, settings.port, key, ResponderProtocol.Command.UdpStats, 0, session).let { (s, _) ->
            s.use { val r = ByteArray(20); ResponderProtocol.readExact(it.getInputStream(), r, 20); r }
        }
        compute(basic, count, ResponderProtocol.rd32(stats, 0), ResponderProtocol.rd32(stats, 4), ResponderProtocol.rd32(stats, 8), ResponderProtocol.rd32(stats, 12),
            synchronized(received) { HashMap(received) }, clientDuplicates, downReordered)
    }

    override suspend fun download(source: String, streams: Int, duration: Duration): TransferResult = withContext(Dispatchers.IO) {
        val counter = SteadyCounter(RAMP_SECONDS)
        val ms = (duration.toMillis() + RAMP_SECONDS * 1000).toInt().coerceIn(1000, ResponderProtocol.MAX_DURATION_MS)
        var error: String? = null
        (0 until streams).map {
            async {
                try {
                    val (s, _) = ResponderProtocol.connect(binder, settings.address, settings.port, key, ResponderProtocol.Command.Download, ms, 0)
                    s.use { val buf = ByteArray(64 * 1024); val inp = it.getInputStream(); while (true) { val n = inp.read(buf); if (n <= 0) break; counter.add(n) } }
                } catch (e: IOException) { if (error == null) error = e.message }
            }
        }.awaitAll()
        val (bytes, secs) = counter.result()
        TransferResult(bytes, secs, if (bytes == 0L) error ?: "no data received" else null)
    }

    override suspend fun upload(source: String, streams: Int, duration: Duration): TransferResult = withContext(Dispatchers.IO) {
        val counter = SteadyCounter(RAMP_SECONDS)
        val ms = (duration.toMillis() + RAMP_SECONDS * 1000).toInt().coerceIn(1000, ResponderProtocol.MAX_DURATION_MS)
        var error: String? = null
        (0 until streams).map {
            async {
                try {
                    val (s, _) = ResponderProtocol.connect(binder, settings.address, settings.port, key, ResponderProtocol.Command.Upload, ms, 0)
                    s.use {
                        val chunk = ResponderProtocol.randomBytes(64 * 1024)
                        val end = System.currentTimeMillis() + ms
                        val out = it.getOutputStream()
                        while (System.currentTimeMillis() < end && isActive) { out.write(chunk); counter.add(chunk.size) }
                        out.flush(); it.shutdownOutput()
                        ResponderProtocol.readExact(it.getInputStream(), ByteArray(8), 8)
                    }
                } catch (e: IOException) { if (error == null) error = e.message }
            }
        }.awaitAll()
        val (bytes, secs) = counter.result()
        TransferResult(bytes, secs, if (bytes == 0L) error ?: "no data sent" else null)
    }

    companion object {
        const val RAMP_SECONDS = 1.0

        /** Per-direction metrics from the four timestamps (pure; same definitions as C# Compute). */
        fun compute(basic: TwoWayResult, sent: Int, unique: Int, dup: Int, upReordered: Int, replies: Int, received: Map<Int, LongArray>,
                    clientDuplicates: Int, downReordered: Int): TwoWayResult {
            val ordered = received.entries.sortedBy { it.key }.map { it.value }
            val rtt = ordered.map { ((it[3] - it[0]) - (it[2] - it[1])) / 1000.0 }.filter { it >= 0 }.sorted()
            fun meanAbsDelta(d: List<Long>): Double? {
                if (d.size < 2) return null
                var s = 0.0
                for (i in 1 until d.size) s += abs(d[i] - d[i - 1])
                return (s / (d.size - 1) / 1000.0).roundTo(3)
            }
            val upJ = meanAbsDelta(ordered.map { it[1] - it[0] })
            val downJ = meanAbsDelta(ordered.map { it[3] - it[2] })
            val upLoss = if (sent > 0) (100.0 * maxOf(0, sent - unique) / sent).roundTo(3) else null
            val downLoss = if (replies > 0) (100.0 * maxOf(0, replies - (received.size + clientDuplicates)) / replies).roundTo(3) else null
            val p50 = ConnectionAnalyzer.percentile(rtt, 50.0)
            fun mos(loss: Double?, jitter: Double?): Double? {
                if (p50 == null || loss == null || jitter == null) return null
                val eff = p50 / 2 + 2 * jitter + 10
                var rf = if (eff < 160) 93.2 - eff / 40 else 93.2 - (eff - 120) / 10
                rf = (rf - 2.5 * loss).coerceIn(0.0, 100.0)
                return (1 + 0.035 * rf + 0.000007 * rf * (rf - 60) * (100 - rf)).coerceIn(1.0, 4.5).roundTo(2)
            }
            return basic.copy(sent = sent, receivedByResponder = unique, repliesSent = replies, receivedBack = received.size,
                upLossPct = upLoss, downLossPct = downLoss, rttP50Ms = p50, rttP95Ms = ConnectionAnalyzer.percentile(rtt, 95.0),
                upJitterMs = upJ, downJitterMs = downJ, upReordered = upReordered, downReordered = downReordered,
                duplicates = dup + clientDuplicates, mosUp = mos(upLoss, upJ), mosDown = mos(downLoss, downJ))
        }
    }
}

/** Port of HttpSpeedClient: stage B against a custom HTTP server the owner controls (no public provider preset, DR-0014). */
class HttpSpeedClient(private val settings: SpeedServerSettings, private val binder: NetBinder) : SpeedTestClient {
    override val provider get() = "Custom HTTP server"
    override val server get() = settings.httpDownloadUrl

    override suspend fun download(source: String, streams: Int, duration: Duration): TransferResult = withContext(Dispatchers.IO) {
        if (settings.httpDownloadUrl.isBlank()) return@withContext TransferResult(0, 0.0, "no download URL configured")
        val counter = SteadyCounter(ResponderClient.RAMP_SECONDS)
        val end = System.currentTimeMillis() + duration.toMillis() + (ResponderClient.RAMP_SECONDS * 1000).toLong()
        var error: String? = null
        (0 until streams).map {
            async {
                try {
                    while (System.currentTimeMillis() < end) {
                        val c = binder.openHttp(URL(settings.httpDownloadUrl))
                        c.connectTimeout = 5000; c.readTimeout = 5000
                        try {
                            if (c.responseCode !in 200..299) throw IOException("HTTP ${c.responseCode}")
                            val inp = c.inputStream; val buf = ByteArray(64 * 1024)
                            while (System.currentTimeMillis() < end) { val n = inp.read(buf); if (n <= 0) break; counter.add(n) }
                        } finally { c.disconnect() }
                    }
                } catch (e: IOException) { if (error == null) error = e.message }
            }
        }.awaitAll()
        val (bytes, secs) = counter.result()
        TransferResult(bytes, secs, if (bytes == 0L) error ?: "no data received" else null)
    }

    override suspend fun upload(source: String, streams: Int, duration: Duration): TransferResult = withContext(Dispatchers.IO) {
        if (settings.httpUploadUrl.isBlank()) return@withContext TransferResult(0, 0.0, "no upload URL configured")
        val counter = SteadyCounter(ResponderClient.RAMP_SECONDS)
        val end = System.currentTimeMillis() + duration.toMillis() + (ResponderClient.RAMP_SECONDS * 1000).toLong()
        var error: String? = null
        (0 until streams).map {
            async {
                try {
                    val c = binder.openHttp(URL(settings.httpUploadUrl))
                    c.requestMethod = "POST"; c.doOutput = true; c.setChunkedStreamingMode(64 * 1024); c.connectTimeout = 5000
                    try {
                        val chunk = ResponderProtocol.randomBytes(64 * 1024)
                        c.outputStream.use { out -> while (System.currentTimeMillis() < end) { out.write(chunk); counter.add(chunk.size) } }
                        c.responseCode
                    } finally { c.disconnect() }
                } catch (e: IOException) { if (error == null) error = e.message }
            }
        }.awaitAll()
        val (bytes, secs) = counter.result()
        TransferResult(bytes, secs, if (bytes == 0L) error ?: "no data sent" else null)
    }
}
