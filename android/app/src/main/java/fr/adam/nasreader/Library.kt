package fr.adam.nasreader

import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

data class Book(
    val id: String,
    val path: String,
    val title: String,
    val kind: String,   // comic | manga | ebook | magazine | pdf
    val fmt: String,    // cbz | cbr | cb7 | pdf | epub | mobi | azw3
    val size: Long,
    val mtime: Long,
    val crumbs: List<String>,
    val sorted: Boolean,
) {
    val fileName: String get() = path.substringAfterLast('\\').substringAfterLast('/')

    fun toJson(): JSONObject = JSONObject().put("id", id).put("path", path).put("title", title).put("kind", kind)
        .put("fmt", fmt).put("size", size).put("mtime", mtime).put("crumbs", JSONArray(crumbs)).put("sorted", sorted)

    companion object {
        fun from(o: JSONObject) = Book(
            o.getString("id"), o.getString("path"), o.getString("title"), o.getString("kind"), o.getString("fmt"),
            o.getLong("size"), o.getLong("mtime"), Json.strings(o.getJSONArray("crumbs")), o.optBoolean("sorted"),
        )
    }
}

object Library {
    private val extensions = setOf("cbz", "cbr", "cb7", "pdf", "epub", "mobi", "azw3")

    // Folder names that mark a section of the library, whatever share they live in.
    private val sections = mapOf(
        "comics" to "comic", "comic" to "comic", "bd" to "comic", "bandes dessinées" to "comic",
        "mangas" to "manga", "manga" to "manga",
        "ebooks" to "ebook", "ebook" to "ebook", "livres" to "ebook", "romans" to "ebook",
        "magazines" to "magazine", "magazine" to "magazine", "presse" to "magazine",
    )

    @Volatile var books: List<Book> = emptyList(); private set
    @Volatile var scannedAt: Long = 0; private set
    @Volatile private var byId: Map<String, Book> = emptyMap()

    val scanning = AtomicBoolean(false)
    @Volatile var found = 0
    @Volatile var current = ""
    @Volatile var error: String? = null

    init {
        Json.load("library.json")?.let { t ->
            runCatching {
                val o = JSONObject(t)
                val a = o.getJSONArray("books")
                books = List(a.length()) { Book.from(a.getJSONObject(it)) }
                scannedAt = o.optLong("scannedAt")
                byId = books.associateBy { it.id }
            }
        }
    }

    fun get(id: String): Book? = byId[id]

    /**
     * Uses the list of books the PC put on the NAS after its own scan: one small file instead
     * of walking every folder over Wi-Fi. Returns true when it was taken.
     */
    fun adoptShared(maxAgeDays: Int = 7): Boolean {
        val st = Settings.current
        if (!st.syncEnabled || st.roots.isNotEmpty()) return false
        return runCatching {
            val bytes = Sync.Store.read(st.syncPath(), "library.json") ?: return false
            val o = JSONObject(String(bytes))
            val at = o.optLong("scannedAt")
            if (at <= scannedAt || System.currentTimeMillis() - at > maxAgeDays * 86_400_000L) return at > 0 && at <= scannedAt
            val a = o.getJSONArray("books")
            val list = List(a.length()) { Book.from(a.getJSONObject(it)) }
            if (list.isEmpty()) return false
            books = list
            byId = list.associateBy { it.id }
            scannedAt = at
            Json.save("library.json", String(bytes))
            UserData.reconcile(list)
            true
        }.getOrDefault(false)
    }

    /** Same id as the Windows app: SHA-1 of the lower-cased path, first 8 bytes in hex. */
    fun idFor(path: String): String {
        val h = MessageDigest.getInstance("SHA-1").digest(path.lowercase(Locale.ROOT).toByteArray())
        return h.take(8).joinToString("") { "%02x".format(it) }
    }

    fun json(): JSONObject = JSONObject()
        .put("books", JSONArray().also { a -> books.forEach { a.put(it.toJson()) } })
        .put("scannedAt", scannedAt)

    fun startScan() {
        if (!scanning.compareAndSet(false, true)) return
        Thread {
            try {
                scan(); error = null
            } catch (e: Exception) {
                error = e.message ?: e.toString()
            } finally {
                scanning.set(false); current = ""
            }
        }.apply { name = "scan"; isDaemon = true; start() }
    }

    private fun scan() {
        val st = Settings.current
        found = 0
        val exclude = st.excludeFolders.map { it.lowercase() }.toSet()
        val result = ArrayList<Book>()
        var reached = 0
        var lastError: Exception? = null
        for (root in st.effectiveRoots()) {
            try {
                if (!Fs.exists(root)) continue
            } catch (e: Exception) {
                lastError = e
                // A login or network problem affects every share: stop here.
                if (e.message?.startsWith("La NAS") == true || e.message?.startsWith("Impossible") == true) throw e
                continue
            }
            reached++
            val stack = ArrayDeque<String>().apply { addLast(root) }
            while (stack.isNotEmpty()) {
                val dir = stack.removeLast()
                current = dir
                val entries = try { Fs.list(dir) } catch (e: Exception) { lastError = e; continue }
                for (e in entries) {
                    if (e.isDir) {
                        if (!e.hidden && e.name.lowercase() !in exclude && !e.name.startsWith(".")) stack.addLast(Fs.child(dir, e.name))
                    } else {
                        val ext = e.name.substringAfterLast('.', "").lowercase()
                        if (ext in extensions) {
                            result.add(classify(root, dir, e, ext))
                            found = result.size
                        }
                    }
                }
            }
        }
        if (reached == 0) throw lastError ?: java.io.IOException("Aucun dossier de la NAS n'a pu être ouvert. Vérifie les Réglages.")

        books = result
        byId = result.associateBy { it.id }
        scannedAt = System.currentTimeMillis()
        Json.save("library.json", json().toString())
        UserData.reconcile(result)
        Enricher.restart()
    }

    private fun classify(root: String, dir: String, e: FsEntry, fmt: String): Book {
        val smb = Fs.isSmb(root)
        val sep = if (smb) '\\' else '/'
        val share = root.trimEnd(sep).substringAfterLast(sep)
        val rel = dir.removePrefix(root).trim(sep)
        val folders = (listOf(share) + if (rel.isEmpty()) emptyList() else rel.split(sep)).toMutableList()

        var kind: String? = null
        var crumbs: List<String> = emptyList()
        // The first section folder wins, except that a "comics" folder inside an "ebooks"
        // folder (\\nas\Ebooks\comics\Marvel) still makes it a comic.
        for (i in folders.indices) {
            val k = sections[folders[i].lowercase()] ?: continue
            if (kind == "ebook" && k == "ebook") continue
            kind = k
            crumbs = folders.drop(i + 1)
            if (k != "ebook") break
        }
        val sorted = kind != null
        if (kind == null) {
            kind = when (fmt) { "cbz", "cbr", "cb7" -> "comic"; "epub", "mobi", "azw3" -> "ebook"; else -> "pdf" }
            crumbs = folders
        } else if (kind == "ebook" && fmt in setOf("cbz", "cbr", "cb7")) kind = "comic"

        val clean = ArrayList<String>()
        for (c in crumbs) if (clean.isEmpty() || !clean.last().equals(c, ignoreCase = true)) clean.add(c)

        val path = Fs.child(dir, e.name)
        return Book(idFor(path), path, e.name.substringBeforeLast('.'), kind, fmt, e.size, e.mtime, clean, sorted)
    }
}
