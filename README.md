# AffiliateVideoFlow

Crawl and download videos from Shopee KOL accounts using a real Android device, ADB, and mitmproxy traffic interception.

## How it works

1. **ADB** controls the phone (open app, search, scroll).
2. **mitmproxy** intercepts HTTPS traffic from the Shopee app and extracts video CDN URLs.
3. The C# tool downloads the captured MP4 / HLS streams to `Downloads/{kol}/`.

## Prerequisites

| Tool | Where to get |
|------|-------------|
| .NET 8 SDK | https://dotnet.microsoft.com/download |
| Python 3.10+ | https://python.org |
| ADB (platform-tools) | https://developer.android.com/tools/releases/platform-tools |
| ffmpeg (for HLS) | https://ffmpeg.org/download.html |

## Setup

### 1 — Python dependencies

```bash
pip install -r requirements.txt
```

### 2 — ADB

Place `adb.exe` next to the built binary **or** add the Android platform-tools folder to your system PATH.

### 3 — Android phone (one-time)

1. Enable **Developer Options** → **USB Debugging**.
2. Connect via USB; run `adb devices` to confirm the device appears.
3. Open the phone's **WiFi settings** → set the proxy to `<your-PC-IP>:8080`.
4. In the phone browser open `http://mitm.it` and install the mitmproxy CA certificate.
5. Trust the cert in **Settings → Security → CA Certificates** (or Trusted Credentials on some ROMs).

### 4 — Build & run

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
| `scrollCount` | `25` | Number of scroll gestures on the video tab |
| `scrollDelayMs` | `2000` | Pause between scrolls (ms) — give CDN requests time to fire |
| `mitmproxyPort` | `8080` | Port mitmproxy listens on |
| `collectorPort` | `5050` | Local HTTP port the C# tool listens on for captured URLs |
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
├── Program.cs            # Orchestrator + HTTP URL collector
├── AdbController.cs      # ADB wrapper (tap, swipe, UIAutomator)
├── ShopeeNavigator.cs    # Shopee-specific navigation
├── VideoDownloader.cs    # MP4 direct + HLS/ffmpeg downloader
├── traffic_sniffer.py    # mitmproxy addon — captures video URLs
├── config.json           # Runtime configuration
└── requirements.txt      # Python dependencies
```

## Troubleshooting

**No URLs captured**
- Confirm the phone proxy points to your PC IP (not `127.0.0.1`).
- Make sure the mitmproxy CA cert is trusted — open any HTTPS site in the phone browser and check there is no cert warning.
- Try increasing `scrollDelayMs` so videos fully load before the next scroll.

**Shopee won't open / crashes**
- Update `shopeePackage` in `config.json` for your region.
- Some Shopee versions detect proxies — try setting `--ssl-insecure` in mitmdump or use certificate pinning bypass (e.g. Frida + objection).

**Wrong elements tapped**
- Dump the UI with `adb shell uiautomator dump` and adjust `coordinates` in `config.json`.
