using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;

namespace WCS2.Skills.Primitives;

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

public sealed class PlayerScalePrimitive : ISkillPrimitive
{
    public string Type => "player_scale";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target))
        {
            ctx.Activated = false;
            return;
        }

        float scale = Math.Clamp(action.GetLevelFloat("scale", ctx.SkillLevel, 0.8f), 0.4f, 2.0f);
        float duration = MathF.Max(0f, action.GetLevelFloat("duration", ctx.SkillLevel, 0f));

        ctx.Services.State.SetScale(target!, scale, duration);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string valStr = scale.ToString("0.##");
            string pctStr = ((int)Math.Round(scale * 100f)).ToString();
            string durStr = duration.ToString("0.#");
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, value: valStr, pct: pctStr, duration: durStr));
        }
    }
}

public sealed class InfiniteAmmoPrimitive : ISkillPrimitive
{
    public string Type => "infinite_ammo";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target))
        {
            ctx.Activated = false;
            return;
        }

        float chance = Math.Clamp(action.GetLevelFloat("chance", ctx.SkillLevel, 50f), 0f, 100f);
        float duration = MathF.Max(0f, action.GetLevelFloat("duration", ctx.SkillLevel, 0f));

        ctx.Services.State.SetInfiniteAmmo(target!, chance, duration);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string chanceStr = ((int)Math.Round(chance)).ToString();
            string durStr = duration.ToString("0.#");
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, value: chanceStr, chance: chanceStr, duration: durStr));
        }
    }
}

public sealed class AuraPrimitive : ISkillPrimitive
{
    public const string DefaultParticle = "particles/explosions_fx/explosion_hegrenade_water_ripple.vpcf";

    private readonly RoundTracker _roundTracker;

    internal AuraPrimitive(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Type => "aura";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var caster = ctx.Owner;
        if (!SkillHelpers.IsAlive(caster))
        {
            ctx.Activated = false;
            return;
        }

        string effect = action.GetString("effect", "burn").Trim().ToLowerInvariant();
        if (effect is not ("burn" or "poison" or "freeze" or "heal" or "slow"))
            effect = "burn";

        string targetMode = string.IsNullOrWhiteSpace(action.Target)
            ? "enemy"
            : action.Target.Trim().ToLowerInvariant();

        float radius = Math.Clamp(action.GetLevelFloat("radius", ctx.SkillLevel, 220f), 32f, 1500f);
        float value = ResolveEffectValue(action, ctx.SkillLevel, effect);
        float chance = Math.Clamp(action.GetLevelFloat("chance", ctx.SkillLevel, 100f), 0f, 100f);
        float interval = MathF.Max(0.25f, action.GetLevelFloat("interval", ctx.SkillLevel, 1.0f));
        float duration = MathF.Max(0f, action.GetLevelFloat("duration", ctx.SkillLevel, 0f));
        bool overheal = action.GetBool("overheal", false);
        bool includeSelf = action.GetBool("include_self", true);

        string particle = action.GetString("particle", DefaultParticle);
        float particleInterval = action.GetLevelFloat("particle_interval", ctx.SkillLevel, 3.0f);
        float particleDuration = Math.Clamp(action.GetLevelFloat("particle_duration", ctx.SkillLevel, 2.5f), 0.2f, 15f);

        var services = ctx.Services;
        string skillKey = ctx.Skill?.Key ?? "item";
        string effectTag = $"aura_{skillKey}_{effect}";
        string fxTag = $"aura_fx_{skillKey}_{effect}";
        string slowSourceKey = $"aura_slow_{caster.Slot}_{skillKey}";
        string slowSlotsDataKey = $"aura_slow_slots_{skillKey}";
        int capturedRound = _roundTracker.CurrentRoundId;

        services.State.CancelPlayerTimers(caster, effectTag);
        services.State.CancelPlayerTimers(caster, fxTag);

        // If re-activating a slow aura before the previous instance ended, immediately restore any previously slowed players
        if (effect == "slow")
        {
            var priorSlots = services.State.GetCustomData<HashSet<int>>(caster, slowSlotsDataKey);
            if (priorSlots != null && priorSlots.Count > 0)
            {
                foreach (int oldSlot in priorSlots)
                {
                    var oldPlayer = Utilities.GetPlayerFromSlot(oldSlot);
                    if (oldPlayer != null && oldPlayer.IsValid)
                        services.State.ClearSpeedMultiplier(oldPlayer, slowSourceKey);
                }
                priorSlots.Clear();
            }
        }

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string valStr = value.ToString("0.##");
            string chanceStr = ((int)Math.Round(chance)).ToString();
            string durStr = duration.ToString("0.#");
            services.Effects.PrintChat(
                caster,
                SkillHelpers.FormatMessage(msg, ctx, value: valStr, amount: valStr, damage: valStr, chance: chanceStr, duration: durStr));
        }

        // Visual ground ripple timer (independent of gameplay pulse interval)
        if (!string.IsNullOrWhiteSpace(particle) && particleInterval > 0f)
        {
            var initialOrigin = SkillHelpers.CloneOrigin(caster);
            if (initialOrigin != null)
            {
                services.Effects.SpawnParticle(initialOrigin, particle, particleDuration);
            }

            float safeFxInterval = MathF.Max(0.25f, particleInterval);
            float fxElapsed = 0f;
            bool fxStopped = false;

            services.State.SchedulePlayerTimer(caster, safeFxInterval, () =>
            {
                if (fxStopped)
                    return;

                fxElapsed += safeFxInterval;
                if (capturedRound != _roundTracker.CurrentRoundId
                    || !_roundTracker.IsRoundActive
                    || !SkillHelpers.IsAlive(caster))
                {
                    fxStopped = true;
                    if (SkillHelpers.IsValidPlayer(caster))
                        services.State.CancelPlayerTimers(caster, fxTag);
                    return;
                }

                if (duration > 0f && fxElapsed > duration + 0.01f)
                {
                    fxStopped = true;
                    services.State.CancelPlayerTimers(caster, fxTag);
                    return;
                }

                var liveOrigin = SkillHelpers.CloneOrigin(caster);
                if (liveOrigin != null)
                {
                    services.Effects.SpawnParticle(liveOrigin, particle, particleDuration);
                }

                if (duration > 0f && fxElapsed >= duration)
                {
                    fxStopped = true;
                    services.State.CancelPlayerTimers(caster, fxTag);
                }
            }, repeat: true, tag: fxTag);
        }

        // Gameplay effect pulse timer
        float elapsed = 0f;
        bool stopped = false;
        var currentlySlowedSlots = new HashSet<int>();
        if (effect == "slow")
            services.State.SetCustomData(caster, slowSlotsDataKey, currentlySlowedSlots);

        void ClearAllSlowed()
        {
            if (currentlySlowedSlots.Count == 0)
                return;

            foreach (int slowedSlot in currentlySlowedSlots)
            {
                var p = Utilities.GetPlayerFromSlot(slowedSlot);
                if (p != null && p.IsValid)
                    services.State.ClearSpeedMultiplier(p, slowSourceKey);
            }
            currentlySlowedSlots.Clear();
        }

        services.State.SchedulePlayerTimer(caster, interval, () =>
        {
            if (stopped)
                return;

            elapsed += interval;
            if (capturedRound != _roundTracker.CurrentRoundId
                || !_roundTracker.IsRoundActive
                || !SkillHelpers.IsAlive(caster))
            {
                stopped = true;
                ClearAllSlowed();
                if (SkillHelpers.IsValidPlayer(caster))
                {
                    services.State.CancelPlayerTimers(caster, effectTag);
                    services.State.CancelPlayerTimers(caster, fxTag);
                }
                return;
            }

            if (duration > 0f && elapsed > duration + 0.01f)
            {
                stopped = true;
                ClearAllSlowed();
                services.State.CancelPlayerTimers(caster, effectTag);
                services.State.CancelPlayerTimers(caster, fxTag);
                return;
            }

            var origin = SkillHelpers.CloneOrigin(caster);
            if (origin == null)
                return;

            var targets = ResolveAuraTargets(services, caster, origin, radius, targetMode, includeSelf);

            if (effect == "slow")
            {
                float slowFactor = Math.Clamp(value, 0.2f, 0.98f);
                float slowDuration = interval + 0.35f;
                var activeThisPulse = new HashSet<int>();

                foreach (var t in targets)
                {
                    if (!SkillHelpers.IsAlive(t))
                        continue;
                    if (!SkillHelpers.RollChance(services.Rng, chance))
                        continue;

                    services.State.SetSpeedMultiplier(t, slowSourceKey, slowFactor, slowDuration);
                    services.Effects.SendFade(t, Color.FromArgb(40, 100, 170, 255), 300, 0);
                    activeThisPulse.Add(t.Slot);
                    currentlySlowedSlots.Add(t.Slot);
                }

                // Immediately restore exact personal speed for players who left the aura (or failed proc)
                if (currentlySlowedSlots.Count > 0)
                {
                    List<int>? exited = null;
                    foreach (int prevSlot in currentlySlowedSlots)
                    {
                        if (!activeThisPulse.Contains(prevSlot))
                        {
                            exited ??= new List<int>();
                            exited.Add(prevSlot);
                        }
                    }

                    if (exited != null)
                    {
                        foreach (int slot in exited)
                        {
                            var prevPlayer = Utilities.GetPlayerFromSlot(slot);
                            if (prevPlayer != null && prevPlayer.IsValid)
                                services.State.ClearSpeedMultiplier(prevPlayer, slowSourceKey);
                            currentlySlowedSlots.Remove(slot);
                        }
                    }
                }
            }
            else
            {
                foreach (var t in targets)
                {
                    if (!SkillHelpers.IsAlive(t))
                        continue;
                    if (!SkillHelpers.RollChance(services.Rng, chance))
                        continue;

                    ApplyAuraEffect(services, caster, t, effect, value, overheal);
                }
            }

            if (duration > 0f && elapsed >= duration)
            {
                stopped = true;
                ClearAllSlowed();
                services.State.CancelPlayerTimers(caster, effectTag);
                services.State.CancelPlayerTimers(caster, fxTag);
            }
        }, repeat: true, tag: effectTag);

        ctx.Activated = true;
    }

    private static float ResolveEffectValue(SkillActionConfig action, int skillLevel, string effect)
    {
        if (HasNumericKey(action, "value"))
            return MathF.Max(0f, action.GetLevelFloat("value", skillLevel, effect == "slow" ? 0.65f : 5f));

        if (effect is "burn" or "poison" && HasNumericKey(action, "damage"))
            return MathF.Max(0f, action.GetLevelFloat("damage", skillLevel, effect == "burn" ? 5f : 4f));

        if (effect == "heal" && HasNumericKey(action, "amount"))
            return MathF.Max(0f, action.GetLevelFloat("amount", skillLevel, 5f));

        if (effect == "freeze" && HasNumericKey(action, "freeze_duration"))
            return MathF.Max(0f, action.GetLevelFloat("freeze_duration", skillLevel, 1.0f));

        if (effect == "slow" && HasNumericKey(action, "slow_factor"))
            return MathF.Max(0f, action.GetLevelFloat("slow_factor", skillLevel, 0.65f));

        return effect switch
        {
            "burn" => 5f,
            "poison" => 4f,
            "freeze" => 1.0f,
            "heal" => 5f,
            "slow" => 0.65f,
            _ => 5f
        };
    }

    private static bool HasNumericKey(SkillActionConfig action, string key)
        => action.LevelValues.ContainsKey(key) || action.Params.ContainsKey(key);

    private static List<CCSPlayerController> ResolveAuraTargets(
        ISkillServices services,
        CCSPlayerController caster,
        Vector origin,
        float radius,
        string targetMode,
        bool includeSelf)
    {
        var results = new List<CCSPlayerController>();

        if (targetMode is "self" or "owner")
        {
            if (SkillHelpers.IsAlive(caster))
                results.Add(caster);
            return results;
        }

        if (targetMode is "team" or "nearby_teammates" or "nearby_allies")
        {
            if (!SkillHelpers.IsPlayableTeam(caster.Team))
                return results;

            var allies = services.Combat.GetPlayersInRadius(origin, radius, teamFilter: caster.Team, exclude: caster, aliveOnly: true);
            if (includeSelf && SkillHelpers.IsAlive(caster))
                results.Add(caster);

            foreach (var ally in allies)
            {
                if (SkillHelpers.IsAlive(ally) && ally.Slot != caster.Slot && ally.Team == caster.Team)
                    results.Add(ally);
            }
            return results;
        }

        // Default: "enemy" (or "nearby_enemies") — respects FFA, NEVER hits teammates otherwise
        bool ffa = services.Config.FfaEnabled || services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(caster.Team);
        if (!ffa && enemyTeam == null)
            return results;

        var enemies = services.Combat.GetPlayersInRadius(origin, radius, teamFilter: enemyTeam, exclude: caster, aliveOnly: true);
        foreach (var enemy in enemies)
        {
            if (!SkillHelpers.IsAlive(enemy) || enemy.Slot == caster.Slot)
                continue;
            if (!ffa && enemy.Team == caster.Team)
                continue;
            results.Add(enemy);
        }
        return results;
    }

    private static void ApplyAuraEffect(
        ISkillServices services,
        CCSPlayerController caster,
        CCSPlayerController target,
        string effect,
        float value,
        bool overheal)
    {
        switch (effect)
        {
            case "burn":
                if (value > 0f)
                {
                    services.Combat.DealDamage(target, caster, value, "inferno", ignoreArmor: false, isSkillDamage: true);
                    services.Effects.SendFade(target, Color.FromArgb(45, 255, 90, 0), 300, 0);
                }
                break;

            case "poison":
                if (value > 0f)
                {
                    services.Combat.DealDamage(target, caster, value, "poison", ignoreArmor: true, isSkillDamage: true);
                    services.Effects.SendFade(target, Color.FromArgb(45, 40, 200, 60), 300, 0);
                }
                break;

            case "freeze":
            {
                float freezeDur = Math.Clamp(value, 0.2f, 5.0f);
                services.State.SetFreeze(target, true, freezeDur);
                services.Effects.SendFade(target, Color.FromArgb(55, 120, 200, 255), 400, 0);
                break;
            }

            case "heal":
            {
                var tPawn = SkillHelpers.GetAlivePawn(target);
                int healAmt = (int)Math.Round(value);
                if (tPawn != null && healAmt > 0)
                {
                    int curHp = tPawn.Health;
                    int maxHp = tPawn.MaxHealth > 0 ? tPawn.MaxHealth : 100;
                    int newHp = overheal ? curHp + healAmt : Math.Min(curHp + healAmt, maxHp);
                    if (newHp > curHp)
                    {
                        services.State.SetHealth(target, newHp, add: false, updateMaxHealth: overheal && newHp > maxHp);
                    }
                }
                break;
            }
        }
    }
}
