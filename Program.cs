using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AffiliateVideoFlow;

// ── Config ───────────────────────────────────────────────────────────────────

AppConfig cfg = LoadConfig("config.json");
Directory.CreateDirectory(cfg.DownloadsFolder);

// ── Banner ───────────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║         AffiliateVideoFlow – Shopee Crawler      ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();

// ── ADB check ────────────────────────────────────────────────────────────────

var adb = new AdbController(cfg.AdbPath, cfg.DeviceSerial);

Console.WriteLine("[INIT] Checking ADB devices…");
var devices = await adb.GetConnectedDevicesAsync();
if (devices.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[ERROR] No Android device found via ADB.");
    Console.WriteLine("        Connect your phone, enable USB Debugging, and retry.");
    Console.ResetColor();
    return 1;
}

if (string.IsNullOrEmpty(cfg.DeviceSerial) && devices.Count > 1)
{
    Console.WriteLine("Multiple devices found:");
    for (int i = 0; i < devices.Count; i++)
        Console.WriteLine($"  [{i}] {devices[i]}");
    Console.Write("Select device number: ");
    if (int.TryParse(Console.ReadLine(), out int sel) && sel >= 0 && sel < devices.Count)
        cfg = cfg with { DeviceSerial = devices[sel] };
}
else if (string.IsNullOrEmpty(cfg.DeviceSerial))
{
    cfg = cfg with { DeviceSerial = devices[0] };
}

Console.WriteLine($"[INIT] Using device: {cfg.DeviceSerial}");

// ── KOL input ────────────────────────────────────────────────────────────────

Console.Write("\nEnter KOL nickname (Shopee username): ");
string kolNickname = Console.ReadLine()?.Trim() ?? "";
if (string.IsNullOrEmpty(kolNickname))
{
    Console.WriteLine("[ERROR] Nickname cannot be empty.");
    return 1;
}

// ── Mitmproxy setup info ──────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("──────────────────────────────────────────────────────");
Console.WriteLine("BEFORE CONTINUING – complete these one-time setup steps");
Console.WriteLine("──────────────────────────────────────────────────────");
Console.WriteLine($"1. Set your phone's WiFi proxy to:  {GetLocalIp()}:{cfg.MitmproxyPort}");
Console.WriteLine("2. Open http://mitm.it in the phone browser and install the CA cert.");
Console.WriteLine("3. Trust the cert in Settings → Security → CA Certificates.");
Console.WriteLine("──────────────────────────────────────────────────────");
Console.Write("\nPress ENTER when ready… ");
Console.ReadLine();

// ── Captured video URLs ───────────────────────────────────────────────────────

var capturedUrls = new ConcurrentBag<string>();

// ── HTTP collector (receives URLs from traffic_sniffer.py) ────────────────────

using var cts = new CancellationTokenSource();
var collectorTask = RunCollectorAsync(cfg.CollectorPort, capturedUrls, cts.Token);

// ── Start mitmproxy ───────────────────────────────────────────────────────────

Process? mitm = null;
try
{
    Console.WriteLine($"\n[MITM] Starting mitmproxy on port {cfg.MitmproxyPort}…");
    mitm = StartMitmproxy(cfg);
    await Task.Delay(1500); // let it initialise
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[MITM] Could not auto-start mitmproxy: {ex.Message}");
    Console.WriteLine("       Please start it manually:");
    Console.WriteLine($"       mitmdump -p {cfg.MitmproxyPort} -s traffic_sniffer.py --set collector_port={cfg.CollectorPort}");
    Console.ResetColor();
}

// ── Navigate + scroll ─────────────────────────────────────────────────────────

var navigator = new ShopeeNavigator(new AdbController(cfg.AdbPath, cfg.DeviceSerial), cfg);
await navigator.NavigateToKolVideosAsync(kolNickname);
await navigator.ScrollThroughVideosAsync(cfg.ScrollCount, cfg.ScrollDelayMs);

// ── Give the sniffer a moment to flush remaining URLs ─────────────────────────

Console.WriteLine("\n[INFO] Waiting 3 s for last URLs to arrive…");
await Task.Delay(3000);

// ── Shut down mitmproxy ───────────────────────────────────────────────────────

cts.Cancel();
mitm?.Kill(entireProcessTree: true);

// ── Download ──────────────────────────────────────────────────────────────────

var allUrls = capturedUrls.Distinct().ToList();
Console.WriteLine($"\n[INFO] Captured {allUrls.Count} unique video URL(s).");

if (allUrls.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] No URLs captured. Check:");
    Console.WriteLine("       • Proxy is set correctly on the phone.");
    Console.WriteLine("       • mitmproxy CA cert is installed and trusted.");
    Console.WriteLine("       • The phone actually loaded videos while scrolling.");
    Console.ResetColor();
}
else
{
    var downloader = new VideoDownloader(cfg);
    await downloader.DownloadAllAsync(allUrls, kolNickname);
    Console.WriteLine($"\n[DONE] {downloader.TotalDownloaded} video(s) saved to {cfg.DownloadsFolder}/{kolNickname}/");
}

return 0;

// ─────────────────────────────────────────────────────────────────────────────
// Local functions
// ─────────────────────────────────────────────────────────────────────────────

static async Task RunCollectorAsync(
    int port,
    ConcurrentBag<string> bag,
    CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    Console.WriteLine($"[COLLECTOR] Listening on http://localhost:{port}/");

    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext? ctx = null;
        try
        {
            ctx = await listener.GetContextAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException) { break; }
        catch { break; }

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                string body = await reader.ReadToEndAsync();
                var msg = JsonSerializer.Deserialize<VideoUrlMessage>(body);
                if (!string.IsNullOrWhiteSpace(msg?.Url))
                {
                    bag.Add(msg.Url);
                    Console.WriteLine($"[COLLECTOR] + {msg.Url}");
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch { /* ignore malformed requests */ }
        }, ct);
    }

    listener.Stop();
}

static Process StartMitmproxy(AppConfig cfg)
{
    var psi = new ProcessStartInfo
    {
        FileName  = "mitmdump",
        Arguments = $"-p {cfg.MitmproxyPort} -s traffic_sniffer.py " +
                    $"--set collector_port={cfg.CollectorPort}",
        UseShellExecute = false,
        CreateNoWindow  = false  // keep visible so user can see captured URLs
    };
    return Process.Start(psi) ?? throw new InvalidOperationException("mitmdump failed to start.");
}

static AppConfig LoadConfig(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"[CONFIG] {path} not found – using defaults.");
        return AppConfig.Default;
    }
    string json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? AppConfig.Default;
}

static string GetLocalIp()
{
    try
    {
        using var socket = new System.Net.Sockets.UdpClient("8.8.8.8", 53);
        return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint!).Address.ToString();
    }
    catch { return "YOUR-PC-IP"; }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

record VideoUrlMessage([property: JsonPropertyName("url")] string? Url);

// ── Config model ──────────────────────────────────────────────────────────────

public record Coordinate(int X, int Y);

public record CoordinateMap(
    Coordinate SearchButton,
    Coordinate FirstSearchResult,
    Coordinate VideoTab,
    Coordinate ScrollFrom,
    Coordinate ScrollTo);

public record AppConfig(
    string AdbPath,
    string DeviceSerial,
    string ShopeePackage,
    string ShopeeDomain,
    int    CollectorPort,
    int    MitmproxyPort,
    string DownloadsFolder,
    int    ScrollCount,
    int    ScrollDelayMs,
    int    AppLaunchDelayMs,
    int    PageLoadDelayMs,
    CoordinateMap Coordinates)
{
    public static AppConfig Default => new(
        AdbPath:         "adb.exe",
        DeviceSerial:    "",
        ShopeePackage:   "com.shopee.vn",
        ShopeeDomain:    "shopee.vn",
        CollectorPort:   5050,
        MitmproxyPort:   8080,
        DownloadsFolder: "Downloads",
        ScrollCount:     25,
        ScrollDelayMs:   2000,
        AppLaunchDelayMs:3000,
        PageLoadDelayMs: 2500,
        Coordinates: new CoordinateMap(
            SearchButton:      new Coordinate(540, 80),
            FirstSearchResult: new Coordinate(540, 350),
            VideoTab:          new Coordinate(540, 280),
            ScrollFrom:        new Coordinate(540, 1600),
            ScrollTo:          new Coordinate(540, 600)
        )
    );
}
