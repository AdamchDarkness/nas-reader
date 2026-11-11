using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NasReader.Server;

public record Book(
    string Id,
    string Path,
    string Title,
    string Kind,      // comic | manga | ebook | magazine | pdf
    string Fmt,       // cbz | cbr | cb7 | pdf | epub | mobi | azw3
    long Size,
    long Mtime,
    string[] Crumbs,  // folders below the section, e.g. [Marvel, Spider-Man, Amazing Spider-Man]
    bool Sorted);     // true when the file sits in a Comics / Mangas / Ebooks / Magazines folder

public class LibrarySnapshot
{
    public List<Book> Books { get; set; } = new();
    public long ScannedAt { get; set; }
}

public static class Library
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cbz", ".cbr", ".cb7", ".cbt", ".cba", ".pdf", ".epub", ".mobi", ".azw3" };

    // Folder names that mark a section of the library, whatever share they live in.
    static readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comics"] = "comic", ["comic"] = "comic", ["bd"] = "comic", ["bandes dessinées"] = "comic",
        ["mangas"] = "manga", ["manga"] = "manga",
        ["webtoons"] = "webtoon", ["webtoon"] = "webtoon", ["manhwa"] = "webtoon",
        ["ebooks"] = "ebook", ["ebook"] = "ebook", ["livres"] = "ebook", ["romans"] = "ebook",
        ["magazines"] = "magazine", ["magazine"] = "magazine", ["presse"] = "magazine",
    };

    static readonly object Gate = new();
    static LibrarySnapshot _snapshot = Json.Load<LibrarySnapshot>(Paths.LibraryFile) ?? new();
    static Dictionary<string, Book> _byId = _snapshot.Books.ToDictionary(b => b.Id);
    static CancellationTokenSource? _scanCts;

    public static volatile bool Scanning;
    public static volatile int ScanFound;
    public static volatile string ScanCurrent = "";
    public static string? ScanError;

    public static LibrarySnapshot Snapshot { get { lock (Gate) return _snapshot; } }

    public static Book? Get(string id) { lock (Gate) return _byId.GetValueOrDefault(id); }

    public static string IdFor(string path)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Uses the list of books put on the NAS by another device or by the Synology service
    /// (nas/nasreader-service.py) when it is newer than this one: no need to walk every folder.
    /// Returns true when this PC's list is now up to date.
    /// </summary>
    public static bool AdoptShared(double maxAgeDays = 7)
    {
        var st = Settings.Current;
        if (!st.UsesSync || st.Roots.Count > 0) return false;
        try
        {
            var file = Path.Combine(Sync.Folder(st), "library.json");
            if (!File.Exists(file)) return false;
            LibrarySnapshot? shared;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                shared = JsonSerializer.Deserialize<LibrarySnapshot>(fs, Json.Options);
            if (shared == null || shared.Books.Count == 0) return false;
            var mine = Snapshot.ScannedAt;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - shared.ScannedAt > maxAgeDays * 86_400_000) return false;
            if (shared.ScannedAt <= mine) return true;
            // Keep the books of this PC's own folders: the shared list only has the NAS.
            shared.Books.AddRange(Snapshot.Books.Where(b => !st.OnMainNas(b.Path)));
            lock (Gate)
            {
                _snapshot = shared;
                _byId = shared.Books.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            }
            Json.Save(Paths.LibraryFile, shared);
            UserData.Reconcile(shared.Books);
            Log.Info($"Liste partagée reprise de la NAS ({shared.Books.Count} livres)");
            return true;
        }
        catch (Exception ex) { Log.Error("AdoptShared", ex); return false; }
    }

    public static List<string> DiscoverShares(string nas)
    {
        var shares = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("net", $"view \\\\{nas}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            foreach (var line in output.Split('\n'))
            {
                // "Comics Mangas Livre  Disk           Comics,Mangas,Livre"
                var idx = line.IndexOf("  Disk", StringComparison.Ordinal);
                if (idx > 0) shares.Add(line[..idx].Trim());
            }
        }
        catch { }
        return shares.Select(s => $@"\\{nas}\{s}").ToList();
    }

    /// <summary>The NAS shares to scan (all of them unless chosen in the settings).</summary>
    public static List<string> NasRoots(Settings s) =>
        !s.HasNas ? new() : s.Roots.Count > 0 ? s.Roots : DiscoverShares(s.Nas);

    static List<string> Extras(Settings s) =>
        s.ExtraFolders.Select(f => f.Trim().TrimEnd('\\')).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Scans the NAS and the extra folders; with <paramref name="extrasOnly"/>, only the extra folders.</summary>
    public static void StartScan(bool extrasOnly = false)
    {
        lock (Gate)
        {
            if (Scanning) return;
            Scanning = true;
            _scanCts = new CancellationTokenSource();
        }
        var token = _scanCts.Token;
        Task.Run(() =>
        {
            try { Scan(token, extrasOnly); ScanError = null; }
            catch (Exception ex) { ScanError = ex.Message; Log.Error("Scan", ex); }
            finally { Scanning = false; ScanCurrent = ""; }
        });
    }

    static void Scan(CancellationToken token, bool extrasOnly)
    {
        var settings = Settings.Current;
        ScanFound = 0;
        var extras = Extras(settings);
        var nasRoots = extrasOnly ? new List<string>() : NasRoots(settings);
        // NAS out of reach: keep its books from the last scan and still read the other folders.
        var keepNas = extrasOnly || (settings.HasNas && nasRoots.Count == 0);
        if (keepNas && !extrasOnly && extras.Count == 0)
            throw new IOException($"Impossible de joindre \\\\{settings.Nas}. Vérifie que la NAS est allumée et accessible.");
        if (!settings.HasNas && extras.Count == 0)
            throw new IOException("Aucun dossier à lire : indique le nom de la NAS ou ajoute un dossier dans les réglages.");
        var roots = nasRoots.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var previous = Snapshot.Books;

        var exclude = new HashSet<string>(settings.ExcludeFolders, StringComparer.OrdinalIgnoreCase);
        var found = keepNas ? previous.Where(b => settings.OnMainNas(b.Path)).ToList() : new List<Book>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                // A folder that cannot be reached right now (friend's NAS turned off, USB disk unplugged) keeps its books.
                var prefix = root.TrimEnd('\\') + "\\";
                found.AddRange(previous.Where(b => b.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
                Log.Info($"Dossier injoignable, livres gardés : {root}");
                continue;
            }
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                ScanCurrent = dir.FullName;
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos("*", options).ToList(); }
                catch { continue; }
                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (!exclude.Contains(d.Name) && !d.Name.StartsWith('.')) stack.Push(d);
                    }
                    else if (e is FileInfo f && Extensions.Contains(f.Extension))
                    {
                        found.Add(Classify(root, f));
                        ScanFound = found.Count;
                    }
                }
            }
        }

        var snapshot = new LibrarySnapshot { Books = found, ScannedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        lock (Gate)
        {
            _snapshot = snapshot;
            _byId = found.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
        }
        Json.Save(Paths.LibraryFile, snapshot);
        UserData.Reconcile(found);
        SharedCache.PushLibrary();
        Enricher.Restart();
    }

    public static Book Classify(string root, FileInfo f)
    {
        var fmt = f.Extension.TrimStart('.').ToLowerInvariant();
        var share = root.TrimEnd('\\').Split('\\').Last();
        var rel = System.IO.Path.GetRelativePath(root, f.DirectoryName!);
        var folders = rel == "." ? new List<string>() : rel.Split('\\').ToList();
        folders.Insert(0, share);

        string? kind = null;
        var crumbs = new List<string>();
        // The first section folder wins, except that a "comics" folder inside an "ebooks"
        // folder (\\nas\Ebooks\comics\Marvel) still makes it a comic.
        for (int i = 0; i < folders.Count; i++)
        {
            if (!Sections.TryGetValue(folders[i], out var k)) continue;
            if (kind == "ebook" && k == "ebook") continue;
            kind = k;
            crumbs = folders.Skip(i + 1).ToList();
            if (k != "ebook") break;
        }
        // A "Webtoons" folder wins wherever it is (Comics\Webtoons\Solo Leveling): they read differently.
        var wt = folders.FindIndex(x => Sections.TryGetValue(x, out var k) && k == "webtoon");
        if (wt >= 0) { kind = "webtoon"; crumbs = folders.Skip(wt + 1).ToList(); }

        bool sorted = kind != null;
        if (kind == null)
        {
            kind = fmt switch
            {
                "cbz" or "cbr" or "cb7" or "cbt" or "cba" => "comic",
                "epub" or "mobi" or "azw3" => "ebook",
                _ => "pdf",
            };
            crumbs = folders;
        }
        else if (kind == "ebook" && fmt is "cbz" or "cbr" or "cb7" or "cbt" or "cba")
        {
            kind = "comic";
        }

        // Collapse "Batman / Batman" style repetitions.
        var clean = new List<string>();
        foreach (var c in crumbs)
            if (clean.Count == 0 || !string.Equals(clean[^1], c, StringComparison.OrdinalIgnoreCase)) clean.Add(c);

        return new Book(
            IdFor(f.FullName),
            f.FullName,
            System.IO.Path.GetFileNameWithoutExtension(f.Name),
            kind,
            fmt,
            f.Length,
            new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            clean.ToArray(),
            sorted);
    }
}
