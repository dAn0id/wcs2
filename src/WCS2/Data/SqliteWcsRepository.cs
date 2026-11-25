using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WCS2.Data;

public sealed class SqliteWcsRepository : IWcsRepository
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly Channel<Func<Task>> _writeChannel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SqliteWcsRepository(string databasePath, ILogger logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();

        _writeChannel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(ProcessWritesAsync);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public async Task InitializeAsync()
    {
        using var conn = OpenConnection();
        const string schema = """
            CREATE TABLE IF NOT EXISTS players (
                steamid INTEGER PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                current_race TEXT NOT NULL DEFAULT 'undead_scourge',
                lastconnect INTEGER NOT NULL DEFAULT 0,
                bank_level INTEGER NOT NULL DEFAULT 0,
                rested_xp INTEGER NOT NULL DEFAULT 0,
                menu_type TEXT NOT NULL DEFAULT 'wasd',
                inspect_ultimate INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS races (
                steamid INTEGER NOT NULL,
                race_key TEXT NOT NULL,
                xp INTEGER NOT NULL DEFAULT 0,
                level INTEGER NOT NULL DEFAULT 0,
                unused INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (steamid, race_key)
            );

            CREATE TABLE IF NOT EXISTS skills (
                steamid INTEGER NOT NULL,
                race_key TEXT NOT NULL,
                skill_key TEXT NOT NULL,
                level INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (steamid, race_key, skill_key)
            );

            CREATE TABLE IF NOT EXISTS settings (
                setting TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_races_steamid ON races(steamid);
            CREATE INDEX IF NOT EXISTS idx_skills_steamid_race ON skills(steamid, race_key);
            """;

        await conn.ExecuteAsync(schema);
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO settings (setting, value) VALUES ('schema_version', '1');");
        _logger.LogInformation("[WCS2] SQLite database initialized cleanly.");
    }

    public async Task<LoadedPlayerData> LoadPlayerAsync(ulong steamId)
    {
        using var conn = OpenConnection();
        long sid = unchecked((long)steamId);

        var player = await conn.QuerySingleOrDefaultAsync<PlayerRecord>(
            """
            SELECT steamid AS SteamId, name AS Name, current_race AS CurrentRace,
                   lastconnect AS LastConnect, bank_level AS BankLevel,
                   rested_xp AS RestedXp, menu_type AS MenuType,
                   inspect_ultimate AS InspectUltimate
            FROM players WHERE steamid = @sid
            """,
            new { sid });

        var races = (await conn.QueryAsync<RaceRecord>(
            """
            SELECT steamid AS SteamId, race_key AS RaceKey, xp AS Xp, level AS Level, unused AS Unused
            FROM races WHERE steamid = @sid
            """,
            new { sid })).ToList();

        var skills = (await conn.QueryAsync<SkillRecord>(
            """
            SELECT steamid AS SteamId, race_key AS RaceKey, skill_key AS SkillKey, level AS Level
            FROM skills WHERE steamid = @sid
            """,
            new { sid })).ToList();

        return new LoadedPlayerData
        {
            Player = player,
            Races = races,
            Skills = skills
        };
    }

    public void EnqueueSavePlayer(
        PlayerRecord player,
        IReadOnlyCollection<RaceRecord> races,
        IReadOnlyCollection<SkillRecord> skills)
    {
        if (_disposed) return;

        // Snapshot copies so caller mutations never race with async DB write
        var pCopy = new PlayerRecord
        {
            SteamId = player.SteamId,
            Name = player.Name,
            CurrentRace = player.CurrentRace,
            LastConnect = player.LastConnect,
            BankLevel = player.BankLevel,
            RestedXp = player.RestedXp,
            MenuType = player.MenuType,
            InspectUltimate = player.InspectUltimate
        };
        var rCopy = races.Select(r => new RaceRecord
        {
            SteamId = r.SteamId,
            RaceKey = r.RaceKey,
            Xp = r.Xp,
            Level = r.Level,
            Unused = r.Unused
        }).ToList();
        var sCopy = skills.Select(s => new SkillRecord
        {
            SteamId = s.SteamId,
            RaceKey = s.RaceKey,
            SkillKey = s.SkillKey,
            Level = s.Level
        }).ToList();

        _writeChannel.Writer.TryWrite(() => SavePlayerInternalAsync(pCopy, rCopy, sCopy));
    }

    private async Task SavePlayerInternalAsync(PlayerRecord player, List<RaceRecord> races, List<SkillRecord> skills)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        long sid = unchecked((long)player.SteamId);

        await conn.ExecuteAsync(
            """
            INSERT INTO players (steamid, name, current_race, lastconnect, bank_level, rested_xp, menu_type, inspect_ultimate)
            VALUES (@sid, @Name, @CurrentRace, @LastConnect, @BankLevel, @RestedXp, @MenuType, @InspectUltimate)
            ON CONFLICT(steamid) DO UPDATE SET
                name = excluded.name,
                current_race = excluded.current_race,
                lastconnect = excluded.lastconnect,
                bank_level = excluded.bank_level,
                rested_xp = excluded.rested_xp,
                menu_type = excluded.menu_type,
                inspect_ultimate = excluded.inspect_ultimate;
            """,
            new
            {
                sid,
                player.Name,
                player.CurrentRace,
                player.LastConnect,
                player.BankLevel,
                player.RestedXp,
                player.MenuType,
                InspectUltimate = player.InspectUltimate ? 1 : 0
            },
            tx);

        foreach (var r in races)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO races (steamid, race_key, xp, level, unused)
                VALUES (@sid, @RaceKey, @Xp, @Level, @Unused)
                ON CONFLICT(steamid, race_key) DO UPDATE SET
                    xp = excluded.xp,
                    level = excluded.level,
                    unused = excluded.unused;
                """,
                new { sid, r.RaceKey, r.Xp, r.Level, r.Unused },
                tx);
        }

        foreach (var s in skills)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO skills (steamid, race_key, skill_key, level)
                VALUES (@sid, @RaceKey, @SkillKey, @Level)
                ON CONFLICT(steamid, race_key, skill_key) DO UPDATE SET
                    level = excluded.level;
                """,
                new { sid, s.RaceKey, s.SkillKey, s.Level },
                tx);
        }

        tx.Commit();
    }

    public async Task<List<TopPlayerEntry>> GetTopPlayersAsync(int limit = 10)
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<TopPlayerEntry>(
            """
            SELECT p.steamid AS SteamId,
                   p.name AS Name,
                   p.current_race AS CurrentRace,
                   COALESCE(SUM(r.level), 0) AS TotalLevel
            FROM players p
            LEFT JOIN races r ON r.steamid = p.steamid
            GROUP BY p.steamid
            ORDER BY TotalLevel DESC, p.lastconnect DESC
            LIMIT @limit;
            """,
            new { limit });
        return rows.ToList();
    }

    public async Task<(int Rank, int TotalPlayers, int TotalLevel)> GetPlayerRankAsync(ulong steamId)
    {
        using var conn = OpenConnection();
        long sid = unchecked((long)steamId);

        int totalLevel = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(SUM(level), 0) FROM races WHERE steamid = @sid;",
            new { sid });

        int totalPlayers = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM players;");

        int higherCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM (
                SELECT p.steamid, COALESCE(SUM(r.level), 0) AS tl
                FROM players p
                LEFT JOIN races r ON r.steamid = p.steamid
                GROUP BY p.steamid
                HAVING tl > @totalLevel
            );
            """,
            new { totalLevel });

        return (higherCount + 1, Math.Max(1, totalPlayers), totalLevel);
    }

    private async Task ProcessWritesAsync()
    {
        try
        {
            await foreach (var work in _writeChannel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WCS2] SQLite background write failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_writeChannel.Writer.TryWrite(() =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            await Task.WhenAny(tcs.Task, Task.Delay(3000));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeChannel.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* ignore on shutdown */ }
        _cts.Cancel();
        _cts.Dispose();
    }
}
