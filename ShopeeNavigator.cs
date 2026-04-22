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
        await Task.Delay(1000);
        await _adb.LaunchAppAsync(_cfg.ShopeePackage);

        // Wait for the home screen to fully render before interacting.
        Console.WriteLine($"[NAV] Waiting {_cfg.AppLaunchDelayMs} ms for Shopee to load…");
        await Task.Delay(_cfg.AppLaunchDelayMs);

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
    /// Navigates via Shopee's search UI to find the KOL and open their profile.
    /// </summary>
    private async Task SearchForKolAsync(string kolNickname)
    {
        Console.WriteLine("[NAV] Opening search bar…");
        var coords = _cfg.Coordinates;

        // Shopee VN shows a search bar/button with various labels depending on the version.
        // Try UIAutomator first (most reliable), fall back to coordinates.
        string[] searchLabels =
        {
            "Tìm kiếm", "Tìm kiếm sản phẩm", "Tìm trong Shopee",
            "Search", "Search products", "Cari"
        };

        bool tappedSearch = false;
        // Retry up to 3 times — Shopee may still be loading on first attempt.
        for (int attempt = 0; attempt < 3 && !tappedSearch; attempt++)
        {
            foreach (string label in searchLabels)
            {
                tappedSearch = await _adb.TapElementAsync(label, partial: true);
                if (tappedSearch)
                {
                    Console.WriteLine($"[NAV] Search bar found via UIAutomator ('{label}').");
                    break;
                }
            }

            if (!tappedSearch)
            {
                Console.WriteLine($"[NAV] Search bar not found via UIAutomator (attempt {attempt + 1}/3) — waiting…");
                await Task.Delay(1500);
            }
        }

        if (!tappedSearch)
        {
            Console.WriteLine("[NAV] Falling back to configured search-bar coordinates.");
            await _adb.TapAsync(coords.SearchButton.X, coords.SearchButton.Y);
        }

        // Give Shopee time to open the search overlay and display the keyboard.
        await Task.Delay(1500);

        // Explicitly locate the EditText field and tap it to guarantee keyboard focus.
        var inputCenter = await _adb.FindInputFieldAsync();
        if (inputCenter != null)
        {
            Console.WriteLine($"[NAV] Focusing input field at ({inputCenter.Value.X}, {inputCenter.Value.Y}).");
            await _adb.TapAsync(inputCenter.Value.X, inputCenter.Value.Y);
        }
        else
        {
            Console.WriteLine("[NAV] WARN: EditText not found in UI dump — typing anyway.");
        }
        await Task.Delay(600);

        // Type the KOL's nickname into the focused field.
        Console.WriteLine($"[NAV] Typing '{kolNickname}'…");
        await _adb.TypeTextAsync(kolNickname);
        await Task.Delay(500);

        // Submit the search.
        await _adb.PressKeyAsync(AdbController.KEYCODE_ENTER);
        await Task.Delay(_cfg.PageLoadDelayMs);

        // Switch to the Shop / User tab so we get profile results, not products.
        string[] userTabLabels =
        {
            "Shop", "Gian hàng", "User", "Người dùng", "Seller", "Người bán"
        };
        bool switchedTab = false;
        foreach (string label in userTabLabels)
        {
            switchedTab = await _adb.TapElementAsync(label, partial: false);
            if (switchedTab)
            {
                Console.WriteLine($"[NAV] Switched to '{label}' tab in search results.");
                break;
            }
        }
        if (!switchedTab)
            Console.WriteLine("[NAV] WARN: Could not switch to User/Shop tab — tapping first result.");

        await Task.Delay(800);

        // Tap the result that matches the nickname; fall back to first-result coordinates.
        bool tappedResult = await _adb.TapElementAsync(kolNickname, partial: true);
        if (!tappedResult)
        {
            Console.WriteLine("[NAV] Nickname not found in results — tapping first result coordinates.");
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
