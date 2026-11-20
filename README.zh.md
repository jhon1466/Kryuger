<div align="center">

<img src="gm-tools/public/kryuger.png" alt="Kryuger" width="128" />

# Kryuger

**少女前线英文版私服：真实逻辑、数据持久化与管理面板**

[![Release](https://img.shields.io/github/v/release/jhon1466/Kryuger?style=for-the-badge&color=f97316&label=release)](https://github.com/jhon1466/Kryuger/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jhon1466/Kryuger/total?style=for-the-badge&color=0ea5e9)](https://github.com/jhon1466/Kryuger/releases)
[![Last commit](https://img.shields.io/github/last-commit/jhon1466/Kryuger?style=for-the-badge&color=22c55e)](https://github.com/jhon1466/Kryuger/commits/main)

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-server-239120?style=flat-square)
![LiteDB](https://img.shields.io/badge/LiteDB-database-0f766e?style=flat-square)
![Tauri 2](https://img.shields.io/badge/Tauri-2-24C8DB?style=flat-square&logo=tauri&logoColor=white)
![SolidJS](https://img.shields.io/badge/SolidJS-UI-2C4F7C?style=flat-square&logo=solid&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square)

<a href="https://github.com/jhon1466/Kryuger/releases/latest"><img src="https://img.shields.io/badge/下载%20Kryuger%20GM%20Tools-f97316?style=for-the-badge&logo=github&logoColor=white" alt="下载 Kryuger GM Tools" /></a>

[Español](README.md) · [English](README.en.md) · **中文** · [日本語](README.ja.md)

</div>

---

**少女前线**（Steam 英文版客户端）私服，使用 C#（.NET 8）和 LiteDB 数据库编写。它根据游戏自带的数据表还原官方服务器的逻辑：你的修改会被保存，任务按真实目标判定胜利，敌人会在自己的回合行动。

> 本项目为非营利项目，仅用于学习和保存目的。与 SunBorn 及任何游戏发行商无关。不包含任何游戏文件：你需要自行安装客户端。

---

## 使用方法：Kryuger GM Tools

**服务器通过 Kryuger GM Tools 使用**（`gm-tools` 文件夹），无需手动编译：

1. 打开 `kryuger-gm-tools.exe`。
2. 点击 **启动**。首次运行会下载本仓库，在电脑没有 .NET 8 SDK 时安装一个私有副本，并编译服务器。之后只会检查 `main` 是否有新的提交，有则自动更新。
3. 点击 **开始游戏**（或从 Steam 启动少女前线）。
4. 点击 **停止** 或关闭程序时会移除重定向，游戏将重新连接官方服务器。

整个服务器保存在 `%LOCALAPPDATA%\Kryuger`：代码在 `project`，私有 .NET 在 `dotnet`，玩家数据在 `data`，与会被更新的代码分开存放。

### Kryuger GM Tools 的功能

提供西班牙语、英语、中文和日语界面。

- **服务器：** 启动、停止、检查更新、启动游戏、实时日志和安装状态。
- **控制中心：** 账号和目录的概览。
- **账号：** 按 UID 或名称搜索，查看资源、钻石、人形、装备和妖精。
- **操作：** 通过目录搜索发放人形、道具、装备、妖精、皮肤、钻石或资源，并设置钻石、资源、等级或名称。玩家在线时也可以使用。

服务器的 GM API（`/gm`）只响应 `127.0.0.1`，并需要 `KRYUGER_GM_TOKEN.txt` 中的令牌，该令牌在服务器启动时生成。每次修改都会记录在 `gm_audit.log` 中。

---

## 运行要求

- Windows 10/11。
- 通过 Steam 安装的少女前线英文版（文件夹会自动检测）。
- 首次运行（下载服务器和 .NET）以及 Steam 登录时需要联网。

---

## 工作原理

```
src/
  Kryuger.Common/   AuthCode 加密（RC4 + MD5 + gzip）、LiteDB 数据库和数据模型
  Kryuger.Data/     游戏数据表加载和建造公式
  Kryuger.Server/   ASP.NET Core 服务器：游戏 API、战斗逻辑和 GM API
  Kryuger.Tests/    加密和持久化测试
  gfl_redirect/     将游戏重定向到本地服务器的 version.dll（Rust）
runtime/            预编译的 version.dll 和登录模板
data/gf-data-us/    服务器使用的游戏数据表
gm-tools/           Kryuger GM Tools（Tauri 2 + Astro + SolidJS）
```

服务器监听 `127.0.0.1`（端口 5000、80 和 443）。运行期间会在游戏文件夹放置 `version.dll` 以重定向流量，停止时将其移除。

---

## 路线图

### 已完成

| 类别 | 功能 | 状态 |
|---|---|:---:|
| 账号与登录 | Steam 登录、AuthCode 加密，响应采用官方格式。 | `已完成` |
|  | 名称、个人简介、副官、游戏设置、新手教程和格琳娜的皮肤。 | `已完成` |
|  | 重新登录后继续进行中的任务。 | `已完成` |
| 工厂与人形 | 人形建造（普通、批量和快速）、装备建造和妖精建造。 | `已完成` |
|  | 梯队、阵型、强化、扩编、回收、锁定、按真实生命上限修复、技能训练、心智升级、誓约和皮肤。 | `已完成` |
|  | 装备和妖精：装备、强化、回收和锁定。 | `已完成` |
| 战斗 | 开始任务、移动、战斗、补给、结束回合和撤退，包含官方响应的全部字段。 | `已完成` |
|  | 每场战斗后的经验、等级、剩余生命、评级和好感度。 | `已完成` |
|  | 真实的胜利条件：占领指挥部、全歼敌人、击败 N 个敌人、击败指定敌人、抵达指定节点、占领指挥部节点、坚守 N 回合、回合限制、营救人质和建筑目标。 | `已完成` |
|  | 敌方回合按数据表中的 AI 行动（待机、巡逻、警戒、扩张、追击和进攻指挥部），失去指挥部时任务失败。 | `已完成` |
|  | 人质（解救、护送和营救）以及生命值会保留的首领。 | `已完成` |
|  | 友军和第三方部队及其 AI、背叛和转化。 | `已完成` |
|  | 建筑：易主、摧毁和防御。 | `已完成` |
|  | 带迷雾的夜战任务：只显示梯队视野内的内容。 | `已完成` |
|  | 任务、章节、活动和模拟作战按正常进度解锁。 | `已完成` |
| 进度与经济 | 后勤支援（真实时长和奖励）以及自律作战。 | `已完成` |
|  | 每日和每周任务，以及按真实进度计算的成就。 | `已完成` |
|  | 邮件（列表、领取、全部领取和删除）、商店和战斗通行证。 | `已完成` |

### 待完成

| 功能 | 状态 |
|---|:---:|
| 建筑的主动效果（导弹、炮塔和雷达攻击节点）。 | `待完成` |
| 友军与敌人之间的战斗。 | `待完成` |
| 用官方抓包确认友军背叛/转化的通知格式，以及根据地图计算的 AI 参数。 | `待完成` |
| 完整的宿舍（家具、访问和电池）和好友。 | `待完成` |
| 活动、剧场、战棋、重装部队、铁血、载具和探索。 | `待完成` |
| 强化界面：`Gun/eatGunIndex` 的格式尚未确认。 | `待完成` |

---

## 手动编译（可选）

服务器（[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)）：

```powershell
dotnet run --project src/Kryuger.Server
```

GM Tools（[Node.js](https://nodejs.org) 20+ 和 [Rust](https://rustup.rs)）：

```powershell
cd gm-tools
npm install
npx tauri build --no-bundle
```

可执行文件生成在 `gm-tools\src-tauri\target\release\kryuger-gm-tools.exe`。
