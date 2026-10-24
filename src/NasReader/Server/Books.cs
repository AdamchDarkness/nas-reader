using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NasReader.Server;

/// <summary>A book opened for reading: its pages as images, cached on disk as they are produced.</summary>
public abstract class OpenBook : IDisposable
{
    public required Book Book { get; init; }
    public int Count { get; protected set; }
    public abstract Task<(string path, string mime)> GetPageAsync(int index, CancellationToken ct);
    public virtual void Dispose() { }
}

public static class Books
{
    static readonly ConcurrentDictionary<string, Lazy<Task<OpenBook>>> Open = new();
    static readonly ConcurrentQueue<string> Recent = new();
    static int _busy;

    /// <summary>True while a book is being read from the NAS; background work waits for it.</summary>
    public static bool Busy => Volatile.Read(ref _busy) > 0;
    public static void BeginIo() => Interlocked.Increment(ref _busy);
    public static void EndIo() => Interlocked.Decrement(ref _busy);

    public static Task<OpenBook> OpenAsync(Book book)
    {
        var lazy = Open.GetOrAdd(book.Id, _ => new Lazy<Task<OpenBook>>(() => Task.Run(() => CreateAsync(book))));
        var task = lazy.Value;
        if (task.IsFaulted) // let the next attempt retry
        {
            Open.TryRemove(book.Id, out _);
        }
        else if (!Recent.Contains(book.Id))
        {
            Recent.Enqueue(book.Id);
            while (Recent.Count > 6 && Recent.TryDequeue(out var old))
                if (Open.TryRemove(old, out var gone) && gone.IsValueCreated && gone.Value.IsCompletedSuccessfully)
                    gone.Value.Result.Dispose();
        }
        return task;
    }

    static async Task<OpenBook> CreateAsync(Book book)
    {
        _ = Task.Run(TrimCache);
        var dir = Path.Combine(Paths.Pages, book.Id);
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        return book.Fmt switch
        {
            "pdf" => await PdfBook.LoadAsync(book, dir),
            "cbz" or "cbr" or "cb7" => ArchiveBook.Load(book, dir),
            _ => new FileOnlyBook { Book = book },
        };
    }

    /// <summary>Reads the first pages of a book in the background so it opens instantly.</summary>
    public static void Prefetch(Book book)
    {
        if (book.Fmt is not ("pdf" or "cbz" or "cbr" or "cb7")) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var ob = await OpenAsync(book);
                for (int i = 0; i < Math.Min(3, ob.Count); i++) await ob.GetPageAsync(i, CancellationToken.None);
            }
            catch { }
        });
    }

    /// <summary>The file as it is on the NAS, or the local copy when there is one.</summary>
    public static string Source(Book book)
    {
        var local = Path.Combine(Paths.Files, book.Id + "." + book.Fmt);
        var fi = new FileInfo(local);
        return fi.Exists && fi.Length == book.Size ? local : book.Path;
    }

    /// <summary>EPUB and MOBI are handed to the reader as whole files, so they are copied first.</summary>
    public static async Task<string> EnsureLocalCopyAsync(Book book)
    {
        var local = Path.Combine(Paths.Files, book.Id + "." + book.Fmt);
        var fi = new FileInfo(local);
        if (fi.Exists && fi.Length == book.Size)
        {
            fi.LastWriteTimeUtc = DateTime.UtcNow;
            return local;
        }
        var tmp = local + ".part";
        await using (var src = new FileStream(book.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            await src.CopyToAsync(dst);
        File.Move(tmp, local, true);
        return local;
    }

    public static long CacheSize() =>
        new DirectoryInfo(Paths.Files).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) +
        new DirectoryInfo(Paths.Pages).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    public static void ClearCache()
    {
        foreach (var lazy in Open.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result.Dispose();
        Open.Clear();
        foreach (var d in new[] { Paths.Files, Paths.Pages })
            foreach (var e in new DirectoryInfo(d).EnumerateFileSystemInfos())
                try { if (e is DirectoryInfo di) di.Delete(true); else e.Delete(); } catch { }
    }

    static void TrimCache()
    {
        try
        {
            long limit = (long)Settings.Current.CacheLimitGb * 1024 * 1024 * 1024;
            var items = new Dictionary<string, (long size, DateTime used)>();
            void Add(string id, long size, DateTime used)
            {
                items.TryGetValue(id, out var cur);
                items[id] = (cur.size + size, used > cur.used ? used : cur.used);
            }
            foreach (var f in new DirectoryInfo(Paths.Files).EnumerateFiles())
                Add(f.Name.Split('.')[0], f.Length, f.LastWriteTimeUtc);
            foreach (var d in new DirectoryInfo(Paths.Pages).EnumerateDirectories())
                Add(d.Name, d.EnumerateFiles().Sum(f => f.Length), d.LastWriteTimeUtc);

            long total = items.Values.Sum(v => v.size);
            foreach (var (id, v) in items.OrderBy(kv => kv.Value.used))
            {
                if (total <= limit) break;
                if (Open.ContainsKey(id)) continue;
                foreach (var f in Directory.EnumerateFiles(Paths.Files, id + ".*")) File.Delete(f);
                var pages = Path.Combine(Paths.Pages, id);
                if (Directory.Exists(pages)) Directory.Delete(pages, true);
                total -= v.size;
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- helpers

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif" };

    public static bool IsPageEntry(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var name = Path.GetFileName(key);
        if (name.StartsWith("._") || key.Contains("__MACOSX")) return false;
        return ImageExt.Contains(Path.GetExtension(key));
    }

    public static bool IsComicInfo(string? key) =>
        key != null && Path.GetFileName(key).Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        ".pdf" => "application/pdf",
        ".epub" => "application/epub+zip",
        _ => "application/octet-stream",
    };

    static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Sort key so that "page 2" comes before "page 10".</summary>
    public static string NaturalKey(string s) =>
        Digits.Replace(s.Replace('\\', '/').ToLowerInvariant(), m => m.Value.PadLeft(10, '0'));

    public static async Task<byte[]> ResizeToJpegAsync(Stream input, uint width)
    {
        using var ras = new InMemoryRandomAccessStream();
        // Not disposed on purpose: disposing the wrapper would close the WinRT stream.
        var w = ras.AsStreamForWrite();
        await input.CopyToAsync(w);
        await w.FlushAsync();
        ras.Seek(0);
        var dec = await BitmapDecoder.CreateAsync(ras);
        uint targetW = Math.Min(width, dec.PixelWidth);
        uint targetH = Math.Max(1, (uint)((ulong)dec.PixelHeight * targetW / Math.Max(1u, dec.PixelWidth)));
        using var bmp = await dec.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = targetW, ScaledHeight = targetH, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.82, Windows.Foundation.PropertyType.Single) };
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        enc.SetSoftwareBitmap(bmp);
        await enc.FlushAsync();
        output.Seek(0);
        using var ms = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }
}

/// <summary>EPUB / MOBI: the reader uses the raw file.</summary>
public class FileOnlyBook : OpenBook
{
    public override Task<(string, string)> GetPageAsync(int index, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>
/// CBZ / CBR / CB7 read straight from the NAS: the archive is streamed once and each page is
/// written to the cache as soon as it is decompressed, so page 1 shows while the rest arrives.
/// Pages further ahead are pulled directly when the archive allows random access.
/// </summary>
public class ArchiveBook : OpenBook
{
    string _dir = "";
    string _source = "";
    List<string> _keys = [];
    TaskCompletionSource<string>[] _pages = [];
    IArchive? _random;
    Dictionary<string, IArchiveEntry> _entries = new();
    readonly object _randomLock = new();
    readonly CancellationTokenSource _cts = new();

    public static ArchiveBook Load(Book book, string dir)
    {
        var ab = new ArchiveBook { Book = book, _dir = dir, _source = Books.Source(book) };
        var index = Path.Combine(dir, "index.json");

        if (File.Exists(index))
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(index)) ?? [];
            if (names.Length > 0 && names.All(n => n != null && File.Exists(Path.Combine(dir, n))))
            {
                ab._pages = names.Select(n =>
                {
                    var t = new TaskCompletionSource<string>();
                    t.SetResult(Path.Combine(dir, n));
                    return t;
                }).ToArray();
                ab.Count = names.Length;
                return ab;
            }
        }

        var fs = new FileStream(ab._source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        var archive = ArchiveFactory.Open(fs);
        var images = archive.Entries
            .Where(e => !e.IsDirectory && Books.IsPageEntry(e.Key))
            .OrderBy(e => Books.NaturalKey(e.Key!), StringComparer.Ordinal)
            .ToList();
        if (images.Count == 0)
        {
            archive.Dispose(); fs.Dispose();
            throw new InvalidDataException("Aucune image trouvée dans cette archive.");
        }

        ab._keys = images.Select(e => e.Key!).ToList();
        ab.Count = images.Count;
        ab._pages = images.Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        if (archive.IsSolid)
        {
            archive.Dispose(); fs.Dispose();
        }
        else
        {
            ab._random = archive;
            ab._entries = images.ToDictionary(e => e.Key!, e => e);
        }
        _ = Task.Run(ab.ExtractAll);
        return ab;
    }

    string PagePath(int i) => Path.Combine(_dir, $"{i:D4}{Path.GetExtension(_keys[i]).ToLowerInvariant()}");

    void Save(int i, Action<Stream> write)
    {
        if (_pages[i].Task.IsCompleted) return;
        var path = PagePath(i);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var fs = File.Create(tmp)) write(fs);
        try { File.Move(tmp, path, true); }
        catch { File.Delete(tmp); if (!File.Exists(path)) throw; }
        _pages[i].TrySetResult(path);
    }

    void ExtractAll()
    {
        var order = _keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        Books.BeginIo();
        try
        {
            using var fs = new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            using var archive = ArchiveFactory.Open(fs);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                _cts.Token.ThrowIfCancellationRequested();
                var key = reader.Entry.Key;
                if (reader.Entry.IsDirectory || key == null) continue;
                if (Books.IsComicInfo(key))
                {
                    using var ms = new MemoryStream();
                    reader.WriteEntryTo(ms);
                    ms.Position = 0;
                    var meta = Covers.ParseComicInfo(ms);
                    if (meta != null) MetaStore.Set(Book.Id, meta);
                    continue;
                }
                if (!order.TryGetValue(key, out var i) || _pages[i].Task.IsCompleted) continue;
                Save(i, s => reader.WriteEntryTo(s));
            }
            File.WriteAllText(Path.Combine(_dir, "index.json"),
                JsonSerializer.Serialize(_pages.Select(p => p.Task.IsCompletedSuccessfully ? Path.GetFileName(p.Task.Result) : null)));
        }
        catch (Exception ex)
        {
            foreach (var p in _pages) p.TrySetException(ex);
        }
        finally
        {
            Books.EndIo();
        }
        foreach (var p in _pages) p.TrySetException(new InvalidDataException("Page illisible dans l'archive."));
    }

    public override async Task<(string, string)> GetPageAsync(int index, CancellationToken ct)
    {
        var task = _pages[index].Task;
        if (!task.IsCompleted && _random != null)
        {
            // Give the sequential reader a moment, then fetch this page on its own.
            await Task.WhenAny(task, Task.Delay(150, ct));
            if (!task.IsCompleted)
                await Task.Run(() =>
                {
                    lock (_randomLock)
                    {
                        if (_random == null || _pages[index].Task.IsCompleted) return;
                        try
                        {
                            using var es = _entries[_keys[index]].OpenEntryStream();
                            Save(index, s => es.CopyTo(s));
                        }
                        catch { /* the sequential reader will still deliver it */ }
                    }
                }, ct);
        }
        var path = await task.WaitAsync(TimeSpan.FromMinutes(3), ct);
        return (path, Books.Mime(path));
    }

    public override void Dispose()
    {
        _cts.Cancel();
        lock (_randomLock) { _random?.Dispose(); _random = null; }
    }
}

public class PdfBook : OpenBook
{
    PdfDocument _doc = null!;
    FileStream _stream = null!;
    string _dir = "";
    readonly SemaphoreSlim _render = new(1, 1);

    public static async Task<PdfBook> LoadAsync(Book book, string dir)
    {
        // Windows' PDF engine reads only the parts it needs, straight from the NAS.
        var fs = new FileStream(Books.Source(book), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var doc = await PdfDocument.LoadFromStreamAsync(fs.AsRandomAccessStream());
            return new PdfBook { Book = book, _doc = doc, _stream = fs, _dir = dir, Count = (int)doc.PageCount };
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public override async Task<(string, string)> GetPageAsync(int index, CancellationToken ct)
    {
        var path = Path.Combine(_dir, $"p{index:D4}.jpg");
        if (File.Exists(path)) return (path, "image/jpeg");
        await _render.WaitAsync(ct);
        Books.BeginIo();
        try
        {
            if (File.Exists(path)) return (path, "image/jpeg");
            using var page = _doc.GetPage((uint)index);
            var size = page.Size;
            // Landscape pages are usually double spreads: give them more pixels.
            uint width = size.Width > size.Height ? 2800u : 1800u;
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = width,
                DestinationHeight = (uint)(width * size.Height / Math.Max(1, size.Width)),
                BitmapEncoderId = BitmapEncoder.JpegEncoderId,
            });
            stream.Seek(0);
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp)) await stream.AsStreamForRead().CopyToAsync(fs, ct);
            File.Move(tmp, path, true);
            return (path, "image/jpeg");
        }
        finally
        {
            Books.EndIo();
            _render.Release();
        }
    }

    public override void Dispose()
    {
        _render.Wait(5000);
        _stream.Dispose();
    }
}
