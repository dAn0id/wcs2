# WcsCs2 — Warcraft:Source for Counter-Strike 2

A data-driven **CounterStrikeSharp** (`.NET 10.0`) port of **Warcraft:Source (WCS)** for Counter-Strike 2 dedicated servers.

## Features

- **12 Playable Races (50 Skills):**
  - **Standard Races (11):** Archmage Proudmoore, Blood Mage, Crypt Lord, Flame Predator, Human Alliance, Night Elves, Orcish Horde, Shadow Hunter, Succubus Hunter, Undead Scourge, Warden
  - **Custom Race:** Defiladed Destructor (steel oil-drum barrel disguise, P250 250-round clip, explosive physics barrels)
- **4 Shop Items:** Belt of Vitality, Boots of Speed, Gem of Life, Rune of Wisdom
- **Core WCS Progression:** Per-race XP & levels (`required_xp = 80 * level`), Rested XP (online & offline accumulation), Level Bank (with new-player bonus), Bot random race & ability support, and full `wcsadmin` management
- **Source 2 Collision Tracing:** Native raycast + standing/crouched player-hull validation prevents teleporting or spawning props/wards inside walls, floors, ceilings, or solid props
- **Data-Driven Engine:** Declarative JSON race & item configs (`src/WcsCs2/configs/`) powered by 33 composable skill primitives and 6 bespoke custom handlers
- **Embedded SQLite Persistence:** Async WAL-mode SQLite database (`Microsoft.Data.Sqlite` + `Dapper`) auto-initialized on first boot
- **Interactive Menus:** Built on `CS2MenuManager` (`WasdMenu`, `CenterHtmlMenu`, `ChatMenu`, `ConsoleMenu`)

---

## Setup Guide

### 1. Prerequisites

On your **CS2 Linux x86_64 dedicated server**:
- **Metamod:Source** `2.0.0-dev` (installed in `game/csgo/addons/metamod/` and registered in `game/csgo/gameinfo.gi`)
- **CounterStrikeSharp** `v1.0.381+` (`with-runtime` Linux x64 release bundling the **.NET 10.0** runtime)
- **CS2MenuManager** `v1.0.43+` (`shared/CS2MenuManager/` extracted into `game/csgo/addons/counterstrikesharp/shared/CS2MenuManager/`)

On your **build machine**:
- **.NET 10.0 SDK** (`dotnet --version` -> `10.0.x`)

### 2. Build & Publish

```bash
# Restore & build Release
dotnet build -c Release src/WcsCs2.sln

# Publish Linux x64 plugin bundle
dotnet publish src/WcsCs2/WcsCs2.csproj -c Release -r linux-x64 --no-self-contained -o publish/WcsCs2
```

Publishing with `-r linux-x64 --no-self-contained` automatically bundles `WcsCs2.dll`, `Microsoft.Data.Sqlite.dll`, `Dapper.dll`, `SQLitePCLRaw.*.dll`, the native Linux x64 `libe_sqlite3.so` binary, and the default `configs/` folder into `publish/WcsCs2/` (while keeping `CounterStrikeSharp.API` and `CS2MenuManager` runtime-excluded so they resolve cleanly from CounterStrikeSharp's `api/` and `shared/` directories).

### 3. Deploy to Your CS2 Server

1. Ensure `CS2MenuManager` is installed at:
   ```text
   game/csgo/addons/counterstrikesharp/shared/CS2MenuManager/
   ├── CS2MenuManager.dll
   ├── CS2MenuManager.deps.json
   ├── config.toml
   └── ...
   ```
2. Copy the contents of `publish/WcsCs2/` into `game/csgo/addons/counterstrikesharp/plugins/WcsCs2/`:
   ```text
   game/csgo/addons/counterstrikesharp/plugins/WcsCs2/
   ├── WcsCs2.dll
   ├── WcsCs2.deps.json
   ├── Dapper.dll
   ├── Microsoft.Data.Sqlite.dll
   ├── SQLitePCLRaw.batteries_v2.dll
   ├── SQLitePCLRaw.core.dll
   ├── SQLitePCLRaw.provider.e_sqlite3.dll
   ├── libe_sqlite3.so
   ├── configs/
   │   ├── wcs.json
   │   ├── items.json
   │   └── races/
   │       ├── archmage_proudmoore.json
   │       ├── blood_mage.json
   │       ├── crypt_lord.json
   │       ├── defiladed_destructor.json
   │       ├── flame_predator.json
   │       ├── human_alliance.json
   │       ├── night_elves.json
   │       ├── orcish_horde.json
   │       ├── shadow_hunter.json
   │       ├── succubus_hunter.json
   │       ├── undead_scourge.json
   │       └── warden.json
   └── data/                      # Auto-created on first boot
       └── wcs_cs2.sqlite         # Fresh WAL-mode SQLite DB
   ```
3. Ensure the files are owned by the Linux user running your CS2 server (e.g. `chown -R <server-user>:<server-user> plugins/WcsCs2`).

### 4. Configure Admins

`WcsCs2` grants access to `!wcsadmin` and `wcs_reload` via **either** of two methods (you can use one or both):

1. **CounterStrikeSharp `admins.json` (Recommended):**
   Create/edit `game/csgo/addons/counterstrikesharp/configs/admins.json` and grant `@css/root` or `@wcs/admin`:
   ```json
   {
     "YourName": {
       "identity": "76561197960265728",
       "immunity": 100,
       "flags": [
         "@css/root",
         "@wcs/admin"
       ]
     }
   }
   ```
2. **Plugin config `plugins/WcsCs2/configs/wcs.json` (`admin_steam_ids`):**
   Replace the placeholder `76561197960265728` in `admin_steam_ids` with your own 17-digit **SteamID64** integer(s) (look up your SteamID64 via e.g. [steamid.io](https://steamid.io/)):
   ```json
   "admin_steam_ids": [
     76561197960265728
   ]
   ```

### 5. Customize Configs & Hot-Reload

- **`configs/wcs.json`**: XP rates (`xp_interval`, `kill_xp`, `headshot_xp`, `knife_xp`, `round_win_xp`, etc.), Rested XP intervals, Level Bank new-player bonus, bot race behavior, menu style (`wasd`, `html`, `chat`, `console`), `allow_inspect_ultimate_bind`, and stock CS2 model paths.
- **`configs/items.json`**: Shop item pricing, buy limits, duration, and action lists.
- **`configs/races/*.json`**: One JSON file per race; add new races or tweak skill values per level without recompiling.
- **Hot-Reload Configs:** Run `wcs_reload` in the server console (or as an admin) to reload `wcs.json`, `items.json`, and all `races/*.json` live without restarting the server.

### 6. Start & Verify

Restart your CS2 server and verify in the server console / RCON:
- `meta list` -> `CounterStrikeSharp` loaded
- `css_plugins list` -> `[#1:LOADED]: "WcsCs2" (1.0.0)`
- Server startup logs confirm:
  - `Loaded 12 races across 3 categories.`
  - `Loaded 4 shop items.`
  - `SQLite database initialized cleanly.`
  - `Source2TraceEngine initialized cleanly (...)`

---

## In-Game Commands & Keybinds

### Chat Commands (`!` public / `/` silent)
| Command | Description |
| --- | --- |
| `!wcs`, `!wcsmenu` | Open WCS main menu |
| `!changerace [name]` | Change your active race |
| `!spendskills` | Spend unused skill points |
| `!showskills`, `!myinfo` | Display current race skills & XP |
| `!resetskills` | Reset skill points for your current race |
| `!raceinfo [name]`, `!myraceinfo` | View race skill details |
| `!shopmenu`, `!shopinfo` | Buy or inspect WCS shop items |
| `!playerinfo` | Inspect online players' races & levels |
| `!wcsrank`, `!rank`, `!showxp` | Show your XP and server rank |
| `!wcstop` | Show WCS Top 10 leaderboard |
| `!wcsbank`, `!levelbank` | Manage & withdraw banked levels |
| `!wcshelp` | Print command help |
| `!wcsadmin` | Open WCS Admin menu (`@css/root` / `@wcs/admin` / `admin_steam_ids`) |

### Console Commands & Recommended Binds
Players can bind their Ultimate and Ability keys in the CS2 console:
```text
bind v ultimate
bind c ability
```
- `ultimate` / `css_ultimate` — Activate your race's Ultimate skill. When `allow_inspect_ultimate_bind` is `true` in `wcs.json`, players can also opt in to triggering their Ultimate with the weapon inspect key (`F` / `+lookatweapon`).
- `ability` / `css_ability` — Activate your race's Ability skill (e.g. Shadow Hunter Serpent Ward, Defiladed Destructor Build Barrel).
- `wcs_reload` — Reload `wcs.json`, `items.json`, and all `races/*.json` configs (admin / server console).

---

## Project Structure

- `src/WcsCs2/Config/` — Strongly typed JSON config models (`wcs.json`, `items.json`, `races/*.json`)
- `src/WcsCs2/Core/` — Sessions, player state management, progression/XP/bank, shop, bots, race registry
- `src/WcsCs2/Data/` — Async WAL-mode SQLite repository
- `src/WcsCs2/Services/` — Combat/damage hooks, weapons/restrictions, visual/audio effects, Source 2 collision tracer & teleportation
- `src/WcsCs2/Skills/` — Skill execution engine, composable `Primitives/`, and `CustomHandlers/`
- `src/WcsCs2/Menus/` — `CS2MenuManager` menu flows
- `src/WcsCs2/configs/` — Default server config, 4 shop items, and 12 race definitions
