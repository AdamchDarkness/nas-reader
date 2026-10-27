using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NasReader.Server;

public class AppHost
{
    WebApplication _app = null!;
    public string Url { get; private set; } = "";

    public static AppHost Start()
    {
        Paths.Ensure();
        var host = new AppHost();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        int.TryParse(Environment.GetEnvironmentVariable("NASREADER_PORT"), out var port);
        builder.WebHost.UseKestrel(o => o.Listen(System.Net.IPAddress.Loopback, port));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
        });
        var app = builder.Build();
        Map(app);
        app.StartAsync().GetAwaiter().GetResult();
        host._app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        host.Url = address.Replace("[::1]", "127.0.0.1") + "/";

        if (Library.Snapshot.Books.Count > 0) Enricher.Restart();
        Sync.Start();
        SharedCache.Start();
        Updater.Start();
        if (Settings.Current.ScanOnStartup || Library.Snapshot.Books.Count == 0) Library.StartScan();
        return host;
    }

    public void Stop()
    {
        try { _app.StopAsync().Wait(2000); } catch { }
    }

    static readonly Dictionary<string, string> Resources = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(n => n.StartsWith("www/"))
        .ToDictionary(n => n[4..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    static IResult Embedded(string path)
    {
        if (!Resources.TryGetValue(path, out var name)) return Results.NotFound();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        var mime = Path.GetExtension(path) switch
        {
            ".js" => "text/javascript", ".css" => "text/css", ".svg" => "image/svg+xml",
            ".png" => "image/png", ".html" => "text/html; charset=utf-8", _ => "application/octet-stream",
        };
        return Results.Stream(stream, mime);
    }

    static void Map(WebApplication app)
    {
        app.MapGet("/", () =>
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resources["index.html"])!;
            var body = new StreamReader(s).ReadToEnd();
            // index.html is written as an artifact fragment; give it a full document here.
            var html = "<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\">" +
                       "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                       "<style>html,body{margin:0}img{max-width:100%}[hidden]{display:none!important}</style>" +
                       "<script>window.NAS_APP=true</script></head><body>" + body + "</body></html>";
            return Results.Content(html, "text/html; charset=utf-8");
        });
        app.MapGet("/lib/{file}", (string file) => Embedded("lib/" + file));

        app.MapGet("/api/library", () =>
        {
            var snap = Library.Snapshot;
            return Results.Json(new { books = snap.Books, scannedAt = snap.ScannedAt, meta = MetaStore.Filled() });
        });

        app.MapGet("/api/meta", () => Results.Json(MetaStore.Filled()));

        app.MapGet("/api/status", () => Results.Json(new
        {
            scanning = Library.Scanning,
            found = Library.ScanFound,
            current = Library.ScanCurrent,
            error = Library.ScanError,
            scannedAt = Library.Snapshot.ScannedAt,
            count = Library.Snapshot.Books.Count,
            enriching = Enricher.Running,
            enrichDone = Enricher.Done,
            enrichTotal = Enricher.Total,
            sync = new { enabled = Settings.Current.SyncEnabled, folder = Sync.Folder(Settings.Current), last = Sync.LastSync, error = Sync.LastError, version = Sync.Version },
            update = Updater.Status(),
            nas = Reach.Nas(),
            shared = new { uploaded = SharedCache.Uploaded, pending = SharedCache.Pending },
        }));

        app.MapGet("/api/offline", () => Results.Json(Offline.Status()));
        app.MapPost("/api/offline", (List<string> ids) => { Offline.Enqueue(ids); return Results.Json(Offline.Status()); });
        app.MapPost("/api/offline-remove", (List<string> ids) => { Offline.Remove(ids); return Results.Json(Offline.Status()); });

        app.MapPost("/api/sync", async () => { await Sync.RunAsync(); return Results.Json(new { last = Sync.LastSync, error = Sync.LastError, version = Sync.Version }); });
        app.MapGet("/api/update", () => Results.Json(Updater.Status()));
        app.MapPost("/api/update/check", async () => { await Updater.CheckAsync(); return Results.Json(Updater.Status()); });
        app.MapPost("/api/update/install", () =>
        {
            if (!Updater.Install(relaunch: true)) return Results.Json(new { error = "Aucune mise à jour prête." }, statusCode: 409);
            var form = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault();
            form?.BeginInvoke(() => System.Windows.Forms.Application.Exit());
            return Results.Ok();
        });

        app.MapPost("/api/scan", () => { Library.StartScan(); return Results.Ok(); });

        app.MapGet("/api/settings", () =>
        {
            var s = Settings.Current;
            return Results.Json(new
            {
                settings = s,
                shares = Library.DiscoverShares(s.Nas),
                syncFolder = Sync.Folder(s),
                cacheBytes = Books.CacheSize(),
                dataFolder = Paths.Root,
            });
        });

        app.MapPost("/api/settings", (Settings s) =>
        {
            Settings.Update(s);
            _ = Sync.RunAsync();
            return Results.Ok();
        });

        app.MapPost("/api/cache/clear", () => { Books.ClearCache(); return Results.Ok(); });

        app.MapGet("/api/cover/{id}", async (string id, HttpContext ctx) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            var path = await Covers.GetAsync(book);
            if (path == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/book/{id}/open", async (string id) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            if (Offline.File_(book) == null && !Reach.Nas())
                return Results.Json(new { error = "Ce livre n'est pas téléchargé sur ce PC et la NAS n'est pas joignable. Il s'ouvrira de retour à la maison." }, statusCode: 503);
            try
            {
                var ob = await Books.OpenAsync(book);
                return Results.Json(new { pages = ob.Count, fmt = book.Fmt });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = Friendly(ex) }, statusCode: 500);
            }
        });

        app.MapGet("/api/book/{id}/page/{n:int}", async (string id, int n, HttpContext ctx) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            try
            {
                var ob = await Books.OpenAsync(book);
                if (n < 0 || n >= ob.Count) return Results.NotFound();
                var (path, mime) = await ob.GetPageAsync(n, ctx.RequestAborted);
                if (int.TryParse(ctx.Request.Query["w"], out var w) && w > 0)
                    (path, mime) = await Books.FitAsync(path, mime, w);
                ctx.Response.Headers.CacheControl = "public, max-age=604800";
                return Results.File(path, mime);
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex) { return Results.Problem(Friendly(ex)); }
        });

        app.MapPost("/api/book/{id}/prefetch", (string id) =>
        {
            var book = Library.Get(id);
            if (book != null) Books.Prefetch(book);
            return Results.Ok();
        });

        app.MapGet("/api/book/{id}/file", async (string id) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            var local = await Books.EnsureLocalCopyAsync(book);
            return Results.File(local, Books.Mime(local), enableRangeProcessing: true);
        });

        app.MapPost("/api/book/{id}/external", (string id) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            Process.Start(new ProcessStartInfo(book.Path) { UseShellExecute = true });
            return Results.Ok();
        });

        app.MapPost("/api/book/{id}/reveal", (string id) =>
        {
            var book = Library.Get(id);
            if (book == null) return Results.NotFound();
            Process.Start("explorer.exe", $"/select,\"{book.Path}\"");
            return Results.Ok();
        });

        app.MapGet("/api/userdata", () => Results.Json(UserData.Snapshot()));
        app.MapPost("/api/progress/{id}", (string id, Progress p) => { UserData.SetProgress(id, p); return Results.Ok(); });
        app.MapDelete("/api/progress/{id}", (string id) => { UserData.RemoveProgress([id]); return Results.Ok(); });
        app.MapPost("/api/progress-batch", (List<ProgressItem> items) => { UserData.SetProgressMany(items); return Results.Ok(); });
        app.MapPost("/api/progress-clear", (List<string> ids) => { UserData.RemoveProgress(ids); return Results.Ok(); });
        app.MapPost("/api/lists/{list}/{id}", (string list, string id, HttpRequest req) =>
            UserData.SetMark(list, id, req.Query["on"] != "0") ? Results.Ok() : Results.NotFound());
    }

    static string Friendly(Exception ex) => ex switch
    {
        IOException io when io.Message.Contains("network", StringComparison.OrdinalIgnoreCase) =>
            "La NAS ne rÃ©pond pas. VÃ©rifie qu'elle est allumÃ©e.",
        FileNotFoundException => "Ce fichier n'existe plus sur la NAS. Relance un scan.",
        UnauthorizedAccessException => "AccÃ¨s refusÃ© Ã  ce fichier sur la NAS.",
        _ => ex.Message,
    };
}
