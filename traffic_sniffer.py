"""
traffic_sniffer.py — mitmproxy addon for AffiliateVideoFlow
============================================================
Intercepts HTTPS traffic from the Shopee mobile app and forwards
captured video URLs to the C# collector (HttpListener).

Usage:
    mitmdump -p 8080 -s traffic_sniffer.py --set collector_port=5050

Phone setup (one-time):
    1. Set WiFi proxy to  <your-PC-IP>:8080
    2. Visit http://mitm.it  → install the mitmproxy CA certificate
    3. Trust it in  Settings → Security → CA Certificates
"""

from __future__ import annotations

import json
import re
import threading
from urllib.request import urlopen, Request
from urllib.error import URLError

from mitmproxy import ctx, http

# ---------------------------------------------------------------------------
# Video URL patterns
# ---------------------------------------------------------------------------
# Shopee video CDN hosts and paths vary by region. These patterns are written
# broadly so they survive region changes or CDN migrations.

VIDEO_URL_PATTERNS: list[re.Pattern] = [
    # Direct MP4 files from any Shopee-owned or Shopee CDN domain
    re.compile(r"https?://[^/]*(?:shopee|cf\.susercontent|spstatic)[^/]*/.*\.mp4", re.I),
    # HLS playlists
    re.compile(r"https?://[^/]*(?:shopee|cf\.susercontent|spstatic)[^/]*/.*\.m3u8", re.I),
    # Generic video CDN path fragment
    re.compile(r"https?://[^/]+/(?:video|vod|media)/[A-Za-z0-9_\-/]+(?:\.mp4|\.m3u8|\.ts)?", re.I),
]

# Shopee API responses that embed video metadata (JSON bodies)
VIDEO_JSON_KEYS = ("video_url", "video_path", "play_url", "stream_url", "cdn_url")

# Domains we care about (to avoid scanning every response body)
SHOPEE_DOMAINS = re.compile(r"shopee\.|spstatic\.|susercontent\.", re.I)

# ---------------------------------------------------------------------------
# Collector endpoint (C# HttpListener)
# ---------------------------------------------------------------------------

_collector_port: int = 5050
_seen_urls: set[str] = set()
_lock = threading.Lock()


def _send_url(url: str) -> None:
    """Posts a captured video URL to the C# collector (fire-and-forget)."""
    with _lock:
        if url in _seen_urls:
            return
        _seen_urls.add(url)

    payload = json.dumps({"url": url}).encode()
    req = Request(
        f"http://localhost:{_collector_port}/",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(req, timeout=3):
            pass
        ctx.log.info(f"[sniffer] Captured → {url}")
    except URLError as exc:
        ctx.log.warn(f"[sniffer] Collector unreachable: {exc.reason}")


# ---------------------------------------------------------------------------
# mitmproxy addon class
# ---------------------------------------------------------------------------

class ShopeeVideoSniffer:
    """mitmproxy addon that captures Shopee video URLs."""

    def load(self, loader):
        loader.add_option(
            name="collector_port",
            typespec=int,
            default=5050,
            help="Port of the C# HTTP collector.",
        )

    def configure(self, updates):
        global _collector_port
        if "collector_port" in updates:
            _collector_port = ctx.options.collector_port
            ctx.log.info(f"[sniffer] Collector port set to {_collector_port}")

    # -- Request interception (catch video URLs in the request itself) --------

    def request(self, flow: http.HTTPFlow) -> None:
        url = flow.request.pretty_url
        self._check_url(url)

    # -- Response interception (catch URLs embedded in JSON responses) --------

    def response(self, flow: http.HTTPFlow) -> None:
        url = flow.request.pretty_url

        # Also scan the response URL (sometimes a redirect leads to the video)
        self._check_url(url)

        # Only scan JSON responses from Shopee domains to limit CPU usage
        content_type = flow.response.headers.get("content-type", "")
        if "json" not in content_type:
            return
        if not SHOPEE_DOMAINS.search(flow.request.pretty_host):
            return

        try:
            body = flow.response.get_text(strict=False)
            if not body:
                return
            self._extract_from_json(body)
        except Exception:
            pass

    # -- Helpers ----------------------------------------------------------------

    def _check_url(self, url: str) -> None:
        for pattern in VIDEO_URL_PATTERNS:
            if pattern.search(url):
                threading.Thread(target=_send_url, args=(url,), daemon=True).start()
                return

    def _extract_from_json(self, body: str) -> None:
        """
        Recursively walks a parsed JSON body looking for keys that
        typically hold video URLs (see VIDEO_JSON_KEYS).
        """
        try:
            data = json.loads(body)
        except json.JSONDecodeError:
            return
        self._walk_json(data)

    def _walk_json(self, node) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                if key.lower() in VIDEO_JSON_KEYS and isinstance(value, str) and value.startswith("http"):
                    threading.Thread(target=_send_url, args=(value,), daemon=True).start()
                else:
                    self._walk_json(value)
        elif isinstance(node, list):
            for item in node:
                self._walk_json(item)


# ---------------------------------------------------------------------------
# Register addon
# ---------------------------------------------------------------------------

addons = [ShopeeVideoSniffer()]
