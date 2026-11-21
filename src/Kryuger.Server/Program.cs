using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kryuger.Common.Crypto;
using Kryuger.Common.Database;
using Kryuger.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

// Ensure Windows system proxy is disabled
try
{
    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
    key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
}
catch { }

static X509Certificate2 CreateLocalSunbornCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=*.sunborngame.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("*.sunborngame.com");
    san.AddDnsName("gf-passport.sunborngame.com");
    san.AddDnsName("gf-transit.sunborngame.com");
    san.AddDnsName("gfus-cdn.sunborngame.com");
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    req.CertificateExtensions.Add(san.Build());

    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    return new X509Certificate2(cert.Export(X509ContentType.Pfx, "gfl"), "gfl", X509KeyStorageFlags.Exportable);
}

var builder = WebApplication.CreateBuilder(args);

int serverPort = 5000;
if (int.TryParse(Environment.GetEnvironmentVariable("GFL_SERVER_PORT"), out int p))
    serverPort = p;

var localCert = CreateLocalSunbornCert();

// GFL_TEST_MODE=1: isolated instance for automated tests. Listens only on GFL_SERVER_PORT and never touches
// ports 80/443 or the game's version.dll, so it can run next to the real server without disturbing the game.
bool testMode = Environment.GetEnvironmentVariable("GFL_TEST_MODE") == "1";

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(serverPort);
    if (testMode) return;
    try { options.Listen(IPAddress.Loopback, 80); }
    catch (Exception ex) { Console.WriteLine($"[Kestrel] Port 80 notice: {ex.Message}"); }

    try
    {
        options.Listen(IPAddress.Loopback, 443, listenOpts =>
        {
            listenOpts.UseHttps(localCert);
        });
    }
    catch (Exception ex) { Console.WriteLine($"[Kestrel] Port 443 HTTPS notice: {ex.Message}"); }
});

builder.Services.AddControllers();
// KRYUGER_DATA_DIR keeps player data outside the project folder (Kryuger GM Tools updates that folder).
string dataDir = Environment.GetEnvironmentVariable("KRYUGER_DATA_DIR") is { Length: > 0 } dd ? dd : Path.Combine(AppContext.BaseDirectory, "data");
string dbPath = Path.Combine(dataDir, "gfl_game.db");
builder.Services.AddSingleton(new DatabaseManager(dbPath));

var app = builder.Build();

string dataPath = "";
for (var cur = new DirectoryInfo(AppContext.BaseDirectory); cur != null; cur = cur.Parent)
{
    string candidate = Path.Combine(cur.FullName, "data", "gf-data-us");
    if (Directory.Exists(Path.Combine(candidate, "stc"))) { dataPath = candidate; break; }
}
if (string.IsNullOrEmpty(dataPath) || !Directory.Exists(dataPath))
    dataPath = @"e:\Documentos\gf\data\gf-data-us";

Console.WriteLine($"[Startup] Loading static tables from: {dataPath}");
MasterDataManager.Instance.LoadData(dataPath);
Kryuger.Server.Controllers.Tables.DataPath = dataPath;

// Auto-install game redirect hook into Steam directory
// KRYUGER_GAME_DIR lets Kryuger GM Tools point at the user's install; default is the usual Steam path.
string steamGameDir = Environment.GetEnvironmentVariable("KRYUGER_GAME_DIR") is { Length: > 0 } gameDirEnv
    ? gameDirEnv
    : @"C:\Program Files (x86)\Steam\steamapps\common\GIRLS' FRONTLINE EN";
string hookSrc = Path.Combine(Directory.GetCurrentDirectory(), "..", "gfl_redirect", "target", "release", "version.dll");
if (!File.Exists(hookSrc))
    hookSrc = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "runtime", "version.dll"); // prebuilt copy in the repo
if (!File.Exists(hookSrc))
    hookSrc = Path.Combine(AppContext.BaseDirectory, "version.dll");

string hookTarget = Path.Combine(steamGameDir, "version.dll");
string hookParked = hookTarget + ".old";

// The redirect hook must only be in the game folder while this private server runs; otherwise the game cannot
// reach the official servers ("User Center failed to initialize").
void RemoveHook()
{
    try
    {
        if (File.Exists(hookTarget))
        {
            try { File.Delete(hookTarget); }
            catch
            {
                // Windows will not delete a DLL the running game has loaded, but it allows renaming it, which
                // still keeps the next game launch from loading it.
                if (File.Exists(hookParked)) File.Delete(hookParked);
                File.Move(hookTarget, hookParked);
            }
            Console.WriteLine($"[Hook] Removed game redirect hook: {hookTarget}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Hook Warning] Could not remove {hookTarget}: {ex.Message}");
    }
}

if (!testMode && Directory.Exists(steamGameDir) && File.Exists(hookSrc))
{
    try
    {
        try { if (File.Exists(hookParked)) File.Delete(hookParked); } catch { }
        File.Copy(hookSrc, hookTarget, true);
        Console.WriteLine($"[Hook] Installed isolated game redirect hook to: {hookTarget}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Hook Warning] {ex.Message}");
    }
}

// Ctrl+C, closing the console window and normal shutdown all end here.
if (!testMode)
{
    AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveHook();
    Console.CancelKeyPress += (_, _) => RemoveHook();
}

using var upstreamHttp = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false
})
{
    Timeout = TimeSpan.FromMinutes(10)
};

// Self-delimiting response buffer (Content-Length + Connection: close) + upstream passthrough for unmapped CDN/Passport paths
string logFilePath = Path.Combine(AppContext.BaseDirectory, "gfl_server.log");
void LogLine(string msg)
{
    Console.WriteLine(msg);
    try { File.AppendAllText(logFilePath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
}

string captureFlagPath = Path.Combine(AppContext.BaseDirectory, "capture.flag");
string captureLogPath = Path.Combine(AppContext.BaseDirectory, "gfl_capture.log");
string captureIndexPath = Path.Combine(AppContext.BaseDirectory, "capture_index.json");
string capturedSignKey = AuthCode.DefaultKey;

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    string host = context.Request.Host.Host ?? "";
    string scheme = context.Request.Scheme;
    string reqDesc = $"{context.Request.Method} {scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
    LogLine($"[HTTP] {reqDesc}");

    if (HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = 0;
        context.Response.Headers.Connection = "close";
        return;
    }

    var originalBody = context.Response.Body;
    await using var buffer = new MemoryStream();
    context.Response.Body = buffer;

    try
    {
        if (File.Exists(captureFlagPath) &&
            host.Equals("gf-game.sunborngame.com", StringComparison.OrdinalIgnoreCase))
        {
            string targetUrl = $"{scheme}://{host}{context.Request.Path}{context.Request.QueryString}";
            LogLine($"[Capture Passthrough] -> {targetUrl}");

            using var reqMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);
            if (context.Request.ContentLength > 0 || context.Request.Body.CanRead)
            {
                context.Request.Body.Position = 0;
                var reqBuf = new MemoryStream();
                await context.Request.Body.CopyToAsync(reqBuf, context.RequestAborted);
                if (reqBuf.Length > 0)
                {
                    reqBuf.Position = 0;
                    reqMsg.Content = new StreamContent(reqBuf);
                    if (!string.IsNullOrEmpty(context.Request.ContentType))
                        reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
            }

            foreach (var h in context.Request.Headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            using var resp = await upstreamHttp.SendAsync(reqMsg, context.RequestAborted);
            context.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers)
            {
                if (!h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[h.Key] = h.Value.ToArray();
            }
            foreach (var h in resp.Content.Headers)
            {
                if (!h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[h.Key] = h.Value.ToArray();
            }

            byte[] respBytes = await resp.Content.ReadAsByteArrayAsync(context.RequestAborted);
            await buffer.WriteAsync(respBytes, context.RequestAborted);

            try
            {
                string rawResp = System.Text.Encoding.UTF8.GetString(respBytes);
                string decoded = rawResp;
                string reqPath = context.Request.Path.Value ?? "";
                if (reqPath.Contains("getUid", StringComparison.OrdinalIgnoreCase))
                {
                    decoded = AuthCode.Decode(rawResp, AuthCode.DefaultKey);
                    var jobj = JObject.Parse(decoded);
                    string? sign = jobj["sign"]?.ToString();
                    if (!string.IsNullOrEmpty(sign)) capturedSignKey = sign;
                }
                else if (rawResp.StartsWith("#") || !rawResp.TrimStart().StartsWith("{"))
                {
                    try { decoded = AuthCode.Decode(rawResp, capturedSignKey); }
                    catch
                    {
                        try { decoded = AuthCode.Decode(rawResp, AuthCode.DefaultKey); } catch { }
                    }
                }

                File.AppendAllText(captureLogPath, $"=== {DateTime.Now:HH:mm:ss} {reqPath} ===\n{decoded}\n\n");
                if (reqPath.EndsWith("/Index/index", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(captureIndexPath, decoded);
            }
            catch (Exception ex)
            {
                File.AppendAllText(captureLogPath, $"=== {DateTime.Now:HH:mm:ss} {context.Request.Path} DECODE ERR: {ex.Message} ===\n");
            }
        }
        else
        {
            await next();
        }

        if (context.Response.StatusCode == StatusCodes.Status404NotFound &&
            (context.Request.Path.Value?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true))
        {
            buffer.SetLength(0);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/xml; charset=utf-8";
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<servers>\n\t<server>\n\t\t<version>default</version>\n\t\t<channel>en_mica</channel>\n\t\t<url>http://127.0.0.1:5000/index.php</url>\n\t\t<name>Kryuger</name>\n\t</server>\n</servers>";
            byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
            await buffer.WriteAsync(xmlBytes, context.RequestAborted);
            LogLine($"[Fallback XML] Served server.xml for {context.Request.Path}");
        }
        else if (context.Response.StatusCode == StatusCodes.Status404NotFound &&
            context.Request.Path.Value?.Contains("userProtocol", StringComparison.OrdinalIgnoreCase) == true)
        {
            buffer.SetLength(0);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            byte[] okBytes = System.Text.Encoding.UTF8.GetBytes("{\"errorReport\":{\"protocol_id\":0,\"error_msg\":\"\",\"error_no\":0}}");
            await buffer.WriteAsync(okBytes, context.RequestAborted);
            LogLine($"[UserProtocol OK] Responded 0 to {context.Request.Path}");
        }
        else if (context.Response.StatusCode == StatusCodes.Status404NotFound &&
            IsUpstreamAsset(host))
        {
            buffer.SetLength(0);
            context.Response.Headers.Clear();
            string targetUrl = $"{scheme}://{host}{context.Request.Path}{context.Request.QueryString}";
            LogLine($"[Passthrough] -> {targetUrl}");

            using var reqMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);
            if (context.Request.ContentLength > 0 || context.Request.Body.CanRead)
            {
                var reqBuf = new MemoryStream();
                await context.Request.Body.CopyToAsync(reqBuf, context.RequestAborted);
                if (reqBuf.Length > 0)
                {
                    reqBuf.Position = 0;
                    reqMsg.Content = new StreamContent(reqBuf);
                    if (!string.IsNullOrEmpty(context.Request.ContentType))
                        reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
            }

            foreach (var h in context.Request.Headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            string cacheDir = Path.Combine(AppContext.BaseDirectory, "data", "passthrough_cache");
            string safeName = string.Concat((host + (context.Request.Path.Value ?? "")).Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
            string cacheFile = Path.Combine(cacheDir, safeName + ".bin");
            bool isPassport = host.Equals("gf-passport.sunborngame.com", StringComparison.OrdinalIgnoreCase);
            try
            {
                using var upCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                upCts.CancelAfter(TimeSpan.FromSeconds(isPassport ? 8 : 60));
                using var resp = await upstreamHttp.SendAsync(reqMsg, upCts.Token);
                byte[] respBytes = await resp.Content.ReadAsByteArrayAsync(upCts.Token);
                if (!resp.IsSuccessStatusCode && isPassport && File.Exists(cacheFile))
                    throw new HttpRequestException($"upstream status {(int)resp.StatusCode}");

                context.Response.StatusCode = (int)resp.StatusCode;
                foreach (var h in resp.Headers)
                {
                    if (!h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                foreach (var h in resp.Content.Headers)
                {
                    if (!h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                await buffer.WriteAsync(respBytes, context.RequestAborted);
                LogLine($"[Passthrough] <- {(int)resp.StatusCode} ({respBytes.Length} bytes) {targetUrl}");

                if (isPassport)
                {
                    LogLine($"[Passport Body] {context.Request.Path} <- {System.Text.Encoding.UTF8.GetString(respBytes)}");
                    if (resp.IsSuccessStatusCode)
                    {
                        try
                        {
                            Directory.CreateDirectory(cacheDir);
                            File.WriteAllBytes(cacheFile, respBytes);
                            File.WriteAllText(cacheFile + ".type", resp.Content.Headers.ContentType?.ToString() ?? "application/json");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                // Upstream unreachable or failing: replay the last good response so the client's
                // User Center can still initialize instead of the whole login failing.
                buffer.SetLength(0);
                context.Response.Headers.Clear();
                if (File.Exists(cacheFile))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = File.Exists(cacheFile + ".type") ? File.ReadAllText(cacheFile + ".type") : "application/json";
                    await buffer.WriteAsync(File.ReadAllBytes(cacheFile), context.RequestAborted);
                    LogLine($"[Passthrough Fallback] {ex.Message} -> served cached {context.Request.Path}");
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    LogLine($"[Passthrough Error] {ex.Message} (no cache yet for {context.Request.Path})");
                }
            }
        }
        else if (context.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            LogLine($"[404 NOT FOUND] {reqDesc}");
        }

        if (!context.Response.HasStarted)
        {
            context.Response.Headers.Remove("Transfer-Encoding");
            context.Response.ContentLength = buffer.Length;
            context.Response.Headers.Connection = "close";
        }

        LogLine($"[HTTP RESP] {context.Request.Path} -> {context.Response.StatusCode} ({buffer.Length} bytes)");

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);
    }
    finally
    {
        context.Response.Body = originalBody;
    }
});

app.MapControllers();

Console.WriteLine("=================================================");
Console.WriteLine(" KRYUGER - GIRLS' FRONTLINE EN PRIVATE SERVER");
Console.WriteLine($" HTTP:  http://0.0.0.0:{serverPort} & http://127.0.0.1:80");
Console.WriteLine(" HTTPS: https://127.0.0.1:443 (Mica Passport + SSL)");
Console.WriteLine(" Redirection: Native Isolated DLL Hook (version.dll)");
Console.WriteLine($" GM API: /gm (token in {Kryuger.Server.Controllers.GmController.TokenFile})");
_ = Kryuger.Server.Controllers.GmController.Token();
Console.WriteLine(" Windows Proxy: DISABLED (PC Internet unaffected)");
Console.WriteLine("=================================================");

if (!testMode) app.Lifetime.ApplicationStopping.Register(RemoveHook);
app.Run();
if (!testMode) RemoveHook();

// Any redirected SunBorn host other than the game API itself (CDNs, passport, the alternative resource packs the
// client switches to after the factory "naive" recipe, etc.) is forwarded upstream when the server has no route.
static bool IsUpstreamAsset(string host)
{
    string h = host.ToLowerInvariant();
    if (h is "127.0.0.1" or "localhost" || h.StartsWith("gf-game.") || h.StartsWith("gfus-game.")) return false;
    return h.Contains("sunborngame.com") || h.Contains("girlfrontline") || h.Contains("micateam");
}
