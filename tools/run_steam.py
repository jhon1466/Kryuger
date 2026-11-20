#!/usr/bin/env python3
"""
Girls' Frontline EN (Steam) - Private Server Runner & Session Capture
Launches the .NET Kryuger.Server and the Steam Client with traffic bridge.
"""

import os
import sys
import subprocess
import time
import socket
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME_EXE = Path(r"C:\Program Files (x86)\Steam\steamapps\common\GIRLS' FRONTLINE EN\GirlsFrontLine.exe")
SERVER_PROJECT = ROOT / "src" / "Kryuger.Server" / "Kryuger.Server.csproj"
PROXY_SCRIPT = ROOT / "tools" / "proxy.py"
SERVER_PORT = 5000
PROXY_PORT = 8888

def is_port_open(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(1.0)
        return s.connect_ex((host, port)) == 0

def start_server():
    if is_port_open("127.0.0.1", SERVER_PORT):
        print(f"[Runner] .NET Kryuger.Server already running on port {SERVER_PORT}.")
        return None

    print(f"[Runner] Starting .NET Kryuger.Server on http://127.0.0.1:{SERVER_PORT}...")
    proc = subprocess.Popen(
        ["dotnet", "run", "--project", str(SERVER_PROJECT)],
        cwd=str(ROOT / "src" / "Kryuger.Server"),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1
    )
    
    # Wait for server to bind
    for _ in range(20):
        if is_port_open("127.0.0.1", SERVER_PORT):
            print("[Runner] .NET Kryuger.Server is healthy and listening.")
            return proc
        time.sleep(0.5)

    print("[Runner] Warning: .NET Kryuger.Server took longer than expected to bind.")
    return proc

def start_proxy():
    if is_port_open("127.0.0.1", PROXY_PORT):
        print(f"[Runner] Proxy already running on port {PROXY_PORT}.")
        return None

    print(f"[Runner] Starting mitmdump bridge on 127.0.0.1:{PROXY_PORT}...")
    # Find mitmdump
    mitm_cmd = "mitmdump"
    proc = subprocess.Popen(
        [mitm_cmd, "-p", str(PROXY_PORT), "-s", str(PROXY_SCRIPT), "--ssl-insecure"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL
    )
    time.sleep(1.5)
    return proc

def launch_game():
    if not GAME_EXE.exists():
        print(f"[Runner] Error: Game executable not found at: {GAME_EXE}")
        return

    print(f"[Runner] Launching Girls' Frontline Steam Client...")
    env = os.environ.copy()
    env["HTTP_PROXY"] = f"http://127.0.0.1:{PROXY_PORT}"
    env["HTTPS_PROXY"] = f"http://127.0.0.1:{PROXY_PORT}"

    # Set working directory to game folder
    subprocess.Popen([str(GAME_EXE)], cwd=str(GAME_EXE.parent), env=env)
    print("[Runner] Client launched! Traffic is now bridging to your private server.")

def main():
    print("=========================================================")
    print(" Girls' Frontline EN (Steam) - Private Server Bridge")
    print("=========================================================")
    
    server_proc = start_server()
    proxy_proc = start_proxy()

    try:
        launch_game()
        print("\n[Runner] Server is running in the background. Press Ctrl+C to stop.")
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[Runner] Shutting down...")
        if server_proc:
            server_proc.terminate()
        if proxy_proc:
            proxy_proc.terminate()

if __name__ == "__main__":
    main()

