using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Allied / third-party echelons and buildings on the mission map.
    /// Allies come from spot.ally_team_id + ally_team.json. Their reply format is the official one captured in
    /// mission_act_info.ally_instance_info (kchang06/GFAlarm, MissionActInfo.cs example):
    ///   {"1":{"ally_instance_id":"1","ally_team_id":"2216","ally_enemy":"2264","ally_type":"2","ai":"102",
    ///         "ai_para":"","ally_boss_hp":"0","ally_enemy_hp_percent":"1","team_guns_life":[],"move_turn":"0",
    ///         "transform_condition2":"","betray_condition2":""}}
    /// and each spot lists its allies in ally_instance_ids. ally_team.ai / ai_content are ";"-lists indexed by
    /// ally_type (initial_type). Allies hold their position for now (no ally AI movement yet).
    /// Buildings: spot.building_id -> building.json (belong, hold_belong). A building changes hands when an
    /// echelon holds its spot; changes are reported as building_change_belong1 (player turn) and
    /// building_change_belong2 (enemy turn), maps of spot id -> new owner (hoxy SMissionEndTurn).
    /// </summary>
    public partial class GameController
    {
        private static void PlaceAlliesAndBuildings(ActiveMissionState m)
        {
            int next = 1;
            foreach (var s in m.Spots)
            {
                var row = Tables.Row("spot", s.SpotId);
                int buildingId = row.Int("building_id");
                if (buildingId > 0)
                {
                    s.BuildingId = buildingId;
                    s.BuildingBelong = Tables.Row("building", buildingId).Int("belong", s.Belong);
                }

                int allyTeam = row.Int("ally_team_id");
                if (allyTeam <= 0) continue;
                var at = Tables.Row("ally_team", allyTeam);
                int type = at.Int("initial_type", 2);
                string Pick(string field)
                {
                    var parts = (at?[field]?.ToString() ?? "").Split(';');
                    return type >= 0 && type < parts.Length ? parts[type] : parts.LastOrDefault() ?? "";
                }
                var ally = new AllyInstance
                {
                    Id = next++, AllyTeamId = allyTeam, AllyEnemy = at.Int("enemy_team_id"), AllyType = type,
                    Ai = string.IsNullOrEmpty(Pick("ai")) ? "0" : Pick("ai"), AiPara = Pick("ai_content")
                };
                m.Allies.Add(ally);
                s.AllyInstanceIds.Add(ally.Id);
            }
        }

        private static object AllyInstanceInfo(ActiveMissionState m)
        {
            if (m.Allies.Count == 0) return new List<object>();
            return m.Allies.ToDictionary(a => a.Id.ToString(), a => (object)new Dictionary<string, object>
            {
                ["ally_instance_id"] = a.Id.ToString(),
                ["ally_team_id"] = a.AllyTeamId.ToString(),
                ["ally_enemy"] = a.AllyEnemy.ToString(),
                ["ally_type"] = a.AllyType.ToString(),
                ["ai"] = a.Ai,
                ["ai_para"] = a.AiPara,
                ["ally_boss_hp"] = "0",
                ["ally_enemy_hp_percent"] = "1",
                ["team_guns_life"] = new List<object>(),
                ["move_turn"] = "0",
                ["transform_condition2"] = "",
                ["betray_condition2"] = ""
            });
        }

        /// <summary>Buildings whose spot is now held by a different side change owner. Returns spot -> new owner.</summary>
        private static Dictionary<string, int> UpdateBuildings(ActiveMissionState m, bool enemyTurn)
        {
            var changed = new Dictionary<string, int>();
            foreach (var s in m.Spots.Where(s => s.BuildingId > 0))
            {
                int holder = enemyTurn
                    ? (s.EnemyTeamId > 0 ? 2 : 0)
                    : (s.TeamId > 0 && s.EnemyTeamId == 0 ? 1 : 0);
                if (holder == 0 || s.BuildingBelong == holder || s.BuildingBelong == 3) continue;
                int result = CaptureResult(s.BuildingId, holder);
                if (s.BuildingBelong == result) continue;
                s.BuildingBelong = result;
                changed[s.SpotId.ToString()] = result;
            }
            return changed;
        }
    

        /// <summary>building_defender_change: spot -> defender strength of the building after it changed hands.</summary>
        private static void AddBuildingDefenders(ActiveMissionState m, Dictionary<string, int> changed, Dictionary<string, object> section)
        {
            if (changed.Count == 0) return;
            var def = new Dictionary<string, int>();
            foreach (var kv in changed)
            {
                var spot = m.Spots.FirstOrDefault(s => s.SpotId.ToString() == kv.Key);
                if (spot == null) continue;
                def[kv.Key] = kv.Value == 3 ? 0 : Tables.Row("building", spot.BuildingId).Int("defender");
            }
            section["building_defender_change"] = def;
        }

        /// <summary>Betray / transform checks; changed allies are reported and stay changed for the mission.</summary>
        private static void AddAllyChanges(PlayerAccount p, ActiveMissionState m, Dictionary<string, object>? section0 = null)
        {
            var (betrayed, transformed) = UpdateAllyStates(p, m);
            var section = section0;
            if (section == null)
            {
                // battleFinish builds its section later; keep the result until then
                if (betrayed.Count > 0) p.Extra["pending_ally_betray"] = Newtonsoft.Json.JsonConvert.SerializeObject(betrayed);
                if (transformed.Count > 0) p.Extra["pending_ally_transform"] = Newtonsoft.Json.JsonConvert.SerializeObject(transformed);
                return;
            }
            foreach (var (key, list) in new[] { ("ally_instance_betray", betrayed), ("ally_instance_transform", transformed) })
            {
                var pendingKey = key == "ally_instance_betray" ? "pending_ally_betray" : "pending_ally_transform";
                if (p.Extra.TryGetValue(pendingKey, out var pending))
                {
                    list.InsertRange(0, Newtonsoft.Json.JsonConvert.DeserializeObject<List<object>>(pending) ?? new List<object>());
                    p.Extra.Remove(pendingKey);
                }
                if (list.Count > 0) section[key] = list;
            }
            if (betrayed.Count + transformed.Count > 0) section["ally_instance_info"] = AllyInstanceInfo(m);
        }
    }
}
