using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kryuger.Common.Crypto;
using Kryuger.Common.Models;
using Kryuger.Data;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>Generic access to any stc/*.json table, indexed by "id".</summary>
    public static class Tables
    {
        public static string DataPath { get; set; } = "";
        private static readonly ConcurrentDictionary<string, Dictionary<int, JObject>> _cache = new();

        public static Dictionary<int, JObject> Get(string name) => _cache.GetOrAdd(name, n =>
        {
            var result = new Dictionary<int, JObject>();
            foreach (var dir in new[] { "stc", "catchdata" })
            {
                string f = Path.Combine(DataPath, dir, n + ".json");
                if (!File.Exists(f)) continue;
                var tok = JToken.Parse(File.ReadAllText(f));
                IEnumerable<JToken> rows = tok is JArray a ? a : tok is JObject o && o.Properties().FirstOrDefault()?.Value is JArray inner ? inner : Enumerable.Empty<JToken>();
                foreach (var r in rows.OfType<JObject>())
                    if (int.TryParse((r["id"] ?? r["identity"] ?? r["lv"])?.ToString(), out int id)) result[id] = r;
                break;
            }
            return result;
        });

        public static JObject? Row(string table, int id) => Get(table).TryGetValue(id, out var r) ? r : null;
        public static int Int(this JObject? row, string key, int def = 0) =>
            row != null && int.TryParse(row[key]?.ToString(), out int v) ? v : def;

        public static double Dbl(this JObject? row, string key, double def = 0) =>
            row != null && double.TryParse(row[key]?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

        /// <summary>
        /// Computes the official per-stat enhancement cap (pow, hit, dodge, rate) for a doll at a given level
        /// using gun, gun_type_info, and game_config_info growth constants.
        /// </summary>
        public static (int Pow, int Hit, int Dodge, int Rate) GetMaxEnhancement(int gunId, int level)
        {
            int lv = Math.Max(1, level);
            int baseLv = Math.Min(lv, 100) - 1;
            int after100 = Math.Max(0, lv - 100);

            double fPow = 0.242 * baseLv + 0.060 * after100;
            double fHit = 0.303 * baseLv + 0.075 * after100;
            double fDodge = 0.303 * baseLv + 0.075 * after100;
            double fRate = 0.181 * baseLv + 0.022 * after100;

            var g = Row("gun", gunId);
            int type = g.Int("type", 4);
            double eat = g.Int("eat_ratio", 100) / 100.0;
            var t = Row("gun_type_info", type);

            int Cap(double factor, string attrKey, string ratioKey) =>
                (int)Math.Ceiling(factor * t.Dbl(attrKey, 1.0) * (g.Int(ratioKey, 100) / 100.0) * eat);

            return (
                Cap(fPow, "basic_attribute_pow", "ratio_pow"),
                Cap(fHit, "basic_attribute_hit", "ratio_hit"),
                Cap(fDodge, "basic_attribute_dodge", "ratio_dodge"),
                Cap(fRate, "basic_attribute_rate", "ratio_rate")
            );
        }
    }

    public partial class GameController
    {
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static readonly Random _rng = new();

        // ---------- request helpers ----------
        private static long L(JObject? b, params string[] keys)
        {
            foreach (var k in keys)
                if (b?[k] != null && long.TryParse(b[k]!.ToString(), out long v)) return v;
            return 0;
        }

        private static List<long> Ids(JToken? t)
        {
            if (t == null) return new();
            if (t is JArray a) return a.Select(x => long.TryParse((x is JObject o ? (o["id"] ?? o.Properties().First().Value) : x).ToString(), out long v) ? v : 0).Where(v => v > 0).ToList();
            if (t is JObject obj) return obj.Properties().Select(p => long.TryParse(p.Value.ToString(), out long v) ? v : 0).Where(v => v > 0).ToList();
            return t.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out long v) ? v : 0).Where(v => v > 0).ToList();
        }

        private IActionResult Ok1(PlayerAccount p) { _db.SavePlayer(p); return PlainSuccess(); }
        private IActionResult Enc(PlayerAccount p, object data) { _db.SavePlayer(p); return EncryptedResponse(data, p.SignKey); }

        // ---------- resource / item engine ----------
        private static void Spend(PlayerAccount p, int mp, int ammo, int mre, int part)
        {
            p.Mp = Math.Max(0, p.Mp - mp); p.Ammo = Math.Max(0, p.Ammo - ammo);
            p.Mre = Math.Max(0, p.Mre - mre); p.Part = Math.Max(0, p.Part - part);
        }

        private static void AddItem(PlayerAccount p, int itemId, int n)
        {
            string k = itemId.ToString();
            p.Items[k] = Math.Max(0, (p.Items.TryGetValue(k, out int c) ? c : 0) + n);
        }

        private static long NextGunId(PlayerAccount p) => p.Guns.Count > 0 ? p.Guns.Max(g => g.Id) + 1 : 1;
        private static long NextEquipId(PlayerAccount p) => p.Equips.Count > 0 ? p.Equips.Max(e => e.Id) + 1 : 1;
        private static long NextFairyId(PlayerAccount p) => p.Fairies.Count > 0 ? p.Fairies.Max(e => e.Id) + 1 : 1;

        private PlayerGun GiveGun(PlayerAccount p, int gunId)
        {
            var g = new PlayerGun
            {
                Id = NextGunId(p), UserId = p.Id, GunId = gunId, GunLevel = 1, GunExp = 0, Number = 1,
                Life = 1000, Favor = 500000, MaxFavor = 1000000, FavorTopLimit = 1000000,
                Skill1 = 1, Skill2 = 0, TeamId = 0, Location = 1, Position = 0, IsLocked = 0,
                Pow = 0, Hit = 0, Dodge = 0, Rate = 0
            };
            p.Guns.Add(g);
            return g;
        }

        private PlayerEquip GiveEquip(PlayerAccount p, int equipId)
        {
            var e = new PlayerEquip { Id = NextEquipId(p), UserId = p.Id, EquipId = equipId, EquipLevel = 0 };
            var row = Tables.Row("equip", equipId);
            if (row != null)
                foreach (var stat in new[] { "pow", "hit", "dodge", "speed", "rate", "critical_harm_rate", "critical_percent", "armor_piercing", "armor", "shield", "damage_amplify", "damage_reduction", "night_view_percent", "bullet_number_up" })
                {
                    var parts = row[stat]?.ToString().Split(',');
                    if (parts?.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
                        e.Stats[stat] = _rng.Next(Math.Min(lo, hi), Math.Max(lo, hi) + 1);
                }
            p.Equips.Add(e);
            return e;
        }

        private static Dictionary<string, object> EquipJson(PlayerAccount p, PlayerEquip e)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = e.Id.ToString(), ["user_id"] = p.Id.ToString(), ["equip_id"] = e.EquipId.ToString(),
                ["equip_exp"] = e.EquipExp.ToString(), ["equip_level"] = e.EquipLevel.ToString(),
                ["gun_with_user_id"] = e.GunId.ToString(), ["is_locked"] = e.IsLocked.ToString(),
                ["adjust_count"] = "0", ["last_adjust"] = "", ["last_pow"] = "", ["last_hit"] = "", ["last_dodge"] = "",
                ["last_speed"] = "", ["last_rate"] = "", ["last_critical_harm_rate"] = "", ["last_critical_percent"] = "",
                ["last_armor_piercing"] = "", ["last_armor"] = "", ["last_shield"] = "", ["last_damage_amplify"] = "",
                ["last_damage_reduction"] = "", ["last_night_view_percent"] = "", ["last_bullet_number_up"] = ""
            };
            foreach (var stat in new[] { "pow", "hit", "dodge", "speed", "rate", "critical_harm_rate", "critical_percent", "armor_piercing", "armor", "shield", "damage_amplify", "damage_reduction", "night_view_percent", "bullet_number_up" })
                d[stat] = (e.Stats.TryGetValue(stat, out int v) ? v : 0).ToString();
            return d;
        }

        private static Dictionary<string, object> FairyJson(PlayerAccount p, PlayerFairy f) => new()
        {
            ["id"] = f.Id.ToString(), ["user_id"] = p.Id.ToString(), ["fairy_id"] = f.FairyId.ToString(),
            ["team_id"] = f.TeamId.ToString(), ["fairy_lv"] = f.Level.ToString(), ["fairy_exp"] = f.Exp.ToString(),
            ["quality_lv"] = f.QualityLevel.ToString(), ["quality_exp"] = "0", ["skill_lv"] = f.SkillLevel.ToString(),
            ["passive_skill"] = f.PassiveSkill.ToString(), ["is_locked"] = f.IsLocked.ToString(),
            ["equip_id"] = "0", ["adjust_count"] = "0", ["last_adjust"] = "0", ["passive_skill_collect"] = f.PassiveSkill.ToString(),
            ["skin"] = f.Skin.ToString(), ["is_store"] = "0"
        };

        // ---------- gacha ----------
        private static int RollEquipOrFairy(int mp, int ammo, int mre, int part, out bool isFairy)
        {
            isFairy = false;
            // Fairy chance grows with heavy inputs (official: fairies appear from heavy equipment production)
            if (mp + ammo + mre + part >= 2000 && _rng.NextDouble() < 0.2)
            {
                var fairies = Tables.Get("fairy").Values.Where(f => f.Int("develop_duration") > 0).ToList();
                if (fairies.Count > 0) { isFairy = true; return fairies[_rng.Next(fairies.Count)].Int("id"); }
            }
            int maxRank = (mp + ammo + mre + part) >= 600 ? 5 : (mp + ammo + mre + part) >= 300 ? 4 : 3;
            double r = _rng.NextDouble();
            int rank = r < 0.6 ? 2 : r < 0.85 ? 3 : r < 0.96 ? 4 : 5;
            rank = Math.Min(rank, maxRank);
            var pool = Tables.Get("equip").Values.Where(e => e.Int("rank") == rank && e.Int("develop_duration") > 0).ToList();
            if (pool.Count == 0) pool = Tables.Get("equip").Values.Where(e => e.Int("develop_duration") > 0).ToList();
            return pool.Count > 0 ? pool[_rng.Next(pool.Count)].Int("id") : 1;
        }

        // ---------- dispatcher ----------
        private IActionResult? TryHandleExtended(string op, JObject? b, PlayerAccount? p)
        {
            if (p == null) return null;
            var progress = TryHandleProgress(op, b, p);
            if (progress != null) return progress;
            var systems = TryHandleSystems(op, b, p);
            if (systems != null) return systems;
            switch (op.ToLowerInvariant())
            {
                // ===== T-Doll production =====
                case "gun/developgun":
                {
                    int slot = (int)L(b, "build_slot"); int mp = (int)L(b, "mp"), ammo = (int)L(b, "ammo"), mre = (int)L(b, "mre"), part = (int)L(b, "part");
                    int inputLevel = (int)L(b, "input_level");
                    Spend(p, mp, ammo, mre, part);
                    AddItem(p, 1, -(inputLevel > 0 ? new[] { 0, 20, 50, 100 }[Math.Min(3, inputLevel)] : 1));
                    var g = MasterDataManager.Instance.RollGFLGacha(mp, ammo, mre, part);
                    int gunId = g?.Id ?? 2;
                    p.DevelopSlots.RemoveAll(x => x.Slot == slot);
                    p.DevelopSlots.Add(new DevelopSlot { Slot = slot, GunId = gunId, StartTime = Now, Duration = g?.DevelopDuration ?? 1200, Mp = mp, Ammo = ammo, Mre = mre, Part = part, InputLevel = inputLevel });
                    Count(p, "develop_gun"); Count(p, "develop_all");
                    return Enc(p, new { gun_id = gunId.ToString() });
                }
                case "gun/developmultigun":
                {
                    int mp = (int)L(b, "mp"), ammo = (int)L(b, "ammo"), mre = (int)L(b, "mre"), part = (int)L(b, "part");
                    int inputLevel = (int)L(b, "input_level");
                    bool quick = L(b, "build_quick") > 0;
                    var slots = Ids(b?["build_slots"] ?? b?["slots"]).Select(x => (int)x).ToList();
                    int multi = (int)L(b, "build_multi");
                    if (slots.Count == 0)
                    {
                        var used = p.DevelopSlots.Select(s => s.Slot).ToHashSet();
                        slots = Enumerable.Range(1, p.MaxBuildSlot * 2).Where(s => s % 2 == 1 && !used.Contains(s)).Take(Math.Max(1, multi)).ToList();
                    }
                    var list = new List<object>();
                    var quickAdds = new List<object>();
                    foreach (int slot in slots)
                    {
                        Spend(p, mp, ammo, mre, part); AddItem(p, 1, -1);
                        var g = MasterDataManager.Instance.RollGFLGacha(mp, ammo, mre, part);
                        int gunId = g?.Id ?? 2;
                        if (quick)
                        {
                            AddItem(p, 3, -1);
                            var given = GiveGun(p, gunId);
                            quickAdds.Add(new { gun_with_user_add = new { gun_with_user_id = given.Id.ToString(), gun_id = given.GunId.ToString() } });
                        }
                        else
                        {
                            p.DevelopSlots.RemoveAll(x => x.Slot == slot);
                            p.DevelopSlots.Add(new DevelopSlot { Slot = slot, GunId = gunId, StartTime = Now, Duration = g?.DevelopDuration ?? 1200, Mp = mp, Ammo = ammo, Mre = mre, Part = part, InputLevel = inputLevel });
                            list.Add(new { id = gunId.ToString(), slot = slot.ToString() });
                        }
                    }
                    Count(p, "develop_gun", Math.Max(list.Count, quickAdds.Count)); Count(p, "develop_all", Math.Max(list.Count, quickAdds.Count));
                    return quick ? Enc(p, quickAdds) : Enc(p, new { gun_ids = list });
                }
                case "gun/quickdevelop":
                {
                    int slot = (int)L(b, "build_slot");
                    var s = p.DevelopSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s != null) { s.Duration = 0; s.StartTime = Now - 1; AddItem(p, 3, -1); }
                    return Ok1(p);
                }
                case "gun/finishdevelop":
                {
                    int slot = (int)L(b, "build_slot");
                    var s = p.DevelopSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s == null) return Enc(p, new { gun_with_user_add = new { } });
                    var g = GiveGun(p, s.GunId);
                    p.DevelopSlots.Remove(s);
                    return Enc(p, new { gun_with_user_add = new { gun_with_user_id = g.Id.ToString(), gun_id = g.GunId.ToString() } });
                }
                case "gun/finishalldevelop":
                {
                    bool costQuick = L(b, "is_cost_item3") > 0;
                    var done = costQuick ? p.DevelopSlots.ToList() : p.DevelopSlots.Where(s => s.StartTime + s.Duration <= Now).ToList();
                    var adds = new List<object>();
                    int cost3 = 0;
                    foreach (var s in done)
                    {
                        if (s.StartTime + s.Duration > Now) { AddItem(p, 3, -1); cost3++; }
                        var g = GiveGun(p, s.GunId); p.DevelopSlots.Remove(s);
                        adds.Add(new { build_slot = s.Slot, gun_with_user_id = g.Id.ToString(), gun_id = g.GunId.ToString() });
                    }
                    return Enc(p, new { gun_with_user_add_list = adds, cost_item3_num = cost3 });
                }

                // ===== Equipment / fairy production =====
                case "equip/develop":
                case "equip/developequip":
                {
                    int slot = (int)L(b, "build_slot"); int mp = (int)L(b, "mp"), ammo = (int)L(b, "ammo"), mre = (int)L(b, "mre"), part = (int)L(b, "part");
                    Spend(p, mp, ammo, mre, part); AddItem(p, 2, -1);
                    int id = RollEquipOrFairy(mp, ammo, mre, part, out bool fairy);
                    Count(p, "develop_equip"); Count(p, "develop_all");
                    int dur = fairy ? Tables.Row("fairy", id).Int("develop_duration", 3600) : Tables.Row("equip", id).Int("develop_duration", 600);
                    p.EquipDevelopSlots.RemoveAll(x => x.Slot == slot);
                    p.EquipDevelopSlots.Add(new DevelopSlot { Slot = slot, EquipId = fairy ? 0 : id, FairyId = fairy ? id : 0, StartTime = Now, Duration = dur, Mp = mp, Ammo = ammo, Mre = mre, Part = part });
                    return Enc(p, fairy ? new { equip_id = "0", fairy_id = id.ToString(), type = "1" } : (object)new { equip_id = id.ToString(), fairy_id = "0", type = "0" });
                }
                case "equip/quickdevelop":
                {
                    int slot = (int)L(b, "build_slot");
                    var s = p.EquipDevelopSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s != null) { s.Duration = 0; s.StartTime = Now - 1; AddItem(p, 3, -1); }
                    return Ok1(p);
                }
                case "equip/finishdevelop":
                {
                    int slot = (int)L(b, "build_slot");
                    var s = p.EquipDevelopSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s == null) return Enc(p, new { });
                    p.EquipDevelopSlots.Remove(s);
                    if (s.FairyId > 0)
                    {
                        var f = new PlayerFairy { Id = NextFairyId(p), FairyId = s.FairyId, PassiveSkill = 0 };
                        p.Fairies.Add(f);
                        return Enc(p, new { fairy_with_user = FairyJson(p, f) });
                    }
                    var e = GiveEquip(p, s.EquipId);
                    return Enc(p, new { equip_with_user = EquipJson(p, e) });
                }
                case "equip/retire":
                case "equip/retireequip":
                {
                    var ids = Ids(b?["equips"] ?? b?["equip_ids"] ?? b?["equip_with_user_ids"] ?? b?["_array"]).ToHashSet();
                    foreach (var e in p.Equips.Where(e => ids.Contains(e.Id)))
                    {
                        var row = Tables.Row("equip", e.EquipId);
                        p.Mp += row.Int("retire_mp"); p.Ammo += row.Int("retire_ammo"); p.Mre += row.Int("retire_mre"); p.Part += row.Int("retire_part");
                    }
                    p.Equips.RemoveAll(e => ids.Contains(e.Id));
                    return Ok1(p);
                }
                case "equip/eat":
                case "equip/eatequip":
                {
                    long target = L(b, "equip_with_user_id", "if_equip_with_user_id");
                    var food = Ids(b?["food"] ?? b?["equips"]).ToHashSet();
                    var e = p.Equips.FirstOrDefault(x => x.Id == target);
                    int addExp = Math.Max(1, food.Count) * 100;
                    if (e != null)
                    {
                        e.EquipExp += addExp;
                        e.EquipLevel = Math.Min(10, e.EquipLevel + Math.Max(1, food.Count / 2));
                    }
                    p.Equips.RemoveAll(x => food.Contains(x.Id));
                    return Enc(p, new { equip_add_exp = addExp, equip_exp = e?.EquipExp.ToString() ?? "0", equip_level = e?.EquipLevel.ToString() ?? "0" });
                }
                case "equip/adjust":
                {
                    var e = p.Equips.FirstOrDefault(x => x.Id == L(b, "equip_with_user_id"));
                    if (e != null)
                    {
                        var row = Tables.Row("equip", e.EquipId);
                        if (row != null)
                        {
                            foreach (var sk in new[] { "pow", "hit", "dodge", "speed", "rate", "critical_harm_rate", "critical_percent", "armor_piercing", "armor", "shield", "damage_amplify", "damage_reduction", "night_view_percent", "bullet_number_up" })
                            {
                                if (!string.IsNullOrEmpty(row[sk]?.ToString()))
                                    e.Stats[sk] = 10000;
                            }
                        }
                        return Enc(p, e.Stats);
                    }
                    return Enc(p, new Dictionary<string, int>());
                }
                case "equip/changelock":
                case "equip/lock":
                {
                    var ids = Ids(b?["equips"] ?? b?["equip_with_user_ids"] ?? b?["lock"]).ToHashSet();
                    long single = L(b, "equip_with_user_id");
                    if (single > 0) ids.Add(single);
                    int lockVal = b?["is_locked"] != null ? (int)L(b, "is_locked") : -1;
                    foreach (var e in p.Equips.Where(e => ids.Contains(e.Id))) e.IsLocked = lockVal >= 0 ? lockVal : 1 - e.IsLocked;
                    foreach (var e in p.Equips.Where(e => Ids(b?["unlock"]).Contains(e.Id))) e.IsLocked = 0;
                    return Ok1(p);
                }
                case "equip/gunequip":
                {
                    long gunInst = L(b, "gun_with_user_id"); long equipInst = L(b, "equip_with_user_id");
                    int slot = (int)Math.Max(1, L(b, "equip_slot", "slot"));
                    var gun = p.Guns.FirstOrDefault(g => g.Id == gunInst);
                    if (gun != null)
                    {
                        // unequip previous holder of this equip
                        foreach (var og in p.Guns)
                        {
                            if (og.Equip1Id == equipInst && equipInst > 0) og.Equip1Id = 0;
                            if (og.Equip2Id == equipInst && equipInst > 0) og.Equip2Id = 0;
                            if (og.Equip3Id == equipInst && equipInst > 0) og.Equip3Id = 0;
                            if (og.Equip4Id == equipInst && equipInst > 0) og.Equip4Id = 0;
                        }
                        long old = slot switch { 1 => gun.Equip1Id, 2 => gun.Equip2Id, 3 => gun.Equip3Id, _ => gun.Equip4Id };
                        var oldE = p.Equips.FirstOrDefault(e => e.Id == old); if (oldE != null) oldE.GunId = 0;
                        switch (slot) { case 1: gun.Equip1Id = equipInst; break; case 2: gun.Equip2Id = equipInst; break; case 3: gun.Equip3Id = equipInst; break; default: gun.Equip4Id = equipInst; break; }
                        var ne = p.Equips.FirstOrDefault(e => e.Id == equipInst); if (ne != null) { ne.GunId = gun.Id; ne.Slot = slot; }
                    }
                    return Ok1(p);
                }

                // ===== Echelons =====
                // teamGun:  {"team_id":1,"team_set":{"1":449,"2":2,...}}  -> full echelon (slot order -> doll instance, 0 = empty)
                // teamGuns: {"team_id":1,"guns":{"2":1}}                  -> only the listed slots change
                case "gun/teamgun" when (b?["team_set"] is JObject):
                case "gun/teamguns":
                {
                    int team = (int)L(b, "team_id");
                    var map = (b?["team_set"] ?? b?["guns"]) as JObject;
                    if (team <= 0 || map == null) return Ok1(p);
                    bool full = b?["team_set"] != null;
                    int[] cells = { 7, 8, 9, 12, 13, 14, 17, 18, 19 };
                    var slots = map.Properties()
                        .Select(pr => (loc: int.TryParse(pr.Name, out int l) ? l : 0, gun: long.TryParse(pr.Value.ToString(), out long g) ? g : 0))
                        .Where(x => x.loc > 0).ToList();
                    var keepIds = slots.Where(x => x.gun > 0).Select(x => x.gun).ToHashSet();
                    // dolls leaving this echelon (full set) or displaced from the changed slots
                    foreach (var g in p.Guns.Where(g => g.TeamId == team && !keepIds.Contains(g.Id)
                                                        && (full || slots.Any(x => x.loc == g.Location))))
                    { g.TeamId = 0; g.Location = 1; g.Position = 0; }
                    foreach (var (loc, gid) in slots)
                    {
                        if (gid <= 0) continue;
                        var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                        if (g == null) continue;
                        bool wasHere = g.TeamId == team;
                        g.TeamId = team; g.Location = loc;
                        if (!wasHere || !cells.Contains(g.Position))
                        {
                            var used = p.Guns.Where(x => x.TeamId == team && x.Id != g.Id).Select(x => x.Position).ToHashSet();
                            g.Position = cells.FirstOrDefault(c => !used.Contains(c), 7);
                        }
                    }
                    return Ok1(p);
                }
                case "index/fixunbrokentargets":
                {
                    foreach (var id in Ids(b?["gun_with_user_ids"])) { var g = p.Guns.FirstOrDefault(x => x.Id == id); if (g != null) { g.Life = MaxLife(g); g.FixEndTime = 0; } }
                    return Ok1(p);
                }

                // ===== Doll management =====
                case "gun/retiregun":
                {
                    var ids = Ids(b?["_array"] ?? b?["gun_with_user_ids"] ?? b?["guns"]).ToHashSet();
                    foreach (var g in p.Guns.Where(g => ids.Contains(g.Id)))
                    {
                        var row = Tables.Row("gun", g.GunId);
                        int rank = row.Int("rank", 3);
                        p.Mp += rank * 5 * g.Number; p.Ammo += rank * 5 * g.Number; p.Mre += rank * 5 * g.Number; p.Part += rank * 2 * g.Number;
                        foreach (var e in p.Equips.Where(e => e.GunId == g.Id)) e.GunId = 0;
                    }
                    p.Guns.RemoveAll(g => ids.Contains(g.Id));
                    Count(p, "retire_gun", ids.Count);
                    return Ok1(p);
                }
                case "gun/eatgun":
                {
                    long target = L(b, "gun_with_user_id");
                    int item9 = Math.Max(0, (int)L(b, "item9_num", "item_num"));
                    var food = Ids(b?["food"] ?? b?["guns"]).ToHashSet();
                    var gun = p.Guns.FirstOrDefault(g => g.Id == target);
                    int dp = item9, dh = item9, dd = item9, dr = item9;
                    if (item9 > 0) AddItem(p, 9, -item9);
                    if (gun != null)
                    {
                        foreach (var f in p.Guns.Where(g => food.Contains(g.Id)))
                        {
                            var fr = Tables.Row("gun", f.GunId);
                            int rank = fr.Int("rank", 2);
                            dp += rank * 2 * f.Number; dh += rank * f.Number; dd += rank * f.Number; dr += rank * f.Number;
                        }
                        var (maxP, maxH, maxD, maxR) = Tables.GetMaxEnhancement(gun.GunId, gun.GunLevel);
                        int oldP = gun.Pow, oldH = gun.Hit, oldD = gun.Dodge, oldR = gun.Rate;
                        gun.Pow = Math.Min(maxP, gun.Pow + dp); gun.Hit = Math.Min(maxH, gun.Hit + dh);
                        gun.Dodge = Math.Min(maxD, gun.Dodge + dd); gun.Rate = Math.Min(maxR, gun.Rate + dr);
                        dp = gun.Pow - oldP; dh = gun.Hit - oldH; dd = gun.Dodge - oldD; dr = gun.Rate - oldR;
                    }
                    p.Guns.RemoveAll(g => food.Contains(g.Id));
                    Count(p, "eat");
                    return Enc(p, new { pow = dp, dodge = dd, hit = dh, rate = dr });
                }
                case "gun/combinegun":
                {
                    long target = L(b, "gun_with_user_id");
                    var mats = Ids(b?["combine"] ?? b?["guns"] ?? b?["food"]).ToHashSet();
                    var gun = p.Guns.FirstOrDefault(g => g.Id == target);
                    if (gun != null)
                    {
                        int add = Math.Max(1, mats.Count);
                        if (mats.Count == 0) AddItem(p, 33, -1); // core/link item fallback
                        gun.Number = Math.Min(5, gun.Number + add);
                        p.Guns.RemoveAll(g => mats.Contains(g.Id));
                        var row = Tables.Row("gun", gun.GunId);
                        AddItem(p, 41, -row.Int("rank", 3)); // cores
                    }
                    return Ok1(p);
                }
                case "gun/setcollect":
                {
                    var g = p.Guns.FirstOrDefault(x => x.Id == L(b, "gun_with_user_id"));
                    if (g != null && b?["is_collect"] != null)
                    {
                        var tok = b["is_collect"]!;
                        g.IsCollect = tok.Type == JTokenType.Boolean ? (tok.Value<bool>() ? 1 : 0) : (int)L(b, "is_collect");
                    }
                    return Ok1(p);
                }
                case "equip/presetlist":
                    return Enc(p, new List<object>());
                case "gun/changelock":
                case "gun/lockgun":
                {
                    var lockIds = Ids(b?["lock"]); var unlockIds = Ids(b?["unlock"]);
                    long single = L(b, "gun_with_user_id");
                    foreach (var g in p.Guns)
                    {
                        if (lockIds.Contains(g.Id)) g.IsLocked = 1;
                        if (unlockIds.Contains(g.Id)) g.IsLocked = 0;
                        if (single > 0 && g.Id == single) g.IsLocked = b?["is_locked"] != null ? (int)L(b, "is_locked") : 1 - g.IsLocked;
                    }
                    return Ok1(p);
                }
                case "gun/fixguns":
                case "gun/fixgun":
                {
                    bool quick = L(b, "if_quick") > 0;
                    var fg = b?["fix_guns"] as JObject;
                    var fs = b?["fix_slots"] as JObject;
                    var pairs = fg != null
                        ? fg.Properties().Select(pr => (slot: int.TryParse(pr.Value.ToString(), out int s) ? s : 1, gun: long.TryParse(pr.Name, out long gid) ? gid : 0)).ToList()
                        : fs != null
                        ? fs.Properties().Select(pr => (slot: int.TryParse(pr.Name, out int s) ? s : 0, gun: long.TryParse(pr.Value.ToString(), out long gid) ? gid : 0)).ToList()
                        : Ids(b?["gun_with_user_ids"] ?? b?["_array"]).Select((gid, i) => (slot: i + 1, gun: gid)).ToList();
                    foreach (var (slot, gid) in pairs)
                    {
                        var g = p.Guns.FirstOrDefault(x => x.Id == gid); if (g == null) continue;
                        if (quick) { g.Life = MaxLife(g); g.FixEndTime = 0; AddItem(p, 4, -1); }
                        else
                        {
                            long end = Now + 600 + g.GunLevel * 30;
                            g.FixEndTime = end;
                            p.FixSlots.RemoveAll(x => x.Slot == slot);
                            p.FixSlots.Add(new TimedSlot { Slot = slot, GunInstId = gid, EndTime = end });
                        }
                        Count(p, "fix");
                    }
                    return Ok1(p);
                }
                case "gun/quickfix":
                case "gun/finishfix":
                case "gun/fixfinish":
                {
                    int slot = (int)L(b, "fix_slot");
                    var s = p.FixSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s != null)
                    {
                        var g = p.Guns.FirstOrDefault(x => x.Id == s.GunInstId);
                        if (g != null) { g.Life = MaxLife(g); g.FixEndTime = 0; }
                        p.FixSlots.Remove(s);
                        if (op.EndsWith("quickfix", StringComparison.OrdinalIgnoreCase)) AddItem(p, 4, -1);
                    }
                    return Ok1(p);
                }
                case "gun/skillupgrade":
                {
                    long gid = L(b, "gun_with_user_id"); int skill = (int)Math.Max(1, L(b, "skill")); int slot = (int)L(b, "upgrade_slot");
                    var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                    if (g != null)
                    {
                        int lvl = skill == 2 ? g.Skill2 : g.Skill1;
                        AddItem(p, lvl < 5 ? 8 : lvl < 8 ? 9 : 10, -Math.Max(1, lvl)); // training data
                        if (L(b, "if_quick") > 0) { if (skill == 2) g.Skill2 = Math.Min(10, g.Skill2 + 1); else g.Skill1 = Math.Min(10, g.Skill1 + 1); AddItem(p, 7, -1); }
                        else
                        {
                            p.UpgradeSlots.RemoveAll(x => x.Slot == slot);
                            p.UpgradeSlots.Add(new TimedSlot { Slot = slot, GunInstId = gid, Skill = skill, EndTime = Now + 3600 * Math.Max(1, lvl) });
                        }
                        Count(p, "upgrade_skill");
                    }
                    return Ok1(p);
                }
                case "gun/finishupgrade":
                case "gun/quickupgrade":
                {
                    int slot = (int)L(b, "upgrade_slot");
                    var s = p.UpgradeSlots.FirstOrDefault(x => x.Slot == slot);
                    if (s != null)
                    {
                        var g = p.Guns.FirstOrDefault(x => x.Id == s.GunInstId);
                        if (g != null) { if (s.Skill == 2) g.Skill2 = Math.Min(10, g.Skill2 + 1); else g.Skill1 = Math.Min(10, g.Skill1 + 1); }
                        p.UpgradeSlots.Remove(s);
                    }
                    return Ok1(p);
                }
                case "gun/upgradeskill":
                {
                    long gid = L(b, "gun_with_user_id"); int skill = (int)Math.Max(1, L(b, "skill"));
                    var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                    if (g != null) { if (skill == 2) g.Skill2 = Math.Min(10, g.Skill2 + 1); else g.Skill1 = Math.Min(10, g.Skill1 + 1); }
                    return Ok1(p);
                }
                case "gun/mindupdate":
                {
                    long gid = L(b, "gun_with_user_id");
                    var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                    if (g != null)
                    {
                        g.IfModification = Math.Min(3, g.IfModification + 1);
                        if (g.IfModification == 1 && g.Skill2 == 0) g.Skill2 = 1;
                        g.Number = 5;
                    }
                    return Ok1(p);
                }
                case "gun/contract":
                case "gun/soulbond":
                case "gun/oath":
                {
                    var g = p.Guns.FirstOrDefault(x => x.Id == L(b, "gun_with_user_id"));
                    if (g != null)
                    {
                        g.SoulBond = 1;
                        g.SoulBondTime = Now;
                        g.MaxFavor = Math.Max(g.MaxFavor, g.IfModification >= 3 ? 2000000 : 1500000);
                        g.FavorTopLimit = g.MaxFavor;
                        AddItem(p, 7, -1); // 7 = Ring
                    }
                    return Enc(p, new { soul_bond_time = (g?.SoulBondTime ?? Now).ToString() });
                }
                case "dorm/gifttogun":
                {
                    long gid = L(b, "gun_with_user_id");
                    int itemId = (int)L(b, "item_id");
                    int num = (int)Math.Max(1, L(b, "num"));
                    var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                    var row = Tables.Row("gift_item", itemId);
                    long expAdd = 0;
                    int favorAdd = 0;
                    int giftSkinId = 0;
                    if (g != null && row != null)
                    {
                        favorAdd = row.Int("favor") * num;
                        if (favorAdd > 0) g.Favor = Math.Min(g.MaxFavor, g.Favor + favorAdd);
                        giftSkinId = row.Int("skin");
                        if (giftSkinId > 0 && !p.Skins.Contains(giftSkinId)) p.Skins.Add(giftSkinId);
                        expAdd = (long)row.Int("exp") * num;
                        if (expAdd > 0) AddGunExp(g, expAdd);
                        string gk = itemId.ToString();
                        if (p.Gifts.TryGetValue(gk, out int cur))
                            p.Gifts[gk] = Math.Max(0, cur - num);
                    }
                    return Enc(p, new
                    {
                        all_favorup_gun = new List<object>(),
                        all_favorup_sangvis = new List<object>(),
                        last_favor_recover_time = Now.ToString(),
                        skin_add_favor = favorAdd,
                        gun_with_user_id = (g?.Id ?? 0).ToString(),
                        gun_exp = (int)expAdd
                    });
                }
                case "gun/onspecialeffect":
                case "gun/specialeffect":
                case "gun/usespecialeffect":
                case "gun/changespecialeffect":
                case "dorm/specialeffect":
                case "dorm/usespecialeffect":
                case "dorm/changespecialeffect":
                {
                    var g = p.Guns.FirstOrDefault(x => x.Id == L(b, "gun_with_user_id"));
                    if (g != null)
                        g.SpecialEffect = (int)L(b, "special_effect_id", "special_effect", "item_id", "effect_id");
                    return Ok1(p);
                }
                case "gun/offspecialeffect":
                {
                    var g = p.Guns.FirstOrDefault(x => x.Id == L(b, "gun_with_user_id"));
                    if (g != null)
                        g.SpecialEffect = 0;
                    return Ok1(p);
                }
                case "gun/useskin":
                case "gun/changeskin":
                case "gun/skin":
                case "dorm/changeskin":
                {
                    long gid = L(b, "gun_with_user_id");
                    int skinId = (int)L(b, "skin_id", "skin");
                    if (gid == -1)
                        p.Extra["kalina_skin"] = skinId.ToString();
                    else if (gid < -1)
                        p.Extra[$"npc_skin_{gid}"] = skinId.ToString();
                    else
                    {
                        var g = p.Guns.FirstOrDefault(x => x.Id == gid);
                        if (g != null) g.Skin = skinId;
                    }
                    return Ok1(p);
                }

                // ===== Fairies =====
                case "fairy/teamfairy":
                {
                    long fid = L(b, "fairy_with_user_id"); int team = (int)L(b, "team_id");
                    foreach (var f in p.Fairies.Where(f => f.TeamId == team && team > 0)) f.TeamId = 0;
                    var t = p.Fairies.FirstOrDefault(f => f.Id == fid); if (t != null) t.TeamId = team;
                    return Ok1(p);
                }
                case "fairy/changelock":
                {
                    foreach (var f in p.Fairies) { if (Ids(b?["lock"]).Contains(f.Id)) f.IsLocked = 1; if (Ids(b?["unlock"]).Contains(f.Id)) f.IsLocked = 0; }
                    return Ok1(p);
                }
                case "fairy/retire":
                case "fairy/retirefairy":
                {
                    var ids = Ids(b?["_array"] ?? b?["fairy_with_user_ids"]).ToHashSet();
                    p.Fairies.RemoveAll(f => ids.Contains(f.Id));
                    return Ok1(p);
                }
                case "fairy/eatfairy":
                case "fairy/eat":
                {
                    var f = p.Fairies.FirstOrDefault(x => x.Id == L(b, "fairy_with_user_id"));
                    var food = Ids(b?["food"] ?? b?["fairies"]).ToHashSet();
                    if (f != null) f.QualityLevel = Math.Min(5, f.QualityLevel + Math.Max(1, food.Count));
                    p.Fairies.RemoveAll(x => food.Contains(x.Id));
                    return Ok1(p);
                }
                case "fairy/skillupgrade":
                case "fairy/upgradeskill":
                {
                    var f = p.Fairies.FirstOrDefault(x => x.Id == L(b, "fairy_with_user_id"));
                    if (f != null) f.SkillLevel = Math.Min(10, f.SkillLevel + 1);
                    return Ok1(p);
                }
                case "fairy/finishupgrade":
                    return Ok1(p);
                case "fairy/adjust":
                {
                    var f = p.Fairies.FirstOrDefault(x => x.Id == L(b, "fairy_with_user_id"));
                    var talents = Tables.Get("fairy_talent").Keys.ToList();
                    int talent = talents.Count > 0 ? talents[_rng.Next(talents.Count)] : (f?.PassiveSkill ?? 0);
                    if (f != null && talent > 0)
                        p.Extra[$"fairy_pending_talent_{f.Id}"] = talent.ToString();
                    return Enc(p, new { passive_skill = talent });
                }
                case "fairy/adjustconfirm":
                {
                    var f = p.Fairies.FirstOrDefault(x => x.Id == L(b, "fairy_with_user_id"));
                    if (f != null && p.Extra.TryGetValue($"fairy_pending_talent_{f.Id}", out var ts) && int.TryParse(ts, out int talent))
                    {
                        f.PassiveSkill = talent;
                        p.Extra.Remove($"fairy_pending_talent_{f.Id}");
                    }
                    return Ok1(p);
                }

                // ===== Base / Dorm / Data Room / Sim =====
                case "dorm/get_build_coin":
                    return Enc(p, new { build_coin = 40 });
                case "dorm/change":
                {
                    if (b?["furn_pos"] is JArray fp && p.Extra.TryGetValue("dorm_furniture", out var fj) && !string.IsNullOrEmpty(fj))
                    {
                        var furnList = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(fj);
                        if (furnList != null)
                        {
                            foreach (var pos in fp.OfType<JArray>())
                            {
                                if (pos.Count < 3) continue;
                                string fid = pos[0].ToString();
                                var item = furnList.FirstOrDefault(x => x.GetValueOrDefault("id") == fid);
                                if (item != null) { item["x"] = pos[1].ToString(); item["y"] = pos[2].ToString(); }
                            }
                            p.Extra["dorm_furniture"] = JsonConvert.SerializeObject(furnList);
                        }
                    }
                    return Enc(p, new { all_favorup_gun = new List<object>(), all_favorup_sangvis = new List<object>(), last_favor_recover_time = Now.ToString(), score = 3200 });
                }
                case "dorm/coffeeshop_unlock":
                {
                    int cid = (int)L(b, "id");
                    int ctype = (int)L(b, "type");
                    string key = $"coffeeshop_{ctype}";
                    var unlocked = (p.Extra.GetValueOrDefault(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (cid > 0 && unlocked.Add(cid.ToString()))
                    {
                        var row = Tables.Row(ctype == 2 ? "coffeeshop_comic" : "coffeeshop_pv", cid);
                        if (row != null)
                        {
                            foreach (var pair in (row["cost"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var kv = pair.Split('-');
                                if (kv.Length == 2 && int.TryParse(kv[0], out int itemId) && int.TryParse(kv[1], out int cnt) && itemId > 0 && cnt > 0)
                                    AddItem(p, itemId, -cnt);
                            }
                            GrantRow(p, row);
                        }
                    }
                    p.Extra[key] = string.Join(',', unlocked);
                    return Ok1(p);
                }
                case "dorm/coffeeshop_unlock_list":
                {
                    var pv = (p.Extra.GetValueOrDefault("coffeeshop_1") ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    var comic = (p.Extra.GetValueOrDefault("coffeeshop_2") ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (pv.Count == 0 && comic.Count == 0)
                        return Enc(p, new List<object>());
                    var res = new Dictionary<string, object>();
                    if (pv.Count > 0) res["pv"] = pv;
                    if (comic.Count > 0) res["comic"] = comic;
                    return Enc(p, res);
                }
                case "dorm/gasha_draw":
                {
                    int drawType = (int)L(b, "draw_type");
                    int count = drawType >= 2 ? 10 : 1;
                    int tickets = int.TryParse(p.Extra.GetValueOrDefault("gasha_tickets"), out int tk) ? tk : 0;
                    tickets += count;
                    p.Extra["gasha_tickets"] = tickets.ToString();

                    var allFurn = Tables.Get("furniture").Keys.ToList();
                    var rolled = new List<object>();
                    var furnList = p.Extra.TryGetValue("dorm_furniture", out var fj) && !string.IsNullOrEmpty(fj)
                        ? JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(fj) ?? new()
                        : new List<Dictionary<string, string>>();
                    long nextFurnId = furnList.Select(x => long.TryParse(x.GetValueOrDefault("id"), out long v) ? v : 0).DefaultIfEmpty(19562800).Max() + 1;
                    for (int i = 0; i < count && allFurn.Count > 0; i++)
                    {
                        int fid = allFurn[_rng.Next(allFurn.Count)];
                        long instId = nextFurnId++;
                        furnList.Add(new Dictionary<string, string> { ["id"] = instId.ToString(), ["dorm"] = "0", ["furniture_id"] = fid.ToString(), ["x"] = "0", ["y"] = "0" });
                        rolled.Add(new { dorm_with_user_id = instId, id = fid.ToString() });
                    }
                    p.Extra["dorm_furniture"] = JsonConvert.SerializeObject(furnList);
                    return Enc(p, new { exchange_tickets = tickets, furniture = rolled, cosmetic = new List<object>(), decompose = new List<object>() });
                }
                case "outhouse/establish_upgrade":
                {
                    int estId = (int)L(b, "establish_id");
                    if (estId > 0) p.Extra["establish_upgrading"] = estId.ToString();
                    return Ok1(p);
                }
                case "outhouse/establish_upgrade_finish":
                {
                    int estId = (int)L(b, "establish_id");
                    if (estId == 0 && int.TryParse(p.Extra.GetValueOrDefault("establish_upgrading"), out int saved)) estId = saved;
                    bool quick = L(b, "is_quick") > 0 || b?["is_quick"]?.Type == JTokenType.Boolean && b["is_quick"]!.Value<bool>();
                    int cost = quick ? 1 : 0;
                    if (cost > 0) p.Gem = Math.Max(0, p.Gem - cost);
                    if (estId > 0)
                    {
                        int lv = int.TryParse(p.Extra.GetValueOrDefault($"establish_lv_{estId}"), out int cur) ? cur : 1;
                        p.Extra[$"establish_lv_{estId}"] = (lv + 1).ToString();
                        p.Extra.Remove("establish_upgrading");
                    }
                    return Enc(p, new { cost_gem = cost });
                }
                case "fetter/index":
                {
                    var stories = (p.Extra.GetValueOrDefault("fetter_stories") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => new { fetter_story_id = s, user_id = p.Id.ToString() }).ToList();
                    return Enc(p, new { fetter_with_user = new List<object>(), fetter_story_with_user = stories, fetter_bounty_with_user = new List<object>(), org_bounty_with_user = new List<object>() });
                }
                case "fetter/readstory":
                {
                    int storyId = (int)L(b, "fetter_story_id");
                    var storyRow = Tables.Row("fetter_story", storyId);
                    int prizeId = storyRow.Int("reward", 22006);
                    var pr = Tables.Row("prize", prizeId);
                    if (pr != null)
                    {
                        p.Mp += pr.Int("mp"); p.Ammo += pr.Int("ammo"); p.Mre += pr.Int("mre"); p.Part += pr.Int("part");
                        p.Gem += pr.Int("gem");
                    }
                    var read = (p.Extra.GetValueOrDefault("fetter_stories") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (storyId > 0) read.Add(storyId.ToString());
                    p.Extra["fetter_stories"] = string.Join(',', read);
                    return Enc(p, new
                    {
                        prize = new
                        {
                            id = prizeId.ToString(), name = pr?["name"]?.ToString() ?? "", user_exp = pr?["user_exp"]?.ToString() ?? "0",
                            mp = pr?["mp"]?.ToString() ?? "0", ammo = pr?["ammo"]?.ToString() ?? "0", mre = pr?["mre"]?.ToString() ?? "0",
                            part = pr?["part"]?.ToString() ?? "0", core = pr?["core"]?.ToString() ?? "0", gem = pr?["gem"]?.ToString() ?? "0",
                            gem_pay = "0", gun_id = "", sangvis = "", item_ids = pr?["item_ids"]?.ToString() ?? "",
                            furniture = new List<object>(), gift = "", equip_ids = "", coins = "", skin = "0",
                            content = pr?["content"]?.ToString() ?? "", send_limit = "0", icon = "", bp_pay = "0",
                            fairy_ids = "", chip = new List<object>(), commander_uniform = new List<object>(),
                            vehicle_component = new List<object>(), prize_id = prizeId.ToString(), gun_with_user_id = 0,
                            sangvis_with_user_id = 0, fairys = new List<object>(), equips = new List<object>(), vehicle_component_list = new List<object>()
                        },
                        status = 1
                    });
                }
                case "chess/setteam":
                {
                    int tid = (int)L(b, "team_id");
                    var teams = p.Extra.TryGetValue("chess_teams", out var ct) && !string.IsNullOrEmpty(ct)
                        ? JsonConvert.DeserializeObject<Dictionary<string, JArray>>(ct) ?? new()
                        : new Dictionary<string, JArray>();
                    if (b?["guns"] is JArray guns) teams[tid.ToString()] = guns;
                    p.Extra["chess_teams"] = JsonConvert.SerializeObject(teams);
                    return Ok1(p);
                }
                case "chess/index":
                {
                    var teamList = new List<object>();
                    if (p.Extra.TryGetValue("chess_teams", out var ct) && !string.IsNullOrEmpty(ct))
                    {
                        var teams = JsonConvert.DeserializeObject<Dictionary<string, JArray>>(ct);
                        if (teams != null)
                        {
                            foreach (var kv in teams)
                                foreach (var gObj in kv.Value.OfType<JObject>())
                                    teamList.Add(new
                                    {
                                        team_id = int.TryParse(kv.Key, out int tid) ? tid : 0,
                                        member_id = gObj["member_id"]?.Value<int>() ?? 1,
                                        gun_id = gObj["gun_id"]?.ToString() ?? "1",
                                        skin = gObj["skin"]?.ToString() ?? "0",
                                        chess_chip_id = gObj["chess_chip_id"]?.Value<int>() ?? 20101,
                                        chess_gun_type = gObj["chess_gun_type"]?.ToString() ?? "1",
                                        user_id = p.Id
                                    });
                        }
                    }
                    return Enc(p, new
                    {
                        chess_info = new
                        {
                            user_id = p.Id, chess_score = 0, max_chess_score = 0, tutorial = 1, season_id = "20",
                            ticket_daily_num = 100, ticket_daily_refresh_time = Now, ticket_cost = 0, acquired_bonus = ""
                        },
                        team_list = teamList
                    });
                }
                case "index/accomplishweeklyquest":
                {
                    int qid = (int)L(b, "weekly_quest_id");
                    var done = (p.Extra.GetValueOrDefault("weekly_done") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (qid > 0) done.Add(qid.ToString());
                    p.Extra["weekly_done"] = string.Join(',', done);
                    int acc = done.Count * 10;
                    p.Mp += 1000; p.Ammo += 1000; p.Mre += 1000; p.Part += 1000;
                    return Enc(p, new { weekly_accumulation = acc.ToString(), battlepass_pt_item = new Dictionary<string, object>(), mail_list = new List<object>() });
                }
                case "index/quickgetresourceinmails":
                    return Enc(p, new
                    {
                        user_exp = 0, mp = 0, ammo = 0, mre = 0, part = 0, core = 0, gem = 0, gem_pay = 0,
                        gun_id = new List<object>(), item_ids = "", equip_ids = new List<object>(), furniture = new List<object>(),
                        gift = "", skin = new List<object>(), bp_pay = 0, coins = "", fairy_ids = new List<object>(),
                        commander_uniform = new List<object>(), sangvis = new List<object>(), vehicle_component = new List<object>()
                    });
                case "battlepass/getbonus":
                {
                    int bpId = (int)L(b, "battlepass_id");
                    int bpType = (int)L(b, "type");
                    int param = (int)L(b, "param");
                    string key = $"bp_{bpId}_{bpType}";
                    var claimed = (p.Extra.GetValueOrDefault(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (param > 0) claimed.Add(param.ToString());
                    p.Extra[key] = string.Join(',', claimed);
                    p.Mp += 500; p.Ammo += 500; p.Mre += 500; p.Part += 500;
                    return Enc(p, new
                    {
                        free_bonus = p.Extra.GetValueOrDefault($"bp_{bpId}_1", ""),
                        paid_bonus = p.Extra.GetValueOrDefault($"bp_{bpId}_2", ""),
                        extra_bonus = p.Extra.GetValueOrDefault($"bp_{bpId}_3", "")
                    });
                }
                case "mall/buyvehicleskin":
                {
                    foreach (long mid in Ids(b?["mall_ids"] ?? b?["id"]))
                    {
                        var row = Tables.Row("mall", (int)mid);
                        if (row == null) continue;
                        if (MallRestNum(p, row) == 0) continue;
                        int gemCost = row.Int("gemprice");
                        if (gemCost > 0) p.Gem = Math.Max(0, p.Gem - gemCost);
                        GrantRow(p, row);
                        RecordMallPurchase(p, row, 1);
                    }
                    return Enc(p, new { status = 1 });
                }
                case "friend/cosmeticbuy":
                {
                    int cid = (int)L(b, "id");
                    var owned = (p.Extra.GetValueOrDefault("friend_cosmetics") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (cid > 0) owned.Add(cid.ToString());
                    p.Extra["friend_cosmetics"] = string.Join(',', owned);
                    return Ok1(p);
                }
                case "mall/buyuniform":
                {
                    var owned = (p.Extra.GetValueOrDefault("commander_uniforms") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    var added = new List<object>();
                    foreach (long mid in Ids(b?["mall_ids"] ?? b?["id"]))
                    {
                        var row = Tables.Row("mall", (int)mid);
                        if (row == null) continue;
                        if (MallRestNum(p, row) == 0) continue;
                        int gemCost = row.Int("gemprice");
                        if (gemCost > 0) p.Gem = Math.Max(0, p.Gem - gemCost);
                        var pr = row.Int("prize_id") > 0 ? Tables.Row("prize", row.Int("prize_id")) : null;
                        string rawUniforms = pr?["commander_uniform"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(rawUniforms) && Tables.Row("commander_uniform", (int)mid) != null) rawUniforms = mid.ToString();
                        foreach (var uidStr in rawUniforms.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string u = uidStr.Trim();
                            if (string.IsNullOrEmpty(u)) continue;
                            owned.Add(u);
                            added.Add(new { user_id = p.Id, commander_uniform_id = u, color_get = new List<object>(), get_color_num = 0, create_time = Now });
                        }
                        RecordMallPurchase(p, row, 1);
                    }
                    p.Extra["commander_uniforms"] = string.Join(',', owned);
                    return Enc(p, new { status = 1, commander_uniform = added, commander_uniform_info = added });
                }
                case "mall/buy":
                case "mall/gem":
                case "mall/gemtogiftbag":
                case "mall/gemtoresources":
                case "mall/gemtoitem":
                case "mall/itemtogiftbag":
                case "mall/pointtogiftbag":
                {
                    int mallId = (int)L(b, "id", "mall_id", "good_id");
                    int num = (int)Math.Max(1, L(b, "num", "count"));
                    var row = Tables.Row("mall", mallId);
                    int prizeId = row?.Int("prize_id") ?? 0;
                    var pr = prizeId > 0 ? Tables.Row("prize", prizeId) : null;
                    long lastGunInstId = 0;
                    int lastGunId = 0;
                    var gunAddList = new List<object>();
                    if (row != null)
                    {
                        int restBefore = MallRestNum(p, row);
                        if (restBefore == 0) return Enc(p, new { status = 0, rest_num = 0 });
                        if (restBefore > 0) num = Math.Min(num, restBefore);

                        int gemCost = row.Int("gemprice") * num;
                        if (gemCost > 0) p.Gem = Math.Max(0, p.Gem - gemCost);
                        foreach (var costPart in (row["itemprice"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = costPart.Split('-', ':');
                            if (kv.Length == 2 && int.TryParse(kv[0], out int cid) && int.TryParse(kv[1], out int ccnt) && cid > 0)
                                AddItem(p, cid, -(ccnt * num));
                        }
                        p.Gem += row.Int("gem") * num;
                        p.Mp += row.Int("mp") * num;
                        p.Ammo += row.Int("ammo") * num;
                        p.Mre += row.Int("mre") * num;
                        p.Part += row.Int("part") * num;
                        p.Bp += row.Int("bp_pay") * num;
                        int skinId = row.Int("skin");
                        if (skinId > 0 && !p.Skins.Contains(skinId)) p.Skins.Add(skinId);
                        foreach (var part in (row["item_ids"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = part.Split('-');
                            if (kv.Length == 2 && int.TryParse(kv[0], out int iid) && int.TryParse(kv[1], out int cnt))
                                AddItem(p, iid, cnt * num);
                        }
                        foreach (var part in (row["gift"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = part.Split('-', ':'); // table uses "510-1"
                            if (kv.Length == 2 && int.TryParse(kv[0], out int gid) && int.TryParse(kv[1], out int cnt))
                                p.Gifts[gid.ToString()] = (p.Gifts.TryGetValue(gid.ToString(), out int cur) ? cur : 0) + cnt * num;
                        }
                        if (pr != null)
                        {
                            // full prize: resources, items, dolls, equipment, coins, skins
                            for (int i = 0; i < num; i++)
                            {
                                var gunsBefore = p.Guns.Select(g => g.Id).ToHashSet();
                                GrantRow(p, pr);
                                foreach (var given in p.Guns.Where(g => !gunsBefore.Contains(g.Id)))
                                {
                                    lastGunInstId = given.Id; lastGunId = given.GunId;
                                    gunAddList.Add(new { gun_with_user_id = given.Id.ToString(), gun_id = given.GunId.ToString() });
                                }
                            }
                        }
                        RecordMallPurchase(p, row, num);
                    }
                    var prizeObj = pr == null ? null : new
                    {
                        id = prizeId.ToString(), name = pr["name"]?.ToString() ?? "", user_exp = pr["user_exp"]?.ToString() ?? "0",
                        mp = pr["mp"]?.ToString() ?? "0", ammo = pr["ammo"]?.ToString() ?? "0", mre = pr["mre"]?.ToString() ?? "0",
                        part = pr["part"]?.ToString() ?? "0", core = pr["core"]?.ToString() ?? "0", gem = pr["gem"]?.ToString() ?? "0",
                        gem_pay = "0", gun_id = pr["gun_id"]?.ToString() ?? "", sangvis = "", item_ids = pr["item_ids"]?.ToString() ?? "",
                        furniture = new List<object>(), gift = pr["gift"]?.ToString() ?? "", equip_ids = "", coins = "",
                        skin = pr["skin"]?.ToString() ?? "0", content = pr["content"]?.ToString() ?? "", send_limit = "0",
                        icon = "", bp_pay = "0", fairy_ids = "", chip = new List<object>(), commander_uniform = new List<object>(),
                        vehicle_component = new List<object>(), prize_id = prizeId.ToString(), gun_with_user_id = lastGunInstId,
                        sangvis_with_user_id = 0, fairys = new List<object>(), equips = new List<object>(), vehicle_component_list = new List<object>()
                    };
                    var resp = new Dictionary<string, object?>
                    {
                        ["status"] = 1,
                        ["rest_num"] = row != null ? MallRestNum(p, row) : -1,
                        ["mall_score"] = 0,
                        ["prize_id"] = prizeId,
                        ["gun_with_user_add_list"] = gunAddList,
                        ["equip_with_user_add_list"] = new List<object>(),
                        ["fairy_with_user_add_list"] = new List<object>()
                    };
                    if (lastGunInstId > 0)
                        resp["gun_with_user_add"] = new { gun_with_user_id = lastGunInstId.ToString(), gun_id = lastGunId.ToString() };
                    if (prizeObj != null)
                    {
                        resp["prize"] = prizeObj;
                        resp["package"] = prizeObj;
                    }
                    // Official Mall/gemToItem (gfl_capture.log L940) returns {"status":1}; giftbag/other mall ops return
                    // status=1 plus rest_num and prize/package/gun_with_user_add_list.
                    if (op.Equals("mall/gemtoitem", StringComparison.OrdinalIgnoreCase)) return Enc(p, new { status = 1 });
                    resp["status"] = 1;
                    return Enc(p, resp);
                }
                case "index/rank":
                    return Enc(p, new { rank_list = new List<object>() });
                case "index/guildgetgamesign":
                    return Enc(p, new { sessionKey = "local", gateway_url = "127.0.0.1:11301", guid = p.Id.ToString() });
                case "friend/usercard":
                    return Enc(p, new { f_userid = L(b, "f_userid"), name = "Commander", lv = "100", medal = new List<object>(), medal_is_visible = "1", medal_with_user = new List<object>() });
                case "outhouse/establish_build":
                {
                    int num = Math.Max(1, (int)L(b, "num"));
                    return Enc(p, new { build_coin = 0, gift_item_id = 200001, exp = 0, build_num = num, build_tmp_data = new List<string>() });
                }
                case "outhouse/establish_build_finish":
                {
                    p.Gifts["200001"] = (p.Gifts.TryGetValue("200001", out int c) ? c : 0) + 1;
                    return Enc(p, new { gift = "200001:1" });
                }
                case "index/recoverbp":
                {
                    p.Bp = Math.Min(9999, p.Bp + 1);
                    return Enc(p, new { recover_bp = 1, last_bp_recover_time = Now });
                }
                case "automission/finishautomission":
                    return Enc(p, new
                    {
                        add_gun_exp = new List<object>(),
                        add_fairy_exp = new List<object>(),
                        free_exp = 0,
                        add_user_exp = 0,
                        add_prize = new List<object>(),
                        favor_change = new Dictionary<string, int>(),
                        success_number = 1
                    });
                case "gun/developlogcollect":
                    return Ok1(p);
                case "mission/cointeam":
                    return Ok1(p);
                case "mission/coinbattlefinish":
                    return Enc(p, new { coin_num = "500", coin_type = "1" });
                case "mission/starttrial":
                    return Enc(p, new { trial_id = 1 });
                case "mission/endtrialexercise":
                {
                    var dpsMap = new Dictionary<string, int>();
                    var lifeMap = new Dictionary<string, int>();
                    if (b?["battle_guns"] is JObject bg)
                    {
                        foreach (var pr in bg.Properties())
                        {
                            dpsMap[pr.Name] = (int)L(pr.Value as JObject, "dps");
                            lifeMap[pr.Name] = (int)L(pr.Value as JObject, "life");
                        }
                    }
                    return Enc(p, new { guns_dps = dpsMap, guns_real_life = lifeMap, reward_voucher = 100 });
                }

                // ===== Logistics =====
                case "operation/abortoperation":
                {
                    int opId = (int)L(b, "operation_id");
                    var act = p.ActiveOperations.FirstOrDefault(o => o.OperationId == opId);
                    if (act != null) p.ActiveOperations.Remove(act);
                    return Ok1(p);
                }

                case "vehicle/taskinfo":
                    return Enc(p, new List<object>());
                case "daily/index":
                    return Enc(p, new Dictionary<string, object>());
                case "dorm/medal":
                    return Enc(p, new List<object>());
                // ===== Profile / misc persistence =====
                case "commander/changedressed":
                {
                    var dressed = b?["dressed_info"] ?? b?["dressed"];
                    if (dressed != null) p.Extra["commander_dressed"] = dressed.ToString();
                    return Ok1(p);
                }
                case "index/adjutant":
                {
                    if (b?["adjutant"] != null) p.Adjutant = b["adjutant"]!.ToString();
                    if (b?["adjutant_multi"] != null) p.AdjutantMulti = b["adjutant_multi"]!.ToString();
                    if (b?["adjutant_fairy"] != null) p.AdjutantFairy = b["adjutant_fairy"]!.ToString();
                    return Ok1(p);
                }
                case "index/gamesetting":
                {
                    if (b?["settings"] != null) p.GameSetting = b["settings"]!.ToString(Formatting.None);
                    return Ok1(p);
                }
                case "index/guide":
                {
                    // the client sends the guide as a JSON *string*; store its text, not a re-quoted token
                    if (b?["guide"] != null) p.Guide = b["guide"]!.Type == JTokenType.String ? b["guide"]!.Value<string>()! : b["guide"]!.ToString(Formatting.None);
                    return Ok1(p);
                }
                case "mission/supplyteam":
                    return null; // handled by GameController.HandleSupplyTeam
            }

            // Generic key/value persistence: every op whose request we do not model is stored so nothing
            // the client sends is silently lost, and can be replayed into Index/index later.
            return null;
        }
    }
}
