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

        bool deepLinkWorked = await TryDeepLinkAsync(kolNickname);
        if (!deepLinkWorked)
            await SearchForKolAsync(kolNickname);

        await Task.Delay(_cfg.PageLoadDelayMs);
        await NavigateToVideoTabAsync();
        Console.WriteLine($"[NAV] Reached video tab for '{kolNickname}'.");
    }

    /// <summary>
    /// Collects Shopee share links for up to <paramref name="videoCount"/> videos
    /// by tapping each video, pressing Share → Copy Link, reading the clipboard,
    /// then swiping to the next video.
    /// </summary>
    /// <returns>List of collected Shopee share URLs.</returns>
    public async Task<List<string>> CollectVideoShareLinksAsync(int videoCount)
    {
        var links = new List<string>();
        var coords = _cfg.Coordinates;

        Console.WriteLine($"[NAV] Collecting share links for {videoCount} video(s)…");

        // Tap the first video thumbnail to enter the video player
        Console.WriteLine("[NAV] Tapping first video…");
        bool tappedFirst = await _adb.TapElementAsync("Video", partial: true);
        if (!tappedFirst)
            await _adb.TapAsync(coords.FirstVideoThumbnail.X, coords.FirstVideoThumbnail.Y);

        await Task.Delay(_cfg.VideoLoadDelayMs);

        for (int i = 0; i < videoCount; i++)
        {
            Console.WriteLine($"[NAV] Video {i + 1}/{videoCount} – getting share link…");

            string? link = await TryCopyShareLinkAsync();
            if (!string.IsNullOrEmpty(link))
            {
                links.Add(link);
                Console.WriteLine($"[NAV]   ✓ {link}");
            }
            else
            {
                Console.WriteLine("[NAV]   ✗ Could not get link – skipping.");
            }

            if (i < videoCount - 1)
            {
                // Swipe up to next video in the feed
                await _adb.SwipeAsync(
                    coords.VideoSwipeFrom.X, coords.VideoSwipeFrom.Y,
                    coords.VideoSwipeTo.X,   coords.VideoSwipeTo.Y,
                    durationMs: 500);
                await Task.Delay(_cfg.VideoLoadDelayMs);
            }
        }

        return links;
    }

    // ── Private steps ────────────────────────────────────────────────────────

    /// <summary>
    /// While a video is open, taps Share → Copy Link and returns the link
    /// read from the Android clipboard.
    /// </summary>
    private async Task<string?> TryCopyShareLinkAsync()
    {
        var coords = _cfg.Coordinates;

        // --- Tap the Share button ---
        bool tappedShare = await _adb.TapElementAsync("Share", partial: true)
                        || await _adb.TapElementAsync("Chia sẻ", partial: true);
        if (!tappedShare)
        {
            Console.WriteLine("[NAV]   Share button not found via UIAutomator – using coordinates.");
            await _adb.TapAsync(coords.ShareButton.X, coords.ShareButton.Y);
        }
        await Task.Delay(1200); // wait for share sheet

        // --- Tap Copy Link ---
        bool tappedCopy = await _adb.TapElementAsync("Copy Link", partial: true)
                       || await _adb.TapElementAsync("Sao chép liên kết", partial: true)
                       || await _adb.TapElementAsync("Copy link", partial: true);
        if (!tappedCopy)
        {
            Console.WriteLine("[NAV]   Copy Link not found via UIAutomator – using coordinates.");
            await _adb.TapAsync(coords.CopyLinkButton.X, coords.CopyLinkButton.Y);
        }
        await Task.Delay(800); // let the clipboard write complete

        // --- Read clipboard ---
        string link = await _adb.GetClipboardAsync();

        // Dismiss the share sheet if still visible (press Back)
        await _adb.PressKeyAsync(AdbController.KEYCODE_BACK);
        await Task.Delay(400);

        return string.IsNullOrWhiteSpace(link) ? null : link;
    }

    /// <summary>
    /// Attempts to jump directly to the KOL's profile via an Android deep link.
    /// </summary>
    private async Task<bool> TryDeepLinkAsync(string kolNickname)
    {
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

        bool tappedSearch = await _adb.TapElementAsync("Search", partial: true);
        if (!tappedSearch)
            await _adb.TapAsync(coords.SearchButton.X, coords.SearchButton.Y);
        await Task.Delay(600);

        await _adb.TypeTextAsync(kolNickname);
        await Task.Delay(400);
        await _adb.PressKeyAsync(AdbController.KEYCODE_ENTER);
        await Task.Delay(_cfg.PageLoadDelayMs);

        bool switchedToUsers = await _adb.TapElementAsync("Shop", partial: false)
                            || await _adb.TapElementAsync("User", partial: false)
                            || await _adb.TapElementAsync("Seller", partial: false);
        if (!switchedToUsers)
            Console.WriteLine("[NAV] WARN: Could not switch to User/Shop tab — tapping first result.");

        await Task.Delay(800);

        bool tappedResult = await _adb.TapElementAsync(kolNickname, partial: true);
        if (!tappedResult)
            await _adb.TapAsync(coords.FirstSearchResult.X, coords.FirstSearchResult.Y);

        await Task.Delay(_cfg.PageLoadDelayMs);
    }

    /// <summary>
    /// Finds and taps the "Video" tab inside a KOL's profile page.
    /// </summary>
    private async Task NavigateToVideoTabAsync()
    {
        Console.WriteLine("[NAV] Looking for Video tab…");

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

        var coords = _cfg.Coordinates;
        Console.WriteLine("[NAV] WARN: Video tab not found via UIAutomator — using configured coordinates.");
        await _adb.TapAsync(coords.VideoTab.X, coords.VideoTab.Y);
        await Task.Delay(800);
    }
}
