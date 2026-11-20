use serde::{Deserialize, Serialize};
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;
use tauri::{AppHandle, Emitter, State};

#[cfg(windows)]
use std::os::windows::process::CommandExt;
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub const APP_VERSION: &str = "1.0.0";
const REPO: &str = "jhon1466/Kryuger";
const SERVER_PORT: u16 = 5000;
const SERVER_PROJECT: &str = "src/Kryuger.Server/Kryuger.Server.csproj";
const GAME_FOLDER: &str = "GIRLS' FRONTLINE EN";
const GAME_EXE: &str = "GirlsFrontLine.exe";
// v2: earlier versions could record a commit while extracting a stale cached main.zip.
const COMMIT_MARKER: &str = ".kryuger-commit-v2";
const BUILT_MARKER: &str = ".kryuger-built";

/// UI language chosen in the app (0 es, 1 en, 2 zh, 3 ja); messages sent to the UI follow it.
static LANG: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(0);

fn tr(es: &str, en: &str, zh: &str, ja: &str) -> String {
    match LANG.load(std::sync::atomic::Ordering::Relaxed) {
        1 => en,
        2 => zh,
        3 => ja,
        _ => es,
    }
    .to_string()
}

#[tauri::command]
pub fn manager_set_language(lang: String) {
    let v = match lang.as_str() { "en" => 1, "zh" => 2, "ja" => 3, _ => 0 };
    LANG.store(v, std::sync::atomic::Ordering::Relaxed);
}

#[derive(Serialize, Deserialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub game_dir: Option<String>,
}

#[derive(Default)]
pub struct Manager {
    pub server: Mutex<Option<Child>>,
    pub busy: Mutex<bool>,
}

fn hidden(cmd: &mut Command) -> &mut Command {
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd
}

fn local_app_dir() -> PathBuf {
    std::env::var_os("LOCALAPPDATA").map(|p| PathBuf::from(p).join("Kryuger")).unwrap_or_else(|| PathBuf::from("Kryuger"))
}

fn managed_root() -> PathBuf {
    local_app_dir().join("project")
}

/// The server always lives in %LOCALAPPDATA%\Kryuger\project (downloaded and auto-updated from GitHub).
pub fn repo_root() -> PathBuf {
    managed_root()
}

fn is_managed(_root: &Path) -> bool {
    true
}

/// Player data lives in %LOCALAPPDATA%\Kryuger\data, outside the project folder so updates never touch it.
fn data_dir(_root: &Path) -> PathBuf {
    local_app_dir().join("data")
}

fn log_path() -> PathBuf {
    let dir = local_app_dir().join("logs");
    let _ = fs::create_dir_all(&dir);
    dir.join("server.log")
}

fn append_log(line: &str) {
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(log_path()) {
        let _ = writeln!(f, "{line}");
    }
}

fn progress(app: &AppHandle, stage: &str, pct: f64, message: impl Into<String>) {
    let msg = message.into();
    append_log(&format!("[{stage}] {msg}"));
    let _ = app.emit("manager-progress", serde_json::json!({ "stage": stage, "pct": pct, "message": msg }));
}

fn powershell(script: &str, envs: &[(&str, &Path)]) -> Result<String, String> {
    let mut cmd = Command::new("powershell.exe");
    hidden(&mut cmd).args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
    for (k, v) in envs {
        cmd.env(k, v);
    }
    let out = cmd.output().map_err(|e| e.to_string())?;
    if out.status.success() {
        Ok(String::from_utf8_lossy(&out.stdout).trim().to_string())
    } else {
        Err(String::from_utf8_lossy(&out.stderr).trim().to_string())
    }
}

const PS_PRELUDE: &str = r#"$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
function Download-File($url, $dest) {
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    & curl.exe -f -s -S -L -o $dest $url
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $dest -PathType Leaf)) { return }
  }
  Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dest -UserAgent 'Mozilla/5.0'
}
"#;

// ---------------------------------------------------------------- updates

/// Latest commit of `main` on GitHub (None without connection or while the repository is still empty).
fn latest_remote_commit() -> Option<String> {
    let script = format!(
        "{PS_PRELUDE}(Invoke-RestMethod -UseBasicParsing -TimeoutSec 15 -Headers @{{ 'User-Agent'='Kryuger-GM-Tools'; 'Accept'='application/vnd.github+json' }} -Uri 'https://api.github.com/repos/{REPO}/commits/main').sha"
    );
    let sha = powershell(&script, &[]).ok()?;
    (sha.len() == 40 && sha.chars().all(|c| c.is_ascii_hexdigit())).then_some(sha)
}

fn local_commit(root: &Path) -> String {
    fs::read_to_string(root.join(COMMIT_MARKER)).unwrap_or_default().trim().to_string()
}

fn download_project(root: &Path, sha: &str) -> Result<(), String> {
    let script = format!(
        r#"{PS_PRELUDE}$tempDir = Join-Path $env:TEMP ('kryuger-update-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$archive = Join-Path $tempDir 'project.zip'
Download-File 'https://github.com/{REPO}/archive/{sha}.zip' $archive
Expand-Archive -LiteralPath $archive -DestinationPath $tempDir
$source = Get-ChildItem -LiteralPath $tempDir -Directory | Select-Object -First 1
if (!(Test-Path -LiteralPath (Join-Path $source.FullName '{SERVER_PROJECT}'))) {{ throw 'The download does not contain the Kryuger server.' }}
New-Item -ItemType Directory -Force -Path $env:KRYUGER_PROJECT | Out-Null
Copy-Item -Path (Join-Path $source.FullName '*') -Destination $env:KRYUGER_PROJECT -Recurse -Force
$resolved = (Resolve-Path -LiteralPath $tempDir).Path
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {{ Remove-Item -LiteralPath $resolved -Recurse -Force }}
"#
    );
    powershell(&script, &[("KRYUGER_PROJECT", root)]).map(|_| ()).map_err(|e| format!("{}: {e}", tr("No se pudo descargar el servidor", "Could not download the server", "无法下载服务器", "サーバーをダウンロードできませんでした")))
}

/// Managed copy: downloads it the first time and whenever `main` has a new commit.
fn ensure_project(root: &Path, app: &AppHandle) -> Result<(), String> {
    let installed = root.join(SERVER_PROJECT).is_file();
    if installed && !is_managed(root) {
        return Ok(()); // development checkout: never overwritten
    }
    let remote = latest_remote_commit();
    if installed {
        match &remote {
            Some(sha) if *sha != local_commit(root) => progress(app, "update", 5.0, tr("Hay una versión nueva del servidor. Descargando…", "A new server version is available. Downloading…", "发现新版服务器，正在下载…", "新しいサーバーがあります。ダウンロード中…")),
            _ => return Ok(()),
        }
    } else {
        if remote.is_none() {
            return Err(format!("{} github.com/{REPO}", tr("No se pudo obtener el servidor (sin conexión o repositorio vacío):", "Could not get the server (no connection or empty repository):", "无法获取服务器（无网络或仓库为空）：", "サーバーを取得できません（未接続または空のリポジトリ）:")));
        }
        progress(app, "update", 5.0, tr("Descargando el servidor Kryuger…", "Downloading the Kryuger server…", "正在下载 Kryuger 服务器…", "Kryuger サーバーをダウンロード中…"));
    }
    let sha = remote.unwrap_or_default();
    match download_project(root, &sha) {
        Ok(()) => {
            let _ = fs::write(root.join(COMMIT_MARKER), &sha);
            Ok(())
        }
        Err(e) if installed => {
            append_log(&format!("[update] {e} — se usa la copia existente"));
            Ok(())
        }
        Err(e) => Err(e),
    }
}

// ---------------------------------------------------------------- .NET

fn portable_dotnet() -> PathBuf {
    local_app_dir().join("dotnet").join("dotnet.exe")
}

fn has_sdk8(dotnet: &Path) -> bool {
    hidden(Command::new(dotnet).arg("--list-sdks"))
        .output()
        .map(|o| {
            String::from_utf8_lossy(&o.stdout)
                .lines()
                .any(|l| l.split('.').next().and_then(|m| m.trim().parse::<u32>().ok()).is_some_and(|m| m >= 8))
        })
        .unwrap_or(false)
}

/// System .NET 8+ SDK if present, otherwise a private copy in %LOCALAPPDATA%\Kryuger\dotnet (official installer script).
fn ensure_dotnet(app: &AppHandle) -> Result<PathBuf, String> {
    if has_sdk8(Path::new("dotnet")) {
        return Ok(PathBuf::from("dotnet"));
    }
    let portable = portable_dotnet();
    if portable.is_file() && has_sdk8(&portable) {
        return Ok(portable);
    }
    progress(app, "dotnet", 15.0, tr("Instalando .NET 8 SDK privado (solo la primera vez)…", "Installing a private .NET 8 SDK (first time only)…", "正在安装私有 .NET 8 SDK（仅首次）…", "専用の .NET 8 SDK をインストール中（初回のみ）…"));
    let dir = portable.parent().unwrap().to_path_buf();
    let script = format!(
        r#"{PS_PRELUDE}$script = Join-Path $env:TEMP 'kryuger-dotnet-install.ps1'
Download-File 'https://dot.net/v1/dotnet-install.ps1' $script
& $script -Channel 8.0 -InstallDir $env:KRYUGER_DOTNET -NoPath
Remove-Item -LiteralPath $script -Force
"#
    );
    powershell(&script, &[("KRYUGER_DOTNET", &dir)]).map_err(|e| format!("{}: {e}", tr("No se pudo instalar .NET", "Could not install .NET", "无法安装 .NET", ".NET をインストールできませんでした")))?;
    if has_sdk8(&portable) { Ok(portable) } else { Err(tr(".NET se descargó pero no funciona.", ".NET was downloaded but does not work.", ".NET 已下载但无法运行。", ".NET をダウンロードしましたが動作しません。").into()) }
}

fn server_exe(root: &Path) -> PathBuf {
    root.join("src/Kryuger.Server/bin/Release/net8.0/Kryuger.Server.exe")
}

/// Builds the server in Release when the code changed (new commit) or the build is missing.
fn ensure_built(root: &Path, app: &AppHandle) -> Result<(), String> {
    let version = if is_managed(root) { local_commit(root) } else { "dev".into() };
    let built = fs::read_to_string(root.join(BUILT_MARKER)).unwrap_or_default();
    if server_exe(root).is_file() && built.trim() == version && is_managed(root) {
        return Ok(());
    }
    let dotnet = ensure_dotnet(app)?;
    progress(app, "build", 45.0, tr("Compilando el servidor Kryuger…", "Building the Kryuger server…", "正在编译 Kryuger 服务器…", "Kryuger サーバーをビルド中…"));
    let mut cmd = Command::new(&dotnet);
    hidden(&mut cmd)
        .current_dir(root)
        .args(["build", SERVER_PROJECT, "-c", "Release", "--nologo", "-v", "q"])
        .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        .env("DOTNET_NOLOGO", "1")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let out = cmd.output().map_err(|e| format!("{}: {e}", tr("No se pudo ejecutar dotnet", "Could not run dotnet", "无法运行 dotnet", "dotnet を実行できませんでした")))?;
    for line in String::from_utf8_lossy(&out.stdout).lines() {
        append_log(line);
    }
    if !out.status.success() || !server_exe(root).is_file() {
        return Err(tr("La compilación del servidor falló. Revisa el registro.", "The server build failed. Check the log.", "服务器编译失败，请查看日志。", "サーバーのビルドに失敗しました。ログを確認してください。").into());
    }
    let _ = fs::write(root.join(BUILT_MARKER), version);
    Ok(())
}

// ---------------------------------------------------------------- settings / game

fn settings_path() -> PathBuf {
    local_app_dir().join("settings.json")
}

pub fn load_settings() -> Settings {
    let mut s: Settings = fs::read_to_string(settings_path()).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    if s.game_dir.is_none() {
        s.game_dir = detect_game_dir().map(|p| p.display().to_string());
    }
    s
}

fn save_settings(s: &Settings) -> Result<(), String> {
    let _ = fs::create_dir_all(local_app_dir());
    fs::write(settings_path(), serde_json::to_vec_pretty(s).map_err(|e| e.to_string())?).map_err(|e| e.to_string())
}

fn detect_game_dir() -> Option<PathBuf> {
    let mut roots = vec![
        PathBuf::from(r"C:\Program Files (x86)\Steam"),
        PathBuf::from(r"C:\Program Files\Steam"),
        PathBuf::from(r"D:\SteamLibrary"),
        PathBuf::from(r"E:\SteamLibrary"),
    ];
    for r in roots.clone() {
        if let Ok(txt) = fs::read_to_string(r.join("steamapps").join("libraryfolders.vdf")) {
            for line in txt.lines() {
                let parts: Vec<&str> = line.trim().split('"').collect();
                if parts.len() >= 4 && parts[1] == "path" {
                    roots.push(PathBuf::from(parts[3].replace("\\\\", "\\")));
                }
            }
        }
    }
    roots.into_iter().map(|r| r.join("steamapps").join("common").join(GAME_FOLDER)).find(|c| c.join(GAME_EXE).is_file())
}

/// The redirect hook belongs in the game folder only while the private server runs.
fn remove_hook() {
    let Some(dir) = load_settings().game_dir else { return };
    let dll = Path::new(&dir).join("version.dll");
    if dll.is_file() && fs::remove_file(&dll).is_err() {
        let parked = dll.with_extension("dll.old");
        let _ = fs::remove_file(&parked);
        let _ = fs::rename(&dll, &parked);
    }
}

fn port_open(port: u16) -> bool {
    TcpStream::connect_timeout(&SocketAddr::from(([127, 0, 0, 1], port)), Duration::from_millis(250)).is_ok()
}

// ---------------------------------------------------------------- status

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub app_version: String,
    pub repo_root: String,
    pub managed: bool,
    pub installed: bool,
    pub built: bool,
    pub local_commit: String,
    pub server_running: bool,
    pub game_dir: Option<String>,
    pub hook_installed: bool,
    pub gm_ready: bool,
    pub data_dir: String,
}

pub fn compute_status(m: &Manager) -> Status {
    if let Ok(mut g) = m.server.lock() {
        if let Some(c) = g.as_mut() {
            if let Ok(Some(_)) = c.try_wait() {
                *g = None;
            }
        }
    }
    let root = repo_root();
    let settings = load_settings();
    let running = port_open(SERVER_PORT);
    Status {
        app_version: APP_VERSION.into(),
        repo_root: root.display().to_string(),
        managed: is_managed(&root),
        installed: root.join(SERVER_PROJECT).is_file(),
        built: server_exe(&root).is_file(),
        local_commit: if is_managed(&root) { local_commit(&root) } else { "desarrollo".into() },
        server_running: running,
        hook_installed: settings.game_dir.as_deref().is_some_and(|d| Path::new(d).join("version.dll").is_file()),
        game_dir: settings.game_dir,
        gm_ready: running && token(&root).is_some(),
        data_dir: data_dir(&root).display().to_string(),
    }
}

// ---------------------------------------------------------------- commands

fn kill_server_ports() {
    for port in [SERVER_PORT, 80, 443] {
        let script = format!(
            "Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object {{ $p = Get-Process -Id $_ -ErrorAction SilentlyContinue; if ($p -and $p.ProcessName -eq 'Kryuger.Server') {{ Stop-Process -Id $_ -Force }} }}"
        );
        let _ = powershell(&script, &[]);
    }
}

pub fn shutdown(m: &Manager) {
    if let Ok(mut guard) = m.server.lock() {
        if let Some(mut child) = guard.take() {
            #[cfg(windows)]
            let _ = hidden(Command::new("taskkill").args(["/F", "/T", "/PID", &child.id().to_string()])).status();
            let _ = child.kill();
            let _ = child.wait();
        }
    }
    kill_server_ports();
    remove_hook();
}

#[tauri::command]
pub fn manager_status(m: State<'_, Manager>) -> Status {
    compute_status(&m)
}

fn begin(m: &Manager) -> Result<(), String> {
    let mut b = m.busy.lock().unwrap();
    if *b {
        return Err(tr("Ya hay una operación en curso.", "Another operation is in progress.", "已有操作正在进行。", "別の処理を実行中です。").into());
    }
    *b = true;
    Ok(())
}

#[tauri::command]
pub async fn manager_start(app: AppHandle, m: State<'_, Manager>) -> Result<String, String> {
    if port_open(SERVER_PORT) {
        return Ok(tr("El servidor ya está en ejecución.", "The server is already running.", "服务器已在运行。", "サーバーはすでに実行中です。").into());
    }
    begin(&m)?;
    let result: Result<String, String> = async {
        let root = repo_root();
        let a = app.clone();
        let r2 = root.clone();
        tokio::task::spawn_blocking(move || -> Result<(), String> {
            ensure_project(&r2, &a)?;
            ensure_built(&r2, &a)
        })
        .await
        .map_err(|e| e.to_string())??;

        progress(&app, "start", 85.0, tr("Iniciando el servidor…", "Starting the server…", "正在启动服务器…", "サーバーを起動中…"));
        let data = data_dir(&root);
        let _ = fs::create_dir_all(&data);
        let log = OpenOptions::new().create(true).append(true).open(log_path()).map_err(|e| e.to_string())?;
        let log_err = log.try_clone().map_err(|e| e.to_string())?;
        let settings = load_settings();
        let mut cmd = Command::new(server_exe(&root));
        hidden(&mut cmd)
            .current_dir(root.join("src/Kryuger.Server"))
            .env("KRYUGER_DATA_DIR", &data)
            .stdout(Stdio::from(log))
            .stderr(Stdio::from(log_err))
            .stdin(Stdio::null());
        if let Some(g) = settings.game_dir.as_deref() {
            cmd.env("KRYUGER_GAME_DIR", g);
        }
        let child = cmd.spawn().map_err(|e| format!("{}: {e}", tr("No se pudo iniciar el servidor", "Could not start the server", "无法启动服务器", "サーバーを起動できませんでした")))?;
        *m.server.lock().unwrap() = Some(child);
        for _ in 0..60 {
            tokio::time::sleep(Duration::from_millis(500)).await;
            if port_open(SERVER_PORT) {
                progress(&app, "done", 100.0, tr("Servidor Kryuger activo.", "Kryuger server running.", "Kryuger 服务器运行中。", "Kryuger サーバー稼働中。"));
                return Ok(tr("Servidor iniciado.", "Server started.", "服务器已启动。", "サーバーを起動しました。").into());
            }
        }
        Err(tr("El servidor tardó demasiado en arrancar. Revisa el registro.", "The server took too long to start. Check the log.", "服务器启动超时，请查看日志。", "サーバーの起動に時間がかかりすぎました。ログを確認してください。").into())
    }
    .await;
    *m.busy.lock().unwrap() = false;
    if let Err(ref e) = result {
        progress(&app, "error", 0.0, e.clone());
    }
    result
}

#[tauri::command]
pub fn manager_stop(app: AppHandle, m: State<'_, Manager>) -> Result<String, String> {
    shutdown(&m);
    progress(&app, "done", 100.0, tr("Servidor detenido. El juego vuelve a conectar al servidor oficial.", "Server stopped. The game connects to the official server again.", "服务器已停止，游戏将重新连接官方服务器。", "サーバーを停止しました。ゲームは公式サーバーに接続します。"));
    Ok(tr("Servidor detenido.", "Server stopped.", "服务器已停止。", "サーバーを停止しました。").into())
}

/// Checks GitHub now and rebuilds if there is a new commit (managed copy only).
#[tauri::command]
pub async fn manager_update(app: AppHandle, m: State<'_, Manager>) -> Result<String, String> {
    begin(&m)?;
    let a = app.clone();
    let res = tokio::task::spawn_blocking(move || -> Result<String, String> {
        let root = repo_root();
        if !is_managed(&root) {
            return Ok(tr("Copia de desarrollo: se usa el código local, sin descargas.", "Development copy: local code is used, nothing is downloaded.", "开发副本：使用本地代码，不会下载。", "開発用コピー：ローカルのコードを使い、ダウンロードはしません。").into());
        }
        let before = local_commit(&root);
        ensure_project(&root, &a)?;
        ensure_built(&root, &a)?;
        let after = local_commit(&root);
        Ok(if before == after { tr("Ya tienes la última versión.", "You already have the latest version.", "已是最新版本。", "最新バージョンです。").into() } else { format!("{} {}.", tr("Actualizado a", "Updated to", "已更新到", "更新しました:"), &after[..7.min(after.len())]) })
    })
    .await
    .map_err(|e| e.to_string())?;
    *m.busy.lock().unwrap() = false;
    match &res {
        Ok(msg) => progress(&app, "done", 100.0, msg.clone()),
        Err(e) => progress(&app, "error", 0.0, e.clone()),
    }
    res
}

#[tauri::command]
pub fn manager_set_game_dir(game_dir: String) -> Result<(), String> {
    let dir = game_dir.trim().trim_matches('"').to_string();
    if !Path::new(&dir).join(GAME_EXE).is_file() {
        return Err(format!("{} {GAME_EXE}.", tr("En esa carpeta no está", "That folder does not contain", "该文件夹中没有", "このフォルダーにありません:")));
    }
    save_settings(&Settings { game_dir: Some(dir) })
}

#[tauri::command]
pub fn manager_play() -> Result<(), String> {
    let dir = load_settings().game_dir.ok_or(tr("Primero indica la carpeta del juego.", "Set the game folder first.", "请先指定游戏文件夹。", "先にゲームのフォルダーを指定してください。"))?;
    let exe = Path::new(&dir).join(GAME_EXE);
    Command::new(&exe).current_dir(&dir).spawn().map(|_| ()).map_err(|e| format!("{}: {e}", tr("No se pudo iniciar el juego", "Could not start the game", "无法启动游戏", "ゲームを起動できませんでした")))
}

#[tauri::command]
pub fn manager_log_tail(lines: Option<usize>) -> String {
    let text = fs::read_to_string(log_path()).unwrap_or_default();
    let all: Vec<&str> = text.lines().collect();
    all[all.len().saturating_sub(lines.unwrap_or(200))..].join("\n")
}

#[tauri::command]
pub fn manager_open_folder(kind: Option<String>) {
    let root = repo_root();
    let target = match kind.as_deref() {
        Some("game") => load_settings().game_dir.map(PathBuf::from).unwrap_or(root),
        Some("data") => data_dir(&root),
        Some("logs") => local_app_dir().join("logs"),
        _ => root,
    };
    let _ = Command::new("explorer").arg(target).spawn();
}

// ---------------------------------------------------------------- GM API proxy

fn token(root: &Path) -> Option<String> {
    let t = fs::read_to_string(root.join("KRYUGER_GM_TOKEN.txt")).ok()?.trim().to_string();
    (t.len() >= 24).then_some(t)
}

/// Calls the server GM API (loopback only) with the token from the repository folder.
#[tauri::command]
pub async fn gm_request(method: String, path: String, body: Option<String>) -> Result<serde_json::Value, String> {
    if !path.starts_with("/gm/") || path.contains("..") {
        return Err(tr("Ruta no permitida.", "Path not allowed.", "不允许的路径。", "許可されていないパスです。").into());
    }
    let method = if method.eq_ignore_ascii_case("POST") { "POST" } else { "GET" };
    tokio::task::spawn_blocking(move || {
        let tok = token(&repo_root()).ok_or(tr("No se encontró KRYUGER_GM_TOKEN.txt; arranca el servidor primero.", "KRYUGER_GM_TOKEN.txt not found; start the server first.", "未找到 KRYUGER_GM_TOKEN.txt，请先启动服务器。", "KRYUGER_GM_TOKEN.txt がありません。先にサーバーを起動してください。"))?;
        let body = body.unwrap_or_default();
        let mut s = TcpStream::connect_timeout(&SocketAddr::from(([127, 0, 0, 1], SERVER_PORT)), Duration::from_secs(3))
            .map_err(|_| tr("El servidor no está en ejecución.", "The server is not running.", "服务器未运行。", "サーバーは実行されていません。").to_string())?;
        let _ = s.set_read_timeout(Some(Duration::from_secs(30)));
        let req = format!(
            "{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nX-GM-Token: {tok}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        s.write_all(req.as_bytes()).map_err(|e| e.to_string())?;
        let mut raw = Vec::new();
        s.read_to_end(&mut raw).map_err(|e| e.to_string())?;
        let text = String::from_utf8_lossy(&raw);
        let (head, payload) = text.split_once("\r\n\r\n").ok_or(tr("Respuesta inválida del servidor.", "Invalid response from the server.", "服务器响应无效。", "サーバーの応答が不正です。"))?;
        let status: u16 = head.split_whitespace().nth(1).and_then(|c| c.parse().ok()).unwrap_or(0);
        let value: serde_json::Value = serde_json::from_str(payload.trim()).unwrap_or(serde_json::Value::String(payload.trim().to_string()));
        if status == 200 {
            Ok(value)
        } else {
            Err(value.get("error").and_then(|e| e.as_str()).map(String::from).unwrap_or(format!("Error {status}")))
        }
    })
    .await
    .map_err(|e| e.to_string())?
}

#[allow(dead_code)]
fn read_lines(p: &Path) -> Vec<String> {
    fs::File::open(p).map(|f| BufReader::new(f).lines().map_while(Result::ok).collect()).unwrap_or_default()
}

#[allow(dead_code)]
fn emit_unused(_: &AppHandle) {}
