package com.wirelessscanner.android.platform

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import com.wirelessscanner.core.domain.*
import com.wirelessscanner.core.report.Json
import com.wirelessscanner.core.report.JsonRead
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.security.KeyStore
import java.time.Instant
import java.util.concurrent.ConcurrentLinkedDeque
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** App log (port of AppLogger.cs): logcat + rolling private file (2 × 1 MB) + last 500 lines for the diagnostics export. */
class AppLog(context: Context) : AppLogger {
    private val dir = File(context.filesDir, "logs").also { it.mkdirs() }
    private val file = File(dir, "wirelessscanner.log")
    private val recent = ConcurrentLinkedDeque<String>()

    @Synchronized private fun write(level: String, message: String, t: Throwable?) {
        val line = "${Instant.now()} [$level] $message" + (t?.let { " | ${it.javaClass.simpleName}: ${it.message}" } ?: "")
        recent.addLast(line)
        while (recent.size > 500) recent.pollFirst()
        try {
            if (file.length() > 1_000_000) { File(dir, "wirelessscanner.1.log").delete(); file.renameTo(File(dir, "wirelessscanner.1.log")) }
            file.appendText(line + "\n")
        } catch (_: Exception) {}
    }

    override fun info(message: String) { Log.i(TAG, message); write("INFO", message, null) }
    override fun warn(message: String) { Log.w(TAG, message); write("WARN", message, null) }
    override fun error(message: String, t: Throwable?) { Log.e(TAG, message, t); write("ERROR", message, t) }
    fun recentLines(): List<String> = recent.toList()
    fun files(): List<File> = dir.listFiles()?.filter { it.isFile }?.sortedBy { it.name } ?: emptyList()

    companion object { const val TAG = "WirelessScanner" }
}

/** Small secrets (API key, responder keys) encrypted with an Android Keystore AES-GCM key (Windows: DPAPI). */
object Secrets {
    private const val ALIAS = "wirelessscanner-secrets"
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        return gen.generateKey()
    }

    fun protect(plain: String): String {
        if (plain.isEmpty()) return ""
        val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.ENCRYPT_MODE, key())
        return "ks:" + Base64.encodeToString(c.iv + c.doFinal(plain.toByteArray(Charsets.UTF_8)), Base64.NO_WRAP)
    }

    fun unprotect(stored: String?): String {
        if (stored.isNullOrEmpty() || !stored.startsWith("ks:")) return stored ?: ""
        return try {
            val b = Base64.decode(stored.substring(3), Base64.NO_WRAP)
            val c = Cipher.getInstance("AES/GCM/NoPadding"); c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, b, 0, 12))
            String(c.doFinal(b, 12, b.size - 12), Charsets.UTF_8)
        } catch (e: Exception) { "" }
    }
}

/** Preferences (Windows: SettingsRepository + RestApi settings). Secrets go through [Secrets]. */
class AppSettings(context: Context) : SettingsRepository {
    private val p = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    override fun get(key: String): String? = p.getString(key, null)
    override fun set(key: String, value: String?) { p.edit().apply { if (value == null) remove(key) else putString(key, value) }.apply() }
    fun bool(key: String, def: Boolean = false) = p.getBoolean(key, def)
    fun setBool(key: String, v: Boolean) = p.edit().putBoolean(key, v).apply()
    fun int(key: String, def: Int) = p.getInt(key, def)
    fun setInt(key: String, v: Int) = p.edit().putInt(key, v).apply()
    fun secret(key: String) = Secrets.unprotect(get(key))
    fun setSecret(key: String, v: String) = set(key, if (v.isEmpty()) null else Secrets.protect(v))

    var demoMode: Boolean get() = bool("demo"); set(v) = setBool("demo", v)
    var apiEnabled: Boolean get() = bool("api.enabled"); set(v) = setBool("api.enabled", v)
    var apiPort: Int get() = int("api.port", 8080); set(v) = setInt("api.port", v)
    var apiLan: Boolean get() = bool("api.lan"); set(v) = setBool("api.lan", v)
    var apiKey: String get() = secret("api.key"); set(v) = setSecret("api.key", v)
    var responderRoleEnabled: Boolean get() = bool("responder.role"); set(v) = setBool("responder.role", v)
    var responderRolePort: Int get() = int("responder.role.port", 5410); set(v) = setInt("responder.role.port", v)
    var responderRoleKey: String get() = secret("responder.role.key"); set(v) = setSecret("responder.role.key", v)

    var speedServer: SpeedServerSettings
        get() = get("speed")?.let { runCatching { JsonRead.read(it, SpeedServerSettings::class.java) }.getOrNull() } ?: SpeedServerSettings()
        set(v) = set("speed", Json.write(v))
    var responder: ResponderSettings
        get() = ResponderSettings(get("responder.address") ?: "", int("responder.port", 5410), secret("responder.key"))
        set(v) { set("responder.address", v.address); setInt("responder.port", v.port); setSecret("responder.key", v.key) }
    var unitOverrides: UnitOverrides
        get() = get("units")?.let { runCatching { JsonRead.read(it, UnitOverrides::class.java) }.getOrNull() } ?: UnitOverrides()
        set(v) = set("units", Json.write(v))
}

/** SQLite storage for sessions and both test kinds (Windows: EF Core / SQLite, schema 4). Rows hold the core records as JSON. */
class Db(context: Context) : SQLiteOpenHelper(context, "wirelessscanner.db", null, 1) {
    val path: File = context.getDatabasePath("wirelessscanner.db")
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE sessions(id TEXT PRIMARY KEY, start INTEGER, json TEXT)")
        db.execSQL("CREATE TABLE samples(session_id TEXT, ts INTEGER, json TEXT)")
        db.execSQL("CREATE INDEX ix_samples ON samples(session_id, ts)")
        db.execSQL("CREATE TABLE sat_tests(id TEXT PRIMARY KEY, start INTEGER, end_utc INTEGER, status TEXT, active REAL, config TEXT)")
        db.execSQL("CREATE TABLE sat_rows(test_id TEXT, kind TEXT, seq INTEGER, json TEXT)")
        db.execSQL("CREATE INDEX ix_sat_rows ON sat_rows(test_id, kind, seq)")
        db.execSQL("CREATE TABLE conn_tests(id TEXT PRIMARY KEY, start INTEGER, end_utc INTEGER, status TEXT, active REAL, config TEXT)")
        db.execSQL("CREATE TABLE conn_rows(test_id TEXT, kind TEXT, seq INTEGER, json TEXT)")
        db.execSQL("CREATE INDEX ix_conn_rows ON conn_rows(test_id, kind, seq)")
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {}

    fun sizeMb() = path.length() / (1024.0 * 1024.0)

    private inline fun <T> tx(block: SQLiteDatabase.() -> T): T { val d = writableDatabase; d.beginTransaction(); try { val r = d.block(); d.setTransactionSuccessful(); return r } finally { d.endTransaction() } }
    private fun rows(sql: String, vararg args: String): List<String> = readableDatabase.rawQuery(sql, args).use { c -> buildList { while (c.moveToNext()) add(c.getString(0)) } }

    val sessions = object : SessionRepository {
        override suspend fun saveSession(session: CaptureSession) = io { writableDatabase.insertWithOnConflict("sessions", null, ContentValues().apply { put("id", session.sessionId); put("start", session.startTime.toEpochMilli()); put("json", Json.write(session)) }, SQLiteDatabase.CONFLICT_REPLACE); Unit }
        override suspend fun saveSamples(samples: List<TelemetrySample>, sessionId: String) = io { tx { samples.forEach { s -> insert("samples", null, ContentValues().apply { put("session_id", sessionId); put("ts", s.timestamp.toEpochMilli()); put("json", Json.write(s)) }) } } }
        override suspend fun updateSessionStatus(sessionId: String, status: CaptureSessionStatus, endTime: Instant?) = io {
            val s = getSessionSync(sessionId) ?: return@io
            writableDatabase.update("sessions", ContentValues().apply { put("json", Json.write(s.copy(status = status, endTime = endTime ?: s.endTime))) }, "id=?", arrayOf(sessionId)); Unit
        }
        private fun getSessionSync(id: String) = rows("SELECT json FROM sessions WHERE id=?", id).firstOrNull()?.let { JsonRead.read(it, CaptureSession::class.java) }
        override suspend fun getSession(sessionId: String) = io { getSessionSync(sessionId) }
        override suspend fun getSamples(sessionId: String) = io { rows("SELECT json FROM samples WHERE session_id=? ORDER BY ts", sessionId).map { JsonRead.read(it, TelemetrySample::class.java) } }
        override suspend fun getAllSessions() = io { rows("SELECT json FROM sessions ORDER BY start DESC").map { JsonRead.read(it, CaptureSession::class.java) } }
        override suspend fun deleteSession(sessionId: String) = io { tx { delete("samples", "session_id=?", arrayOf(sessionId)); delete("sessions", "id=?", arrayOf(sessionId)) }; Unit }
        override suspend fun purgeSessionsByAge(threshold: Instant) = io { getAllSessionsSync().filter { it.startTime < threshold }.forEach { s -> tx { delete("samples", "session_id=?", arrayOf(s.sessionId)); delete("sessions", "id=?", arrayOf(s.sessionId)) } } }
        private fun getAllSessionsSync() = rows("SELECT json FROM sessions").map { JsonRead.read(it, CaptureSession::class.java) }
    }

    fun sampleCount(): Int = readableDatabase.rawQuery("SELECT COUNT(*) FROM samples", null).use { it.moveToFirst(); it.getInt(0) }

    val saturation = object : SaturationRepository {
        override suspend fun saveTest(config: SaturationTestConfig, startUtc: Instant, status: SaturationTestStatus) = io {
            writableDatabase.insertWithOnConflict("sat_tests", null, ContentValues().apply { put("id", config.testId); put("start", startUtc.toEpochMilli()); put("status", status.name); put("config", Json.write(config)) }, SQLiteDatabase.CONFLICT_REPLACE); Unit }
        override suspend fun updateTestStatus(testId: String, status: SaturationTestStatus, endUtc: Instant?, activeSeconds: Double?) = io {
            writableDatabase.update("sat_tests", ContentValues().apply { put("status", status.name); endUtc?.let { put("end_utc", it.toEpochMilli()) }; activeSeconds?.let { put("active", it) } }, "id=?", arrayOf(testId)); Unit }
        override suspend fun saveCycle(testId: String, cycle: SaturationCycle) = io { writableDatabase.insert("sat_rows", null, ContentValues().apply { put("test_id", testId); put("kind", "cycle"); put("seq", cycle.sequence); put("json", Json.write(cycle)) }); Unit }
        override suspend fun saveMarker(testId: String, marker: SaturationMarker) = io { writableDatabase.insert("sat_rows", null, ContentValues().apply { put("test_id", testId); put("kind", "marker"); put("seq", marker.elapsedMs); put("json", Json.write(marker)) }); Unit }
        private fun record(c: android.database.Cursor) = SaturationTestRecord(JsonRead.read(c.getString(5), SaturationTestConfig::class.java), Instant.ofEpochMilli(c.getLong(1)),
            if (c.isNull(2)) null else Instant.ofEpochMilli(c.getLong(2)), SaturationTestStatus.valueOf(c.getString(3)), c.getInt(6), if (c.isNull(4)) null else c.getDouble(4))
        private val q = "SELECT id,start,end_utc,status,active,config,(SELECT COUNT(*) FROM sat_rows r WHERE r.test_id=t.id AND r.kind='cycle') FROM sat_tests t"
        override suspend fun getTests() = io { readableDatabase.rawQuery("$q ORDER BY start DESC", null).use { c -> buildList { while (c.moveToNext()) add(record(c)) } } }
        override suspend fun getTest(testId: String) = io { readableDatabase.rawQuery("$q WHERE id=?", arrayOf(testId)).use { c -> if (c.moveToFirst()) record(c) else null } }
        override suspend fun getCycles(testId: String) = io { rows("SELECT json FROM sat_rows WHERE test_id=? AND kind='cycle' ORDER BY seq", testId).map { JsonRead.read(it, SaturationCycle::class.java) } }
        override suspend fun getMarkers(testId: String) = io { rows("SELECT json FROM sat_rows WHERE test_id=? AND kind='marker' ORDER BY seq", testId).map { JsonRead.read(it, SaturationMarker::class.java) } }
        override suspend fun deleteTest(testId: String) = io { tx { delete("sat_rows", "test_id=?", arrayOf(testId)); delete("sat_tests", "id=?", arrayOf(testId)) }; Unit }
    }

    val connection = object : ConnectionRepository {
        private fun put(testId: String, kind: String, seq: Long, v: Any) = writableDatabase.insert("conn_rows", null, ContentValues().apply { put("test_id", testId); put("kind", kind); put("seq", seq); put("json", Json.write(v)) })
        override suspend fun saveTest(config: ConnectionTestConfig, startUtc: Instant, status: ConnectionTestStatus) = io {
            writableDatabase.insertWithOnConflict("conn_tests", null, ContentValues().apply { put("id", config.testId); put("start", startUtc.toEpochMilli()); put("status", status.name); put("config", Json.write(config)) }, SQLiteDatabase.CONFLICT_REPLACE); Unit }
        override suspend fun updateTestStatus(testId: String, status: ConnectionTestStatus, endUtc: Instant?, activeSeconds: Double?) = io {
            writableDatabase.update("conn_tests", ContentValues().apply { put("status", status.name); endUtc?.let { put("end_utc", it.toEpochMilli()) }; activeSeconds?.let { put("active", it) } }, "id=?", arrayOf(testId)); Unit }
        override suspend fun saveProbes(testId: String, probes: List<ConnectionProbeResult>) = io { tx { probes.forEach { put(testId, "probe", it.elapsedMs, it) } }; Unit }
        override suspend fun saveLinkSamples(testId: String, samples: List<ConnectionLinkSample>) = io { tx { samples.forEach { put(testId, "link", it.elapsedMs, it) } }; Unit }
        override suspend fun saveMarker(testId: String, marker: ConnectionMarker) = io { put(testId, "marker", marker.elapsedMs, marker); Unit }
        override suspend fun saveSpeedTest(testId: String, result: SpeedTestResult) = io { put(testId, "speed", result.elapsedMs, result); Unit }
        override suspend fun saveTwoWay(testId: String, result: TwoWayResult) = io { put(testId, "twoway", result.elapsedMs, result); Unit }
        override suspend fun getTests() = io {
            readableDatabase.rawQuery("SELECT id,start,end_utc,status,active,config,(SELECT COUNT(*) FROM conn_rows r WHERE r.test_id=t.id AND r.kind='probe') FROM conn_tests t ORDER BY start DESC", null).use { c ->
                buildList { while (c.moveToNext()) add(ConnectionTestRecord(JsonRead.read(c.getString(5), ConnectionTestConfig::class.java), Instant.ofEpochMilli(c.getLong(1)),
                    if (c.isNull(2)) null else Instant.ofEpochMilli(c.getLong(2)), ConnectionTestStatus.valueOf(c.getString(3)), c.getInt(6), if (c.isNull(4)) null else c.getDouble(4))) }
            }
        }
        override suspend fun getData(testId: String) = io {
            fun <T> k(kind: String, cls: Class<T>) = rows("SELECT json FROM conn_rows WHERE test_id=? AND kind=? ORDER BY seq", testId, kind).map { JsonRead.read(it, cls) }
            ConnectionData(k("probe", ConnectionProbeResult::class.java), k("link", ConnectionLinkSample::class.java), k("speed", SpeedTestResult::class.java),
                k("twoway", TwoWayResult::class.java), k("marker", ConnectionMarker::class.java))
        }
        override suspend fun deleteTest(testId: String) = io { tx { delete("conn_rows", "test_id=?", arrayOf(testId)); delete("conn_tests", "id=?", arrayOf(testId)) }; Unit }
    }

    private suspend fun <T> io(block: () -> T): T = withContext(Dispatchers.IO) { synchronized(this@Db) { block() } }
}
