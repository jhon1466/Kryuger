using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Kryuger.Common.Database;
using Kryuger.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// GM API used by Kryuger GM Tools. Only answers loopback clients that send the token
    /// (header X-GM-Token). The token comes from KRYUGER_GM_TOKEN or the file KRYUGER_GM_TOKEN.txt at the
    /// repository root; it is generated on first use. Edits run under the same lock as game requests, so they
    /// never race with a connected player.
    /// </summary>
    [ApiController]
    [Route("gm")]
    public class GmController : ControllerBase
    {
        private readonly DatabaseManager _db;
        public GmController(DatabaseManager db) => _db = db;

        private static string? _token;
        public static string TokenFile => Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "KRYUGER_GM_TOKEN.txt"));

        public static string Token()
        {
            if (_token != null) return _token;
            var env = Environment.GetEnvironmentVariable("KRYUGER_GM_TOKEN");
            if (!string.IsNullOrWhiteSpace(env) && env.Length >= 24) return _token = env.Trim();
            try
            {
                if (System.IO.File.Exists(TokenFile))
                {
                    var t = System.IO.File.ReadAllText(TokenFile).Trim();
                    if (t.Length >= 24) return _token = t;
                }
                var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                System.IO.File.WriteAllText(TokenFile, fresh);
                return _token = fresh;
            }
            catch { return _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); }
        }

        private bool Allowed()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            if (ip == null || !IPAddress.IsLoopback(ip)) return false;
            var given = Request.Headers["X-GM-Token"].ToString();
            var expected = Token();
            return given.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(expected));
        }

        // ---------------- names from asset/table/*.txt ----------------
        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _texts = new();
        private static string Text(string file, string? key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            var map = _texts.GetOrAdd(file, f =>
            {
                var d = new Dictionary<string, string>();
                var path = Path.Combine(Tables.DataPath, "asset", "table", f + ".txt");
                if (System.IO.File.Exists(path))
                    foreach (var line in System.IO.File.ReadLines(path))
                    {
                        int c = line.IndexOf(',');
                        if (c > 0) d[line[..c]] = line[(c + 1)..];
                    }
                return d;
            });
            return map.TryGetValue(key, out var v) ? v : key;
        }

        private static object? _catalog;

        [HttpGet("catalog")]
        public IActionResult Catalog()
        {
            if (!Allowed()) return Unauthorized();
            _catalog ??= new
            {
                guns = Tables.Get("gun").Values.Where(r => r.Int("id") < 9000)
                    .Select(r => new { id = r.Int("id"), name = r["en_name"]?.ToString() is { Length: > 0 } n ? n : Text("gun", r["name"]?.ToString()), rank = r.Int("rank"), type = r.Int("type") })
                    .OrderBy(x => x.id).ToList(),
                items = Tables.Get("item").Values
                    .Select(r => new { id = r.Int("id"), name = Text("item", r["item_name"]?.ToString()), type = r.Int("type") })
                    .Where(x => !string.IsNullOrEmpty(x.name)).OrderBy(x => x.id).ToList(),
                equips = Tables.Get("equip").Values
                    .Select(r => new { id = r.Int("id"), name = Text("equip", r["name"]?.ToString()), rank = r.Int("rank") })
                    .OrderBy(x => x.id).ToList(),
                fairies = Tables.Get("fairy").Values
                    .Select(r => new { id = r.Int("id"), name = Text("fairy", r["name"]?.ToString()) })
                    .OrderBy(x => x.id).ToList(),
                skins = Tables.Get("skin").Values
                    .Select(r => new { id = r.Int("id"), name = Text("skin", r["name"]?.ToString()), gun = r.Int("fit_gun") })
                    .OrderBy(x => x.id).ToList()
            };
            return Ok(_catalog);
        }

        [HttpGet("players")]
        public IActionResult Players()
        {
            if (!Allowed()) return Unauthorized();
            return Ok(_db.AllPlayers().Select(p => new
            {
                id = p.Id, name = p.Name, openId = p.OpenId, level = p.Level, experience = p.Experience,
                gem = p.Gem, mp = p.Mp, ammo = p.Ammo, mre = p.Mre, part = p.Part,
                guns = p.Guns.Count, equips = p.Equips.Count, fairies = p.Fairies.Count, lastLogin = p.LastLoginAt
            }).OrderBy(p => p.id));
        }

        public class GiveReq
        {
            public long Uid { get; set; }
            public string Kind { get; set; } = "";   // resources | gem | item | gun | equip | fairy | skin
            public int Id { get; set; }
            public int Count { get; set; } = 1;
        }

        [HttpPost("give")]
        public async Task<IActionResult> Give([FromBody] GiveReq req)
        {
            if (!Allowed()) return Unauthorized();
            if (req.Count <= 0 || req.Count > 100000) return BadRequest(new { error = "count must be 1-100000" });
            await GameController._requestLock.WaitAsync();
            try
            {
                var p = _db.GetPlayerById(req.Uid);
                if (p == null) return NotFound(new { error = "player not found" });
                var gc = new GameController(_db);
                string done;
                switch (req.Kind)
                {
                    case "resources":
                        p.Mp += req.Count; p.Ammo += req.Count; p.Mre += req.Count; p.Part += req.Count;
                        done = $"+{req.Count} of each resource"; break;
                    case "gem":
                        p.Gem += req.Count; done = $"+{req.Count} gems"; break;
                    case "item":
                        if (Tables.Row("item", req.Id) == null) return BadRequest(new { error = "unknown item" });
                        gc.GmAddItem(p, req.Id, req.Count); done = $"+{req.Count} item {req.Id}"; break;
                    case "gun":
                        if (Tables.Row("gun", req.Id) == null) return BadRequest(new { error = "unknown doll" });
                        for (int i = 0; i < Math.Min(req.Count, 100); i++) gc.GmGiveGun(p, req.Id);
                        done = $"+{Math.Min(req.Count, 100)} doll {req.Id}"; break;
                    case "equip":
                        if (Tables.Row("equip", req.Id) == null) return BadRequest(new { error = "unknown equipment" });
                        for (int i = 0; i < Math.Min(req.Count, 100); i++) gc.GmGiveEquip(p, req.Id);
                        done = $"+{Math.Min(req.Count, 100)} equipment {req.Id}"; break;
                    case "fairy":
                        if (Tables.Row("fairy", req.Id) == null) return BadRequest(new { error = "unknown fairy" });
                        for (int i = 0; i < Math.Min(req.Count, 20); i++) gc.GmGiveFairy(p, req.Id);
                        done = $"+{Math.Min(req.Count, 20)} fairy {req.Id}"; break;
                    case "skin":
                        if (Tables.Row("skin", req.Id) == null) return BadRequest(new { error = "unknown skin" });
                        if (!p.Skins.Contains(req.Id)) p.Skins.Add(req.Id);
                        done = $"skin {req.Id} unlocked"; break;
                    default:
                        return BadRequest(new { error = "unknown kind" });
                }
                _db.SavePlayer(p);
                Audit($"give uid={req.Uid} {done}");
                return Ok(new { ok = true, message = done });
            }
            finally { GameController._requestLock.Release(); }
        }

        public class SetReq
        {
            public long Uid { get; set; }
            public string Field { get; set; } = "";  // gem | mp | ammo | mre | part | level | name
            public string Value { get; set; } = "";
        }

        [HttpPost("set")]
        public async Task<IActionResult> Set([FromBody] SetReq req)
        {
            if (!Allowed()) return Unauthorized();
            await GameController._requestLock.WaitAsync();
            try
            {
                var p = _db.GetPlayerById(req.Uid);
                if (p == null) return NotFound(new { error = "player not found" });
                if (req.Field == "name")
                {
                    if (string.IsNullOrWhiteSpace(req.Value) || req.Value.Length > 20) return BadRequest(new { error = "name 1-20 chars" });
                    p.Name = req.Value.Trim();
                }
                else
                {
                    if (!int.TryParse(req.Value, out int v) || v < 0) return BadRequest(new { error = "value must be a non-negative number" });
                    switch (req.Field)
                    {
                        case "gem": p.Gem = v; break;
                        case "mp": p.Mp = v; break;
                        case "ammo": p.Ammo = v; break;
                        case "mre": p.Mre = v; break;
                        case "part": p.Part = v; break;
                        case "level": p.Level = Math.Clamp(v, 1, 300); break;
                        default: return BadRequest(new { error = "unknown field" });
                    }
                }
                _db.SavePlayer(p);
                Audit($"set uid={req.Uid} {req.Field}={req.Value}");
                return Ok(new { ok = true, message = $"{req.Field} = {req.Value}" });
            }
            finally { GameController._requestLock.Release(); }
        }

        private static void Audit(string line)
        {
            try { System.IO.File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gm_audit.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n"); } catch { }
        }
    }
}
