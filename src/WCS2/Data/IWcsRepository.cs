namespace WCS2.Data;

public sealed class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CurrentRace { get; set; } = "undead_scourge";
    public long LastConnect { get; set; }
    public int BankLevel { get; set; }
    public int RestedXp { get; set; }
    public string MenuType { get; set; } = "wasd";
    public bool InspectUltimate { get; set; }
}

public sealed class RaceRecord
{
    public ulong SteamId { get; set; }
    public string RaceKey { get; set; } = string.Empty;
    public int Xp { get; set; }
    public int Level { get; set; }
    public int Unused { get; set; }
}

public sealed class SkillRecord
{
    public ulong SteamId { get; set; }
    public string RaceKey { get; set; } = string.Empty;
    public string SkillKey { get; set; } = string.Empty;
    public int Level { get; set; }
}

public sealed class TopPlayerEntry
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CurrentRace { get; set; } = string.Empty;
    public int TotalLevel { get; set; }
}

public sealed class LoadedPlayerData
{
    public PlayerRecord? Player { get; init; }
    public List<RaceRecord> Races { get; init; } = new();
    public List<SkillRecord> Skills { get; init; } = new();
}

public interface IWcsRepository : IDisposable
{
    Task InitializeAsync();
    Task<LoadedPlayerData> LoadPlayerAsync(ulong steamId);
    void EnqueueSavePlayer(PlayerRecord player, IReadOnlyCollection<RaceRecord> races, IReadOnlyCollection<SkillRecord> skills);
    Task<List<TopPlayerEntry>> GetTopPlayersAsync(int limit = 10);
    Task<(int Rank, int TotalPlayers, int TotalLevel)> GetPlayerRankAsync(ulong steamId);
    Task FlushAsync();
}
