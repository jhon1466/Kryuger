using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>Battle pass, auto-missions (combat simulation dispatch) and table-driven prize helpers.</summary>
    public partial class GameController
    {
        /// <summary>
        /// Max HP as the client computes it (checked against the client: M16A1/RO635/AR-15/M4A1 at Lv1 x5 links =
        /// 305/485/265/275): ceil(base(lv) * type basic_attribute_life * gun ratio_life / 100) * dummy links.
        /// </summary>
        public static int MaxLife(PlayerGun g)
        {
            var row = Tables.Row("gun", g.GunId);
            var type = Tables.Row("gun_type_info", row.Int("type", 4));
            double typeMul = double.TryParse(type?["basic_attribute_life"]?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 1.0;
            int lv = Math.Max(1, g.GunLevel);
            double baseLife = lv <= 100 ? 55 + 0.555 * (lv - 1) : 96.283 + 0.138 * (lv - 1);
            int perLink = (int)Math.Ceiling(baseLife * typeMul * row.Int("ratio_life", 100) / 100.0);
            return perLink * Math.Max(1, g.Number);
        }

        /// <summary>Official prize rows are sent back as the raw row with every value as a string.</summary>
        private static JObject PrizeJson(int prizeId)
        {
            var row = Tables.Row("prize", prizeId);
            var o = new JObject();
            if (row == null) return o;
            foreach (var pr in row.Properties())
                o[pr.Name] = pr.Value.Type is JTokenType.Array or JTokenType.Object ? pr.Value : (JToken)(pr.Value.ToString());
            return o;
        }

        /// <summary>"level:prizeId,level:prizeId" -> map.</summary>
        private static Dictionary<int, int> LevelPrizes(string? spec)
        {
            var m = new Dictionary<int, int>();
            foreach (var part in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && int.TryParse(kv[0], out int lv) && int.TryParse(kv[1], out int pid)) m[lv] = pid;
                else if (kv.Length == 1 && int.TryParse(kv[0], out int only)) m[1] = only;
            }
            return m;
        }

        // ===================== Mail =====================
        // Official Index/getMailList item (captured): id, user_id, type, sub_id, user_exp, mp, ammo, mre, part, core, gem,
        // gem_pay, gun_id, sangvis, fairy_ids, item_ids, equip_ids, furniture, gift, coins, skin, commander_uniform,
        // vehicle_component, bp_pay, chip, title, content, code, start_time, end_time, if_read, if_received, remark, if_locked.
        private static readonly string[] MailRewardFields =
            { "user_exp", "mp", "ammo", "mre", "part", "core", "gem", "gem_pay", "gun_id", "sangvis", "fairy_ids", "item_ids",
              "equip_ids", "furniture", "gift", "coins", "skin", "commander_uniform", "vehicle_component", "bp_pay", "chip" };
        private static readonly HashSet<string> MailNumericFields =
            new() { "user_exp", "mp", "ammo", "mre", "part", "core", "gem", "gem_pay", "gun_id", "sangvis", "skin", "bp_pay" };

        private static List<JObject> Mails(PlayerAccount p)
        {
            try
            {
                var raw = p.Extra.GetValueOrDefault("mails");
                return string.IsNullOrEmpty(raw) ? new() : JArray.Parse(raw).OfType<JObject>().ToList();
            }
            catch { return new(); }
        }

        private static void SaveMails(PlayerAccount p, List<JObject> mails) =>
            p.Extra["mails"] = new JArray(mails).ToString(Formatting.None);

        /// <summary>Puts a mail in the player's mailbox. reward is any row with prize-style fields
        /// (prize, achievement, daily...); type 1 = system, 2 = achievement (official values).</summary>
        public static void SendMail(PlayerAccount p, string title, string content, JObject? reward, int type = 1, int subId = 0)
        {
            var mails = Mails(p);
            long next = mails.Count == 0 ? 1 : mails.Max(m => m.Value<long>("id")) + 1;
            var m = new JObject
            {
                ["id"] = next.ToString(), ["user_id"] = p.Id.ToString(), ["type"] = type.ToString(), ["sub_id"] = subId.ToString()
            };
            foreach (var f in MailRewardFields)
            {
                var v = reward?[f];
                string sv = v == null || v.Type is JTokenType.Null or JTokenType.Array or JTokenType.Object ? "" : v.ToString();
                m[f] = MailNumericFields.Contains(f) && string.IsNullOrEmpty(sv) ? "0" : sv;
            }
            m["title"] = title; m["content"] = content; m["code"] = "";
            m["start_time"] = Now.ToString(); m["end_time"] = "2100000000";
            m["if_read"] = "0"; m["if_received"] = "0"; m["remark"] = ""; m["if_locked"] = 0;
            mails.Add(m);
            SaveMails(p, mails);
        }

        /// <summary>Claims one mail; returns the doll it created (if any).</summary>
        private PlayerGun? ClaimMail(PlayerAccount p, JObject m, Dictionary<string, long> totals)
        {
            if (m.Value<string>("if_received") == "1") return null;
            var gunsBefore = p.Guns.Select(g => g.Id).ToHashSet();
            var got = GrantRow(p, m);
            foreach (var k in new[] { "mp", "ammo", "mre", "part", "gem", "core", "user_exp" })
                if (got.TryGetValue(k, out var v) && v is int n) totals[k] = totals.GetValueOrDefault(k) + n;
            foreach (var part in (m.Value<string>("gift") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('-', ':');
                if (kv.Length == 2 && int.TryParse(kv[1], out int cnt))
                    p.Gifts[kv[0]] = (p.Gifts.TryGetValue(kv[0], out int cur) ? cur : 0) + cnt;
            }
            m["if_read"] = "1"; m["if_received"] = "1";
            return p.Guns.FirstOrDefault(g => !gunsBefore.Contains(g.Id));
        }

        private static HashSet<long> MailIds(JObject? b)
        {
            var ids = Ids(b?["mail_with_user_ids"] ?? b?["_array"]).ToHashSet();
            long one = L(b, "mail_with_user_id", "mail_id", "id");
            if (one > 0) ids.Add(one);
            return ids;
        }

        private static List<int>? _defaultOpenMissions;

        /// <summary>Missions listed as open (not cleared) for a new account in the official capture
        /// (data/index_template.json, mission_with_user_info).</summary>
        private static List<int> DefaultOpenMissions()
        {
            if (_defaultOpenMissions != null) return _defaultOpenMissions;
            var list = new List<int>();
            try
            {
                string f = IndexTemplatePath();
                if (System.IO.File.Exists(f))
                    foreach (var m in JObject.Parse(System.IO.File.ReadAllText(f))["mission_with_user_info"] as JArray ?? new JArray())
                        if (int.TryParse(m["mission_id"]?.ToString(), out int id) && Tables.Row("mission", id) != null) list.Add(id);
            }
            catch { }
            return _defaultOpenMissions = list;
        }

        private IActionResult? TryHandleSystems(string op, JObject? b, PlayerAccount p)
        {
            if (!p.Extra.ContainsKey("mail_welcome"))
            {
                p.Extra["mail_welcome"] = "1";
                SendMail(p, "Welcome, Commander", "Supplies from Griffin HQ.",
                    new JObject { ["gem"] = "1000", ["mp"] = "5000", ["ammo"] = "5000", ["mre"] = "5000", ["part"] = "5000", ["item_ids"] = "1-50,2-50,3-20,4-20" });
                _db.SavePlayer(p);
            }
            switch (op.ToLowerInvariant())
            {
                // ===== Enhancement screen =====
                // Gun/eatGunIndex {"gun_with_user_id":449}: opened before Gun/eatGun. No official capture exists; the
                // reply mirrors Gun/eatGun's documented shape (pow/hit/dodge/rate, see kyoukaya/hoxy defs/gunEatGun.go)
                // with the doll's current enhancement and its cap, instead of the bare "1" the client cannot parse.
                case "gun/eatgunindex":
                {
                    var g = p.Guns.FirstOrDefault(x => x.Id == L(b, "gun_with_user_id"));
                    if (g == null) return EncryptedResponse(new { pow = 0, hit = 0, dodge = 0, rate = 0 }, p.SignKey);
                    var (mp, mh, md, mr) = Tables.GetMaxEnhancement(g.GunId, g.GunLevel);
                    return EncryptedResponse(new
                    {
                        gun_with_user_id = g.Id.ToString(),
                        pow = g.Pow, hit = g.Hit, dodge = g.Dodge, rate = g.Rate,
                        max_pow = mp, max_hit = mh, max_dodge = md, max_rate = mr
                    }, p.SignKey);
                }

                // ===== Mail =====
                case "index/getmaillist":
                {
                    long startId = L(b, "start_id");
                    var list = Mails(p).Where(m => m.Value<long>("id") > startId && m.Value<long>("end_time") > Now).ToList();
                    return EncryptedResponse(list, p.SignKey);
                }
                case "index/readmail":
                case "index/readmails":
                {
                    var mails = Mails(p);
                    var ids = MailIds(b);
                    foreach (var m in mails.Where(m => ids.Contains(m.Value<long>("id")))) m["if_read"] = "1";
                    SaveMails(p, mails);
                    return Ok1(p);
                }
                case "index/getresourceinmail":
                {
                    var ids = MailIds(b);
                    var mails = Mails(p);
                    var m = mails.FirstOrDefault(x => ids.Contains(x.Value<long>("id")));
                    if (m == null) return EncryptedResponse(new object?[] { null }, p.SignKey);
                    var gun = ClaimMail(p, m, new Dictionary<string, long>());
                    SaveMails(p, mails);
                    return gun != null ? Enc(p, new { gun_with_user_id = gun.Id.ToString() }) : Enc(p, new object?[] { null });
                }
                case "index/quickgetresourceinmails":
                {
                    var mails = Mails(p);
                    var totals = new Dictionary<string, long>();
                    var guns = new List<string>();
                    foreach (var m in mails.Where(m => m.Value<string>("if_received") != "1" && m.Value<long>("end_time") > Now))
                    {
                        var g = ClaimMail(p, m, totals);
                        if (g != null) guns.Add(g.Id.ToString());
                    }
                    SaveMails(p, mails);
                    return Enc(p, new
                    {
                        user_exp = totals.GetValueOrDefault("user_exp"), mp = totals.GetValueOrDefault("mp"), ammo = totals.GetValueOrDefault("ammo"),
                        mre = totals.GetValueOrDefault("mre"), part = totals.GetValueOrDefault("part"), core = totals.GetValueOrDefault("core"),
                        gem = totals.GetValueOrDefault("gem"), gem_pay = 0, gun_id = guns, item_ids = "", equip_ids = new List<object>(),
                        furniture = new List<object>(), gift = "", skin = new List<object>(), bp_pay = 0, coins = "", fairy_ids = new List<object>(),
                        commander_uniform = new List<object>(), sangvis = new List<object>(), vehicle_component = new List<object>()
                    });
                }
                case "index/deletemail":
                case "index/deletemails":
                case "index/removemail":
                {
                    var mails = Mails(p);
                    var ids = MailIds(b);
                    // with ids: delete those (unless locked); without ids: "delete all read" = every claimed mail
                    mails.RemoveAll(m => ids.Count > 0
                        ? ids.Contains(m.Value<long>("id")) && m.Value<int>("if_locked") != 1
                        : m.Value<string>("if_received") == "1");
                    SaveMails(p, mails);
                    return Ok1(p);
                }

                // ===== Battle pass =====
                // getBonus {"battlepass_id":42,"type":1,"param":1}: type 1 = free track, 2 = paid track, 3 = extra, param = level
                case "battlepass/getbonus":
                case "battlepass/getallbonus":
                {
                    int bpId = (int)L(b, "battlepass_id");
                    var row = Tables.Row("event_battlepass", bpId);
                    var tracks = new (int type, string key, Dictionary<int, int> prizes)[]
                    {
                        (1, "free_bonus", LevelPrizes(row?["gift_free"]?.ToString())),
                        (2, "paid_bonus", LevelPrizes(row?["gift_paytounlock"]?.ToString())),
                        (3, "extra_bonus", LevelPrizes(row?["gift_extra"]?.ToString())),
                    };
                    bool all = op.EndsWith("getallbonus", StringComparison.OrdinalIgnoreCase);
                    int onlyType = (int)L(b, "type"), onlyLevel = (int)L(b, "param");
                    var resp = new JObject();
                    foreach (var (type, key, prizes) in tracks)
                    {
                        var got = new JObject();
                        string claimKey = $"bp_{bpId}_{type}";
                        var claimed = (p.Extra.GetValueOrDefault(claimKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                        foreach (var (lv, pid) in prizes.OrderBy(x => x.Key))
                        {
                            if (!all && (type != onlyType || lv != onlyLevel)) continue;
                            if (!claimed.Add(lv.ToString())) continue;
                            GrantRow(p, Tables.Row("prize", pid));
                            got[lv.ToString()] = PrizeJson(pid);
                        }
                        p.Extra[claimKey] = string.Join(',', claimed);
                        resp[key] = got;
                    }
                    return Enc(p, resp);
                }

                // ===== Auto missions (dispatching echelons to a cleared map) =====
                case "automission/startautomission":
                {
                    int missionId = (int)L(b, "auto_mission_id", "mission_id");
                    var row = Tables.Get("auto_mission").Values.FirstOrDefault(r => r.Int("mission_id") == missionId);
                    int number = (int)Math.Max(1, L(b, "number", "num"));
                    string teams = b?["team_ids"] is JArray ta ? string.Join(",", ta) : b?["team_ids"]?.ToString() ?? L(b, "team_id").ToString();
                    var acts = AutoMissions(p);
                    acts.RemoveAll(a => a.Value<int>("auto_mission_id") == missionId);
                    acts.Add(new JObject
                    {
                        ["id"] = (Now % 10_000_000).ToString(),
                        ["user_id"] = p.Id.ToString(),
                        ["auto_mission_id"] = missionId.ToString(),
                        ["team_ids"] = teams,
                        ["end_time"] = (Now + row.Int("duration", 3600) * number).ToString(),
                        ["number"] = number.ToString()
                    });
                    // the official server charges the dispatch cost up front
                    Spend(p, row.Int("mp") * number, row.Int("ammo") * number, row.Int("mre") * number, row.Int("part") * number);
                    p.Extra["auto_missions"] = new JArray(acts).ToString(Formatting.None);
                    return Ok1(p);
                }
                case "automission/quickautomission":
                {
                    // Instant finish: costs quick_auto_mission_item (525) by remaining time, per
                    // quick_auto_mission_num "60:1;120:2;9999:3" (minutes:items), then resolves like a normal finish.
                    int qid = (int)L(b, "auto_mission_id", "mission_id");
                    var qacts = AutoMissions(p);
                    var qact = qacts.FirstOrDefault(a => a.Value<int>("auto_mission_id") == qid);
                    if (qact == null) return Enc(p, new { add_gun_exp = new List<object>(), free_exp = 0, add_user_exp = "0", add_prize = new List<object>(), favor_change = new Dictionary<string, int>(), success_number = 0 });
                    long minutesLeft = Math.Max(0, (qact.Value<long>("end_time") - Now + 59) / 60);
                    int cost = minutesLeft <= 0 ? 0 : minutesLeft <= 60 ? 1 : minutesLeft <= 120 ? 2 : 3;
                    if (cost > 0)
                    {
                        if (p.Items.GetValueOrDefault("525") < cost) return Enc(p, new { error = "not enough quick tickets" });
                        AddItem(p, 525, -cost);
                    }
                    qact["end_time"] = Now.ToString();
                    p.Extra["auto_missions"] = new JArray(qacts).ToString(Formatting.None);
                    goto case "automission/finishautomission";
                }
                case "automission/finishautomission":
                case "automission/stopautomission":
                {
                    int missionId = (int)L(b, "auto_mission_id", "mission_id");
                    var acts = AutoMissions(p);
                    var act = acts.FirstOrDefault(a => a.Value<int>("auto_mission_id") == missionId) ?? acts.FirstOrDefault();
                    if (act == null) return Enc(p, new { add_gun_exp = new List<object>(), free_exp = 0, add_user_exp = "0", add_prize = new List<object>(), favor_change = new Dictionary<string, int>(), success_number = 0 });
                    missionId = act.Value<int>("auto_mission_id");
                    var row = Tables.Get("auto_mission").Values.FirstOrDefault(r => r.Int("mission_id") == missionId);
                    int number = act.Value<int>("number");
                    bool stopped = op.EndsWith("stopautomission", StringComparison.OrdinalIgnoreCase) && act.Value<long>("end_time") > Now;
                    int success = stopped ? 0 : number;

                    var teamIds = act.Value<string>("team_ids")!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => int.TryParse(t, out int v) ? v : 0).ToHashSet();
                    var gunExp = new List<object>();
                    var favor = new Dictionary<string, int>();
                    int exp = row.Int("experience", 100) * success;
                    foreach (var g in p.Guns.Where(g => g.TeamId > 0 && teamIds.Contains(g.TeamId)))
                    {
                        if (success > 0) AddGunExp(g, exp);
                        gunExp.Add(new { gun_with_user_id = g.Id.ToString(), gun_exp = exp.ToString(), gun_life = g.Life.ToString() });
                        favor[g.Id.ToString()] = 0;
                    }
                    int userExp = success * Math.Max(1, row.Int("experience", 100) / 10);
                    p.Experience += userExp;

                    var prizes = new List<object>();
                    var pool = (row?["gun_n_pool"]?.ToString() ?? "").Split(',').Select(s => int.TryParse(s, out int v) ? v : 0).Where(v => v > 0 && Tables.Row("gun", v) != null).ToList();
                    for (int i = 0; i < success * row.Int("get_gun_num"); i++)
                        if (pool.Count > 0 && _rng.NextDouble() < 0.5)
                        {
                            var ng = GiveGun(p, pool[_rng.Next(pool.Count)]);
                            prizes.Add(new { gun_with_user_id = ng.Id.ToString(), gun_id = ng.GunId.ToString() });
                        }

                    acts.Remove(act);
                    p.Extra["auto_missions"] = new JArray(acts).ToString(Formatting.None);
                    if (success > 0) Count(p, "auto_battle", success);
                    return Enc(p, new
                    {
                        add_gun_exp = gunExp,
                        free_exp = 0,
                        add_user_exp = userExp.ToString(),
                        add_prize = prizes,
                        favor_change = favor,
                        success_number = success
                    });
                }
            }
            return null;
        }

        private static List<JObject> AutoMissions(PlayerAccount p)
        {
            try
            {
                var raw = p.Extra.GetValueOrDefault("auto_missions");
                return string.IsNullOrEmpty(raw) ? new() : JArray.Parse(raw).OfType<JObject>().ToList();
            }
            catch { return new(); }
        }

        /// <summary>auto_mission_act_info for Index/index: the official server sends a list (null when there are none).</summary>
        private static object? AutoMissionActInfo(PlayerAccount p)
        {
            var acts = AutoMissions(p);
            return acts.Count == 0 ? null : new JArray(acts);
        }
    }
}
