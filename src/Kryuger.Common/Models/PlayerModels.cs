using System;
using System.Collections.Generic;
using LiteDB;
using Newtonsoft.Json;

namespace Kryuger.Common.Models
{
    public class PlayerAccount
    {
        [BsonId]
        public long Id { get; set; }
        public string OpenId { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public string SignKey { get; set; } = string.Empty;
        public string Name { get; set; } = "Commander";
        public int Level { get; set; } = 100;
        public long Experience { get; set; } = 5000000;
        public int Gem { get; set; } = 99999;
        public int GemPay { get; set; } = 99999;
        public int Ammo { get; set; } = 300000;
        public int Mre { get; set; } = 300000;
        public int Part { get; set; } = 300000;
        public int Mp { get; set; } = 300000;
        public int Bp { get; set; } = 9999;
        public int MaxGun { get; set; } = 500;
        public int MaxEquip { get; set; } = 800;
        public int MaxTeam { get; set; } = 10;
        public int MaxBuildSlot { get; set; } = 8;
        public int MaxEquipBuildSlot { get; set; } = 8;
        public int MaxFixSlot { get; set; } = 8;
        public int MaxUpgradeSlot { get; set; } = 8;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

        public List<PlayerGun> Guns { get; set; } = new();
        public List<PlayerEquip> Equips { get; set; } = new();
        public List<int> Skins { get; set; } = new();
        public Dictionary<string, int> Items { get; set; } = new(); // ItemId -> Count
        public Dictionary<string, int> Gifts { get; set; } = new(); // GiftId -> Count
        public List<DevelopSlot> DevelopSlots { get; set; } = new();
        public List<OperationAct> ActiveOperations { get; set; } = new();
        public Dictionary<string, MissionRecord> Missions { get; set; } = new(); // MissionId -> Record
        public string Guide { get; set; } = string.Empty;
        public string Adjutant { get; set; } = string.Empty;
        public string AdjutantMulti { get; set; } = string.Empty;
        public string AdjutantFairy { get; set; } = string.Empty;
        public string GameSetting { get; set; } = string.Empty;
        public string Intro { get; set; } = string.Empty;
        public string Gender { get; set; } = "0";
        public ActiveMissionState? ActiveMission { get; set; }
        public bool CollectionSeeded { get; set; }
        public List<DevelopSlot> EquipDevelopSlots { get; set; } = new();
        public List<TimedSlot> FixSlots { get; set; } = new();
        public List<TimedSlot> UpgradeSlots { get; set; } = new();
        public List<PlayerFairy> Fairies { get; set; } = new();
        public Dictionary<string, string> Extra { get; set; } = new();
    }

    public class PlayerGun
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("user_id")]
        public long UserId { get; set; }

        [JsonProperty("gun_id")]
        public int GunId { get; set; }

        [JsonProperty("gun_exp")]
        public long GunExp { get; set; } = 0;

        [JsonProperty("gun_level")]
        public int GunLevel { get; set; } = 100;

        [JsonProperty("team_id")]
        public int TeamId { get; set; } = 0;

        [JsonProperty("if_modification")]
        public int IfModification { get; set; } = 0;

        [JsonProperty("location")]
        public int Location { get; set; } = 1;

        [JsonProperty("position")]
        public int Position { get; set; } = 1;

        [JsonProperty("life")]
        public int Life { get; set; } = 1000;

        [JsonProperty("favor")]
        public int Favor { get; set; } = 1000000; // 100 affection

        [JsonProperty("max_favor")]
        public int MaxFavor { get; set; } = 2000000;

        [JsonProperty("favor_toplimit")]
        public int FavorTopLimit { get; set; } = 2000000;

        [JsonProperty("soul_bond")]
        public int SoulBond { get; set; } = 0;

        [JsonProperty("skin")]
        public int Skin { get; set; } = 0;

        [JsonProperty("can_click")]
        public int CanClick { get; set; } = 1;

        [JsonProperty("soul_bond_time")]
        public long SoulBondTime { get; set; } = 0;

        [JsonProperty("special_effect")]
        public int SpecialEffect { get; set; } = 0;

        [JsonProperty("is_collect")]
        public int IsCollect { get; set; } = 0;

        [JsonProperty("skill1")]
        public int Skill1 { get; set; } = 10;

        [JsonProperty("skill2")]
        public int Skill2 { get; set; } = 10;

        [JsonProperty("number")]
        public int Number { get; set; } = 5; // 5 dummy links

        [JsonProperty("equip1_id")]
        public long Equip1Id { get; set; } = 0;

        [JsonProperty("equip2_id")]
        public long Equip2Id { get; set; } = 0;

        [JsonProperty("equip3_id")]
        public long Equip3Id { get; set; } = 0;

        [JsonProperty("equip4_id")]
        public long Equip4Id { get; set; } = 0;

        [JsonProperty("pow")]
        public int Pow { get; set; } = 0;

        [JsonProperty("hit")]
        public int Hit { get; set; } = 0;

        [JsonProperty("dodge")]
        public int Dodge { get; set; } = 0;

        [JsonProperty("rate")]
        public int Rate { get; set; } = 0;

        [JsonProperty("is_locked")]
        public int IsLocked { get; set; } = 1;
        public int GunAmmo { get; set; } = 5;
        public int GunMre { get; set; } = 10;
        public long FixEndTime { get; set; } = 0;
    }

    public class TimedSlot
    {
        public int Slot { get; set; }
        public long GunInstId { get; set; }
        public int Skill { get; set; }
        public long EndTime { get; set; }
    }

    public class PlayerFairy
    {
        public long Id { get; set; }
        public int FairyId { get; set; }
        public int Level { get; set; } = 1;
        public long Exp { get; set; }
        public int TeamId { get; set; }
        public int QualityLevel { get; set; } = 1;
        public int SkillLevel { get; set; } = 1;
        public int PassiveSkill { get; set; }
        public int IsLocked { get; set; }
        public int Skin { get; set; }
    }

    public class PlayerEquip
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("user_id")]
        public long UserId { get; set; }

        [JsonProperty("equip_id")]
        public int EquipId { get; set; }

        [JsonProperty("equip_level")]
        public int EquipLevel { get; set; } = 10;

        [JsonProperty("equip_exp")]
        public int EquipExp { get; set; } = 0;

        [JsonProperty("gun_id")]
        public long GunId { get; set; } = 0;

        [JsonProperty("slot")]
        public int Slot { get; set; } = 0;

        [JsonIgnore]
        public int IsLocked { get; set; } = 0;

        [JsonIgnore]
        public Dictionary<string, int> Stats { get; set; } = new();
    }

    public class DevelopSlot
    {
        [JsonProperty("slot")]
        public int Slot { get; set; }

        [JsonProperty("gun_id")]
        public int GunId { get; set; }

        [JsonProperty("start_time")]
        public long StartTime { get; set; }

        [JsonProperty("duration")]
        public int Duration { get; set; }

        [JsonProperty("mp")]
        public int Mp { get; set; }

        [JsonProperty("ammo")]
        public int Ammo { get; set; }

        [JsonProperty("mre")]
        public int Mre { get; set; }

        [JsonProperty("part")]
        public int Part { get; set; }
        public int EquipId { get; set; }
        public int FairyId { get; set; }
        public int InputLevel { get; set; }
    }

    public class OperationAct
    {
        [JsonProperty("operation_id")]
        public int OperationId { get; set; }

        [JsonProperty("team_id")]
        public int TeamId { get; set; }

        [JsonProperty("start_time")]
        public long StartTime { get; set; }

        [JsonProperty("duration")]
        public int Duration { get; set; }
    }

    public class MissionRecord
    {
        [JsonProperty("mission_id")]
        public int MissionId { get; set; }

        [JsonProperty("medal4")]
        public int Medal4 { get; set; } = 1; // Gold medal

        [JsonProperty("bestrank")]
        public int BestRank { get; set; } = 5; // S rank

        [JsonProperty("type")]
        public int Type { get; set; } = 1;

        public int Counter { get; set; } = 0;
        public int WinCounter { get; set; } = 0;
    }

    public class AllyInstance
    {
        public int Id { get; set; }
        public int AllyTeamId { get; set; }
        public int AllyEnemy { get; set; }
        public int AllyType { get; set; }
        public string Ai { get; set; } = "0";
        public string AiPara { get; set; } = "";
    }

    public class ActiveMissionState
    {
        public List<AllyInstance> Allies { get; set; } = new();
        public int MissionId { get; set; }
        public int Turn { get; set; } = 1;
        public int Ap { get; set; } = 0;
        public List<MissionSpotState> Spots { get; set; } = new();
    }

    public class MissionSpotState
    {
        public int SpotId { get; set; }
        public int Belong { get; set; }
        public int TeamId { get; set; }
        public int EnemyTeamId { get; set; }
        public int EnemyInstanceId { get; set; }
        public int Seed { get; set; }
        public int ReinforceCount { get; set; }
        public int SupplyCount { get; set; }
        /// <summary>Remaining HP of a boss echelon on this spot (0 = full / not a boss).</summary>
        public int BossHp { get; set; }
        /// <summary>Hostage doll on this spot (spot.hostage_info "gunId,hp"); carried by the echelon on the spot.</summary>
        public int HostageId { get; set; }
        public int HostageHp { get; set; }
        public int HostageMaxHp { get; set; }
        /// <summary>True once a Griffin echelon has picked the hostage up (only then do battles hurt it).</summary>
        public bool HostageEscorted { get; set; }
        /// <summary>building.json id on this spot (0 = none) and who currently controls it (1 Griffin, 2 enemy, 3 neutral).</summary>
        public int BuildingId { get; set; }
        public int BuildingBelong { get; set; }
        /// <summary>Allied/third-party echelon instances standing on this spot.</summary>
        public List<int> AllyInstanceIds { get; set; } = new();
    }
}

