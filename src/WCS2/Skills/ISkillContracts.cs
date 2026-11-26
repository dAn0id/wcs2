using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;

namespace WCS2.Skills;

public enum SkillTrigger
{
    PlayerSpawn,
    PlayerAttacker,
    PlayerVictim,
    PlayerDeath,
    PlayerKill,
    PlayerUltimate,
    PlayerAbility,
    PlayerRoundStart,
    PlayerRoundEnd,
    PlayerBuy
}

public interface IPlayerSession
{
    ulong SteamId { get; }
    int Slot { get; }
    string PlayerName { get; }
    bool IsBot { get; }
    string CurrentRaceKey { get; }
    string? PendingRaceKey { get; }
    int RaceLevel { get; }
    int RaceXp { get; }
    int TotalLevel { get; }
    int UnusedPoints { get; }
    int BankLevel { get; }
    int RestedXp { get; }
    IReadOnlyDictionary<string, int> SkillLevels { get; }
    IReadOnlyDictionary<string, int> ActiveItems { get; }
    bool UltimateBlocked { get; set; }
    bool SkillsDisabled { get; set; }

    int GetSkillLevel(string skillKey);
    bool IsOnCooldown(string skillKey, out float remainingSeconds);
    void SetCooldown(string skillKey, float seconds);
    void ResetCooldown(string skillKey);
    void ResetAllCooldowns();
}

public sealed class SkillContext
{
    public required ISkillServices Services { get; init; }
    public required CCSPlayerController Owner { get; init; }
    public required IPlayerSession OwnerSession { get; init; }
    public required RaceDefinition Race { get; init; }
    public SkillDefinition? Skill { get; set; }
    public int SkillLevel { get; set; }
    public SkillTrigger Trigger { get; init; }

    /// <summary>
    /// For combat/death/kill triggers: the attacking player (may be null for world/fall damage).
    /// </summary>
    public CCSPlayerController? Attacker { get; init; }

    /// <summary>
    /// For combat/death/kill triggers: the victim player.
    /// </summary>
    public CCSPlayerController? Victim { get; init; }

    /// <summary>
    /// For Pre-Damage hooks: raw CTakeDamageInfo.
    /// </summary>
    public CTakeDamageInfo? DamageInfo { get; init; }

    /// <summary>
    /// Incoming or post-mitigation damage amount.
    /// </summary>
    public float DamageDealt { get; set; }

    public string WeaponName { get; init; } = string.Empty;
    public bool IsHeadshot { get; init; }
    public bool IsSkillDamage { get; init; }

    // Mutable outputs honored by CombatService on OnPlayerTakeDamagePre:
    public bool CancelDamage { get; set; }
    public float DamageMultiplier { get; set; } = 1.0f;
    public float BonusDamage { get; set; } = 0f;

    /// <summary>
    /// Set by custom handlers or primitives to indicate whether an ultimate/ability actually fired
    /// (controls whether cooldown is applied). Defaults to true.
    /// </summary>
    public bool Activated { get; set; } = true;

    public CCSPlayerController? ResolveSingleTarget(string targetSelector)
    {
        return targetSelector.ToLowerInvariant() switch
        {
            "self" or "owner" => Owner,
            "victim" => Victim,
            "attacker" => Attacker,
            "enemy" => Owner == Attacker ? Victim : Attacker,
            _ => Owner
        };
    }
}

public interface ISkillPrimitive
{
    string Type { get; }
    void Execute(SkillContext ctx, SkillActionConfig action);
}

public interface ISkillHandler
{
    string Key { get; }
    void Execute(SkillContext ctx);
    void OnCleanup(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false) { }
    void OnRoundStart() { }
    void OnRoundEnd() { }
}

public interface ISkillExecutionEngine
{
    void TriggerEvent(SkillTrigger trigger, SkillContext ctx);
    void TriggerRaceLifecycleActions(SkillTrigger trigger, CCSPlayerController player, IPlayerSession session, RaceDefinition race);
    void ExecuteItemActions(CCSPlayerController player, IPlayerSession session, ItemDefinition item);
    bool TryActivateUltimate(CCSPlayerController player, IPlayerSession session);
    bool TryActivateAbility(CCSPlayerController player, IPlayerSession session);
    void CleanupPlayer(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false);
    void OnRoundStart();
    void OnRoundEnd();
    IReadOnlyCollection<string> RegisteredPrimitives { get; }
    IReadOnlyCollection<string> RegisteredHandlers { get; }
}

public interface ISkillServices
{
    WcsConfig Config { get; }
    IPlayerStateManager State { get; }
    ICombatService Combat { get; }
    IWeaponService Weapons { get; }
    IEffectsService Effects { get; }
    ITeleportService Teleport { get; }
    ISessionProvider Sessions { get; }
    Random Rng { get; }
    CounterStrikeSharp.API.Modules.Timers.Timer AddTimer(float delaySeconds, Action callback, TimerFlags? flags = null);
    void RunNextFrame(Action action);
}

public interface IPlayerStateManager
{
    void ResetTransientState(CCSPlayerController player, bool isDisconnect = false);
    void SetHealth(CCSPlayerController player, int value, bool add = false, bool updateMaxHealth = true);
    void SetArmor(CCSPlayerController player, int value, bool add = false, bool giveHelmet = false);
    void SetSpeed(CCSPlayerController player, float value, bool additive = false, float duration = 0f, string? source = null);
    void SetGravity(CCSPlayerController player, float scale, float duration = 0f, string? source = null);
    void SetLongjump(CCSPlayerController player, float multiplier, bool additive = false);
    void SetInvisibility(CCSPlayerController player, int alpha, int r = 255, int g = 255, int b = 255, float duration = 0f);
    void SetColor(CCSPlayerController player, int r, int g, int b, int a = 255, float duration = 0f);
    void ReapplyRenderState(CCSPlayerController player);
    void SetFreeze(CCSPlayerController player, bool frozen, float duration = 0f);
    void SetGodMode(CCSPlayerController player, bool enabled, float duration = 0f);
    bool IsGodMode(CCSPlayerController player);
    void SetJetpack(CCSPlayerController player, bool enabled, float thrust = 320f, float duration = 0f);
    void SetFallDamageImmune(CCSPlayerController player, bool immune);
    bool IsFallDamageImmune(CCSPlayerController player);
    void SetHeadshotImmune(CCSPlayerController player, bool immune);
    bool IsHeadshotImmune(CCSPlayerController player);
    void SetCash(CCSPlayerController player, int amount, bool add = true);
    void SetNoBlock(CCSPlayerController player, bool enabled);
    void SetAttackSpeed(CCSPlayerController player, float multiplier, float duration = 0f);
    void SetNoRecoil(CCSPlayerController player, bool enabled, float duration = 0f);
    void SetFov(CCSPlayerController player, uint fov, float duration = 0f);
    void SetScale(CCSPlayerController player, float scale, float durationSeconds = 0f);
    void SetInfiniteAmmo(CCSPlayerController player, float chancePercent, float durationSeconds = 0f);
    float GetInfiniteAmmoChance(int slot);
    int GetRoundProcCount(int slot, string key);
    int IncrementRoundProcCount(int slot, string key);
    void SetPropDisguise(CCSPlayerController player, string modelPath);
    void ClearPropDisguise(CCSPlayerController player);
    void TrackSpawnedEntity(CCSPlayerController player, CBaseEntity entity);
    void SchedulePlayerTimer(CCSPlayerController player, float delaySeconds, Action callback, bool repeat = false, string? tag = null);
    void CancelPlayerTimers(CCSPlayerController player, string? tag = null);
    void SetCustomData<T>(CCSPlayerController player, string key, T value);
    T? GetCustomData<T>(CCSPlayerController player, string key, T? defaultValue = default);
    bool IsPlayerHiddenFromEnemies(int slot);
    bool IsEntityHiddenFromPlayer(uint entityIndex, int viewerSlot);
}

public interface ICombatService
{
    void DealDamage(CCSPlayerController victim, CCSPlayerController? attacker, float damage, string weaponHint = "world", bool ignoreArmor = false, bool isSkillDamage = true);
    int DealAreaDamage(Vector origin, float radius, float damage, CCSPlayerController attacker, bool hitAllies = false, string weaponHint = "hegrenade", Action<CCSPlayerController>? onHit = null);
    void ApplyBurn(CCSPlayerController victim, CCSPlayerController attacker, float duration, float damagePerTick = 5f, float interval = 0.5f);
    void ApplyRegen(CCSPlayerController player, int healPerTick, float interval, int maxHealthCap = 0, float duration = 0f, string? tag = null);
    void HealPlayer(CCSPlayerController player, int amount, int maxHealthCap = 0);
    void PushPlayer(CCSPlayerController target, Vector velocity, bool additive = false);
    void PushDirection(CCSPlayerController target, Vector fromOrigin, float force, float upwardBoost = 100f, bool pull = false);
    List<CCSPlayerController> GetPlayersInRadius(Vector origin, float radius, CsTeam? teamFilter = null, CCSPlayerController? exclude = null, bool aliveOnly = true);
}

public interface IWeaponService
{
    string NormalizeWeaponName(string weaponName);
    void GiveWeapon(CCSPlayerController player, string weaponName);
    void StripWeapons(CCSPlayerController player, bool keepKnife = true, bool keepC4 = true);
    void DropWeapon(CCSPlayerController player, bool primaryOnly = false);
    void RestrictAllExcept(CCSPlayerController player, IEnumerable<string> allowedWeapons);
    void RestrictWeapons(CCSPlayerController player, IEnumerable<string> restrictedWeapons);
    void UnrestrictWeapon(CCSPlayerController player, string weaponName);
    void ClearRestrictions(CCSPlayerController player);
    bool IsWeaponAllowed(CCSPlayerController player, string weaponName);
    void SetClipAndAmmo(CCSPlayerController player, string slotOrWeapon, int? clip = null, int? reserve = null);
    List<string> SnapshotWeapons(CCSPlayerController player);
    void RestoreWeapons(CCSPlayerController player, IEnumerable<string> weapons);
}

public interface IEffectsService
{
    void DrawBeamPoints(Vector start, Vector end, Color color, float width = 2f, float duration = 1f);
    void DrawBeamRing(Vector center, float radius, Color color, float width = 2f, float duration = 1f, int segments = 16, float zOffset = 10f);
    void SpawnParticle(string effectName, Vector origin, float duration = 2f);
    void SpawnParticle(Vector origin, string particlePath, float durationSeconds = 3f);
    void SendFade(CCSPlayerController player, Color color, int durationMs, int holdTimeMs, int flags = 0x0001);
    void SendShake(CCSPlayerController player, float amplitude, float frequency, float duration);
    void PlaySound(CCSPlayerController player, string soundEvent);
    void EmitSound(CCSPlayerController player, string soundEvent, float volume = 1.0f);
    void PlaySoundAll(string soundEvent);
    CBreakableProp? SpawnPhysicsProp(Vector origin, QAngle angles, string modelPath, bool explosive = false, float explosionDamage = 90f, float explosionRadius = 220f, CCSPlayerController? owner = null, float lifetime = 30f);
    void PrintChat(CCSPlayerController player, string message);
    void PrintCenter(CCSPlayerController player, string message);
}

public interface ITeleportService
{
    bool TeleportToCrosshair(CCSPlayerController player, float maxDistance = 1200f);
    bool TeleportSafe(CCSPlayerController player, Vector destination, QAngle? angles = null, Vector? velocity = null);
    Vector? GetCrosshairPoint(CCSPlayerController player, float maxDistance = 1500f);
    bool RespawnPlayer(CCSPlayerController player, Vector? atOrigin = null, IEnumerable<string>? restoreWeapons = null);
    Vector? GetRandomTeamSpawn(CsTeam team);
}

public interface ISessionProvider
{
    IPlayerSession? GetSession(CCSPlayerController player);
    RaceDefinition? GetRace(string raceKey);
    IReadOnlyList<CCSPlayerController> GetAlivePlayers(CsTeam? team = null);
    void GiveLevels(CCSPlayerController player, int levels);
    void GiveXp(CCSPlayerController player, int xp, string? reason = null);
    bool IsFriendlyFireAllowed();
}
