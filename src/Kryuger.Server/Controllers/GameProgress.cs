using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>Logistics, mission victory, experience, prizes and quest rewards.</summary>
    public partial class GameController
    {
        // ---------- counters (daily / weekly / achievement progress) ----------
        private static void Count(PlayerAccount p, string type, int n = 1)
        {
            foreach (var scope in new[] { "d", "w", "a" })
            {
                string k = $"cnt_{scope}_{type}";
                p.Extra[k] = ((int.TryParse(p.Extra.GetValueOrDefault(k), out int v) ? v : 0) + n).ToString();
            }
        }

        private static int Counter(PlayerAccount p, string scope, string type) =>
            int.TryParse(p.Extra.GetValueOrDefault($"cnt_{scope}_{type}"), out int v) ? v : 0;

        /// <summary>Resets daily (and weekly on Monday) counters at the server's day boundary (UTC-8).</summary>
        private static void RollPeriods(PlayerAccount p)
        {
            var local = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-8));
            string day = local.ToString("yyyyMMdd");
            if (p.Extra.GetValueOrDefault("period_day") != day)
            {
                foreach (var k in p.Extra.Keys.Where(k => k.StartsWith("cnt_d_") || k.StartsWith("claim_daily_")).ToList()) p.Extra.Remove(k);
                p.Extra["period_day"] = day;
            }
            int diff = ((int)local.DayOfWeek + 6) % 7;
            string week = local.Date.AddDays(-diff).ToString("yyyyMMdd");
            if (p.Extra.GetValueOrDefault("period_week") != week)
            {
                foreach (var k in p.Extra.Keys.Where(k => k.StartsWith("cnt_w_") || k.StartsWith("claim_weekly_")).ToList()) p.Extra.Remove(k);
                p.Extra["period_week"] = week;
            }
        }

        // ---------- prize engine ----------
        private Dictionary<string, object> GrantRow(PlayerAccount p, JObject? row, double mult = 1.0)
        {
            var got = new Dictionary<string, object> { ["mp"] = 0, ["ammo"] = 0, ["mre"] = 0, ["part"] = 0, ["gem"] = 0, ["core"] = 0, ["user_exp"] = 0 };
            if (row == null) return got;
            int R(string k) => (int)Math.Round(row.Int(k) * mult);
            int mp = R("mp"), ammo = R("ammo"), mre = R("mre"), part = R("part"), gem = row.Int("gem"), core = row.Int("core"), exp = row.Int("user_exp");
            p.Mp += mp; p.Ammo += ammo; p.Mre += mre; p.Part += part; p.Gem += gem;
            if (core > 0) AddItem(p, 41, core);
            if (exp > 0) p.Experience += exp;
            got["mp"] = mp; got["ammo"] = ammo; got["mre"] = mre; got["part"] = part; got["gem"] = gem; got["core"] = core; got["user_exp"] = exp;

            var items = new List<object>();
            foreach (var pair in (row["item_ids"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('-');
                if (int.TryParse(kv[0], out int id) && id > 0)
                {
                    int n = kv.Length > 1 && int.TryParse(kv[1], out int c) ? c : 1;
                    AddItem(p, id, n); items.Add(new { item_id = id.ToString(), number = n.ToString() });
                }
            }
            got["item"] = items;

            var guns = new List<object>();
            foreach (var g in (row["gun_id"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(g.Split('-')[0], out int gid) && gid > 0)
                {
                    var ng = GiveGun(p, gid); guns.Add(new { gun_with_user_id = ng.Id.ToString(), gun_id = gid.ToString() });
                }
            got["gun"] = guns;

            var equips = new List<object>();
            foreach (var e in (row["equip_ids"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = e.Split('-');
                if (int.TryParse(kv[0], out int eid) && eid > 0)
                    for (int i = 0, n = kv.Length > 1 && int.TryParse(kv[1], out int c) ? c : 1; i < n; i++)
                        equips.Add(EquipJson(p, GiveEquip(p, eid)));
            }
            got["equip"] = equips;

            foreach (var c in (row["coins"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = c.Split('-'); // coin1..3 are kept as items 101..103 here
                if (kv.Length == 2 && int.TryParse(kv[0], out int ci) && int.TryParse(kv[1], out int cn)) AddItem(p, 100 + ci, cn);
            }
            int skin = row.Int("skin");
            if (skin > 0 && !p.Skins.Contains(skin)) p.Skins.Add(skin);

            foreach (var gPart in (row["gift"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = gPart.Split('-', ':');
                if (kv.Length == 2 && int.TryParse(kv[0], out int gid) && int.TryParse(kv[1], out int cnt) && gid > 0)
                    p.Gifts[gid.ToString()] = (p.Gifts.TryGetValue(gid.ToString(), out int cur) ? cur : 0) + cnt;
            }

            foreach (var fPart in (row["fairy_ids"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = fPart.Split('-');
                if (int.TryParse(kv[0], out int fid) && fid > 0)
                {
                    int n = kv.Length > 1 && int.TryParse(kv[1], out int c) ? c : 1;
                    for (int i = 0; i < n; i++)
                        p.Fairies.Add(new PlayerFairy { Id = NextFairyId(p), FairyId = fid, PassiveSkill = 0 });
                }
            }

            string rawUniform = row["commander_uniform"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(rawUniform))
            {
                var ownedUni = (p.Extra.GetValueOrDefault("commander_uniforms") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                foreach (var u in rawUniform.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrWhiteSpace(u)) ownedUni.Add(u.Trim());
                p.Extra["commander_uniforms"] = string.Join(',', ownedUni);
            }

            int prizeId = row.Int("prize_id");
            if (prizeId > 0 && row != Tables.Row("prize", prizeId))
                foreach (var kv in GrantRow(p, Tables.Row("prize", prizeId))) got["prize_" + kv.Key] = kv.Value;
            return got;
        }

        // ---------- experience ----------
        private static int ExpToNext(int level) =>
            Tables.Row("gun_exp_info", level) is JObject r ? r.Int("exp") : level * 100;

        /// <summary>Cumulative exp for the doll's level plus progress inside it (what the client expects in gun_exp).</summary>
        private static long TotalGunExp(PlayerGun g)
        {
            long total = 0;
            for (int lv = 1; lv < g.GunLevel; lv++) total += ExpToNext(lv);
            return total + Math.Max(0, g.GunExp);
        }

        /// <summary>Level cap: 100, raised by each neural upgrade stage to 110, 115 and 120.</summary>
        internal static int GunLevelCap(PlayerGun g) => g.IfModification switch { <= 0 => 100, 1 => 110, 2 => 115, _ => 120 };

        internal static void AddGunExp(PlayerGun g, long exp)
        {
            int cap = GunLevelCap(g);
            g.GunExp += exp;
            while (g.GunLevel < cap)
            {
                int need = ExpToNext(g.GunLevel);
                if (need <= 0 || g.GunExp < need) break;
                g.GunExp -= need; g.GunLevel++;
            }
            if (g.GunLevel >= cap) g.GunExp = 0;
        }

        /// <summary>Missions unlocked when `missionId` is cleared (next sub-mission, or next chapter + emergency/night on chapter clear).</summary>
        private static List<int> NextMissionsAfterClear(int missionId)
        {
            var unlocked = new List<int>();
            var row = Tables.Row("mission", missionId);
            if (row == null) return unlocked;

            int c = row.Int("campaign"), sub = row.Int("sub"), emerg = row.Int("if_emergency"), dup = row.Int("duplicate_type");
            var pool = Tables.Get("mission").Values
                .Where(r => r.Int("endless_mode") == 0 && r.Int("is_hide") == 0)
                .ToList();

            var nextSub = pool.FirstOrDefault(r =>
                r.Int("campaign") == c && r.Int("if_emergency") == emerg && r.Int("duplicate_type") == dup && r.Int("sub") == sub + 1);
            if (nextSub != null)
            {
                unlocked.Add(nextSub.Int("id"));
                return unlocked;
            }

            if (emerg == 0 && dup == 0 && c >= 0)
            {
                var nextChap = pool.FirstOrDefault(r => r.Int("campaign") == c + 1 && r.Int("if_emergency") == 0 && r.Int("duplicate_type") == 0 && r.Int("sub") == 1);
                if (nextChap != null) unlocked.Add(nextChap.Int("id"));
                var emergChap = pool.FirstOrDefault(r => r.Int("campaign") == c && r.Int("if_emergency") == 1 && r.Int("duplicate_type") == 0 && r.Int("sub") == 1);
                if (emergChap != null) unlocked.Add(emergChap.Int("id"));
                var nightChap = pool.FirstOrDefault(r => r.Int("campaign") == c && r.Int("if_emergency") == 3 && r.Int("duplicate_type") == 0 && r.Int("sub") == 1);
                if (nightChap != null) unlocked.Add(nightChap.Int("id"));
            }
            return unlocked;
        }

        // ---------- mission victory ----------
        private Dictionary<string, object> CompleteMission(PlayerAccount p)
        {
            var m = p.ActiveMission!;
            var row = Tables.Row("mission", m.MissionId);
            int expectTurn = row.Int("expect_turn", 99);
            int rank = m.Turn <= expectTurn ? 5 : m.Turn <= expectTurn + 2 ? 4 : 3;

            string key = m.MissionId.ToString();
            if (!p.Missions.TryGetValue(key, out var rec)) p.Missions[key] = rec = new MissionRecord { MissionId = m.MissionId, BestRank = rank, Medal4 = 0 };
            rec.BestRank = Math.Max(rec.BestRank, rank);
            if (rank == 5) rec.Medal4 = 1;
            rec.Counter++;
            rec.WinCounter++;

            int expParam = Math.Max(1, row.Int("exp_parameter", 10));
            int userExp = expParam * 10;
            p.Experience += userExp;

            // reward doll from the mission drop pool (spot random_get), if any
            var rewardGuns = new List<object>();
            var drop = MasterData.MissionSpotsDrop(m.MissionId);
            if (drop.Count > 0 && _rng.NextDouble() < 0.5)
            {
                var ng = GiveGun(p, drop[_rng.Next(drop.Count)]);
                rewardGuns.Add(new { gun_with_user_id = ng.Id.ToString(), gun_id = ng.GunId.ToString() });
            }

            Count(p, "win_normal"); Count(p, "mission_win"); if (rank == 5) Count(p, "s_win");
            int turn = m.Turn;
            int kills = int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int k) ? k : 0;
            ResetMissionCounters(p);
            // unlock the next mission(s) of the campaign (next sub-mission, or next chapter + emergency/night)
            var open = new Dictionary<string, int>();
            foreach (int nextId in NextMissionsAfterClear(m.MissionId))
            {
                open[nextId.ToString()] = 1;
                if (!p.Missions.ContainsKey(nextId.ToString()))
                    p.Missions[nextId.ToString()] = new MissionRecord { MissionId = nextId, BestRank = 0, Medal4 = 0 };
            }
            p.ActiveMission = null;

            // Official shape (captured Mission/startTurn + hoxy SMissionEndTurn samples)
            return new Dictionary<string, object>
            {
                ["win_type_ids"] = new List<object>(),
                ["rank"] = rank.ToString(),
                ["medal4"] = rec.Medal4,
                ["open"] = open,
                ["user_exp"] = userExp.ToString(),
                ["reward_gun"] = rewardGuns,
                ["mission_info"] = new Dictionary<string, object>
                {
                    ["turn"] = turn.ToString(), ["enemydie_num"] = kills.ToString(), ["enemydie_num_killbyfriend"] = "0",
                    ["gundie_num"] = "0", ["mysquad_die_num"] = "0", ["sangvisdie_num"] = "0"
                },
                ["mica_client_log"] = new { mid = m.MissionId, uid = p.Id, type = "type5_score" }
            };
        }

        private IActionResult? TryHandleProgress(string op, JObject? b, PlayerAccount p)
        {
            RollPeriods(p);
            string o = op.ToLowerInvariant();
            switch (o)
            {
                // ===== Logistics =====
                case "operation/startoperation":
                {
                    int opId = (int)L(b, "operation_id"); int team = (int)L(b, "team_id");
                    var row = Tables.Row("operation_info", opId);
                    p.ActiveOperations.RemoveAll(x => x.TeamId == team || x.OperationId == opId);
                    p.ActiveOperations.Add(new OperationAct { OperationId = opId, TeamId = team, StartTime = Now, Duration = row.Int("duration", 3600) });
                    return Ok1(p);
                }
                case "operation/finishoperation":
                {
                    int opId = (int)L(b, "operation_id");
                    var act = p.ActiveOperations.FirstOrDefault(x => x.OperationId == opId);
                    var row = Tables.Row("operation_info", opId);
                    bool big = _rng.NextDouble() < 0.3;
                    var got = GrantRow(p, row, big ? 1.5 : 1.0);
                    var pool = (row?["item_pool"]?.ToString() ?? "").Split(',').Select(s => int.TryParse(s, out int v) ? v : 0).Where(v => v > 0).ToList();
                    int itemId = 0;
                    if (pool.Count > 0 && _rng.NextDouble() < 0.5) { itemId = pool[_rng.Next(pool.Count)]; AddItem(p, itemId, 1); }
                    if (act != null) p.ActiveOperations.Remove(act);
                    Count(p, "operation");
                    return Enc(p, new[] { new { item_id = itemId > 0 ? itemId.ToString() : "", big_success = big ? 1 : 0 } });
                }

                // ===== Missions =====
                case "mission/skip":
                {
                    foreach (int skipId in Ids(b?["mission_skip_ids"] ?? b?["id"]))
                    {
                        var row = Tables.Row("mission_skip", skipId);
                        if (row == null) continue;
                        int price = row.Int("price");
                        if (price > 0) p.Gem = Math.Max(0, p.Gem - price);
                        foreach (long mid in Ids(row["mission"]))
                        {
                            string k = mid.ToString();
                            if (!p.Missions.TryGetValue(k, out var rec))
                                p.Missions[k] = rec = new MissionRecord { MissionId = (int)mid };
                            rec.BestRank = Math.Max(rec.BestRank, 5);
                            rec.Medal4 = 1;
                            rec.Counter = Math.Max(rec.Counter, 1);
                            rec.WinCounter = Math.Max(rec.WinCounter, 1);
                        }
                        foreach (long mid in Ids(row["mission_unlock"]))
                        {
                            string k = mid.ToString();
                            if (!p.Missions.ContainsKey(k))
                                p.Missions[k] = new MissionRecord { MissionId = (int)mid, BestRank = 0, Medal4 = 0 };
                        }
                    }
                    return Ok1(p);
                }
                case "mission/battlefinish":
                {
                    // persist hp the client reports for the battle
                    foreach (var gt in (b?["guns"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var g = p.Guns.FirstOrDefault(x => x.Id == L(gt, "id", "gun_with_user_id"));
                        if (g != null && gt["life"] != null) g.Life = (int)L(gt, "life");
                    }
                    Count(p, "win_enemy");
                    var m = p.ActiveMission;
                    if (m == null) return null;
                    long spotId = L(b, "spot_id");
                    var spot = spotId > 0 ? m.Spots.FirstOrDefault(s => s.SpotId == spotId) : m.Spots.FirstOrDefault(s => s.TeamId > 0 && s.EnemyTeamId > 0);
                    var died = new List<string>();
                    bool enemyDied = b?["if_enemy_die"] == null || b["if_enemy_die"]!.Type != JTokenType.Boolean || (bool)b["if_enemy_die"]!;
                    if (spot != null && spot.EnemyInstanceId > 0 && enemyDied)
                    {
                        RecordKill(p, spot.EnemyTeamId);
                        died.Add(spot.EnemyInstanceId.ToString()); spot.EnemyTeamId = 0; spot.EnemyInstanceId = 0; // captured at endTurn (official battleFinish change_belong is empty)
                    }
                    bool hostageDied = spot != null && AfterBattle(spot, b, enemyDied);
                    AddAllyChanges(p, m, section0: null);
                    if (spot != null && enemyDied) DeliverHostage(p, spot);
                    if (spot != null && !enemyDied && spot.TeamId > 0 && spot.EnemyTeamId > 0)
                    {
                        // the echelon lost the fight: it is pushed out and the enemy holds the node
                        spot.TeamId = 0; spot.Belong = 2;
                    }
                    var section = BuildMissionSectionPayload(m, died);
                    AddAllyChanges(p, m, section);
                    // battle rewards in the official shape: exp for the echelon that fought, remaining hp, rank, favor
                    var mrow = Tables.Row("mission", m.MissionId);
                    int expParam = Math.Max(1, mrow.Int("exp_parameter", 10));
                    int team = spot?.TeamId ?? 0;
                    var fought = p.Guns.Where(g => team > 0 && g.TeamId == team).ToList();
                    var gunExp = new List<object>(); var gunLife = new List<object>(); var favor = new Dictionary<string, int>();
                    foreach (var g in fought)
                    {
                        int e = enemyDied ? expParam * 3 * (g.Location == 1 ? 2 : 1) : 0;
                        if (e > 0) AddGunExp(g, e);
                        gunExp.Add(new { gun_with_user_id = g.Id.ToString(), exp = e.ToString() });
                        gunLife.Add(new { gun_with_user_id = g.Id.ToString(), life = Math.Min(g.Life, MaxLife(g)).ToString() });
                        favor[g.Id.ToString()] = 0;
                    }
                    int battleUserExp = enemyDied ? expParam : 0;
                    p.Experience += battleUserExp;
                    section["free_exp"] = 0;
                    section["gun_exp"] = gunExp;
                    section["gun_life"] = gunLife;
                    section["user_exp"] = battleUserExp.ToString();
                    section["squad_exp"] = new List<object>();
                    section["battle_rank"] = enemyDied ? "5" : "1";
                    section["seed"] = _rng.Next(1000, 9999);
                    section["favor_change"] = favor;
                    if (hostageDied)
                    {
                        section["mission_lose_result"] = LoseResult(p, m);
                        Count(p, "mission_lose");
                        ResetMissionCounters(p);
                        p.ActiveMission = null;
                    }
                    else if (MissionWon(p, m)) section["mission_win_result"] = CompleteMission(p);
                    return Enc(p, section);
                }
                case "mission/teammove":
                case "mission/endturn":
                {
                    if (p.ActiveMission == null) return null;
                    int enemyTeamOnDest = 0;
                    int enemyInstOnDest = 0;
                    if (o == "mission/teammove")
                    {
                        var to = p.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == L(b, "to_spot_id"));
                        var from = p.ActiveMission.Spots.FirstOrDefault(s => s.SpotId == L(b, "from_spot_id"));
                        if (to == null || from == null) return null;
                        to.TeamId = from.TeamId; from.TeamId = 0;
                        // Official: the node is captured when the player turn ends (endTurn change_belong1), not on arrival.
                        if (to.EnemyTeamId != 0) { enemyTeamOnDest = to.EnemyTeamId; enemyInstOnDest = to.EnemyInstanceId; }
                        MoveHostage(p, from, to);
                        if (p.ActiveMission.Ap > 0) p.ActiveMission.Ap--;
                    }
                    var section = BuildMissionSectionPayload(p.ActiveMission);
                    AddAllyChanges(p, p.ActiveMission, section);
                    if (o == "mission/endturn")
                    {
                        var captured = new Dictionary<string, int>();
                        foreach (var sp in p.ActiveMission.Spots.Where(sp => sp.TeamId > 0 && sp.EnemyTeamId == 0 && sp.Belong != 1))
                        {
                            sp.Belong = 1;
                            captured[sp.SpotId.ToString()] = 1;
                        }
                        section["change_belong1"] = captured.Count > 0 ? captured : (object)new List<object>();
                        var bchg1 = UpdateBuildings(p.ActiveMission, enemyTurn: false);
                        if (bchg1.Count > 0) section["building_change_belong1"] = bchg1;
                        AddBuildingDefenders(p.ActiveMission, bchg1, section);
                    }
                    if (enemyTeamOnDest > 0)
                    {
                        section["enemy_team_id"] = enemyTeamOnDest.ToString();
                        section["enemy_instance_id"] = enemyInstOnDest.ToString();
                    }
                    if (!MissionWon(p, p.ActiveMission)) return Enc(p, section);
                    section["mission_win_result"] = CompleteMission(p);
                    return Enc(p, section);
                }
                case "mission/abortmission":
                    Count(p, "mission_abort");
                    ResetMissionCounters(p);
                    return null;
                case "mission/startmission":
                    ResetMissionCounters(p);
                    p.Extra.Remove("enemy_origin");
                    return null; // map setup is built by GameController.HandleStartMission
                case "mission/startenemyturn":
                {
                    var m = p.ActiveMission;
                    if (m == null) return null;
                    var (moves, changed, lost) = RunEnemyTurn(p, m);
                    var section = BuildMissionSectionPayload(m);
                    section["enemy_move"] = VisibleMoves(m, moves);
                    section["ally_move"] = VisibleMoves(m, RunAllyTurn(m));
                    section["change_belong2"] = changed.Count > 0 ? changed : (object)new List<object>();
                    var bchg2 = UpdateBuildings(m, enemyTurn: true);
                    section["building_change_belong2"] = bchg2.Count > 0 ? bchg2 : (object)new List<object>();
                    AddBuildingDefenders(m, bchg2, section);
                    AddAllyChanges(p, m, section);
                    if (lost)
                    {
                        int kills = int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int kk) ? kk : 0;
                        section["mission_lose_result"] = new Dictionary<string, object>
                        {
                            ["win_type_ids"] = new List<object>(), ["turn"] = m.Turn.ToString(), ["enemydie_num"] = kills.ToString(),
                            ["enemydie_num_killbyfriend"] = "0", ["gundie_num"] = "0", ["mysquad_die_num"] = "0", ["sangvisdie_num"] = "0",
                            ["mica_client_log"] = new { mid = m.MissionId, uid = p.Id, type = "type5_score" }
                        };
                        Count(p, "mission_lose");
                        ResetMissionCounters(p);
                        p.ActiveMission = null;
                    }
                    return Enc(p, section);
                }
                case "mission/startturn":
                {
                    // new round: echelons hold their spots; "survive N rounds" / turn-limit missions are won here
                    var m = p.ActiveMission;
                    if (m == null) return null;
                    m.Turn += 1;
                    var turnCaptured = new Dictionary<string, int>();
                    foreach (var sp in m.Spots.Where(sp => sp.TeamId > 0 && sp.EnemyTeamId == 0 && sp.Belong != 1))
                    {
                        sp.Belong = 1;
                        turnCaptured[sp.SpotId.ToString()] = 1;
                    }
                    m.Ap = ComputeTurnAp(m);
                    var section = BuildMissionSectionPayload(m);
                    // Official startTurn reports captured nodes as change_belong {"spot":1}.
                    section["change_belong"] = turnCaptured.Count > 0 ? turnCaptured : (object)new List<object>();
                    AddAllyChanges(p, m, section);
                    if (MissionWon(p, m)) section["mission_win_result"] = CompleteMission(p);
                    return Enc(p, section);
                }
            }

            // ===== Quest / achievement rewards (tolerant of the exact op name) =====
            bool isQuest = o.Contains("quest") || o.Contains("achievement") || o.Contains("daily") || o.Contains("weekly") || o.Contains("career");
            bool isClaim = o.Contains("prize") || o.Contains("reward") || o.Contains("finish") || o.Contains("receive") || o.Contains("get")
                        || o.Contains("accomplish") || o.Contains("claim") || o.Contains("complete");
            if (isQuest && isClaim && !o.EndsWith("index/quest"))
            {
                string table = o.Contains("weekly") ? "weekly" : o.Contains("achievement") ? "achievement" : o.Contains("career") ? "career_quest" : "daily";
                var ids = Ids(b?["_array"] ?? b?["quest_ids"] ?? b?["ids"]);
                long one = L(b, "quest_id", "id", "identity", "achievement_id", "daily_id", "weekly_id");
                if (one > 0) ids.Add(one);
                var got = new List<object>();
                foreach (int id in ids.Distinct().Select(x => (int)x))
                {
                    string ck = $"claim_{table}_{id}";
                    if (p.Extra.ContainsKey(ck)) continue;
                    var row = Tables.Get(table).Values.FirstOrDefault(r => r.Int("identity") == id || r.Int("id") == id);
                    if (row == null) continue;
                    p.Extra[ck] = Now.ToString();
                    got.Add(GrantRow(p, row));
                    Count(p, table + "_complete");
                }
                return Enc(p, new { prize = got });
            }
            return null;
        }
    }

    internal static class MasterData
    {
        public static List<int> MissionSpotsDrop(int missionId)
        {
            var list = new List<int>();
            if (Kryuger.Data.MasterDataManager.Instance.MissionSpots.TryGetValue(missionId, out var spots))
                foreach (var s in spots)
                    foreach (var part in (s.RandomGet ?? "").Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(part.Split('-')[0], out int gid) && Tables.Row("gun", gid) != null) list.Add(gid);
            return list;
        }
    }
}

namespace Kryuger.Server.Controllers
{
    public partial class GameController
    {
        /// <summary>
        /// Diagnostic switch: if data/index_gun_limit.txt holds a number N, only the first N dolls
        /// (team members first) are sent in Index/index. Nothing is deleted from the database.
        /// </summary>
        private static IEnumerable<Kryuger.Common.Models.PlayerGun> IndexGuns(Kryuger.Common.Models.PlayerAccount p)
        {
            string f = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "index_gun_limit.txt");
            if (System.IO.File.Exists(f) && int.TryParse(System.IO.File.ReadAllText(f).Trim(), out int n) && n > 0)
                return p.Guns.OrderByDescending(g => g.TeamId > 0).ThenBy(g => g.Id).Take(n);
            return p.Guns;
        }
    }
}

namespace Kryuger.Server.Controllers
{
    public partial class GameController
    {
        private static Newtonsoft.Json.Linq.JObject? _indexTemplate;

        /// <summary>
        /// Tutorial progress. The current client has 247+ tutorial steps; anything missing from the array counts
        /// as "not done" and locks features. Pads the stored array with completed (1) steps.
        /// </summary>
        private static string CompletedGuide(string? stored)
        {
            const int steps = 247; // length of the official tutorial array for client 30810
            var course = new List<int>();
            try
            {
                if (!string.IsNullOrEmpty(stored) && Newtonsoft.Json.Linq.JObject.Parse(stored)["course"] is Newtonsoft.Json.Linq.JArray arr)
                    course = arr.Select(t => (int)t).ToList();
            }
            catch { }
            for (int i = 0; i < course.Count; i++) course[i] = 1;
            while (course.Count < steps) course.Add(1);
            return "{\"course\":[" + string.Join(",", course) + "]}";
        }

        /// <summary>
        /// Fills game-configuration blocks that the private server does not generate (events, mission events,
        /// dorm establishments, furniture collection, departments) from an official Index/index capture stored
        /// in data/index_template.json. The current client's tables reference these; leaving them empty makes
        /// the client fail to read the login data ("Network connection failure ... CacheData").
        /// </summary>
        private static void ApplyIndexTemplate(Dictionary<string, object?> response, Kryuger.Common.Models.PlayerAccount p)
        {
            if (_indexTemplate == null)
            {
                string f = IndexTemplatePath();
                if (!System.IO.File.Exists(f)) return;
                _indexTemplate = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(f));
            }
            string officialUid = _indexTemplate["user_info"]?["user_id"]?.ToString() ?? "";
            foreach (var key in new[] { "event_info", "mission_event_info", "outhouse_establish_info", "furniture_collect_info", "org_department_info", "authentication_url" })
            {
                var tv = _indexTemplate[key];
                if (tv == null || tv.Type == Newtonsoft.Json.Linq.JTokenType.Null) continue;
                bool empty = !response.TryGetValue(key, out var cur) || cur == null
                    || (cur is System.Collections.ICollection c && c.Count == 0)
                    || (cur is string s && s.Length == 0);
                if (!empty) continue;
                string json = tv.ToString(Newtonsoft.Json.Formatting.None);
                if (officialUid.Length > 0) json = json.Replace("\"" + officialUid + "\"", "\"" + p.Id + "\"").Replace(":" + officialUid + ",", ":" + p.Id + ",");
                response[key] = Newtonsoft.Json.Linq.JToken.Parse(json);
            }
        }
    }
}
