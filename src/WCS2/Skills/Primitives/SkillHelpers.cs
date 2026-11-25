using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;

namespace WCS2.Skills.Primitives;

internal static class SkillStateKeys
{
    public const string EvasionChance = "evasion_chance";
    public const string EvasionCount = "evasion_count";
    public const string LastRoundEvasionCount = "last_round_evasion_count";
    public const string VoodooActive = "voodoo_active";
    public const string VoodooEvaded = "voodoo_evaded";
    public const string DamageAbsorb = "damage_absorb";
    public const string SpikedCarapaceMin = "spiked_carapace_min";
    public const string SpikedCarapaceMax = "spiked_carapace_max";
    public const string UltimateImmunity = "ulti_immunity";
    public const string IsFrozen = "is_frozen";
    public const string IsSlowed = "is_slowed";
    public const string IsBurning = "is_burning";
    public const string PendingRespawn = "pending_respawn";
    public const string ArchmageFlying = "archmage_flying";
    public const string ArchmageBonusHp = "archmage_bonus_hp";
    public const string PhoenixUsed = "phoenix_used";
    public const string PhoenixFfa = "phoenix_ffa";
    public const string WardCharges = "ward_charges";
}

internal sealed class RoundTracker
{
    public int CurrentRoundId { get; private set; } = 1;
    public bool IsRoundActive { get; private set; } = true;

    public void MarkRoundStart()
    {
        CurrentRoundId++;
        IsRoundActive = true;
    }

    public void MarkRoundEnd()
    {
        CurrentRoundId++;
        IsRoundActive = false;
    }
}

internal sealed class EvasionHistory
{
    private readonly Dictionary<ulong, int> _lastRoundEvaded = new();

    private static ulong GetKey(CCSPlayerController player, IPlayerSession? session = null)
    {
        if (session is { SteamId: > 0 }) return session.SteamId;
        if (player is { IsValid: true, SteamID: > 0 }) return player.SteamID;
        if (session != null) return (ulong)(session.Slot + 1);
        return player is { IsValid: true } ? (ulong)(player.Slot + 1) : 0;
    }

    public void Record(CCSPlayerController player, IPlayerSession? session, int count)
    {
        ulong key = GetKey(player, session);
        if (key != 0) _lastRoundEvaded[key] = count;
    }

    public int ConsumeOrRead(CCSPlayerController player, IPlayerSession? session)
    {
        ulong key = GetKey(player, session);
        return key != 0 && _lastRoundEvaded.TryGetValue(key, out int count) ? count : 0;
    }

    public void Clear(CCSPlayerController player, IPlayerSession? session)
    {
        ulong key = GetKey(player, session);
        if (key != 0) _lastRoundEvaded.Remove(key);
    }
}

internal static class SkillHelpers
{
    public static bool IsValidPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true }
            && player.Connected == PlayerConnectedState.Connected;
    }

    public static bool IsAlive(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player) || !player!.PawnIsAlive)
            return false;

        var pawn = player.PlayerPawn?.Value;
        return pawn is { IsValid: true }
            && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
    }

    public static CCSPlayerPawn? GetAlivePawn(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player) || !player!.PawnIsAlive)
            return null;

        var pawn = player.PlayerPawn?.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            return null;

        return pawn;
    }

    public static CCSPlayerPawn? GetAnyPawn(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player))
            return null;

        var pawn = player!.PlayerPawn?.Value;
        return pawn is { IsValid: true } ? pawn : null;
    }

    public static Vector? CloneOrigin(CCSPlayerController? player)
    {
        var pawn = GetAnyPawn(player);
        var origin = pawn?.AbsOrigin;
        if (origin == null)
            return null;

        return new Vector(origin.X, origin.Y, origin.Z);
    }

    public static Vector? CloneEyePosition(CCSPlayerController? player, float zOffset = 64f)
    {
        var origin = CloneOrigin(player);
        if (origin == null)
            return null;

        return new Vector(origin.X, origin.Y, origin.Z + zOffset);
    }

    public static int GetCash(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player))
            return 0;

        return player!.InGameMoneyServices?.Account ?? 0;
    }

    public static CsTeam? GetEnemyTeam(CsTeam team) => team switch
    {
        CsTeam.Terrorist => CsTeam.CounterTerrorist,
        CsTeam.CounterTerrorist => CsTeam.Terrorist,
        _ => null
    };

    public static bool IsPlayableTeam(CsTeam team)
        => team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    public static bool IsEnemy(CCSPlayerController a, CCSPlayerController b, bool ffa)
    {
        if (!IsValidPlayer(a) || !IsValidPlayer(b))
            return false;
        if (a.Slot == b.Slot)
            return false;
        if (!IsPlayableTeam(a.Team) || !IsPlayableTeam(b.Team))
            return false;

        return ffa || a.Team != b.Team;
    }

    public static bool RollChance(Random rng, float chance)
    {
        if (chance >= 100f) return true;
        if (chance <= 0f) return false;
        return rng.NextDouble() * 100.0 <= chance;
    }

    public static int RollInclusive(Random rng, int min, int max)
    {
        if (max <= min) return min;
        return rng.Next(min, max + 1);
    }

    public static bool IsUltimateImmune(ISkillServices services, CCSPlayerController target)
    {
        return IsValidPlayer(target)
            && services.State.GetCustomData<bool>(target, SkillStateKeys.UltimateImmunity);
    }

    public static bool MatchesWeaponFilter(IWeaponService weapons, IReadOnlyList<string> allowedWeapons, string actualWeapon)
    {
        if (allowedWeapons.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(actualWeapon))
            return false;

        string normalizedActual = weapons.NormalizeWeaponName(actualWeapon).ToLowerInvariant();
        bool actualIsKnife = IsKnifeWeapon(normalizedActual);

        foreach (var filter in allowedWeapons)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            string normalizedFilter = weapons.NormalizeWeaponName(filter).ToLowerInvariant();
            if (actualIsKnife && IsKnifeWeapon(normalizedFilter))
                return true;

            if (string.Equals(normalizedActual, normalizedFilter, StringComparison.OrdinalIgnoreCase))
                return true;

            // Also match stripped "weapon_" prefix
            string shortActual = normalizedActual.StartsWith("weapon_") ? normalizedActual[7..] : normalizedActual;
            string shortFilter = normalizedFilter.StartsWith("weapon_") ? normalizedFilter[7..] : normalizedFilter;
            if (string.Equals(shortActual, shortFilter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsKnifeWeapon(string weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName))
            return false;

        string w = weaponName.ToLowerInvariant();
        return w.Contains("knife") || w.Contains("bayonet") || w == "weapon_melee" || w == "melee";
    }

    public static Color ReadColor(SkillActionConfig action, string arrayKey, Color fallback)
    {
        if (action.Params.TryGetValue(arrayKey, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            var vals = new List<int>(4);
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    vals.Add(Math.Clamp(item.GetInt32(), 0, 255));
            }
            if (vals.Count >= 3)
            {
                int a = vals.Count >= 4 ? vals[3] : 255;
                return Color.FromArgb(a, vals[0], vals[1], vals[2]);
            }
        }

        bool hasR = action.Params.ContainsKey("r") || action.LevelValues.ContainsKey("r");
        bool hasG = action.Params.ContainsKey("g") || action.LevelValues.ContainsKey("g");
        bool hasB = action.Params.ContainsKey("b") || action.LevelValues.ContainsKey("b");
        if (hasR || hasG || hasB)
        {
            int r = Math.Clamp(action.GetLevelInt("r", 1, fallback.R), 0, 255);
            int g = Math.Clamp(action.GetLevelInt("g", 1, fallback.G), 0, 255);
            int b = Math.Clamp(action.GetLevelInt("b", 1, fallback.B), 0, 255);
            int a = Math.Clamp(action.GetLevelInt("a", 1, fallback.A), 0, 255);
            return Color.FromArgb(a, r, g, b);
        }

        return fallback;
    }

    public static string FormatMessage(
        string template,
        SkillContext ctx,
        string? value = null,
        string? extra = null,
        string? pct = null,
        string? amount = null,
        string? damage = null,
        string? heal = null,
        string? duration = null,
        string? chance = null,
        string? count = null)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        string ownerName = IsValidPlayer(ctx.Owner) ? ctx.Owner.PlayerName : string.Empty;
        string attackerName = IsValidPlayer(ctx.Attacker) ? ctx.Attacker!.PlayerName : ownerName;
        string victimName = IsValidPlayer(ctx.Victim) ? ctx.Victim!.PlayerName : string.Empty;
        string primary = value ?? amount ?? damage ?? heal ?? pct ?? duration ?? chance ?? count ?? string.Empty;

        return template
            .Replace("{value}", value ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{extra}", extra ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{pct}", pct ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}", amount ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{damage}", damage ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{heal}", heal ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{duration}", duration ?? extra ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{chance}", chance ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{count}", count ?? primary, StringComparison.OrdinalIgnoreCase)
            .Replace("{owner}", ownerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{attacker}", attackerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{victim}", victimName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasKey(SkillActionConfig action, string key)
        => action.LevelValues.ContainsKey(key) || action.Params.ContainsKey(key);
}
