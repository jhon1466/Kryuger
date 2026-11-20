using System;
using System.Collections.Generic;
using System.Linq;
using Kryuger.Common.Models;
using Kryuger.Data;
using Newtonsoft.Json;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Enemy turn: every enemy echelon acts once according to its AI (enemy_team.ai, team_ai.json texts):
    ///   0     mission default (mission.enemy_ai_type: 1 standby, 2/4 random, 3 advance on the command post)
    ///   1/999 or if_stay=1   standby / garrison
    ///   101/201/104 patrol   wander, never more than 1 node from where it started
    ///   102/202 alert        stay until a Griffin echelon is within ai_content nodes, then close in
    ///   103 expand           move toward the nearest node it does not control
    ///   106 lock on / 1(force 3) eliminate   chase the nearest Griffin echelon
    /// Moves go one node per turn along spot.route. Entering a Griffin echelon's node starts a battle, which the
    /// client plays and reports through Mission/battleFinish. Taking a Griffin command post loses the mission.
    /// Reply item format (hoxy SMissionEndTurn.enemy_move): from_spot_id, to_spot_id, enemy_ai, enemy_ai_para,
    /// hostage_id, hostage_hp, squad_instance_id.
    /// </summary>
    public partial class GameController
    {
        private const int Griffin = 1, Enemy = 2;

        private static Dictionary<int, List<int>> MissionGraph(int missionId)
        {
            var g = new Dictionary<int, List<int>>();
            void Link(int a, int b)
            {
                if (!g.TryGetValue(a, out var la)) g[a] = la = new List<int>();
                if (!la.Contains(b)) la.Add(b);
            }
            foreach (var row in Tables.Get("spot").Values.Where(r => r.Int("mission_id") == missionId))
            {
                int id = row.Int("id");
                if (!g.ContainsKey(id)) g[id] = new List<int>();
                foreach (var part in (row["route"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part, out int to)) { Link(id, to); Link(to, id); }
            }
            return g;
        }

        /// <summary>Breadth-first distances from <paramref name="from"/>.</summary>
        private static Dictionary<int, int> Distances(Dictionary<int, List<int>> g, int from)
        {
            var d = new Dictionary<int, int> { [from] = 0 };
            var q = new Queue<int>(); q.Enqueue(from);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                foreach (int n in g.GetValueOrDefault(c) ?? new List<int>())
                    if (!d.ContainsKey(n)) { d[n] = d[c] + 1; q.Enqueue(n); }
            }
            return d;
        }

        /// <summary>Next node on a shortest path from <paramref name="from"/> toward any of <paramref name="goals"/>.</summary>
        private static int? StepToward(Dictionary<int, List<int>> g, int from, ICollection<int> goals, Func<int, bool> canEnter)
        {
            if (goals.Count == 0) return null;
            int best = int.MaxValue; int? step = null;
            foreach (int n in g.GetValueOrDefault(from) ?? new List<int>())
            {
                if (!canEnter(n)) continue;
                var d = Distances(g, n);
                int dist = goals.Where(d.ContainsKey).Select(x => d[x]).DefaultIfEmpty(int.MaxValue).Min();
                if (dist < best) { best = dist; step = n; }
            }
            return step;
        }

        private static Dictionary<int, int> EnemyOrigins(PlayerAccount p)
        {
            try { return JsonConvert.DeserializeObject<Dictionary<int, int>>(p.Extra.GetValueOrDefault("enemy_origin") ?? "") ?? new(); }
            catch { return new(); }
        }

        /// <summary>Runs the enemy turn; returns the moves (official item shape) and the nodes that changed hands.</summary>
        private (List<object> moves, Dictionary<string, int> changeBelong, bool lost) RunEnemyTurn(PlayerAccount p, ActiveMissionState m)
        {
            var moves = new List<object>();
            var changed = new Dictionary<string, int>();
            bool lost = false;
            var g = MissionGraph(m.MissionId);
            var spots = m.Spots.ToDictionary(s => s.SpotId);
            var spotRows = Tables.Get("spot");
            int missionAi = Tables.Row("mission", m.MissionId).Int("enemy_ai_type", 1);
            var origins = EnemyOrigins(p);
            var rng = new Random(m.Turn * 7919 + m.MissionId);

            // Griffin command posts held at mission start: losing one of them loses the mission
            var griffinPosts = m.Spots.Where(s => Tables.Row("spot", s.SpotId).Int("type") == 1 && Tables.Row("spot", s.SpotId).Int("belong") == Griffin)
                .Select(s => s.SpotId).ToHashSet();

            foreach (var s in m.Spots.Where(s => s.EnemyTeamId > 0 && s.EnemyInstanceId > 0).OrderBy(s => s.EnemyInstanceId).ToList())
            {
                if (s.EnemyTeamId == 0) continue; // already moved away this turn
                var team = Tables.Row("enemy_team", s.EnemyTeamId);
                int ai = team.Int("ai");
                origins.TryAdd(s.EnemyInstanceId, s.SpotId);
                if (team.Int("if_stay") == 1 || ai == 1 || ai == 999) continue;
                if (ai == 0) ai = missionAi switch { 3 => 3, 2 => 2, 4 => 2, _ => 1 };
                if (ai == 1) continue;

                bool Free(int n) => spots.TryGetValue(n, out var t) && t.EnemyTeamId == 0;
                var griffinTeams = m.Spots.Where(x => x.TeamId > 0).Select(x => x.SpotId).ToList();
                var dist = Distances(g, s.SpotId);
                int? to = null;

                switch (ai)
                {
                    case 2: // random
                    case 101: case 104: case 201: // patrol around the origin
                    {
                        int origin = origins[s.EnemyInstanceId];
                        var od = Distances(g, origin);
                        var options = (g.GetValueOrDefault(s.SpotId) ?? new List<int>())
                            .Where(n => Free(n) && (ai == 2 || od.GetValueOrDefault(n, 99) <= 1)).ToList();
                        if (options.Count > 0 && rng.NextDouble() < 0.7) to = options[rng.Next(options.Count)];
                        break;
                    }
                    case 3: // advance on the Griffin command post
                        to = StepToward(g, s.SpotId, griffinPosts.Where(spots.ContainsKey).ToList(), Free);
                        break;
                    case 102: case 202: case 203: // alert: react to Griffin echelons within range
                    {
                        int range = int.TryParse(team?["ai_content"]?.ToString(), out int r) && r > 0 ? r : 2;
                        var inRange = griffinTeams.Where(x => dist.GetValueOrDefault(x, 99) <= range).ToList();
                        if (inRange.Count > 0) to = StepToward(g, s.SpotId, inRange, Free);
                        break;
                    }
                    case 103: // expand toward nodes it does not hold
                    {
                        var targets = m.Spots.Where(x => x.Belong != Enemy).Select(x => x.SpotId).ToList();
                        to = StepToward(g, s.SpotId, targets, Free);
                        break;
                    }
                    default: // 106 lock on and other hunters: nearest Griffin echelon
                        to = StepToward(g, s.SpotId, griffinTeams, Free);
                        break;
                }

                if (to == null || !spots.TryGetValue(to.Value, out var dest)) continue;
                moves.Add(new
                {
                    from_spot_id = s.SpotId, to_spot_id = dest.SpotId, enemy_ai = ai, enemy_ai_para = 0,
                    hostage_id = 0, hostage_hp = 0, squad_instance_id = 0
                });
                dest.EnemyTeamId = s.EnemyTeamId; dest.EnemyInstanceId = s.EnemyInstanceId; dest.BossHp = s.BossHp;
                s.EnemyTeamId = 0; s.EnemyInstanceId = 0; s.BossHp = 0;
                if (dest.TeamId == 0 && dest.Belong != Enemy)
                {
                    dest.Belong = Enemy;
                    changed[dest.SpotId.ToString()] = Enemy;
                    if (griffinPosts.Contains(dest.SpotId)) lost = true;
                }
                // a Griffin echelon on dest means a battle: the client plays it and reports Mission/battleFinish
            }

            p.Extra["enemy_origin"] = JsonConvert.SerializeObject(origins);
            return (moves, changed, lost);
        }
    }
}
