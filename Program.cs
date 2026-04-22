using System.Text.Json;
using System.Text.Json.Serialization;
using AffiliateVideoFlow;

// -- Config ------------------------------------------------------------------

AppConfig cfg = LoadConfig("config.json");
Directory.CreateDirectory(cfg.DownloadsFolder);

// -- Banner ------------------------------------------------------------------

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║         AffiliateVideoFlow - Shopee Crawler      ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();

// -- ADB check ---------------------------------------------------------------

var adb = new AdbController(cfg.AdbPath, cfg.DeviceSerial);

Console.WriteLine("[INIT] Checking ADB devices...");
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

// -- KOL input ---------------------------------------------------------------

Console.Write("\nEnter KOL nickname (Shopee username): ");
string kolNickname = Console.ReadLine()?.Trim() ?? "";
if (string.IsNullOrEmpty(kolNickname))
{
    Console.WriteLine("[ERROR] Nickname cannot be empty.");
    return 1;
}

Console.Write("How many videos to download? (default: 10): ");
string countInput = Console.ReadLine()?.Trim() ?? "";
int videoCount = int.TryParse(countInput, out int n) && n > 0 ? n : cfg.VideoCount;

// -- Navigate to KOL video tab -----------------------------------------------

var navigator = new ShopeeNavigator(new AdbController(cfg.AdbPath, cfg.DeviceSerial), cfg);
await navigator.NavigateToKolVideosAsync(kolNickname);

// -- Collect Shopee share links via ADB Share -> Copy Link -------------------

Console.WriteLine("\n[STEP 1] Collecting Shopee share links via ADB...");
List<string> shopeeLinks = await navigator.CollectVideoShareLinksAsync(videoCount);

Console.WriteLine($"\n[INFO] Collected {shopeeLinks.Count} share link(s).");
if (shopeeLinks.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] No links collected. Check:");
    Console.WriteLine("       - The KOL profile has videos.");
    Console.WriteLine("       - The Share / Copy Link buttons are at the configured coordinates.");
    Console.WriteLine("       - UIAutomator can detect the buttons (try adjusting coordinates in config.json).");
    Console.ResetColor();
    return 1;
}

// -- Extract direct MP4 URLs via Playwright ----------------------------------

Console.WriteLine("\n[STEP 2] Extracting direct MP4 URLs via browser (downloadvideo.vn)...");
await using var extractor = new ShopeeVideoLinkExtractor();
await extractor.InitAsync(headless: true);

var mp4Urls = new List<string>();
for (int i = 0; i < shopeeLinks.Count; i++)
{
    string shareLink = shopeeLinks[i];
    Console.Write($"  [{i + 1}/{shopeeLinks.Count}] {shareLink} => ");

    string? mp4 = await extractor.GetMp4UrlAsync(shareLink);
    if (!string.IsNullOrEmpty(mp4))
    {
        mp4Urls.Add(mp4);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("OK");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("FAIL (skipped)");
        Console.ResetColor();
    }
}

// -- Download MP4 files ------------------------------------------------------

Console.WriteLine($"\n[STEP 3] Downloading {mp4Urls.Count} MP4 file(s)...");
if (mp4Urls.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] No MP4 URLs extracted. The downloader site layout may have changed.");
    Console.ResetColor();
    return 1;
}

var downloader = new VideoDownloader(cfg);
await downloader.DownloadAllAsync(mp4Urls, kolNickname);

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"[DONE] {downloader.TotalDownloaded} video(s) saved to {cfg.DownloadsFolder}/{kolNickname}/");
Console.ResetColor();
return 0;

// ---------------------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------------------

static AppConfig LoadConfig(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"[CONFIG] {path} not found - using defaults.");
        return AppConfig.Default;
    }
    string json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? AppConfig.Default;
}

// -- Config model ------------------------------------------------------------

public record Coordinate(int X, int Y);

public record CoordinateMap(
    Coordinate SearchButton,
    Coordinate FirstSearchResult,
    Coordinate VideoTab,
    Coordinate ScrollFrom,
    Coordinate ScrollTo,
    Coordinate FirstVideoThumbnail,
    Coordinate ShareButton,
    Coordinate CopyLinkButton,
    Coordinate VideoSwipeFrom,
    Coordinate VideoSwipeTo);

public record AppConfig(
    string        AdbPath,
    string        DeviceSerial,
    string        ShopeePackage,
    string        ShopeeDomain,
    string        DownloadsFolder,
    int           VideoCount,
    int           AppLaunchDelayMs,
    int           PageLoadDelayMs,
    int           VideoLoadDelayMs,
    CoordinateMap Coordinates)
{
    public static AppConfig Default => new(
        AdbPath: "D:\\Tools\\platform-tools-latest-windows\\platform-tools\\adb.exe",
        DeviceSerial:     "",
        ShopeePackage:    "com.shopee.vn",
        ShopeeDomain:     "shopee.vn",
        DownloadsFolder:  "Downloads",
        VideoCount:       10,
        AppLaunchDelayMs: 3000,
        PageLoadDelayMs:  2500,
        VideoLoadDelayMs: 2000,
        Coordinates: new CoordinateMap(
            SearchButton:        new Coordinate(540,  80),
            FirstSearchResult:   new Coordinate(540, 350),
            VideoTab:            new Coordinate(540, 280),
            ScrollFrom:          new Coordinate(540, 1600),
            ScrollTo:            new Coordinate(540,  600),
            FirstVideoThumbnail: new Coordinate(270,  500),
            ShareButton:         new Coordinate(980, 1700),
            CopyLinkButton:      new Coordinate(540, 1500),
            VideoSwipeFrom:      new Coordinate(540, 1400),
            VideoSwipeTo:        new Coordinate(540,  400)
        )
    );
}
