using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Night missions, ally betrayal / transformation, building capture/destruction and win condition 1406.
    ///
    /// Night missions (mission.special_type == 1) answer with night_spots / night_enemy / night_ally instead of
    /// spot_act_info (official replies, kchang06/GFAlarm MissionData.cs and Mission.cs):
    ///   night_spots: {"spot_id","belong","seed"}
    ///   night_enemy: {"enemy_team_id","from_spot_id","to_spot_id","boss_hp","enemy_hp_percent","enemy_instance_id",
    ///                 "enemy_birth_turn","enemy_ai","enemy_ai_para","hostage_id","hostage_hp","squad_instance_id",
    ///                 "ally_instance_id"}
    /// Enemies and allies are only reported inside the echelons' sight: nodes held by Griffin echelons and their
    /// neighbours.
    ///
    /// Ally conditions (ally_team.betray_condition / transform_condition + *_number, ";"-separated, any met):
    ///   1/5 turn number reached, 2 Griffin echelon on one of the spots, 201 enemy on one of the spots,
    ///   3 "spot-belong" spot owned by that side, 4/6 that enemy team defeated.
    /// The result (betray_result / transform_result) is the ally's new type; its AI follows ally_team.ai for that type.
    /// Changed allies are sent in ally_instance_betray / ally_instance_transform with the ally_instance_info item shape.
    /// </summary>
    public partial class GameController
    {
        private static bool IsNightMission(int missionId) => Tables.Row("mission", missionId).Int("special_type") == 1;

        private static HashSet<int> GriffinSight(ActiveMissionState m)
        {
            var g = MissionGraph(m.MissionId);
            var sight = new HashSet<int>();
            foreach (var s in m.Spots.Where(s => s.TeamId > 0))
            {
                sight.Add(s.SpotId);
                foreach (int n in g.GetValueOrDefault(s.SpotId) ?? new List<int>()) sight.Add(n);
            }
            return sight;
        }

        /// <summary>Night version of the map part of a mission reply.</summary>
        private static void ApplyNightView(ActiveMissionState m, Dictionary<string, object> payload)
        {
            var sight = GriffinSight(m);
            payload.Remove("spot_act_info");
            payload["night_spots"] = m.Spots.Select(s => new Dictionary<string, object>
            {
                ["spot_id"] = s.SpotId.ToString(), ["belong"] = s.Belong.ToString(), ["seed"] = s.Seed
            }).ToList();
            payload["night_enemy"] = m.Spots.Where(s => s.EnemyTeamId > 0 && sight.Contains(s.SpotId)).Select(s => new Dictionary<string, object>
            {
                ["enemy_team_id"] = s.EnemyTeamId.ToString(), ["from_spot_id"] = s.SpotId.ToString(), ["to_spot_id"] = s.SpotId.ToString(),
                ["boss_hp"] = s.BossHp.ToString(), ["enemy_hp_percent"] = "1", ["enemy_instance_id"] = s.EnemyInstanceId.ToString(),
                ["enemy_birth_turn"] = "1", ["enemy_ai"] = "0", ["enemy_ai_para"] = 0, ["hostage_id"] = s.HostageId.ToString(),
                ["hostage_hp"] = s.HostageHp.ToString(), ["squad_instance_id"] = "0", ["ally_instance_id"] = 0
            }).ToList();
            payload["night_ally"] = m.Spots.Where(s => s.AllyInstanceIds.Count > 0 && sight.Contains(s.SpotId))
                .SelectMany(s => s.AllyInstanceIds.Select(id => (s, id))).Select(x =>
                {
                    var a = m.Allies.FirstOrDefault(al => al.Id == x.id);
                    return new Dictionary<string, object>
                    {
                        ["enemy_team_id"] = (a?.AllyEnemy ?? 0).ToString(), ["from_spot_id"] = x.s.SpotId.ToString(), ["to_spot_id"] = x.s.SpotId.ToString(),
                        ["boss_hp"] = "0", ["enemy_hp_percent"] = "1", ["enemy_instance_id"] = "0", ["enemy_birth_turn"] = "1",
                        ["enemy_ai"] = a?.Ai ?? "0", ["enemy_ai_para"] = a?.AiPara ?? "", ["hostage_id"] = "0", ["hostage_hp"] = "0",
                        ["squad_instance_id"] = "0", ["ally_instance_id"] = x.id
                    };
                }).ToList();
        }

        /// <summary>Night: enemy/ally moves are only shown when they start or end inside Griffin sight.</summary>
        private static List<object> VisibleMoves(ActiveMissionState m, List<object> moves)
        {
            if (!IsNightMission(m.MissionId)) return moves;
            var sight = GriffinSight(m);
            return moves.Where(mv =>
            {
                var t = mv.GetType();
                int from = Convert.ToInt32(t.GetProperty("from_spot_id")?.GetValue(mv));
                int to = Convert.ToInt32(t.GetProperty("to_spot_id")?.GetValue(mv));
                return sight.Contains(from) || sight.Contains(to);
            }).ToList();
        }

        // ---------------- ally betrayal / transformation ----------------
        private static bool AllyConditionMet(PlayerAccount p, ActiveMissionState m, string code, string number)
        {
            var nums = Numbers(number);
            switch (code.Trim())
            {
                case "1":
                case "5": return nums.Count > 0 && m.Turn >= nums[0];
                case "2": return m.Spots.Any(s => s.TeamId > 0 && nums.Contains(s.SpotId));
                case "201": return m.Spots.Any(s => s.EnemyTeamId > 0 && nums.Contains(s.SpotId));
                case "3":
                {
                    foreach (var pair in number.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = pair.Split('-');
                        if (kv.Length == 2 && int.TryParse(kv[0], out int spot) && int.TryParse(kv[1], out int belong)
                            && m.Spots.Any(s => s.SpotId == spot && s.Belong == belong)) return true;
                    }
                    return false;
                }
                case "4":
                case "6":
                {
                    var killed = KillsByTeam(p);
                    return nums.Any(n => killed.GetValueOrDefault(n.ToString()) > 0);
                }
                default: return false;
            }
        }

        /// <summary>Checks every ally's betray/transform conditions; returns (betrayed, transformed) ally infos.</summary>
        private static (List<object> betrayed, List<object> transformed) UpdateAllyStates(PlayerAccount p, ActiveMissionState m)
        {
            var betrayed = new List<object>();
            var transformed = new List<object>();
            foreach (var ally in m.Allies)
            {
                var at = Tables.Row("ally_team", ally.AllyTeamId);
                if (at == null) continue;
                foreach (var (condField, numField, resField, list, tag) in new[]
                {
                    ("betray_condition", "betray_number", "betray_result", betrayed, "b"),
                    ("transform_condition", "transform_number", "transform_result", transformed, "t")
                })
                {
                    string doneKey = $"ally_{tag}_{ally.Id}";
                    if (p.Extra.ContainsKey(doneKey)) continue;
                    var conds = (at[condField]?.ToString() ?? "").Split(';');
                    var numbers = (at[numField]?.ToString() ?? "").Split(';');
                    bool met = false;
                    for (int i = 0; i < conds.Length && !met; i++)
                        if (conds[i] is not ("" or "0"))
                            met = AllyConditionMet(p, m, conds[i], i < numbers.Length ? numbers[i] : "");
                    if (!met) continue;
                    var res = Numbers(at[resField]?.ToString());
                    p.Extra[doneKey] = "1";
                    if (res.Count == 0 || res[0] == 0) continue;
                    ally.AllyType = (int)res[0];
                    string Pick(string field)
                    {
                        var parts = (at[field]?.ToString() ?? "").Split(';');
                        return ally.AllyType < parts.Length ? parts[ally.AllyType] : parts.LastOrDefault() ?? "";
                    }
                    ally.Ai = string.IsNullOrEmpty(Pick("ai")) ? "0" : Pick("ai");
                    ally.AiPara = Pick("ai_content");
                    list.Add(((Dictionary<string, object>)AllyInstanceInfo(new ActiveMissionState { Allies = { ally } }))[ally.Id.ToString()]);
                }
            }
            return (betrayed, transformed);
        }

        // ---------------- buildings ----------------
        /// <summary>Capture by <paramref name="holder"/>: is_destroy lists the sides that destroy it instead (-> neutral 3).</summary>
        private static int CaptureResult(int buildingId, int holder)
        {
            var destroyers = Numbers(Tables.Row("building", buildingId)?["is_destroy"]?.ToString());
            return destroyers.Contains(holder) ? 3 : holder;
        }

        /// <summary>Win condition 1406: "ref:state[,ref:state]" — state 1: Griffin controls the building/spot, 0: a
        /// Griffin echelon stands on it. ref is a spot id or the building / special spot it hosts.</summary>
        private static bool? Check1406(ActiveMissionState m, string args)
        {
            var pairs = args.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length == 0) return null;
            bool any = false;
            foreach (var pair in pairs)
            {
                var kv = pair.Split(':');
                if (!long.TryParse(kv[0], out long target)) continue;
                int state = kv.Length > 1 && int.TryParse(kv[1], out int st) ? st : 1;
                var spots = m.Spots.Where(s => SpotRefs(s.SpotId).Contains(target)).ToList();
                if (spots.Count == 0) continue;
                any = true;
                bool ok = state == 0
                    ? spots.Any(s => s.TeamId > 0)
                    : spots.All(s => (s.BuildingId > 0 ? s.BuildingBelong : s.Belong) is 1 or 3 && s.EnemyTeamId == 0);
                if (!ok) return false;
            }
            return any ? true : null;
        }
    }
}
