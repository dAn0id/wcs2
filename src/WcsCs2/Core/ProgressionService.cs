using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using WcsCs2.Config;
using WcsCs2.Data;
using WcsCs2.Skills;

namespace WcsCs2.Core;

public sealed class ProgressionService : ISessionProvider
{
    private readonly BasePlugin _plugin;
    private readonly WcsConfig _config;
    private readonly RaceRegistry _registry;
    private readonly IWcsRepository _repo;
    private readonly IEffectsService _effects;
    private readonly ILogger _logger;
    private readonly Dictionary<int, PlayerSession> _sessionsBySlot = new();
    private readonly Dictionary<ulong, PlayerSession> _sessionsBySteam = new();

    public ProgressionService(
        BasePlugin plugin,
        WcsConfig config,
        RaceRegistry registry,
        IWcsRepository repo,
        IEffectsService effects,
        ILogger logger)
    {
        _plugin = plugin;
        _config = config;
        _registry = registry;
        _repo = repo;
        _effects = effects;
        _logger = logger;
    }

    public event Action<CCSPlayerController, PlayerSession, RaceDefinition>? OnRaceChanged;
    public event Action<CCSPlayerController, PlayerSession>? OnSkillsReset;

    public PlayerSession? GetSessionBySlot(int slot)
        => _sessionsBySlot.TryGetValue(slot, out var s) ? s : null;

    public PlayerSession? GetTypedSession(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return null;
        if (_sessionsBySlot.TryGetValue(player.Slot, out var s)) return s;
        if (player.SteamID > 0 && _sessionsBySteam.TryGetValue(player.SteamID, out s))
        {
            s.Slot = player.Slot;
            _sessionsBySlot[player.Slot] = s;
            return s;
        }
        return null;
    }

    public IPlayerSession? GetSession(CCSPlayerController player) => GetTypedSession(player);

    public RaceDefinition? GetRace(string raceKey) => _registry.GetRace(raceKey);

    public IReadOnlyList<CCSPlayerController> GetAlivePlayers(CsTeam? team = null)
    {
        var list = new List<CCSPlayerController>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.Connected != PlayerConnectedState.Connected) continue;
            if (!p.PawnIsAlive) continue;
            if (team.HasValue && p.Team != team.Value) continue;
            list.Add(p);
        }
        return list;
    }

    public bool IsFriendlyFireAllowed() => _config.FfaEnabled;

    public int GetRequiredXp(int currentLevel)
    {
        int interval = Math.Max(10, _config.XpInterval);
        return interval * Math.Max(1, currentLevel);
    }

    public void EnsureSession(CCSPlayerController player)
    {
        if (player == null || !player.IsValid) return;
        int slot = player.Slot;
        ulong steamId = player.SteamID;
        bool isBot = player.IsBot || player.IsHLTV || steamId == 0;

        if (_sessionsBySlot.TryGetValue(slot, out var existing))
        {
            existing.PlayerName = player.PlayerName ?? existing.PlayerName;
            return;
        }

        var defaultRace = _registry.GetDefaultRace(_config.DefaultRace)?.Key ?? _config.DefaultRace;
        var session = new PlayerSession
        {
            SteamId = steamId,
            Slot = slot,
            PlayerName = player.PlayerName ?? "Player",
            IsBot = isBot,
            CurrentRaceKey = defaultRace,
            MenuType = _config.DefaultMenuType,
            LastConnect = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        session.GetOrCreateRaceProgress(defaultRace);

        _sessionsBySlot[slot] = session;
        if (!isBot && steamId > 0)
        {
            _sessionsBySteam[steamId] = session;
            LoadHumanFromDb(player, session);
        }
        else
        {
            session.IsLoaded = true;
        }
    }

    private void LoadHumanFromDb(CCSPlayerController player, PlayerSession session)
    {
        ulong steamId = session.SteamId;
        int slot = session.Slot;
        string playerName = session.PlayerName;

        _ = Task.Run(async () =>
        {
            try
            {
                var data = await _repo.LoadPlayerAsync(steamId);
                (int rank, int totalPlayers, int _) = (0, 0, 0);
                if (data.Player != null && _config.TopAnnouncementEnable)
                {
                    (rank, totalPlayers, _) = await _repo.GetPlayerRankAsync(steamId);
                }

                Server.NextFrame(() =>
                {
                    if (!_sessionsBySlot.TryGetValue(slot, out var cur) || cur.SteamId != steamId)
                        return;

                    var p = Utilities.GetPlayerFromSlot(slot);
                    if (p == null || !p.IsValid) return;

                    cur.PlayerName = p.PlayerName ?? playerName;

                    if (data.Player == null)
                    {
                        // Brand-new player: grant configured new_player_bank_bonus
                        cur.BankLevel = _config.NewPlayerBankBonus;
                        cur.CurrentRaceKey = _registry.GetDefaultRace(_config.DefaultRace)?.Key ?? _config.DefaultRace;
                        cur.GetOrCreateRaceProgress(cur.CurrentRaceKey);
                        cur.IsLoaded = true;
                        cur.IsDirty = true;
                        SaveSession(cur);

                        if (_config.NewPlayerBankBonus > 0)
                        {
                            _effects.PrintChat(p, $"Welcome! You received {_config.NewPlayerBankBonus} bonus levels in your Level Bank (!wcsbank).");
                        }
                    }
                    else
                    {
                        var validRace = _registry.GetRace(data.Player.CurrentRace)?.Key
                                        ?? _registry.GetDefaultRace(_config.DefaultRace)?.Key
                                        ?? _config.DefaultRace;
                        cur.CurrentRaceKey = validRace;
                        cur.BankLevel = data.Player.BankLevel;
                        cur.RestedXp = data.Player.RestedXp;
                        cur.MenuType = string.IsNullOrWhiteSpace(data.Player.MenuType) ? _config.DefaultMenuType : data.Player.MenuType;
                        cur.InspectUltimate = data.Player.InspectUltimate;
                        cur.LastConnect = data.Player.LastConnect;

                        foreach (var r in data.Races)
                        {
                            var rp = cur.GetOrCreateRaceProgress(r.RaceKey);
                            rp.Xp = r.Xp;
                            rp.Level = r.Level;
                            rp.Unused = r.Unused;
                        }
                        foreach (var s in data.Skills)
                        {
                            var rp = cur.GetOrCreateRaceProgress(s.RaceKey);
                            rp.Skills[s.SkillKey] = s.Level;
                        }
                        cur.GetOrCreateRaceProgress(cur.CurrentRaceKey);

                        // Offline Rested XP accrual (up to 7 days)
                        if (_config.RestedXpEnabled && cur.LastConnect > 0 && _config.RestedXpOfflineIntervalSeconds > 0)
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            long elapsed = Math.Clamp(now - cur.LastConnect, 0, _config.RestedXpOfflineMaxSeconds);
                            int gained = (int)(elapsed / _config.RestedXpOfflineIntervalSeconds);
                            if (gained > 0)
                            {
                                cur.RestedXp += gained;
                                cur.IsDirty = true;
                            }
                        }

                        cur.LastConnect = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        cur.IsLoaded = true;

                        if (_config.TopAnnouncementEnable && rank > 0 && rank <= _config.TopAnnouncementLimit)
                        {
                            Server.PrintToChatAll($" {ChatColors.Green}{_config.ChatPrefix} {ChatColors.LightBlue}{cur.PlayerName} {ChatColors.Default}(Rank #{rank}/{totalPlayers}, Total Lvl {cur.TotalLevel}) connected!");
                        }
                    }

                    ApplyClanTag(p, cur);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WcsCs2] Failed loading player {SteamId}", steamId);
            }
        });
    }

    public void SaveSession(PlayerSession session)
    {
        if (session.IsBot || session.SteamId == 0 || !session.IsLoaded) return;
        var (p, r, s) = session.ToDbRecords();
        _repo.EnqueueSavePlayer(p, r, s);
        session.IsDirty = false;
    }

    public void SaveAllDirty()
    {
        foreach (var session in _sessionsBySlot.Values)
        {
            if (session.IsDirty && !session.IsBot)
                SaveSession(session);
        }
    }

    public void OnPlayerDisconnect(CCSPlayerController player)
    {
        if (player == null) return;
        if (_sessionsBySlot.Remove(player.Slot, out var session))
        {
            if (!session.IsBot)
            {
                SaveSession(session);
                _sessionsBySteam.Remove(session.SteamId);
            }
        }
    }

    public void TickOnlineRestedXp()
    {
        if (!_config.RestedXpEnabled) return;
        foreach (var session in _sessionsBySlot.Values)
        {
            if (session.IsBot || !session.IsLoaded) continue;
            session.RestedXp += 1;
            session.IsDirty = true;
        }
    }

    public void ApplyClanTag(CCSPlayerController player, PlayerSession session)
    {
        if (!_config.RaceClanTag || !player.IsValid) return;
        var race = _registry.GetRace(session.CurrentRaceKey);
        if (race == null) return;

        string tag = race.Name.Length > 12 ? race.Name[..12] : race.Name;
        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    public void GiveXp(CCSPlayerController player, int baseXp, string? reason = null)
    {
        if (baseXp <= 0 || player == null || !player.IsValid) return;
        var session = GetTypedSession(player);
        if (session == null) return;

        var race = _registry.GetRace(session.CurrentRaceKey);
        if (race == null) return;

        int maxLevel = race.MaxLevel; // 0 = uncapped (per legacy entity.py)
        var prog = session.CurrentProgress;
        if (maxLevel > 0 && prog.Level >= maxLevel) return;

        int bonusRested = 0;
        if (_config.RestedXpEnabled && !session.IsBot && session.RestedXp > 0)
        {
            int r = session.RestedXp;
            int basePart = Math.Min(_config.RestedXpGainedBase, r);
            int pctPart = (int)Math.Floor(Math.Max(0, r - basePart) * _config.RestedXpGainedPercent);
            bonusRested = Math.Min(r, Math.Max(1, basePart + pctPart));
            session.RestedXp -= bonusRested;
        }

        int totalGain = baseXp + bonusRested;
        int oldTotalLevel = session.TotalLevel;
        prog.Xp += totalGain;
        session.IsDirty = true;

        int levelsGained = 0;
        while (maxLevel == 0 || prog.Level < maxLevel)
        {
            int req = GetRequiredXp(prog.Level);
            if (prog.Xp < req) break;
            prog.Xp -= req;
            prog.Level++;
            prog.Unused++;
            levelsGained++;
        }

        if (maxLevel > 0 && prog.Level >= maxLevel)
            prog.Xp = 0;

        if (!session.IsBot)
        {
            string restedPart = bonusRested > 0 ? $" (+{bonusRested} rested)" : "";
            string reasonPart = string.IsNullOrEmpty(reason) ? "" : $" for {reason}";
            _effects.PrintChat(player, $"You gained {ChatColors.Green}+{baseXp} XP{restedPart}{ChatColors.Default}{reasonPart}. ({prog.Xp}/{GetRequiredXp(prog.Level)})");

            if (levelsGained > 0)
            {
                OnLevelUp(player, session, race, levelsGained, oldTotalLevel);
            }
        }
    }

    public void GiveLevels(CCSPlayerController player, int levels)
    {
        if (levels == 0 || player == null || !player.IsValid) return;
        var session = GetTypedSession(player);
        if (session == null) return;
        var race = _registry.GetRace(session.CurrentRaceKey);
        if (race == null) return;

        int oldTotal = session.TotalLevel;
        var prog = session.CurrentProgress;
        int maxLevel = race.MaxLevel; // 0 = uncapped

        if (levels > 0)
        {
            int add = maxLevel > 0 ? Math.Min(levels, Math.Max(0, maxLevel - prog.Level)) : levels;
            if (add <= 0) return;
            prog.Level += add;
            prog.Unused += add;
            if (maxLevel > 0 && prog.Level >= maxLevel) prog.Xp = 0;
            session.IsDirty = true;
            if (!session.IsBot)
                OnLevelUp(player, session, race, add, oldTotal);
        }
        else
        {
            int newLevel = Math.Max(0, prog.Level + levels);
            prog.Level = newLevel;
            prog.Xp = 0;
            // Recompute unused cleanly if skills exceed level
            int spent = prog.Skills.Values.Sum();
            if (spent > newLevel)
            {
                prog.Skills.Clear();
                prog.Unused = newLevel;
            }
            else
            {
                prog.Unused = newLevel - spent;
            }
            session.IsDirty = true;
        }

        SaveSession(session);
    }

    private void OnLevelUp(CCSPlayerController player, PlayerSession session, RaceDefinition race, int levelsGained, int oldTotalLevel)
    {
        _effects.PrintChat(player, $"{ChatColors.Gold}LEVEL UP! {ChatColors.Default}{race.Name} is now level {ChatColors.Green}{session.RaceLevel}{ChatColors.Default} ({session.UnusedPoints} unspent skill points — type !spendskills).");

        if (_config.LevelUpEffect && player.PawnIsAlive && player.PlayerPawn.Value?.AbsOrigin is { } origin)
        {
            _effects.DrawBeamRing(origin, 75f, System.Drawing.Color.Gold, 3f, 1.2f, 16, 15f);
            _effects.PlaySound(player, "UI.XP.LevelUp");
        }

        if (_config.UnlockRaceNotification)
        {
            int newTotal = session.TotalLevel;
            foreach (var r in _registry.Races)
            {
                if (r.RequiredLevel > oldTotalLevel && r.RequiredLevel <= newTotal)
                {
                    _effects.PrintChat(player, $"{ChatColors.Gold}Unlocked new race: {ChatColors.Green}{r.Name}{ChatColors.Default} (Req Lvl {r.RequiredLevel})! Type !changerace.");
                }
            }
        }

        SaveSession(session);
    }

    public bool TrySpendSkill(CCSPlayerController player, string skillKey, out string feedback)
    {
        var session = GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        if (session == null || race == null)
        {
            feedback = "Player session not loaded.";
            return false;
        }

        var skill = race.Skills.FirstOrDefault(s => s.Key.Equals(skillKey, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
        {
            feedback = "Skill not found.";
            return false;
        }

        if (session.UnusedPoints <= 0)
        {
            feedback = "You have no unspent skill points.";
            return false;
        }

        int curLvl = session.GetSkillLevel(skill.Key);
        if (curLvl >= skill.MaxLevel)
        {
            feedback = $"{skill.Name} is already at max level ({skill.MaxLevel}).";
            return false;
        }

        if (session.RaceLevel < skill.RequiredLevel)
        {
            feedback = $"{skill.Name} requires race level {skill.RequiredLevel}.";
            return false;
        }

        session.CurrentProgress.Unused--;
        session.SetSkillLevel(skill.Key, curLvl + 1);
        SaveSession(session);
        feedback = $"Upgraded {skill.Name} to level {curLvl + 1}/{skill.MaxLevel}. ({session.UnusedPoints} points left)";
        return true;
    }

    public void ResetSkills(CCSPlayerController player)
    {
        var session = GetTypedSession(player);
        if (session == null) return;

        if (_config.ResetskillsNextRound && player.PawnIsAlive)
        {
            session.PendingSkillReset = true;
            _effects.PrintChat(player, "Your skills will be reset at the start of next round.");
            return;
        }

        ApplySkillResetNow(player, session);
    }

    public void ApplySkillResetNow(CCSPlayerController player, PlayerSession session)
    {
        var prog = session.CurrentProgress;
        prog.Skills.Clear();
        prog.Unused = prog.Level;
        session.PendingSkillReset = false;
        session.IsDirty = true;
        SaveSession(session);
        OnSkillsReset?.Invoke(player, session);
        _effects.PrintChat(player, $"Skills reset! You have {ChatColors.Green}{prog.Unused}{ChatColors.Default} skill points to spend (!spendskills).");
    }

    public bool TryChangeRace(CCSPlayerController player, string raceKey, bool forceAdmin, out string feedback)
    {
        var session = GetTypedSession(player);
        var targetRace = _registry.GetRace(raceKey);
        if (session == null || targetRace == null)
        {
            feedback = "Race not found.";
            return false;
        }

        if (!forceAdmin)
        {
            if (targetRace.RequiredLevel > session.TotalLevel)
            {
                feedback = $"{targetRace.Name} requires Total Level {targetRace.RequiredLevel} (you have {session.TotalLevel}).";
                return false;
            }
            if (targetRace.RestrictTeam > 0 && (int)player.Team == targetRace.RestrictTeam)
            {
                feedback = $"{targetRace.Name} is restricted for your team.";
                return false;
            }
            if (targetRace.TeamLimit > 0)
            {
                int sameTeamCount = _sessionsBySlot.Values.Count(s =>
                {
                    if (s.Slot == player.Slot || !s.CurrentRaceKey.Equals(targetRace.Key, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var other = Utilities.GetPlayerFromSlot(s.Slot);
                    return other != null && other.IsValid && other.Team == player.Team;
                });
                if (sameTeamCount >= targetRace.TeamLimit)
                {
                    feedback = $"{targetRace.Name} has reached its team limit ({targetRace.TeamLimit}).";
                    return false;
                }
            }
        }

        if (_config.ChangeraceNextRound && player.PawnIsAlive && !forceAdmin)
        {
            session.PendingRaceKey = targetRace.Key;
            feedback = $"You will change to {targetRace.Name} on next spawn.";
            return true;
        }

        ApplyRaceChangeNow(player, session, targetRace);
        feedback = $"Changed race to {targetRace.Name} (Level {session.RaceLevel}).";
        return true;
    }

    public void ApplyRaceChangeNow(CCSPlayerController player, PlayerSession session, RaceDefinition targetRace)
    {
        session.PendingRaceKey = null;
        session.CurrentRaceKey = targetRace.Key;
        session.GetOrCreateRaceProgress(targetRace.Key);
        session.ResetAllCooldowns();
        session.IsDirty = true;

        ApplyClanTag(player, session);
        SaveSession(session);
        OnRaceChanged?.Invoke(player, session, targetRace);
    }

    public bool TryWithdrawBankLevels(CCSPlayerController player, int amount, out string feedback)
    {
        var session = GetTypedSession(player);
        var race = session != null ? _registry.GetRace(session.CurrentRaceKey) : null;
        if (session == null || race == null)
        {
            feedback = "Session not loaded.";
            return false;
        }

        if (amount <= 0 || session.BankLevel < amount)
        {
            feedback = $"Not enough levels in bank (available: {session.BankLevel}).";
            return false;
        }

        int maxLevel = race.MaxLevel; // 0 = uncapped
        int room = maxLevel > 0 ? Math.Max(0, maxLevel - session.RaceLevel) : amount;
        int actual = Math.Min(amount, room);
        if (actual <= 0)
        {
            feedback = $"{race.Name} is already at max level ({maxLevel}).";
            return false;
        }

        session.BankLevel -= actual;
        GiveLevels(player, actual);
        feedback = $"Withdrew {actual} level(s) into {race.Name}. Bank remaining: {session.BankLevel}.";
        return true;
    }

    public void AwardKillXp(CCSPlayerController attacker, CCSPlayerController victim, string weapon, bool headshot)
    {
        if (!attacker.IsValid || !victim.IsValid || attacker.Slot == victim.Slot) return;
        if (!_config.FfaEnabled && attacker.Team == victim.Team) return;

        var attSession = GetTypedSession(attacker);
        var vicSession = GetTypedSession(victim);
        if (attSession == null) return;

        int xp = victim.IsBot ? _config.KillBotXp : _config.KillXp;
        var parts = new List<string> { "kill" };

        if (weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) || weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
        {
            xp += _config.KnifeXp;
            parts.Add("knife");
        }
        if (headshot)
        {
            xp += _config.HeadshotXp;
            parts.Add("headshot");
        }
        if (vicSession != null && vicSession.RaceLevel > attSession.RaceLevel && _config.BonusXpPerLevelDiff > 0)
        {
            int diff = Math.Min(vicSession.RaceLevel - attSession.RaceLevel, _config.BonusXpLevelCap);
            int bonus = diff * _config.BonusXpPerLevelDiff;
            if (bonus > 0)
            {
                xp += bonus;
                parts.Add($"+{bonus} level diff");
            }
        }

        GiveXp(attacker, xp, string.Join(" + ", parts));
    }
}
