package fr.adam.nasreader

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import org.json.JSONObject
import org.w3c.dom.Element
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.URLDecoder
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.Semaphore
import java.util.concurrent.atomic.AtomicInteger
import javax.xml.parsers.DocumentBuilderFactory

/** Covers and details (ComicInfo.xml, EPUB metadata), read from a book in one pass. */
object Covers {
    private const val WIDTH = 360
    private val gate = Semaphore(3)
    private val pending = ConcurrentHashMap<String, Any>()

    private fun coverFile(b: Book) = File(App.covers, "${b.id}.jpg")
    private fun failFile(b: Book) = File(App.covers, "${b.id}.fail")
    private fun hasMetaFormat(b: Book) = b.fmt in setOf("cbz", "cbr", "cb7", "epub")
    private fun needsCover(b: Book) = b.fmt !in setOf("mobi", "azw3") && !coverFile(b).exists() && !failFile(b).exists()
    private fun needsMeta(b: Book) = hasMetaFormat(b) && !MetaStore.has(b.id)
    fun needsWork(b: Book) = needsCover(b) || needsMeta(b)

    /** Cover asked by the screen: served before background work. */
    fun get(b: Book): File? {
        val f = coverFile(b)
        if (f.exists()) return f
        if (failFile(b).exists()) return null
        process(b, useGate = true)
        return f.takeIf { it.exists() }
    }

    fun ensure(b: Book) {
        if (needsWork(b)) process(b, useGate = false)
    }

    private fun process(b: Book, useGate: Boolean) {
        val lock = pending.computeIfAbsent(b.id) { Any() }
        synchronized(lock) {
            if (useGate) gate.acquire()
            val needCover = needsCover(b)
            val needMeta = needsMeta(b)
            try {
                if (!needCover && !needMeta) return
                // Already made by the PC or another device: a small copy instead of opening the comic.
                if (needCover && SharedCache.fetchCover(b.id, coverFile(b))) {
                    // The device that made it already read the details; empty ones are not shared.
                    if (needMeta) MetaStore.set(b.id, JSONObject(), overwrite = false)
                    return
                }
                val t0 = System.currentTimeMillis()
                val counter = CountingFile(Books.openFile(b))
                val (jpeg, meta) = counter.use { raw ->
                    CachedFile(raw).use { rf ->
                        when (b.fmt) {
                            "cbz", "cbr", "cb7" -> fromArchive(rf, needCover)
                            "pdf" -> fromPdf(rf, b.id) to null
                            "epub" -> fromEpub(rf, needCover)
                            else -> null to null
                        }
                    }
                }
                android.util.Log.i("NasReader", "cover ${b.fmt} ${b.size / 1024} KB: read ${counter.bytes / 1024} KB in ${counter.calls} calls, ${System.currentTimeMillis() - t0} ms")
                if (needMeta) MetaStore.set(b.id, meta ?: JSONObject(), overwrite = false)
                if (needCover) {
                    if (jpeg == null) throw IllegalStateException("no cover")
                    coverFile(b).writeBytes(jpeg)
                    SharedCache.publishCover(b.id, coverFile(b))
                }
            } catch (e: Exception) {
                if (needCover) runCatching { failFile(b).writeText(e.toString()) }
                if (needMeta) MetaStore.set(b.id, JSONObject(), overwrite = false)
            } finally {
                if (useGate) gate.release()
                pending.remove(b.id)
            }
        }
    }

    private fun fromArchive(rf: RandomFile, needCover: Boolean): Pair<ByteArray?, JSONObject?> {
        var meta: JSONObject? = null
        var firstSeen: String? = null
        var firstBytes: ByteArray? = null
        val images = ArrayList<String>()
        ArchiveReader(rf).use { ar ->
            while (true) {
                val e = ar.next() ?: break
                when {
                    !e.isFile -> ar.skip()
                    Pages.isComicInfo(e.name) -> meta = parseComicInfo(ar.bytes())
                    Pages.isPage(e.name) -> {
                        images.add(e.name)
                        // Usually the first image stored is the cover: keep it to avoid a second pass.
                        if (needCover && firstSeen == null) { firstSeen = e.name; firstBytes = ar.bytes() } else ar.skip()
                    }
                    else -> ar.skip()
                }
            }
        }
        meta?.let { if (!it.has("pageCount")) it.put("pageCount", images.size) }
        if (!needCover || images.isEmpty()) return null to meta
        val first = images.minByOrNull { Pages.naturalKey(it) }!!
        val bytes = if (first == firstSeen) firstBytes else ArchiveReader(rf).use { ar ->
            var found: ByteArray? = null
            while (true) {
                val e = ar.next() ?: break
                if (e.name == first) { found = ar.bytes(); break }
                ar.skip()
            }
            found
        }
        return bytes?.let { resize(it) } to meta
    }

    private fun fromPdf(rf: RandomFile, tag: String): ByteArray? {
        ProxyFd(object : RandomFile by rf { override fun close() {} }, "cover-$tag").use { proxy ->
            PdfRenderer(proxy.fd).use { r ->
                if (r.pageCount == 0) return null
                r.openPage(0).use { p ->
                    val h = (WIDTH.toLong() * p.height / maxOf(1, p.width)).toInt()
                    val bmp = Bitmap.createBitmap(WIDTH, h, Bitmap.Config.ARGB_8888)
                    bmp.eraseColor(Color.WHITE)
                    p.render(bmp, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                    return ByteArrayOutputStream().also { bmp.compress(Bitmap.CompressFormat.JPEG, 82, it); bmp.recycle() }.toByteArray()
                }
            }
        }
    }

    private fun fromEpub(rf: RandomFile, needCover: Boolean): Pair<ByteArray?, JSONObject?> {
        val texts = HashMap<String, ByteArray>()
        val images = ArrayList<Pair<String, Long>>()
        ArchiveReader(rf).use { ar ->
            while (true) {
                val e = ar.next() ?: break
                val n = e.name.lowercase()
                if (e.isFile && (n.endsWith(".opf") || n == "meta-inf/container.xml")) texts[e.name] = ar.bytes()
                else { if (e.isFile && Pages.isPage(e.name)) images.add(e.name to e.size); ar.skip() }
            }
        }
        val (href, meta) = readOpf(texts)
        if (!needCover) return null to meta
        val target = href?.takeIf { h -> images.any { it.first == h } }
            ?: images.sortedWith(compareByDescending<Pair<String, Long>> { it.first.contains("cover", true) }.thenByDescending { it.second }).firstOrNull()?.first
            ?: return null to meta
        val bytes = ArchiveReader(rf).use { ar ->
            var found: ByteArray? = null
            while (true) {
                val e = ar.next() ?: break
                if (e.name == target) { found = ar.bytes(); break }
                ar.skip()
            }
            found
        }
        return bytes?.let { resize(it) } to meta
    }

    fun resize(bytes: ByteArray): ByteArray? {
        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, o)
        if (o.outWidth <= 0) return null
        var sample = 1
        while (o.outWidth / (sample * 2) >= WIDTH) sample *= 2
        val src = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
        val w = minOf(WIDTH, src.width)
        val h = maxOf(1, (src.height.toLong() * w / src.width).toInt())
        val scaled = if (w == src.width) src else Bitmap.createScaledBitmap(src, w, h, true)
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, 82, out)
        if (scaled !== src) scaled.recycle()
        src.recycle()
        return out.toByteArray()
    }

    private fun clean(s: String?): String? {
        if (s.isNullOrBlank()) return null
        val t = android.text.Html.fromHtml(s.replace(Regex("<[^>]+>"), " "), android.text.Html.FROM_HTML_MODE_LEGACY).toString()
            .replace(Regex("\\s+"), " ").trim()
        return t.ifEmpty { null }
    }

    private fun xml(bytes: ByteArray) = DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true }
        .newDocumentBuilder().parse(ByteArrayInputStream(bytes))

    fun parseComicInfo(bytes: ByteArray): JSONObject? = runCatching {
        val root = xml(bytes).documentElement
        fun f(name: String): String? {
            val nodes = root.childNodes
            for (i in 0 until nodes.length) {
                val n = nodes.item(i)
                if (n is Element && n.localName.equals(name, true) || n.nodeName.equals(name, true)) return clean(n.textContent)
            }
            return null
        }
        val m = JSONObject()
        fun put(k: String, v: String?) { if (v != null) m.put(k, v) }
        put("title", f("Title")); put("series", f("Series")); put("number", f("Number")); put("volume", f("Volume"))
        put("year", f("Year")?.takeIf { it != "-1" && it != "0" })
        put("writer", f("Writer")); put("artist", f("Penciller") ?: f("Artist") ?: f("CoverArtist"))
        put("publisher", f("Publisher")); put("summary", f("Summary")); put("genre", f("Genre")); put("characters", f("Characters"))
        f("PageCount")?.toIntOrNull()?.takeIf { it > 0 }?.let { m.put("pageCount", it) }
        m.takeIf { it.has("title") || it.has("series") || it.has("number") || it.has("writer") || it.has("summary") || it.has("year") }
    }.getOrNull()

    private fun readOpf(texts: Map<String, ByteArray>): Pair<String?, JSONObject?> = runCatching {
        val container = texts.entries.firstOrNull { it.key.equals("META-INF/container.xml", true) }?.value
        val opfPath = container?.let {
            val rf = xml(it).getElementsByTagNameNS("*", "rootfile")
            if (rf.length > 0) (rf.item(0) as Element).getAttribute("full-path") else null
        } ?: texts.keys.firstOrNull { it.endsWith(".opf", true) } ?: return null to null
        val doc = xml(texts[opfPath] ?: return null to null)
        fun all(tag: String) = doc.getElementsByTagNameNS("*", tag).let { l -> List(l.length) { l.item(it) as Element } }
        fun dc(tag: String) = clean(all(tag).firstOrNull()?.textContent)
        val metas = all("meta")
        fun metaName(n: String) = metas.firstOrNull { it.getAttribute("name") == n }?.getAttribute("content")
        fun metaProp(p: String) = clean(metas.firstOrNull { it.getAttribute("property") == p }?.textContent)

        val m = JSONObject()
        fun put(k: String, v: String?) { if (!v.isNullOrBlank()) m.put(k, v) }
        put("title", dc("title"))
        put("writer", all("creator").mapNotNull { clean(it.textContent) }.distinct().joinToString(", "))
        put("publisher", dc("publisher")); put("summary", dc("description")); put("genre", dc("subject"))
        dc("date")?.takeIf { it.length >= 4 && it[0].isDigit() }?.let { put("year", it.take(4)) }
        put("series", clean(metaName("calibre:series")) ?: metaProp("belongs-to-collection"))
        put("number", clean(metaName("calibre:series_index"))?.removeSuffix(".0") ?: metaProp("group-position"))

        val items = all("item")
        val item = items.firstOrNull { it.getAttribute("properties").contains("cover-image") }
            ?: metaName("cover")?.let { id -> items.firstOrNull { it.getAttribute("id") == id } }
        val href = item?.getAttribute("href")?.takeIf { it.isNotEmpty() }?.let {
            val base = opfPath.substringBeforeLast('/', "")
            URLDecoder.decode(if (base.isEmpty()) it else "$base/$it", "UTF-8")
        }
        href to m.takeIf { it.length() > 0 }
    }.getOrDefault(null to null)
}

/** After each scan, prepares covers and details for the whole library in the background. */
object Enricher {
    private val pool = Executors.newFixedThreadPool(3)
    private var job: List<Future<*>> = emptyList()
    private val generation = AtomicInteger()
    val done = AtomicInteger()
    @Volatile var total = 0
    @Volatile var running = false

    @Synchronized
    fun restart() {
        val gen = generation.incrementAndGet()
        job.forEach { it.cancel(false) }
        // On a phone, opening every comic over Wi-Fi would take hours and slow down reading:
        // in the background only the covers already on the NAS are fetched. The others are
        // made when they appear on screen.
        SharedCache.pullMeta()
        val shared = SharedCache.remote()
        val books = Library.books.filter { Covers.needsWork(it) && (it.id in shared || !Fs.isSmb(it.path)) }
            .sortedWith(compareBy<Book>({ if (it.sorted) 0 else 1 }, { if (it.kind == "comic" || it.kind == "manga") 0 else 1 }, { it.path.lowercase() }))
        done.set(0)
        total = books.size
        running = books.isNotEmpty()
        val left = AtomicInteger(books.size)
        job = books.map { b ->
            pool.submit {
                if (generation.get() != gen) return@submit
                while (Books.isBusy || Books.isReading) Thread.sleep(500)
                runCatching { Covers.ensure(b) }
                done.incrementAndGet()
                if (left.decrementAndGet() == 0) { running = false; MetaStore.flush() }
            }
        }
    }
}
