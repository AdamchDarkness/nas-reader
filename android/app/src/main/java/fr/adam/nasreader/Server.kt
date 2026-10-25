package fr.adam.nasreader

import fi.iki.elonen.NanoHTTPD
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.IOException

/** Serves the shared interface and the same /api routes as the Windows app, on 127.0.0.1. */
class Server : NanoHTTPD("127.0.0.1", 0) {
    val url get() = "http://127.0.0.1:$listeningPort/"

    override fun serve(s: IHTTPSession): Response = try {
        route(s)
    } catch (e: Exception) {
        json(JSONObject().put("error", friendly(e)), Response.Status.INTERNAL_ERROR)
    }

    private fun body(s: IHTTPSession): String {
        val m = HashMap<String, String>()
        s.parseBody(m)
        return m["postData"] ?: ""
    }

    private fun route(s: IHTTPSession): Response {
        val p = s.uri.split('/').filter { it.isNotEmpty() }
        val get = s.method == Method.GET
        val post = s.method == Method.POST
        return when {
            p.isEmpty() -> index()
            p[0] == "lib" && p.size == 2 -> asset("lib/${p[1]}")
            p[0] != "api" -> notFound()
            get && p == listOf("api", "library") -> json(Library.json().put("meta", MetaStore.filled()))
            get && p == listOf("api", "meta") -> json(MetaStore.filled())
            get && p == listOf("api", "status") -> json(
                JSONObject()
                    .put("scanning", Library.scanning.get()).put("found", Library.found).put("current", Library.current)
                    .put("error", Library.error ?: JSONObject.NULL).put("scannedAt", Library.scannedAt).put("count", Library.books.size)
                    .put("enriching", Enricher.running).put("enrichDone", Enricher.done.get()).put("enrichTotal", Enricher.total)
                    .put("sync", syncJson())
                    .put("nas", Reach.nas())
                    .put("update", Updater.status())
            )
            get && p == listOf("api", "offline") -> json(Offline.status())
            post && p == listOf("api", "offline") -> { Offline.enqueue(Json.strings(JSONArray(body(s)))); json(Offline.status()) }
            post && p == listOf("api", "offline-remove") -> { Offline.remove(Json.strings(JSONArray(body(s)))); json(Offline.status()) }
            post && p == listOf("api", "sync") -> { runCatching { Sync.runNow() }; json(syncJson()) }
            get && p == listOf("api", "update") -> json(Updater.status())
            post && p == listOf("api", "update", "check") -> { Updater.check(); json(Updater.status()) }
            post && p == listOf("api", "update", "install") ->
                if (Updater.install()) ok() else json(JSONObject().put("error", "Aucune mise à jour prête."), Response.Status.CONFLICT)
            post && p == listOf("api", "scan") -> { Smb.reset(); Library.startScan(); ok() }
            get && p == listOf("api", "settings") -> {
                val st = Settings.current
                json(
                    JSONObject().put("settings", st.toJson())
                        .put("shares", JSONArray((st.shares() + st.roots).distinct()))
                        .put("cacheBytes", Books.cacheSize()).put("dataFolder", "le stockage de l'application")
                        .put("syncFolder", st.syncPath())
                )
            }
            post && p == listOf("api", "settings") -> {
                Settings.update(Settings.parse(JSONObject(body(s)), Settings.current))
                Smb.reset()
                Sync.changed()
                ok()
            }
            post && p == listOf("api", "cache", "clear") -> { Books.clearCache(); ok() }
            get && p.size == 3 && p[1] == "cover" -> {
                val b = Library.get(p[2]) ?: return notFound()
                val f = Covers.get(b) ?: return notFound()
                file(f, "image/jpeg", cache = true)
            }
            p.size >= 4 && p[1] == "book" -> book(s, p)
            get && p == listOf("api", "userdata") -> json(UserData.snapshot())
            post && p.size == 3 && p[1] == "progress" -> {
                UserData.setProgress(p[2], Progress.from(JSONObject(body(s)))); ok()
            }
            s.method == Method.DELETE && p.size == 3 && p[1] == "progress" -> { UserData.remove(listOf(p[2])); ok() }
            post && p == listOf("api", "progress-batch") -> {
                val a = JSONArray(body(s))
                UserData.setMany(List(a.length()) { a.getJSONObject(it).let { o -> Triple(o.getString("id"), o.getInt("page"), o.getInt("pages")) } })
                ok()
            }
            post && p == listOf("api", "progress-clear") -> { UserData.remove(Json.strings(JSONArray(body(s)))); ok() }
            post && p.size == 4 && p[1] == "lists" -> {
                if (UserData.setMark(p[2], p[3], s.parameters["on"]?.firstOrNull() != "0")) ok() else notFound()
            }
            else -> notFound()
        }
    }

    private fun book(s: IHTTPSession, p: List<String>): Response {
        val b = Library.get(p[2]) ?: return notFound()
        if (p[3] in setOf("open", "file") && Fs.isSmb(b.path) && Offline.file(b) == null && !Reach.nas())
            return json(JSONObject().put("error", "Ce livre n'est pas téléchargé sur l'appareil et la NAS n'est pas joignable. Il s'ouvrira de retour à la maison."), Response.Status.SERVICE_UNAVAILABLE)
        return when (p[3]) {
            "open" -> {
                val ob = Books.get(b)
                json(JSONObject().put("pages", ob.count).put("fmt", b.fmt))
            }
            "page" -> {
                val ob = Books.get(b)
                val n = p.getOrNull(4)?.toIntOrNull() ?: return notFound()
                if (n < 0 || n >= ob.count) return notFound()
                val f = ob.page(n)
                file(f, Pages.mime(f.name), cache = true)
            }
            "prefetch" -> { Books.prefetch(b); ok() }
            "file" -> { val f = Books.localCopy(b); file(f, Pages.mime(f.name), cache = false) }
            else -> notFound()
        }
    }

    private fun syncJson() = JSONObject()
        .put("enabled", Settings.current.syncEnabled).put("folder", Settings.current.syncPath())
        .put("last", Sync.lastSync).put("error", Sync.lastError ?: JSONObject.NULL).put("version", Sync.dataVersion)

    private fun index(): Response {
        val body = App.ctx.assets.open("index.html").bufferedReader().readText()
        // index.html is written as an artifact fragment; give it a full document here.
        val html = "<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">" +
            "<style>html,body{margin:0}img{max-width:100%}[hidden]{display:none!important}</style>" +
            "<script>window.NAS_APP=true;window.NAS_ANDROID=true</script></head><body>" + body + "</body></html>"
        return newFixedLengthResponse(Response.Status.OK, "text/html; charset=utf-8", html)
    }

    private fun asset(path: String): Response = try {
        val bytes = App.ctx.assets.open(path).use { it.readBytes() }
        newFixedLengthResponse(Response.Status.OK, Pages.mime(path), bytes.inputStream(), bytes.size.toLong())
    } catch (e: IOException) {
        notFound()
    }

    private fun file(f: File, mime: String, cache: Boolean): Response =
        newFixedLengthResponse(Response.Status.OK, mime, FileInputStream(f), f.length()).apply {
            if (cache) addHeader("Cache-Control", "public, max-age=604800")
        }

    private fun json(o: Any, status: Response.Status = Response.Status.OK) =
        newFixedLengthResponse(status, "application/json; charset=utf-8", o.toString())

    private fun ok() = newFixedLengthResponse(Response.Status.OK, "text/plain", "")
    private fun notFound() = newFixedLengthResponse(Response.Status.NOT_FOUND, "text/plain", "")

    private fun friendly(e: Throwable): String = when {
        e.message?.startsWith("La NAS") == true || e.message?.startsWith("Impossible") == true -> e.message!!
        e is java.io.FileNotFoundException || e.message?.contains("STATUS_OBJECT_NAME_NOT_FOUND") == true -> "Ce fichier n'existe plus sur la NAS. Relance un scan."
        e.message?.contains("STATUS_ACCESS_DENIED") == true -> "Accès refusé à ce fichier sur la NAS."
        else -> e.message ?: e.toString()
    }
}
