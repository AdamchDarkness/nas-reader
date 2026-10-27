using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace NasReader.Server;

/// <summary>
/// Covers and book details kept on the NAS next to the sync file (.nasreader\covers, .nasreader\meta.json),
/// so they are made once and the phone and tablet only fetch small images instead of opening every comic.
/// Book ids are the same on every device.
/// </summary>
public static class SharedCache
{
    static readonly Channel<(string id, string local)> Uploads = Channel.CreateUnbounded<(string, string)>();
    static HashSet<string>? _remote;
    static DateTime _listedAt;
    static readonly object Gate = new();

    public static volatile int Uploaded;
    public static volatile int Pending;

    static bool Enabled => Settings.Current.SyncEnabled;
    static string Dir => Sync.Folder(Settings.Current);
    static string CoversDir => Path.Combine(Dir, "covers");

    public static void Start()
    {
        _ = Task.Run(UploadLoopAsync);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            PullMeta();
            Backfill();
        });
    }

    /// <summary>Ids of the covers already on the NAS (one directory listing, refreshed every 10 minutes).</summary>
    static HashSet<string> Remote()
    {
        lock (Gate)
        {
            if (_remote != null && DateTime.UtcNow - _listedAt < TimeSpan.FromMinutes(10)) return _remote;
            var set = new HashSet<string>();
            try
            {
                Directory.CreateDirectory(CoversDir);
                foreach (var f in Directory.EnumerateFiles(CoversDir, "*.jpg")) set.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            _listedAt = DateTime.UtcNow;
            return _remote = set;
        }
    }

    /// <summary>Copies the NAS cover to the local cache when it exists there.</summary>
    public static bool TryFetchCover(string id, string local)
    {
        if (!Enabled || !Remote().Contains(id)) return false;
        try
        {
            File.Copy(Path.Combine(CoversDir, id + ".jpg"), local + ".tmp", true);
            File.Move(local + ".tmp", local, true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sends a cover made here to the NAS, in the background.</summary>
    public static void PublishCover(string id, string local)
    {
        if (!Enabled || Remote().Contains(id)) return;
        Interlocked.Increment(ref Pending);
        Uploads.Writer.TryWrite((id, local));
    }

    static async Task UploadLoopAsync()
    {
        await foreach (var (id, local) in Uploads.Reader.ReadAllAsync())
        {
            try
            {
                var remote = Remote();
                if (!remote.Contains(id) && File.Exists(local))
                {
                    var dst = Path.Combine(CoversDir, id + ".jpg");
                    File.Copy(local, dst + ".tmp", true);
                    File.Move(dst + ".tmp", dst, true);
                    lock (Gate) remote.Add(id);
                    Interlocked.Increment(ref Uploaded);
                }
            }
            catch { }
            finally { Interlocked.Decrement(ref Pending); }
        }
    }

    /// <summary>Sends every cover this PC already made and the NAS does not have yet.</summary>
    public static void Backfill()
    {
        if (!Enabled) return;
        var remote = Remote();
        foreach (var b in Library.Snapshot.Books)
        {
            if (remote.Contains(b.Id)) continue;
            var local = Path.Combine(Paths.Covers, b.Id + ".jpg");
            if (File.Exists(local)) PublishCover(b.Id, local);
        }
        PushMeta();
    }

    static string MetaFile => Path.Combine(Dir, "meta.json");

    /// <summary>After a scan, the list of books goes to the NAS: phones load it instead of scanning over Wi-Fi.</summary>
    public static void PushLibrary()
    {
        if (!Enabled || Settings.Current.Roots.Count > 0) return; // only a scan of the whole NAS
        try
        {
            Directory.CreateDirectory(Dir);
            var dst = Path.Combine(Dir, "library.json");
            File.Copy(Paths.LibraryFile, dst + $".{Environment.MachineName}.tmp", true);
            File.Move(dst + $".{Environment.MachineName}.tmp", dst, true);
        }
        catch { }
    }

    /// <summary>Takes the details other devices found and this one has not looked for yet.</summary>
    public static void PullMeta()
    {
        if (!Enabled) return;
        try
        {
            if (!File.Exists(MetaFile)) return;
            Dictionary<string, BookMeta>? remote;
            using (var fs = new FileStream(MetaFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                remote = JsonSerializer.Deserialize<Dictionary<string, BookMeta>>(fs, Json.Options);
            if (remote == null) return;
            foreach (var (id, m) in remote) if (!m.IsEmpty) MetaStore.Set(id, m, overwrite: false);
            MetaStore.Flush();
        }
        catch { }
    }

    /// <summary>Merges this device's details into the NAS copy.</summary>
    public static void PushMeta()
    {
        if (!Enabled) return;
        try
        {
            Dictionary<string, BookMeta> merged = new();
            if (File.Exists(MetaFile))
                using (var fs = new FileStream(MetaFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    merged = JsonSerializer.Deserialize<Dictionary<string, BookMeta>>(fs, Json.Options) ?? new();
            foreach (var (id, m) in MetaStore.Filled()) merged[id] = m;
            var tmp = MetaFile + $".{Environment.MachineName}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(merged, Json.Options));
            File.Move(tmp, MetaFile, true);
        }
        catch { }
    }
}
