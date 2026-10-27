package fr.adam.nasreader

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.pdf.PdfRenderer
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelFileDescriptor
import android.os.ProxyFileDescriptorCallback
import android.os.storage.StorageManager
import android.system.ErrnoException
import android.system.OsConstants
import org.json.JSONArray
import java.io.Closeable
import java.io.File
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** A book opened for reading: its pages as images, cached on the phone as they are produced. */
abstract class OpenBook(val book: Book) : Closeable {
    var count = 0
        protected set

    abstract fun page(index: Int): File
    override fun close() {}
}

object Books {
    private val open = LinkedHashMap<String, CompletableFuture<OpenBook>>()
    private val busy = AtomicInteger()
    private val pool = Executors.newCachedThreadPool()

    /** True while a book is being read from the NAS; background work waits for it. */
    val isBusy get() = busy.get() > 0

    /** A page was asked in the last 30 seconds: someone is reading, keep the Wi-Fi for them. */
    @Volatile private var lastPage = 0L
    val isReading get() = System.currentTimeMillis() - lastPage < 30_000
    fun reading() { lastPage = System.currentTimeMillis() }
    fun <T> io(block: () -> T): T {
        busy.incrementAndGet()
        try { return block() } finally { busy.decrementAndGet() }
    }

    fun openAsync(book: Book): CompletableFuture<OpenBook> = synchronized(open) {
        open[book.id]?.let { f ->
            if (!f.isCompletedExceptionally) {
                open.remove(book.id); open[book.id] = f // most recent last
                return f
            }
            open.remove(book.id)
        }
        val f = CompletableFuture.supplyAsync({ create(book) }, pool)
        open[book.id] = f
        while (open.size > 4) {
            val (id, old) = open.entries.first()
            open.remove(id)
            old.thenAccept { runCatching { it.close() } }
        }
        f
    }

    fun get(book: Book): OpenBook = try {
        openAsync(book).get(3, TimeUnit.MINUTES)
    } catch (e: java.util.concurrent.ExecutionException) {
        throw e.cause ?: e
    }

    private fun create(book: Book): OpenBook {
        pool.execute(::trimCache)
        val dir = File(App.pages, book.id).apply { mkdirs(); setLastModified(System.currentTimeMillis()) }
        return when (book.fmt) {
            "pdf" -> PdfBook(book, dir)
            "cbz", "cbr", "cb7" -> ArchiveBook(book, dir)
            else -> object : OpenBook(book) { override fun page(index: Int) = throw IOException("Pas de pages") }
        }
    }

    /** Closes a book's files, e.g. before deleting its downloaded copy. */
    fun forget(id: String) = synchronized(open) {
        open.remove(id)?.thenAccept { runCatching { it.close() } }
    }

    fun prefetch(book: Book) {
        if (book.fmt !in setOf("pdf", "cbz", "cbr", "cb7")) return
        pool.execute {
            runCatching {
                val ob = get(book)
                for (i in 0 until minOf(3, ob.count)) ob.page(i)
            }
        }
    }

    /** The file as it is on the NAS, or the local copy when there is one. */
    fun openFile(book: Book): RandomFile {
        Offline.file(book)?.let { return LocalFile(it) }
        val local = File(App.files, "${book.id}.${book.fmt}")
        return if (local.exists() && local.length() == book.size) LocalFile(local) else Fs.open(book.path, book.size)
    }

    /** EPUB and MOBI are handed to the reader as whole files, so they are copied first. */
    fun localCopy(book: Book): File {
        Offline.file(book)?.let { return it }
        val local = File(App.files, "${book.id}.${book.fmt}")
        if (local.exists() && local.length() == book.size) {
            local.setLastModified(System.currentTimeMillis()); return local
        }
        val part = File(App.files, "${book.id}.part")
        io {
            Fs.open(book.path, book.size).use { rf ->
                part.outputStream().use { SeqStream(rf).copyTo(it, 1 shl 20) }
            }
        }
        part.renameTo(local)
        return local
    }

    fun cacheSize(): Long = App.files.walk().filter { it.isFile }.sumOf { it.length() } + App.pages.walk().filter { it.isFile }.sumOf { it.length() }

    fun clearCache() {
        synchronized(open) {
            open.values.forEach { f -> f.thenAccept { runCatching { it.close() } } }
            open.clear()
        }
        App.files.listFiles()?.forEach { it.deleteRecursively() }
        App.pages.listFiles()?.forEach { it.deleteRecursively() }
    }

    private fun trimCache() = runCatching {
        val limit = Settings.current.cacheLimitGb.toLong() * 1024 * 1024 * 1024
        val items = HashMap<String, Pair<Long, Long>>() // id -> (size, lastUsed)
        fun add(id: String, size: Long, used: Long) {
            val cur = items[id]
            items[id] = (cur?.first ?: 0) + size to maxOf(cur?.second ?: 0, used)
        }
        App.files.listFiles()?.forEach { add(it.name.substringBefore('.'), it.length(), it.lastModified()) }
        App.pages.listFiles()?.forEach { d -> add(d.name, d.walk().filter { it.isFile }.sumOf { it.length() }, d.lastModified()) }
        var total = items.values.sumOf { it.first }
        val openIds = synchronized(open) { open.keys.toSet() }
        for ((id, v) in items.entries.sortedBy { it.value.second }) {
            if (total <= limit) break
            if (id in openIds) continue
            App.files.listFiles { f -> f.name.startsWith("$id.") }?.forEach { it.delete() }
            File(App.pages, id).deleteRecursively()
            total -= v.first
        }
    }
}

/**
 * CBZ / CBR / CB7 read straight from the NAS: the archive is streamed once and each page is
 * written to the cache as soon as it is decompressed. A page far ahead is pulled on its own.
 */
class ArchiveBook(book: Book, private val dir: File) : OpenBook(book) {
    private var keys: List<String> = emptyList()
    private var futures: Array<CompletableFuture<File>> = emptyArray()
    @Volatile private var seqIndex = 0
    @Volatile private var closed = false
    private val randomLock = Any()

    init {
        val index = File(dir, "index.json")
        val cached = runCatching { Json.strings(JSONArray(index.readText())) }.getOrNull()
        if (cached != null && cached.isNotEmpty() && cached.all { File(dir, it).exists() }) {
            futures = Array(cached.size) { CompletableFuture.completedFuture(File(dir, cached[it])) }
            count = cached.size
            seqIndex = Int.MAX_VALUE
        } else {
            val t0 = System.currentTimeMillis()
            val counter = CountingFile(Books.openFile(book))
            keys = Books.io {
                // Listing only reads the small entry headers spread through the file: small blocks.
                CachedFile(counter, 32 * 1024, 128).use { rf ->
                    ArchiveReader(rf, 32 * 1024).use { ar ->
                        val names = ArrayList<String>()
                        while (true) {
                            val e = ar.next() ?: break
                            if (e.isFile && Pages.isPage(e.name)) names.add(e.name)
                            ar.skip()
                        }
                        names.sortedBy { Pages.naturalKey(it) }
                    }
                }
            }
            android.util.Log.i("NasReader", "list ${book.fmt} ${book.size / 1024} KB: read ${counter.bytes / 1024} KB in ${counter.calls} calls, ${System.currentTimeMillis() - t0} ms")
            if (keys.isEmpty()) throw IOException("Aucune image trouvée dans cette archive.")
            count = keys.size
            futures = Array(keys.size) { CompletableFuture<File>() }
            Thread(::extractAll).apply { name = "extract-${book.id}"; isDaemon = true; start() }
        }
    }

    private fun pagePath(i: Int) = File(dir, "%04d.%s".format(i, keys[i].substringAfterLast('.').lowercase()))

    private fun save(i: Int, ar: ArchiveReader) {
        if (futures[i].isDone) { ar.skip(); return }
        val f = pagePath(i)
        val tmp = File(dir, "${f.name}.${Thread.currentThread().id}.tmp")
        tmp.outputStream().buffered(1 shl 16).use { ar.copyTo(it) }
        if (!tmp.renameTo(f)) tmp.delete()
        futures[i].complete(f)
    }

    private fun extractAll() {
        val order = keys.withIndex().associate { it.value to it.index }
        try {
            Books.io {
                Books.openFile(book).use { rf ->
                    ArchiveReader(rf, 1 shl 20).use { ar ->
                        while (!closed) {
                            val e = ar.next() ?: break
                            seqIndex++
                            when {
                                !e.isFile -> ar.skip()
                                Pages.isComicInfo(e.name) -> Covers.parseComicInfo(ar.bytes())?.let { MetaStore.set(book.id, it) }
                                else -> order[e.name]?.let { save(it, ar) } ?: ar.skip()
                            }
                        }
                    }
                }
            }
            if (!closed) File(dir, "index.json").writeText(JSONArray(futures.map { if (it.isDone && !it.isCompletedExceptionally) it.get().name else null }).toString())
        } catch (e: Exception) {
            futures.forEach { it.completeExceptionally(e) }
        }
        futures.forEach { it.completeExceptionally(IOException("Page illisible dans l'archive.")) }
    }

    override fun page(index: Int): File {
        val f = futures[index]
        if (!f.isDone) {
            runCatching { return f.get(250, TimeUnit.MILLISECONDS) }
            // Far ahead of the sequential reader (slider jump): fetch this page on its own.
            if (index > seqIndex + 6) synchronized(randomLock) {
                if (!f.isDone) runCatching {
                    Books.io {
                        CachedFile(Books.openFile(book)).use { rf ->
                            ArchiveReader(rf).use { ar ->
                                while (true) {
                                    val e = ar.next() ?: break
                                    if (e.name == keys[index]) { save(index, ar); break }
                                    ar.skip()
                                }
                            }
                        }
                    }
                }
            }
        }
        return try {
            f.get(3, TimeUnit.MINUTES)
        } catch (e: java.util.concurrent.ExecutionException) {
            throw e.cause ?: e
        }
    }

    override fun close() { closed = true }
}

/** Windows-like access to a NAS file for Android's PDF engine, without copying it. */
class ProxyFd(rf: RandomFile, tag: String) : Closeable {
    private val thread = HandlerThread("pdf-$tag").apply { start() }
    val fd: ParcelFileDescriptor = App.ctx.getSystemService(StorageManager::class.java).openProxyFileDescriptor(
        ParcelFileDescriptor.MODE_READ_ONLY,
        object : ProxyFileDescriptorCallback() {
            override fun onGetSize() = rf.size
            override fun onRead(offset: Long, size: Int, data: ByteArray): Int = try {
                rf.readFully(offset, data, 0, size)
            } catch (e: Exception) {
                throw ErrnoException("read", OsConstants.EIO)
            }
            override fun onRelease() {
                runCatching { rf.close() }
                thread.quitSafely()
            }
        },
        Handler(thread.looper),
    )

    override fun close() = runCatching { fd.close() }.let { }
}

class PdfBook(book: Book, private val dir: File) : OpenBook(book) {
    private val proxy = ProxyFd(CachedFile(Books.openFile(book), 256 * 1024, 64), book.id)
    private val renderer = try {
        PdfRenderer(proxy.fd)
    } catch (e: Exception) {
        proxy.close()
        throw IOException("Ce PDF n'a pas pu être ouvert (protégé ou abîmé).", e)
    }

    init { count = renderer.pageCount }

    override fun page(index: Int): File {
        val out = File(dir, "p%04d.jpg".format(index))
        if (out.exists()) return out
        synchronized(renderer) {
            if (out.exists()) return out
            Books.io {
                renderer.openPage(index).use { p ->
                    // Landscape pages are usually double spreads: give them more pixels.
                    val w = if (p.width > p.height) 2400 else 1600
                    val h = (w.toLong() * p.height / maxOf(1, p.width)).toInt()
                    val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
                    bmp.eraseColor(Color.WHITE)
                    p.render(bmp, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                    val tmp = File(dir, "${out.name}.tmp")
                    tmp.outputStream().use { bmp.compress(Bitmap.CompressFormat.JPEG, 85, it) }
                    bmp.recycle()
                    tmp.renameTo(out)
                }
            }
        }
        return out
    }

    override fun close() {
        synchronized(renderer) { runCatching { renderer.close() } }
        proxy.close()
    }
}
