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

            // Create new player with starter T-Dolls and starter items
            long newId = _players.Count() > 0 ? (_players.Max(x => x.Id) + 1) : 100001;
            if (newId < 100001) newId = 100001;

            player = new PlayerAccount
            {
                Id = newId,
                OpenId = openId,
                SteamId = steamId,
                SignKey = Guid.NewGuid().ToString("N")[..16],
                Name = "",
                Level = 100,
                Experience = 2500000,
                Gem = 100000,
                GemPay = 50000,
                Ammo = 300000,
                Mre = 300000,
                Part = 300000,
                Mp = 300000,
                Bp = 9999,
                MaxGun = 500,
                MaxEquip = 800,
                MaxTeam = 10
            };

            // Starter items: T-Doll contracts (item 1), Equipment contracts (item 2), Quick Production (item 3), Quick Repair (item 4), Core (item 41), Calibration (item 42), Tokens (item 50)
            player.Items["1"] = 9999;
            player.Items["2"] = 9999;
            player.Items["3"] = 9999;
            player.Items["4"] = 9999;
            player.Items["41"] = 9999;
            player.Items["42"] = 9999;
            player.Items["50"] = 9999;

            // Starter Iconic Dolls (AR Team + popular dolls)
            // 55: M4A1, 56: M4 SOPMOD II, 57: ST AR-15, 54: M16A1, 143: RO635, 113: UMP45, 114: UMP9, 106: FAL, 62: G41, 197: Carcano M91/38
            int[] starterGunIds = { 55, 56, 57, 54, 143, 113, 114, 106, 62, 197 };
            long gunInstId = 1;

            for (int i = 0; i < starterGunIds.Length; i++)
            {
                var gun = new PlayerGun
                {
                    Id = gunInstId++,
                    UserId = player.Id,
                    GunId = starterGunIds[i],
                    GunLevel = 100,
                    GunExp = 0,
                    Number = 5,
                    Life = 1000,
                    Favor = 1000000,
                    MaxFavor = 2000000,
                    Skill1 = 10,
                    Skill2 = 10,
                    TeamId = (i < 5) ? 1 : 0,
                    // location = slot order in the echelon (1-5), position = formation grid cell (official format)
                    Location = (i < 5) ? (i + 1) : 1,
                    Position = (i < 5) ? (new int[] { 7, 8, 9, 12, 13 }[i]) : 0
                };
                player.Guns.Add(gun);
            }

            _players.Insert(player);
            return player;
        }

        public List<PlayerAccount> AllPlayers() => _players.FindAll().ToList();

        public PlayerAccount? GetPlayerById(long id)
        {
            return _players.FindById(id);
        }

        public void EnsureAllGunsAndSkins(PlayerAccount player, IEnumerable<int> allGunIds, IEnumerable<int> allSkinIds)
        {
            bool changed = false;

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
