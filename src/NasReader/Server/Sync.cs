using System.Text.Json;

namespace NasReader.Server;

/// <summary>
/// Shares progress, favorites and the "to read" list with the phone and the tablet through
/// one file on the NAS. Each side merges, keeping what is newer, then writes the result back.
/// </summary>
public static class Sync
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static System.Threading.Timer? _periodic, _debounce;
    static int _version;

    public static long LastSync;
    public static string? LastError;
    /// <summary>Goes up each time data from another device arrived, so the screen can reload it.</summary>
    public static int Version => Volatile.Read(ref _version);

    public static string Folder(Settings s) =>
        string.IsNullOrWhiteSpace(s.SyncFolder) ? $@"\\{s.Nas}\Comics Mangas Livre\.nasreader" : s.SyncFolder.TrimEnd('\\');

    public static void Start()
    {
        _periodic ??= new System.Threading.Timer(_ => _ = RunAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(2));
    }

    /// <summary>Local data changed: send it a few seconds later, once the reader has settled.</summary>
    public static void Changed()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => _ = RunAsync(), null, 4000, Timeout.Infinite);
    }

    public static async Task RunAsync()
    {
        var s = Settings.Current;
        if (!s.SyncEnabled) return;
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var dir = Folder(s);
            var file = Path.Combine(dir, "userdata.json");
            Directory.CreateDirectory(dir);

            if (File.Exists(file))
            {
                UserDataFile? remote;
                await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    remote = await JsonSerializer.DeserializeAsync<UserDataFile>(fs, Json.Options);
                if (remote != null && UserData.Merge(remote)) Interlocked.Increment(ref _version);
            }

            var tmp = Path.Combine(dir, $"userdata.{Environment.MachineName}.tmp");
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(UserData.Export(), Json.Options));
            File.Move(tmp, file, true);
            LastSync = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"Synchronisation impossible avec {Folder(s)} : {ex.Message}";
        }
        finally
        {
            Gate.Release();
        }
    }
}
