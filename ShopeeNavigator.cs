namespace AffiliateVideoFlow;

/// <summary>
/// High-level Shopee navigation built on top of <see cref="AdbController"/>.
/// All tap coordinates are read from <see cref="AppConfig"/> so they can be
/// tuned per-device without recompiling.
/// </summary>
public class ShopeeNavigator
{
    private readonly AdbController _adb;
    private readonly AppConfig _cfg;

    public ShopeeNavigator(AdbController adb, AppConfig cfg)
    {
        _adb = adb;
        _cfg = cfg;
    }

    // ── Public workflow ──────────────────────────────────────────────────────

    /// <summary>
    /// Full flow: opens Shopee, searches for <paramref name="kolNickname"/>,
    /// opens their profile and navigates to the Video tab.
    /// </summary>
    public async Task NavigateToKolVideosAsync(string kolNickname)
    {
        Console.WriteLine($"[NAV] Launching Shopee ({_cfg.ShopeePackage})…");
        await _adb.ForceStopAppAsync(_cfg.ShopeePackage);
        await Task.Delay(800);
        await _adb.LaunchAppAsync(_cfg.ShopeePackage);
        await Task.Delay(_cfg.AppLaunchDelayMs);

        // Try deep-link shortcut first; fall back to UI search
        bool deepLinkWorked = await TryDeepLinkAsync(kolNickname);
        if (!deepLinkWorked)
        {
            await SearchForKolAsync(kolNickname);
        }

        await Task.Delay(_cfg.PageLoadDelayMs);
        await NavigateToVideoTabAsync();
        Console.WriteLine($"[NAV] Reached video tab for '{kolNickname}'.");
    }

    /// <summary>
    /// Scrolls through the video tab <paramref name="scrollCount"/> times,
    /// pausing between scrolls to let the network traffic fire.
    /// </summary>
    public async Task ScrollThroughVideosAsync(int scrollCount, int delayBetweenScrollsMs)
    {
        var coords = _cfg.Coordinates;
        Console.WriteLine($"[NAV] Scrolling {scrollCount} times to trigger video loads…");

        for (int i = 0; i < scrollCount; i++)
        {
            await _adb.SwipeAsync(
                coords.ScrollFrom.X, coords.ScrollFrom.Y,
                coords.ScrollTo.X,   coords.ScrollTo.Y,
                durationMs: 600);

            Console.WriteLine($"[NAV] Scroll {i + 1}/{scrollCount}");
            await Task.Delay(delayBetweenScrollsMs);
        }
    }

    // ── Private steps ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to jump directly to the KOL's profile via an Android deep link.
    /// Returns true if the deep link resolved to a profile page.
    /// </summary>
    private async Task<bool> TryDeepLinkAsync(string kolNickname)
    {
        // Shopee's scheme varies by region. We try the most common patterns.
        string[] deepLinks =
        {
            $"shopee://profile/{Uri.EscapeDataString(kolNickname)}",
            $"https://{_cfg.ShopeeDomain}/{Uri.EscapeDataString(kolNickname)}"
        };

        foreach (string link in deepLinks)
        {
            Console.WriteLine($"[NAV] Trying deep link: {link}");
            await _adb.OpenUrlAsync(link);
            await Task.Delay(_cfg.PageLoadDelayMs);

            // Check if we landed on a profile page by looking for "Video" tab
            var center = await _adb.FindElementCenterAsync("Video", partial: true);
            if (center != null)
            {
                Console.WriteLine("[NAV] Deep link succeeded.");
                return true;
            }
        }

        Console.WriteLine("[NAV] Deep link did not land on profile. Falling back to search.");
        return false;
    }

    /// <summary>
    /// Navigates via Shopee's search UI to find the KOL and open their profile.
    /// </summary>
    private async Task SearchForKolAsync(string kolNickname)
    {
        Console.WriteLine("[NAV] Opening search via UI…");
        var coords = _cfg.Coordinates;

        // Tap the search bar (top of Home screen)
        bool tappedSearch = await _adb.TapElementAsync("Search", partial: true);
        if (!tappedSearch)
        {
            await _adb.TapAsync(coords.SearchButton.X, coords.SearchButton.Y);
        }
        await Task.Delay(600);

        // Type the KOL nickname
        await _adb.TypeTextAsync(kolNickname);
        await Task.Delay(400);
        await _adb.PressKeyAsync(AdbController.KEYCODE_ENTER);
        await Task.Delay(_cfg.PageLoadDelayMs);

        // Switch to the "Shop" or "User" tab in search results
        bool switchedToUsers = await _adb.TapElementAsync("Shop", partial: false)
                            || await _adb.TapElementAsync("User", partial: false)
                            || await _adb.TapElementAsync("Seller", partial: false);
        if (!switchedToUsers)
            Console.WriteLine("[NAV] WARN: Could not switch to User/Shop tab — tapping first result.");

        await Task.Delay(800);

        // Tap the first result that matches the nickname
        bool tappedResult = await _adb.TapElementAsync(kolNickname, partial: true);
        if (!tappedResult)
        {
            // Fallback: tap at configured first-result coordinates
            await _adb.TapAsync(coords.FirstSearchResult.X, coords.FirstSearchResult.Y);
        }

        await Task.Delay(_cfg.PageLoadDelayMs);
    }

    /// <summary>
    /// Finds and taps the "Video" tab inside a KOL's profile page.
    /// </summary>
    private async Task NavigateToVideoTabAsync()
    {
        Console.WriteLine("[NAV] Looking for Video tab…");

        // Try UIAutomator element first (most reliable)
        string[] videoTabLabels = { "Video", "Videos", "Video\n", "คลิป", "视频" };
        foreach (string label in videoTabLabels)
        {
            bool found = await _adb.TapElementAsync(label, partial: false);
            if (found)
            {
                Console.WriteLine($"[NAV] Tapped Video tab ('{label}').");
                await Task.Delay(800);
                return;
            }
        }

        // Fallback to configured coordinates
        var coords = _cfg.Coordinates;
        Console.WriteLine("[NAV] WARN: Video tab not found via UIAutomator — using configured coordinates.");
        await _adb.TapAsync(coords.VideoTab.X, coords.VideoTab.Y);
        await Task.Delay(800);
    }
}
