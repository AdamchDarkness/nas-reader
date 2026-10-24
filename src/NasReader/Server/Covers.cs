using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NasReader.Server;

/// <summary>Covers and details (ComicInfo.xml, EPUB metadata), read from a book in one pass.</summary>
public static class Covers
{
    const uint Width = 360;
    static readonly SemaphoreSlim Gate = new(4, 4);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> Pending = new();

    static string CoverPath(Book b) => Path.Combine(Paths.Covers, b.Id + ".jpg");
    static bool HasMetaFormat(Book b) => b.Fmt is "cbz" or "cbr" or "cb7" or "epub";
    static bool NeedsCover(Book b) => b.Fmt is not ("mobi" or "azw3") && !File.Exists(CoverPath(b)) && !File.Exists(CoverPath(b) + ".fail");
    static bool NeedsMeta(Book b) => HasMetaFormat(b) && !MetaStore.Has(b.Id);
    public static bool NeedsWork(Book b) => NeedsCover(b) || NeedsMeta(b);

    /// <summary>Cover asked by the screen: served before background work.</summary>
    public static async Task<string?> GetAsync(Book book)
    {
        var path = CoverPath(book);
        if (File.Exists(path)) return path;
        if (File.Exists(path + ".fail")) return null;
        await RunOnce(book, useGate: true);
        return File.Exists(path) ? path : null;
    }

    public static Task EnsureAsync(Book book) => NeedsWork(book) ? RunOnce(book, useGate: false) : Task.CompletedTask;

    static Task RunOnce(Book book, bool useGate)
    {
        var lazy = Pending.GetOrAdd(book.Id, _ => new Lazy<Task>(() => ProcessAsync(book, useGate)));
        return lazy.Value;
    }

    static async Task ProcessAsync(Book book, bool useGate)
    {
        if (useGate) await Gate.WaitAsync();
        bool needCover = NeedsCover(book), needMeta = NeedsMeta(book);
        try
        {
            if (!needCover && !needMeta) return;
            var source = Books.Source(book);
            var (jpeg, meta) = book.Fmt switch
            {
                "cbz" or "cbr" or "cb7" => await FromArchiveAsync(source, needCover),
                "pdf" => (await FromPdfAsync(source), null),
                "epub" => await FromEpubAsync(source, needCover),
                _ => (null, null),
            };
            if (needMeta) MetaStore.Set(book.Id, meta ?? new BookMeta(), overwrite: false);
            if (needCover)
            {
                if (jpeg == null) throw new InvalidDataException("no cover");
                await File.WriteAllBytesAsync(CoverPath(book), jpeg);
            }
        }
        catch (Exception ex)
        {
            if (needCover) try { File.WriteAllText(CoverPath(book) + ".fail", ex.ToString()); } catch { }
            if (needMeta) MetaStore.Set(book.Id, new BookMeta(), overwrite: false);
        }
        finally
        {
            if (useGate) Gate.Release();
            Pending.TryRemove(book.Id, out _);
        }
    }

    static async Task<(byte[]?, BookMeta?)> FromArchiveAsync(string file, bool needCover)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        using var archive = ArchiveFactory.Open(fs);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        var first = entries
            .Where(e => Books.IsPageEntry(e.Key))
            .OrderBy(e => Books.NaturalKey(e.Key!), StringComparer.Ordinal)
            .FirstOrDefault();
        var info = entries.FirstOrDefault(e => Books.IsComicInfo(e.Key));

        BookMeta? meta = null;
        using var cover = new MemoryStream();
        if (archive.IsSolid)
        {
            // Solid archives decompress in order: stop at the cover, and take
            // ComicInfo.xml only if it comes before it (the reader picks it up otherwise).
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var key = reader.Entry.Key;
                if (Books.IsComicInfo(key))
                {
                    using var ms = new MemoryStream();
                    reader.WriteEntryTo(ms);
                    ms.Position = 0;
                    meta = ParseComicInfo(ms);
                }
                else if (first != null && key == first.Key)
                {
                    if (needCover) reader.WriteEntryTo(cover);
                    break;
                }
            }
        }
        else
        {
            if (info != null)
            {
                using var es = info.OpenEntryStream();
                using var ms = new MemoryStream();
                await es.CopyToAsync(ms);
                ms.Position = 0;
                meta = ParseComicInfo(ms);
            }
            if (needCover && first != null)
            {
                using var es = first.OpenEntryStream();
                await es.CopyToAsync(cover);
            }
        }
        if (meta != null && meta.PageCount == null && first != null)
            meta.PageCount = entries.Count(e => Books.IsPageEntry(e.Key));

        byte[]? jpeg = null;
        if (cover.Length > 0)
        {
            cover.Position = 0;
            jpeg = await Books.ResizeToJpegAsync(cover, Width);
        }
        return (jpeg, meta);
    }

    static async Task<byte[]?> FromPdfAsync(string file)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        var doc = await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream());
        if (doc.PageCount == 0) return null;
        using var page = doc.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = Width,
            DestinationHeight = (uint)(Width * page.Size.Height / Math.Max(1, page.Size.Width)),
            BitmapEncoderId = BitmapEncoder.JpegEncoderId,
        });
        stream.Seek(0);
        using var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }

    static async Task<(byte[]?, BookMeta?)> FromEpubAsync(string file, bool needCover)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var (href, meta) = ReadOpf(zip);
        if (!needCover) return (null, meta);

        var entry = href != null ? zip.GetEntry(href) : null;
        entry ??= zip.Entries
            .Where(e => Books.IsPageEntry(e.FullName))
            .OrderByDescending(e => e.FullName.Contains("cover", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Length)
            .FirstOrDefault();
        if (entry == null) return (null, meta);
        await using var es = entry.Open();
        using var ms = new MemoryStream();
        await es.CopyToAsync(ms);
        ms.Position = 0;
        return (await Books.ResizeToJpegAsync(ms, Width), meta);
    }

    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    public static BookMeta? ParseComicInfo(Stream xml)
    {
        try
        {
            var doc = XDocument.Load(xml);
            string? F(string name) => Clean(doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);
            var meta = new BookMeta
            {
                Title = F("Title"),
                Series = F("Series"),
                Number = F("Number"),
                Volume = F("Volume"),
                Year = F("Year"),
                Writer = F("Writer"),
                Artist = F("Penciller") ?? F("Artist") ?? F("CoverArtist"),
                Publisher = F("Publisher"),
                Summary = F("Summary"),
                Genre = F("Genre"),
                Characters = F("Characters"),
                PageCount = int.TryParse(F("PageCount"), out var pc) && pc > 0 ? pc : null,
            };
            if (meta.Year == "-1" || meta.Year == "0") meta.Year = null;
            return meta.IsEmpty ? null : meta;
        }
        catch { return null; }
    }

    static (string? coverHref, BookMeta? meta) ReadOpf(ZipArchive zip)
    {
        try
        {
            var container = zip.GetEntry("META-INF/container.xml");
            if (container == null) return (null, null);
            XDocument cx;
            using (var s = container.Open()) cx = XDocument.Load(s);
            var opfPath = cx.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            var opfEntry = opfPath == null ? null : zip.GetEntry(opfPath);
            if (opfEntry == null) return (null, null);
            XDocument opf;
            using (var s = opfEntry.Open()) opf = XDocument.Load(s);

            string? Dc(string name) => Clean(opf.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value);
            string? MetaName(string name) => opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == name)?.Attribute("content")?.Value;
            string? MetaProp(string prop) => Clean(opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("property")?.Value == prop)?.Value);

            var date = Dc("date");
            var meta = new BookMeta
            {
                Title = Dc("title"),
                Writer = string.Join(", ", opf.Descendants().Where(e => e.Name.LocalName == "creator").Select(e => Clean(e.Value)).Where(v => v != null).Distinct()) is { Length: > 0 } w ? w : null,
                Publisher = Dc("publisher"),
                Summary = Dc("description"),
                Year = date != null && date.Length >= 4 && char.IsDigit(date[0]) ? date[..4] : null,
                Series = Clean(MetaName("calibre:series")) ?? MetaProp("belongs-to-collection"),
                Number = Clean(MetaName("calibre:series_index"))?.Replace(".0", "") ?? MetaProp("group-position"),
                Genre = Dc("subject"),
            };

            var items = opf.Descendants().Where(e => e.Name.LocalName == "item").ToList();
            var item = items.FirstOrDefault(i => (i.Attribute("properties")?.Value ?? "").Contains("cover-image"));
            if (item == null)
            {
                var coverId = MetaName("cover");
                item = items.FirstOrDefault(i => i.Attribute("id")?.Value == coverId);
            }
            string? full = null;
            var href = item?.Attribute("href")?.Value;
            if (href != null)
            {
                var baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/');
                full = Uri.UnescapeDataString(string.IsNullOrEmpty(baseDir) ? href : baseDir + "/" + href);
            }
            return (full, meta.IsEmpty ? null : meta);
        }
        catch { return (null, null); }
    }
}

/// <summary>After each scan, prepares covers and details for the whole library in the background.</summary>
public static class Enricher
{
    static CancellationTokenSource? _cts;
    static int _done, _total;
    public static int Done => Volatile.Read(ref _done);
    public static int Total => Volatile.Read(ref _total);
    public static volatile bool Running;

    public static void Restart()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Task.Run(() => RunAsync(cts.Token));
    }

    static async Task RunAsync(CancellationToken ct)
    {
        var books = Library.Snapshot.Books
            .Where(Covers.NeedsWork)
            .OrderBy(b => b.Sorted ? 0 : 1)
            .ThenBy(b => b.Kind == "comic" || b.Kind == "manga" ? 0 : 1)
            .ThenBy(b => b.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Volatile.Write(ref _done, 0);
        Volatile.Write(ref _total, books.Count);
        Running = books.Count > 0;
        try
        {
            await Parallel.ForEachAsync(books, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (b, c) =>
            {
                while (Books.Busy) await Task.Delay(400, c);
                try { await Covers.EnsureAsync(b); } catch { }
                Interlocked.Increment(ref _done);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) Running = false;
            MetaStore.Flush();
        }
    }
}
