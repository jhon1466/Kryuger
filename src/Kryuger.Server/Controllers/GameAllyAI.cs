using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Allied / third-party echelons act after the enemy, during the enemy phase (official reply field ally_move).
    /// The AI code is the one picked from ally_team.ai for the ally's current type (101 patrol, 102 alert,
    /// 103 expand, 1/999 standby...). Move items use the same shape as the official night_enemy items
    /// (GFAlarm MissionData.cs), which carry ally_instance_id for allied units.
    /// Allies do not walk into nodes held by enemies or by other units, so no ally-vs-enemy battle is produced.
    /// </summary>
    public partial class GameController
    {
        private List<object> RunAllyTurn(ActiveMissionState m)
        {
            var moves = new List<object>();
            if (m.Allies.Count == 0) return moves;
            var g = MissionGraph(m.MissionId);
            var spots = m.Spots.ToDictionary(s => s.SpotId);
            var rng = new Random(m.Turn * 104729 + m.MissionId);

            foreach (var ally in m.Allies.OrderBy(a => a.Id))
            {
                var at = m.Spots.FirstOrDefault(s => s.AllyInstanceIds.Contains(ally.Id));
                if (at == null) continue;
                int ai = int.TryParse(ally.Ai, out int v) ? v : 0;
                bool Free(int n) => spots.TryGetValue(n, out var t) && t.EnemyTeamId == 0 && t.TeamId == 0 && t.AllyInstanceIds.Count == 0;
                var neighbours = (g.GetValueOrDefault(at.SpotId) ?? new List<int>()).Where(Free).ToList();
                int? to = null;
                switch (ai)
                {
                    case 101: case 104: case 201: case 2: case 4:
                        if (neighbours.Count > 0 && rng.NextDouble() < 0.7) to = neighbours[rng.Next(neighbours.Count)];
                        break;
                    case 102: case 202:
                    {
                        // alert: approach enemies within range (ai_para may be "spot,range,..." or just the range)
                        var nums = Numbers(ally.AiPara);
                        int range = nums.Count >= 2 ? (int)nums[1] : nums.Count == 1 ? (int)nums[0] : 1;
                        var dist = Distances(g, at.SpotId);
                        var threats = m.Spots.Where(s => s.EnemyTeamId > 0 && dist.GetValueOrDefault(s.SpotId, 99) <= Math.Max(1, range))
                            .Select(s => s.SpotId).ToList();
                        if (threats.Count > 0) to = StepToward(g, at.SpotId, threats, Free);
                        break;
                    }
                    case 103:
                        to = StepToward(g, at.SpotId, m.Spots.Where(s => s.Belong == 2).Select(s => s.SpotId).ToList(), Free);
                        break;
                    default: // 0/1/999 standby / garrison
                        break;
                }
                if (to == null || !spots.TryGetValue(to.Value, out var dest)) continue;
                at.AllyInstanceIds.Remove(ally.Id);
                dest.AllyInstanceIds.Add(ally.Id);
                moves.Add(new
                {
                    enemy_team_id = ally.AllyEnemy.ToString(), from_spot_id = at.SpotId.ToString(), to_spot_id = dest.SpotId.ToString(),
                    boss_hp = "0", enemy_hp_percent = "1", enemy_instance_id = "0", enemy_birth_turn = "1",
                    enemy_ai = ally.Ai, enemy_ai_para = ally.AiPara, hostage_id = "0", hostage_hp = "0",
                    squad_instance_id = "0", ally_instance_id = ally.Id
                });
            }
            return moves;
        }
    }
}
