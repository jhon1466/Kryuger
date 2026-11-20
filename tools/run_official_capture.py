#!/usr/bin/env python3
"""
Launches Girls' Frontline EN (Steam) through mitmdump directly against
the OFFICIAL Sunborn servers (without Kryuger.Server) to capture & decrypt responses.
"""

import os
import socket
import subprocess
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME_EXE = Path(r"C:\Program Files (x86)\Steam\steamapps\common\GIRLS' FRONTLINE EN\GirlsFrontLine.exe")
CAPTURE_ADDON = ROOT / "tools" / "capture_official.py"
PROXY_PORT = 8888


def is_port_open(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(1.0)
        return s.connect_ex(("127.0.0.1", port)) == 0


def main():
    if is_port_open(PROXY_PORT):
        print(f"[Capture] Port {PROXY_PORT} is already in use. Close any running proxy/run_steam.py first.")
        return

    print(f"[Capture] Starting mitmdump official capture on 127.0.0.1:{PROXY_PORT}...")
    proxy_proc = subprocess.Popen(
        ["mitmdump", "-p", str(PROXY_PORT), "-s", str(CAPTURE_ADDON), "--ssl-insecure", "--ignore-hosts", ".*:443"],
    )
    time.sleep(1.5)

    if not GAME_EXE.exists():
        print(f"[Capture] Game executable not found: {GAME_EXE}")
        proxy_proc.terminate()
        return

    env = os.environ.copy()
    env["HTTP_PROXY"] = f"http://127.0.0.1:{PROXY_PORT}"
    env["HTTPS_PROXY"] = f"http://127.0.0.1:{PROXY_PORT}"
    subprocess.Popen([str(GAME_EXE)], cwd=str(GAME_EXE.parent), env=env)
    print(f"[Capture] Game launched against OFFICIAL servers. Logs -> {ROOT / 'captured_official.log'}")
    print("[Capture] Perform the actions you want to capture (e.g. give a gift), then press Ctrl+C here.")

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[Capture] Stopping...")
        proxy_proc.terminate()


if __name__ == "__main__":
    main()
