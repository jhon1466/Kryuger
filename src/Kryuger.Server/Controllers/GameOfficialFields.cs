using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kryuger.Server.Controllers
{
    /// <summary>
    /// Mission replies of the current client carry many fields the server does not model (building info, triggers,
    /// win/lose results, mission_control...). These defaults are the values of the official replies captured in
    /// gfl_capture.log (2026-11-13, client 30810); any field our handler did not set is filled from here so the
    /// client always receives the complete official shape.
    /// </summary>
    public partial class GameController
    {
        private const string SectionDefaults = @"{
            ""died_this_section"":{""enemy"":[],""ally"":[]},""fight_environment_group"":"""",""range_atk_rec"":[],
            ""range_atk_turn"":{""10"":[],""304"":[],""305"":[],""4"":[],""5"":[],""6"":[]},""mission_trigger_engine"":[],
            ""type5_score"":""0"",""target_moved_step"":[],""ally1_add"":[],""fairy_skill_return"":[],""fairy_skill_perform"":[],
            ""fairy_skill_on_spot"":[],""fairy_skill_on_team"":[],""fairy_skill_on_enemy"":[],""fairy_skill_on_squad"":[],
            ""fairy_skill_on_ally"":[],""fairy_skill_on_sangvis"":[],""fairy_skill_on_vehicle"":[],""fairy_skill_on_friend_team"":[],
            ""fairy_skill_on_friend_sangvis"":[],""spot_act_info"":[],""building_info"":[],""mission_win_step_control_ids"":[],
            ""trigger_para"":{""drop_item"":[],""cost_item"":[]}}";

        private static readonly Dictionary<string, string> OpDefaults = new()
        {
            ["mission/startmission"] = @"{""squad_info"":[],""vehicle_info"":[],""ap"":2,""ally_instance_info"":[]}",
            ["mission/teammove"] = @"{""mre"":""5"",""building_defender_change"":[],""spot_teleport_result"":[],""mission_lose_result"":[],
                ""mission_win_result"":[],""ap"":1,""ally_instance_transform"":[],""ally_instance_betray"":[],
                ""mission_control"":{""1"":1,""2"":2,""3"":3},""change_belong"":[]}",
            ["mission/battlefinish"] = @"{""free_exp"":0,""gun_exp"":[],""gun_life"":[],""user_exp"":""0"",""squad_exp"":[],""battle_rank"":""5"",
                ""change_belong"":[],""mission_lose_result"":[],""mission_win_result"":[],""building_defender_change"":[],
                ""spot_teleport_result"":[],""seed"":3133,""favor_change"":{},""ally_instance_transform"":[],""ally_instance_betray"":[],
                ""mission_control"":{""1"":1,""2"":2,""3"":3}}",
            ["mission/endturn"] = @"{""change_belong1"":[],""building_change_belong1"":[],""mission_lose_result"":[],""mission_win_result"":[],
                ""range_atk_squad_result"":[],""nobattle_ally_life_change"":[],""range_atk_result"":[],""ally_instance_transform"":[],
                ""ally_instance_betray"":[],""building_defender_change"":[],""mission_control"":{""1"":1,""2"":2,""3"":3}}",
            ["mission/startenemyturn"] = @"{""building_defender_change"":[],""change_belong2"":[],""building_change_belong2"":[],
                ""mission_lose_result"":[],""mission_win_result"":[],""spot_teleport_result"":[],""enemy_move"":[],""ally_move"":[],
                ""ally_battle"":[],""nobattle_ally_life_change"":[],""ally_instance_transform"":[],""ally_instance_betray"":[],
                ""mission_control"":{""1"":1,""2"":2,""3"":3}}",
            ["mission/endenemyturn"] = @"{""change_belong"":[],""building_change_belong"":[],""building_defender_change"":[],
                ""mission_lose_result"":[],""mission_win_result"":[],""range_atk_squad_result"":[],""nobattle_ally_life_change"":[],
                ""range_atk_result"":[],""ally_instance_transform"":[],""ally_instance_betray"":[],""mission_control"":{""1"":1,""2"":2,""3"":3}}",
            ["mission/startturn"] = @"{""mission_lose_result"":[],""change_belong"":[],""building_change_belong"":[],""building_defender_change"":[],
                ""mission_win_result"":[],""died_enemy"":[],""died_ally"":[],""died_ally_type"":[],""ally_instance_transform"":[],
                ""ally_instance_betray"":[],""mission_control"":{""1"":1,""2"":2,""3"":3}}",
            ["mission/supplyteam"] = @"{""max_ammo"":5,""max_mre"":10,""type5_score"":""0"",""supply_count"":""1""}",
        };

        private static readonly HashSet<string> SectionOps = new()
        {
            "mission/startmission", "mission/teammove", "mission/battlefinish", "mission/endturn",
            "mission/startenemyturn", "mission/endenemyturn", "mission/startturn"
        };

        /// <summary>Adds the official fields missing from a reply (never overwrites what the handler set).</summary>
        private static string CompleteOfficialFields(string op, string json)
        {
            string key = (op ?? "").ToLowerInvariant();
            if (!OpDefaults.TryGetValue(key, out var defaults)) return json;
            if (!json.TrimStart().StartsWith("{")) return json;
            JObject reply;
            try { reply = JObject.Parse(json); } catch { return json; }

            bool night = reply["night_spots"] != null;
            void Fill(string src)
            {
                foreach (var prop in JObject.Parse(src).Properties())
                    if (reply[prop.Name] == null && !(night && prop.Name == "spot_act_info")) reply[prop.Name] = prop.Value;
            }
            if (SectionOps.Contains(key)) Fill(SectionDefaults);
            Fill(defaults);
            return reply.ToString(Formatting.None);
        }
    }
}
