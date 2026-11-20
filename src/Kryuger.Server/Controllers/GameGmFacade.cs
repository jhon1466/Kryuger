using Kryuger.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace Kryuger.Server.Controllers
{
    /// <summary>Entry points the GM API uses to grant things with the same rules as the game handlers.</summary>
    public partial class GameController
    {
        /// <summary>Official index template: local capture if present, else the sanitized copy shipped in runtime/.</summary>
        internal static string IndexTemplatePath()
        {
            string local = System.IO.Path.Combine(System.AppContext.BaseDirectory, "data", "index_template.json");
            if (System.IO.File.Exists(local)) return local;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "..", "..", "runtime", "index_template.json"));
        }

        [NonAction] public void GmAddItem(PlayerAccount p, int itemId, int count) => AddItem(p, itemId, count);
        [NonAction] public PlayerGun GmGiveGun(PlayerAccount p, int gunId) => GiveGun(p, gunId);
        [NonAction] public PlayerEquip GmGiveEquip(PlayerAccount p, int equipId) => GiveEquip(p, equipId);
        [NonAction]
        public PlayerFairy GmGiveFairy(PlayerAccount p, int fairyId)
        {
            var f = new PlayerFairy { Id = NextFairyId(p), FairyId = fairyId };
            p.Fairies.Add(f);
            return f;
        }
    }
}
