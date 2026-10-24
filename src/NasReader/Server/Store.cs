using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasReader.Server;

public static class Paths
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasReader");
    public static readonly string Covers = Path.Combine(Root, "covers");
    public static readonly string Pages = Path.Combine(Root, "pages");
    public static readonly string Files = Path.Combine(Root, "files");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string ProgressFile => Path.Combine(Root, "progress.json");
    public static string ListsFile => Path.Combine(Root, "lists.json");
    public static string MetaFile => Path.Combine(Root, "meta.json");

    public static void Ensure()
    {
        foreach (var d in new[] { Root, Covers, Pages, Files }) Directory.CreateDirectory(d);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch { return null; }
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

public class Settings
{
    public string Nas { get; set; } = "lord_of_adam";
    /// <summary>UNC folders to scan. Empty = every disk share of the NAS.</summary>
    public List<string> Roots { get; set; } = new();
    public List<string> ExcludeFolders { get; set; } = new() { "#recycle", "@eaDir", "#snapshot", "_Doublons", ".Trash", "$RECYCLE.BIN" };
    public bool ScanOnStartup { get; set; } = true;
    public int CacheLimitGb { get; set; } = 8;
    public bool SyncEnabled { get; set; } = true;
    /// <summary>Folder on the NAS holding userdata.json. Empty = \\nas\Comics Mangas Livre\.nasreader</summary>
    public string SyncFolder { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;

    static readonly object Gate = new();
    static Settings? _current;

    public static Settings Current
    {
        get { lock (Gate) return _current ??= Json.Load<Settings>(Paths.SettingsFile) ?? new Settings(); }
    }

    public static void Update(Settings s)
    {
        lock (Gate) { _current = s; Json.Save(Paths.SettingsFile, s); }
    }
}

// Size, Mtime and Name are a fingerprint of the file: they let progress and lists
// follow a book that was moved or renamed on the NAS (its id comes from its path).
// Deleted / Removed are kept as dated markers so that "unread" and "remove from list"
// also reach the other devices through the sync file.
public record Progress(int Page, int Pages, string? Loc, long Updated, long Size = 0, long Mtime = 0, string? Name = null, bool Deleted = false);
public record Mark(long Added, long Size = 0, long Mtime = 0, string? Name = null, long Removed = 0)
{
    [JsonIgnore] public bool Active => Added > Removed;
    [JsonIgnore] public long Stamp => Math.Max(Added, Removed);
}
public record ProgressItem(string Id, int Page, int Pages);

/// <summary>The shared file on the NAS: everything, markers included.</summary>
public class UserDataFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, Progress> Progress { get; set; } = new();
    public Dictionary<string, Dictionary<string, Mark>> Lists { get; set; } = new();
}

public static class UserData
{
    public static readonly string[] ListNames = ["favorites", "toread"];

    static readonly object Gate = new();
    static readonly Dictionary<string, Progress> _progress = Json.Load<Dictionary<string, Progress>>(Paths.ProgressFile) ?? new();
    static readonly Dictionary<string, Dictionary<string, Mark>> _lists = LoadLists();
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static Dictionary<string, Dictionary<string, Mark>> LoadLists()
    {
        var l = Json.Load<Dictionary<string, Dictionary<string, Mark>>>(Paths.ListsFile) ?? new();
        foreach (var n in ListNames) l.TryAdd(n, new());
        return l;
    }

    public static object Snapshot()
    {
        lock (Gate) return new
        {
            progress = _progress.Where(kv => !kv.Value.Deleted).ToDictionary(kv => kv.Key, kv => kv.Value),
            lists = _lists.ToDictionary(kv => kv.Key, kv => kv.Value.Where(m => m.Value.Active).OrderByDescending(m => m.Value.Added).Select(m => m.Key).ToList()),
        };
    }

    static Progress Stamp(string id, Progress p)
    {
        var b = Library.Get(id);
        return b == null ? p : p with { Size = b.Size, Mtime = b.Mtime, Name = Path.GetFileName(b.Path) };
    }

    static void SaveProgress() { Json.Save(Paths.ProgressFile, _progress); Sync.Changed(); }
    static void SaveLists() { Json.Save(Paths.ListsFile, _lists); Sync.Changed(); }

    public static void SetProgress(string id, Progress p)
    {
        lock (Gate) { _progress[id] = Stamp(id, p with { Deleted = false }); SaveProgress(); }
    }

    public static void SetProgressMany(IEnumerable<ProgressItem> items)
    {
        lock (Gate)
        {
            foreach (var i in items) _progress[i.Id] = Stamp(i.Id, new Progress(i.Page, i.Pages, null, Now));
            SaveProgress();
        }
    }

    public static void RemoveProgress(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            foreach (var id in ids)
                _progress[id] = _progress.TryGetValue(id, out var p) ? p with { Deleted = true, Updated = Now } : Stamp(id, new Progress(0, 0, null, Now, Deleted: true));
            SaveProgress();
        }
    }

    public static bool SetMark(string list, string id, bool on)
    {
        lock (Gate)
        {
            if (!_lists.TryGetValue(list, out var marks)) return false;
            var b = Library.Get(id);
            marks.TryGetValue(id, out var cur);
            marks[id] = on
                ? new Mark(Now, b?.Size ?? cur?.Size ?? 0, b?.Mtime ?? cur?.Mtime ?? 0, b == null ? cur?.Name : Path.GetFileName(b.Path))
                : (cur ?? new Mark(0, b?.Size ?? 0, b?.Mtime ?? 0, b == null ? null : Path.GetFileName(b.Path))) with { Removed = Now };
            SaveLists();
            return true;
        }
    }

    public static UserDataFile Export()
    {
        lock (Gate) return new UserDataFile
        {
            Progress = new(_progress),
            Lists = _lists.ToDictionary(kv => kv.Key, kv => new Dictionary<string, Mark>(kv.Value)),
        };
    }

    /// <summary>Takes whatever is newer in the other device's data. Returns true when something changed here.</summary>
    public static bool Merge(UserDataFile other)
    {
        lock (Gate)
        {
            bool pc = false, lc = false;
            foreach (var (id, r) in other.Progress)
                if (!_progress.TryGetValue(id, out var l) || l.Updated < r.Updated) { _progress[id] = r; pc = true; }
            foreach (var (name, marks) in other.Lists)
            {
                if (!_lists.TryGetValue(name, out var local)) _lists[name] = local = new();
                foreach (var (id, r) in marks)
                    if (!local.TryGetValue(id, out var l) || l.Stamp < r.Stamp) { local[id] = r; lc = true; }
            }
            // Markers older than six months have reached every device by now.
            var old = Now - 180L * 24 * 3600 * 1000;
            foreach (var (id, p) in _progress.ToList()) if (p.Deleted && p.Updated < old) { _progress.Remove(id); pc = true; }
            foreach (var marks in _lists.Values) foreach (var (id, mk) in marks.ToList()) if (!mk.Active && mk.Removed < old) { marks.Remove(id); lc = true; }
            if (pc) Json.Save(Paths.ProgressFile, _progress);
            if (lc) Json.Save(Paths.ListsFile, _lists);
            return pc || lc;
        }
    }

    /// <summary>After a scan, re-attach progress and lists of books whose path changed.</summary>
    public static void Reconcile(IReadOnlyCollection<Book> books)
    {
        var ids = books.Select(b => b.Id).ToHashSet();
        var byId = books.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        var byNameSize = books.GroupBy(b => (Path.GetFileName(b.Path).ToLowerInvariant(), b.Size)).ToDictionary(g => g.Key, g => g.ToList());
        var bySizeTime = books.GroupBy(b => (b.Size, b.Mtime / 1000)).ToDictionary(g => g.Key, g => g.ToList());

        Book? Find(long size, long mtime, string? name)
        {
            if (size <= 0) return null;
            if (name != null && byNameSize.TryGetValue((name.ToLowerInvariant(), size), out var a) && a.Count == 1) return a[0];
            if (bySizeTime.TryGetValue((size, mtime / 1000), out var c) && c.Count == 1) return c[0];
            return null;
        }

        lock (Gate)
        {
            bool progressChanged = false, listsChanged = false;
            foreach (var (id, p) in _progress.ToList())
            {
                if (p.Deleted) continue;
                if (ids.Contains(id))
                {
                    if (p.Size == 0) { var b = byId[id]; _progress[id] = p with { Size = b.Size, Mtime = b.Mtime, Name = Path.GetFileName(b.Path) }; progressChanged = true; }
                    continue;
                }
                var moved = Find(p.Size, p.Mtime, p.Name);
                if (moved == null) continue;
                _progress[id] = p with { Deleted = true, Updated = Now };
                if (!_progress.TryGetValue(moved.Id, out var existing) || existing.Updated < p.Updated || existing.Deleted)
                    _progress[moved.Id] = p with { Name = Path.GetFileName(moved.Path), Updated = Now };
                progressChanged = true;
            }
            foreach (var marks in _lists.Values)
            {
                foreach (var (id, m) in marks.ToList())
                {
                    if (!m.Active || ids.Contains(id)) continue;
                    var moved = Find(m.Size, m.Mtime, m.Name);
                    if (moved == null) continue;
                    marks[id] = m with { Removed = Now };
                    if (!marks.TryGetValue(moved.Id, out var e) || !e.Active)
                        marks[moved.Id] = m with { Name = Path.GetFileName(moved.Path), Added = Now, Removed = 0 };
                    listsChanged = true;
                }
            }
            if (progressChanged) SaveProgress();
            if (listsChanged) SaveLists();
        }
    }
}

/// <summary>Details read from ComicInfo.xml (CBZ/CBR) or the OPF of an EPUB.</summary>
public class BookMeta
{
    public string? Title { get; set; }
    public string? Series { get; set; }
    public string? Number { get; set; }
    public string? Volume { get; set; }
    public string? Year { get; set; }
    public string? Writer { get; set; }
    public string? Artist { get; set; }
    public string? Publisher { get; set; }
    public string? Summary { get; set; }
    public string? Genre { get; set; }
    public string? Characters { get; set; }
    public int? PageCount { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Title == null && Series == null && Number == null && Writer == null && Summary == null && Year == null;
}

public static class MetaStore
{
    static readonly object Gate = new();
    static readonly Dictionary<string, BookMeta> _all = Json.Load<Dictionary<string, BookMeta>>(Paths.MetaFile) ?? new();
    static int _dirty;
    static System.Threading.Timer? _timer;

    public static bool Has(string id) { lock (Gate) return _all.ContainsKey(id); }

    /// <summary>Only books that have real details (empty entries just mean "already looked").</summary>
    public static Dictionary<string, BookMeta> Filled()
    {
        lock (Gate) return _all.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static void Set(string id, BookMeta meta, bool overwrite = true)
    {
        lock (Gate)
        {
            if (!overwrite && _all.TryGetValue(id, out var old) && !old.IsEmpty) return;
            _all[id] = meta;
            _dirty++;
            _timer ??= new System.Threading.Timer(_ => Flush(), null, 4000, Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            _timer?.Dispose(); _timer = null;
            if (_dirty == 0) return;
            _dirty = 0;
            try { Json.Save(Paths.MetaFile, _all); } catch { }
        }
    }
}
