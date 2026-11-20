using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Kryuger.Common.Models;
using Kryuger.Data;
using Newtonsoft.Json;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Mission victory conditions, driven by mission.json (win_spot_id, win_turn, win_type) and
    /// mission_win_type_config.json. Condition types and their meaning come from the client texts
    /// (asset/table/mission_win_type_config.txt):
    ///   1305 defeat all enemies            1301 defeat at least N enemies
    ///   1303 defeat enemy team X (x N)     1306 engage enemy team X in battle
    ///   1101 / 1407 / 1309 an echelon reaches spot X (radar, rendezvous, evacuation point)
    ///   1102 capture every non-allied command post
    ///   1402 survive to round N            1405 keep the supply line for N turns
    /// win_type: "a|b" = a OR b, "a,b" = a AND b. Types not modelled yet (hostages, buildings...) do not block a
    /// group that also has modelled conditions; a mission with only unmodelled conditions falls back to the
    /// classic rules (enemy HQ / turn limit / no enemies left).
    /// </summary>
    public partial class GameController
    {
        private static Dictionary<string, int> KillsByTeam(PlayerAccount p)
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, int>>(p.Extra.GetValueOrDefault("mission_kill_teams") ?? "") ?? new(); }
            catch { return new(); }
        }

        /// <summary>Records a defeated enemy echelon (total and per enemy team id).</summary>
        private static void RecordKill(PlayerAccount p, int enemyTeamId)
        {
            p.Extra["mission_kills"] = ((int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int k) ? k : 0) + 1).ToString();
            var byTeam = KillsByTeam(p);
            string key = enemyTeamId.ToString();
            byTeam[key] = byTeam.GetValueOrDefault(key) + 1;
            p.Extra["mission_kill_teams"] = JsonConvert.SerializeObject(byTeam);
        }

        private static void ResetMissionCounters(PlayerAccount p)
        {
            p.Extra.Remove("mission_rescued");
            p.Extra.Remove("mission_kills");
            p.Extra.Remove("mission_kill_teams");
        }

        private static List<long> Numbers(string? s) =>
            Regex.Matches(s ?? "", @"\d+").Select(m => long.Parse(m.Value)).ToList();

        /// <summary>Ids a spot can be referred to by in win conditions.</summary>
        private static HashSet<long> SpotRefs(int spotId)
        {
            var refs = new HashSet<long> { spotId };
            var row = Tables.Row("spot", spotId);
            if (row == null) return refs;
            foreach (var f in new[] { "building_id", "special_eft" })
            {
                int v = row.Int(f);
                if (v <= 0) continue;
                refs.Add(v);
                int linked = Tables.Row("special_spot_config", v).Int("special_spot_config_id");
                if (linked > 0) refs.Add(linked);
            }
            return refs;
        }

        /// <summary>null = condition type not modelled.</summary>
        private static bool? CheckCondition(PlayerAccount p, ActiveMissionState m, int conditionId)
        {
            var cfg = Tables.Row("mission_win_type_config", conditionId);
            if (cfg == null) return null;
            string args = cfg["arguments"]?.ToString() ?? "";
            var nums = Numbers(args);
            int kills = int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int k) ? k : 0;
            var mySpots = m.Spots.Where(s => s.TeamId > 0).Select(s => (long)s.SpotId).ToHashSet();

            switch (cfg.Int("type"))
            {
                case 1305: return m.Spots.All(s => s.EnemyTeamId == 0);
                case 1301: return kills >= Math.Max(1, nums.FirstOrDefault());
                case 1303:
                case 1306:
                {
                    // "team:count[,team:count]"
                    var byTeam = KillsByTeam(p);
                    var pairs = args.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (pairs.Length == 0) return null;
                    foreach (var pair in pairs)
                    {
                        var kv = pair.Split(':');
                        int need = kv.Length > 1 && int.TryParse(kv[1], out int n) ? Math.Max(1, n) : 1;
                        if (byTeam.GetValueOrDefault(kv[0].Trim()) < need) return false;
                    }
                    return true;
                }
                case 1101:
                case 1407:
                case 1309:
                {
                    // the argument may be the spot id itself or the special spot it hosts (building / special effect /
                    // special_spot_config chain), e.g. 1407 "410302:1" = spot 71117 with building 410301 -> config 410302
                    var targets = m.Spots.Where(sp => SpotRefs(sp.SpotId).Overlaps(nums)).Select(sp => (long)sp.SpotId).ToHashSet();
                    if (targets.Count == 0) return null;
                    return targets.Any(mySpots.Contains);
                }
                case 1102:
                {
                    var spotTypes = MasterDataManager.Instance.MissionSpots.TryGetValue(m.MissionId, out var sl)
                        ? sl.ToDictionary(s => s.Id, s => s.Type) : new Dictionary<int, int>();
                    var commandPosts = m.Spots.Where(s => spotTypes.GetValueOrDefault(s.SpotId) == 1).ToList();
                    if (commandPosts.Count == 0) return null;
                    return commandPosts.All(s => s.Belong == 1 && s.EnemyTeamId == 0);
                }
                case 1406: // buildings / special nodes in a given state (capture, deactivate, reach)
                    return Check1406(m, args);
                case 1404: // rescue N hostages (bring them to a Griffin command post)
                    return Rescued(p) >= Math.Max(1, nums.FirstOrDefault());
                case 1402:
                case 1405:
                    return m.Turn >= Math.Max(1, nums.FirstOrDefault());
                default:
                    return null;
            }
        }

        private static bool MissionWon(PlayerAccount p, ActiveMissionState m)
        {
            var row = Tables.Row("mission", m.MissionId);
            string winType = row?["win_type"]?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(winType))
            {
                bool anyModelled = false;
                foreach (var group in winType.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var results = group.Split(',', ';').Select(x => x.Trim()).Where(x => int.TryParse(x, out _))
                        .Select(x => CheckCondition(p, m, int.Parse(x))).ToList();
                    var known = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
                    if (known.Count == 0) continue;
                    anyModelled = true;
                    if (known.All(x => x)) return true;
                }
                if (anyModelled) return false;
            }

            // classic rules
            var winSpots = Numbers(row?["win_spot_id"]?.ToString()).ToHashSet();
            if (winSpots.Count > 0)
                return m.Spots.Any(s => winSpots.Contains(s.SpotId) && s.TeamId > 0 && s.EnemyTeamId == 0);
            int winTurn = row.Int("win_turn");
            if (winTurn > 0) return m.Turn >= winTurn;
            return m.Spots.All(s => s.EnemyTeamId == 0);
        }
    }
}
