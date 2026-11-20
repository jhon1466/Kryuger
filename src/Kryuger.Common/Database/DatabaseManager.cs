using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Kryuger.Common.Models;
using LiteDB;

namespace Kryuger.Common.Database
{
    public class DatabaseManager : IDisposable
    {
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<PlayerAccount> _players;

        public DatabaseManager(string dbPath = "gfl_data.db")
        {
            string dir = Path.GetDirectoryName(dbPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _db = new LiteDatabase(dbPath);
            _players = _db.GetCollection<PlayerAccount>("players");
            _players.EnsureIndex(x => x.OpenId);
            _players.EnsureIndex(x => x.SteamId);
        }

        public PlayerAccount GetOrCreatePlayer(string openId, string steamId = "")
        {
            var player = _players.FindOne(x => x.OpenId == openId || (!string.IsNullOrEmpty(steamId) && x.SteamId == steamId));
            if (player != null)
            {
                player.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(steamId) && string.IsNullOrEmpty(player.SteamId))
                    player.SteamId = steamId;
                _players.Update(player);
                return player;
            }

            // New commanders start like on the official server (captured new account): level 1, 2000 of each
            // resource, 100 dolls / 200 equipment / 4 echelons, no gems and no free dolls.
            // The three starter handguns are handed out by the game server on the first user info request.
            long newId = _players.Count() > 0 ? (_players.Max(x => x.Id) + 1) : 100001;
            if (newId < 100001) newId = 100001;

            player = new PlayerAccount
            {
                Id = newId,
                OpenId = openId,
                SteamId = steamId,
                SignKey = Guid.NewGuid().ToString("N")[..16],
                Name = "",
                Level = 1,
                Experience = 0,
                Gem = 0,
                GemPay = 0,
                Ammo = 2000,
                Mre = 2000,
                Part = 2000,
                Mp = 2000,
                Bp = 0,
                MaxGun = 100,
                MaxEquip = 200,
                MaxTeam = 4,
                CollectionSeeded = true
            };
            player.Extra["official_start"] = "1";
            player.Extra["starter_pending"] = "1";

            _players.Insert(player);
            return player;
        }

        public List<PlayerAccount> AllPlayers() => _players.FindAll().ToList();

        public bool DeletePlayer(long id) => _players.Delete(id);

        public PlayerAccount? GetPlayerById(long id)
        {
            return _players.FindById(id);
        }

        public void EnsureAllGunsAndSkins(PlayerAccount player, IEnumerable<int> allGunIds, IEnumerable<int> allSkinIds)
        {
            bool changed = false;

            // Accounts created with the official start never receive the old test collection or limits.
            if (player.Extra.ContainsKey("official_start")) return;

            if (player.MaxGun < 1000)
            {
                player.MaxGun = 1000;
                changed = true;
            }

            if (player.MaxEquip < 1000)
            {
                player.MaxEquip = 1000;
                changed = true;
            }

            if (!player.Items.TryGetValue("13", out int renameCards) || renameCards < 99)
            {
                player.Items["13"] = 99;
                changed = true;
            }

            // One-time fix: starter echelons were created with location/position swapped.
            if (!player.Extra.ContainsKey("fix_loc_swap"))
            {
                var team = player.Guns.Where(g => g.TeamId > 0).ToList();
                foreach (var grp in team.GroupBy(g => g.TeamId))
                    if (grp.Any(g => g.Location > 5))
                        foreach (var g in grp) (g.Location, g.Position) = (g.Position, g.Location);
                player.Extra["fix_loc_swap"] = "1";
                changed = true;
            }

            // One-time fix: starter echelons used grid cells (4,1,2) that do not exist in the 3x3 formation
            // grid (valid cells: 7,8,9,12,13,14,17,18,19), so the client found no valid leader.
            if (!player.Extra.ContainsKey("fix_pos_cells"))
            {
                int[] valid = { 7, 8, 9, 12, 13, 14, 17, 18, 19 };
                foreach (var grp in player.Guns.Where(g => g.TeamId > 0).GroupBy(g => g.TeamId))
                    if (grp.Any(g => !valid.Contains(g.Position)))
                    {
                        int i = 0;
                        foreach (var g in grp.OrderBy(g => g.Location)) g.Position = valid[Math.Min(i++, valid.Length - 1)];
                    }
                player.Extra["fix_pos_cells"] = "1";
                changed = true;
            }

            // Seed the full collection only once. Re-running it on every login used to delete
            // duplicate (newly built) dolls and resurrect retired ones, discarding player changes.
            if (player.CollectionSeeded)
            {
                if (changed) SavePlayer(player);
                return;
            }
            player.CollectionSeeded = true;
            changed = true;
            var seenGunIds = new HashSet<int>(player.Guns.Select(g => g.GunId));
            var existingGunIds = seenGunIds;
            long nextId = player.Guns.Count > 0 ? player.Guns.Max(g => g.Id) + 1 : 1;

            foreach (int gunId in allGunIds.OrderBy(id => id))
            {
                if (existingGunIds.Add(gunId))
                {
                    player.Guns.Add(new PlayerGun
                    {
                        Id = nextId++,
                        UserId = player.Id,
                        GunId = gunId,
                        GunLevel = 100,
                        TeamId = 0,
                        Location = 1,
                        Position = 0,
                        Number = 5,
                        Skill1 = 10,
                        Skill2 = 10,
                        Life = 1000,
                        Favor = 1000000,
                        MaxFavor = 2000000,
                        FavorTopLimit = 2000000
                    });
                    changed = true;
                }
            }

            var existingSkinIds = new HashSet<int>(player.Skins);
            foreach (int skinId in allSkinIds.OrderBy(id => id))
            {
                if (existingSkinIds.Add(skinId))
                {
                    player.Skins.Add(skinId);
                    changed = true;
                }
            }

            if (changed)
            {
                SavePlayer(player);
            }
        }

        public void SavePlayer(PlayerAccount player)
        {
            lock (_writeLock)
            {
                _players.Update(player);
                _db.Checkpoint();
            }
        }

        private readonly object _writeLock = new();

        public void Dispose()
        {
            _db.Dispose();
        }
    }
}
