using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Skills.Primitives;

namespace WcsCs2.Skills.CustomHandlers;

public sealed class SuccubusSkullsHandler : ISkillHandler, IHandlesOwnChance
{
    private const int MaxSkulls = 5;
    private static readonly float[] DefaultHeadHunterChances = { 2f, 4f, 5f, 6f, 7f, 8f, 9f, 10f };
    private static readonly float[] DefaultIncantationChances = { 15f, 30f, 45f, 60f, 70f, 80f, 90f, 100f };

    // Persists across rounds in a match; cleared on disconnect or race change away from succubus_hunter
    private readonly Dictionary<ulong, int> _skullsByPlayer = new();

    public string Key => "succubus_skulls";

    public void Execute(SkillContext ctx)
    {
        var owner = ctx.Owner;
        if (!SkillHelpers.IsValidPlayer(owner))
        {
            ctx.Activated = false;
            return;
        }

        // Race-level lifecycle hooks (ctx.Skill == null)
        if (ctx.Skill == null)
        {
            if (ctx.Trigger == SkillTrigger.PlayerSpawn)
                HandleRaceSpawn(ctx);
            else if (ctx.Trigger == SkillTrigger.PlayerDeath)
                HandleRaceDeath(ctx);
            return;
        }

        string skillKey = ctx.Skill.Key.ToLowerInvariant();
        if (skillKey.Contains("head_hunter") || ctx.Trigger == SkillTrigger.PlayerAttacker)
        {
            HandleHeadHunter(ctx);
        }
        else if (skillKey.Contains("incantation") || ctx.Trigger == SkillTrigger.PlayerSpawn)
        {
            HandleIncantation(ctx);
        }
        else if (skillKey.Contains("transformation") || ctx.Trigger == SkillTrigger.PlayerUltimate)
        {
            HandleTransformation(ctx);
        }
    }

    public void HandleRaceSpawn(SkillContext ctx)
    {
        if (!SkillHelpers.IsAlive(ctx.Owner)) return;
        int skulls = GetSkulls(ctx.Owner);
        ctx.Services.Effects.PrintChat(ctx.Owner, $"You have collected {skulls} / {MaxSkulls} skulls.");
    }

    public void HandleRaceDeath(SkillContext ctx)
    {
        if (!SkillHelpers.IsValidPlayer(ctx.Owner)) return;
        int skulls = GetSkulls(ctx.Owner);
        if (skulls <= 0) return;

        // 33% chance (rand(0, 2) == 0) to lose 1 skull on death
        if (ctx.Services.Rng.Next(0, 3) == 0)
        {
            skulls--;
            SetSkulls(ctx.Owner, skulls);
            ctx.Services.Effects.PrintChat(ctx.Owner, $"You lost a skull on death! ({skulls} / {MaxSkulls} remaining)");
        }
    }

    private void HandleHeadHunter(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var victim = ctx.Victim;
        if (!SkillHelpers.IsAlive(owner) || !SkillHelpers.IsAlive(victim))
            return;

        float chance = ctx.Skill is { Chances.Count: > 0 }
            ? ctx.Skill.GetChance(ctx.SkillLevel)
            : DefaultHeadHunterChances[Math.Clamp(Math.Max(1, ctx.SkillLevel) - 1, 0, DefaultHeadHunterChances.Length - 1)];

        if (!SkillHelpers.RollChance(ctx.Services.Rng, chance))
            return;

        int dmg = SkillHelpers.RollInclusive(ctx.Services.Rng, 9, 15);
        if (ctx.DamageInfo != null)
            ctx.BonusDamage += dmg;
        else
            ctx.Services.Combat.DealDamage(victim!, owner, dmg, "world", ignoreArmor: false, isSkillDamage: true);

        ctx.Services.Effects.PrintCenter(owner, $"Head Hunter dealt +{dmg} bonus damage!");

        var start = SkillHelpers.CloneOrigin(owner);
        var end = SkillHelpers.CloneOrigin(victim);
        if (start != null && end != null)
        {
            ctx.Services.Effects.DrawBeamPoints(
                new Vector(start.X, start.Y, start.Z + 20f),
                new Vector(end.X, end.Y, end.Z + 20f),
                Color.FromArgb(220, 255, 128, 50), 3.0f, 0.7f);
            ctx.Services.Effects.DrawBeamRing(end, 45f, Color.FromArgb(220, 255, 220, 180), 2.5f, 0.6f, 12, 20f);
        }

        int skulls = GetSkulls(owner);
        if (skulls < MaxSkulls)
        {
            skulls++;
            SetSkulls(owner, skulls);
            ctx.Services.Effects.PrintChat(owner, $"Collected a skull! ({skulls} / {MaxSkulls})");
        }
    }

    private void HandleIncantation(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null) return;

        int skulls = GetSkulls(owner);
        if (skulls <= 0) return;

        float chance = ctx.Skill is { Chances.Count: > 0 }
            ? ctx.Skill.GetChance(ctx.SkillLevel)
            : DefaultIncantationChances[Math.Clamp(Math.Max(1, ctx.SkillLevel) - 1, 0, DefaultIncantationChances.Length - 1)];

        if (!SkillHelpers.RollChance(ctx.Services.Rng, chance))
            return;

        int bonusHp = skulls * 10;
        int bonusCash = skulls * 150;

        int newHp = pawn.Health + bonusHp;
        ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);
        ctx.Services.State.SetCash(owner, bonusCash, add: true);

        ctx.Services.Effects.PrintChat(owner, $"Incantation granted +${bonusCash} and +{bonusHp} HP ({skulls} skulls)!");
        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            ctx.Services.Effects.DrawBeamRing(origin, 75f, Color.FromArgb(200, 255, 128, 30), 2.5f, 1.0f, 16, 20f);
            ctx.Services.Effects.DrawBeamRing(origin, 95f, Color.FromArgb(200, 255, 150, 50), 2.5f, 1.0f, 16, 32f);
        }
    }

    private void HandleTransformation(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        int skulls = GetSkulls(owner);
        if (skulls < 1)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "You have no skulls! Collect skulls with Head Hunter first.");
            return;
        }

        skulls--;
        SetSkulls(owner, skulls);
        ctx.Services.Effects.PrintChat(owner, $"Transformation consumed 1 skull ({skulls} / {MaxSkulls} remaining)!");

        var action = ctx.Skill?.Actions.FirstOrDefault();
        float gravity = action != null && SkillHelpers.HasKey(action, "gravity")
            ? action.GetLevelFloat("gravity", ctx.SkillLevel, 0.50f)
            : 0.50f;
        int hpGain = action != null && SkillHelpers.HasKey(action, "bonus_hp")
            ? action.GetLevelInt("bonus_hp", ctx.SkillLevel, 25)
            : 25;
        int hpCap = action != null && SkillHelpers.HasKey(action, "health_cap")
            ? action.GetLevelInt("health_cap", ctx.SkillLevel, 200)
            : 200;
        float speedGain = action != null && SkillHelpers.HasKey(action, "bonus_speed")
            ? action.GetLevelFloat("bonus_speed", ctx.SkillLevel, 0.25f)
            : 0.25f;
        float speedCap = action != null && SkillHelpers.HasKey(action, "speed_cap")
            ? action.GetLevelFloat("speed_cap", ctx.SkillLevel, 2.0f)
            : 2.0f;

        ctx.Services.State.SetGravity(owner, Math.Clamp(gravity, 0.1f, 1.5f), duration: 0f, source: "succubus_transform");

        int oldHp = pawn.Health;
        int newHp = Math.Min(hpCap, oldHp + hpGain);
        if (newHp > oldHp)
        {
            ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);
            ctx.Services.Effects.PrintChat(owner, newHp >= hpCap
                ? $"Transformation health cap reached ({hpCap} HP)!"
                : $"Transformation granted +{newHp - oldHp} HP!");
        }
        else
        {
            ctx.Services.Effects.PrintChat(owner, $"Transformation health cap reached ({hpCap} HP)!");
        }

        float currentSpeed = pawn.VelocityModifier > 0.1f ? pawn.VelocityModifier : 1.0f;
        float newSpeed = Math.Min(speedCap, currentSpeed + speedGain);
        ctx.Services.State.SetSpeed(owner, newSpeed, additive: false, duration: 0f, source: "succubus_transform");
        ctx.Services.Effects.PrintChat(owner, newSpeed >= speedCap
            ? $"Transformation speed cap reached ({speedCap:0.##}x)!"
            : $"Transformation boosted speed to {newSpeed:0.##}x!");

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            ctx.Services.Effects.DrawBeamRing(origin, 70f, Color.FromArgb(200, 255, 128, 30), 2.5f, 0.8f, 16, 15f);
            ctx.Services.Effects.DrawBeamRing(origin, 85f, Color.FromArgb(200, 255, 140, 40), 2.5f, 0.8f, 16, 28f);
            ctx.Services.Effects.DrawBeamRing(origin, 100f, Color.FromArgb(200, 255, 160, 60), 2.5f, 0.8f, 16, 41f);
        }

        ctx.Activated = true;
    }

    public void OnCleanup(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false)
    {
        // Only wipe skulls on disconnect or race change away from succubus_hunter!
        bool disconnected = isDisconnect || !SkillHelpers.IsValidPlayer(player);
        bool changedRace = !string.Equals(session.CurrentRaceKey, "succubus_hunter", StringComparison.OrdinalIgnoreCase);

        if (disconnected || changedRace)
        {
            ulong key = session.SteamId != 0 ? session.SteamId : (ulong)(session.Slot + 1);
            _skullsByPlayer.Remove(key);
        }
    }

    private int GetSkulls(CCSPlayerController player)
    {
        ulong key = GetKey(player);
        return _skullsByPlayer.TryGetValue(key, out int count) ? Math.Clamp(count, 0, MaxSkulls) : 0;
    }

    private void SetSkulls(CCSPlayerController player, int count)
    {
        _skullsByPlayer[GetKey(player)] = Math.Clamp(count, 0, MaxSkulls);
    }

    private static ulong GetKey(CCSPlayerController player)
        => player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1);
}
