using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Enum;
using CS2MenuManager.API.Interface;
using CS2MenuManager.API.Menu;
using WcsCs2.Config;
using WcsCs2.Core;
using WcsCs2.Data;
using WcsCs2.Skills;

namespace WcsCs2.Menus;

public sealed class WcsMenuService
{
    private readonly BasePlugin _plugin;
    private readonly WcsConfig _config;
    private readonly RaceRegistry _registry;
    private readonly ProgressionService _progression;
    private readonly ShopService _shop;
    private readonly IWcsRepository _repo;
    private readonly IEffectsService _effects;

    public WcsMenuService(
        BasePlugin plugin,
        WcsConfig config,
        RaceRegistry registry,
        ProgressionService progression,
        ShopService shop,
        IWcsRepository repo,
        IEffectsService effects)
    {
        _plugin = plugin;
        _config = config;
        _registry = registry;
        _progression = progression;
        _shop = shop;
        _repo = repo;
        _effects = effects;
    }

    public bool IsWcsAdmin(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot) return false;
        if (_config.AdminSteamIds.Contains(player.SteamID)) return true;
        return AdminManager.PlayerHasPermissions(player, "@css/root")
            || AdminManager.PlayerHasPermissions(player, "@wcs/admin");
    }

    private BaseMenu CreateMenu(CCSPlayerController player, string title, IMenu? prevMenu = null)
    {
        var session = _progression.GetTypedSession(player);
        string pref = (session?.MenuType ?? _config.DefaultMenuType).Trim().ToLowerInvariant();

        BaseMenu menu = pref switch
        {
            "chat" => new ChatMenu(title, _plugin),
            "html" or "centerhtml" => new CenterHtmlMenu(title, _plugin),
            "console" => new ConsoleMenu(title, _plugin),
            _ => new WasdMenu(title, _plugin)
        };
        menu.PrevMenu = prevMenu;
        menu.ExitButton = true;
        return menu;
    }

    public void OpenMainMenu(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        string subtitle = race != null && session != null
            ? $"WCS Menu | {race.Name} (Lvl {session.RaceLevel})"
            : "WCS Main Menu";

        var menu = CreateMenu(player, subtitle);
        menu.AddItem("Change Race", (p, _) => OpenChangeRaceCategories(p, menu));
        menu.AddItem($"Spend Skills ({session?.UnusedPoints ?? 0} pts)", (p, _) => OpenSpendSkillsMenu(p, menu));
        menu.AddItem("Show My Skills", (p, _) => PrintMySkills(p));
        menu.AddItem("Reset Skills", (p, _) => _progression.ResetSkills(p));
        menu.AddItem("Shop Menu", (p, _) => OpenShopMenu(p, menu));
        menu.AddItem("Race Info", (p, _) => OpenRaceInfoCategories(p, menu));
        menu.AddItem("Player Info", (p, _) => OpenPlayerInfoMenu(p, menu));
        menu.AddItem($"Level Bank ({session?.BankLevel ?? 0} lvls)", (p, _) => OpenLevelBankMenu(p, menu));
        menu.AddItem("WCS Top 10", (p, _) => ShowTopPlayers(p));
        menu.AddItem("My Rank & XP", (p, _) => ShowMyRank(p));
        menu.AddItem("Settings (Menu / Ultimate Bind)", (p, _) => OpenSettingsMenu(p, menu));
        menu.AddItem("WCS Help", (p, _) => PrintHelp(p));

        if (IsWcsAdmin(player))
        {
            menu.AddItem("[ADMIN] WCS Admin", (p, _) => OpenAdminMenu(p, menu));
        }

        menu.Display(player, 30);
    }

    public void OpenChangeRaceCategories(CCSPlayerController player, IMenu? prev = null, string? searchFilter = null)
    {
        if (!player.IsValid || player.IsBot) return;

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var filtered = _registry.Races
                .Where(r => r.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)
                         || r.Key.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (filtered.Count == 0)
            {
                _effects.PrintChat(player, $"No races matching '{searchFilter}'.");
                return;
            }
            if (filtered.Count == 1)
            {
                _progression.TryChangeRace(player, filtered[0].Key, false, out var msg);
                _effects.PrintChat(player, msg);
                return;
            }
            OpenRaceListMenu(player, $"Search: {searchFilter}", filtered, prev);
            return;
        }

        var menu = CreateMenu(player, "Select Race Category", prev);
        foreach (var (catName, races) in _registry.Categories.OrderBy(kv => kv.Key == "Standard races" ? 0 : kv.Key == "All races" ? 2 : 1).ThenBy(kv => kv.Key))
        {
            string cName = catName;
            var cRaces = races;
            menu.AddItem($"{cName} ({cRaces.Count})", (p, _) => OpenRaceListMenu(p, cName, cRaces, menu));
        }
        menu.Display(player, 30);
    }

    private void OpenRaceListMenu(CCSPlayerController player, string title, List<RaceDefinition> races, IMenu? prev)
    {
        var session = _progression.GetTypedSession(player);
        int totalLvl = session?.TotalLevel ?? 0;

        var menu = CreateMenu(player, $"{title} (Total Lvl: {totalLvl})", prev);
        foreach (var race in races)
        {
            var r = race;
            bool isCurrent = session != null && session.CurrentRaceKey.Equals(r.Key, StringComparison.OrdinalIgnoreCase);
            bool unlocked = totalLvl >= r.RequiredLevel;
            int myLvl = session?.AllRaces.TryGetValue(r.Key, out var rp) == true ? rp.Level : 0;

            string lvlText = r.MaxLevel > 0 ? $"Lvl {myLvl}/{r.MaxLevel}" : $"Lvl {myLvl}";
            string status = isCurrent ? "[ACTIVE]"
                : !unlocked ? $"[Req {r.RequiredLevel}]"
                : $"[{lvlText}]";

            var disable = unlocked ? DisableOption.None : DisableOption.DisableShowNumber;
            menu.AddItem($"{r.Name} {status}", (p, _) =>
            {
                _progression.TryChangeRace(p, r.Key, false, out var feedback);
                _effects.PrintChat(p, feedback);
            }, disable);
        }
        menu.Display(player, 30);
    }

    public void OpenSpendSkillsMenu(CCSPlayerController player, IMenu? prev = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        if (session == null || race == null) return;

        if (session.UnusedPoints <= 0)
        {
            _effects.PrintChat(player, "You have no unspent skill points.");
            return;
        }

        var menu = CreateMenu(player, $"Spend Skills ({session.UnusedPoints} pts) - {race.Name}", prev);
        foreach (var skill in race.Skills)
        {
            var sk = skill;
            int cur = session.GetSkillLevel(sk.Key);
            bool maxed = cur >= sk.MaxLevel;
            bool lvlLocked = session.RaceLevel < sk.RequiredLevel;
            string suffix = maxed ? "[MAX]" : lvlLocked ? $"[Req Lvl {sk.RequiredLevel}]" : $"[{cur}/{sk.MaxLevel}]";

            var disable = (!maxed && !lvlLocked) ? DisableOption.None : DisableOption.DisableShowNumber;
            menu.AddItem($"{sk.Name} {suffix}", (p, opt) =>
            {
                _progression.TrySpendSkill(p, sk.Key, out var feedback);
                _effects.PrintChat(p, feedback);
                var s2 = _progression.GetTypedSession(p);
                if (s2 != null && s2.UnusedPoints > 0)
                    OpenSpendSkillsMenu(p, prev);
            }, disable);
        }
        menu.Display(player, 30);
    }

    public void OpenShopMenu(CCSPlayerController player, IMenu? prev = null)
    {
        if (!player.IsValid || player.IsBot) return;
        int money = player.InGameMoneyServices?.Account ?? 0;
        var session = _progression.GetTypedSession(player);

        var menu = CreateMenu(player, $"WCS Shop (${money})", prev);
        foreach (var item in _registry.Items)
        {
            var it = item;
            int owned = session?.GetItemCount(it.Key) ?? 0;
            bool maxed = it.MaxCount > 0 && owned >= it.MaxCount && it.Duration != ItemDuration.Instant;
            bool affordable = money >= it.Cost;
            string tag = maxed ? "[OWNED]" : $"${it.Cost}";
            var disable = (!maxed && affordable) ? DisableOption.None : DisableOption.DisableShowNumber;

            menu.AddItem($"{it.Name} ({tag})", (p, _) =>
            {
                _shop.TryBuyItem(p, it.Key, out var feedback);
                _effects.PrintChat(p, feedback);
            }, disable);
        }
        menu.Display(player, 30);
    }

    public void PrintShopInfo(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        _effects.PrintChat(player, $"{ChatColors.Gold}=== WCS Shop Items ===");
        foreach (var item in _registry.Items)
        {
            _effects.PrintChat(player, $"{ChatColors.Green}{item.Name}{ChatColors.Default} (${item.Cost}): {item.Description}");
        }
    }

    public void OpenRaceInfoCategories(CCSPlayerController player, IMenu? prev = null, string? raceQuery = null)
    {
        if (!player.IsValid || player.IsBot) return;
        if (!string.IsNullOrWhiteSpace(raceQuery))
        {
            var found = _registry.Races.FirstOrDefault(r =>
                r.Key.Equals(raceQuery, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(raceQuery, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                PrintRaceInfo(player, found);
                return;
            }
        }

        var menu = CreateMenu(player, "Race Info", prev);
        foreach (var race in _registry.Races)
        {
            var r = race;
            menu.AddItem($"{r.Name} (Req {r.RequiredLevel})", (p, _) => PrintRaceInfo(p, r));
        }
        menu.Display(player, 30);
    }

    public void PrintRaceInfo(CCSPlayerController player, RaceDefinition race)
    {
        if (!player.IsValid || player.IsBot) return;
        string maxLvlText = race.MaxLevel > 0 ? race.MaxLevel.ToString() : "Unlimited";
        _effects.PrintChat(player, $"{ChatColors.Gold}=== {race.Name} ==={ChatColors.Default} (Req Lvl: {race.RequiredLevel}, Max Lvl: {maxLvlText})");
        if (!string.IsNullOrWhiteSpace(race.Description))
            _effects.PrintChat(player, $"{ChatColors.LightBlue}{race.Description}");
        foreach (var sk in race.Skills)
        {
            string trig = sk.Trigger switch
            {
                SkillTrigger.PlayerUltimate => "[ULTIMATE]",
                SkillTrigger.PlayerAbility => "[ABILITY]",
                SkillTrigger.PlayerDeath => "[DEATH]",
                SkillTrigger.PlayerVictim => "[VICTIM]",
                SkillTrigger.PlayerAttacker => "[ATTACKER]",
                _ => "[PASSIVE]"
            };
            _effects.PrintChat(player, $"{ChatColors.Green}{sk.Name} {ChatColors.Yellow}{trig}{ChatColors.Default} (Max {sk.MaxLevel}): {sk.Description}");
        }
    }

    public void PrintMySkills(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        if (session == null || race == null) return;

        int reqXp = _progression.GetRequiredXp(session.RaceLevel);
        string maxLvlSuffix = race.MaxLevel > 0 ? $"/{race.MaxLevel}" : "";
        _effects.PrintChat(player, $"{ChatColors.Gold}{race.Name}{ChatColors.Default} | Lvl {ChatColors.Green}{session.RaceLevel}{maxLvlSuffix}{ChatColors.Default} | XP: {session.RaceXp}/{reqXp} | Unused: {session.UnusedPoints} | Total Lvl: {session.TotalLevel}");
        foreach (var sk in race.Skills)
        {
            int lvl = session.GetSkillLevel(sk.Key);
            _effects.PrintChat(player, $"  - {ChatColors.Green}{sk.Name}{ChatColors.Default}: {lvl}/{sk.MaxLevel}");
        }
    }

    public void OpenPlayerInfoMenu(CCSPlayerController player, IMenu? prev = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var menu = CreateMenu(player, "Player Info", prev);
        foreach (var p in Utilities.GetPlayers().Where(x => x != null && x.IsValid && x.Connected == PlayerConnectedState.Connected))
        {
            var target = p;
            var ts = _progression.GetTypedSession(target);
            var tr = ts != null ? _registry.GetRace(ts.CurrentRaceKey) : null;
            string label = $"{target.PlayerName} [{tr?.Name ?? "?"} Lvl {ts?.RaceLevel ?? 0}]";
            menu.AddItem(label, (viewer, _) =>
            {
                if (!target.IsValid) return;
                var s = _progression.GetTypedSession(target);
                var r = s != null ? _registry.GetRace(s.CurrentRaceKey) : null;
                if (s == null || r == null) return;
                _effects.PrintChat(viewer, $"{ChatColors.Gold}{target.PlayerName}{ChatColors.Default} — Race: {ChatColors.Green}{r.Name}{ChatColors.Default} (Lvl {s.RaceLevel}, Total Lvl {s.TotalLevel})");
                foreach (var sk in r.Skills)
                {
                    _effects.PrintChat(viewer, $"  - {sk.Name}: {s.GetSkillLevel(sk.Key)}/{sk.MaxLevel}");
                }
            });
        }
        menu.Display(player, 30);
    }

    public void OpenLevelBankMenu(CCSPlayerController player, IMenu? prev = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        if (session == null || race == null) return;

        var menu = CreateMenu(player, $"Level Bank: {session.BankLevel} lvls -> {race.Name}", prev);
        foreach (int amt in new[] { 1, 5, 10, session.BankLevel })
        {
            if (amt <= 0) continue;
            int a = amt;
            string lbl = amt == session.BankLevel ? $"Withdraw All ({a})" : $"Withdraw {a} level(s)";
            var disable = session.BankLevel >= a ? DisableOption.None : DisableOption.DisableShowNumber;
            menu.AddItem(lbl, (p, _) =>
            {
                _progression.TryWithdrawBankLevels(p, a, out var feedback);
                _effects.PrintChat(p, feedback);
            }, disable);
        }
        if (session.BankLevel <= 0)
        {
            menu.AddItem("No levels available in bank", (_, _) => { }, DisableOption.DisableShowNumber);
        }
        menu.Display(player, 30);
    }

    public void OpenSettingsMenu(CCSPlayerController player, IMenu? prev = null)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        if (session == null) return;

        var menu = CreateMenu(player, "WCS Settings", prev);
        menu.AddItem($"Menu Style: {session.MenuType.ToUpperInvariant()} (Cycle)", (p, _) =>
        {
            var s = _progression.GetTypedSession(p);
            if (s == null) return;
            s.MenuType = s.MenuType.ToLowerInvariant() switch
            {
                "wasd" => "html",
                "html" => "chat",
                _ => "wasd"
            };
            s.IsDirty = true;
            _progression.SaveSession(s);
            _effects.PrintChat(p, $"Menu style changed to {ChatColors.Green}{s.MenuType.ToUpperInvariant()}{ChatColors.Default}.");
            OpenSettingsMenu(p, prev);
        });

        if (_config.AllowInspectUltimateBind)
        {
            menu.AddItem($"Inspect Key (F) triggers Ultimate: {(session.InspectUltimate ? "ON" : "OFF")}", (p, _) =>
            {
                var s = _progression.GetTypedSession(p);
                if (s == null) return;
                s.InspectUltimate = !s.InspectUltimate;
                s.IsDirty = true;
                _progression.SaveSession(s);
                _effects.PrintChat(p, $"Inspect-key (F) ultimate trigger: {(s.InspectUltimate ? "ON" : "OFF")}.");
                OpenSettingsMenu(p, prev);
            });
        }

        menu.Display(player, 30);
    }

    public void ShowTopPlayers(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        int slot = player.Slot;

        _ = Task.Run(async () =>
        {
            var top = await _repo.GetTopPlayersAsync(10);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p == null || !p.IsValid) return;

                _effects.PrintChat(p, $"{ChatColors.Gold}=== WCS Top 10 Players ===");
                if (top.Count == 0)
                {
                    _effects.PrintChat(p, "No ranked players recorded yet.");
                    return;
                }
                for (int i = 0; i < top.Count; i++)
                {
                    var e = top[i];
                    var rName = _registry.GetRace(e.CurrentRace)?.Name ?? e.CurrentRace;
                    _effects.PrintChat(p, $"{ChatColors.Yellow}#{i + 1} {ChatColors.LightBlue}{e.Name}{ChatColors.Default} — Total Lvl {ChatColors.Green}{e.TotalLevel}{ChatColors.Default} ({rName})");
                }
            });
        });
    }

    public void ShowMyRank(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        var session = _progression.GetTypedSession(player);
        if (session == null) return;

        int slot = player.Slot;
        ulong steamId = session.SteamId;
        var race = _registry.GetRace(session.CurrentRaceKey);
        int reqXp = _progression.GetRequiredXp(session.RaceLevel);

        _effects.PrintChat(player, $"Race: {ChatColors.Green}{race?.Name ?? session.CurrentRaceKey}{ChatColors.Default} | Lvl {session.RaceLevel} | XP: {session.RaceXp}/{reqXp} | Rested XP: {session.RestedXp} | Bank: {session.BankLevel}");

        _ = Task.Run(async () =>
        {
            var (rank, totalPlayers, totalLevel) = await _repo.GetPlayerRankAsync(steamId);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p == null || !p.IsValid) return;
                _effects.PrintChat(p, $"Your Rank: {ChatColors.Gold}#{rank}/{totalPlayers}{ChatColors.Default} (Total Level: {ChatColors.Green}{totalLevel}{ChatColors.Default})");
            });
        });
    }

    public void PrintHelp(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot) return;
        _effects.PrintChat(player, $"{ChatColors.Gold}=== WCS Commands ===");
        _effects.PrintChat(player, "Chat: !wcs, !changerace, !spendskills, !resetskills, !showskills, !shopmenu, !raceinfo, !playerinfo, !wcsbank, !wcstop, !wcsrank");
        _effects.PrintChat(player, "Console Binds: bind v ultimate | bind c ability (or enable Inspect-key F in !wcs -> Settings)");
    }

    public void OpenAdminMenu(CCSPlayerController admin, IMenu? prev = null)
    {
        if (!IsWcsAdmin(admin))
        {
            _effects.PrintChat(admin, "You do not have WCS admin permissions.");
            return;
        }

        var menu = CreateMenu(admin, "WCS Admin — Select Player", prev);
        foreach (var p in Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsBot && x.Connected == PlayerConnectedState.Connected))
        {
            var target = p;
            var ts = _progression.GetTypedSession(target);
            menu.AddItem($"{target.PlayerName} (Lvl {ts?.RaceLevel ?? 0})", (a, _) => OpenAdminPlayerActions(a, target, menu));
        }
        menu.Display(admin, 30);
    }

    private void OpenAdminPlayerActions(CCSPlayerController admin, CCSPlayerController target, IMenu? prev)
    {
        if (!target.IsValid) return;
        var menu = CreateMenu(admin, $"Admin: {target.PlayerName}", prev);

        menu.AddItem("Give +100 XP", (a, _) =>
        {
            if (!target.IsValid) return;
            _progression.GiveXp(target, 100, "admin grant");
            _effects.PrintChat(a, $"Gave +100 XP to {target.PlayerName}.");
        });
        menu.AddItem("Give +1 Level", (a, _) =>
        {
            if (!target.IsValid) return;
            _progression.GiveLevels(target, 1);
            _effects.PrintChat(a, $"Gave +1 level to {target.PlayerName}.");
        });
        menu.AddItem("Give +5 Levels", (a, _) =>
        {
            if (!target.IsValid) return;
            _progression.GiveLevels(target, 5);
            _effects.PrintChat(a, $"Gave +5 levels to {target.PlayerName}.");
        });
        menu.AddItem("Take -1 Level", (a, _) =>
        {
            if (!target.IsValid) return;
            _progression.GiveLevels(target, -1);
            _effects.PrintChat(a, $"Removed 1 level from {target.PlayerName}.");
        });
        menu.AddItem("Give +10 Bank Levels", (a, _) =>
        {
            if (!target.IsValid) return;
            var s = _progression.GetTypedSession(target);
            if (s != null)
            {
                s.BankLevel += 10;
                s.IsDirty = true;
                _progression.SaveSession(s);
                _effects.PrintChat(a, $"Gave +10 bank levels to {target.PlayerName} (now {s.BankLevel}).");
            }
        });
        menu.AddItem("Force Change Race", (a, _) => OpenAdminForceRaceMenu(a, target, menu));
        menu.AddItem("Reset Target Skills", (a, _) =>
        {
            if (!target.IsValid) return;
            var s = _progression.GetTypedSession(target);
            if (s != null)
            {
                _progression.ApplySkillResetNow(target, s);
                _effects.PrintChat(a, $"Reset skills for {target.PlayerName}.");
            }
        });

        menu.Display(admin, 30);
    }

    private void OpenAdminForceRaceMenu(CCSPlayerController admin, CCSPlayerController target, IMenu? prev)
    {
        var menu = CreateMenu(admin, $"Set Race for {target.PlayerName}", prev);
        foreach (var race in _registry.Races)
        {
            var r = race;
            menu.AddItem(r.Name, (a, _) =>
            {
                if (!target.IsValid) return;
                _progression.TryChangeRace(target, r.Key, forceAdmin: true, out var msg);
                _effects.PrintChat(a, $"Set {target.PlayerName}'s race to {r.Name}.");
                _effects.PrintChat(target, $"Admin changed your race: {msg}");
            });
        }
        menu.Display(admin, 30);
    }
}
