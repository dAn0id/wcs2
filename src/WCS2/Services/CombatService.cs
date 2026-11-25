using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Core;
using WCS2.Skills;

namespace WCS2.Services;

public sealed class CombatService : ICombatService
{
    private readonly IPlayerStateManager _state;
    private readonly IEffectsService _effects;
    private readonly ISessionProvider _sessions;

    // Re-entrancy guard so bonus/reflect/AoE skill damage never infinitely recurses
    public bool IsApplyingSkillDamage { get; private set; }

    public CombatService(IPlayerStateManager state, IEffectsService effects, ISessionProvider sessions)
    {
        _state = state;
        _effects = effects;
        _sessions = sessions;
    }

    public void DealDamage(
        CCSPlayerController victim,
        CCSPlayerController? attacker,
        float damage,
        string weaponHint = "world",
        bool ignoreArmor = false,
        bool isSkillDamage = true)
    {
        if (damage <= 0f) return;
        if (!PlayerStateManager.TryGetAlivePawn(victim, out var victimPawn)) return;
        if (_state.IsGodMode(victim)) return;

        if (attacker != null && attacker.IsValid && attacker.Slot != victim.Slot
            && !_sessions.IsFriendlyFireAllowed() && attacker.Team == victim.Team)
        {
            return;
        }

        int dmgInt = Math.Max(1, (int)Math.Round(damage));

        // Optional simple armor absorption when not ignoring armor
        if (!ignoreArmor && victimPawn.ArmorValue > 0)
        {
            int armorAbsorb = Math.Min(victimPawn.ArmorValue, dmgInt / 4);
            victimPawn.ArmorValue -= armorAbsorb;
            Utilities.SetStateChanged(victimPawn, "CCSPlayerPawn", "m_ArmorValue");
            dmgInt = Math.Max(1, dmgInt - armorAbsorb);
        }

        int remainingHp = victimPawn.Health - dmgInt;
        if (remainingHp > 0)
        {
            victimPawn.Health = remainingHp;
            Utilities.SetStateChanged(victimPawn, "CBaseEntity", "m_iHealth");
            return;
        }

        // Lethal skill damage: kill victim and attribute properly
        IsApplyingSkillDamage = isSkillDamage;
        try
        {
            victimPawn.Health = 0;
            Utilities.SetStateChanged(victimPawn, "CBaseEntity", "m_iHealth");
            victim.CommitSuicide(false, true);

            if (attacker != null && attacker.IsValid && attacker.Slot != victim.Slot)
            {
                // Award kill credit in scoreboard & killfeed event
                if (attacker.ActionTrackingServices?.MatchStats != null)
                {
                    attacker.ActionTrackingServices.MatchStats.Kills++;
                }
                var deathEv = new EventPlayerDeath(true)
                {
                    Userid = victim,
                    Attacker = attacker,
                    Weapon = weaponHint,
                    Headshot = false
                };
                deathEv.FireEvent(false);
            }
        }
        finally
        {
            IsApplyingSkillDamage = false;
        }
    }

    public int DealAreaDamage(
        Vector origin,
        float radius,
        float damage,
        CCSPlayerController attacker,
        bool hitAllies = false,
        string weaponHint = "hegrenade",
        Action<CCSPlayerController>? onHit = null)
    {
        if (radius <= 0f || damage <= 0f || !attacker.IsValid) return 0;

        CsTeam? teamFilter = hitAllies || _sessions.IsFriendlyFireAllowed()
            ? null
            : (attacker.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist);

        var targets = GetPlayersInRadius(origin, radius, teamFilter, exclude: attacker, aliveOnly: true);
        int hits = 0;
        foreach (var t in targets)
        {
            DealDamage(t, attacker, damage, weaponHint, ignoreArmor: false, isSkillDamage: true);
            onHit?.Invoke(t);
            hits++;
        }
        return hits;
    }

    public void ApplyBurn(
        CCSPlayerController victim,
        CCSPlayerController attacker,
        float duration,
        float damagePerTick = 5f,
        float interval = 0.5f)
    {
        if (!PlayerStateManager.TryGetAlivePawn(victim, out _)) return;
        if (duration <= 0f || interval <= 0.1f) return;

        int victimSlot = victim.Slot;
        int attackerSlot = attacker.IsValid ? attacker.Slot : -1;
        int totalTicks = Math.Max(1, (int)Math.Ceiling(duration / interval));
        int tickCount = 0;
        string tag = $"burn_{attackerSlot}";

        _state.CancelPlayerTimers(victim, tag);
        _state.SchedulePlayerTimer(victim, interval, () =>
        {
            var v = Utilities.GetPlayerFromSlot(victimSlot);
            if (!PlayerStateManager.TryGetAlivePawn(v, out var vPawn))
            {
                if (v != null && v.IsValid) _state.CancelPlayerTimers(v, tag);
                return;
            }

            tickCount++;
            var att = attackerSlot >= 0 ? Utilities.GetPlayerFromSlot(attackerSlot) : null;
            DealDamage(v!, att, damagePerTick, "inferno", ignoreArmor: true, isSkillDamage: true);
            _effects.SendFade(v!, Color.FromArgb(65, 255, 90, 0), 150, 150);

            if (vPawn.AbsOrigin is { } pos)
                _effects.DrawBeamRing(pos, 26f, Color.OrangeRed, 2f, 0.35f, 10, 20f);

            if (tickCount >= totalTicks)
                _state.CancelPlayerTimers(v!, tag);
        }, repeat: true, tag: tag);
    }

    public void ApplyRegen(
        CCSPlayerController player,
        int healPerTick,
        float interval,
        int maxHealthCap = 0,
        float duration = 0f,
        string? tag = null)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out _)) return;
        if (healPerTick <= 0 || interval <= 0.1f) return;

        int slot = player.Slot;
        string timerTag = tag ?? "regen";
        float endTime = duration > 0f ? Server.CurrentTime + duration : float.MaxValue;

        _state.CancelPlayerTimers(player, timerTag);
        _state.SchedulePlayerTimer(player, interval, () =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (!PlayerStateManager.TryGetAlivePawn(p, out _))
            {
                if (p != null && p.IsValid) _state.CancelPlayerTimers(p, timerTag);
                return;
            }
            if (Server.CurrentTime >= endTime)
            {
                _state.CancelPlayerTimers(p!, timerTag);
                return;
            }
            HealPlayer(p!, healPerTick, maxHealthCap);
        }, repeat: true, tag: timerTag);
    }

    public void HealPlayer(CCSPlayerController player, int amount, int maxHealthCap = 0)
    {
        if (amount <= 0 || !PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return;
        int cap = maxHealthCap > 0 ? maxHealthCap : Math.Max(100, pawn.MaxHealth);
        if (pawn.Health >= cap) return;
        pawn.Health = Math.Min(cap, pawn.Health + amount);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    public void PushPlayer(CCSPlayerController target, Vector velocity, bool additive = false)
    {
        if (!PlayerStateManager.TryGetAlivePawn(target, out var pawn)) return;
        var finalVel = additive
            ? new Vector(pawn.AbsVelocity.X + velocity.X, pawn.AbsVelocity.Y + velocity.Y, pawn.AbsVelocity.Z + velocity.Z)
            : velocity;
        pawn.Teleport(null, null, finalVel);
    }

    public void PushDirection(CCSPlayerController target, Vector fromOrigin, float force, float upwardBoost = 100f, bool pull = false)
    {
        if (!PlayerStateManager.TryGetAlivePawn(target, out var pawn) || pawn.AbsOrigin == null) return;
        float dx = pawn.AbsOrigin.X - fromOrigin.X;
        float dy = pawn.AbsOrigin.Y - fromOrigin.Y;
        float dz = pawn.AbsOrigin.Z - fromOrigin.Z;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 0.001f) len = 1f;

        float sign = pull ? -1f : 1f;
        var vel = new Vector(
            (dx / len) * force * sign,
            (dy / len) * force * sign,
            MathF.Max(upwardBoost, (dz / len) * force * sign + upwardBoost));
        pawn.Teleport(null, null, vel);
    }

    public List<CCSPlayerController> GetPlayersInRadius(
        Vector origin,
        float radius,
        CsTeam? teamFilter = null,
        CCSPlayerController? exclude = null,
        bool aliveOnly = true)
    {
        var result = new List<CCSPlayerController>();
        float r2 = radius * radius;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.Connected != PlayerConnectedState.Connected) continue;
            if (exclude != null && p.Slot == exclude.Slot) continue;
            if (aliveOnly && !p.PawnIsAlive) continue;
            if (teamFilter.HasValue && p.Team != teamFilter.Value) continue;
            var pos = p.PlayerPawn?.Value?.AbsOrigin;
            if (pos == null) continue;

            float dx = pos.X - origin.X;
            float dy = pos.Y - origin.Y;
            float dz = pos.Z - origin.Z;
            if (dx * dx + dy * dy + dz * dz <= r2)
                result.Add(p);
        }
        return result;
    }
}
