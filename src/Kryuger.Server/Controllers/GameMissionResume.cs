using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;
using Newtonsoft.Json;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Index/index mission_act_info: the mission in progress, so a client that restarts mid-mission resumes it.
    /// Field list and encoding (nested values as JSON strings) follow a real official reply
    /// (kchang06/GFAlarm, GFAlarm/Data/Element/MissionActInfo.cs).
    /// </summary>
    public partial class GameController
    {
        private static object? BuildMissionActInfo(PlayerAccount p)
        {
            var m = p.ActiveMission;
            if (m == null) return null;
            string S(object o) => JsonConvert.SerializeObject(o);
            int kills = int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int k) ? k : 0;
            var killTeams = KillsByTeam(p).Keys.ToList();
            var spots = BuildSpotActList(m).ToDictionary(d => d["spot_id"].ToString()!, d => (object)d);
            var teams = m.Spots.Where(s => s.TeamId > 0).Select(s => s.TeamId).Distinct().OrderBy(t => t).ToList();
            var gunLevels = p.Guns.Where(g => g.TeamId > 0 && teams.Contains(g.TeamId))
                .ToDictionary(g => g.GunId.ToString(), g => g.GunLevel.ToString());
            var boss = m.Spots.FirstOrDefault(s => s.BossHp > 0);
            var allyInfo = AllyInstanceInfo(m);

            return new Dictionary<string, object>
            {
                ["mission_id"] = m.MissionId.ToString(),
                ["user_id"] = p.Id.ToString(),
                ["turn"] = m.Turn.ToString(),
                ["turn_belong"] = "1",
                ["ap"] = m.Ap.ToString(),
                ["spot"] = S(spots),
                ["ally_instance_info"] = S(allyInfo),
                ["end_ally_instance_id"] = (m.Allies.Count + 1).ToString(),
                ["end_enemy_instance_id"] = (m.Spots.Select(s => s.EnemyInstanceId).DefaultIfEmpty(0).Max() + 1).ToString(),
                ["end_squad_instance_id"] = "1",
                ["join_teams"] = string.Join(",", teams),
                ["reinforce_teams"] = string.Join(",", teams),
                ["enemydie_num"] = kills.ToString(),
                ["enemydie_this_turn"] = "[]",
                ["enemydie_tigger_team_colour"] = S(killTeams),
                ["enemydie_num_killbyfriend"] = "0",
                ["enemydie_num_killbymyside"] = "0",
                ["enemydie_num_killbyhostage"] = "0",
                ["gundie_num"] = "0",
                ["mysquad_die_num"] = "0",
                ["save_hostage"] = Rescued(p).ToString(),
                ["boss_hp"] = (boss?.BossHp ?? 0).ToString(),
                ["boss_max_hp"] = "0",
                ["is_win"] = "0",
                ["lose_type"] = "1",
                ["type5_score"] = "0",
                ["battle_count"] = p.Extra.GetValueOrDefault("mission_battles") ?? "0",
                ["last_battle_finish_time"] = Now.ToString(),
                ["last_battle_info"] = "",
                ["gunid_gunlev"] = S(gunLevels),
                ["gunid_gunlv"] = "",
                ["gun_average_level"] = "",
                ["building_info"] = "[]",
                ["squad_info"] = "[]",
                ["spots_change"] = "[]",
                ["defend_line_start_turn"] = "0",
                ["pass_missionevent"] = "0",
                ["spot_belong_remain_reset_count"] = "-1",
                ["friend_team_fairy"] = "[]",
                ["growenemy_with_enemydie"] = "0",
                ["reinforce_ally_team"] = "[]",
                ["reinforce_squads"] = "[]",
                ["mission_control_trigger_spot_id"] = "[]",
                ["enter_assisttype_squads"] = "[]",
                ["get_guns"] = "",
                ["fairy_skill_perform"] = "[]",
                ["fairy_skill_on_team"] = "[]",
                ["fairy_skill_on_enemy"] = "[]",
                ["fairy_skill_on_spot"] = "[]",
                ["fairy_skill_on_squad"] = "[]",
                ["mission_control"] = "{\"1\":1,\"2\":2,\"3\":3}",
                ["team_effect"] = "",
                ["end_skill_id"] = "0"
            };
        }
    }
}
