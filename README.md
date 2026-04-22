# AffiliateVideoFlow

Crawl and download videos from Shopee KOL accounts using a real Android device, ADB, and a headless browser.

## How it works

1. **ADB** controls the phone (open app, navigate to KOL profile, collect share links via Share → Copy Link).
2. **Playwright (headless Chromium)** submits each share link to downloadvideo.vn and extracts the direct MP4 URL.
3. The C# tool downloads the captured MP4 files to `Downloads/{kol}/`.

## Prerequisites

| Tool | Where to get |
|------|-------------|
| .NET 8 SDK | https://dotnet.microsoft.com/download |
| ADB (platform-tools) | https://developer.android.com/tools/releases/platform-tools |
| Playwright browsers | run `playwright install chromium` after `dotnet build` |

## Setup

### 1 — ADB

Place `adb.exe` next to the built binary **or** add the Android platform-tools folder to your system PATH.

### 2 — Android phone (one-time)

1. Enable **Developer Options** → **USB Debugging**.
2. Connect via USB; run `adb devices` to confirm the device appears.

### 3 — Build & run

```bash
dotnet build -c Release
dotnet run --project AffiliateVideoFlow.csproj
```

Follow the on-screen prompts to enter the KOL's Shopee nickname.

## Configuration — `config.json`

| Key | Default | Description |
|-----|---------|-------------|
| `shopeePackage` | `com.shopee.vn` | App package for your region (`com.shopee.id`, `com.shopee.th`, …) |
| `shopeeDomain` | `shopee.vn` | Domain used for deep-link fallback |
| `videoCount` | `10` | Number of videos to collect per KOL |
| `appLaunchDelayMs` | `3000` | Wait after launching Shopee (ms) |
| `pageLoadDelayMs` | `2500` | Wait after page navigation (ms) |
| `videoLoadDelayMs` | `2000` | Wait between video actions (ms) |
| `downloadsFolder` | `Downloads` | Output directory |
| `coordinates.*` | various | Tap/swipe pixel coords — **tune these to your phone resolution** |

### Finding correct coordinates

```bash
adb shell uiautomator dump /sdcard/ui.xml
adb pull /sdcard/ui.xml
# Open ui.xml and find bounds="[x1,y1][x2,y2]" for the element you need
```

## Project structure

```
AffiliateVideoFlow/
├── Program.cs                  # Orchestrator
├── AdbController.cs            # ADB wrapper (tap, swipe, UIAutomator, clipboard)
├── ShopeeNavigator.cs          # Shopee-specific navigation & share-link collection
├── ShopeeVideoLinkExtractor.cs # Playwright headless browser — resolves MP4 URLs
├── VideoDownloader.cs          # HTTP MP4 downloader
└── config.json                 # Runtime configuration
```

## Troubleshooting

**No URLs extracted**
- The downloadvideo.vn site layout may have changed — check the page manually and update selectors in `ShopeeVideoLinkExtractor.cs`.
- Make sure the collected share links are valid by opening one in a browser.

**Shopee won't open / crashes**
- Update `shopeePackage` in `config.json` for your region.

**Wrong elements tapped**
- Dump the UI with `adb shell uiautomator dump` and adjust `coordinates` in `config.json`.
