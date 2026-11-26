# Changelog

All notable changes to **WCS2** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.4] - 2026-11-26 — New Skill Primitives & In-Game Updater

### Added
- **In-Game GitHub Release Updater (`UpdateService`):**
  - Added `[ROOT] WCS2 Updates` submenu inside `!wcsadmin` (restricted to `@css/root` / `admin_steam_ids`) for checking latest GitHub releases, viewing release notes, and downloading/installing updates in-game.
  - Added once-per-map red chat notifications for Root Admins on team join when a newer GitHub release is available (`Current WCS2 Version: ...`, `New Version: ...`).
  - Added `wcs_update_check` console/RCON command and `wcs.json` updater configuration (`update_check_enabled`, `github_repo`, optional `github_token`).
  - Downloads verify strict `SHA256SUMS.txt` checksums, guard against zip-slip, preserve `configs/` and `data/`, replace plugin binaries atomically, and live-reload via `css_plugins reload WCS2`.
- **6 New Skill Primitives (40 Built-In Total):** Added `cheat_death`, `splash_damage`, `revive_teammate`, `player_scale`, `infinite_ammo`, and `instant_restock` — all wired into `SkillExecutionEngine`, documented in `README.md`, and integrated into the interactive Race Builder TUI (`wcs2-race-builder.py`).
- **Teammate Target Selectors:** Added `nearby_teammates` (alive teammates within `radius`, excluding self) and `all_teammates` (all alive teammates, excluding self) target selectors for team-oriented skills such as `instant_restock`.

### Changed
- **Auto-Updating Bundled `.py` Tooling:** `RaceRegistry.EnsureConfigsSeeded` now automatically updates bundled `.py` scripts (`wcs2-race-builder.py`) on byte differences with `0755` permissions while continuing to leave all customized `.json` config files untouched.
- **Clean Packaging:** Excluded `configs/**/__pycache__/**` and `configs/**/*.pyc` from `WCS2.csproj` builds and publish outputs.

## [1.0.3] - 2026-11-26 — Custom Race Builder Tool

### Added
- **Interactive TUI Race Builder (`configs/races/wcs2-race-builder.py`):** Zero-dependency Python 3 `curses` wizard bundled directly in `configs/races/` for creating, editing, previewing, and validating custom WCS2 race JSON files (`./<race_key>.json` default save, plus `--load`, `--list-skills`, and `--validate` CLI helpers).
- **Custom Race Creation & Skill Reference Guide:** Added a complete `## Creating Custom Races` admin guide to `README.md` covering copy-paste race templates, triggers, target selectors, all 34 built-in skill primitives, and all 6 reusable custom handlers.
- **Executable Mode Preservation:** `RaceRegistry.EnsureConfigsSeeded` automatically sets `0755` (`+x`) on seeded `.py` files on Linux, and the release workflow enforces `+x` before packaging `.zip` and `.tar.gz` release archives.

### Changed
- Linked the original Warcraft:Source repository by [ThaPwned](https://github.com/ThaPwned/WCS) in `README.md`.
- Removed the non-standard custom race JSON from the default bundled race set while keeping all reusable C# custom handlers intact for server admins.

## [1.0.2] - 2026-11-26 — Bugfixing

### Fixed
- **WASD Menu Key Hints (`W/S`, `E`, `Shift`, `Tab`):** `WCS2` now calls `CS2MenuManager`'s `MenuManager.ReloadConfig()` on startup and `wcs_reload` so `shared/CS2MenuManager/config.toml` (`[Buttons]`, `[WasdMenu]`, `[Lang]`) loads automatically without requiring the optional `CS2MenuManager_MenuManager` plugin.
- **Update-Safe Config Preservation:** Shipped default configs under `defaults/configs/` instead of `configs/` and added non-overwriting `EnsureConfigsSeeded` on startup and `wcs_reload`, ensuring extracting a new release archive over `plugins/WCS2/` never overwrites customized `wcs.json`, `gamedata.json`, `items.json`, `races/*.json`, or `data/wcs2.sqlite`.

### Changed
- Added inline `//` documentation comments to `configs/gamedata.json` explaining the `auto`, `dynamic`, and `gamedata` symbol resolution modes.

## [1.0.1] - 2026-11-25 — Bugfixing & Consistency

### Changed
- **Consistent `WCS2` Naming:** Standardized project, solution, assembly, plugin directory, namespaces, log prefixes, release archives, and default SQLite database (`WCS2.sln`, `WCS2.csproj`, `WCS2.dll`, `plugins/WCS2/`, `wcs2.sqlite`, `WCS2-v*-linux-x64.{zip,tar.gz}`).
- **Documentation & Visuals:** Added direct official download links for all prerequisites in `README.md` (`Counter-Strike 2 Dedicated Server`, `Metamod:Source`, `CounterStrikeSharp`, `CS2MenuManager`, `.NET 10.0 SDK`) and added the optimized project hero banner (`assets/banner.jpg`).

## [1.0.0] - 2026-11-25 — Initial Release

### Added
- **Core WCS2 Engine:** Data-driven CounterStrikeSharp (`.NET 10.0`) port of Warcraft:Source (WCS) for Counter-Strike 2 Linux x64 dedicated servers, featuring 11 Standard Races, 4 Shop Items, per-race XP & levels, Rested XP (online & offline), Level Bank, bot race support, and `wcsadmin`.
- **SQLite Persistence:** Embedded async WAL-mode SQLite database (`Microsoft.Data.Sqlite` + `Dapper`) with bundled Linux x64 `libe_sqlite3.so` auto-initialized on first boot.
- **Interactive Menus:** Full `CS2MenuManager` integration (`WasdMenu`, `CenterHtmlMenu`, `ChatMenu`, `ConsoleMenu`).
- **Source 2 Collision-Safe Tracing:** Native raycast + standing/crouched player-hull validation (`Source2TraceEngine`) preventing wall/prop clipping on teleport and prop placement, with 32-byte aligned unmanaged trace buffers (`NativeMemory.AlignedAlloc`), dynamic ELF64 auto-resolution (`ElfSymbolResolver`), external `configs/gamedata.json` fallback/override, `use_native_trace` toggle, and `wcs_tracetest` diagnostic command.
- **Dash-Style Teleport Alternative:** Added the `teleport_dash` primitive (velocity launch + sound + electric/distort particles) and included a commented-out alternative in `human_alliance.json` using JSONC comment support.
- **Automated Linux x64 Releases:** Added `.github/workflows/release.yml` to build, verify, and publish `.zip`, `.tar.gz`, and `SHA256SUMS.txt` artifacts on `v*` tags.

[1.0.4]: https://github.com/dAn0id/wcs2/compare/v1.0.3...v1.0.4
[1.0.3]: https://github.com/dAn0id/wcs2/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/dAn0id/wcs2/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/dAn0id/wcs2/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/dAn0id/wcs2/releases/tag/v1.0.0
