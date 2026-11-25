using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using WcsCs2.Core;
using WcsCs2.Skills;

namespace WcsCs2.Services;

public sealed class WeaponService : IWeaponService
{
    private sealed class RestrictionRule
    {
        public bool RestrictAllExceptAllowed { get; set; }
        public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> LegacyWeaponAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scout"] = "weapon_ssg08",
        ["weapon_scout"] = "weapon_ssg08",
        ["sg552"] = "weapon_sg556",
        ["weapon_sg552"] = "weapon_sg556",
        ["m3"] = "weapon_nova",
        ["weapon_m3"] = "weapon_nova",
        ["tmp"] = "weapon_mp9",
        ["weapon_tmp"] = "weapon_mp9",
        ["galil"] = "weapon_galilar",
        ["weapon_galil"] = "weapon_galilar",
        ["mp5navy"] = "weapon_mp5sd",
        ["weapon_mp5navy"] = "weapon_mp5sd",
        ["p228"] = "weapon_p250",
        ["weapon_p228"] = "weapon_p250",
        ["usp"] = "weapon_usp_silencer",
        ["weapon_usp"] = "weapon_usp_silencer",
        ["m4a1_silencer"] = "weapon_m4a1_silencer",
        ["knife_t"] = "weapon_knife",
        ["weapon_knife_t"] = "weapon_knife",
        ["hegrenade_projectile"] = "weapon_hegrenade",
        ["weapon_hegrenade_projectile"] = "weapon_hegrenade",
        ["flashbang_projectile"] = "weapon_flashbang",
        ["weapon_flashbang_projectile"] = "weapon_flashbang",
        ["smokegrenade_projectile"] = "weapon_smokegrenade",
        ["weapon_smokegrenade_projectile"] = "weapon_smokegrenade",
        ["molotov_projectile"] = "weapon_molotov",
        ["weapon_molotov_projectile"] = "weapon_molotov",
        ["inferno"] = "weapon_molotov"
    };

    private static readonly HashSet<string> SecondaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_p250",
        "weapon_fiveseven", "weapon_tec9", "weapon_cz75a", "weapon_deagle",
        "weapon_revolver", "weapon_elite"
    };

    private static readonly HashSet<string> UtilityAndKnife = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_knife", "weapon_knife_t", "weapon_c4", "weapon_hegrenade",
        "weapon_flashbang", "weapon_smokegrenade", "weapon_molotov",
        "weapon_incgrenade", "weapon_decoy", "weapon_taser", "weapon_healthshot"
    };

    private readonly Dictionary<int, RestrictionRule> _rules = new();

    public string NormalizeWeaponName(string weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName)) return string.Empty;
        var clean = weaponName.Trim().ToLowerInvariant();
        if (LegacyWeaponAliases.TryGetValue(clean, out var mapped))
            return mapped;
        if (!clean.StartsWith("weapon_") && !clean.StartsWith("item_"))
            clean = "weapon_" + clean;
        return LegacyWeaponAliases.TryGetValue(clean, out var mapped2) ? mapped2 : clean;
    }

    public void GiveWeapon(CCSPlayerController player, string weaponName)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out _)) return;
        var normalized = NormalizeWeaponName(weaponName);
        if (string.IsNullOrEmpty(normalized)) return;
        player.GiveNamedItem(normalized);
    }

    public void StripWeapons(CCSPlayerController player, bool keepKnife = true, bool keepC4 = true)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return;
        if (pawn.WeaponServices?.MyWeapons == null) return;

        bool hadC4 = false;
        var toRemove = new List<CBasePlayerWeapon>();
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var w = handle?.Value;
            if (w == null || !w.IsValid) continue;
            var name = w.DesignerName ?? string.Empty;
            if (keepC4 && name.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase))
            {
                hadC4 = true;
                continue;
            }
            if (keepKnife && (name.Contains("knife", StringComparison.OrdinalIgnoreCase) || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase)))
                continue;
            toRemove.Add(w);
        }

        if (!keepKnife && !keepC4)
        {
            player.RemoveWeapons();
            return;
        }

        foreach (var w in toRemove)
        {
            if (w.IsValid)
            {
                w.Remove();
            }
        }

        if (keepKnife)
        {
            bool hasKnife = pawn.WeaponServices.MyWeapons.Any(h =>
                h?.Value is { IsValid: true } w &&
                (w.DesignerName.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                 w.DesignerName.Contains("bayonet", StringComparison.OrdinalIgnoreCase)));
            if (!hasKnife)
                player.GiveNamedItem("weapon_knife");
        }
        if (keepC4 && hadC4)
        {
            bool hasC4 = pawn.WeaponServices.MyWeapons.Any(h =>
                h?.Value is { IsValid: true } w && w.DesignerName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase));
            if (!hasC4)
                player.GiveNamedItem("weapon_c4");
        }
    }

    public void DropWeapon(CCSPlayerController player, bool primaryOnly = false)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return;
        if (!primaryOnly)
        {
            player.DropActiveWeapon();
            return;
        }

        if (pawn.WeaponServices?.MyWeapons == null) return;
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var w = handle?.Value;
            if (w == null || !w.IsValid) continue;
            var name = NormalizeWeaponName(w.DesignerName);
            if (!SecondaryWeapons.Contains(name) && !UtilityAndKnife.Contains(name))
            {
                w.Remove();
                break;
            }
        }
    }

    public void RestrictAllExcept(CCSPlayerController player, IEnumerable<string> allowedWeapons)
    {
        if (!player.IsValid) return;
        var rule = new RestrictionRule { RestrictAllExceptAllowed = true };
        foreach (var w in allowedWeapons)
        {
            var norm = NormalizeWeaponName(w);
            if (!string.IsNullOrEmpty(norm))
                rule.Allowed.Add(norm);
        }
        // Always allow C4 so bomb gameplay never breaks
        rule.Allowed.Add("weapon_c4");
        _rules[player.Slot] = rule;

        EnforceRestrictionsOnInventory(player, rule);
    }

    public void RestrictWeapons(CCSPlayerController player, IEnumerable<string> restrictedWeapons)
    {
        if (!player.IsValid) return;
        if (!_rules.TryGetValue(player.Slot, out var rule))
        {
            rule = new RestrictionRule();
            _rules[player.Slot] = rule;
        }
        foreach (var w in restrictedWeapons)
        {
            var norm = NormalizeWeaponName(w);
            if (!string.IsNullOrEmpty(norm))
                rule.Blocked.Add(norm);
        }
        EnforceRestrictionsOnInventory(player, rule);
    }

    public void UnrestrictWeapon(CCSPlayerController player, string weaponName)
    {
        if (!player.IsValid) return;
        if (!_rules.TryGetValue(player.Slot, out var rule)) return;
        var norm = NormalizeWeaponName(weaponName);
        if (string.IsNullOrEmpty(norm)) return;
        rule.Blocked.Remove(norm);
        if (rule.RestrictAllExceptAllowed)
            rule.Allowed.Add(norm);
    }

    public void ClearRestrictions(CCSPlayerController player)
    {
        if (player == null) return;
        _rules.Remove(player.Slot);
    }

    public bool IsWeaponAllowed(CCSPlayerController player, string weaponName)
    {
        if (player == null || !player.IsValid) return true;
        if (!_rules.TryGetValue(player.Slot, out var rule)) return true;

        var norm = NormalizeWeaponName(weaponName);
        if (string.IsNullOrEmpty(norm)) return true;
        if (norm.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.Contains("knife", StringComparison.OrdinalIgnoreCase) || norm.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            norm = "weapon_knife";

        if (rule.RestrictAllExceptAllowed)
            return rule.Allowed.Contains(norm);

        return !rule.Blocked.Contains(norm);
    }

    private void EnforceRestrictionsOnInventory(CCSPlayerController player, RestrictionRule rule)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return;
        if (pawn.WeaponServices?.MyWeapons == null) return;

        var disallowed = new List<CBasePlayerWeapon>();
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var w = handle?.Value;
            if (w == null || !w.IsValid) continue;
            if (!IsWeaponAllowed(player, w.DesignerName))
                disallowed.Add(w);
        }

        foreach (var w in disallowed)
        {
            if (w.IsValid)
                w.Remove();
        }
    }

    public void SetClipAndAmmo(CCSPlayerController player, string slotOrWeapon, int? clip = null, int? reserve = null)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return;
        if (pawn.WeaponServices?.MyWeapons == null) return;

        CBasePlayerWeapon? target = null;
        string key = slotOrWeapon.Trim().ToLowerInvariant();

        if (key == "active")
        {
            target = pawn.WeaponServices.ActiveWeapon?.Value;
        }
        else
        {
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var w = handle?.Value;
                if (w == null || !w.IsValid) continue;
                var norm = NormalizeWeaponName(w.DesignerName);

                if (key is "primary" or "1st")
                {
                    if (!SecondaryWeapons.Contains(norm) && !UtilityAndKnife.Contains(norm))
                    {
                        target = w;
                        break;
                    }
                }
                else if (key is "secondary" or "2nd")
                {
                    if (SecondaryWeapons.Contains(norm))
                    {
                        target = w;
                        break;
                    }
                }
                else if (norm.Equals(NormalizeWeaponName(key), StringComparison.OrdinalIgnoreCase))
                {
                    target = w;
                    break;
                }
            }
        }

        if (target == null || !target.IsValid) return;

        if (clip.HasValue)
        {
            target.Clip1 = Math.Max(0, clip.Value);
            Utilities.SetStateChanged(target, "CBasePlayerWeapon", "m_iClip1");
        }
        if (reserve.HasValue)
        {
            target.ReserveAmmo[0] = Math.Max(0, reserve.Value);
            Utilities.SetStateChanged(target, "CBasePlayerWeapon", "m_pReserveAmmo");
        }
    }

    public List<string> SnapshotWeapons(CCSPlayerController player)
    {
        var list = new List<string>();
        if (!player.IsValid || player.PlayerPawn?.Value?.WeaponServices?.MyWeapons == null) return list;

        foreach (var handle in player.PlayerPawn.Value.WeaponServices.MyWeapons)
        {
            var w = handle?.Value;
            if (w == null || !w.IsValid) continue;
            var name = NormalizeWeaponName(w.DesignerName);
            if (!string.IsNullOrEmpty(name) && name != "weapon_c4")
                list.Add(name);
        }
        return list;
    }

    public void RestoreWeapons(CCSPlayerController player, IEnumerable<string> weapons)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out _)) return;
        StripWeapons(player, keepKnife: true, keepC4: true);
        foreach (var w in weapons)
        {
            var norm = NormalizeWeaponName(w);
            if (string.IsNullOrEmpty(norm) || norm == "weapon_knife" || norm == "weapon_c4") continue;
            if (IsWeaponAllowed(player, norm))
                player.GiveNamedItem(norm);
        }
    }
}
