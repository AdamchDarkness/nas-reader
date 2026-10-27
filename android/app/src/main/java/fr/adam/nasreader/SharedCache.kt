package fr.adam.nasreader

import org.json.JSONObject
import java.io.File

/**
 * Covers and book details kept on the NAS next to the sync file (.nasreader\covers, .nasreader\meta.json).
 * The PC makes them once; the phone and tablet fetch these small images instead of opening every comic.
 */
object SharedCache {
    @Volatile private var remote: HashSet<String>? = null
    @Volatile private var listedAt = 0L

    private val enabled get() = Settings.current.syncEnabled
    private val dir get() = Settings.current.syncPath()
    private fun coversDir() = Fs.child(dir, "covers")

    /** Ids of the covers on the NAS: one folder listing, refreshed every 10 minutes. */
    @Synchronized
    fun remote(): Set<String> {
        val cached = remote
        if (cached != null && System.currentTimeMillis() - listedAt < 600_000) return cached
        val set = HashSet<String>()
        runCatching {
            Fs.list(coversDir()).forEach { e -> if (!e.isDir && e.name.endsWith(".jpg")) set.add(e.name.removeSuffix(".jpg")) }
        }
        listedAt = System.currentTimeMillis()
        remote = set
        return set
    }

    /** Copies the NAS cover to the local cache when it exists there. */
    fun fetchCover(id: String, local: File): Boolean {
        if (!enabled || id !in remote()) return false
        return runCatching {
            val bytes = Sync.Store.read(coversDir(), "$id.jpg") ?: return false
            val tmp = File(local.path + ".tmp")
            tmp.writeBytes(bytes)
            tmp.renameTo(local)
        }.getOrDefault(false)
    }

    /** A cover made here goes to the NAS too, for the other devices. */
    fun publishCover(id: String, local: File) {
        if (!enabled || id in remote()) return
        runCatching {
            Sync.Store.write(coversDir(), "$id.jpg", local.readBytes())
            synchronized(this) { remote?.add(id) }
        }
    }

    /** Takes the details found by the other devices. */
    fun pullMeta() {
        if (!enabled) return
        runCatching {
            val bytes = Sync.Store.read(dir, "meta.json") ?: return
            val o = JSONObject(String(bytes))
            o.keys().forEach { id -> val m = o.getJSONObject(id); if (m.length() > 0) MetaStore.set(id, m, overwrite = false) }
            MetaStore.flush()
        }
    }
}
