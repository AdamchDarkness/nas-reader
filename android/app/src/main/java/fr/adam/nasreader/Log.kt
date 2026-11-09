package fr.adam.nasreader

import android.os.Build
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** A short journal of what went wrong, for "Signaler un problème". At most about 1 MB. */
object Log {
    private val file get() = File(App.data, "log.txt")
    private val fmt = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.ROOT)

    fun info(message: String) = write("INFO", message)
    fun error(message: String, e: Throwable? = null) =
        write("ERR ", if (e == null) message else "$message : ${e.javaClass.simpleName}: ${e.message}")

    @Synchronized
    private fun write(level: String, message: String) {
        runCatching {
            val f = file
            if (f.length() > 1 shl 20) f.writeText(f.readText().let { it.substring(it.length / 2) })
            f.appendText("${fmt.format(Date())} $level ${message.replace('\n', ' ')}\n")
        }
    }

    @Synchronized
    fun report(): String {
        val tail = runCatching { file.takeIf { it.exists() }?.readText() ?: "" }.getOrElse { it.message ?: "" }.takeLast(60_000)
        val s = Settings.current
        val version = runCatching { App.ctx.packageManager.getPackageInfo(App.ctx.packageName, 0).versionName }.getOrNull()
        return """
            |Nas Reader $version · Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}) · ${Build.MANUFACTURER} ${Build.MODEL}
            |NAS joignable : ${if (Reach.nas()) "oui" else "non"} · ${Library.books.size} livres · NAS ${s.host}
            |Synchro : ${if (s.syncEnabled) "oui" else "non"}${Sync.lastError?.let { " · $it" } ?: ""}
            |Cache : ${s.cacheLimitGb} Go max · ${Books.cacheSize() / 1_000_000} Mo utilisés
            |
            |""".trimMargin() + tail
    }
}
