package fr.adam.nasreader

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout

class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var frame: FrameLayout
    private var immersive = false

    companion object {
        private var server: Server? = null

        @Synchronized
        fun server(): Server = server ?: Server().also {
            it.start(60_000, true)
            server = it
            Thread {
                // The PC's scan, shared on the NAS, is much faster than scanning from the phone.
                val shared = Library.adoptShared()
                if (!shared && (Settings.current.scanOnStartup || Library.books.isEmpty())) Library.startScan()
                else if (Library.books.isNotEmpty()) Enricher.restart()
            }.start()
            Sync.start()
            Updater.start()
        }
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        App.init(this)
        window.statusBarColor = Color.rgb(13, 15, 20)
        window.navigationBarColor = Color.rgb(13, 15, 20)

        web = WebView(this).apply {
            setBackgroundColor(Color.rgb(13, 15, 20))
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.mediaPlaybackRequiresUserGesture = true
            settings.allowFileAccess = false
            settings.setSupportZoom(false)
            settings.builtInZoomControls = false
            addJavascriptInterface(Bridge(), "NasAndroid")
            webViewClient = object : WebViewClient() {
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val u = request.url
                    if (u.host == "127.0.0.1") return false
                    runCatching { startActivity(Intent(Intent.ACTION_VIEW, u)) }
                    return true
                }
            }
        }
        // WebView ignores its own padding: a frame around it keeps the interface clear of the
        // status and navigation bars (Android 15 always draws edge to edge).
        frame = FrameLayout(this).apply {
            setBackgroundColor(Color.rgb(13, 15, 20))
            addView(web, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            setOnApplyWindowInsetsListener { v, insets ->
                if (immersive) v.setPadding(0, 0, 0, 0)
                else if (Build.VERSION.SDK_INT >= 30) {
                    val i = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout() or WindowInsets.Type.ime())
                    v.setPadding(i.left, i.top, i.right, i.bottom)
                } else {
                    @Suppress("DEPRECATION")
                    v.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
                }
                insets
            }
        }
        setContentView(frame)
        frame.requestApplyInsets()

        Thread {
            val url = server().url
            runOnUiThread { web.loadUrl(url) }
        }.start()
    }

    override fun onResume() {
        super.onResume()
        // Back from "allow installs from Nas Reader": carry on with the update.
        if (Updater.resumeInstall && (Build.VERSION.SDK_INT < 26 || packageManager.canRequestPackageInstalls())) Updater.install()
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        web.evaluateJavascript("window.nasBack ? nasBack() : false") { handled ->
            @Suppress("DEPRECATION")
            if (handled != "true") super.onBackPressed()
        }
    }

    private fun applyImmersive(on: Boolean) {
        immersive = on
        if (on) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= 30) {
            val c = window.insetsController ?: return
            if (on) {
                c.hide(WindowInsets.Type.systemBars())
                c.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            } else c.show(WindowInsets.Type.systemBars())
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = if (on)
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            else 0
        }
        frame.requestApplyInsets()
    }

    /** Called from the interface. */
    inner class Bridge {
        @JavascriptInterface
        fun immersive(on: Boolean) = runOnUiThread { applyImmersive(on) }
    }
}
