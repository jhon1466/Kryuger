using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kryuger.Common.Crypto;
using Kryuger.Common.Database;
using Kryuger.Common.Models;
using Kryuger.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    [ApiController]
    public partial class GameController : ControllerBase
    {
        private readonly DatabaseManager _db;

        public GameController(DatabaseManager db)
        {
            _db = db;
        }

        [NonAction]
        public Task<IActionResult> DispatchOpForTest(string op, JObject? body, PlayerAccount? player)
        {
            return DispatchOpAsync(op, body, player);
        }

        private async Task<(string op, JObject? body, PlayerAccount? player)> ParseGflRequest(HttpContext ctx)
        {
            string path = ctx.Request.Path.Value ?? "";
            string op = path.TrimStart('/');
            int idx = path.IndexOf("index.php", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string sub = path[(idx + 9)..].TrimStart('/');
                int slash = sub.IndexOf('/');
                if (slash >= 0 && int.TryParse(sub[..slash], out _))
                    op = sub[(slash + 1)..];
                else
                    op = sub;
            }

            IFormCollection? form = null;
            if (ctx.Request.HasFormContentType)
            {
                try { form = await ctx.Request.ReadFormAsync(); } catch { }
            }

            string uidStr = form?["uid"].ToString() ?? ctx.Request.Query["uid"].ToString();
            long.TryParse(uidStr, out long uid);

            PlayerAccount? player = uid > 0 ? _db.GetPlayerById(uid) : null;
            string key = player?.SignKey ?? AuthCode.DefaultKey;

            string rawPayload = "";
            if (form != null)
            {
                if (!string.IsNullOrEmpty(form["outdatacode"]))
                    rawPayload = form["outdatacode"].ToString();
                else if (!string.IsNullOrEmpty(form["signcode"]))
                    rawPayload = form["signcode"].ToString();
            }

            JObject? body = null;
            if (!string.IsNullOrEmpty(rawPayload))
            {
                try
                {
                    string decoded = AuthCode.Decode(rawPayload, key);
                    string t = decoded?.TrimStart() ?? "";
                    if (t.StartsWith("{"))
                        body = JObject.Parse(t);
                    else if (t.StartsWith("["))
                        body = new JObject { ["_array"] = JArray.Parse(t) };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Decode Error] op: {op}, err: {ex.Message}");
                }
            }

            return (op, body, player);
        }

        /// <summary>Op being handled; lets EncryptedResponse complete mission replies with the official fields.</summary>
        private string _currentOp = "";

        private IActionResult EncryptedResponse(object data, string key, bool compress = true)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.None);
            json = CompleteOfficialFields(_currentOp, json);
            string enc = AuthCode.Encode(json, key, compress);
            return Content(enc, "text/plain; charset=utf-8");
        }

        private IActionResult PlainSuccess() => Content("1", "text/plain; charset=utf-8");

        // Server list XML endpoints
        [HttpGet("/server.xml")]
        [HttpGet("/channel_server.xml")]
        [HttpGet("/serverxml/{fileName}")]
        [HttpGet("/asset/serverxml/{fileName}")]
        [HttpGet("/android/server.xml")]
        [HttpGet("/android/{fileName}")]
        [HttpGet("/pc/{fileName}")]
        [HttpGet("/steam/{fileName}")]
        public IActionResult GetServerXml(string? fileName)
        {
            if (!string.IsNullOrEmpty(fileName) && !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return NotFound();

            string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<servers>
	<server>
		<version>default</version>
		<channel>en_mica</channel>
		<url>http://127.0.0.1:5000/index.php</url>
		<name>Kryuger</name>
	</server>
</servers>";
            Console.WriteLine($"[ServerList XML] Served server.xml for request: {Request.Path}");
            return Content(xml, "application/xml; charset=utf-8");
        }

        private static readonly System.Net.Http.HttpClient _transitHttp = new(new System.Net.Http.SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static void LogToFile(string msg)
        {
            Console.WriteLine(msg);
            try { System.IO.File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gfl_server.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        private async Task<IActionResult> HandleTransitRoot()
        {
            Request.EnableBuffering();
            Request.Body.Position = 0;
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            string rawBody = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            LogToFile($"[Transit POST /index.php] Body: {rawBody}");

            try
            {
                string targetUrl = $"http://gf-transit.sunborngame.com/index.php{Request.QueryString}";
                using var reqMsg = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, targetUrl);
                reqMsg.Content = new System.Net.Http.StringContent(rawBody, Encoding.UTF8, Request.ContentType?.Split(';')[0] ?? "application/x-www-form-urlencoded");
                foreach (var h in Request.Headers)
                {
                    if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
                }

                using var resp = await _transitHttp.SendAsync(reqMsg);
                string respText = await resp.Content.ReadAsStringAsync();
                string contentType = resp.Content.Headers.ContentType?.ToString() ?? "text/html; charset=utf-8";
                LogToFile($"[Transit Upstream] Status: {(int)resp.StatusCode}, Type: {contentType}, Resp: {respText}");

                return Content(respText, contentType);
            }
            catch (Exception ex)
            {
                LogToFile($"[Transit Error] {ex.Message}");
                return GetServerXml(null);
            }
        }

        private static string _officialSignKey = AuthCode.DefaultKey;

        private async Task<IActionResult> HandleOfficialPassthrough()
        {
            Request.EnableBuffering();
            Request.Body.Position = 0;
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            string rawBody = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            string targetUrl = $"http://gf-game.sunborngame.com{Request.Path}{Request.QueryString}";
            using var reqMsg = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(Request.Method), targetUrl);
            if (!string.IsNullOrEmpty(rawBody))
                reqMsg.Content = new System.Net.Http.StringContent(rawBody, Encoding.UTF8, Request.ContentType?.Split(';')[0] ?? "application/x-www-form-urlencoded");
            foreach (var h in Request.Headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            using var resp = await _transitHttp.SendAsync(reqMsg);
            string respText = await resp.Content.ReadAsStringAsync();
            string contentType = resp.Content.Headers.ContentType?.ToString() ?? "text/html; charset=utf-8";

            string reqDecoded = rawBody;
            if (rawBody.Contains("outdatacode="))
            {
                var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(rawBody);
                if (form.TryGetValue("outdatacode", out var codeVal))
                {
                    foreach (var k in new[] { _officialSignKey, AuthCode.DefaultKey })
                    {
                        try { reqDecoded = AuthCode.Decode(codeVal.ToString(), k); break; } catch { }
                    }
                }
            }

            string respDecoded = respText;
            if (!string.IsNullOrWhiteSpace(respText) && respText.Trim() != "1" && !respText.TrimStart().StartsWith("{"))
            {
                foreach (var k in new[] { _officialSignKey, AuthCode.DefaultKey })
                {
                    try { respDecoded = AuthCode.Decode(respText, k); break; } catch { }
                }
            }

            if (Request.Path.Value?.Contains("getUid", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var j = JObject.Parse(respDecoded);
                    var s = j["sign"]?.ToString();
                    if (!string.IsNullOrEmpty(s)) _officialSignKey = s;
                }
                catch { }
            }

            string logPath = Path.Combine(AppContext.BaseDirectory, "captured_official.log");
            string respSummary = Request.Path.Value?.Contains("Index/index", StringComparison.OrdinalIgnoreCase) == true && respDecoded.Length > 2000
                ? respDecoded[..1000] + $"... <truncated {respDecoded.Length} chars>"
                : respDecoded;
            try
            {
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {Request.Method} {Request.Path}\nREQ: {reqDecoded}\nRESP ({(int)resp.StatusCode}): {respSummary}\n\n");
            }
            catch { }

            return Content(respText, contentType);
        }

        [Route("index.php/{**rest}")]
        [Route("1000/{**rest}")]
        [Route("1001/{**rest}")]
        public async Task<IActionResult> HandleIndex(string? rest)
        {
            if (string.IsNullOrWhiteSpace(rest) && Request.Path.Value?.TrimEnd('/').EndsWith("index.php", StringComparison.OrdinalIgnoreCase) == true)
            {
                return await HandleTransitRoot();
            }

            if (Environment.GetEnvironmentVariable("GFL_CAPTURE_OFFICIAL") == "1")
            {
                return await HandleOfficialPassthrough();
            }

            // Serialize game requests: each handler loads the whole player document and saves it back,
            // so concurrent requests would overwrite each other's changes (lost updates).
            await _requestLock.WaitAsync();
            try { return await HandleIndexLocked(); }
            finally { _requestLock.Release(); }
        }

        internal static readonly SemaphoreSlim _requestLock = new(1, 1);

        private async Task<IActionResult> HandleIndexLocked()
        {
            var (op, body, player) = await ParseGflRequest(HttpContext);
            _currentOp = op;
            if (string.IsNullOrWhiteSpace(op))
            {
                return await HandleTransitRoot();
            }

            LogToFile($"[GFL Request] Op: {op}, Player: {player?.Id} ({player?.Name})");
            if (body != null && !op.Equals("index/crashlog", StringComparison.OrdinalIgnoreCase))
            {
                LogToFile($"[GFL Body] {op} -> {body.ToString(Formatting.None)}");
            }

            return await DispatchOpAsync(op, body, player);
        }

        private async Task<IActionResult> DispatchOpAsync(string op, JObject? body, PlayerAccount? player)
        {
            var extended = TryHandleExtended(op, body, player);
            if (extended != null) return extended;

            return op.ToLowerInvariant() switch
            {
                "index/version" => HandleVersion(),
                "index/getserverlist" => HandleServerListJson(),
                "index/serverlist" => HandleServerListJson(),
                "index/getzonelist" => HandleServerListJson(),
                "index/getuidenmicaqueue" => await HandleGetUid(),
                "index/getdigitalskyuser" => await HandleGetUid(),
                "index/getuiden" => await HandleGetUid(),
                "index/quickgetuiden" => await HandleGetUid(),
                "index/login" => await HandleGetUid(),
                "index/checksign" => await HandleGetUid(),
                "index/index" => HandleGetUserInfo(player),
                "index/setusername" => HandleSetUserName(player, body),
                "index/modname" => HandleSetUserName(player, body),
                "index/changeusername" => HandleSetUserName(player, body),
                "index/changename" => HandleSetUserName(player, body),
                "friend/changename" => HandleSetUserName(player, body),
                "friend/changeintro" => HandleChangeIntro(player, body),
                "index/changegender" => HandleChangeGender(player, body),
                "commander/changegender" => HandleChangeGender(player, body),
                "index/guide" => HandleGuide(player, body),
                "index/adjutant" => HandleAdjutant(player, body),
                "index/gamesetting" => HandleGameSetting(player, body),
                "index/downloadsuccess" => PlainSuccess(),
                "index/fixunbrokentargets" => PlainSuccess(),
                "index/crashlog" => await HandleCrashLog(player, body),
                "index/attendance" => HandleAttendance(player),
                "index/sevenattendance" => HandleSevenAttendance(player),
                "index/carnival" => HandleCarnival(player),
                "index/quickgetquestsresourceinmails" => HandleQuickGetQuestsResourceInMails(player),
                "index/listtodo" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "index/quest" => HandleQuest(player),
                "index/home" => HandleHome(player),
                "index/getresourceinmail" => EncryptedResponse(new object?[] { null }, player?.SignKey ?? AuthCode.DefaultKey),
                "index/getmaillist" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "index/geteventinfo" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "index/achievement" => EncryptedResponse(new { id = (player?.Id ?? 0).ToString(), user_id = (player?.Id ?? 0).ToString(), different_gun = (player?.Guns.Select(g => g.GunId).Distinct().Count() ?? 0).ToString() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/dorminfo" => HandleDormInfo(player),
                "friend/applylist" => EncryptedResponse(new { list = new List<object>(), apply_from_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/list" => EncryptedResponse(new { list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/mall" => EncryptedResponse(new { list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/mallbytype9" => EncryptedResponse(new { mall_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/messagelist" => EncryptedResponse(new { list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/randomlist" => EncryptedResponse(new { list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "friend/teamandpraiseprize" => EncryptedResponse(new { team_prize_num = 0, praise_num = 0, vehicle_maintain_num = "10" }, player?.SignKey ?? AuthCode.DefaultKey),
                "dorm/gasha" => EncryptedResponse(new { gasha_exchange_list = new List<object>(), activity_list = new List<object>(), gift_list = new List<object>(), furniture_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "dorm/receive_favor" => HandleDormReceiveFavor(player, body),
                "dorm/changeskin" => HandleUseSkin(player, body),
                "dorm/kalinafavor" => HandleKalinaFavorUpLevel(player, body),
                "dorm/kalinafavoruplevel" => HandleKalinaFavorUpLevel(player, body),
                "dorm/share" => PlainSuccess(),
                "dorm/skinread" => PlainSuccess(),
                "event/letterindex" => EncryptedResponse(new Dictionary<string, object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "explore/getmalllist" => EncryptedResponse(BuildExploreMallList(), player?.SignKey ?? AuthCode.DefaultKey),
                "giftbox/checkovertime" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "manual/readlist" => EncryptedResponse(new { user_id = player?.Id ?? 0, tutorial_manual_id = new List<object>(), tutorial_guide_id = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "sangvis/gasha" => EncryptedResponse(new { event_info = new List<object>(), item_info = new List<object>(), sangvis_gasha_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "sangvis/duplicateindex" => EncryptedResponse(new { sangvis_duplicate_list = new List<object>(), win_1 = 0, win_2 = 0, win_3 = 0 }, player?.SignKey ?? AuthCode.DefaultKey),
                "sangvis/gasharecover" => EncryptedResponse(new { item_info = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "battlepass/index" => EncryptedResponse(BuildBattlepassIndex(player), player?.SignKey ?? AuthCode.DefaultKey),
                "battlepass/getallbonus" => EncryptedResponse(new { free_bonus = new Dictionary<string, object>(), paid_bonus = new Dictionary<string, object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "mall/statictables" => EncryptedResponse(BuildMallStaticTablesPayload(player), player?.SignKey ?? AuthCode.DefaultKey),
                "fetter/orginfo" => EncryptedResponse(new
                {
                    org_info_with_user = new { user_id = player?.Id ?? 0, max_gun = new List<object>(), max_fairy = new List<object>(), max_squad = new List<object>(), max_sangvis = new List<object>(), npc = new List<object>() },
                    org_department_info = new List<object>()
                }, player?.SignKey ?? AuthCode.DefaultKey),
                "gun/developcollectlist" => EncryptedResponse(new { log_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "gun/likelist" => EncryptedResponse(new { rank = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "gun/developlog" => EncryptedResponse(new { log_list = new List<object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "gun/developgun" => HandleDevelopGun(player, body),
                "gun/finishdevelop" => HandleFinishDevelop(player, body),
                "gun/eatgunindex" => PlainSuccess(),
                "gun/teamgun" => HandleTeamGun(player, body),
                "gun/setposition" => HandleSetPosition(player, body),
                "gun/changelock" => HandleChangeLock(player, body),
                "gun/setcollect" => PlainSuccess(),
                "gun/retiregun" => HandleRetireGun(player, body),
                "gun/eatgun" => HandleEatGun(player, body),
                "gun/combinegun" => HandleCombineGun(player, body),
                "gun/upgradeskill" => HandleUpgradeSkill(player, body),
                "gun/mindupdate" => HandleMindUpdate(player, body),
                "gun/useskin" => HandleUseSkin(player, body),
                "gun/changeskin" => HandleUseSkin(player, body),
                "gun/skin" => HandleUseSkin(player, body),
                "equip/gunequip" => HandleGunEquip(player, body),
                "equip/changelock" => PlainSuccess(),
                "operation/startoperation" => HandleStartOperation(player, body),
                "operation/finishoperation" => HandleFinishOperation(player, body),
                "automission/startautomission" => PlainSuccess(),
                "mission/combinationinfo" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "mission/drawevent" => EncryptedResponse(new List<object>(), player?.SignKey ?? AuthCode.DefaultKey),
                "mission/eventdraw" => EncryptedResponse(new { mission_group_today_reset_num = new List<object>(), mission_event = new Dictionary<string, object>() }, player?.SignKey ?? AuthCode.DefaultKey),
                "mission/startmission" => HandleStartMission(player, body),
                "mission/reinforceteam" => HandleReinforceTeam(player, body),
                "mission/teammove" => HandleTeamMove(player, body),
                "mission/supplyteam" => HandleSupplyTeam(player, body),
                "mission/battlefinish" => HandleBattleFinish(player, body),
                "mission/endturn" => HandleEndTurn(player, body),
                "mission/startenemyturn" => HandleStartEnemyTurn(player, body),
                "mission/endenemyturn" => HandleEndEnemyTurn(player, body),
                "mission/startturn" => HandleStartTurn(player, body),
                "mission/abortmission" => HandleAbortMission(player, body),
                _ => HandleGenericSuccess(player, op, body)
            };
        }

        private IActionResult HandleVersion()
        {
            // Same shape as the official server's Index/version (captured): PST8PDT day boundaries.
            var pst = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var nowUtc = DateTimeOffset.UtcNow;
            var local = TimeZoneInfo.ConvertTime(nowUtc, pst);
            DateTimeOffset At(DateTime d) => new DateTimeOffset(d, pst.GetUtcOffset(d));
            long tomorrow = At(local.Date.AddDays(1)).ToUnixTimeSeconds();
            var monthStart = new DateTime(local.Year, local.Month, 1);
            long monthZero = At(monthStart).ToUnixTimeSeconds();
            long nextMonthZero = At(monthStart.AddMonths(1)).ToUnixTimeSeconds();
            int weekday = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;

            return Ok(new
            {
                now = nowUtc.ToUnixTimeSeconds().ToString(),
                tomorrow_zero = tomorrow.ToString(),
                month_zero = monthZero,
                next_month_zero = nextMonthZero,
                timezone = "PST8PDT",
                data_version = "45898a92847d339fe9a9dfd7584ec4f5",
                client_version = "30810",
                ab_version = "2026082516",
                is_kick = "0",
                weekday = weekday,
                authentication_url = "http://realauth.ucenter.ppgame.com/authoriz.html?appid={0}&openid={1}&accounttype=1&language=zh"
            });
        }

        private IActionResult HandleServerListJson()
        {
            var list = new[]
            {
                new
                {
                    id = "1000",
                    name = "Kryuger",
                    addr = "http://127.0.0.1:5000/index.php",
                    status = 1
                }
            };
            return Ok(list);
        }

        private async Task<IActionResult> HandleGetUid()
        {
            string openId = "";
            string steamId = "";
            string email = "";

            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                LogToFile($"[GetUid Form] {string.Join(", ", form.Select(kv => $"{kv.Key}={kv.Value}"))}");
                openId = form["openid"].ToString();
                steamId = form["steamid"].ToString();
                email = form["email"].ToString();
                if (string.IsNullOrEmpty(email)) email = form["mail"].ToString();
                if (string.IsNullOrEmpty(email)) email = form["account"].ToString();
                if (string.IsNullOrEmpty(openId)) openId = email;
                if (string.IsNullOrEmpty(openId)) openId = form["sid"].ToString();
                if (string.IsNullOrEmpty(openId)) openId = form["username"].ToString();
            }

            if (string.IsNullOrEmpty(openId))
                openId = Request.Query["openid"].ToString();
            if (string.IsNullOrEmpty(openId))
                openId = Request.Query["email"].ToString();

            if (string.IsNullOrEmpty(openId))
                openId = "commander_steam";

            var player = _db.GetOrCreatePlayer(openId, steamId);
            if (player.SignKey.Length != 32)
            {
                // Official session keys are 32 hex chars.
                player.SignKey = Guid.NewGuid().ToString("N");
                _db.SavePlayer(player);
            }
            if (!string.IsNullOrEmpty(email) && player.Name == "Commander")
            {
                player.Name = email.Split('@')[0];
                _db.SavePlayer(player);
            }

            Console.WriteLine($"[Auth Success] Player ID: {player.Id}, Account: {player.OpenId}, Sign: {player.SignKey}");
            LogToFile($"[Auth Success] Player ID: {player.Id}, Account: {player.OpenId}, Sign: {player.SignKey}");

            return EncryptedResponse(new
            {
                uid = player.Id.ToString(),
                sign = player.SignKey,
                is_username_exist = !string.IsNullOrEmpty(player.Name),
                real_name = 0,
                authentication_url = "http://realauth.ucenter.ppgame.com/authoriz.html?appid={0}&openid={1}&accounttype=1&language=zh",
                tc_order_retry = 0
            }, AuthCode.DefaultKey);
        }

        private IActionResult HandleSetUserName(PlayerAccount? player, JObject? body)
        {
            if (player == null) return PlainSuccess();
            string? name = body?.Value<string>("name")
                ?? body?.Value<string>("username")
                ?? body?.Value<string>("user_name")
                ?? body?.Value<string>("new_name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                player.Name = name.Trim();
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleChangeIntro(PlayerAccount? player, JObject? body)
        {
            if (player == null) return PlainSuccess();
            string? intro = body?["intro"]?.ToString();
            if (intro != null)
            {
                player.Intro = intro;
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleChangeGender(PlayerAccount? player, JObject? body)
        {
            if (player == null) return PlainSuccess();
            string? gender = body?["gender"]?.ToString();
            if (!string.IsNullOrEmpty(gender))
            {
                player.Gender = gender;
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleGetUserInfo(PlayerAccount? player)
        {
            if (player == null)
                return BadRequest("Player not found");

            if (player.Extra.Remove("starter_pending"))
            {
                // Official starter echelon: M1911, M1895 (Nagant Revolver) and P38 in echelon 1.
                int[] starters = { 2, 5, 9 };
                int[] cells = { 7, 12, 17 };
                for (int i = 0; i < starters.Length; i++)
                {
                    var g = GiveGun(player, starters[i]);
                    g.TeamId = 1; g.Location = i + 1; g.Position = cells[i];
                }
                _db.SavePlayer(player);
            }

            _db.EnsureAllGunsAndSkins(
                player,
                MasterDataManager.Instance.Guns.Keys.Where(id => id < 9000),
                MasterDataManager.Instance.Skins.Keys);

            // A mission in progress is kept and sent back as mission_act_info so the client can resume it
            // (it used to be dropped on every login).

            bool statsUpdated = false;
            foreach (var g in player.Guns)
            {
                if (g.GunLevel >= 100 && g.Pow == 0 && g.Hit == 0 && g.Dodge == 0 && g.Rate == 0)
                {
                    var (maxP, maxH, maxD, maxR) = Tables.GetMaxEnhancement(g.GunId, g.GunLevel);
                    g.Pow = maxP;
                    g.Hit = maxH;
                    g.Dodge = maxD;
                    g.Rate = maxR;
                    statsUpdated = true;
                }
            }
            if (SeedFullInventory(player)) statsUpdated = true;
            if (statsUpdated) _db.SavePlayer(player);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string gunCollect = string.Join(",", player.Guns.Select(g => g.GunId).Distinct());
            string courseZeros = string.Join(",", Enumerable.Repeat(1, 93));

            var response = new Dictionary<string, object?>
            {
                ["user_info"] = new Dictionary<string, object?>
                {
                    ["gem"] = player.Gem.ToString(),
                    ["id"] = player.Id.ToString(),
                    ["user_id"] = player.Id.ToString(),
                    ["open_id"] = player.OpenId,
                    ["channel_id"] = "0",
                    ["sign"] = player.SignKey,
                    ["name"] = string.IsNullOrEmpty(player.Name) ? "Commander" : player.Name,
                    ["gem_pay"] = player.GemPay.ToString(),
                    ["pause_turn_chance"] = "3",
                    ["experience"] = player.Experience.ToString(),
                    ["lv"] = player.Level.ToString(),
                    ["maxequip"] = player.MaxEquip.ToString(),
                    ["maxgun"] = player.MaxGun.ToString(),
                    ["maxteam"] = player.MaxTeam.ToString(),
                    ["max_build_slot"] = player.MaxBuildSlot.ToString(),
                    ["max_equip_build_slot"] = player.MaxEquipBuildSlot.ToString(),
                    ["max_fix_slot"] = player.MaxFixSlot.ToString(),
                    ["max_upgrade_slot"] = player.MaxUpgradeSlot.ToString(),
                    ["max_gun_preset"] = "10",
                    ["max_fairy"] = "100",
                    ["max_sangvis"] = "100",
                    ["bp"] = player.Bp.ToString(),
                    ["bp_pay"] = "0",
                    ["mp"] = player.Mp.ToString(),
                    ["ammo"] = player.Ammo.ToString(),
                    ["mre"] = player.Mre.ToString(),
                    ["part"] = player.Part.ToString(),
                    ["core"] = "999",
                    ["coin1"] = "999",
                    ["coin2"] = "999",
                    ["coin3"] = "999",
                    ["monthlycard1_end_time"] = "0",
                    ["monthlycard2_end_time"] = "0",
                    ["monthlycard3_end_time"] = "0",
                    ["growthfond"] = "0",
                    ["last_recover_time"] = now.ToString(),
                    ["last_bp_recover_time"] = "0",
                    ["last_favor_recover_time"] = now.ToString(),
                    ["last_monthlycard1_recover_time"] = "0",
                    ["last_monthlycard2_recover_time"] = "0",
                    ["last_monthlycard3_recover_time"] = "0",
                    ["last_login_time"] = now.ToString(),
                    ["reg_time"] = now.ToString(),
                    ["gun_collect"] = gunCollect,
                    ["equip_collect"] = "",
                    ["maxdorm"] = "2",
                    ["maxdorm_spare"] = "106",
                    ["last_dorm_material_change_time1"] = "0",
                    ["last_dorm_material_change_time2"] = "0",
                    ["material_available_num1"] = "0",
                    ["material_available_num2"] = "0",
                    ["dorm_max_score"] = "0",
                    ["last_ssoc_change_time"] = "0",
                    ["app_guard_id"] = null,
                    ["app_guard_num"] = "0",
                    ["is_bind"] = "0",
                    ["dorm_max_score_lifetime"] = "6400",
                    ["gender"] = string.IsNullOrEmpty(player.Gender) ? "0" : player.Gender
                },
                ["upgrade_act_info"] = player.UpgradeSlots.Select(u => new Dictionary<string, object>
                {
                    ["user_id"] = player.Id.ToString(), ["gun_with_user_id"] = u.GunInstId.ToString(), ["skill"] = u.Skill.ToString(),
                    ["upgrade_slot"] = u.Slot.ToString(), ["fairy_with_user_id"] = "0", ["end_time"] = u.EndTime.ToString()
                }).ToList(),
                ["guide_info"] = new Dictionary<string, object>
                {
                    ["user_id"] = player.Id,
                    ["guide"] = CompletedGuide(player.Guide)
                },
                ["item_with_user_info"] = player.Items.Select(kv => new Dictionary<string, object>
                {
                    ["item_id"] = kv.Key,
                    ["number"] = kv.Value.ToString(),
                    ["last_recover_time"] = "0"
                }).ToList(),
                ["item_limit_with_user_type9_info"] = new List<object>(),
                ["mission_with_user_info"] = BuildMissionWithUserInfo(player),
                ["operation_act_info"] = player.ActiveOperations.Select(o => new Dictionary<string, object>
                {
                    ["operation_id"] = o.OperationId.ToString(),
                    ["team_id"] = o.TeamId.ToString(),
                    ["start_time"] = o.StartTime.ToString(),
                    ["duration"] = o.Duration.ToString()
                }).ToList(),
                ["develop_act_info"] = player.DevelopSlots.Select(s => new Dictionary<string, object>
                {
                    ["build_slot"] = s.Slot.ToString(),
                    ["gun_id"] = s.GunId.ToString(),
                    ["start_time"] = s.StartTime.ToString(),
                    ["duration"] = s.Duration.ToString()
                }).ToList(),
                ["fix_act_info"] = player.FixSlots.Select(f => new Dictionary<string, object>
                {
                    ["user_id"] = player.Id.ToString(), ["gun_with_user_id"] = f.GunInstId.ToString(),
                    ["fix_slot"] = f.Slot.ToString(), ["end_time"] = f.EndTime.ToString(), ["fix_end_time"] = f.EndTime.ToString()
                }).ToList(),
                ["mission_act_info"] = BuildMissionActInfo(player),
                ["spot_act_info"] = null,
                ["user_record"] = new Dictionary<string, object>
                {
                    ["user_id"] = player.Id,
                    ["mission_campaign"] = ComputeMissionCampaign(player),
                    ["special_mission_campaign"] = "",
                    ["attendance_type1_day"] = GetAttendanceDay(player),
                    ["attendance_type1_time"] = GetAttendanceNextTime(player),
                    ["attendance_type2_day"] = "0",
                    ["attendance_type2_time"] = "0",
                    ["develop_lowrank_count"] = "0",
                    ["special_develop_lowrank_count"] = "0",
                    ["get_gift_ids"] = "",
                    ["spend_point"] = 0,
                    ["gem_mall_ids"] = "",
                    ["product_type21_ids"] = "",
                    ["seven_type"] = SevenType(player),
                    ["seven_start_time"] = GetSevenStartTime(player),
                    ["seven_attendance_days"] = GetSevenAttendanceDays(player),
                    ["seven_spend_point"] = 0,
                    ["last_bp_buy_time"] = 0,
                    ["bp_buy_count"] = 0,
                    ["new_developgun_count"] = 0,
                    ["buyitem1_developgun_count"] = 0,
                    ["buyitem1_specialdevelopgun_count"] = 0,
                    ["buyitem1_num"] = 0,
                    ["last_developgun_time"] = 0,
                    ["last_specialdevelopgun_time"] = 0,
                    ["furniture_classes"] = "2",
                    ["adjutant_multi"] = player.AdjutantMulti ?? "",
                    ["adjutant"] = player.Adjutant ?? "",
                    ["adjutant_fairy"] = player.AdjutantFairy ?? "",
                    ["desktop_pet"] = "",
                    ["mission_group_today_reset_num"] = 0,
                    ["mission_group_last_reset_time"] = 0,
                    ["spend_point_thismonth"] = 0,
                    ["last_spendpoint_time"] = 0,
                    ["spendpoint_age_id"] = 0,
                    ["next_core_recover_gun_time"] = 0,
                    ["next_core_recover_equip_time"] = 0,
                    ["last_datacell_change_time"] = now,
                    ["buymp_num"] = 0,
                    ["buyammo_num"] = 0,
                    ["buymre_num"] = 0,
                    ["buypart_num"] = 0,
                    ["develop_nonew_num"] = 0,
                    ["fail_num"] = 0,
                    ["first_sangvis"] = 0,
                    ["equip_type_produce_num"] = 0,
                    ["equip_type_produce_recover_time"] = 0,
                    ["equip_type_spproduce_num"] = 0,
                    ["equip_type_spproduce_recover_time"] = 0,
                    ["permanent_pool"] = "",
                    ["limited_pool"] = "",
                    ["function_unlock"] = "",
                    ["career_update"] = 3030,
                    ["vehicle_update"] = 3030,
                    ["achievement_update"] = 3070,
                    ["fairy_building_resource"] = 0,
                    ["sangvis_duplicate_award_k"] = "",
                    ["last_energy_recover_time"] = 0,
                    ["career_quest_version"] = "0"
                },
                ["user_adjutant_multi"] = BuildUserAdjutantMulti(player.AdjutantMulti),
                ["equip_with_user_info"] = player.Equips.Select(e => EquipJson(player, e)).ToList(),
                ["fairy_with_user_info"] = player.Fairies.Select(f => FairyJson(player, f)).ToList(),
                ["develop_equip_act_info"] = player.EquipDevelopSlots.Select(s => new Dictionary<string, object>
                {
                    ["build_slot"] = s.Slot.ToString(), ["equip_id"] = s.EquipId.ToString(), ["fairy_id"] = s.FairyId.ToString(),
                    ["start_time"] = s.StartTime.ToString(), ["duration"] = s.Duration.ToString(), ["user_id"] = player.Id.ToString()
                }).ToList(),
                ["develop_fairy_act_info"] = new List<object>(),
                ["team_in_coin_mission_info"] = new Dictionary<string, object>(),
                ["skin_with_user_info"] = player.Skins
                    .Distinct()
                    .ToDictionary(
                        sid => sid.ToString(),
                        sid => (object)new Dictionary<string, object>
                        {
                            ["skin_id"] = sid.ToString(),
                            ["num"] = "1",
                            ["user_id"] = player.Id.ToString()
                        }),
                ["event_info"] = new Dictionary<string, object>(),
                ["dorm_score_info"] = new Dictionary<string, object>
                {
                    ["1"] = new Dictionary<string, object> { ["user_id"] = player.Id.ToString(), ["dorm_id"] = "1", ["score"] = "3200" },
                    ["2"] = new Dictionary<string, object> { ["user_id"] = player.Id.ToString(), ["dorm_id"] = "2", ["score"] = "3200" }
                },
                ["furniture_collect_info"] = new List<object>(),
                ["fairy_collect_info"] = new List<object>(),
                ["trial_with_user_info"] = null,
                ["trial_exercise_info"] = null,
                ["gun_preset_info"] = new List<object>(),
                ["outhouse_establish_info"] = new List<object>(),
                ["dorm_rest_friend_build_coin_count"] = 10,
                ["tips_info"] = null,
                ["fairy_skin_with_user_info"] = new List<object>(),
                ["user_game_setting"] = string.IsNullOrEmpty(player.GameSetting) ? null : new Dictionary<string, object>
                {
                    // The client parses "settings" itself as a JSON string; sending the settings as a nested
                    // object (previous behaviour) made it read an empty string and fail the whole login.
                    ["user_id"] = player.Id.ToString(),
                    ["settings"] = player.GameSetting
                },
                ["mission_key_with_user_info"] = new List<object>(),
                ["squad_data_daily"] = new List<object>(),
                ["squad_with_user_info"] = new List<object>(),
                ["squad_unlock_with_user_info"] = new List<object>(),
                ["chip_with_user_info"] = new List<object>(),
                ["squad_train_act_info"] = new List<object>(),
                ["squad_skill_act_info"] = new List<object>(),
                ["squad_fix_act_info"] = new List<object>(),
                ["surplus_datacell_info"] = new Dictionary<string, object> { ["user_id"] = player.Id, ["base"] = 0, ["senior"] = 0 },
                ["gun_memoir_list"] = new List<object>(),
                ["explore_info"] = new Dictionary<string, object>
                {
                    ["list"] = new List<object>(),
                    ["next_time"] = 0,
                    ["ex_prize"] = new Dictionary<string, object>
                    {
                        ["user_exp"] = 0, ["mp"] = 0, ["ammo"] = 0, ["mre"] = 0, ["part"] = 0, ["gem"] = 0, ["core"] = 0,
                        ["gun_id"] = new List<object>(), ["item_ids"] = "", ["gift"] = "", ["furniture"] = new List<object>(),
                        ["equip_ids"] = new List<object>(), ["coins"] = "", ["skin"] = "", ["bp_pay"] = 0,
                        ["fairy_ids"] = new List<object>(), ["chip"] = new List<object>()
                    },
                    ["is_auto"] = 0,
                    ["pets"] = new List<object>(),
                    ["items_taken"] = new List<object>(),
                    ["next_explore_time"] = 0,
                    ["end_time"] = 0,
                    ["explore_time_type"] = 1,
                    ["mp"] = player.Mp.ToString(),
                    ["ammo"] = player.Ammo.ToString(),
                    ["mre"] = player.Mre.ToString(),
                    ["part"] = player.Part.ToString()
                },
                ["theater_exercise_info"] = new Dictionary<string, object> { ["theater_construction_info"] = new List<object>() },
                ["theater_with_user_info"] = new List<object>(),
                ["commander_with_user_info"] = new Dictionary<string, object>
                {
                    ["user_id"] = player.Id,
                    ["commander_emoji"] = "1",
                    ["commander_title"] = 0,
                    ["dressed"] = player.Extra.TryGetValue("commander_dressed", out var dr) && !string.IsNullOrEmpty(dr) ? dr : "1010:,1020:,1030:,1040:,1050:",
                    ["preset1"] = "", ["preset2"] = "", ["preset3"] = "", ["preset4"] = "",
                    ["fail_num"] = 0,
                    ["is_init_effect"] = "1",
                    ["gender"] = string.IsNullOrEmpty(player.Gender) ? "0" : player.Gender
                },
                ["commander_uniform_info"] = new[] { "1010", "1020", "1030", "1040", "1050" }.Select(uid => new Dictionary<string, object>
                {
                    ["user_id"] = player.Id,
                    ["commander_uniform_id"] = uid,
                    ["color_get"] = new List<object>(),
                    ["get_color_num"] = 0,
                    ["create_time"] = now
                }).ToList(),
                ["sangvis_with_user_info"] = new List<object>(),
                ["sangvis_chip_with_user_info"] = new List<object>(),
                ["sangvis_team_with_user_info"] = new List<object>(),
                ["sangvis_capture_collect_info"] = new List<object>(),
                ["fetter_with_user"] = new List<object>(),
                ["fetter_story_with_user"] = new List<object>(),
                ["fetter_bounty_with_user"] = new List<object>(),
                ["org_bounty_with_user"] = new List<object>(),
                ["targettrain_collect_user_info"] = new List<object>(),
                ["mission_guide_info"] = new List<object>(),
                ["wish_gun_with_user_info"] = new List<object>(),
                ["event_rank_with_user"] = new List<object>(),
                ["org_department_info"] = new List<object>(),
                ["returnback_event_info"] = new Dictionary<string, object> { ["return_back_event"] = 0, ["invite_event"] = 0 },
                ["daily_status_with_user_info"] = null,
                ["mall_skin_gift_with_user_info"] = new List<object>(),
                ["vehicle_with_user_info"] = new List<object>(),
                ["vehicle_component_with_user_info"] = new List<object>(),
                ["vehicle_component_collect_info"] = new List<object>(),
                ["vehicle_preset_info"] = new List<object>(),
                ["automission_presets_info"] = null,
                ["breakout_phase_with_user_info"] = null,
                ["friend_with_user_info"] = new List<object>(),
                ["user_friend_info"] = new Dictionary<string, object>
                {
                    ["f_userid"] = player.Id,
                    ["name"] = string.IsNullOrEmpty(player.Name) ? "Commander" : player.Name,
                    ["lv"] = player.Level.ToString(),
                    ["medal_is_visible"] = "1",
                    ["medal_with_user"] = new List<object>(),
                    ["card_id"] = "0", ["headpic_id"] = "0", ["frame_id"] = "0", ["homebg_id"] = "0",
                    ["intro"] = player.Intro ?? "",
                    ["team_info"] = new List<object>(),
                    ["sangvis_info"] = new List<object>(),
                    ["borrow_team_today"] = "0",
                    ["fairy_info"] = new List<object>(),
                    ["vehicle_id"] = "0",
                    ["is_return_user"] = 0
                },
                ["gift_with_user_info"] = player.Gifts.Select(kv => new Dictionary<string, object>
                {
                    ["item_id"] = kv.Key,
                    ["number"] = kv.Value.ToString()
                }).ToList(),
                ["medal_with_user_info"] = new List<object>(),
                ["kalina_with_user_info"] = new Dictionary<string, object>
                {
                    // same fields and types as the official capture (level/favor/skin are strings there)
                    ["user_id"] = player.Id.ToString(),
                    ["click_num"] = int.TryParse(player.Extra.GetValueOrDefault("kalina_click"), out int kc) ? kc : 0,
                    ["click_time"] = now,
                    ["level"] = (int.TryParse(player.Extra.GetValueOrDefault("kalina_level"), out int kl) ? kl : 0).ToString(),
                    ["favor"] = (int.TryParse(player.Extra.GetValueOrDefault("kalina_favor"), out int kf) ? kf : 0).ToString(),
                    ["last_favor"] = "0",
                    ["skin"] = (int.TryParse(player.Extra.GetValueOrDefault("kalina_skin"), out int ks) ? ks : 0).ToString(),
                    ["send_mail_time"] = "0"
                },
                ["share_with_user_info"] = new Dictionary<string, object> { ["last_time"] = 0 },
                ["auto_mission_act_info"] = AutoMissionActInfo(player),
                ["gun_with_user_info"] = IndexGuns(player).Select(g => new Dictionary<string, object>
                {
                    ["id"] = g.Id.ToString(),
                    ["user_id"] = player.Id.ToString(),
                    ["gun_id"] = g.GunId.ToString(),
                    ["gun_exp"] = TotalGunExp(g).ToString(), // client derives the level from cumulative exp
                    ["gun_level"] = g.GunLevel.ToString(),
                    ["team_id"] = g.TeamId.ToString(),
                    ["if_modification"] = g.IfModification.ToString(),
                    ["location"] = g.Location.ToString(),
                    ["position"] = g.Position.ToString(),
                    ["life"] = Math.Min(g.Life, MaxLife(g)).ToString(),
                    ["ammo"] = g.GunAmmo.ToString(),
                    ["mre"] = g.GunMre.ToString(),
                    ["pow"] = g.Pow.ToString(),
                    ["hit"] = g.Hit.ToString(),
                    ["dodge"] = g.Dodge.ToString(),
                    ["rate"] = g.Rate.ToString(),
                    ["skill1"] = g.Skill1.ToString(),
                    ["skill2"] = g.Skill2.ToString(),
                    ["fix_end_time"] = g.FixEndTime.ToString(),
                    ["is_locked"] = g.IsLocked.ToString(),
                    ["number"] = g.Number.ToString(),
                    ["equip1"] = g.Equip1Id.ToString(),
                    ["equip2"] = g.Equip2Id.ToString(),
                    ["equip3"] = g.Equip3Id.ToString(),
                    ["equip4"] = g.Equip4Id.ToString(),
                    ["favor"] = g.Favor.ToString(),
                    ["max_favor"] = g.MaxFavor.ToString(),
                    ["favor_toplimit"] = g.FavorTopLimit.ToString(),
                    ["soul_bond"] = g.SoulBond.ToString(),
                    ["skin"] = g.Skin.ToString(),
                    ["can_click"] = g.CanClick.ToString(),
                    ["soul_bond_time"] = g.SoulBondTime.ToString(),
                    ["special_effect"] = g.SpecialEffect.ToString(),
                    ["is_collect"] = g.IsCollect.ToString()
                }).ToList(),
                ["client_file_upload_info"] = null,
                ["show_cdkey"] = 1,
                ["can_use_payceo"] = 0,
                ["ios_ads"] = 1,
                ["mission_event_info"] = new Dictionary<string, object>(),
                ["authentication_url"] = "",
                ["naive_build_gun_formula"] = "522:320:404:137",
                ["game_config_info"] = new Dictionary<string, object>
                {
                    ["share_global_switch"] = "1",
                    ["special_develop_switch"] = "1",
                    ["equip_enhance_switch"] = "1",
                    ["equip_rectify_switch"] = "1",
                    ["trail_switch"] = "1",
                    ["soulbind_global_switch"] = "1",
                    ["client_version"] = "GF_3010",
                    ["res_table_switch"] = "0",
                    ["static_gun_md5"] = "7b74fb977df846d8c1086fe81a9f466776ad4f39287a72131ec7e52f2db68875"
                },
                ["is_obsolete_server"] = false
            };

            ApplyIndexTemplate(response, player);
            return EncryptedResponse(response, player.SignKey, compress: true);
        }

        private async Task<IActionResult> HandleCrashLog(PlayerAccount? player, JObject? body)
        {
            string formInfo = "";
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                formInfo = string.Join(", ", form.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            LogToFile($"[CLIENT CRASHLOG] Player={player?.Id} Body={body?.ToString(Formatting.None)} Form={formInfo}");
            return Content("1", "text/plain; charset=utf-8");
        }

        private static List<JObject>? _attendanceCache;

        private static long ToPstUnix(string? dtStr, TimeZoneInfo pst)
        {
            if (DateTime.TryParse(dtStr, out var dt))
            {
                var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                return new DateTimeOffset(unspecified, pst.GetUtcOffset(unspecified)).ToUnixTimeSeconds();
            }
            return 0;
        }

        private static List<JObject>? _sevenAttendanceCache;

        private static List<Dictionary<string, object>> BuildMissionWithUserInfo(PlayerAccount player)
        {
            var allMissions = Tables.Get("mission").Values
                // simulations (capsule/data/exp drills: missions 1101-1503) are duplicate_type 1/2/3/5 and must be
                // included, otherwise their difficulties never unlock
                .Where(r => r.Int("endless_mode") == 0 && r.Int("is_hide") == 0)
                .ToList();

            // What the official server opens for a brand-new account (captured): first story missions, the first
            // difficulty of each simulation and the first mission of each campaign/event. The rest opens by playing.
            var openIds = new HashSet<int>(DefaultOpenMissions()) { 1, 5 };
            foreach (var rec in player.Missions.Values)
            {
                openIds.Add(rec.MissionId);
                bool cleared = rec.WinCounter > 0;
                if (!cleared) continue;

                var row = Tables.Row("mission", rec.MissionId);
                if (row == null) continue;
                int c = row.Int("campaign");
                int sub = row.Int("sub");
                int emerg = row.Int("if_emergency");

                var nextInChapter = allMissions.FirstOrDefault(r =>
                    r.Int("campaign") == c && r.Int("if_emergency") == emerg && r.Int("sub") == sub + 1);
                if (nextInChapter != null)
                {
                    openIds.Add(nextInChapter.Int("id"));
                }
                else if (emerg == 0 && c >= 0)
                {
                    // Chapter normal cleared: unlock next chapter normal + current chapter emergency & night
                    var nextChap = allMissions.FirstOrDefault(r => r.Int("campaign") == c + 1 && r.Int("if_emergency") == 0 && r.Int("sub") == 1);
                    if (nextChap != null) openIds.Add(nextChap.Int("id"));
                    var emergChap = allMissions.FirstOrDefault(r => r.Int("campaign") == c && r.Int("if_emergency") == 1 && r.Int("sub") == 1);
                    if (emergChap != null) openIds.Add(emergChap.Int("id"));
                    var nightChap = allMissions.FirstOrDefault(r => r.Int("campaign") == c && r.Int("if_emergency") == 3 && r.Int("sub") == 1);
                    if (nightChap != null) openIds.Add(nightChap.Int("id"));
                }
            }

            var result = new List<Dictionary<string, object>>();
            foreach (int mid in openIds.OrderBy(x => x))
            {
                player.Missions.TryGetValue(mid.ToString(), out var rec);
                int winCounter = rec != null ? (rec.WinCounter > 0 ? rec.WinCounter : (rec.BestRank > 0 ? 1 : 0)) : 0;
                int counter = rec != null ? Math.Max(rec.Counter, winCounter) : 0;
                int bestRank = rec?.BestRank ?? 0;
                if (bestRank <= 0) bestRank = 1;
                int medal4 = rec?.Medal4 ?? 0;

                result.Add(new Dictionary<string, object>
                {
                    ["id"] = mid.ToString(),
                    ["user_id"] = player.Id.ToString(),
                    ["mission_id"] = mid.ToString(),
                    ["medal1"] = winCounter > 0 ? "1" : "0",
                    ["medal2"] = "0",
                    ["bestrank"] = bestRank.ToString(),
                    ["medal4"] = medal4.ToString(),
                    ["counter"] = counter.ToString(),
                    ["win_counter"] = winCounter.ToString(),
                    ["shortest_in_coinmission"] = "0.00",
                    ["type5_score"] = "0",
                    ["is_open"] = "1",
                    ["is_drop_draw_event"] = "0",
                    ["is_close"] = "0",
                    ["cycle_win_count"] = "0",
                    ["mapped_win_counter"] = "0",
                    ["drop_item_count"] = "",
                    ["intensify_item"] = ""
                });
            }
            return result;
        }

        private static Dictionary<string, object>? _cachedMallList;

        private static Dictionary<string, object> BuildMallStaticTablesPayload(PlayerAccount? player)
        {
            if (_cachedMallList == null)
            {
                var dict = new Dictionary<string, object>();
                int idx = 0;
                foreach (var kv in Tables.Get("mall").OrderBy(x => x.Key))
                {
                    var r = kv.Value;
                    var entry = new Dictionary<string, object>
                    {
                        ["id"] = r["id"]?.ToString() ?? kv.Key.ToString(),
                        ["type"] = r["type"]?.ToString() ?? "0",
                        ["sort"] = r["sort"]?.ToString() ?? "0",
                        ["item_ids"] = r["item_ids"]?.ToString() ?? "0",
                        ["gem"] = r["gem"]?.ToString() ?? "0",
                        ["mp"] = r["mp"]?.ToString() ?? "0",
                        ["ammo"] = r["ammo"]?.ToString() ?? "0",
                        ["mre"] = r["mre"]?.ToString() ?? "0",
                        ["part"] = r["part"]?.ToString() ?? "0",
                        ["bp_pay"] = r["bp_pay"]?.ToString() ?? "0",
                        ["skin"] = r["skin"]?.ToString() ?? "0",
                        ["gift"] = r["gift"]?.ToString() ?? "",
                        ["gemprice"] = r["gemprice"]?.ToString() ?? "0",
                        ["original_price"] = r["original_price"]?.ToString() ?? "0",
                        ["pointprice"] = r["pointprice"]?.ToString() ?? "0",
                        ["if_cheap"] = r["if_cheap"]?.ToString() ?? "0",
                        ["quota"] = r["quota"]?.ToString() ?? "0",
                        ["giftbag_name"] = r["giftbag_name"]?.ToString() ?? "",
                        ["event_code"] = r["event_code"]?.ToString() ?? "0",
                        ["icon"] = r["icon"]?.ToString() ?? "",
                        ["daily_quota"] = r["daily_quota"]?.ToString() ?? "0",
                        ["tips"] = r["tips"]?.ToString() ?? "",
                        ["discount"] = r.Dbl("discount", 1.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                        ["sale_starttime"] = r["sale_starttime"]?.ToString() ?? "0",
                        ["sale_endtime"] = r["sale_endtime"]?.ToString() ?? "0",
                        ["label"] = r["label"]?.ToString() ?? "0",
                        ["prize_id"] = r["prize_id"]?.ToString() ?? "0",
                        ["pay_type"] = r["pay_type"]?.ToString() ?? "0",
                        ["itemprice"] = r["itemprice"]?.ToString() ?? "0",
                        ["on_sale_days"] = r["on_sale_days"]?.ToString() ?? "0",
                        ["price_up"] = r["price_up"]?.ToString() ?? "0",
                        ["classification_id"] = r["classification_id"]?.ToString() ?? "0",
                        ["description"] = r["description"]?.ToString() ?? "",
                        ["special_daily_quota"] = "0",
                        ["increasing_price"] = r["increasing_price"]?.ToString() ?? "",
                        ["end_time"] = -1,
                        ["rest_num"] = -1
                    };
                    dict[(idx++).ToString()] = entry;
                }
                _cachedMallList = dict;
            }

            return new Dictionary<string, object>
            {
                ["mall_list"] = _cachedMallList,
                ["increase_gem_order"] = new Dictionary<string, object>(),
                ["max_cost_list"] = new Dictionary<string, object>(),
                ["max_limit"] = new Dictionary<string, object>(),
                ["pointmall_quota"] = new Dictionary<string, object>(),
                ["pointmall_type23_quota"] = new Dictionary<string, object>(),
                ["battlepass_pt_item"] = new Dictionary<string, object>(),
                ["mall_score"] = 0,
                ["monthlycard1_end_time"] = 0,
                ["monthlycard2_end_time"] = 0,
                ["monthlycard3_end_time"] = 0,
                ["gem_mall_ids"] = ""
            };
        }

        private static object BuildBattlepassIndex(PlayerAccount? player)
        {
            string uid = (player?.Id ?? 0).ToString();
            return new
            {
                battlepass_with_user = new[]
                {
                    new
                    {
                        id = "1",
                        user_id = uid,
                        battlepass_id = "42",
                        free_bonus = player?.Extra.GetValueOrDefault("bp_42_1", "") ?? "",
                        paid_bonus = player?.Extra.GetValueOrDefault("bp_42_2", "") ?? "",
                        extra_bonus = player?.Extra.GetValueOrDefault("bp_42_3", "") ?? "",
                        is_expired = "0",
                        start_time = "1794211200"
                    }
                },
                battlepass_pt_item = new Dictionary<string, int>
                {
                    ["10606"] = player?.Items.GetValueOrDefault("10606", 0) ?? 0,
                    ["140083"] = player?.Items.GetValueOrDefault("140083", 0) ?? 0,
                    ["140084"] = player?.Items.GetValueOrDefault("140084", 0) ?? 0
                }
            };
        }

        private static List<object> BuildExploreMallList()
        {
            return Tables.Get("explore_mall").Keys.OrderBy(k => k).Select(id => (object)new
            {
                id,
                start_time = 0,
                end_time = 0,
                n_time = 0,
                num = 0
            }).ToList();
        }

        private IActionResult HandleKalinaFavorUpLevel(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (player.Extra.GetValueOrDefault("kalina_last_date") != today)
            {
                player.Extra["kalina_click"] = "0";
                player.Extra["kalina_last_date"] = today;
            }
            int click = int.TryParse(player.Extra.GetValueOrDefault("kalina_click"), out int c) ? c : 0;
            if (click < 5)
            {
                click++;
                int favor = (int.TryParse(player.Extra.GetValueOrDefault("kalina_favor"), out int f) ? f : 0) + 10;
                int level = Math.Max(1, favor / 100 + 1);
                player.Extra["kalina_click"] = click.ToString();
                player.Extra["kalina_favor"] = favor.ToString();
                player.Extra["kalina_level"] = level.ToString();
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private static int ComputeMissionCampaign(PlayerAccount player)
        {
            int maxCampaign = 1;
            var allMissions = Tables.Get("mission").Values
                .Where(r => r.Int("duplicate_type") == 0 && r.Int("if_emergency") == 0 && r.Int("endless_mode") == 0 && r.Int("is_hide") == 0)
                .ToList();

            var maxSubByCampaign = allMissions
                .GroupBy(r => r.Int("campaign"))
                .ToDictionary(g => g.Key, g => g.Max(r => r.Int("sub")));

            foreach (var rec in player.Missions.Values)
            {
                var row = Tables.Row("mission", rec.MissionId);
                if (row == null) continue;
                if (row.Int("duplicate_type") != 0 || row.Int("if_emergency") != 0 || row.Int("endless_mode") != 0) continue;
                int c = row.Int("campaign");
                int sub = row.Int("sub");
                if (c >= 1 && maxSubByCampaign.TryGetValue(c, out int maxSub) && sub >= maxSub && c + 1 > maxCampaign)
                    maxCampaign = c + 1;
            }
            return maxCampaign;
        }

        private static string GetAttendanceDay(PlayerAccount player) =>
            int.TryParse(player.Extra.GetValueOrDefault("att_count"), out int d) && d > 0 ? d.ToString() : "0";

        private static long GetAttendanceNextTime(PlayerAccount player)
        {
            var pst = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var nowPst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pst);
            if (player.Extra.GetValueOrDefault("att_last_day") != nowPst.ToString("yyyyMMdd"))
                return 0;
            var tomorrowMidnight = nowPst.Date.AddDays(1);
            return new DateTimeOffset(tomorrowMidnight, pst.GetUtcOffset(tomorrowMidnight)).ToUnixTimeSeconds();
        }

        private long GetSevenStartTime(PlayerAccount player)
        {
            if (long.TryParse(player.Extra.GetValueOrDefault("seven_start_time"), out long ts) && ts > 0)
                return ts;
            var pst = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var nowPst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pst);
            var todayMidnight = nowPst.Date;
            ts = new DateTimeOffset(todayMidnight, pst.GetUtcOffset(todayMidnight)).ToUnixTimeSeconds();
            player.Extra["seven_start_time"] = ts.ToString();
            _db.SavePlayer(player);
            return ts;
        }

        /// <summary>Seven-day check-in table: type 1 is the newcomer track (rewards on all 7 days) used by accounts
        /// created with the official start; older accounts keep type 3, as captured from the official server.</summary>
        private static int SevenType(PlayerAccount player) => player.Extra.ContainsKey("official_start") ? 1 : 3;

        private static string GetSevenAttendanceDays(PlayerAccount player)
        {
            int count = int.TryParse(player.Extra.GetValueOrDefault("seven_att_count"), out int c) ? Math.Clamp(c, 0, 7) : 0;
            return count > 0 ? string.Join(",", Enumerable.Range(1, count)) : "";
        }

        private IActionResult HandleSevenAttendance(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            var pst = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            string today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pst).ToString("yyyyMMdd");
            if (player.Extra.GetValueOrDefault("seven_att_last_day") != today)
            {
                if (_sevenAttendanceCache == null)
                {
                    string f = Path.Combine(Tables.DataPath, "catchdata", "seven_attendance_info.json");
                    _sevenAttendanceCache = System.IO.File.Exists(f)
                        ? JArray.Parse(System.IO.File.ReadAllText(f)).OfType<JObject>().ToList()
                        : new List<JObject>();
                }
                int days = int.TryParse(player.Extra.GetValueOrDefault("seven_att_count"), out int d) ? d : 0;
                if (days < 7)
                {
                    days++;
                    var rewardRow = _sevenAttendanceCache.FirstOrDefault(r =>
                        r["type"]?.ToString() == SevenType(player).ToString() && r["day"]?.ToString() == days.ToString());
                    if (rewardRow != null) GrantRow(player, rewardRow);
                }
                player.Extra["seven_att_count"] = days.ToString();
                player.Extra["seven_att_last_day"] = today;
                _db.SavePlayer(player);
            }
            int count = Math.Clamp(int.TryParse(player.Extra.GetValueOrDefault("seven_att_count"), out int c) ? c : 1, 1, 7);
            string list = string.Join(",", Enumerable.Range(1, count));
            return EncryptedResponse(new { seven_attendant = list }, player.SignKey);
        }

        private IActionResult HandleAttendance(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            var pst = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var nowPst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, pst);
            string monthPrefix = nowPst.ToString("yyyy-MM");
            string today = nowPst.ToString("yyyyMMdd");

            if (_attendanceCache == null)
            {
                string f = Path.Combine(Tables.DataPath, "catchdata", "attendance_info.json");
                _attendanceCache = System.IO.File.Exists(f)
                    ? JArray.Parse(System.IO.File.ReadAllText(f)).OfType<JObject>().ToList()
                    : new List<JObject>();
            }

            var monthRows = _attendanceCache.Where(r => (r["start_time"]?.ToString() ?? "").StartsWith(monthPrefix)).ToList();
            if (monthRows.Count == 0 && _attendanceCache.Count > 0)
            {
                string fallbackMonth = (_attendanceCache.Last()["start_time"]?.ToString() ?? "");
                if (fallbackMonth.Length >= 7) fallbackMonth = fallbackMonth[..7];
                monthRows = _attendanceCache.Where(r => (r["start_time"]?.ToString() ?? "").StartsWith(fallbackMonth)).ToList();
            }

            var type1 = new List<Dictionary<string, object>>();
            var type2 = new List<Dictionary<string, object>>();
            foreach (var r in monthRows)
            {
                var entry = new Dictionary<string, object>
                {
                    ["day"] = r["day"]?.ToString() ?? "1",
                    ["start_time"] = ToPstUnix(r["start_time"]?.ToString(), pst),
                    ["end_time"] = ToPstUnix(r["end_time"]?.ToString(), pst),
                    ["attendance_type"] = r["attendance_type"]?.ToString() ?? "1",
                    ["mp"] = r["mp"]?.ToString() ?? "0",
                    ["ammo"] = r["ammo"]?.ToString() ?? "0",
                    ["mre"] = r["mre"]?.ToString() ?? "0",
                    ["part"] = r["part"]?.ToString() ?? "0",
                    ["gem"] = r["gem"]?.ToString() ?? "0",
                    ["core"] = r["core"]?.ToString() ?? "0",
                    ["gun_id"] = r["gun_id"]?.ToString() ?? "0",
                    ["item_ids"] = r["item_ids"]?.ToString() ?? "0",
                    ["gift"] = r["gift"]?.ToString() ?? "",
                    ["prize_id"] = r["prize_id"]?.ToString() ?? "0"
                };
                if ((r["attendance_type"]?.ToString() ?? "1") == "2") type2.Add(entry);
                else type1.Add(entry);
            }

            if (player.Extra.GetValueOrDefault("att_last_day") != today)
            {
                if (player.Extra.GetValueOrDefault("att_month") != monthPrefix)
                {
                    player.Extra["att_month"] = monthPrefix;
                    player.Extra["att_count"] = "0";
                }
                int dayNum = (int.TryParse(player.Extra.GetValueOrDefault("att_count"), out int ac) ? ac : 0) + 1;
                player.Extra["att_count"] = dayNum.ToString();
                player.Extra["att_last_day"] = today;
                var rewardRow = monthRows.FirstOrDefault(r => r["day"]?.ToString() == dayNum.ToString() && (r["attendance_type"]?.ToString() ?? "1") == "1");
                if (rewardRow != null) GrantRow(player, rewardRow);
                _db.SavePlayer(player);
            }

            return EncryptedResponse(new
            {
                attendance_type1 = type1,
                attendance_type2 = type2
            }, player.SignKey);
        }

        private IActionResult HandleQuest(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            RollPeriods(player);
            long end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400 * 7;
            string uid = player.Id.ToString();
            int gunCount = player.Guns.Select(g => g.GunId).Distinct().Count();
            int skinCount = player.Skins.Distinct().Count();
            string claimedAchievements = string.Join(",", player.Extra.Keys.Where(k => k.StartsWith("claim_achievement_")).Select(k => k[18..]));

            return EncryptedResponse(new
            {
                weekly = new
                {
                    id = uid,
                    user_id = uid,
                    refresh_count = "",
                    weekly_quest_list = "1,2,3,4,5,6,7,8,9,10,11",
                    accomplish_quest_list = string.Join(",", player.Extra.Keys.Where(k => k.StartsWith("claim_weekly_")).Select(k => k[13..])),
                    locked_quest_list = "11",
                    end_time = end.ToString(),
                    progress = new Dictionary<string, object>
                    {
                        ["fix"] = Counter(player, "w", "fix").ToString(), ["win_robot"] = Counter(player, "w", "win_robot").ToString(), ["win_person"] = Counter(player, "w", "win_person").ToString(), ["win_boss"] = Counter(player, "w", "win_boss").ToString(),
                        ["win_armorrobot"] = Counter(player, "w", "win_armorrobot").ToString(), ["win_armorperson"] = Counter(player, "w", "win_armorperson").ToString(), ["operation"] = Counter(player, "w", "operation").ToString(), ["s_win"] = Counter(player, "w", "s_win").ToString(),
                        ["eat"] = Counter(player, "w", "eat").ToString(), ["develop_gun"] = Counter(player, "w", "develop_gun").ToString(), ["upgrade"] = Counter(player, "w", "upgrade").ToString(), ["coin_mission"] = Counter(player, "w", "coin_mission").ToString(),
                        ["develop_equip"] = Counter(player, "w", "develop_equip").ToString(), ["special_develop_gun"] = Counter(player, "w", "special_develop_gun").ToString(), ["adjust_equip"] = Counter(player, "w", "adjust_equip").ToString(), ["eat_equip"] = Counter(player, "w", "eat_equip").ToString(),
                        ["special_develop_equip"] = Counter(player, "w", "special_develop_equip").ToString(), ["adjust_fairy"] = Counter(player, "w", "adjust_fairy").ToString(), ["eat_fairy"] = Counter(player, "w", "eat_fairy").ToString(), ["squad_data_analyse"] = Counter(player, "w", "squad_data_analyse").ToString(),
                        ["squad_eat_chip"] = Counter(player, "w", "squad_eat_chip").ToString(), ["win_normal"] = Counter(player, "w", "win_normal").ToString(), ["win_armor"] = Counter(player, "w", "win_armor").ToString(), ["complete_squad_daily"] = Counter(player, "w", "complete_squad_daily").ToString(),
                        ["retire_gun"] = Counter(player, "w", "retire_gun").ToString(), ["explore"] = Counter(player, "w", "explore").ToString(), ["sangvis_daily_retrieval"] = Counter(player, "w", "sangvis_daily_retrieval").ToString(), ["sangvis_exercise"] = Counter(player, "w", "sangvis_exercise").ToString(),
                        ["consume_aid"] = Counter(player, "w", "consume_aid").ToString(), ["procurement"] = Counter(player, "w", "procurement").ToString(), ["win_enemy"] = Counter(player, "w", "win_enemy").ToString(), ["chess"] = Counter(player, "w", "chess").ToString(),
                        ["sp_develop"] = Counter(player, "w", "sp_develop").ToString(), ["upgrade_skill"] = Counter(player, "w", "upgrade_skill").ToString(), ["auto_battle"] = Counter(player, "w", "auto_battle").ToString(), ["battery_collect"] = Counter(player, "w", "battery_collect").ToString(),
                        ["map_capture"] = Counter(player, "w", "map_capture").ToString(), ["daily_complete"] = Counter(player, "w", "daily_complete").ToString(), ["develop_all"] = Counter(player, "w", "develop_all").ToString(), ["combat_simulation"] = Counter(player, "w", "combat_simulation").ToString(),
                        ["weekly_complete"] = Counter(player, "w", "weekly_complete").ToString()
                    },
                    weekly_accumulation = 0
                },
                career = new Dictionary<string, object>
                {
                    ["user_id"] = uid,
                    ["c_tutorial_mission"] = "",
                    ["complete_normal_mission"] = player.Missions.Count,
                    ["vehicle_kill"] = Counter(player, "a", "vehicle_kill"),
                    ["c_gun_level"] = "",
                    ["develop_gun"] = Math.Max(1, Counter(player, "a", "develop_gun")),
                    ["combine_gun"] = Counter(player, "a", "combine_gun").ToString(),
                    ["retire_gun"] = Counter(player, "a", "retire_gun").ToString(),
                    ["develop_equip"] = Counter(player, "a", "develop_equip").ToString(),
                    ["eat_gun"] = Counter(player, "a", "eat").ToString(),
                    ["eat_equip"] = Counter(player, "a", "eat_equip").ToString(),
                    ["upgrade"] = Counter(player, "a", "upgrade").ToString(),
                    ["fix_gun"] = Counter(player, "a", "fix").ToString(),
                    ["operation"] = Counter(player, "a", "operation").ToString(),
                    ["different_skin"] = skinCount.ToString(),
                    ["different_fairy"] = player.Fairies.Select(f => f.FairyId).Distinct().Count().ToString(),
                    ["favor100_gun"] = player.Guns.Count(g => g.Favor >= 1000000).ToString(),
                    ["soul_bond_gun"] = player.Guns.Count(g => g.SoulBond > 0).ToString()
                },
                career_quest_grade = new List<object>(),
                achievement = new Dictionary<string, object>
                {
                    ["id"] = uid,
                    ["user_id"] = uid,
                    ["different_gun"] = gunCount.ToString(),
                    ["different_skin"] = skinCount.ToString(),
                    ["mission_win"] = player.Missions.Count.ToString(),
                    ["develop_gun"] = Counter(player, "a", "develop_gun").ToString(),
                    ["combine_gun"] = Counter(player, "a", "combine_gun").ToString(),
                    ["eat_gun"] = Counter(player, "a", "eat").ToString(),
                    ["retire_gun"] = Counter(player, "a", "retire_gun").ToString(),
                    ["upgrade"] = Counter(player, "a", "upgrade").ToString(),
                    ["fix_gun"] = Counter(player, "a", "fix").ToString(),
                    ["operation"] = Counter(player, "a", "operation").ToString(),
                    ["accomplish_achievement_ids"] = claimedAchievements
                },
                chess_game_counts = new Dictionary<string, object>
                {
                    ["free"] = Counter(player, "w", "free").ToString(),
                    ["match"] = Counter(player, "w", "match").ToString()
                },
                cheers_counts = 0
            }, player.SignKey);
        }

        private IActionResult HandleHome(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return EncryptedResponse(new
            {
                recover_mp = 0,
                recover_ammo = 0,
                recover_mre = 0,
                recover_part = 0,
                gem = player.Gem,
                all_favorup_gun = new List<object>(),
                all_favorup_sangvis = new List<object>(),
                last_favor_recover_time = now,
                recover_ssoc = 0,
                last_ssoc_change_time = 0,
                server_time = now
            }, player.SignKey);
        }

        private IActionResult HandleDevelopGun(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            int slot = body?.Value<int>("build_slot") ?? 1;
            int mp = body?.Value<int>("mp") ?? 400;
            int ammo = body?.Value<int>("ammo") ?? 400;
            int mre = body?.Value<int>("mre") ?? 400;
            int part = body?.Value<int>("part") ?? 400;

            player.Mp = Math.Max(0, player.Mp - mp);
            player.Ammo = Math.Max(0, player.Ammo - ammo);
            player.Mre = Math.Max(0, player.Mre - mre);
            player.Part = Math.Max(0, player.Part - part);

            if (player.Items.ContainsKey("1") && player.Items["1"] > 0)
                player.Items["1"]--;

            var rolledGun = MasterDataManager.Instance.RollGFLGacha(mp, ammo, mre, part);
            int gunId = rolledGun?.Id ?? 55;
            int duration = rolledGun?.DevelopDuration ?? 3600;

            player.DevelopSlots.RemoveAll(x => x.Slot == slot);
            player.DevelopSlots.Add(new DevelopSlot
            {
                Slot = slot,
                GunId = gunId,
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Duration = duration,
                Mp = mp,
                Ammo = ammo,
                Mre = mre,
                Part = part
            });

            _db.SavePlayer(player);
            Console.WriteLine($"[Factory] Player {player.Id} crafted Gun ID: {gunId} ({rolledGun?.EnName}) in slot {slot}, duration: {duration}s");

            var res = new
            {
                gun_id = gunId,
                develop_act_info = player.DevelopSlots
            };
            return EncryptedResponse(res, player.SignKey);
        }

        private IActionResult HandleFinishDevelop(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            int slot = body?.Value<int>("build_slot") ?? 1;
            var developSlot = player.DevelopSlots.FirstOrDefault(x => x.Slot == slot);
            if (developSlot == null)
                return BadRequest("No gun in this slot");

            long newInstId = player.Guns.Count > 0 ? (player.Guns.Max(g => g.Id) + 1) : 1;
            var newGun = new PlayerGun
            {
                Id = newInstId,
                UserId = player.Id,
                GunId = developSlot.GunId,
                GunLevel = 1,
                Number = 1,
                Life = 500,
                Favor = 500000
            };
            player.Guns.Add(newGun);
            player.DevelopSlots.Remove(developSlot);

            _db.SavePlayer(player);
            Console.WriteLine($"[Factory] Player {player.Id} claimed Gun ID: {newGun.GunId} as Instance #{newGun.Id}");

            return EncryptedResponse(new
            {
                gun_with_user_info = newGun,
                develop_act_info = player.DevelopSlots
            }, player.SignKey);
        }

        private IActionResult HandleGuide(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            string? guide = body?["guide"]?.ToString();
            if (!string.IsNullOrEmpty(guide))
            {
                player.Guide = guide;
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleAdjutant(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            string? adjutant = body?["adjutant"]?.ToString();
            if (adjutant != null)
            {
                player.Adjutant = adjutant;
            }
            string? adjutantMulti = body?["adjutant_multi"]?.ToString();
            if (adjutantMulti != null)
            {
                player.AdjutantMulti = adjutantMulti;
            }
            string? fairy = body?["adjutant_fairy"]?.ToString();
            if (fairy != null)
            {
                player.AdjutantFairy = fairy;
            }
            _db.SavePlayer(player);
            return PlainSuccess();
        }

        public static Dictionary<string, object> BuildUserAdjutantMulti(string? adjutantMulti)
        {
            var single = new List<object>();
            var combined = new List<object>();
            if (!string.IsNullOrWhiteSpace(adjutantMulti))
            {
                foreach (var entry in adjutantMulti.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = entry.Split('|');
                    if (p.Length < 9) continue;
                    var item = new Dictionary<string, string>
                    {
                        ["index"] = p[1],
                        ["target_type"] = p[2],
                        ["target_id"] = p[3],
                        ["skin_id"] = p[4],
                        ["is_sexy"] = p[5],
                        ["mod"] = p[6],
                        ["background"] = p[7],
                        ["adjutant_skin"] = p[8]
                    };
                    if (p[0] == "single") single.Add(item);
                    else if (p[0] == "combined") combined.Add(item);
                }
            }
            return new Dictionary<string, object> { ["single"] = single, ["combined"] = combined };
        }

        private IActionResult HandleDormInfo(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            if (!player.Extra.TryGetValue("dorm_furniture", out string? furnJson) || string.IsNullOrEmpty(furnJson))
            {
                var starter = new List<Dictionary<string, string>>();
                long nextId = 19562787;
                foreach (var dorm in new[] { "1", "2" })
                {
                    foreach (var (fid, x, y) in new[] { ("201", "18", "4"), ("202", "3", "6"), ("203", "10", "6"), ("204", "0", "0"), ("205", "10", "0"), ("206", "6", "6"), ("207", "0", "0") })
                    {
                        starter.Add(new Dictionary<string, string> { ["id"] = (nextId++).ToString(), ["dorm"] = dorm, ["furniture_id"] = fid, ["x"] = x, ["y"] = y });
                    }
                }
                furnJson = JsonConvert.SerializeObject(starter);
                player.Extra["dorm_furniture"] = furnJson;
                _db.SavePlayer(player);
            }

            var furnitureList = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(furnJson) ?? new List<Dictionary<string, string>>();
            return EncryptedResponse(new
            {
                info = new { dorm_id = "1" },
                in_mydorm_list = new List<object>(),
                my_visit_list = new List<object>(),
                build_coin_flag = -4,
                current_build_coin = "40",
                establish_build_result = new[] { new { build_tmp_data = new List<object>() }, new { build_tmp_data = new List<object>() }, new { build_tmp_data = new List<object>() }, new { build_tmp_data = new List<object>() } },
                reset_build_coin_data = new List<object>(),
                notice = new { is_view_notice = "0", notice = (object?)null },
                is_show_commander = "1",
                is_use_commander = "1",
                is_sangvis_show_gun = "1",
                furniture_with_user_info = furnitureList
            }, player.SignKey);
        }

        private IActionResult HandleDormReceiveFavor(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            if (gunInstId > 0)
            {
                var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
                if (gun != null)
                {
                    gun.Favor = Math.Min(gun.MaxFavor, gun.Favor + 2500);
                    gun.CanClick = 0;
                    _db.SavePlayer(player);
                }
            }
            return EncryptedResponse(new { favor_click = "2500" }, player.SignKey);
        }

        private IActionResult HandleCarnival(PlayerAccount? player)
        {
            if (player == null) return BadRequest();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return EncryptedResponse(new
            {
                carnival_info = new
                {
                    user_id = player.Id.ToString(),
                    cumulative_login = "1",
                    cumulative_login_last_time = now.ToString()
                }
            }, player.SignKey);
        }

        private IActionResult HandleQuickGetQuestsResourceInMails(PlayerAccount? player)
        {
            return EncryptedResponse(new
            {
                user_exp = 0,
                mp = 0,
                ammo = 0,
                mre = 0,
                part = 0,
                core = 0,
                gem = 0,
                gem_pay = 0,
                gun_id = new List<object>(),
                item_ids = "",
                equip_ids = new List<object>(),
                furniture = new List<object>(),
                gift = "",
                skin = new List<object>(),
                bp_pay = 0,
                coins = "",
                fairy_ids = new List<object>(),
                commander_uniform = new List<object>(),
                sangvis = new List<object>(),
                vehicle_component = new List<object>(),
                career_mail_ids = new List<object>()
            }, player?.SignKey ?? AuthCode.DefaultKey);
        }

        private IActionResult HandleGameSetting(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            var settingsToken = body?["settings"] ?? body;
            if (settingsToken != null)
            {
                player.GameSetting = settingsToken.ToString(Formatting.None);
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleTeamGun(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            int teamId = body?.Value<int?>("team_id") ?? 1;
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            int location = body?.Value<int?>("location") ?? 1;
            int position = body?.Value<int?>("position") ?? 1;

            bool changed = false;
            if (teamId > 0)
            {
                foreach (var existing in player.Guns.Where(g => g.TeamId == teamId && g.Location == location && g.Id != gunInstId))
                {
                    existing.TeamId = 0;
                    existing.Location = 1;
                    existing.Position = 0;
                    changed = true;
                }
            }

            var targetGun = gunInstId > 0 ? player.Guns.FirstOrDefault(g => g.Id == gunInstId) : null;
            if (targetGun != null)
            {
                targetGun.TeamId = teamId;
                targetGun.Location = teamId > 0 ? location : 1;
                targetGun.Position = teamId > 0 ? position : 0;
                changed = true;
            }

            if (changed)
            {
                _db.SavePlayer(player);
            }

            return PlainSuccess();
        }

        private IActionResult HandleSetPosition(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            bool changed = false;
            if (body?["gun_with_user_id"] != null)
            {
                long gunInstId = body.Value<long>("gun_with_user_id");
                int position = body.Value<int?>("position") ?? 1;
                var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
                if (gun != null)
                {
                    gun.Position = position;
                    changed = true;
                }
            }
            else if (body?["positions"] is JObject posObj)
            {
                foreach (var kv in posObj)
                {
                    if (long.TryParse(kv.Key, out long gid) && int.TryParse(kv.Value?.ToString(), out int pos))
                    {
                        var gun = player.Guns.FirstOrDefault(g => g.Id == gid);
                        if (gun != null)
                        {
                            gun.Position = pos;
                            changed = true;
                        }
                    }
                }
            }

            if (changed) _db.SavePlayer(player);
            return PlainSuccess();
        }

        private IActionResult HandleChangeLock(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            bool changed = false;
            if (body?["gun_with_user_id"] != null)
            {
                long gid = body.Value<long>("gun_with_user_id");
                var gun = player.Guns.FirstOrDefault(g => g.Id == gid);
                if (gun != null)
                {
                    gun.IsLocked = body["is_locked"] != null ? body.Value<int>("is_locked") : (gun.IsLocked == 1 ? 0 : 1);
                    changed = true;
                }
            }
            if (body?["lock"] is JArray lockArr)
            {
                foreach (var tok in lockArr)
                {
                    if (long.TryParse(tok.ToString(), out long gid))
                    {
                        var gun = player.Guns.FirstOrDefault(g => g.Id == gid);
                        if (gun != null) { gun.IsLocked = 1; changed = true; }
                    }
                }
            }
            if (body?["unlock"] is JArray unlockArr)
            {
                foreach (var tok in unlockArr)
                {
                    if (long.TryParse(tok.ToString(), out long gid))
                    {
                        var gun = player.Guns.FirstOrDefault(g => g.Id == gid);
                        if (gun != null) { gun.IsLocked = 0; changed = true; }
                    }
                }
            }

            if (changed) _db.SavePlayer(player);
            return PlainSuccess();
        }

        private IActionResult HandleEatGun(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
            if (gun != null)
            {
                gun.Pow = Math.Max(gun.Pow, 100);
                gun.Hit = Math.Max(gun.Hit, 100);
                gun.Dodge = Math.Max(gun.Dodge, 100);
                gun.Rate = Math.Max(gun.Rate, 100);
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleCombineGun(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
            if (gun != null && gun.Number < 5)
            {
                gun.Number = Math.Min(5, gun.Number + 1);
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleUpgradeSkill(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            int skillNum = body?.Value<int?>("skill") ?? 1;
            var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
            if (gun != null)
            {
                if (skillNum == 2) gun.Skill2 = Math.Min(10, gun.Skill2 + 1);
                else gun.Skill1 = Math.Min(10, gun.Skill1 + 1);
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleMindUpdate(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
            if (gun != null)
            {
                gun.IfModification = Math.Min(3, gun.IfModification + 1);
                gun.GunLevel = Math.Max(gun.GunLevel, gun.IfModification >= 3 ? 120 : 115);
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleGunEquip(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            long equipInstId = body?.Value<long?>("equip_with_user_id") ?? 0;
            int slot = body?.Value<int?>("equip_slot") ?? body?.Value<int?>("slot") ?? 1;

            var gun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
            if (gun != null)
            {
                long oldEquipId = slot switch
                {
                    1 => gun.Equip1Id,
                    2 => gun.Equip2Id,
                    3 => gun.Equip3Id,
                    4 => gun.Equip4Id,
                    _ => 0
                };
                if (oldEquipId > 0)
                {
                    var oldEq = player.Equips.FirstOrDefault(e => e.Id == oldEquipId);
                    if (oldEq != null && oldEq.GunId == gun.Id) oldEq.GunId = 0;
                }
                if (equipInstId > 0)
                {
                    var newEq = player.Equips.FirstOrDefault(e => e.Id == equipInstId);
                    if (newEq != null) newEq.GunId = gun.Id;
                }
                if (slot == 1) gun.Equip1Id = equipInstId;
                else if (slot == 2) gun.Equip2Id = equipInstId;
                else if (slot == 3) gun.Equip3Id = equipInstId;
                else if (slot == 4) gun.Equip4Id = equipInstId;
                _db.SavePlayer(player);
            }
            return PlainSuccess();
        }

        private IActionResult HandleRetireGun(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            string gunIdsStr = body?.Value<string>("gun_with_user_ids") ?? "";
            var ids = gunIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => long.TryParse(s, out long v) ? v : 0)
                               .Where(v => v > 0)
                               .ToHashSet();

            player.Guns.RemoveAll(g => ids.Contains(g.Id));
            player.Ammo += ids.Count * 20;
            player.Mre += ids.Count * 20;
            player.Part += ids.Count * 5;

            _db.SavePlayer(player);
            return EncryptedResponse(new { retired_count = ids.Count }, player.SignKey);
        }

        private IActionResult HandleUseSkin(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            long gunInstId = body?.Value<long?>("gun_with_user_id") ?? 0;
            int skinId = body?.Value<int?>("skin_id") ?? body?.Value<int?>("skin") ?? 0;

            if (gunInstId == -1)
            {
                player.Extra["kalina_skin"] = skinId.ToString();
                _db.SavePlayer(player);
            }
            else if (gunInstId < -1)
            {
                player.Extra[$"npc_skin_{gunInstId}"] = skinId.ToString();
                _db.SavePlayer(player);
            }
            else
            {
                var targetGun = player.Guns.FirstOrDefault(g => g.Id == gunInstId);
                if (targetGun != null)
                {
                    targetGun.Skin = skinId;
                    _db.SavePlayer(player);
                }
            }

            return PlainSuccess();
        }

        private IActionResult HandleStartOperation(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            int opId = body?.Value<int>("operation_id") ?? 1;
            int teamId = body?.Value<int>("team_id") ?? 1;

            player.ActiveOperations.RemoveAll(o => o.TeamId == teamId);
            player.ActiveOperations.Add(new OperationAct
            {
                OperationId = opId,
                TeamId = teamId,
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Duration = 3600
            });

            _db.SavePlayer(player);
            return EncryptedResponse(new { operation_act_info = player.ActiveOperations }, player.SignKey);
        }

        private IActionResult HandleFinishOperation(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();

            int opId = body?.Value<int>("operation_id") ?? 1;
            player.ActiveOperations.RemoveAll(o => o.OperationId == opId);

            player.Ammo += 500;
            player.Mre += 500;
            player.Part += 200;
            player.Mp += 400;

            _db.SavePlayer(player);
            return EncryptedResponse(new
            {
                operation_act_info = player.ActiveOperations,
                reward = new { ammo = 500, mre = 500, part = 200, mp = 400 }
            }, player.SignKey);
        }

        private static List<Dictionary<string, object>> BuildSpotActList(ActiveMissionState mission)
        {
            return mission.Spots.Select(s => new Dictionary<string, object>
            {
                ["spot_id"] = s.SpotId.ToString(),
                ["belong"] = s.Belong.ToString(),
                ["if_random"] = "0",
                ["seed"] = s.Seed,
                ["team_id"] = s.TeamId,
                ["enemy_team_id"] = s.EnemyTeamId.ToString(),
                ["sangvis_team_id"] = "0",
                ["vehicle_with_user_id"] = "0",
                ["boss_hp"] = s.BossHp.ToString(),
                ["enemy_hp_percent"] = "1",
                ["enemy_instance_id"] = s.EnemyInstanceId.ToString(),
                ["enemy_ai"] = 0,
                ["enemy_ai_para"] = "",
                ["ally_instance_ids"] = s.AllyInstanceIds.Cast<object>().ToList(),
                ["squad_instance_ids"] = new List<object>(),
                ["hostage_id"] = s.HostageId.ToString(),
                ["hostage_hp"] = s.HostageHp.ToString(),
                ["hostage_max_hp"] = s.HostageMaxHp.ToString(),
                ["enemy_birth_turn"] = s.EnemyTeamId > 0 ? "1" : "999",
                ["reinforce_count"] = s.ReinforceCount,
                ["supply_count"] = s.SupplyCount.ToString(),
                ["fix_count"] = "0"
            }).ToList();
        }

        private static int ComputeTurnAp(ActiveMissionState mission)
        {
            var spotTypes = MasterDataManager.Instance.MissionSpots.TryGetValue(mission.MissionId, out var list)
                ? list.ToDictionary(s => s.Id, s => s.Type)
                : new Dictionary<int, int>();

            int ap = 0;
            foreach (var s in mission.Spots)
            {
                if (s.Belong == 1)
                {
                    if (spotTypes.TryGetValue(s.SpotId, out int type) && (type == 1 || type == 2 || type == 3 || type == 5))
                        ap++;
                }
                if (s.TeamId > 0)
                    ap++;
            }
            return Math.Max(2, ap);
        }

        private static bool SeedFullInventory(PlayerAccount player)
        {
            // Starting inventory is a one-time gift. Running it on every login refilled gems, shop currencies and
            // effects, which undid every purchase and made spending look like it was never saved.
            if (player.Extra.ContainsKey("inventory_seeded")) return false;
            player.Extra["inventory_seeded"] = "1";
            bool changed = true;

            if (player.Gem < 10000) { player.Gem = 999999; changed = true; }
            if (player.GemPay < 10000) { player.GemPay = 999999; changed = true; }

            string[] essentialItems =
            {
                "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
                "14", "15", "16", "17", "31", "32", "33",
                "41", "42", "43", "44", "45", "46", "50", "51", "55", "57", "60",
                "506", "507", "509", "512", "513", "514", "515", "516", "517", "526", "604", "605"
            };
            foreach (var itemId in essentialItems)
            {
                if (!player.Items.ContainsKey(itemId))
                {
                    player.Items[itemId] = 9999;
                    changed = true;
                }
            }

            // All shop currency items referenced in mall.json (True Core Masks, event tokens, medals, etc.)
            foreach (var r in Tables.Get("mall").Values)
            {
                string ip = r["itemprice"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(ip) || ip == "0") continue;
                foreach (var part in ip.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var sep = part.Split('-', ':');
                    if (sep.Length >= 1 && int.TryParse(sep[0], out int iid) && iid > 0)
                    {
                        string k = iid.ToString();
                        if (!player.Items.TryGetValue(k, out int cur) || cur < 1000)
                        {
                            player.Items[k] = 9999;
                            changed = true;
                        }
                    }
                }
            }

            // Special effects (type 7 in item.json, e.g. 7001..7056, 7201): 1 each so UI doesn't repeat cards
            foreach (var kv in Tables.Get("item"))
            {
                if (kv.Value.Int("type") != 7) continue;
                string key = kv.Key.ToString();
                if (!player.Items.TryGetValue(key, out int cur) || cur != 1)
                {
                    player.Items[key] = 1;
                    changed = true;
                }
            }

            // All gifts (gift_item.json: affection gifts, skin gift boxes, combat reports, etc.)
            foreach (var kv in Tables.Get("gift_item"))
            {
                string key = kv.Key.ToString();
                if (!player.Gifts.ContainsKey(key))
                {
                    player.Gifts[key] = 999;
                    changed = true;
                }
            }

            if (player.MaxEquip < 2000)
            {
                player.MaxEquip = 2000;
                changed = true;
            }

            if (player.Equips.RemoveAll(e => Tables.Row("equip", e.EquipId).Int("is_show") != 1) > 0)
                changed = true;

            if (player.Equips.Count == 0)
            {
                string[] statKeys =
                {
                    "pow", "hit", "dodge", "speed", "rate", "critical_harm_rate", "critical_percent",
                    "armor_piercing", "armor", "shield", "damage_amplify", "damage_reduction",
                    "night_view_percent", "bullet_number_up"
                };
                long nextEqId = 1;
                foreach (var kv in Tables.Get("equip").OrderBy(x => x.Key))
                {
                    var row = kv.Value;
                    if (row.Int("rank") != 5 || row.Int("is_show") != 1) continue;
                    bool isCraftableStandard = row.Int("develop_duration") > 0 && string.IsNullOrEmpty(row["fit_guns"]?.ToString());
                    int copies = isCraftableStandard ? 5 : 1;
                    for (int c = 0; c < copies; c++)
                    {
                        var eq = new PlayerEquip
                        {
                            Id = nextEqId++,
                            UserId = player.Id,
                            EquipId = kv.Key,
                            EquipLevel = 10
                        };
                        foreach (var sk in statKeys)
                        {
                            if (!string.IsNullOrEmpty(row[sk]?.ToString()))
                                eq.Stats[sk] = 10000; // 100% calibrated
                        }
                        player.Equips.Add(eq);
                    }
                }
                if (player.Equips.Count > 0) changed = true;
            }

            if (player.Fairies.Count == 0)
            {
                int defaultTalent = Tables.Get("fairy_talent").Keys.OrderBy(k => k).FirstOrDefault();
                long nextFairyId = 1;
                foreach (var kv in Tables.Get("fairy").OrderBy(x => x.Key))
                {
                    player.Fairies.Add(new PlayerFairy
                    {
                        Id = nextFairyId++,
                        FairyId = kv.Key,
                        Level = 100,
                        QualityLevel = 5,
                        SkillLevel = 10,
                        PassiveSkill = defaultTalent
                    });
                }
                if (player.Fairies.Count > 0) changed = true;
            }

            return changed;
        }

        private static Dictionary<string, object> BuildEnemyInstanceInfo(ActiveMissionState mission)
        {
            return mission.Spots
                .Where(s => s.EnemyInstanceId > 0 && s.EnemyTeamId > 0)
                .ToDictionary(
                    s => s.EnemyInstanceId.ToString(),
                    s => (object)new Dictionary<string, object>
                    {
                        ["enemy_team_id"] = s.EnemyTeamId.ToString(),
                        ["boss_hp"] = s.BossHp.ToString(),
                        ["enemy_hp_percent"] = 1,
                        ["enemy_birth_turn"] = 1,
                        ["enemy_ai"] = "0",
                        ["enemy_ai_para"] = ""
                    });
        }

        private static Dictionary<string, object> BuildMissionSectionPayload(
            ActiveMissionState mission,
            IEnumerable<string>? diedEnemies = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["ap"] = mission.Ap,
                ["turn"] = mission.Turn,
                ["died_this_section"] = new Dictionary<string, object>
                {
                    ["enemy"] = diedEnemies?.ToList() ?? new List<string>(),
                    ["ally"] = new List<string>()
                },
                ["fight_environment_group"] = "",
                ["range_atk_rec"] = new List<object>(),
                ["range_atk_turn"] = new Dictionary<string, object>
                {
                    ["10"] = new List<object>(),
                    ["304"] = new List<object>(),
                    ["305"] = new List<object>(),
                    ["4"] = new List<object>(),
                    ["5"] = new List<object>(),
                    ["6"] = new List<object>()
                },
                ["mission_trigger_engine"] = new List<object>(),
                ["type5_score"] = "0",
                ["target_moved_step"] = new List<object>(),
                ["ally1_add"] = new List<object>(),
                ["fairy_skill_return"] = new List<object>(),
                ["fairy_skill_perform"] = new List<object>(),
                ["fairy_skill_on_spot"] = new List<object>(),
                ["fairy_skill_on_team"] = new List<object>(),
                ["fairy_skill_on_enemy"] = new List<object>(),
                ["fairy_skill_on_squad"] = new List<object>(),
                ["fairy_skill_on_ally"] = new List<object>(),
                ["fairy_skill_on_sangvis"] = new List<object>(),
                ["fairy_skill_on_vehicle"] = new List<object>(),
                ["fairy_skill_on_friend_team"] = new List<object>(),
                ["fairy_skill_on_friend_sangvis"] = new List<object>(),
                ["spot_act_info"] = BuildSpotActList(mission),
                ["building_info"] = new List<object>(),
                ["mission_win_step_control_ids"] = new List<object>(),
                ["trigger_para"] = new Dictionary<string, object>
                {
                    ["drop_item"] = new List<object>(),
                    ["cost_item"] = new List<object>()
                },
                ["enemy_instance_info"] = BuildEnemyInstanceInfo(mission),
                ["squad_info"] = new List<object>(),
                ["vehicle_info"] = new List<object>(),
                ["ally_instance_info"] = new List<object>(),
                ["building_defender_change"] = new List<object>(),
                ["spot_teleport_result"] = new List<object>(),
                ["ally_instance_transform"] = new List<object>(),
                ["ally_instance_betray"] = new List<object>(),
                ["change_belong"] = new List<object>(),
                ["mission_win_result"] = new List<object>(),
                ["mission_lose_result"] = new List<object>(),
                ["mission_control"] = new Dictionary<string, int> { ["1"] = 1, ["2"] = 2, ["3"] = 3 }
            };
            if (IsNightMission(mission.MissionId)) ApplyNightView(mission, payload);
            return payload;
        }

        private IActionResult HandleStartMission(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            int missionId = body?.Value<int?>("mission_id") ?? 1;

            var spotsData = MasterDataManager.Instance.MissionSpots.TryGetValue(missionId, out var list)
                ? list
                : new List<MasterSpotInfo>();

            var rnd = new Random();
            int enemyInst = 1;
            var missionState = new ActiveMissionState
            {
                MissionId = missionId,
                Turn = 1,
                Spots = spotsData.Select(s => new MissionSpotState
                {
                    SpotId = s.Id,
                    Belong = s.Belong,
                    TeamId = 0,
                    EnemyTeamId = s.EnemyTeamId,
                    EnemyInstanceId = s.EnemyTeamId > 0 ? enemyInst++ : 0,
                    Seed = rnd.Next(1000, 9999),
                    ReinforceCount = s.Belong == 1 ? 1 : 0
                }).ToList()
            };

            PlaceHostages(missionState);
            PlaceAlliesAndBuildings(missionState);

            if (body?["spots"] is JArray deployArr)
            {
                foreach (var item in deployArr)
                {
                    int spotId = item.Value<int?>("spot_id") ?? 0;
                    int teamId = item.Value<int?>("team_id") ?? 0;
                    var spot = missionState.Spots.FirstOrDefault(s => s.SpotId == spotId);
                    if (spot != null && teamId > 0)
                    {
                        spot.TeamId = teamId;
                        spot.Belong = 1;
                    }
                }
            }

            missionState.Ap = ComputeTurnAp(missionState);
            player.ActiveMission = missionState;
            string mKey = missionId.ToString();
            if (!player.Missions.TryGetValue(mKey, out var mRec))
                player.Missions[mKey] = mRec = new MissionRecord { MissionId = missionId, BestRank = 0, Medal4 = 0, Counter = 0, WinCounter = 0 };
            mRec.Counter++;
            _db.SavePlayer(player);

            var startPayload = BuildMissionSectionPayload(missionState);
            startPayload["ally_instance_info"] = AllyInstanceInfo(missionState);
            return EncryptedResponse(startPayload, player.SignKey);
        }

        private IActionResult HandleReinforceTeam(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new Dictionary<string, object>(), player.SignKey);

            int spotId = body?.Value<int?>("spot_id") ?? 0;
            int teamId = body?.Value<int?>("team_id") ?? 0;
            var spot = player.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == spotId);
            if (spot != null && teamId > 0)
            {
                spot.TeamId = teamId;
                spot.Belong = 1;
                spot.ReinforceCount++;
                player.ActiveMission.Ap++;
                _db.SavePlayer(player);
            }

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleTeamMove(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new Dictionary<string, object>(), player.SignKey);

            int fromSpotId = body?.Value<int?>("from_spot_id") ?? 0;
            int toSpotId = body?.Value<int?>("to_spot_id") ?? 0;

            var fromSpot = player.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == fromSpotId);
            var toSpot = player.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == toSpotId);
            if (fromSpot != null && toSpot != null)
            {
                int movingTeam = fromSpot.TeamId;
                fromSpot.TeamId = 0;
                toSpot.TeamId = movingTeam;
                if (toSpot.EnemyTeamId == 0)
                {
                    toSpot.Belong = 1;
                }
                _db.SavePlayer(player);
            }

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleSupplyTeam(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            int teamId = body?.Value<int?>("target_id") ?? body?.Value<int?>("team_id") ?? 0;
            int spotId = body?.Value<int?>("spot_id") ?? 0;
            int supplyCount = 1;

            if (teamId > 0)
            {
                int ammoCost = 0;
                int mreCost = 0;
                foreach (var g in player.Guns.Where(g => g.TeamId == teamId))
                {
                    int needAmmo = Math.Max(0, 5 - g.GunAmmo);
                    int needMre = Math.Max(0, 10 - g.GunMre);
                    ammoCost += needAmmo * 4;
                    mreCost += needMre * 3;
                    g.GunAmmo = 5;
                    g.GunMre = 10;
                }
                player.Ammo = Math.Max(0, player.Ammo - ammoCost);
                player.Mre = Math.Max(0, player.Mre - mreCost);

                if (player.ActiveMission != null)
                {
                    var spot = spotId > 0
                        ? player.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == spotId)
                        : player.ActiveMission.Spots.FirstOrDefault(s => s.TeamId == teamId);
                    if (spot != null)
                    {
                        spot.SupplyCount++;
                        supplyCount = spot.SupplyCount;
                    }
                }

                _db.SavePlayer(player);
            }

            return EncryptedResponse(new
            {
                max_ammo = 5,
                max_mre = 10,
                type5_score = "0",
                supply_count = supplyCount.ToString()
            }, player.SignKey);
        }

        private IActionResult HandleBattleFinish(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            int missionId = body?.Value<int?>("mission_id") ?? player.ActiveMission?.MissionId ?? 1;
            int spotId = body?.Value<int?>("spot_id") ?? 0;

            var diedEnemies = new List<string>();
            if (player.ActiveMission != null)
            {
                var spot = spotId > 0
                    ? player.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == spotId)
                    : player.ActiveMission.Spots.FirstOrDefault(s => s.TeamId > 0 && s.EnemyTeamId > 0);
                if (spot != null && spot.EnemyInstanceId > 0)
                {
                    diedEnemies.Add(spot.EnemyInstanceId.ToString());
                    spot.EnemyTeamId = 0;
                    spot.EnemyInstanceId = 0;
                    spot.Belong = 1;
                }
            }

            // Mission clear is recorded only on victory (see GameProgress.CompleteMission).
            _db.SavePlayer(player);

            if (player.ActiveMission != null)
            {
                return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission, diedEnemies), player.SignKey);
            }

            return EncryptedResponse(new
            {
                rank = 5,
                exp = 1000,
                mission_id = missionId
            }, player.SignKey);
        }

        private IActionResult HandleEndTurn(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new { turn = 2, enemy_act = new List<object>() }, player.SignKey);

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleStartEnemyTurn(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new Dictionary<string, object>(), player.SignKey);

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleEndEnemyTurn(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new Dictionary<string, object>(), player.SignKey);

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleStartTurn(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            if (player.ActiveMission == null)
                return EncryptedResponse(new Dictionary<string, object>(), player.SignKey);

            player.ActiveMission.Turn += 1;
            foreach (var spot in player.ActiveMission.Spots.Where(s => s.TeamId > 0))
            {
                spot.Belong = 1;
            }
            player.ActiveMission.Ap = ComputeTurnAp(player.ActiveMission);
            _db.SavePlayer(player);

            return EncryptedResponse(BuildMissionSectionPayload(player.ActiveMission), player.SignKey);
        }

        private IActionResult HandleAbortMission(PlayerAccount? player, JObject? body)
        {
            if (player == null) return BadRequest();
            int missionId = player.ActiveMission?.MissionId ?? 1;
            int turn = player.ActiveMission?.Turn ?? 1;
            player.ActiveMission = null;
            _db.SavePlayer(player);

            return EncryptedResponse(new
            {
                mission_lose_result = new
                {
                    win_type_ids = new List<object>(),
                    turn = turn.ToString(),
                    enemydie_num = "0",
                    enemydie_num_killbyfriend = "0",
                    gundie_num = "0",
                    mysquad_die_num = "0",
                    sangvisdie_num = "0",
                    mica_client_log = new { mid = missionId, uid = player.Id }
                }
            }, player.SignKey);
        }

        private IActionResult HandleGenericSuccess(PlayerAccount? player, string op, JObject? body)
        {
            LogToFile($"[Unmapped Op] {op} body={body?.ToString(Formatting.None)}");
            return PlainSuccess();
        }
    }
}
