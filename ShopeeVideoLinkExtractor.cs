using Microsoft.Playwright;
using System.Text.RegularExpressions;

namespace AffiliateVideoFlow;

/// <summary>
/// Uses a headless Chromium browser (via Playwright) to submit a Shopee
/// share URL to downloadvideo.vn and capture the direct .mp4 download link.
/// </summary>
public sealed class ShopeeVideoLinkExtractor : IAsyncDisposable
{
    private const string DownloaderUrl = "https://downloadvideo.vn/vi/tai-video-shopee";

    private IPlaywright? _playwright;
    private IBrowser?    _browser;
    private IBrowserContext? _context;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the headless browser. Call once before any <see cref="GetMp4UrlAsync"/> calls.
    /// </summary>
    public async Task InitAsync(bool headless = true)
    {
        _playwright = await Playwright.CreateAsync();
        _browser    = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Args     = new[] { "--disable-blink-features=AutomationControlled" }
        });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/124.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
        });
    }

    /// <summary>
    /// Submits <paramref name="shopeeShareUrl"/> to downloadvideo.vn and
    /// returns the first .mp4 URL found in the response.
    /// Returns <c>null</c> if extraction fails.
    /// </summary>
    public async Task<string?> GetMp4UrlAsync(string shopeeShareUrl)
    {
        if (_context == null)
            throw new InvalidOperationException("Call InitAsync() first.");

        var page = await _context.NewPageAsync();
        try
        {
            // Intercept network responses to catch the MP4 link early
            string? interceptedMp4 = null;
            page.Response += (_, resp) =>
            {
                if (interceptedMp4 == null && IsMp4Response(resp.Url))
                    interceptedMp4 = resp.Url;
            };

            await page.GotoAsync(DownloaderUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 30_000
            });

            // ── Fill the URL input ────────────────────────────────────────
            ILocator? input = await FindInputAsync(page);
            if (input == null)
            {
                Console.WriteLine("[EXTRACTOR] Could not find URL input field on the page.");
                return null;
            }

            await input.ClearAsync();
            await input.FillAsync(shopeeShareUrl);
            await Task.Delay(300);

            // ── Click the download / submit button ────────────────────────
            ILocator? button = await FindSubmitButtonAsync(page);
            if (button == null)
            {
                Console.WriteLine("[EXTRACTOR] Could not find submit button on the page.");
                return null;
            }

            await button.ClickAsync();

            // ── Wait for result ───────────────────────────────────────────
            // Strategy 1: a link with an .mp4 href appears in the DOM
            string? mp4Url = await WaitForMp4LinkInDomAsync(page);
            if (!string.IsNullOrEmpty(mp4Url)) return mp4Url;

            // Strategy 2: an API response intercepted via network
            if (!string.IsNullOrEmpty(interceptedMp4)) return interceptedMp4;

            // Strategy 3: parse all hrefs on the page
            mp4Url = await ScanPageForMp4Async(page);
            return mp4Url;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EXTRACTOR] Error: {ex.Message}");
            return null;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.DisposeAsync();
        if (_browser  != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static async Task<ILocator?> FindInputAsync(IPage page)
    {
        // Try common selectors for a video-URL input field
        string[] selectors =
        {
            "input[type='text'][placeholder*='http']",
            "input[type='url']",
            "input[name*='url' i]",
            "input[id*='url' i]",
            "input[class*='url' i]",
            "input[placeholder*='link' i]",
            "input[placeholder*='video' i]",
            "textarea[name*='url' i]",
            "input[type='text']:first-of-type",
        };

        foreach (string sel in selectors)
        {
            var loc = page.Locator(sel).First;
            try
            {
                if (await loc.IsVisibleAsync()) return loc;
            }
            catch { /* not found */ }
        }
        return null;
    }

    private static async Task<ILocator?> FindSubmitButtonAsync(IPage page)
    {
        // Try buttons/submits with common Vietnamese/English text
        string[] texts = { "Tải về", "Tải xuống", "Download", "Tải", "Get" };
        foreach (string t in texts)
        {
            var loc = page.GetByRole(AriaRole.Button, new() { Name = t });
            try
            {
                if (await loc.First.IsVisibleAsync()) return loc.First;
            }
            catch { /* try next */ }
        }

        // Fallback: first visible submit button or button[type=submit]
        foreach (string sel in new[] { "button[type='submit']", "input[type='submit']", "button" })
        {
            var loc = page.Locator(sel).First;
            try
            {
                if (await loc.IsVisibleAsync()) return loc;
            }
            catch { /* try next */ }
        }
        return null;
    }

    /// <summary>
    /// Polls the page DOM for up to 20 seconds waiting for a visible anchor
    /// whose href ends with .mp4 (or contains a known CDN pattern).
    /// </summary>
    private static async Task<string?> WaitForMp4LinkInDomAsync(IPage page)
    {
        const int maxWaitMs  = 20_000;
        const int pollMs     = 500;
        int elapsed          = 0;

        while (elapsed < maxWaitMs)
        {
            // Look for <a href="...mp4..."> elements
            var anchors = await page.QuerySelectorAllAsync("a[href*='.mp4'], a[href*='video']");
            foreach (var a in anchors)
            {
                string? href = await a.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href) && IsMp4Url(href))
                    return href;
            }

            // Also check for text nodes or pre/code blocks that might contain the URL
            string? textUrl = await TryExtractUrlFromPageTextAsync(page);
            if (!string.IsNullOrEmpty(textUrl)) return textUrl;

            await Task.Delay(pollMs);
            elapsed += pollMs;
        }
        return null;
    }

    private static async Task<string?> ScanPageForMp4Async(IPage page)
    {
        string html = await page.ContentAsync();
        var m = Regex.Match(html, @"https?://[^\s""'<>]+\.mp4[^\s""'<>]*");
        return m.Success ? m.Value : null;
    }

    private static async Task<string?> TryExtractUrlFromPageTextAsync(IPage page)
    {
        try
        {
            string? bodyText = await page.Locator("body").TextContentAsync(
                new LocatorTextContentOptions { Timeout = 1000 });
            if (bodyText == null) return null;

            var m = Regex.Match(bodyText, @"https?://[^\s""'<>\x00-\x1f]+\.mp4[^\s""'<>\x00-\x1f]*");
            return m.Success ? m.Value : null;
        }
        catch { return null; }
    }

    private static bool IsMp4Url(string url) =>
        Regex.IsMatch(url, @"\.mp4(\?|$)", RegexOptions.IgnoreCase)
        || url.Contains("video", StringComparison.OrdinalIgnoreCase) && url.StartsWith("http");

    private static bool IsMp4Response(string url) =>
        Regex.IsMatch(url, @"\.mp4(\?|$|%)", RegexOptions.IgnoreCase);
}
