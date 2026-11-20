from mitmproxy import http

def request(flow: http.HTTPFlow) -> None:
    print(f"\n>>> [CAPTURED REQUEST] {flow.request.method} {flow.request.pretty_url}")
    print(f"    Headers: Host={flow.request.host}, User-Agent={flow.request.headers.get('User-Agent', '')}")

def response(flow: http.HTTPFlow) -> None:
    print(f"<<< [CAPTURED RESPONSE] {flow.response.status_code} for {flow.request.pretty_url}")

