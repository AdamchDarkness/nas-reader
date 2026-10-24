package fr.adam.nasreader

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

object App {
    lateinit var ctx: Context
    val data: File get() = ctx.filesDir
    val covers: File get() = File(ctx.filesDir, "covers").apply { mkdirs() }
    val pages: File get() = File(ctx.cacheDir, "pages").apply { mkdirs() }
    val files: File get() = File(ctx.cacheDir, "files").apply { mkdirs() }

    fun init(context: Context) {
        ctx = context.applicationContext
        // Android/data/fr.adam.nasreader/files: a folder you can fill over USB to read offline.
        ctx.getExternalFilesDir(null)
    }
}

object Json {
    fun load(name: String): String? = File(App.data, name).takeIf { it.exists() }?.readText()

    fun save(name: String, text: String) {
        val f = File(App.data, name)
        val tmp = File(App.data, "$name.tmp")
        tmp.writeText(text)
        tmp.renameTo(f)
    }

    fun strings(a: JSONArray?): MutableList<String> =
        if (a == null) mutableListOf() else MutableList(a.length()) { a.getString(it) }
}

/** The NAS shares seen on \\lord_of_adam (Android cannot list them without a login). */
val DEFAULT_SHARES = listOf(
    "Comics Mangas Livre", "Downloads", "Ebooks", "Formation", "Media", "Python",
    "Script", "Torrent", "web", "web_packages", "Youtube",
)

class Settings(
    var nas: String = "lord_of_adam",
    var host: String = "192.168.11.108",
    var user: String = "",
    var password: String = "",
    var domain: String = "",
    var roots: MutableList<String> = mutableListOf(),
    var excludeFolders: MutableList<String> = mutableListOf("#recycle", "@eaDir", "#snapshot", "_Doublons", ".Trash", "\$RECYCLE.BIN"),
    var scanOnStartup: Boolean = true,
    var cacheLimitGb: Int = 4,
    var syncEnabled: Boolean = true,
    var syncFolder: String = "",
    var autoUpdate: Boolean = true,
) {
    /** Where userdata.json is shared with the PC and the other devices. */
    fun syncPath(): String = syncFolder.trim().trimEnd('\\').ifEmpty { "\\\\$nas\\Comics Mangas Livre\\.nasreader" }

    fun shares(): List<String> = DEFAULT_SHARES.map { "\\\\$nas\\$it" }
    fun effectiveRoots(): List<String> = roots.ifEmpty { shares() }

    /** What the interface sees: the password itself never leaves the phone. */
    fun toJson(): JSONObject = JSONObject()
        .put("nas", nas).put("host", host).put("user", user).put("domain", domain)
        .put("password", "").put("passwordSet", password.isNotEmpty())
        .put("roots", JSONArray(roots)).put("excludeFolders", JSONArray(excludeFolders))
        .put("scanOnStartup", scanOnStartup).put("cacheLimitGb", cacheLimitGb)
        .put("syncEnabled", syncEnabled).put("syncFolder", syncFolder).put("autoUpdate", autoUpdate)

    private fun toStore(): JSONObject = toJson().put("password", password)

    companion object {
        @Volatile
        private var cur: Settings? = null

        val current: Settings
            @Synchronized get() = cur ?: (Json.load("settings.json")?.let { runCatching { parse(JSONObject(it), null) }.getOrNull() } ?: Settings()).also { cur = it }

        fun parse(o: JSONObject, old: Settings?): Settings = Settings(
            nas = o.optString("nas", "lord_of_adam").trim().trimStart('\\').ifEmpty { "lord_of_adam" },
            host = o.optString("host", "").trim(),
            user = o.optString("user", "").trim(),
            // An empty password from the form means "keep the saved one".
            password = o.optString("password", "").ifEmpty { if (o.optBoolean("clearPassword")) "" else old?.password ?: "" },
            domain = o.optString("domain", "").trim(),
            roots = Json.strings(o.optJSONArray("roots")),
            excludeFolders = Json.strings(o.optJSONArray("excludeFolders")),
            scanOnStartup = o.optBoolean("scanOnStartup", true),
            cacheLimitGb = o.optInt("cacheLimitGb", 4).coerceIn(1, 200),
            syncEnabled = o.optBoolean("syncEnabled", true),
            syncFolder = o.optString("syncFolder", "").trim(),
            autoUpdate = o.optBoolean("autoUpdate", true),
        )

        @Synchronized
        fun update(s: Settings) {
            cur = s
            Json.save("settings.json", s.toStore().toString())
        }
    }
}

data class Progress(
    val page: Int, val pages: Int, val loc: String?, val updated: Long,
    val size: Long = 0, val mtime: Long = 0, val name: String? = null, val deleted: Boolean = false,
) {
    fun toJson(): JSONObject = JSONObject().put("page", page).put("pages", pages).put("loc", loc ?: JSONObject.NULL)
        .put("updated", updated).put("size", size).put("mtime", mtime).put("name", name ?: JSONObject.NULL).put("deleted", deleted)

    companion object {
        fun from(o: JSONObject) = Progress(
            o.optInt("page"), o.optInt("pages"), o.optString("loc").takeIf { o.has("loc") && !o.isNull("loc") && it.isNotEmpty() },
            o.optLong("updated"), o.optLong("size"), o.optLong("mtime"), o.optString("name").takeIf { o.has("name") && !o.isNull("name") },
            o.optBoolean("deleted"),
        )
    }
}

data class Mark(val added: Long, val size: Long = 0, val mtime: Long = 0, val name: String? = null, val removed: Long = 0) {
    val active get() = added > removed
    val stamp get() = maxOf(added, removed)

    fun toJson(): JSONObject = JSONObject().put("added", added).put("size", size).put("mtime", mtime)
        .put("name", name ?: JSONObject.NULL).put("removed", removed)

    companion object {
        fun from(o: JSONObject) = Mark(o.optLong("added"), o.optLong("size"), o.optLong("mtime"), o.optString("name").takeIf { o.has("name") && !o.isNull("name") }, o.optLong("removed"))
    }
}

/**
 * Progress and lists, kept per book. The fingerprint follows books moved on the NAS, and
 * "deleted" / "removed" markers carry "unread" and "remove from list" to the other devices.
 */
object UserData {
    private val progress = HashMap<String, Progress>()
    private val lists = linkedMapOf<String, LinkedHashMap<String, Mark>>("favorites" to linkedMapOf(), "toread" to linkedMapOf())
    private val now get() = System.currentTimeMillis()

    init {
        Json.load("progress.json")?.let { t -> readProgress(JSONObject(t), progress) }
        Json.load("lists.json")?.let { t -> readLists(JSONObject(t), lists) }
    }

    private fun readProgress(o: JSONObject, into: MutableMap<String, Progress>) =
        o.keys().forEach { into[it] = Progress.from(o.getJSONObject(it)) }

    private fun readLists(o: JSONObject, into: MutableMap<String, LinkedHashMap<String, Mark>>) =
        o.keys().forEach { l ->
            val m = into.getOrPut(l) { linkedMapOf() }
            val lo = o.getJSONObject(l)
            lo.keys().forEach { m[it] = Mark.from(lo.getJSONObject(it)) }
        }

    private fun progressJson() = JSONObject().also { o -> progress.forEach { (k, v) -> o.put(k, v.toJson()) } }
    private fun listsJson() = JSONObject().also { o ->
        lists.forEach { (k, v) -> o.put(k, JSONObject().also { lo -> v.forEach { (id, m) -> lo.put(id, m.toJson()) } }) }
    }

    @Synchronized
    fun snapshot(): JSONObject {
        val p = JSONObject().also { o -> progress.forEach { (k, v) -> if (!v.deleted) o.put(k, v.toJson()) } }
        val l = JSONObject().also { o ->
            lists.forEach { (k, v) -> o.put(k, JSONArray(v.entries.filter { it.value.active }.sortedByDescending { it.value.added }.map { it.key })) }
        }
        return JSONObject().put("progress", p).put("lists", l)
    }

    /** Everything, markers included: the content of the shared file. */
    @Synchronized
    fun export(): JSONObject = JSONObject().put("version", 1).put("progress", progressJson()).put("lists", listsJson())

    private fun saveProgress() { Json.save("progress.json", progressJson().toString()); Sync.changed() }
    private fun saveLists() { Json.save("lists.json", listsJson().toString()); Sync.changed() }

    private fun stamp(id: String, p: Progress): Progress {
        val b = Library.get(id) ?: return p
        return p.copy(size = b.size, mtime = b.mtime, name = b.fileName)
    }

    @Synchronized
    fun setProgress(id: String, p: Progress) {
        progress[id] = stamp(id, p.copy(deleted = false)); saveProgress()
    }

    @Synchronized
    fun setMany(items: List<Triple<String, Int, Int>>) {
        items.forEach { (id, page, pages) -> progress[id] = stamp(id, Progress(page, pages, null, now)) }
        saveProgress()
    }

    @Synchronized
    fun remove(ids: List<String>) {
        ids.forEach { id -> progress[id] = progress[id]?.copy(deleted = true, updated = now) ?: stamp(id, Progress(0, 0, null, now, deleted = true)) }
        saveProgress()
    }

    @Synchronized
    fun setMark(list: String, id: String, on: Boolean): Boolean {
        val m = lists[list] ?: return false
        val b = Library.get(id)
        val cur = m[id]
        m[id] = if (on) Mark(now, b?.size ?: cur?.size ?: 0, b?.mtime ?: cur?.mtime ?: 0, b?.fileName ?: cur?.name)
        else (cur ?: Mark(0, b?.size ?: 0, b?.mtime ?: 0, b?.fileName)).copy(removed = now)
        saveLists()
        return true
    }

    /** Takes whatever is newer in the other device's data. Returns true when something changed here. */
    @Synchronized
    fun merge(other: JSONObject): Boolean {
        var pc = false
        var lc = false
        val rp = HashMap<String, Progress>().also { m -> other.optJSONObject("progress")?.let { readProgress(it, m) } }
        for ((id, r) in rp) {
            val l = progress[id]
            if (l == null || l.updated < r.updated) { progress[id] = r; pc = true }
        }
        val rl = linkedMapOf<String, LinkedHashMap<String, Mark>>().also { m -> other.optJSONObject("lists")?.let { readLists(it, m) } }
        for ((name, marks) in rl) {
            val local = lists.getOrPut(name) { linkedMapOf() }
            for ((id, r) in marks) {
                val l = local[id]
                if (l == null || l.stamp < r.stamp) { local[id] = r; lc = true }
            }
        }
        // Markers older than six months have reached every device by now.
        val old = now - 180L * 24 * 3600 * 1000
        progress.entries.removeIf { (it.value.deleted && it.value.updated < old).also { r -> if (r) pc = true } }
        lists.values.forEach { m -> m.entries.removeIf { (!it.value.active && it.value.removed < old).also { r -> if (r) lc = true } } }
        if (pc) Json.save("progress.json", progressJson().toString())
        if (lc) Json.save("lists.json", listsJson().toString())
        return pc || lc
    }

    /** After a scan, re-attach progress and lists of books whose path changed. */
    @Synchronized
    fun reconcile(books: List<Book>) {
        val ids = books.map { it.id }.toHashSet()
        val byNameSize = books.groupBy { it.fileName.lowercase() to it.size }
        val bySizeTime = books.groupBy { it.size to it.mtime / 1000 }
        fun find(size: Long, mtime: Long, name: String?): Book? {
            if (size <= 0) return null
            if (name != null) byNameSize[name.lowercase() to size]?.singleOrNull()?.let { return it }
            return bySizeTime[size to mtime / 1000]?.singleOrNull()
        }
        var pc = false
        var lc = false
        for ((id, p) in progress.toList()) {
            if (p.deleted || id in ids) continue
            val moved = find(p.size, p.mtime, p.name) ?: continue
            progress[id] = p.copy(deleted = true, updated = now)
            val existing = progress[moved.id]
            if (existing == null || existing.updated < p.updated || existing.deleted) progress[moved.id] = p.copy(name = moved.fileName, updated = now)
            pc = true
        }
        for (marks in lists.values) for ((id, m) in marks.toList()) {
            if (!m.active || id in ids) continue
            val moved = find(m.size, m.mtime, m.name) ?: continue
            marks[id] = m.copy(removed = now)
            if (marks[moved.id]?.active != true) marks[moved.id] = m.copy(name = moved.fileName, added = now, removed = 0)
            lc = true
        }
        if (pc) saveProgress()
        if (lc) saveLists()
    }
}

/** Details read from ComicInfo.xml or the EPUB's OPF; an empty object means "already looked". */
object MetaStore {
    private val all = HashMap<String, JSONObject>()
    private var dirty = 0
    private var lastSave = 0L

    init {
        Json.load("meta.json")?.let { t -> val o = JSONObject(t); o.keys().forEach { all[it] = o.getJSONObject(it) } }
    }

    @Synchronized
    fun has(id: String) = all.containsKey(id)

    @Synchronized
    fun filled(): JSONObject = JSONObject().also { o -> all.forEach { (k, v) -> if (v.length() > 0) o.put(k, v) } }

    @Synchronized
    fun set(id: String, meta: JSONObject, overwrite: Boolean = true) {
        if (!overwrite && (all[id]?.length() ?: 0) > 0) return
        all[id] = meta
        dirty++
        if (System.currentTimeMillis() - lastSave > 5000) flush()
    }

    @Synchronized
    fun flush() {
        if (dirty == 0) return
        dirty = 0
        lastSave = System.currentTimeMillis()
        runCatching { Json.save("meta.json", JSONObject().also { o -> all.forEach { (k, v) -> o.put(k, v) } }.toString()) }
    }
}
