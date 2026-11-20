using System;
using Microsoft.AspNetCore.Mvc;
using Kryuger.Common.Database;

namespace Kryuger.Server.Controllers
{
    [ApiController]
    public class PassportController : ControllerBase
    {
        private readonly DatabaseManager _db;

        public PassportController(DatabaseManager db)
        {
            _db = db;
        }

        [HttpPost("/email/login")]
        public IActionResult EmailLogin([FromBody] PassportLoginReq? req)
        {
            string account = string.IsNullOrWhiteSpace(req?.Account) ? "commander@gfl.local" : req.Account.Trim();
            var player = _db.GetOrCreatePlayer(account);
            string token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');

            Console.WriteLine($"[Passport] EmailLogin -> Account: {account}, UID: {player.Id}");
            return Ok(BuildPassportAuth(player.Id, account, token));
        }

        [HttpPost("/account/auto_login")]
        public IActionResult AutoLogin()
        {
            string token = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(token))
                token = "local_auto_token";

            var player = _db.GetOrCreatePlayer("commander_steam");
            Console.WriteLine($"[Passport] AutoLogin -> UID: {player.Id}");
            return Ok(BuildPassportAuth(player.Id, "commander@gfl.local", token));
        }

        [HttpPost("/account/info")]
        public IActionResult AccountInfo()
        {
            string token = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(token))
                token = "local_auto_token";

            var player = _db.GetOrCreatePlayer("commander_steam");
            return Ok(new
            {
                code = 0,
                msg = "OK",
                data = new
                {
                    uid = player.Id,
                    account = "commander@gfl.local",
                    is_real = 1,
                    is_guest = 0,
                    is_activation = 1,
                    is_adult = 1,
                    unfreeze_time = 0,
                    age = 18,
                    id_cards = "",
                    id_name = "",
                    css_token = token,
                    is_need_set_passwd = false,
                    uc_account_token = token
                }
            });
        }

        [HttpGet("/login_component/log")]
        public IActionResult LoginComponentLog()
        {
            return Ok(new
            {
                code = 0,
                msg = "OK",
                data = new { list = Array.Empty<object>() }
            });
        }

        private static object BuildPassportAuth(long uid, string account, string token) => new
        {
            code = 0,
            msg = "OK",
            data = new
            {
                access_token = token,
                is_real = 1,
                is_guest = 0,
                is_activation = 1,
                is_adult = 1,
                unfreeze_time = 0,
                age = 18,
                pi = "",
                is_need_set_passwd = false,
                uc_account_token = token,
                uid = uid,
                account = account
            }
        };
    }

    public class PassportLoginReq
    {
        public string? Account { get; set; }
        public string? Password { get; set; }
    }
}
