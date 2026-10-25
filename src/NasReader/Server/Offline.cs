using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NasReader.Server;

/// <summary>
/// Books the user chose to keep on this computer, to read away from home (a laptop).
/// The whole file is copied; the readers then use it instead of the NAS copy.
/// </summary>
public static class Offline
{
    public record Item(string Fmt, long Size, long Added);
    public class Job(string id, long size)
    {
        public string Id { get; } = id;
        public long Size { get; } = size;
        public volatile string State = "queued";
        public long Bytes;
        public string? Error;
    }

    public static readonly string Dir = Path.Combine(Paths.Root, "offline");
    static string IndexFile => Path.Combine(Paths.Root, "offline.json");
    static readonly object Gate = new();
    static readonly Dictionary<string, Item> Items = Load();
    static readonly ConcurrentDictionary<string, Job> Jobs = new();
    static readonly Channel<(Book, Job)> Queue = Channel.CreateUnbounded<(Book, Job)>();

    static Offline()
    {
        Directory.CreateDirectory(Dir);
        // Files left behind (a delete that failed while the book was open, an interrupted download).
        foreach (var f in Directory.EnumerateFiles(Dir))
        {
            var id = Path.GetFileName(f).Split('.')[0];
            if (!Items.ContainsKey(id)) try { File.Delete(f); } catch { }
        }
        _ = Task.Run(WorkAsync);
    }

    static Dictionary<string, Item> Load()
    {
        var all = Json.Load<Dictionary<string, Item>>(Path.Combine(Paths.Root, "offline.json")) ?? new();
        // Forget entries whose file was deleted by hand.
        return all.Where(kv => File.Exists(Path.Combine(Path.Combine(Paths.Root, "offline"), $"{kv.Key}.{kv.Value.Fmt}")))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>The downloaded copy of a book, if it is complete and still matches the NAS file.</summary>
    public static string? File_(Book b)
    {
        lock (Gate) if (!Items.ContainsKey(b.Id)) return null;
        var f = Path.Combine(Dir, $"{b.Id}.{b.Fmt}");
        var fi = new FileInfo(f);
        return fi.Exists && fi.Length == b.Size ? f : null;
    }

    public static void Enqueue(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var b = Library.Get(id);
            if (b == null || File_(b) != null) continue;
            if (Jobs.TryGetValue(id, out var cur) && cur.State is "queued" or "downloading") continue;
            var job = new Job(id, b.Size);
            Jobs[id] = job;
            Queue.Writer.TryWrite((b, job));
        }
    }

    static async Task WorkAsync()
    {
        await foreach (var (b, job) in Queue.Reader.ReadAllAsync())
        {
            if (job.State == "cancelled") continue;
            job.State = "downloading";
            var part = Path.Combine(Dir, $"{b.Id}.part");
            try
            {
                await using (var src = new FileStream(b.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while (job.State == "downloading" && (n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        Interlocked.Add(ref job.Bytes, n);
                    }
                }
                if (job.State != "downloading") { File.Delete(part); continue; }
                if (new FileInfo(part).Length != b.Size) throw new IOException("Téléchargement incomplet.");
                File.Move(part, Path.Combine(Dir, $"{b.Id}.{b.Fmt}"), true);
                lock (Gate) { Items[b.Id] = new Item(b.Fmt, b.Size, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); Json.Save(IndexFile, Items); }
                job.State = "done";
                _ = Covers.GetAsync(b);
            }
            catch (Exception ex)
            {
                try { File.Delete(part); } catch { }
                job.State = "error";
                job.Error = ex.Message;
            }
        }
    }

    public static void Remove(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            foreach (var id in ids)
            {
                if (Jobs.TryRemove(id, out var j)) j.State = "cancelled";
                Items.Remove(id);
                Books.Forget(id);
                foreach (var f in Directory.EnumerateFiles(Dir, id + ".*")) try { File.Delete(f); } catch { }
            }
            Json.Save(IndexFile, Items);
        }
    }

    public static object Status()
    {
        lock (Gate)
        {
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(Dir)!).AvailableFreeSpace; } catch { }
            return new
            {
                done = Items.ToDictionary(kv => kv.Key, kv => kv.Value.Size),
                active = Jobs.Values.Where(j => j.State is not ("done" or "cancelled"))
                    .ToDictionary(j => j.Id, j => new { state = j.State, bytes = Interlocked.Read(ref j.Bytes), size = j.Size, error = j.Error }),
                totalBytes = Items.Values.Sum(i => i.Size),
                freeBytes = free,
            };
        }
    }
}

/// <summary>Is the NAS reachable right now? Checked at most every 20 seconds, with a short timeout.</summary>
public static class Reach
{
    static long _last;
    static bool _ok = true;

    public static bool Nas()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _last) < 20_000) return _ok;
        Interlocked.Exchange(ref _last, now);
        var s = Settings.Current;
        _ok = new[] { s.Nas, s.Nas + ".local" }.Any(h =>
        {
            try
            {
                using var c = new TcpClient();
                return c.ConnectAsync(h, 445).Wait(1500) && c.Connected;
            }
            catch { return false; }
        });
        return _ok;
    }
}
