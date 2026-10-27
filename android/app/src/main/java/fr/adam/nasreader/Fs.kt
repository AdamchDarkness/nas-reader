package fr.adam.nasreader

import com.hierynomus.msdtyp.AccessMask
import com.hierynomus.msfscc.FileAttributes
import com.hierynomus.mssmb2.SMB2CreateDisposition
import com.hierynomus.mssmb2.SMB2ShareAccess
import com.hierynomus.smbj.SMBClient
import com.hierynomus.smbj.SmbConfig
import com.hierynomus.smbj.auth.AuthenticationContext
import com.hierynomus.smbj.session.Session
import com.hierynomus.smbj.share.DiskShare
import java.io.Closeable
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.RandomAccessFile
import java.util.EnumSet
import java.util.concurrent.TimeUnit

/** A file that can be read at any position: on the NAS or on the device. */
interface RandomFile : Closeable {
    val size: Long
    fun read(pos: Long, buf: ByteArray, off: Int, len: Int): Int

    fun readFully(pos: Long, buf: ByteArray, off: Int, len: Int): Int {
        var total = 0
        while (total < len) {
            val n = read(pos + total, buf, off + total, len - total)
            if (n <= 0) break
            total += n
        }
        return total
    }
}

data class FsEntry(val name: String, val isDir: Boolean, val size: Long, val mtime: Long, val hidden: Boolean)

/**
 * Books live at Windows-style paths (\\lord_of_adam\Share\dir\file.cbr) so that ids match the
 * PC app; local folders on the device are plain paths (/storage/...).
 */
object Fs {
    fun isSmb(path: String) = path.startsWith("\\\\")

    /** "\\nas\Share\a\b" → ("Share", "a\b") */
    fun splitSmb(path: String): Pair<String, String> {
        val parts = path.trimStart('\\').split('\\')
        return parts.getOrElse(1) { "" } to parts.drop(2).joinToString("\\")
    }

    fun list(dir: String): List<FsEntry> =
        if (isSmb(dir)) Smb.list(dir)
        else (File(dir).listFiles() ?: emptyArray()).map { FsEntry(it.name, it.isDirectory, it.length(), it.lastModified(), it.isHidden) }

    fun exists(dir: String): Boolean = if (isSmb(dir)) Smb.exists(dir) else File(dir).exists()

    fun open(path: String, size: Long): RandomFile =
        if (isSmb(path)) Smb.open(path, size) else LocalFile(File(path))

    fun child(dir: String, name: String) = if (isSmb(dir)) "${dir.trimEnd('\\')}\\$name" else File(dir, name).path
}

class LocalFile(file: File) : RandomFile {
    private val raf = RandomAccessFile(file, "r")
    override val size: Long = raf.length()

    @Synchronized
    override fun read(pos: Long, buf: ByteArray, off: Int, len: Int): Int {
        if (pos >= size) return -1
        raf.seek(pos)
        return raf.read(buf, off, len)
    }

    override fun close() = raf.close()
}

object Smb {
    private var client: SMBClient? = null
    private var session: Session? = null
    private val shares = HashMap<String, DiskShare>()

    @Synchronized
    fun reset() {
        shares.values.forEach { runCatching { it.close() } }
        shares.clear()
        runCatching { session?.close() }
        runCatching { client?.close() }
        session = null
        client = null
    }

    @Synchronized
    private fun session(): Session {
        session?.let { if (it.connection.isConnected) return it }
        reset()
        val st = Settings.current
        val c = SMBClient(
            SmbConfig.builder()
                .withTimeout(30, TimeUnit.SECONDS)
                .withSoTimeout(60, TimeUnit.SECONDS)
                .build()
        )
        client = c
        val hosts = listOf(st.host, "${st.nas}.local", st.nas).map { it.trim() }.filter { it.isNotEmpty() }.distinct()
        var last: Exception? = null
        for (h in hosts) {
            try {
                val conn = c.connect(h)
                val auth = if (st.user.isBlank()) AuthenticationContext.guest()
                else AuthenticationContext(st.user, st.password.toCharArray(), st.domain.ifBlank { null })
                try {
                    return conn.authenticate(auth).also { session = it }
                } catch (e: Exception) {
                    runCatching { conn.close() }
                    throw IOException(
                        if (st.user.isBlank()) "La NAS demande un identifiant. Renseigne ton nom d'utilisateur et ton mot de passe dans les Réglages."
                        else "La NAS refuse l'identifiant « ${st.user} ». Vérifie le mot de passe dans les Réglages.", e
                    )
                }
            } catch (e: IOException) {
                if (e.message?.startsWith("La NAS") == true) throw e
                last = e
            }
        }
        throw IOException("Impossible de joindre la NAS (${hosts.joinToString(", ")}). Vérifie que le téléphone est sur le Wi-Fi de la maison et l'adresse IP dans les Réglages.", last)
    }

    @Synchronized
    fun share(name: String): DiskShare {
        shares[name]?.let { if (it.isConnected) return it }
        val d = session().connectShare(name) as DiskShare
        shares[name] = d
        return d
    }

    /** Runs an SMB call, reconnecting once if the connection dropped (Wi-Fi sleep, NAS restart). */
    private fun <T> retry(block: () -> T): T = try {
        block()
    } catch (e: Exception) {
        if (e is IOException && e.message?.startsWith("La NAS") == true || e.message?.startsWith("Impossible de joindre") == true) throw e
        reset()
        block()
    }

    fun exists(dir: String): Boolean = runCatching {
        val (share, rel) = Fs.splitSmb(dir)
        retry { if (rel.isEmpty()) share(share).isConnected else share(share).folderExists(rel) }
    }.getOrElse { if (it.message?.startsWith("La NAS") == true || it.message?.startsWith("Impossible") == true) throw it else false }

    fun list(dir: String): List<FsEntry> {
        val (share, rel) = Fs.splitSmb(dir)
        return retry { share(share).list(rel) }
            .filter { it.fileName != "." && it.fileName != ".." }
            .map {
                val a = it.fileAttributes
                FsEntry(
                    it.fileName,
                    a and FileAttributes.FILE_ATTRIBUTE_DIRECTORY.value != 0L,
                    it.endOfFile,
                    it.lastWriteTime.toEpochMillis(),
                    a and (FileAttributes.FILE_ATTRIBUTE_HIDDEN.value or FileAttributes.FILE_ATTRIBUTE_SYSTEM.value) != 0L,
                )
            }
    }

    /** Reads a small file whole, or null when it does not exist. */
    fun readSmall(path: String): ByteArray? {
        val (share, rel) = Fs.splitSmb(path)
        if (!retry { share(share).fileExists(rel) }) return null
        return open(path, 0).use { rf ->
            val b = ByteArray(rf.size.toInt())
            val n = rf.readFully(0, b, 0, b.size)
            if (n == b.size) b else b.copyOf(n)
        }
    }

    /** Writes a temporary file next to the target, then renames it over the target. */
    fun writeSmall(dir: String, name: String, bytes: ByteArray, tmpName: String) {
        val (share, relDir) = Fs.splitSmb(dir)
        retry {
            val d = share(share)
            if (relDir.isNotEmpty() && !d.folderExists(relDir)) {
                var acc = ""
                for (part in relDir.split('\\')) {
                    acc = if (acc.isEmpty()) part else "$acc\\$part"
                    if (!d.folderExists(acc)) d.mkdir(acc)
                }
            }
            val tmpRel = if (relDir.isEmpty()) tmpName else "$relDir\\$tmpName"
            val finalRel = if (relDir.isEmpty()) name else "$relDir\\$name"
            d.openFile(
                tmpRel, EnumSet.of(AccessMask.GENERIC_WRITE, AccessMask.GENERIC_READ, AccessMask.DELETE), null,
                SMB2ShareAccess.ALL, SMB2CreateDisposition.FILE_OVERWRITE_IF, null,
            ).use { f ->
                f.write(bytes, 0)
                f.rename(finalRel, true)
            }
        }
    }

    fun open(path: String, size: Long): RandomFile {
        val (share, rel) = Fs.splitSmb(path)
        val f = retry {
            share(share).openFile(rel, EnumSet.of(AccessMask.GENERIC_READ), null, SMB2ShareAccess.ALL, SMB2CreateDisposition.FILE_OPEN, null)
        }
        val real = runCatching { f.fileInformation.standardInformation.endOfFile }.getOrDefault(size)
        return SmbFile(f, real)
    }
}

class SmbFile(private val f: com.hierynomus.smbj.share.File, override val size: Long) : RandomFile {
    override fun read(pos: Long, buf: ByteArray, off: Int, len: Int): Int {
        if (pos >= size) return -1
        val want = minOf(len.toLong(), size - pos).toInt()
        val n = f.read(buf, pos, off, want)
        return if (n <= 0) -1 else n
    }

    override fun close() = runCatching { f.close() }.let { }
}

/** Keeps recently read blocks: archive and PDF readers jump around in small reads. */
class CachedFile(private val inner: RandomFile, private val block: Int = 256 * 1024, private val maxBlocks: Int = 32) : RandomFile {
    override val size get() = inner.size
    private val cache = object : LinkedHashMap<Long, ByteArray>(maxBlocks, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Long, ByteArray>?) = size > maxBlocks
    }

    @Synchronized
    override fun read(pos: Long, buf: ByteArray, off: Int, len: Int): Int {
        if (pos >= size) return -1
        val idx = pos / block
        val data = cache[idx] ?: run {
            val start = idx * block
            val n = minOf(block.toLong(), size - start).toInt()
            val b = ByteArray(n)
            val got = inner.readFully(start, b, 0, n)
            (if (got == n) b else b.copyOf(got)).also { cache[idx] = it }
        }
        val inBlock = (pos - idx * block).toInt()
        if (inBlock >= data.size) return -1
        val n = minOf(len, data.size - inBlock)
        System.arraycopy(data, inBlock, buf, off, n)
        return n
    }

    override fun close() = inner.close()
}

/** Counts what is read through it: used to log how much a book costs to open. */
class CountingFile(private val inner: RandomFile) : RandomFile by inner {
    @Volatile var bytes = 0L
    @Volatile var calls = 0
    override fun read(pos: Long, buf: ByteArray, off: Int, len: Int): Int =
        inner.read(pos, buf, off, len).also { if (it > 0) { bytes += it; calls++ } }
    override fun readFully(pos: Long, buf: ByteArray, off: Int, len: Int): Int {
        var total = 0
        while (total < len) {
            val n = read(pos + total, buf, off + total, len - total)
            if (n <= 0) break
            total += n
        }
        return total
    }
    override fun close() = inner.close()
}

/** Reads a RandomFile from start to end in big chunks. */
class SeqStream(private val rf: RandomFile, private var pos: Long = 0) : InputStream() {
    override fun read(): Int {
        val b = ByteArray(1)
        return if (read(b, 0, 1) <= 0) -1 else b[0].toInt() and 0xff
    }

    override fun read(b: ByteArray, off: Int, len: Int): Int {
        val n = rf.read(pos, b, off, len)
        if (n > 0) pos += n
        return n
    }
}
