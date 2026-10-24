package fr.adam.nasreader

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings as AndroidSettings
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Looks for a newer APK in the GitHub releases and downloads it in the background.
 * Android always asks the user to confirm the installation.
 */
object Updater {
    const val REPO = "AdamchDarkness/nas-reader"
    private val exec = Executors.newSingleThreadScheduledExecutor()

    @Volatile var state = "none" // none | available | downloading | ready | error
    @Volatile var latest: String? = null
    @Volatile var notes: String? = null
    @Volatile var page: String? = null
    @Volatile var error: String? = null
    private var apk: File? = null
    /** Set while the user is in Android's "install unknown apps" screen: install on return. */
    @Volatile var resumeInstall = false

    val current: String by lazy {
        App.ctx.packageManager.getPackageInfo(App.ctx.packageName, 0).versionName ?: "0"
    }

    fun start() {
        exec.scheduleWithFixedDelay({ runCatching { check() } }, 20, 6 * 3600, TimeUnit.SECONDS)
    }

    private fun parse(v: String) = v.trimStart('v', 'V').substringBefore('-').split('.').map { it.toIntOrNull() ?: 0 }.let { it + List(3) { 0 } }.take(3)

    private fun newer(a: String, b: String): Boolean {
        val x = parse(a)
        val y = parse(b)
        for (i in 0..2) if (x[i] != y[i]) return x[i] > y[i]
        return false
    }

    private fun get(url: String): HttpURLConnection = (URL(url).openConnection() as HttpURLConnection).apply {
        setRequestProperty("User-Agent", "NasReader-Android/$current")
        setRequestProperty("Accept", "application/vnd.github+json")
        connectTimeout = 20_000
        readTimeout = 60_000
        instanceFollowRedirects = true
    }

    @Synchronized
    fun check() {
        if (state == "downloading" || state == "ready") return
        try {
            val rel = get("https://api.github.com/repos/$REPO/releases/latest").let { c ->
                c.inputStream.use { JSONObject(it.readBytes().decodeToString()) }
            }
            val tag = rel.getString("tag_name")
            if (!newer(tag, current)) { state = "none"; return }
            latest = tag.trimStart('v', 'V')
            notes = rel.optString("body")
            page = rel.optString("html_url")
            state = "available"
            val assets = rel.getJSONArray("assets")
            val asset = (0 until assets.length()).map { assets.getJSONObject(it) }.firstOrNull { it.getString("name").endsWith(".apk", true) } ?: return
            if (!Settings.current.autoUpdate) return

            state = "downloading"
            val dir = File(App.ctx.cacheDir, "updates").apply { mkdirs() }
            dir.listFiles()?.filter { it.name != asset.getString("name") }?.forEach { it.delete() }
            val f = File(dir, asset.getString("name"))
            if (f.length() != asset.getLong("size")) {
                val part = File(dir, "${f.name}.part")
                get(asset.getString("browser_download_url")).inputStream.use { i -> part.outputStream().use { i.copyTo(it, 1 shl 16) } }
                part.renameTo(f)
            }
            apk = f
            state = "ready"
        } catch (e: Exception) {
            error = e.message
            if (state == "downloading") state = "available"
        }
    }

    /** Opens Android's installer. The first time, Android asks to allow installs from Nas Reader. */
    fun install(): Boolean {
        val f = apk ?: return false
        val ctx = App.ctx
        if (Build.VERSION.SDK_INT >= 26 && !ctx.packageManager.canRequestPackageInstalls()) {
            resumeInstall = true
            ctx.startActivity(
                Intent(AndroidSettings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${ctx.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
            return true
        }
        resumeInstall = false
        val uri = FileProvider.getUriForFile(ctx, "${ctx.packageName}.updates", f)
        ctx.startActivity(
            Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        )
        return true
    }

    fun status(): JSONObject = JSONObject()
        .put("current", current).put("latest", latest ?: JSONObject.NULL).put("state", state)
        .put("notes", notes ?: JSONObject.NULL).put("page", page ?: JSONObject.NULL).put("error", error ?: JSONObject.NULL)
        .put("installed", true).put("auto", Settings.current.autoUpdate).put("android", true)
}
