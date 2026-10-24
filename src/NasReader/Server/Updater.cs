using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace NasReader.Server;

/// <summary>
/// Looks for a newer release on GitHub, downloads its installer in the background and runs it
/// when asked, or silently when the app closes.
/// </summary>
public static class Updater
{
    public const string Repo = "AdamchDarkness/nas-reader";

    record Asset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url, [property: JsonPropertyName("size")] long Size);
    record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("body")] string? Body, [property: JsonPropertyName("html_url")] string Page, [property: JsonPropertyName("assets")] List<Asset> Assets);

    public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public static string State = "none"; // none | available | downloading | ready | error
    public static string? Latest, Notes, PageUrl, Error;
    static string? _installer;
    static System.Threading.Timer? _timer;

    /// <summary>Installed by the setup (per user), not run from a copied exe.</summary>
    public static bool Installed => Environment.ProcessPath?.StartsWith(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), StringComparison.OrdinalIgnoreCase) == true;

    public static void Start() => _timer ??= new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

    static Version Parse(string tag) => Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var v) ? v : new Version(0, 0);

    public static async Task CheckAsync()
    {
        if (State is "downloading" or "ready") return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"NasReader/{CurrentText}");
            var rel = await http.GetFromJsonAsync<Release>($"https://api.github.com/repos/{Repo}/releases/latest");
            if (rel == null) return;
            var latest = Parse(rel.Tag);
            if (latest <= new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build))) { State = "none"; return; }

            Latest = $"{latest.Major}.{latest.Minor}.{Math.Max(0, latest.Build)}";
            Notes = rel.Body;
            PageUrl = rel.Page;
            State = "available";
            var asset = rel.Assets.FirstOrDefault(a => a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset == null || !Installed || !Settings.Current.AutoUpdate) return;

            State = "downloading";
            var dir = Path.Combine(Paths.Root, "updates");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, asset.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != asset.Size)
            {
                using var http2 = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
                http2.DefaultRequestHeaders.UserAgent.ParseAdd($"NasReader/{CurrentText}");
                await using var src = await http2.GetStreamAsync(asset.Url);
                await using (var dst = File.Create(path + ".part")) await src.CopyToAsync(dst);
                File.Move(path + ".part", path, true);
            }
            _installer = path;
            State = "ready";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            if (State == "downloading") State = "available";
        }
    }

    /// <summary>Runs the downloaded setup. With relaunch the app comes back once updated.</summary>
    public static bool Install(bool relaunch)
    {
        if (State != "ready" || _installer == null || !File.Exists(_installer)) return false;
        var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" + (relaunch ? "" : " /NORUN");
        Process.Start(new ProcessStartInfo(_installer, args) { UseShellExecute = true });
        State = "none";
        return true;
    }

    public static object Status() => new
    {
        current = CurrentText, latest = Latest, state = State, notes = Notes, page = PageUrl, error = Error,
        installed = Installed, auto = Settings.Current.AutoUpdate,
    };
}
