using System.Collections.Concurrent;

namespace AffiliateVideoFlow;

/// <summary>
/// Downloads video URLs captured by the traffic sniffer.
/// Supports direct MP4 links and HLS (.m3u8) playlists via ffmpeg.
/// Videos are saved to <c>Downloads/{kolNickname}/</c>.
/// </summary>
public class VideoDownloader
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http;

    // Thread-safe set to avoid downloading the same URL twice
    private readonly ConcurrentDictionary<string, byte> _downloaded = new();

    public int TotalDownloaded => _downloaded.Count;

    public VideoDownloader(AppConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        // Mimic a mobile browser so CDN servers don't reject the request
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a list of video URLs to <c>Downloads/{kolNickname}/</c>.
    /// Already-downloaded URLs are skipped.
    /// </summary>
    public async Task DownloadAllAsync(IEnumerable<string> urls, string kolNickname)
    {
        string outputDir = Path.Combine(_cfg.DownloadsFolder, SanitizeFilename(kolNickname));
        Directory.CreateDirectory(outputDir);

        var pending = urls
            .Where(u => !string.IsNullOrWhiteSpace(u) && !_downloaded.ContainsKey(u))
            .Distinct()
            .ToList();

        Console.WriteLine($"[DL] {pending.Count} new video(s) to download → {outputDir}");

        int index = _downloaded.Count + 1;
        foreach (string url in pending)
        {
            string filename = BuildFilename(url, kolNickname, index);
            string outputPath = Path.Combine(outputDir, filename);

            bool success = IsHlsUrl(url)
                ? await DownloadHlsAsync(url, outputPath)
                : await DownloadDirectAsync(url, outputPath);

            if (success)
            {
                _downloaded.TryAdd(url, 0);
                Console.WriteLine($"[DL] [{index}] OK  → {filename}");
                index++;
            }
            else
            {
                Console.WriteLine($"[DL] [{index}] FAIL → {url}");
            }
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<bool> DownloadDirectAsync(string url, string outputPath)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var fs = File.Create(outputPath);
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(fs);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DL] Direct download error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Downloads an HLS stream using ffmpeg, which must be on PATH or next to adb.exe.
    /// </summary>
    private async Task<bool> DownloadHlsAsync(string m3u8Url, string outputPath)
    {
        // Ensure output ends with .mp4
        if (!outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            outputPath = Path.ChangeExtension(outputPath, ".mp4");

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "ffmpeg",
                Arguments = $"-i \"{m3u8Url}\" -c copy -y \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.");

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DL] HLS/ffmpeg error: {ex.Message}");
            return false;
        }
    }

    private static bool IsHlsUrl(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string BuildFilename(string url, string kolNickname, int index)
    {
        // Prefer the last path segment as a hint; fall back to index-based name
        string ext = ".mp4";
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath;
            string seg  = path.Split('/').Last(s => !string.IsNullOrEmpty(s));
            string e    = Path.GetExtension(seg);
            if (!string.IsNullOrEmpty(e)) ext = e;
        }
        catch { /* ignore */ }

        return $"{SanitizeFilename(kolNickname)}_{index:D4}{ext}";
    }

    private static string SanitizeFilename(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }
}
