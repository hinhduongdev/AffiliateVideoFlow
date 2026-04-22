using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AffiliateVideoFlow;

/// <summary>
/// Low-level wrapper around ADB (Android Debug Bridge) commands.
/// Requires adb.exe to be in the configured path or system PATH.
/// </summary>
public class AdbController
{
    private readonly string _adbPath;
    private readonly string _deviceSerial;

    // Android keycodes
    public const int KEYCODE_BACK  = 4;
    public const int KEYCODE_HOME  = 3;
    public const int KEYCODE_ENTER = 66;
    public const int KEYCODE_DEL   = 67;
    public const int KEYCODE_PASTE = 279;

    public AdbController(string adbPath, string deviceSerial = "")
    {
        _adbPath = adbPath;
        _deviceSerial = deviceSerial;
    }

    // ── Core execution ──────────────────────────────────────────────────────

    public async Task<string> RunAsync(string arguments)
    {
        string fullArgs = string.IsNullOrEmpty(_deviceSerial)
            ? arguments
            : $"-s {_deviceSerial} {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = fullArgs,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ADB process.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error  = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            Console.WriteLine($"[ADB WARN] {error.Trim()}");

        return output.Trim();
    }

    // ── Device management ───────────────────────────────────────────────────

    public async Task<List<string>> GetConnectedDevicesAsync()
    {
        string output = await RunAsync("devices");
        return output
            .Split('\n')
            .Skip(1)
            .Where(l => l.Contains("\tdevice"))
            .Select(l => l.Split('\t')[0].Trim())
            .ToList();
    }

    public async Task<bool> IsDeviceReadyAsync()
    {
        var devices = await GetConnectedDevicesAsync();
        return devices.Count > 0;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    public Task TapAsync(int x, int y) =>
        RunAsync($"shell input tap {x} {y}");

    public Task SwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 400) =>
        RunAsync($"shell input swipe {x1} {y1} {x2} {y2} {durationMs}");

    public Task TypeTextAsync(string text)
    {
        // Escape characters that ADB input text treats specially
        string escaped = text
            .Replace(" ", "%s")
            .Replace("&", "\\&")
            .Replace("<", "\\<")
            .Replace(">", "\\>")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"");
        return RunAsync($"shell input text \"{escaped}\"");
    }

    public Task PressKeyAsync(int keycode) =>
        RunAsync($"shell input keyevent {keycode}");

    public Task ClearInputAsync() =>
        RunAsync("shell input keyevent --longpress 67"); // long-press DEL

    // ── App control ─────────────────────────────────────────────────────────

    public Task LaunchAppAsync(string packageName) =>
        RunAsync($"shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1");

    public Task ForceStopAppAsync(string packageName) =>
        RunAsync($"shell am force-stop {packageName}");

    public Task OpenUrlAsync(string url) =>
        RunAsync($"shell am start -a android.intent.action.VIEW -d \"{url}\"");

    // ── Screen info ─────────────────────────────────────────────────────────

    public async Task<(int Width, int Height)> GetScreenSizeAsync()
    {
        string output = await RunAsync("shell wm size");
        // "Physical size: 1080x2340"
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)x(\d+)");
        if (match.Success)
            return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        return (1080, 2340);
    }

    public async Task<byte[]> TakeScreenshotAsync()
    {
        await RunAsync("shell screencap -p /sdcard/screen_tmp.png");
        string b64 = await RunAsync("shell base64 /sdcard/screen_tmp.png");
        await RunAsync("shell rm /sdcard/screen_tmp.png");
        return Convert.FromBase64String(b64.Replace("\n", "").Replace("\r", ""));
    }

    // ── UIAutomator XML dump ─────────────────────────────────────────────────
    // Parses the view hierarchy to find elements without relying on fixed coords.

    public async Task<XDocument?> DumpUiAsync()
    {
        await RunAsync("shell uiautomator dump /sdcard/ui_dump.xml");
        string xml = await RunAsync("shell cat /sdcard/ui_dump.xml");
        await RunAsync("shell rm /sdcard/ui_dump.xml");

        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try { return XDocument.Parse(xml); }
        catch { return null; }
    }

    /// <summary>
    /// Finds the centre coordinates of the first editable text field (EditText)
    /// on screen. Used to explicitly focus the input before typing.
    /// Returns null if no input field is found.
    /// </summary>
    public async Task<(int X, int Y)?> FindInputFieldAsync()
    {
        var doc = await DumpUiAsync();
        if (doc == null) return null;

        foreach (var node in doc.Descendants("node"))
        {
            string cls       = node.Attribute("class")?.Value       ?? "";
            string focusable = node.Attribute("focusable")?.Value   ?? "";
            string enabled   = node.Attribute("enabled")?.Value     ?? "";
            string bounds    = node.Attribute("bounds")?.Value      ?? "";

            bool isInput = cls.Contains("EditText", StringComparison.OrdinalIgnoreCase)
                        || (focusable == "true" && enabled == "true"
                            && cls.Contains("Text", StringComparison.OrdinalIgnoreCase));

            if (isInput && !string.IsNullOrEmpty(bounds))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    bounds, @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
                if (m.Success)
                {
                    int cx = (int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[3].Value)) / 2;
                    int cy = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[4].Value)) / 2;
                    return (cx, cy);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the centre coordinates of the first UI element whose
    /// 'text' or 'content-desc' attribute matches <paramref name="textOrDesc"/>.
    /// Returns null if not found.
    /// </summary>
    public async Task<(int X, int Y)?> FindElementCenterAsync(string textOrDesc, bool partial = false)
    {
        var doc = await DumpUiAsync();
        if (doc == null) return null;

        foreach (var node in doc.Descendants("node"))
        {
            string nodeText = node.Attribute("text")?.Value ?? "";
            string nodeDesc = node.Attribute("content-desc")?.Value ?? "";

            bool matches = partial
                ? nodeText.Contains(textOrDesc, StringComparison.OrdinalIgnoreCase) ||
                  nodeDesc.Contains(textOrDesc, StringComparison.OrdinalIgnoreCase)
                : nodeText.Equals(textOrDesc, StringComparison.OrdinalIgnoreCase) ||
                  nodeDesc.Equals(textOrDesc, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                string bounds = node.Attribute("bounds")?.Value ?? "";
                // Format: [x1,y1][x2,y2]
                var m = System.Text.RegularExpressions.Regex.Match(
                    bounds, @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
                if (m.Success)
                {
                    int cx = (int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[3].Value)) / 2;
                    int cy = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[4].Value)) / 2;
                    return (cx, cy);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Taps the first UI element matching <paramref name="textOrDesc"/>.
    /// Returns true if element was found and tapped.
    /// </summary>
    public async Task<bool> TapElementAsync(string textOrDesc, bool partial = false)
    {
        var center = await FindElementCenterAsync(textOrDesc, partial);
        if (center == null) return false;
        await TapAsync(center.Value.X, center.Value.Y);
        return true;
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current Android clipboard text via the ADB shell clipboard
    /// service call. Works on Android 7–14 without root or extra apps.
    /// Returns an empty string if the clipboard is empty or cannot be read.
    /// </summary>
    public async Task<string> GetClipboardAsync()
    {
        // service call clipboard 2 = IClipboard.getPrimaryClip(packageName, uid)
        string raw = await RunAsync("shell service call clipboard 2 i32 1 i32 0 i32 0");
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string result = ExtractUrlFromParcel(raw);
        if (!string.IsNullOrEmpty(result))
            return result;

        // Fallback: try to read plaintext via content provider (some ROMs support it)
        string raw2 = await RunAsync("shell content query --uri content://clipboard");
        return ExtractUrlFromText(raw2);
    }

    /// <summary>
    /// Parses the hex parcel dump from <c>service call clipboard</c> and
    /// extracts any http/https URL encoded as UTF-16 (BE or LE).
    /// </summary>
    private static string ExtractUrlFromParcel(string parcelOutput)
    {
        // Each word in the dump is 8 hex digits = 4 bytes, printed high-byte first.
        var bytes = new List<byte>();
        foreach (Match m in Regex.Matches(parcelOutput, @"\b[0-9A-Fa-f]{8}\b"))
        {
            string h = m.Value;
            bytes.Add(Convert.ToByte(h[0..2], 16));
            bytes.Add(Convert.ToByte(h[2..4], 16));
            bytes.Add(Convert.ToByte(h[4..6], 16));
            bytes.Add(Convert.ToByte(h[6..8], 16));
        }

        if (bytes.Count < 12) return "";
        byte[] arr = bytes.ToArray();

        // Try UTF-16 big-endian first (most common in Android parcel hexdumps)
        foreach (var enc in new[] { Encoding.BigEndianUnicode, Encoding.Unicode })
        {
            try
            {
                string text = enc.GetString(arr);
                var m = Regex.Match(text, @"https?://[^\s\x00]{10,}");
                if (m.Success)
                    return m.Value.TrimEnd('\0', ' ', '\r', '\n');
            }
            catch { /* try next encoding */ }
        }

        return "";
    }

    private static string ExtractUrlFromText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var m = Regex.Match(raw, @"https?://\S{10,}");
        return m.Success ? m.Value.TrimEnd('\0', ' ') : "";
    }
}
