package fr.adam.nasreader

import android.os.Build
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * Shares progress, favorites and the "to read" list with the PC and the other devices through
 * one file on the NAS. Each side merges, keeping what is newer, then writes the result back.
 */
object Sync {
    private val exec = Executors.newSingleThreadScheduledExecutor()
    private val running = AtomicBoolean(false)
    private var pending: ScheduledFuture<*>? = null
    private val version = AtomicInteger()

    @Volatile var lastSync = 0L
    @Volatile var lastError: String? = null
    /** Goes up each time data from another device arrived, so the screen can reload it. */
    val dataVersion get() = version.get()

    fun start() {
        exec.scheduleWithFixedDelay({ run() }, 3, 120, TimeUnit.SECONDS)
    }

    /** Local data changed: send it a few seconds later, once the reader has settled. */
    @Synchronized
    fun changed() {
        pending?.cancel(false)
        pending = exec.schedule({ run() }, 4, TimeUnit.SECONDS)
    }

    fun runNow() = exec.submit { run() }.get(60, TimeUnit.SECONDS)

    private fun run() {
        val st = Settings.current
        if (!st.syncEnabled || !running.compareAndSet(false, true)) return
        val dir = st.syncPath()
        try {
            val existing = Store.read(dir, "userdata.json")
            if (existing != null && UserData.merge(JSONObject(String(existing)))) version.incrementAndGet()
            Store.write(dir, "userdata.json", UserData.export().toString().toByteArray())
            lastSync = System.currentTimeMillis()
            lastError = null
        } catch (e: Exception) {
            lastError = "Synchronisation impossible avec $dir : ${e.message ?: e}"
        } finally {
            running.set(false)
        }
    }

    /** Small files in a folder of the NAS (or of the device). */
    object Store {
        fun read(dir: String, name: String): ByteArray? =
            if (Fs.isSmb(dir)) Smb.readSmall("${dir.trimEnd('\\')}\\$name")
            else File(dir, name).takeIf { it.exists() }?.readBytes()

        fun write(dir: String, name: String, bytes: ByteArray) {
            if (Fs.isSmb(dir)) {
                Smb.writeSmall(dir, name, bytes, "$name.${Build.MODEL.replace(Regex("[^A-Za-z0-9]"), "")}.tmp")
            } else {
                File(dir).mkdirs()
                val tmp = File(dir, "$name.tmp")
                tmp.writeBytes(bytes)
                tmp.renameTo(File(dir, name))
            }
        }
    }
}
