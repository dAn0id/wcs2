![Warcraft Source — WCS for CS2](assets/banner.jpg)

# WcsCs2 — Warcraft:Source for Counter-Strike 2

A data-driven **CounterStrikeSharp** (`.NET 10.0`) port of **Warcraft:Source (WCS)** for **Counter-Strike 2 Linux x64** dedicated servers.

> **Platform Support:** **Linux x86_64 dedicated servers only.** Native Source 2 collision tracing (`ElfSymbolResolver` + `Source2TraceEngine`) and the bundled `libe_sqlite3.so` binary target Linux x64.

## Features

- **12 Playable Races (50 Skills):**
  - **Standard Races (11):** Archmage Proudmoore, Blood Mage, Crypt Lord, Flame Predator, Human Alliance, Night Elves, Orcish Horde, Shadow Hunter, Succubus Hunter, Undead Scourge, Warden
  - **Custom Race:** Defiladed Destructor (steel oil-drum barrel disguise, P250 250-round clip, explosive physics barrels)
- **4 Shop Items:** Belt of Vitality, Boots of Speed, Gem of Life, Rune of Wisdom
- **Core WCS Progression:** Per-race XP & levels (`required_xp = 80 * level`), Rested XP (online & offline accumulation), Level Bank (with new-player bonus), Bot random race & ability support, and full `wcsadmin` management
- **Source 2 Collision Tracing:** Automatic ELF64 RTTI/xref symbol resolution (`configs/gamedata.json` fallback/override) + standing/crouched player-hull validation prevents teleporting or spawning props inside walls, floors, ceilings, or solid props, plus an optional **Dash-style Teleport (`teleport_dash`)** alternative included in teleport race configs
- **Data-Driven Engine:** JSONC (comments & trailing commas supported) race & item configs (`configs/`) powered by composable skill primitives and bespoke custom handlers
- **Embedded SQLite Persistence:** Async WAL-mode SQLite database (`Microsoft.Data.Sqlite` + `Dapper`) auto-initialized on first boot
- **Interactive Menus:** Built on `CS2MenuManager` (`WasdMenu`, `CenterHtmlMenu`, `ChatMenu`, `ConsoleMenu`)

---

## Setup

For server administrators deploying `WcsCs2` on a **CS2 Linux x64** dedicated server using a prebuilt GitHub Release.

### 1. Prerequisites (Linux x64 CS2 Server)

- **Counter-Strike 2 Dedicated Server** on **Linux x86_64**
- **Metamod:Source** `2.x` (`game/csgo/addons/metamod/` registered in `game/csgo/gameinfo.gi`)
- **CounterStrikeSharp** `v1.0.381+` (**`with-runtime` Linux x64** release bundling the **.NET 10.0** runtime)
- **CS2MenuManager** `v1.0.43+` extracted to `game/csgo/addons/counterstrikesharp/shared/CS2MenuManager/`

### 2. Download & Extract Prebuilt Release

1. Download the latest `WcsCs2-v*-linux-x64.zip` (or `.tar.gz`) archive from **[GitHub Releases](https://github.com/dAn0id/wcs2/releases)**.
2. Extract it into `game/csgo/addons/counterstrikesharp/plugins/` so the directory layout looks like:
   ```text
   game/csgo/addons/counterstrikesharp/
   ├── shared/
   │   └── CS2MenuManager/
   │       ├── CS2MenuManager.dll
   │       ├── CS2MenuManager.deps.json
   │       └── config.toml
   └── plugins/
       └── WcsCs2/
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
           │   ├── gamedata.json
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
3. Ensure all files are owned by the Linux user running your CS2 server (e.g. `chown -R <server-user>:<server-user> plugins/WcsCs2`).

### 3. Configure Admins

`WcsCs2` grants access to `!wcsadmin`, `wcs_reload`, and `wcs_tracetest` via **either** of two methods (you can use one or both):

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

### 4. Customize Configs, Gamedata & Optional Dash Teleport

- **`configs/wcs.json`**: XP rates (`xp_interval`, `kill_xp`, `headshot_xp`, `knife_xp`, `round_win_xp`, etc.), Rested XP intervals, Level Bank new-player bonus, bot race behavior, menu style (`wasd`, `html`, `chat`, `console`), `allow_inspect_ultimate_bind`, `use_native_trace` (kill-switch for Source 2 native physics tracing; falls back to safe managed teleport when `false`), and stock CS2 model paths.
- **`configs/gamedata.json`**: Controls how `Source2TraceEngine` resolves CS2 Linux x64 physics symbols (`TraceShape`, `GameTraceManager`, `CTraceFilter` vtable):
  - `"resolution_mode": "auto"` *(default)* — Automatically resolves all 3 symbols directly from `libserver.so` ELF64 metadata on disk (Itanium RTTI for `12CTraceFilter`, VProf string xref + `.eh_frame_hdr`/prologue for `"Physics/TraceShape (Server)"`, and caller consensus for `GameTraceManager`) so `WcsCs2` survives CS2 compiler updates without recompiling. Any symbol that cannot be resolved dynamically falls back to `linux_signatures` in `gamedata.json`.
  - `"resolution_mode": "dynamic"` — Dynamic ELF64 resolution only.
  - `"resolution_mode": "gamedata"` — Uses only `linux_signatures` from `gamedata.json` (admin override without recompiling).
- **`configs/items.json`**: Shop item pricing, buy limits, duration, and action lists.
- **`configs/races/*.json`**: One JSON file per race (`//` comments and trailing commas are supported).
- **Optional Dash-Style Teleport (`teleport_dash`):** Races with the `teleport` skill (such as `configs/races/human_alliance.json`) include a commented-out `"type": "teleport_dash"` alternative right next to `"teleport"` that launches the player forward with velocity + electric/distort particles instead of instant point-to-point teleport. To switch, comment out `"teleport"`, uncomment `"teleport_dash"`, and run `wcs_reload`.
- **Hot-Reload & Diagnostics:**
  - `wcs_reload` — Reloads `wcs.json`, `gamedata.json`, `items.json`, and all `races/*.json` live without restarting the server.
  - `wcs_tracetest` — Runs a live Source 2 Line + Player-BBox Hull diagnostic trace on the current map and logs symbol resolution sources and hit results.

### 5. Start & Verify

Restart your CS2 server and verify in the server console / RCON:
- `meta list` -> `CounterStrikeSharp` loaded
- `css_plugins list` -> `[#1:LOADED]: "WcsCs2" (1.0.0)`
- Server startup logs confirm:
  - `Loaded 12 races across 3 categories.`
  - `Loaded 4 shop items.`
  - `SQLite database initialized cleanly.`
  - `Source2TraceEngine initialized cleanly (mode=auto): TraceShape=[dynamic-elf] ..., GTM*=[dynamic-elf] ..., FilterVtable=[dynamic-elf] ...`

### 6. In-Game Commands & Keybinds

#### Chat Commands (`!` public / `/` silent)
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

#### Console Commands & Recommended Binds
Players can bind their Ultimate and Ability keys in the CS2 console:
```text
bind v ultimate
bind c ability
```
- `ultimate` / `css_ultimate` — Activate your race's Ultimate skill. When `allow_inspect_ultimate_bind` is `true` in `wcs.json`, players can also opt in to triggering their Ultimate with the weapon inspect key (`F` / `+lookatweapon`).
- `ability` / `css_ability` — Activate your race's Ability skill (e.g. Shadow Hunter Serpent Ward).
- `wcs_reload` — Reload `wcs.json`, `gamedata.json`, `items.json`, and all `races/*.json` configs (admin / server console).
- `wcs_tracetest` — Run a live Source 2 Line + Player-BBox Hull trace self-test (admin / server console).

---

## Build

For developers compiling `WcsCs2` from source.

### Prerequisites
- **.NET 10.0 SDK** (`dotnet --version` -> `10.0.x`)

### Compile & Publish (Linux x64)
```bash
# Restore & build Release
dotnet build -c Release src/WcsCs2.sln

# Publish ready-to-deploy Linux x64 plugin bundle
dotnet publish src/WcsCs2/WcsCs2.csproj -c Release -r linux-x64 --no-self-contained -o publish/WcsCs2
```

Publishing with `-r linux-x64 --no-self-contained` is required so `Microsoft.Data.Sqlite` bundles the Linux x64 native `libe_sqlite3.so` binary alongside `WcsCs2.dll`, `Dapper.dll`, `SQLitePCLRaw.*.dll`, and the default `configs/` directory (while keeping `CounterStrikeSharp.API` and `CS2MenuManager` runtime-excluded so they resolve from CounterStrikeSharp's `api/` and `shared/` folders).

### Project Structure

- `src/WcsCs2/Config/` — Strongly typed JSON config models (`wcs.json`, `gamedata.json`, `items.json`, `races/*.json`)
- `src/WcsCs2/Core/` — Sessions, player state management, progression/XP/bank, shop, bots, race registry
- `src/WcsCs2/Data/` — Async WAL-mode SQLite repository
- `src/WcsCs2/Services/` — Combat/damage hooks, weapons/restrictions, visual/audio effects, ELF64 symbol resolver, Source 2 collision tracer & teleportation
- `src/WcsCs2/Skills/` — Skill execution engine, composable `Primitives/`, and `CustomHandlers/`
- `src/WcsCs2/Menus/` — `CS2MenuManager` menu flows
- `src/WcsCs2/configs/` — Default server config, gamedata, 4 shop items, and 12 race definitions
