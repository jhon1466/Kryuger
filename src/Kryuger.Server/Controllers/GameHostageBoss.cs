using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Hostages and bosses.
    /// Hostages: spot.hostage_info = "gunId,hp". The hostage waits on its spot; the first Griffin echelon that enters
    /// picks it up and it moves with that echelon. Every battle the escorting echelon fights costs the hostage 1 HP
    /// (0 = mission lost). Bringing it to a Griffin command post rescues it (win condition 1404 "save N hostages").
    /// Bosses: Mission/battleFinish reports the boss's remaining HP (boss_hp, hoxy CMissionBattleFinish); it is kept
    /// on the spot and sent back in spot_act_info / enemy_instance_info so the next battle starts from it.
    /// </summary>
    public partial class GameController
    {
        private static void PlaceHostages(ActiveMissionState m)
        {
            foreach (var s in m.Spots)
            {
                var info = Tables.Row("spot", s.SpotId)?["hostage_info"]?.ToString() ?? "";
                var parts = info.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[0], out int gid) && gid > 0)
                {
                    int hp = parts.Length > 1 && int.TryParse(parts[1], out int h) && h > 0 ? h : 1;
                    s.HostageId = gid; s.HostageHp = hp; s.HostageMaxHp = hp;
                }
            }
        }

        private static bool IsGriffinCommandPost(int spotId)
        {
            var row = Tables.Row("spot", spotId);
            return row.Int("type") == 1 && row.Int("belong") == 1;
        }

        private static int Rescued(PlayerAccount p) =>
            int.TryParse(p.Extra.GetValueOrDefault("mission_rescued"), out int r) ? r : 0;

        /// <summary>Echelon moved from -> to: an escorted hostage moves with it; reaching a command post rescues it.</summary>
        private static void MoveHostage(PlayerAccount p, MissionSpotState from, MissionSpotState to)
        {
            // a hostage moves only with the echelon escorting it; one held by an enemy stays until freed
            if (from.HostageId > 0 && from.HostageEscorted && to.HostageId == 0)
            {
                to.HostageId = from.HostageId; to.HostageHp = from.HostageHp; to.HostageMaxHp = from.HostageMaxHp; to.HostageEscorted = true;
                from.HostageId = 0; from.HostageHp = 0; from.HostageMaxHp = 0; from.HostageEscorted = false;
            }
            PickUpHostage(to);
            DeliverHostage(p, to);
        }

        /// <summary>A Griffin echelon on a hostage's spot with no enemy left takes charge of it.</summary>
        private static void PickUpHostage(MissionSpotState s)
        {
            if (s.HostageId > 0 && s.TeamId > 0 && s.EnemyTeamId == 0) s.HostageEscorted = true;
        }

        private static void DeliverHostage(PlayerAccount p, MissionSpotState s)
        {
            PickUpHostage(s);
            if (s.HostageId > 0 && s.TeamId > 0 && s.EnemyTeamId == 0 && IsGriffinCommandPost(s.SpotId))
            {
                p.Extra["mission_rescued"] = (Rescued(p) + 1).ToString();
                s.HostageId = 0; s.HostageHp = 0; s.HostageMaxHp = 0; s.HostageEscorted = false;
            }
        }

        /// <summary>After a battle on <paramref name="spot"/>: boss HP and hostage damage. Returns true if the hostage died.</summary>
        private static bool AfterBattle(MissionSpotState spot, JObject? b, bool enemyDied)
        {
            if (enemyDied) spot.BossHp = 0;
            else if (b?["boss_hp"] != null && int.TryParse(b["boss_hp"]!.ToString(), out int hp) && hp > 0) spot.BossHp = hp;

            // only an escorted hostage is caught in the fighting; freeing one from its captors does not hurt it
            if (spot.HostageId > 0 && spot.HostageEscorted && spot.TeamId > 0)
            {
                spot.HostageHp = Math.Max(0, spot.HostageHp - 1);
                if (spot.HostageHp == 0)
                {
                    spot.HostageId = 0; spot.HostageMaxHp = 0; spot.HostageEscorted = false;
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, object> LoseResult(PlayerAccount p, ActiveMissionState m)
        {
            int kills = int.TryParse(p.Extra.GetValueOrDefault("mission_kills"), out int k) ? k : 0;
            return new Dictionary<string, object>
            {
                ["win_type_ids"] = new List<object>(), ["turn"] = m.Turn.ToString(), ["enemydie_num"] = kills.ToString(),
                ["enemydie_num_killbyfriend"] = "0", ["gundie_num"] = "0", ["mysquad_die_num"] = "0", ["sangvisdie_num"] = "0",
                ["mica_client_log"] = new { mid = m.MissionId, uid = p.Id, type = "type5_score" }
            };
        }
    }
}
