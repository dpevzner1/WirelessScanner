package com.wirelessscanner.core.net

import com.wirelessscanner.core.domain.ProbeOutcome
import com.wirelessscanner.core.domain.ProbeReply

/** Port of ConnectionProber.BuildDnsQuery / ParseDnsResponse (one A query over UDP). */
object DnsPacket {
    fun buildQuery(id: Int, name: String): ByteArray {
        val q = java.io.ByteArrayOutputStream()
        q.write((id shr 8) and 0xFF); q.write(id and 0xFF); q.write(0x01); q.write(0x00); q.write(0); q.write(1); repeat(6) { q.write(0) }
        for (label in name.trimEnd('.').split('.')) {
            val b = label.toByteArray(Charsets.US_ASCII)
            require(b.isNotEmpty() && b.size <= 63) { "Invalid DNS name." }
            q.write(b.size); q.write(b)
        }
        q.write(0); q.write(0); q.write(1); q.write(0); q.write(1)
        return q.toByteArray()
    }

    /** (rcode, answers) for a response to [id]; null when the buffer is not a matching response. */
    fun parseResponse(r: ByteArray, length: Int, id: Int): Pair<Int, Int>? {
        if (length < 12 || (r[0].toInt() and 0xFF) != ((id shr 8) and 0xFF) || (r[1].toInt() and 0xFF) != (id and 0xFF) || (r[2].toInt() and 0x80) == 0) return null
        return (r[3].toInt() and 0x0F) to (((r[6].toInt() and 0xFF) shl 8) or (r[7].toInt() and 0xFF))
    }

    fun reply(rcode: Int, answers: Int, ms: Double, server: String): ProbeReply =
        if (rcode == 0) ProbeReply(ProbeOutcome.Ok, ms, server, "$answers answer(s)")
        else ProbeReply.fail(ProbeOutcome.DnsError, when (rcode) { 2 -> "SERVFAIL"; 3 -> "NXDOMAIN"; 5 -> "REFUSED"; else -> "rcode $rcode" })
}

/**
 * Android ICMP uses the system ping utility (no raw sockets without root). Parses one-shot output of
 * `ping -n -c 1 -W <s> -t <ttl> -I <source> <target>` (iputils / toybox formats).
 */
object PingParser {
    private val time = Regex("""time[=<]\s*([0-9]+(?:\.[0-9]+)?)\s*ms""")
    private val from = Regex("""[Ff]rom\s+([0-9]{1,3}(?:\.[0-9]{1,3}){3})""")

    fun parse(output: String, exitCode: Int, target: String): ProbeReply {
        val lower = output.lowercase()
        time.find(output)?.let { m ->
            val ip = from.find(output)?.groupValues?.get(1) ?: target
            return ProbeReply(ProbeOutcome.Ok, m.groupValues[1].toDouble(), ip)
        }
        if ("time to live exceeded" in lower || "ttl exceeded" in lower) {
            val ip = from.find(output)?.groupValues?.get(1)
            return ProbeReply(ProbeOutcome.TtlExpired, null, ip, "time to live exceeded")
        }
        if ("unreachable" in lower) return ProbeReply.fail(ProbeOutcome.Unreachable, from.find(output)?.let { "from ${it.groupValues[1]}" } ?: "unreachable")
        if ("unknown host" in lower || "cannot resolve" in lower) return ProbeReply.fail(ProbeOutcome.DnsError, "unknown host")
        if ("network is unreachable" in lower || "connect: network" in lower) return ProbeReply.fail(ProbeOutcome.Unreachable, "network unreachable")
        if ("cannot assign requested address" in lower || "bind:" in lower) return ProbeReply.fail(ProbeOutcome.Unreachable, "source address not available")
        if ("permission denied" in lower || "operation not permitted" in lower) return ProbeReply.fail(ProbeOutcome.Error, "ping not permitted on this device")
        return if (exitCode == 1 || "0 received" in lower || "100% packet loss" in lower) ProbeReply.fail(ProbeOutcome.Timeout)
        else ProbeReply.fail(ProbeOutcome.Error, output.lineSequence().firstOrNull { it.isNotBlank() }?.take(120) ?: "ping exit $exitCode")
    }

    /** ping -W takes whole seconds (at least 1). */
    fun timeoutSeconds(ms: Int) = maxOf(1, (ms + 999) / 1000)
}
