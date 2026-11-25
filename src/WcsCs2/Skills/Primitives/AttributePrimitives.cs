using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Config;

namespace WcsCs2.Skills.Primitives;

public sealed class HealthPrimitive : ISkillPrimitive
{
    public string Type => "health";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        var pawn = SkillHelpers.GetAlivePawn(target);
        if (target == null || pawn == null) return;

        int amount = action.GetLevelInt("amount", ctx.SkillLevel, 0);
        if (amount == 0 && !SkillHelpers.HasKey(action, "amount")) return;

        string mode = action.GetString("mode", "add").ToLowerInvariant();
        int maxCap = action.GetLevelInt("max_cap", ctx.SkillLevel, 0);
        int minHp = action.GetLevelInt("min_hp", ctx.SkillLevel, 1);
        bool updateMax = action.GetBool("update_max_health", true);

        if (mode == "set")
        {
            int newHp = Math.Max(minHp, amount);
            if (maxCap > 0) newHp = Math.Min(newHp, maxCap);
            ctx.Services.State.SetHealth(target, newHp, add: false, updateMaxHealth: updateMax);
        }
        else
        {
            int desired = pawn.Health + amount;
            if (maxCap > 0 && amount > 0) desired = Math.Min(desired, maxCap);
            desired = Math.Max(minHp, desired);
            int delta = desired - pawn.Health;
            if (delta != 0)
            {
                ctx.Services.State.SetHealth(target, desired, add: false, updateMaxHealth: updateMax);
            }
        }

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string amtStr = Math.Abs(amount).ToString();
            ctx.Services.Effects.PrintChat(target, SkillHelpers.FormatMessage(msg, ctx, value: amtStr, amount: amtStr));
        }
    }
}

public sealed class SpeedPrimitive : ISkillPrimitive
{
    public string Type => "speed";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        string preventKey = action.GetString("prevent_stack_key");
        if (!string.IsNullOrEmpty(preventKey) && ctx.Services.State.GetCustomData<bool>(target!, preventKey))
        {
            ctx.Activated = false;
            return;
        }

        float value = action.GetLevelFloat("value", ctx.SkillLevel, 1.0f);
        string mode = action.GetString("mode", "set").ToLowerInvariant();
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0f);
        string? source = action.GetString("source");
        if (string.IsNullOrEmpty(source))
            source = ctx.Skill?.Key;

        bool additive = mode == "add";
        ctx.Services.State.SetSpeed(target!, value, additive: additive, duration: duration, source: source);

        if (!string.IsNullOrEmpty(preventKey) && duration > 0f)
        {
            ctx.Services.State.SetCustomData(target!, preventKey, true);
            ctx.Services.State.SchedulePlayerTimer(target!, duration, () =>
            {
                if (SkillHelpers.IsValidPlayer(target))
                    ctx.Services.State.SetCustomData(target!, preventKey, false);
            }, repeat: false, tag: $"guard_{preventKey}");
        }

        string pctVal = additive
            ? ((int)Math.Round(value * 100)).ToString()
            : ((int)Math.Round((value - 1.0f) * 100)).ToString();
        string rawVal = value.ToString("0.##");
        string durStr = duration.ToString("0.#");

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg) && SkillHelpers.IsValidPlayer(ctx.Owner))
        {
            if (target!.Slot == ctx.Owner.Slot)
                ctx.Services.Effects.PrintChat(ctx.Owner, SkillHelpers.FormatMessage(msg, ctx, value: rawVal, pct: pctVal, duration: durStr));
            else
                ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(msg, ctx, value: rawVal, pct: pctVal, duration: durStr));
        }

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg) && target!.Slot != ctx.Owner.Slot)
        {
            ctx.Services.Effects.PrintCenter(target, SkillHelpers.FormatMessage(victimMsg, ctx, value: rawVal, pct: pctVal, duration: durStr));
        }
    }
}

public sealed class GravityPrimitive : ISkillPrimitive
{
    public string Type => "gravity";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float raw = action.GetLevelFloat("value", ctx.SkillLevel, 1.0f);
        string mode = action.GetString("mode", "set").ToLowerInvariant();
        float scale = mode == "subtract" ? 1.0f - raw : raw;
        scale = Math.Clamp(scale, 0.1f, 2.0f);

        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0f);
        string? source = action.GetString("source");
        if (string.IsNullOrEmpty(source))
            source = ctx.Skill?.Key;

        ctx.Services.State.SetGravity(target!, scale, duration: duration, source: source);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string pctStr = ((int)Math.Round(scale * 100)).ToString();
            string valStr = scale.ToString("0.##");
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, value: valStr, pct: pctStr));
        }
    }
}

public sealed class LongjumpPrimitive : ISkillPrimitive
{
    public string Type => "longjump";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float mult = action.GetLevelFloat("multiplier", ctx.SkillLevel, 1.0f);
        string mode = action.GetString("mode", "add").ToLowerInvariant();

        // Clamp sensibly for CS2 physics so extreme legacy values remain playable
        float clamped = Math.Clamp(mult, 0.1f, 2.5f);
        ctx.Services.State.SetLongjump(target!, clamped, additive: mode == "add");

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            int pct = (int)Math.Round(mult * 100);
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, pct.ToString()));
        }
    }
}

public sealed class InvisibilityPrimitive : ISkillPrimitive
{
    public string Type => "invisibility";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        int alpha = Math.Clamp(action.GetLevelInt("alpha", ctx.SkillLevel, 255), 0, 255);
        int r = Math.Clamp(action.GetLevelInt("r", ctx.SkillLevel, 255), 0, 255);
        int g = Math.Clamp(action.GetLevelInt("g", ctx.SkillLevel, 255), 0, 255);
        int b = Math.Clamp(action.GetLevelInt("b", ctx.SkillLevel, 255), 0, 255);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0f);

        ctx.Services.State.SetInvisibility(target!, alpha, r, g, b, duration);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            int invisPct = (int)Math.Round((255 - alpha) / 2.55);
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, invisPct.ToString()));
        }
    }
}

public sealed class EvasionPrimitive : ISkillPrimitive
{
    private readonly EvasionHistory _evasionHistory;

    internal EvasionPrimitive(EvasionHistory evasionHistory)
    {
        _evasionHistory = evasionHistory;
    }

    public string Type => "evasion";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float chance = Math.Clamp(action.GetLevelFloat("chance", ctx.SkillLevel, 0f), 0f, 100f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0f);
        float warnBeforeEnd = action.GetLevelFloat("warn_before_end", ctx.SkillLevel, 0f);

        if (duration <= 0f)
        {
            // Passive spawn evasion (e.g. Night Elves Evasion)
            int lastCount = _evasionHistory.ConsumeOrRead(target!, ctx.OwnerSession);
            ctx.Services.State.SetCustomData(target!, SkillStateKeys.EvasionChance, chance);
            ctx.Services.State.SetCustomData(target!, SkillStateKeys.EvasionCount, 0);

            if (lastCount > 0)
            {
                ctx.Services.Effects.PrintChat(target!, $"Evasion - Last round you evaded {lastCount} attack(s).");
            }

            string msg = action.GetString("message");
            if (!string.IsNullOrEmpty(msg))
            {
                string chanceStr = ((int)Math.Round(chance)).ToString();
                ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, value: chanceStr, chance: chanceStr, extra: lastCount.ToString(), count: lastCount.ToString()));
            }
            return;
        }

        // Timed active evasion (e.g. Shadow Hunter Big Bad Voodoo: 75% for duration)
        float previousChance = ctx.Services.State.GetCustomData<float>(target!, SkillStateKeys.EvasionChance);
        ctx.Services.State.SetCustomData(target!, SkillStateKeys.EvasionChance, Math.Max(previousChance, chance));
        ctx.Services.State.SetCustomData(target!, SkillStateKeys.VoodooActive, true);
        ctx.Services.State.SetCustomData(target!, SkillStateKeys.VoodooEvaded, 0);

        string durStr = duration.ToString("0.#");
        string actMsg = action.GetString("message");
        if (!string.IsNullOrEmpty(actMsg))
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(actMsg, ctx, value: durStr, duration: durStr));

        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(target!, SkillHelpers.FormatMessage(centerMsg, ctx, value: durStr, duration: durStr));

        bool hasExplicitRing = ctx.Skill?.Actions.Any(a => string.Equals(a.Type, "beam_ring", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasExplicitRing)
        {
            var origin = SkillHelpers.CloneOrigin(target);
            if (origin != null)
            {
                ctx.Services.Effects.DrawBeamRing(origin, 150f, Color.FromArgb(255, 255, 255, 255), 3f, 0.75f, 16, 15f);
            }
        }

        string fadingMsg = action.GetString("fading_message");
        if (warnBeforeEnd > 0f && duration > warnBeforeEnd && !string.IsNullOrEmpty(fadingMsg))
        {
            ctx.Services.State.SchedulePlayerTimer(target!, duration - warnBeforeEnd, () =>
            {
                if (SkillHelpers.IsAlive(target) && ctx.Services.State.GetCustomData<bool>(target!, SkillStateKeys.VoodooActive))
                    ctx.Services.Effects.PrintChat(target!, fadingMsg);
            }, repeat: false, tag: "evasion_warn");
        }

        string endedMsg = action.GetString("ended_message");
        ctx.Services.State.SchedulePlayerTimer(target!, duration, () =>
        {
            if (!SkillHelpers.IsValidPlayer(target)) return;

            int evaded = ctx.Services.State.GetCustomData<int>(target!, SkillStateKeys.VoodooEvaded);
            ctx.Services.State.SetCustomData(target!, SkillStateKeys.VoodooActive, false);
            ctx.Services.State.SetCustomData(target!, SkillStateKeys.EvasionChance, previousChance);

            if (!string.IsNullOrEmpty(endedMsg))
                ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(endedMsg, ctx, value: evaded.ToString(), count: evaded.ToString()));
        }, repeat: false, tag: "evasion_end");
    }
}

public sealed class DamageAbsorbPrimitive : ISkillPrimitive
{
    public string Type => "damage_absorb";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float absorb = Math.Clamp(action.GetLevelFloat("absorb", ctx.SkillLevel, 0f), 0f, 0.95f);
        int mirrorMin = action.GetLevelInt("mirror_min", ctx.SkillLevel, 0);
        int mirrorMax = action.GetLevelInt("mirror_max", ctx.SkillLevel, 0);

        ctx.Services.State.SetCustomData(target!, SkillStateKeys.DamageAbsorb, absorb);
        ctx.Services.State.SetCustomData(target!, SkillStateKeys.SpikedCarapaceMin, mirrorMin);
        ctx.Services.State.SetCustomData(target!, SkillStateKeys.SpikedCarapaceMax, mirrorMax);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            int pct = (int)Math.Round(absorb * 100);
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, pct.ToString()));
        }
    }
}

public sealed class UltimateImmunityPrimitive : ISkillPrimitive
{
    public string Type => "ultimate_immunity";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        ctx.Services.State.SetCustomData(target!, SkillStateKeys.UltimateImmunity, true);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx));
    }
}

public sealed class RegenPrimitive : ISkillPrimitive
{
    private readonly RoundTracker _roundTracker;

    internal RegenPrimitive(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Type => "regen";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        var pawn = SkillHelpers.GetAlivePawn(target);
        if (target == null || pawn == null) return;

        int fixedAmount = action.GetLevelInt("amount", ctx.SkillLevel, 0);
        int minHeal = action.GetLevelInt("min_heal", ctx.SkillLevel, fixedAmount > 0 ? fixedAmount : 2);
        int maxHeal = action.GetLevelInt("max_heal", ctx.SkillLevel, fixedAmount > 0 ? fixedAmount : minHeal);
        if (maxHeal < minHeal) maxHeal = minHeal;

        float fixedInterval = action.GetLevelFloat("interval", ctx.SkillLevel, 0f);
        float minInterval = action.GetLevelFloat("min_interval", ctx.SkillLevel, fixedInterval > 0f ? fixedInterval : 1.0f);
        float maxInterval = action.GetLevelFloat("max_interval", ctx.SkillLevel, fixedInterval > 0f ? fixedInterval : minInterval);
        float interval = maxInterval > minInterval
            ? minInterval + (float)ctx.Services.Rng.NextDouble() * (maxInterval - minInterval)
            : minInterval;
        interval = Math.Max(0.5f, interval);

        int configuredCap = action.GetLevelInt("max_cap", ctx.SkillLevel, 0);
        // If max_cap == 0, capture current HP at activation time (matches Belt of Vitality +1.9s spawn capture!)
        int maxCap = configuredCap > 0 ? configuredCap : Math.Max(pawn.Health, pawn.MaxHealth);
        float radius = action.GetLevelFloat("radius", ctx.SkillLevel, 0f);
        string tag = action.GetString("tag");
        if (string.IsNullOrEmpty(tag))
            tag = $"regen_{ctx.Skill?.Key ?? "item"}";

        int capturedRound = _roundTracker.CurrentRoundId;
        var services = ctx.Services;

        // Cancel any previous regen timer with the same tag on this player
        services.State.CancelPlayerTimers(target, tag);

        // Self-only constant regen delegates cleanly to CombatService or uses player timer
        if (radius <= 0f && minHeal == maxHeal)
        {
            services.Combat.ApplyRegen(target, minHeal, interval, maxHealthCap: maxCap, duration: 0f, tag: tag);
            return;
        }

        // Team-aura or randomized-heal regen (e.g. Shadow Hunter Healing Wave)
        services.State.SchedulePlayerTimer(target, interval, () =>
        {
            if (capturedRound != _roundTracker.CurrentRoundId || !SkillHelpers.IsAlive(target))
            {
                services.State.CancelPlayerTimers(target, tag);
                return;
            }

            int healAmt = SkillHelpers.RollInclusive(services.Rng, minHeal, maxHeal);
            if (radius <= 0f)
            {
                services.Combat.HealPlayer(target, healAmt, maxCap);
                return;
            }

            var origin = SkillHelpers.CloneOrigin(target);
            if (origin == null) return;

            var allies = services.Combat.GetPlayersInRadius(origin, radius, teamFilter: target.Team, exclude: null, aliveOnly: true);
            bool healedAny = false;
            foreach (var ally in allies)
            {
                var allyPawn = SkillHelpers.GetAlivePawn(ally);
                if (allyPawn != null && allyPawn.Health < maxCap)
                {
                    services.Combat.HealPlayer(ally, healAmt, maxCap);
                    healedAny = true;
                }
            }

            if (healedAny)
            {
                services.Effects.DrawBeamRing(origin, Math.Min(radius, 180f), Color.FromArgb(200, 80, 255, 140), 2.5f, 0.6f, 16, 15f);
            }
        }, repeat: true, tag: tag);
    }
}

public sealed class SetUltimateCooldownPrimitive : ISkillPrimitive
{
    public string Type => "set_ultimate_cooldown";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        float seconds = action.GetLevelFloat("seconds", ctx.SkillLevel, 0f);
        if (seconds <= 0f) return;

        var ult = ctx.Race.Skills.FirstOrDefault(s => s.Trigger == SkillTrigger.PlayerUltimate);
        if (ult != null)
        {
            ctx.OwnerSession.SetCooldown(ult.Key, seconds);
        }
    }
}

public sealed class GiveLevelsPrimitive : ISkillPrimitive
{
    public string Type => "give_levels";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsValidPlayer(target)) return;

        int levels = action.GetLevelInt("levels", ctx.SkillLevel, 1);
        float bonusChance = action.GetLevelFloat("bonus_chance", ctx.SkillLevel, 0f);
        int bonusLevels = action.GetLevelInt("bonus_levels", ctx.SkillLevel, levels);

        int granted = bonusChance > 0f && SkillHelpers.RollChance(ctx.Services.Rng, bonusChance)
            ? bonusLevels
            : levels;

        if (granted > 0)
        {
            ctx.Services.Sessions.GiveLevels(target!, granted);
            string msg = action.GetString("message");
            if (!string.IsNullOrEmpty(msg))
                ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, granted.ToString()));
        }
    }
}
