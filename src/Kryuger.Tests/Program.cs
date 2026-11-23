using System;
using System.IO;
using System.Linq;
using System.Text;
using Kryuger.Common.Crypto;
using Kryuger.Common.Database;
using Kryuger.Data;
using Kryuger.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

class Program
{
    static void VerifyPacketMacAndExpiry(string encoded, string key)
    {
        string b64 = encoded.StartsWith("#") ? encoded[1..] : encoded;
        byte[] enc = Convert.FromBase64String(b64);
        string hash = AuthCode.Md5(key);
        string x = AuthCode.Md5(hash[..16]);
        string realKeyX = AuthCode.Md5(hash[16..]);
        string realKey = realKeyX + AuthCode.Md5(realKeyX);

        byte[] dec = AuthCode.TransformRc4(enc, realKey);
        string expiry = Encoding.ASCII.GetString(dec, 0, 10);
        string mac = Encoding.ASCII.GetString(dec, 10, 16);
        byte[] bodyBytes = dec[26..];

        byte[] xBytes = Encoding.UTF8.GetBytes(x);
        byte[] macInput = new byte[bodyBytes.Length + xBytes.Length];
        Buffer.BlockCopy(bodyBytes, 0, macInput, 0, bodyBytes.Length);
        Buffer.BlockCopy(xBytes, 0, macInput, bodyBytes.Length, xBytes.Length);
        string expectedMac = AuthCode.Md5(macInput)[..16];

        if (!long.TryParse(expiry, out long exp) || exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new Exception($"Invalid/expired timestamp: {expiry}");
        if (mac != expectedMac)
            throw new Exception($"MAC mismatch: got {mac}, expected {expectedMac}");
    }

    static string ExtractContent(IActionResult res)
    {
        if (res is ContentResult cr) return cr.Content ?? "";
        throw new Exception($"Unexpected IActionResult: {res.GetType().Name}");
    }

    static JObject DecodeResp(IActionResult res, string key)
    {
        string raw = ExtractContent(res);
        string dec = raw.StartsWith("#") ? AuthCode.Decode(raw, key) : raw;
        return JObject.Parse(dec);
    }

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception("ASSERT FAILED: " + msg);
    }

    static void Main()
    {
        string token = "fmQH1s3iZjpRAfqnZZlbzPPKtTCgR7ybb22Dw8p8fAAPb+z9iVP/EH79ElgBIEo8qotYaB2uqdzaVec=";
        string decoded = AuthCode.Decode(token, "yundoudou");
        Console.WriteLine("C# Decoded: " + decoded);

        string reencodedPlain = AuthCode.Encode(decoded, "yundoudou", compress: false);
        VerifyPacketMacAndExpiry(reencodedPlain, "yundoudou");
        if (decoded != AuthCode.Decode(reencodedPlain, "yundoudou")) throw new Exception("Plain roundtrip failed");
        Console.WriteLine("Plain roundtrip + MAC: PASS");

        string jsonPayload = "{\"sign\":\"ada7d88b20464de2\",\"uid\":\"100002\",\"user_id\":\"100002\",\"is_username_exist\":1,\"app_guard_id\":\"\",\"is_gm\":1}";
        string reencodedGz = AuthCode.Encode(jsonPayload, "yundoudou", compress: true);
        VerifyPacketMacAndExpiry(reencodedGz, "yundoudou");
        if (jsonPayload != AuthCode.Decode(reencodedGz, "yundoudou")) throw new Exception("Compressed roundtrip failed");
        Console.WriteLine("Compressed (#) roundtrip + MAC: PASS");

        VerifyLiteDbPersistence();
        VerifyEndToEndServerPersistence();
    }

    static void VerifyLiteDbPersistence()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"gfl_test_{Guid.NewGuid():N}.db");
        try
        {
            const string adjMulti = "single|0|1|39|30039|0|0|0|0,single|1|1|305|11703|0|0|0|0";
            using (var db1 = new DatabaseManager(tempDb))
            {
                var p = db1.GetOrCreatePlayer("test_open_id");
                p.Name = "TestCmdr";
                p.Intro = "Hello";
                p.Gender = "1";
                p.AdjutantMulti = adjMulti;
                p.GameSetting = "{\"autoSupply\":true}";
                p.Extra["att_count"] = "3";
                p.Extra["att_last_day"] = "20261114";
                p.Extra["seven_att_count"] = "2";
                p.Extra["claim_daily_1"] = "1794670000";
                p.Missions["1"] = new Kryuger.Common.Models.MissionRecord { MissionId = 1, BestRank = 5, Medal4 = 1 };
                db1.SavePlayer(p);
            }

            using (var db2 = new DatabaseManager(tempDb))
            {
                var reloaded = db2.GetOrCreatePlayer("test_open_id");
                Assert(reloaded.Name == "TestCmdr", "Name did not persist");
                Assert(reloaded.Intro == "Hello", "Intro did not persist");
                Assert(reloaded.Gender == "1", "Gender did not persist");
                Assert(reloaded.AdjutantMulti == adjMulti, "AdjutantMulti did not persist");
                Assert(reloaded.GameSetting == "{\"autoSupply\":true}", "GameSetting did not persist");
                Assert(reloaded.Extra.GetValueOrDefault("att_count") == "3", "Attendance count did not persist");
                Assert(reloaded.Extra.GetValueOrDefault("seven_att_count") == "2", "Seven attendance did not persist");
                Assert(reloaded.Extra.ContainsKey("claim_daily_1"), "Quest claim did not persist");
                Assert(reloaded.Missions.TryGetValue("1", out var m) && m.BestRank == 5, "Mission record did not persist");
            }
            Console.WriteLine("LiteDB persistence across restart: PASS");
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
            string logDb = tempDb.Replace(".db", "-log.db");
            if (File.Exists(logDb)) File.Delete(logDb);
        }
    }

    static void VerifyEndToEndServerPersistence()
    {
        string dataDir = @"e:\Documentos\gf\data\gf-data-us";
        if (!Directory.Exists(dataDir))
        {
            Console.WriteLine("Skipping E2E server test (data dir missing)");
            return;
        }

        MasterDataManager.Instance.LoadData(dataDir);
        Tables.DataPath = dataDir;

        string tempDb = Path.Combine(Path.GetTempPath(), $"gfl_e2e_{Guid.NewGuid():N}.db");
        try
        {
            long playerId;
            string signKey;
            long firstGunId;
            int initialGunCount;

            using (var db = new DatabaseManager(tempDb))
            {
                var p = db.GetOrCreatePlayer("e2e_open_id");
                p.Gem = 0; // simulate empty gems before SeedFullInventory
                db.SavePlayer(p);
                playerId = p.Id;
                signKey = p.SignKey;
                var ctrl = new GameController(db);

                // 1. Index/index seeds inventory, gems, gifts, special effects=1
                var indexJson = DecodeResp(ctrl.DispatchOpForTest("Index/index", new JObject(), p).Result, signKey);
                p = db.GetPlayerById(playerId)!;
                Assert(p.Gem == 999999, "Gems should refill to 999999");
                Assert(p.Items.GetValueOrDefault("7001") == 1, "Special effect 7001 must be 1");
                Assert(p.Items.GetValueOrDefault("100010") == 9999, "Shop currency 100010 must be seeded");
                Assert(p.Gifts.GetValueOrDefault("100003") == 999, "Gift 100003 must be seeded");
                firstGunId = p.Guns[0].Id;
                initialGunCount = p.Guns.Count;

                // 2. Index/home must return player.Gem, NOT 0
                var homeJson = DecodeResp(ctrl.DispatchOpForTest("Index/home", new JObject(), p).Result, signKey);
                Assert(homeJson["gem"]?.Value<int>() == p.Gem, "Index/home must return player.Gem");

                // 3. Kalina skin (-1)
                Assert(ExtractContent(ctrl.DispatchOpForTest("Dorm/changeSkin", JObject.Parse("{\"gun_with_user_id\":-1,\"skin_id\":209}"), p).Result) == "1", "Dorm/changeSkin Kalina");

                // 4. Gun/contract + Dorm/giftToGun
                ctrl.DispatchOpForTest("Gun/contract", JObject.Parse($"{{\"gun_with_user_id\":{firstGunId}}}"), p).Wait();
                var giftResp = DecodeResp(ctrl.DispatchOpForTest("Dorm/giftToGun", JObject.Parse($"{{\"item_id\":100003,\"gun_with_user_id\":{firstGunId},\"num\":1,\"new\":1}}"), p).Result, signKey);
                Assert(giftResp["skin_add_favor"]?.Value<int>() == 10000, "Dorm/giftToGun skin_add_favor");
                Assert(giftResp["all_favorup_gun"] is JArray arr && arr.Count == 0, "Dorm/giftToGun all_favorup_gun");

                // 5. Gun/setCollect + Gun/onSpecialEffect + Dorm/receive_favor
                ctrl.DispatchOpForTest("Gun/setCollect", JObject.Parse($"{{\"gun_with_user_id\":{firstGunId},\"is_collect\":true}}"), p).Wait();
                ctrl.DispatchOpForTest("Gun/onSpecialEffect", JObject.Parse($"{{\"gun_with_user_id\":{firstGunId},\"special_effect_id\":\"7020\"}}"), p).Wait();
                ctrl.DispatchOpForTest("Dorm/receive_favor", JObject.Parse($"{{\"user_id\":\"{playerId}\",\"dorm_id\":1,\"gun_with_user_id\":{firstGunId}}}"), p).Wait();

                // 6. Gun/fixGuns + Gun/fixFinish
                p.Guns[0].Life = 250;
                db.SavePlayer(p);
                ctrl.DispatchOpForTest("Gun/fixGuns", JObject.Parse($"{{\"if_quick\":0,\"fix_guns\":{{\"{firstGunId}\":1}}}}"), p).Wait();
                Assert(p.FixSlots.Count == 1 && p.FixSlots[0].GunInstId == firstGunId, "FixSlot recorded");
                ctrl.DispatchOpForTest("Gun/fixFinish", JObject.Parse("{\"fix_slot\":1}"), p).Wait();

                // 7. Battlepass/getBonus
                ctrl.DispatchOpForTest("Battlepass/getBonus", JObject.Parse("{\"battlepass_id\":42,\"type\":1,\"param\":1}"), p).Wait();

                // 8. Mall/gemToGiftbag (mall_id=340 -> Thompson gun_id=16)
                var buyResp = DecodeResp(ctrl.DispatchOpForTest("Mall/gemToGiftbag", JObject.Parse("{\"mall_id\":340,\"num\":1}"), p).Result, signKey);
                Assert(buyResp["prize"]?["gun_id"]?.ToString() == "16", "Mall/gemToGiftbag prize gun_id=16");
                Assert(buyResp["gun_with_user_add"]?["gun_id"]?.ToString() == "16", "Mall/gemToGiftbag gun_with_user_add object");

                // 9. Active Gun/developGun, Operation/startOperation, and Automission start+abort
                ctrl.DispatchOpForTest("Gun/developGun", JObject.Parse("{\"mp\":522,\"ammo\":320,\"mre\":404,\"part\":137,\"build_slot\":1,\"input_level\":0}"), p).Wait();
                ctrl.DispatchOpForTest("Operation/startOperation", JObject.Parse("{\"operation_id\":5,\"team_id\":1}"), p).Wait();
                ctrl.DispatchOpForTest("Automission/startAutomission", JObject.Parse("{\"team_ids\":[1],\"auto_mission_id\":6,\"number\":3,\"is_quick\":0}"), p).Wait();
                Assert(ExtractContent(ctrl.DispatchOpForTest("Automission/abortAutomission", JObject.Parse("{\"auto_mission_id\":6}"), p).Result) == "1", "Automission/abortAutomission failed");
            }

            // Reopen DB and verify everything persisted through Index/index!
            using (var db2 = new DatabaseManager(tempDb))
            {
                var p2 = db2.GetPlayerById(playerId)!;
                var ctrl2 = new GameController(db2);
                var reloadedIndex = DecodeResp(ctrl2.DispatchOpForTest("Index/index", new JObject(), p2).Result, signKey);

                Assert(reloadedIndex["kalina_with_user_info"]?["skin"]?.Value<int>() == 209, "Kalina skin did not persist across restart");
                var gunToken = reloadedIndex["gun_with_user_info"]!.First(g => g["id"]?.ToString() == firstGunId.ToString());
                Assert(gunToken["soul_bond"]?.ToString() == "1", "SoulBond did not persist");
                Assert(gunToken["is_collect"]?.ToString() == "1", "IsCollect did not persist");
                Assert(gunToken["special_effect"]?.ToString() == "7020", "SpecialEffect did not persist");
                Assert(gunToken["can_click"]?.ToString() == "0", "CanClick (dorm heart) did not persist");
                var repairedGun = p2.Guns.First(g => g.Id == firstGunId);
                Assert(gunToken["life"]?.ToString() == Kryuger.Server.Controllers.GameController.MaxLife(repairedGun).ToString(), "Repaired Life is not the real max HP");
                Assert(p2.Guns.Count == initialGunCount + 1, "Mall purchase Thompson gun did not persist");
                Assert(p2.Items["100010"] == 9998, "Mall purchase itemprice 100010 did not deduct");

                var devArr = reloadedIndex["develop_act_info"] as JArray;
                Assert(devArr != null && devArr.Count == 1, "develop_act_info should persist");
                var dev = (JObject)devArr![0];
                foreach (var k in new[] { "id", "build_slot", "user_id", "gun_id", "equip_id", "start_time", "mp", "ammo", "mre", "part", "input_level", "core", "item1_num", "wish_gun" })
                    Assert(dev[k] != null && int.TryParse(dev[k]!.ToString(), out _), $"develop_act_info missing/invalid int field {k}");

                var opArr = reloadedIndex["operation_act_info"] as JArray;
                Assert(opArr != null && opArr.Count == 1, "operation_act_info should persist");
                var opObj = (JObject)opArr![0];
                foreach (var k in new[] { "id", "operation_id", "user_id", "team_id", "start_time", "exec_num" })
                    Assert(opObj[k] != null && int.TryParse(opObj[k]!.ToString(), out _), $"operation_act_info missing/invalid int field {k}");

                Assert(reloadedIndex["auto_mission_act_info"] == null || reloadedIndex["auto_mission_act_info"]!.Type == JTokenType.Null, "Aborted automission should not stay active");

                var bpJson = DecodeResp(ctrl2.DispatchOpForTest("Battlepass/index", new JObject(), p2).Result, signKey);
                Assert(bpJson["battlepass_with_user"]![0]!["free_bonus"]?.ToString() == "1", "Battlepass claim did not persist");
                Assert(int.TryParse(bpJson["battlepass_with_user"]![0]!["extra_bonus"]?.ToString(), out _), "Battlepass extra_bonus must parse as int");
            }

            Console.WriteLine("E2E server handlers + persistence across restart: PASS");
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
            string logDb = tempDb.Replace(".db", "-log.db");
            if (File.Exists(logDb)) File.Delete(logDb);
        }
    }
}
