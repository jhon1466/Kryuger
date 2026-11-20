"""
Mitmproxy addon for capturing & decrypting official Girls' Frontline EN traffic.
Does NOT redirect to local server — passes traffic to official Sunborn servers
and logs decrypted requests & responses to captured_official.log.
"""

import base64
import gzip
import hashlib
import json
from pathlib import Path
from urllib.parse import parse_qs, unquote
from mitmproxy import http, ctx

LOG_PATH = Path(__file__).resolve().parent.parent / "captured_official.log"
DEFAULT_KEY = "yundoudou"
SIGN_KEY = DEFAULT_KEY


def md5_hex(data: bytes) -> str:
    return hashlib.md5(data).hexdigest()


def rc4(data: bytes, key: str) -> bytes:
    pwd = key.encode("utf-8")
    box = list(range(256))
    j = 0
    for i in range(256):
        j = (j + box[i] + pwd[i % len(pwd)]) % 256
        box[i], box[j] = box[j], box[i]
    out = bytearray(len(data))
    a = j = 0
    for i, b in enumerate(data):
        a = (a + 1) % 256
        j = (j + box[a]) % 256
        box[a], box[j] = box[j], box[a]
        out[i] = b ^ box[(box[a] + box[j]) % 256]
    return bytes(out)


def decode_authcode(data: str, key: str) -> str:
    if not data:
        return ""
    s = data.strip()
    compressed = s.startswith("#")
    if compressed:
        s = s[1:]
    else:
        s = unquote(s)
    pad = len(s) % 4
    if pad:
        s += "=" * (4 - pad)
    enc = base64.b64decode(s)
    h = md5_hex((key or DEFAULT_KEY).encode("utf-8"))
    rkx = md5_hex(h[16:].encode("utf-8"))
    rk = rkx + md5_hex(rkx.encode("utf-8"))
    dec = rc4(enc, rk)
    if len(dec) < 26:
        return f"<short:{len(dec)}>"
    body = dec[26:]
    if compressed:
        body = gzip.decompress(body)
    return body.decode("utf-8", errors="replace")


def try_decode(raw: str) -> str:
    for k in (SIGN_KEY, DEFAULT_KEY):
        try:
            out = decode_authcode(raw, k)
            if out and (out.startswith("{") or out.startswith("[") or out == "1"):
                return out
        except Exception:
            pass
    return raw[:500]


def log_line(text: str) -> None:
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(text + "\n")


def load(loader):
    ctx.options.connection_strategy = "lazy"
    ctx.options.upstream_cert = False
    ctx.options.ssl_insecure = True
    log_line("=== Official Capture Session Started ===")


def response(flow: http.HTTPFlow) -> None:
    global SIGN_KEY
    host = flow.request.pretty_host.lower()
    if "sunborngame.com" not in host:
        return
    path = flow.request.path
    if "gf-game" not in host and "gf-transit" not in host:
        return

    req_text = flow.request.get_text(strict=False) or ""
    req_decoded = ""
    if "outdatacode=" in req_text:
        qs = parse_qs(req_text, keep_blank_values=True)
        raw_code = (qs.get("outdatacode") or [""])[0]
        req_decoded = try_decode(raw_code)
    elif req_text:
        req_decoded = req_text[:1000]

    resp_text = flow.response.get_text(strict=False) or ""
    resp_decoded = resp_text
    if resp_text.startswith("#") or (len(resp_text) > 16 and not resp_text.lstrip().startswith("{") and resp_text.strip() != "1"):
        resp_decoded = try_decode(resp_text)

    if "getUid" in path or "login" in path.lower():
        try:
            j = json.loads(resp_decoded)
            if isinstance(j, dict) and j.get("sign"):
                SIGN_KEY = str(j["sign"])
                log_line(f"[SIGN_KEY] Updated -> {SIGN_KEY}")
        except Exception:
            pass

    if "Index/index" in path and len(resp_decoded) > 2000:
        resp_logged = resp_decoded[:1000] + f"... <truncated {len(resp_decoded)} chars>"
    else:
        resp_logged = resp_decoded

    log_line(f"\n>>> {flow.request.method} {host}{path}")
    if req_decoded:
        log_line(f"REQ: {req_decoded}")
    log_line(f"RESP ({flow.response.status_code}): {resp_logged}")
    print(f"[Official] {path} -> {flow.response.status_code} ({len(resp_decoded)} chars)")
