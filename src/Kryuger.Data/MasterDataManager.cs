using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kryuger.Data
{
    public class MasterGunInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string EnName { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public int Type { get; set; } // 1: HG, 2: SMG, 3: RF, 4: AR, 5: MG, 6: SG
        public int Rank { get; set; } // 2..5 stars
        public int DevelopDuration { get; set; } // Seconds to craft
        public int BaseAmmo { get; set; }
        public int BaseMre { get; set; }
    }

    public class MasterEquipInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Rank { get; set; }
        public int Type { get; set; }
    }

    public class MasterSkinInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int FitGun { get; set; }
    }

    public class MasterSpotInfo
    {
        public int Id { get; set; }
        public int MissionId { get; set; }
        public int Type { get; set; }
        public int Belong { get; set; }
        public int EnemyTeamId { get; set; }
        public int AllyTeamId { get; set; }
        public string RandomGet { get; set; } = string.Empty;
    }

    public class MasterMissionInfo
    {
        public int Id { get; set; }
        public int Difficulty { get; set; }
        public int ExpParameter { get; set; }
    }

    public class MasterDataManager
    {
        public static MasterDataManager Instance { get; } = new();

        public Dictionary<int, MasterGunInfo> Guns { get; private set; } = new();
        public Dictionary<int, MasterEquipInfo> Equips { get; private set; } = new();
        public Dictionary<int, MasterSkinInfo> Skins { get; private set; } = new();
        public Dictionary<int, List<MasterSpotInfo>> MissionSpots { get; private set; } = new();
        public Dictionary<int, MasterMissionInfo> Missions { get; private set; } = new();
        public bool IsLoaded { get; private set; }

        public void LoadData(string baseDataPath)
        {
            string stcDir = Path.Combine(baseDataPath, "stc");
            if (!Directory.Exists(stcDir))
            {
                for (var cur = new DirectoryInfo(AppContext.BaseDirectory); cur != null; cur = cur.Parent)
                {
                    string candidate = Path.Combine(cur.FullName, "data", "gf-data-us", "stc");
                    if (Directory.Exists(candidate)) { stcDir = candidate; break; }
                }
            }
            if (!Directory.Exists(stcDir) && Directory.Exists(@"e:\Documentos\gf\data\gf-data-us\stc"))
            {
                stcDir = @"e:\Documentos\gf\data\gf-data-us\stc";
            }

            if (!Directory.Exists(stcDir))
            {
                Console.WriteLine($"[MasterData] Directory not found: {stcDir}, skipping master table load.");
                return;
            }

            string gunFile = Path.Combine(stcDir, "gun.json");
            if (File.Exists(gunFile))
            {
                try
                {
                    string json = File.ReadAllText(gunFile);
                    var list = JsonConvert.DeserializeObject<List<JObject>>(json);
                    if (list != null)
                    {
                        foreach (var j in list)
                        {
                            int id = j.Value<int>("id");
                            Guns[id] = new MasterGunInfo
                            {
                                Id = id,
                                Name = j.Value<string>("name") ?? "",
                                EnName = j.Value<string>("en_name") ?? "",
                                Code = j.Value<string>("code") ?? "",
                                Type = j.Value<int>("type"),
                                Rank = j.Value<int>("rank"),
                                DevelopDuration = j.Value<int>("develop_duration"),
                                BaseAmmo = j.Value<int>("baseammo"),
                                BaseMre = j.Value<int>("basemre")
                            };
                        }
                    }
                    Console.WriteLine($"[MasterData] Loaded {Guns.Count} T-Dolls from gun.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MasterData] Error loading gun.json: {ex.Message}");
                }
            }

            string equipFile = Path.Combine(stcDir, "equip.json");
            if (File.Exists(equipFile))
            {
                try
                {
                    string json = File.ReadAllText(equipFile);
                    var list = JsonConvert.DeserializeObject<List<JObject>>(json);
                    if (list != null)
                    {
                        foreach (var j in list)
                        {
                            int id = j.Value<int>("id");
                            Equips[id] = new MasterEquipInfo
                            {
                                Id = id,
                                Name = j.Value<string>("name") ?? "",
                                Rank = j.Value<int>("rank"),
                                Type = j.Value<int>("type")
                            };
                        }
                    }
                    Console.WriteLine($"[MasterData] Loaded {Equips.Count} Equipment items from equip.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MasterData] Error loading equip.json: {ex.Message}");
                }
            }

            string skinFile = Path.Combine(stcDir, "skin.json");
            if (File.Exists(skinFile))
            {
                try
                {
                    string json = File.ReadAllText(skinFile);
                    var list = JsonConvert.DeserializeObject<List<JObject>>(json);
                    if (list != null)
                    {
                        foreach (var j in list)
                        {
                            int id = j.Value<int>("id");
                            Skins[id] = new MasterSkinInfo
                            {
                                Id = id,
                                Name = j.Value<string>("name") ?? "",
                                FitGun = j.Value<int>("fit_gun")
                            };
                        }
                    }
                    Console.WriteLine($"[MasterData] Loaded {Skins.Count} Skins from skin.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MasterData] Error loading skin.json: {ex.Message}");
                }
            }

            string spotFile = Path.Combine(stcDir, "spot.json");
            if (File.Exists(spotFile))
            {
                try
                {
                    string json = File.ReadAllText(spotFile);
                    var list = JsonConvert.DeserializeObject<List<JObject>>(json);
                    if (list != null)
                    {
                        foreach (var j in list)
                        {
                            int missionId = j.Value<int>("mission_id");
                            if (!MissionSpots.TryGetValue(missionId, out var spots))
                            {
                                spots = new List<MasterSpotInfo>();
                                MissionSpots[missionId] = spots;
                            }
                            spots.Add(new MasterSpotInfo
                            {
                                Id = j.Value<int>("id"),
                                MissionId = missionId,
                                Type = j.Value<int>("type"),
                                Belong = j.Value<int>("belong"),
                                EnemyTeamId = j.Value<int>("enemy_team_id"),
                                AllyTeamId = j.Value<int>("ally_team_id"),
                                RandomGet = j.Value<string>("random_get") ?? ""
                            });
                        }
                    }
                    Console.WriteLine($"[MasterData] Loaded spots for {MissionSpots.Count} missions from spot.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MasterData] Error loading spot.json: {ex.Message}");
                }
            }

            string missionFile = Path.Combine(stcDir, "mission.json");
            if (File.Exists(missionFile))
            {
                try
                {
                    string json = File.ReadAllText(missionFile);
                    var list = JsonConvert.DeserializeObject<List<JObject>>(json);
                    if (list != null)
                    {
                        foreach (var j in list)
                        {
                            int id = j.Value<int>("id");
                            Missions[id] = new MasterMissionInfo
                            {
                                Id = id,
                                Difficulty = j.Value<int>("difficulty"),
                                ExpParameter = j.Value<int>("exp_parameter")
                            };
                        }
                    }
                    Console.WriteLine($"[MasterData] Loaded {Missions.Count} missions from mission.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MasterData] Error loading mission.json: {ex.Message}");
                }
            }

            IsLoaded = true;
        }

        public MasterGunInfo? RollGFLGacha(int mp, int ammo, int mre, int part)
        {
            // Real GFL Production Recipe Algorithm
            // Filter guns by craftability (develop_duration > 0)
            var craftable = Guns.Values.Where(g => g.DevelopDuration > 0).ToList();
            if (craftable.Count == 0)
                return Guns.Values.FirstOrDefault();

            // Total resources determines tier and doll class eligibility:
            // HG: sum <= 920
            // SMG: mp >= 400 && ammo >= 400
            // AR: mp >= 400 && mre >= 400
            // RF: mp >= 400 && ammo >= 400 && mre >= 400
            // MG: mp >= 400 && ammo >= 600 && part >= 300
            // SG: Heavy production
            int total = mp + ammo + mre + part;
            var pool = new List<MasterGunInfo>();

            foreach (var gun in craftable)
            {
                bool eligible = gun.Type switch
                {
                    1 => total <= 920, // HG
                    2 => mp >= 400 && ammo >= 400, // SMG
                    3 => mp >= 400 && mre >= 400, // RF
                    4 => mp >= 300 && ammo >= 300 && mre >= 300, // AR
                    5 => mp >= 400 && ammo >= 600 && part >= 300, // MG
                    _ => true
                };

                if (eligible)
                    pool.Add(gun);
            }

            if (pool.Count == 0)
                pool = craftable;

            var rnd = new Random();
            return pool[rnd.Next(pool.Count)];
        }
    }
}

