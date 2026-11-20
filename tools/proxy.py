"""
Mitmproxy addon for Girls' Frontline EN (Steam)
Redirects game client traffic from Sunborn servers to the local Kryuger.Server (.NET).
"""

import os
from urllib.parse import urlparse
from mitmproxy import http
from mitmproxy import ctx

TARGET_SERVER = os.environ.get("GFL_PROXY_TARGET", "http://127.0.0.1:5000")

SUNBORN_HOSTS = {
    "gf-transit.sunborngame.com",
    "gf-game.sunborngame.com",
    "gfus-cdn.sunborngame.com",
    "gf-en.sunborngame.com"
}

def load(loader):
    ctx.options.connection_strategy = "lazy"
    ctx.options.upstream_cert = False
    ctx.options.ssl_insecure = True

def request(flow: http.HTTPFlow) -> None:
    host = flow.request.pretty_host.lower()
    
    # Check if host matches Sunborn / GFL endpoints
    is_target = any(target in host for target in ["sunborngame.com", "girlfrontline"])
    
    if is_target:
        parsed_target = urlparse(TARGET_SERVER)
        flow.request.scheme = parsed_target.scheme or "http"
        flow.request.host = parsed_target.hostname or "127.0.0.1"
        flow.request.port = parsed_target.port or 5000
        flow.request.headers["Host"] = host  # Preserve original Host header
        print(f"[Bridge] Redirected: {host}{flow.request.path} -> {flow.request.scheme}://{flow.request.host}:{flow.request.port}")

