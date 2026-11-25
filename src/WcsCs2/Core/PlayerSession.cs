using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Data;
using WcsCs2.Skills;

namespace WcsCs2.Core;

public sealed class RaceProgress
{
    public string RaceKey { get; set; } = string.Empty;
    public int Xp { get; set; }
    public int Level { get; set; }
    public int Unused { get; set; }
    public Dictionary<string, int> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlayerSession : IPlayerSession
{
    private readonly Dictionary<string, RaceProgress> _races = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _cooldownExpiresAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeItems = new(StringComparer.OrdinalIgnoreCase);

    public ulong SteamId { get; set; }
    public int Slot { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsDirty { get; set; }

    public string CurrentRaceKey { get; set; } = "undead_scourge";
    public string? PendingRaceKey { get; set; }
    public bool PendingSkillReset { get; set; }
    public int BankLevel { get; set; }
    public int RestedXp { get; set; }
    public long LastConnect { get; set; }
    public string MenuType { get; set; } = "wasd";
    public bool InspectUltimate { get; set; }

    public bool UltimateBlocked { get; set; }
    public bool SkillsDisabled { get; set; }

    // Combat/respawn tracking
    public Vector? LastDeathOrigin { get; set; }
    public List<string> LastDeathWeapons { get; set; } = new();
    public Vector? LastSafeOrigin { get; set; }

    public IReadOnlyDictionary<string, RaceProgress> AllRaces => _races;
    public IReadOnlyDictionary<string, int> ActiveItems => _activeItems;

    public RaceProgress GetOrCreateRaceProgress(string raceKey, bool initializeAtLevelOne = true)
    {
        if (!_races.TryGetValue(raceKey, out var prog))
        {
            prog = new RaceProgress
            {
                RaceKey = raceKey,
                Level = initializeAtLevelOne ? 1 : 0,
                Unused = initializeAtLevelOne ? 1 : 0
            };
            _races[raceKey] = prog;
        }
        return prog;
    }

    public RaceProgress CurrentProgress => GetOrCreateRaceProgress(CurrentRaceKey);

    public int RaceLevel => CurrentProgress.Level;
    public int RaceXp => CurrentProgress.Xp;
    public int UnusedPoints => CurrentProgress.Unused;
    public int TotalLevel => _races.Values.Sum(r => r.Level);

    public IReadOnlyDictionary<string, int> SkillLevels => CurrentProgress.Skills;

    public int GetSkillLevel(string skillKey)
    {
        return CurrentProgress.Skills.TryGetValue(skillKey, out var lvl) ? lvl : 0;
    }

    public void SetSkillLevel(string skillKey, int level)
    {
        CurrentProgress.Skills[skillKey] = Math.Max(0, level);
        IsDirty = true;
    }

    public bool IsOnCooldown(string skillKey, out float remainingSeconds)
    {
        if (_cooldownExpiresAt.TryGetValue(skillKey, out var expiresAt))
        {
            float rem = expiresAt - Server.CurrentTime;
            if (rem > 0.01f)
            {
                remainingSeconds = rem;
                return true;
            }
            _cooldownExpiresAt.Remove(skillKey);
        }
        remainingSeconds = 0f;
        return false;
    }

    public void SetCooldown(string skillKey, float seconds)
    {
        if (seconds <= 0f)
        {
            _cooldownExpiresAt.Remove(skillKey);
            return;
        }
        _cooldownExpiresAt[skillKey] = Server.CurrentTime + seconds;
    }

    public void ResetCooldown(string skillKey) => _cooldownExpiresAt.Remove(skillKey);
    public void ResetAllCooldowns() => _cooldownExpiresAt.Clear();

    public int GetItemCount(string itemKey) => _activeItems.TryGetValue(itemKey, out var c) ? c : 0;

    public void AddItemCount(string itemKey, int delta = 1)
    {
        _activeItems[itemKey] = GetItemCount(itemKey) + delta;
    }

    public void ClearItems(Func<string, bool>? predicate = null)
    {
        if (predicate == null)
        {
            _activeItems.Clear();
            return;
        }
        var toRemove = _activeItems.Keys.Where(predicate).ToList();
        foreach (var k in toRemove) _activeItems.Remove(k);
    }

    public (PlayerRecord Player, List<RaceRecord> Races, List<SkillRecord> Skills) ToDbRecords()
    {
        var p = new PlayerRecord
        {
            SteamId = SteamId,
            Name = PlayerName,
            CurrentRace = CurrentRaceKey,
            LastConnect = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            BankLevel = BankLevel,
            RestedXp = RestedXp,
            MenuType = MenuType,
            InspectUltimate = InspectUltimate
        };

        var raceRecs = new List<RaceRecord>(_races.Count);
        var skillRecs = new List<SkillRecord>();
        foreach (var rp in _races.Values)
        {
            raceRecs.Add(new RaceRecord
            {
                SteamId = SteamId,
                RaceKey = rp.RaceKey,
                Xp = rp.Xp,
                Level = rp.Level,
                Unused = rp.Unused
            });
            foreach (var (sk, lvl) in rp.Skills)
            {
                skillRecs.Add(new SkillRecord
                {
                    SteamId = SteamId,
                    RaceKey = rp.RaceKey,
                    SkillKey = sk,
                    Level = lvl
                });
            }
        }
        return (p, raceRecs, skillRecs);
    }
}
