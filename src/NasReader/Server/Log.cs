namespace NasReader.Server;

/// <summary>
/// A short journal of what went wrong (and a few milestones), for "Signaler un problème".
/// Kept in %LOCALAPPDATA%\NasReader\log.txt, at most about 1 MB.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 1 << 20;

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex == null ? message : $"{message} : {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message.Replace('\n', ' ').Replace('\r', ' ')}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var f = new FileInfo(Paths.LogFile);
                if (f.Exists && f.Length > MaxBytes)
                {
                    // Keep the most recent half.
                    var text = File.ReadAllText(f.FullName);
                    File.WriteAllText(f.FullName, text[(text.Length / 2)..]);
                }
                File.AppendAllText(Paths.LogFile, line);
            }
            catch { }
        }
    }

    /// <summary>The end of the journal, with what helps to understand a report.</summary>
    public static string Report()
    {
        string tail;
        lock (Gate)
        {
            try { tail = File.Exists(Paths.LogFile) ? File.ReadAllText(Paths.LogFile) : ""; }
            catch (Exception ex) { tail = ex.Message; }
        }
        if (tail.Length > 60_000) tail = tail[^60_000..];
        var s = Settings.Current;
        var snap = Library.Snapshot;
        return $"""
            Nas Reader {Updater.CurrentText} · Windows {Environment.OSVersion.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")}
            NAS joignable : {(Reach.Nas() ? "oui" : "non")} · {snap.Books.Count} livres · dernier scan {DateTimeOffset.FromUnixTimeMilliseconds(snap.ScannedAt).LocalDateTime:g}
            Synchro : {(s.SyncEnabled ? "oui" : "non")}{(Sync.LastError != null ? " · " + Sync.LastError : "")}
            Cache : {s.CacheLimitGb} Go max · {Books.CacheSize() / 1_000_000} Mo utilisés

            {tail}
            """;
    }
}
