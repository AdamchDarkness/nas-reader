package fr.adam.nasreader

import android.os.StatFs
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

/**
 * Books the user chose to keep on the device, to read away from home.
 * The whole file is copied; the readers then use it instead of the NAS copy.
 */
object Offline {
    val dir: File by lazy { (App.ctx.getExternalFilesDir("offline") ?: File(App.ctx.filesDir, "offline")).apply { mkdirs() } }

    class Job(val id: String, @Volatile var state: String, @Volatile var bytes: Long, val size: Long, @Volatile var error: String? = null)

    private val items = LinkedHashMap<String, JSONObject>() // id -> {fmt, size, added}
    private val jobs = ConcurrentHashMap<String, Job>()
    private val exec = Executors.newSingleThreadExecutor()

    init {
        Json.load("offline.json")?.let { t ->
            val o = JSONObject(t)
            o.keys().forEach { id -> items[id] = o.getJSONObject(id) }
        }
        // Forget entries whose file was removed (storage cleaned by Android, etc.).
        items.entries.removeIf { (id, v) -> !File(dir, "$id.${v.optString("fmt")}").exists() }
        // Files left behind by an interrupted download or a failed delete.
        dir.listFiles()?.forEach { f -> if (f.name.substringBefore('.') !in items) f.delete() }
    }

    private fun save() = Json.save("offline.json", JSONObject().also { o -> items.forEach { (k, v) -> o.put(k, v) } }.toString())

    /** The downloaded copy of a book, if it is complete and still matches the NAS file. */
    fun file(b: Book): File? =
        if (synchronized(this) { b.id !in items }) null
        else File(dir, "${b.id}.${b.fmt}").takeIf { it.exists() && it.length() == b.size }

    @Synchronized
    fun enqueue(ids: List<String>) {
        for (id in ids) {
            val b = Library.get(id) ?: continue
            if (file(b) != null || jobs[id]?.state == "queued" || jobs[id]?.state == "downloading") continue
            val job = Job(id, "queued", 0, b.size)
            jobs[id] = job
            exec.execute { download(b, job) }
        }
    }

    private fun download(b: Book, job: Job) {
        if (job.state == "cancelled") return
        job.state = "downloading"
        val part = File(dir, "${b.id}.part")
        try {
            Fs.open(b.path, b.size).use { rf ->
                part.outputStream().use { out ->
                    val buf = ByteArray(1 shl 20)
                    var pos = 0L
                    while (job.state == "downloading") {
                        val n = rf.read(pos, buf, 0, buf.size)
                        if (n <= 0) break
                        out.write(buf, 0, n)
                        pos += n
                        job.bytes = pos
                    }
                }
            }
            if (job.state != "downloading") { part.delete(); return }
            if (part.length() != b.size) throw java.io.IOException("Téléchargement incomplet.")
            part.renameTo(File(dir, "${b.id}.${b.fmt}"))
            synchronized(this) {
                items[b.id] = JSONObject().put("fmt", b.fmt).put("size", b.size).put("added", System.currentTimeMillis())
                save()
            }
            job.state = "done"
            // Prepare the cover now, while the file is at hand.
            runCatching { Covers.get(b) }
        } catch (e: Exception) {
            part.delete()
            job.state = "error"
            job.error = e.message ?: e.toString()
        }
    }

    @Synchronized
    fun remove(ids: List<String>) {
        for (id in ids) {
            jobs[id]?.let { it.state = "cancelled" }
            jobs.remove(id)
            val v = items.remove(id)
            Books.forget(id)
            File(dir, "$id.${v?.optString("fmt") ?: "*"}").delete()
            dir.listFiles { f -> f.name.startsWith("$id.") }?.forEach { it.delete() }
        }
        save()
    }

    @Synchronized
    fun status(): JSONObject {
        val done = JSONObject()
        var total = 0L
        items.forEach { (id, v) -> done.put(id, v.optLong("size")); total += v.optLong("size") }
        val active = JSONObject()
        jobs.values.filter { it.state != "done" && it.state != "cancelled" }.forEach { j ->
            active.put(j.id, JSONObject().put("state", j.state).put("bytes", j.bytes).put("size", j.size).put("error", j.error ?: JSONObject.NULL))
        }
        val free = runCatching { StatFs(dir.path).availableBytes }.getOrDefault(0L)
        return JSONObject().put("done", done).put("active", active).put("totalBytes", total).put("freeBytes", free)
    }
}

/** Is the NAS reachable right now? Checked at most every 20 seconds, with a short timeout. */
object Reach {
    @Volatile private var last = 0L
    @Volatile private var ok = true

    fun nas(): Boolean {
        val now = System.currentTimeMillis()
        if (now - last < 20_000) return ok
        last = now
        val st = Settings.current
        ok = listOf(st.host, "${st.nas}.local").filter { it.isNotBlank() }.any { h ->
            runCatching { Socket().use { s -> s.connect(InetSocketAddress(h, 445), 1500) }; true }.getOrDefault(false)
        }
        return ok
    }
}
