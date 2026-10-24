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
        { ".cbz", ".cbr", ".cb7", ".pdf", ".epub", ".mobi", ".azw3" };

    // Folder names that mark a section of the library, whatever share they live in.
    static readonly Dictionary<string, string> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comics"] = "comic", ["comic"] = "comic", ["bd"] = "comic", ["bandes dessinÃ©es"] = "comic",
        ["mangas"] = "manga", ["manga"] = "manga",
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

    public static List<string> EffectiveRoots(Settings s) =>
        s.Roots.Count > 0 ? s.Roots : DiscoverShares(s.Nas);

    public static void StartScan()
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
            try { Scan(token); ScanError = null; }
            catch (Exception ex) { ScanError = ex.Message; }
            finally { Scanning = false; ScanCurrent = ""; }
        });
    }

    static void Scan(CancellationToken token)
    {
        var settings = Settings.Current;
        ScanFound = 0;
        var roots = EffectiveRoots(settings);
        if (roots.Count == 0) throw new IOException($"Impossible de joindre \\\\{settings.Nas}. VÃ©rifie que la NAS est allumÃ©e et accessible.");

        var exclude = new HashSet<string>(settings.ExcludeFolders, StringComparer.OrdinalIgnoreCase);
        var found = new List<Book>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
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

        bool sorted = kind != null;
        if (kind == null)
        {
            kind = fmt switch
            {
                "cbz" or "cbr" or "cb7" => "comic",
                "epub" or "mobi" or "azw3" => "ebook",
                _ => "pdf",
            };
            crumbs = folders;
        }
        else if (kind == "ebook" && fmt is "cbz" or "cbr" or "cb7")
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
