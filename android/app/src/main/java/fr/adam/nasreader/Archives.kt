package fr.adam.nasreader

import me.zhanghai.android.libarchive.Archive
import me.zhanghai.android.libarchive.ArchiveEntry
import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.io.OutputStream
import java.nio.ByteBuffer

/**
 * Reads ZIP, RAR (4 and 5) and 7z through libarchive, pulling bytes from a RandomFile so the
 * archive never has to be copied to the phone first.
 */
class ArchiveReader(private val rf: RandomFile, chunk: Int = 256 * 1024) : Closeable {
    class Entry(val name: String, val isFile: Boolean, val size: Long)

    private val a: Long = Archive.readNew()
    private var pos = 0L
    private val tmp = ByteArray(chunk)
    private val inBuf: ByteBuffer = ByteBuffer.allocateDirect(chunk)
    private val outBuf: ByteBuffer = ByteBuffer.allocateDirect(256 * 1024)
    private val outArr = ByteArray(256 * 1024)

    init {
        try {
            Archive.readSupportFilterAll(a)
            Archive.readSupportFormatAll(a)
            Archive.readSetReadCallback(a, Archive.ReadCallback<Any?> { _, _ ->
                val n = rf.read(pos, tmp, 0, tmp.size)
                inBuf.clear()
                if (n > 0) {
                    pos += n
                    inBuf.put(tmp, 0, n)
                }
                inBuf.flip()
                inBuf
            })
            Archive.readSetSeekCallback(a, Archive.SeekCallback<Any?> { _, _, offset, whence ->
                pos = when (whence) {
                    0 -> offset
                    1 -> pos + offset
                    2 -> rf.size + offset
                    else -> pos
                }
                pos
            })
            Archive.readSetSkipCallback(a, Archive.SkipCallback<Any?> { _, _, request ->
                val n = minOf(request, rf.size - pos).coerceAtLeast(0)
                pos += n
                n
            })
            Archive.readSetCallbackData<Any?>(a, null)
            Archive.readOpen1(a)
        } catch (e: Exception) {
            runCatching { Archive.readFree(a) }
            throw e
        }
    }

    fun next(): Entry? {
        val e = Archive.readNextHeader(a)
        if (e == 0L) return null
        val name = ArchiveEntry.pathnameUtf8(e) ?: ArchiveEntry.pathname(e)?.let { String(it, Charsets.ISO_8859_1) } ?: ""
        val isFile = ArchiveEntry.filetype(e) == ArchiveEntry.AE_IFREG || (!ArchiveEntry.filetypeIsSet(e) && !name.endsWith("/"))
        return Entry(name, isFile, if (ArchiveEntry.sizeIsSet(e)) ArchiveEntry.size(e) else -1)
    }

    fun copyTo(out: OutputStream) {
        while (true) {
            outBuf.clear()
            Archive.readData(a, outBuf)
            outBuf.flip()
            val n = outBuf.remaining()
            if (n == 0) break
            outBuf.get(outArr, 0, n)
            out.write(outArr, 0, n)
        }
    }

    fun bytes(): ByteArray = ByteArrayOutputStream().also { copyTo(it) }.toByteArray()

    fun skip() = Archive.readDataSkip(a)

    override fun close() {
        runCatching { Archive.readFree(a) }
    }
}

object Pages {
    private val imageExt = setOf("jpg", "jpeg", "png", "gif", "webp", "bmp", "avif")

    fun isPage(name: String): Boolean {
        val base = name.substringAfterLast('/').substringAfterLast('\\')
        if (base.startsWith("._") || name.contains("__MACOSX")) return false
        return base.substringAfterLast('.', "").lowercase() in imageExt
    }

    fun isComicInfo(name: String) = name.substringAfterLast('/').substringAfterLast('\\').equals("ComicInfo.xml", ignoreCase = true)

    private val digits = Regex("\\d+")

    /** Sort key so that "page 2" comes before "page 10". */
    fun naturalKey(s: String) = digits.replace(s.replace('\\', '/').lowercase()) { it.value.padStart(10, '0') }

    fun mime(name: String) = when (name.substringAfterLast('.').lowercase()) {
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "gif" -> "image/gif"
        "webp" -> "image/webp"
        "bmp" -> "image/bmp"
        "avif" -> "image/avif"
        "pdf" -> "application/pdf"
        "epub" -> "application/epub+zip"
        "js" -> "text/javascript"
        "css" -> "text/css"
        "html" -> "text/html; charset=utf-8"
        else -> "application/octet-stream"
    }
}
