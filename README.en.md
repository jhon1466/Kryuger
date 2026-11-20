# Kryuger

[Español](README.md) · **English** · [中文](README.zh.md) · [日本語](README.ja.md)

Private server for **Girls' Frontline** (Steam EN client), written in C# (.NET 8) with a LiteDB database. It reproduces the official server logic from the game's own tables: your changes are saved, missions are won by their real objectives and enemies act on their turn.

> Non-profit project made for educational and preservation purposes. It is not affiliated with SunBorn or any publisher of the game. It includes no game files: you need your own installed client.

## How to use it: Kryuger GM Tools

**The server is used through Kryuger GM Tools** (the `gm-tools` folder). Nothing needs to be built by hand:

1. Open `kryuger-gm-tools.exe`.
2. Press **Start**. The first time it downloads this repository, installs a private .NET 8 SDK if the PC does not have one and builds the server. Afterwards it only checks whether `main` has a new commit; if it does, it updates itself.
3. Press **Play** (or launch Girls' Frontline from Steam).
4. Pressing **Stop** or closing the app removes the redirect, and the game connects to the official server again.

The whole server lives in `%LOCALAPPDATA%\Kryuger`: the code in `project`, the private .NET in `dotnet` and player data in `data`, kept apart from the code that gets updated.

### What Kryuger GM Tools does

Available in Spanish, English, Chinese and Japanese.

- **Server:** start, stop, check for updates, launch the game, live log and installation status.
- **Control center:** summary of accounts and the catalog.
- **Accounts:** search by UID or name and view resources, gems, T-Dolls, equipment and fairies.
- **Operations:** grant T-Dolls, items, equipment, fairies, skins, gems or resources with a catalog search, and set gems, resources, level or name. Works while the player is online too.

The server's GM API (`/gm`) only answers `127.0.0.1` and requires the token in `KRYUGER_GM_TOKEN.txt`, which the server creates when it starts. Every change is recorded in `gm_audit.log`.

## Requirements

- Windows 10/11.
- Girls' Frontline EN installed from Steam (the folder is detected automatically).
- An internet connection the first time (server and .NET download) and for the Steam sign-in.

## How it works

```
src/
  Kryuger.Common/   AuthCode encryption (RC4 + MD5 + gzip), LiteDB database and models
  Kryuger.Data/     game table loading and construction formulas
  Kryuger.Server/   ASP.NET Core server: game API, combat logic and GM API
  Kryuger.Tests/    encryption and persistence tests
  gfl_redirect/     version.dll (Rust) that redirects the game to the local server
runtime/            prebuilt version.dll and the login template
data/gf-data-us/    game tables used by the server
gm-tools/           Kryuger GM Tools (Tauri 2 + Astro + SolidJS)
```

The server listens on `127.0.0.1` (ports 5000, 80 and 443). While it runs it places `version.dll` in the game folder to redirect the traffic; when it stops, it removes it.

## Roadmap

### Working

**Account and session**
- Steam sign-in, AuthCode encryption and replies in the official format.
- Name, profile text, adjutants, game settings, tutorial and Kalina's skin.
- Resuming a mission in progress after logging back in.

**Factory and T-Dolls**
- T-Doll production (normal, multiple and quick), equipment and fairy production.
- Echelons, formation, enhancement, dummy links, retirement, locking, repair to the real max HP, skill training, neural upgrade, oath and skins.
- Equipment and fairies: equip, enhance, retire and lock.

**Combat**
- Mission start, movement, battles, resupply, end of turn and withdrawal, with every field of the official reply.
- Experience, level, remaining HP, rank and affection after each battle.
- Real victory conditions: headquarters, defeat all enemies, defeat N enemies, defeat a specific enemy, reach a node, capture command posts, survive N rounds, turn limit, rescue hostages and building objectives.
- Enemy turn with the AI from the tables (standby, patrol, alert, expand, chase and advance on the command post), and defeat when the command post is lost.
- Hostages (free, escort and rescue) and bosses with persistent HP.
- Allied and third-party units with their AI, betrayals and transformations.
- Buildings: change of owner, destruction and defense.
- Night missions with fog: only what the echelons can see is shown.
- Natural unlocking of missions, chapters, campaigns and simulations.

**Progress and economy**
- Logistics with real duration and rewards, and auto missions.
- Daily and weekly quests, and achievements with real progress.
- Mail (list, claim, claim all and delete), shop and battle pass.

### Pending

- Active building effects (missiles, towers and radar attacking nodes).
- Battles between allies and enemies.
- Confirming with official captures the ally betrayal/transformation notice and the AI parameter computed from the map.
- Full dormitory (furniture, visits and batteries) and friends.
- Events, Theater, chess, HOC, Sangvis, vehicles and exploration.
- Enhancement screen: the `Gun/eatGunIndex` format is not confirmed.

## Building by hand (optional)

Server ([.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
dotnet run --project src/Kryuger.Server
```

GM Tools ([Node.js](https://nodejs.org) 20+ and [Rust](https://rustup.rs)):

```powershell
cd gm-tools
npm install
npx tauri build --no-bundle
```

The executable is written to `gm-tools\src-tauri\target\release\kryuger-gm-tools.exe`.
