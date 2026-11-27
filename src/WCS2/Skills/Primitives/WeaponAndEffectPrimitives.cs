using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;

namespace WCS2.Skills.Primitives;

public sealed class GiveWeaponPrimitive : ISkillPrimitive
{
    public string Type => "give_weapon";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        bool stripPrimary = action.GetBool("strip_primary", false);
        bool stripSecondary = action.GetBool("strip_secondary", false);
        var weapons = action.GetStringList("weapons");
        string single = action.GetString("weapon");
        if (!string.IsNullOrEmpty(single))
            weapons.Add(single);

        if (stripPrimary || stripSecondary)
        {
            ctx.Services.Weapons.StripWeapons(target!, keepKnife: true, keepC4: true);
            if (weapons.Count > 0)
            {
                var services = ctx.Services;
                var listCopy = weapons.ToList();
                services.State.SchedulePlayerTimer(target!, 0.3f, () =>
                {
                    if (!SkillHelpers.IsAlive(target)) return;
                    foreach (var w in listCopy)
                        services.Weapons.GiveWeapon(target!, w);
                }, repeat: false, tag: "give_weapon_delayed");
            }
        }
        else
        {
            foreach (var w in weapons)
                ctx.Services.Weapons.GiveWeapon(target!, w);
        }

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx));
    }
}

public sealed class DropWeaponPrimitive : ISkillPrimitive
{
    public string Type => "drop_weapon";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim)) return;

        bool primaryOnly = action.GetBool("primary_only", true);
        ctx.Services.Weapons.DropWeapon(victim!, primaryOnly: primaryOnly);

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg) && SkillHelpers.IsValidPlayer(ctx.Owner))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintCenter(victim!, SkillHelpers.FormatMessage(victimMsg, ctx));
    }
}

public sealed class SetClipAmmoPrimitive : ISkillPrimitive
{
    public string Type => "set_clip_ammo";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        string weapon = action.GetString("weapon", "secondary");
        int? clip = SkillHelpers.HasKey(action, "clip") ? action.GetLevelInt("clip", ctx.SkillLevel) : null;
        int? reserve = SkillHelpers.HasKey(action, "reserve") ? action.GetLevelInt("reserve", ctx.SkillLevel) : null;

        ctx.Services.Weapons.SetClipAndAmmo(target!, weapon, clip, reserve);
    }
}

public sealed class BeamPrimitive : ISkillPrimitive
{
    public string Type => "beam";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string fromSel = action.GetString("from", "attacker");
        string toSel = action.GetString("to", "victim");
        var fromPlayer = ctx.ResolveSingleTarget(fromSel);
        var toPlayer = ctx.ResolveSingleTarget(toSel);

        var start = SkillHelpers.CloneOrigin(fromPlayer);
        var end = SkillHelpers.CloneOrigin(toPlayer);
        if (start == null || end == null) return;

        float zOffset = action.GetLevelFloat("z_offset", ctx.SkillLevel, 25f);
        float width = action.GetLevelFloat("width", ctx.SkillLevel, 2.5f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0.7f);
        Color color = SkillHelpers.ReadColor(action, "color", Color.FromArgb(255, 255, 255, 255));

        ctx.Services.Effects.DrawBeamPoints(
            new Vector(start.X, start.Y, start.Z + zOffset),
            new Vector(end.X, end.Y, end.Z + zOffset),
            color, width, duration);
    }
}

public sealed class BeamRingPrimitive : ISkillPrimitive
{
    public string Type => "beam_ring";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        var origin = SkillHelpers.CloneOrigin(target);
        if (origin == null) return;

        float radius = action.GetLevelFloat("radius", ctx.SkillLevel, 65f);
        float width = action.GetLevelFloat("width", ctx.SkillLevel, 2.5f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 1.0f);
        float zOffset = action.GetLevelFloat("z_offset", ctx.SkillLevel, 15f);
        int count = Math.Clamp(action.GetLevelInt("count", ctx.SkillLevel, 1), 1, 6);
        float zStep = action.GetLevelFloat("z_step", ctx.SkillLevel, 10f);
        Color color = SkillHelpers.ReadColor(action, "color", Color.FromArgb(255, 255, 255, 255));

        for (int i = 0; i < count; i++)
        {
            ctx.Services.Effects.DrawBeamRing(origin, radius, color, width, duration, 14, zOffset + i * zStep);
        }
    }
}

public sealed class SoundPrimitive : ISkillPrimitive
{
    public string Type => "sound";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string soundEvent = action.GetString("sound_event");
        if (string.IsNullOrEmpty(soundEvent)) return;

        if (string.Equals(action.Target, "all", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Services.Effects.PlaySoundAll(soundEvent);
            return;
        }

        var target = ctx.ResolveSingleTarget(action.Target);
        if (SkillHelpers.IsValidPlayer(target))
            ctx.Services.Effects.PlaySound(target!, soundEvent);
    }
}

public sealed class MessagePrimitive : ISkillPrimitive
{
    public string Type => "message";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string text = action.GetString("text");
        if (string.IsNullOrEmpty(text)) return;

        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsValidPlayer(target)) return;

        string formatted = SkillHelpers.FormatMessage(text, ctx);
        if (action.GetBool("center", false))
            ctx.Services.Effects.PrintCenter(target!, formatted);
        else
            ctx.Services.Effects.PrintChat(target!, formatted);
    }
}

public sealed class InstantRestockPrimitive : ISkillPrimitive
{
    private readonly RoundTracker _roundTracker;

    internal InstantRestockPrimitive(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Type => "instant_restock";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        if (!SkillHelpers.IsAlive(owner))
        {
            ctx.Activated = false;
            return;
        }

        var services = ctx.Services;
        string rawWeapon = action.GetString("weapon", "weapon_hegrenade");
        if (string.IsNullOrWhiteSpace(rawWeapon))
            rawWeapon = "weapon_hegrenade";
        string weapon = services.Weapons.NormalizeWeaponName(rawWeapon);

        float interval = action.GetLevelFloat("interval", ctx.SkillLevel, 10f);
        float duration = MathF.Max(0f, action.GetLevelFloat("duration", ctx.SkillLevel, 0f));
        bool onlyIfMissing = action.GetBool("only_if_missing", true);
        bool immediate = action.GetBool("immediate", true);
        int maxGrants = Math.Max(0, action.GetLevelInt("max_grants", ctx.SkillLevel, 0));
        float radius = MathF.Max(1f, action.GetLevelFloat("radius", ctx.SkillLevel, 300f));
        string msg = action.GetString("message");
        string targetSelector = string.IsNullOrWhiteSpace(action.Target) ? "self" : action.Target;

        int GrantOnce()
        {
            var targets = SkillHelpers.ResolveTargets(ctx, targetSelector, radius);
            int granted = 0;
            foreach (var target in targets)
            {
                if (!SkillHelpers.IsAlive(target))
                    continue;
                if (onlyIfMissing && SkillHelpers.PlayerHasWeapon(target, services.Weapons, weapon))
                    continue;

                services.Weapons.GiveWeapon(target, weapon);
                granted++;

                if (!string.IsNullOrEmpty(msg))
                {
                    services.Effects.PrintChat(target, SkillHelpers.FormatMessage(msg, ctx, value: weapon));
                }
            }
            return granted;
        }

        // One-shot mode (interval <= 0): grant immediately once with no repeating timer
        if (interval <= 0f)
        {
            int granted = GrantOnce();
            if (granted == 0 && (ctx.Trigger == SkillTrigger.PlayerUltimate || ctx.Trigger == SkillTrigger.PlayerAbility))
            {
                ctx.Activated = false;
                services.Effects.PrintChat(owner, "No eligible targets to restock!");
            }
            return;
        }

        // Repeating restock timer mode
        float safeInterval = MathF.Max(0.5f, interval);
        string tag = $"restock_{ctx.Skill?.Key ?? "item"}_{weapon}";
        int capturedRound = _roundTracker.CurrentRoundId;
        int grantsSoFar = 0;
        float elapsed = 0f;
        bool stopped = false;

        // Replace any previous restock timer with the same tag on this player
        services.State.CancelPlayerTimers(owner, tag);

        if (immediate)
        {
            if (GrantOnce() > 0)
                grantsSoFar++;

            if (maxGrants > 0 && grantsSoFar >= maxGrants)
                return;
        }

        // SchedulePlayerTimer creates TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE
        // and automatically cleans up on death, round end, race change, and disconnect.
        services.State.SchedulePlayerTimer(owner, safeInterval, () =>
        {
            if (stopped)
                return;

            elapsed += safeInterval;
            if (capturedRound != _roundTracker.CurrentRoundId
                || !_roundTracker.IsRoundActive
                || !SkillHelpers.IsAlive(owner))
            {
                stopped = true;
                if (SkillHelpers.IsValidPlayer(owner))
                    services.State.CancelPlayerTimers(owner, tag);
                return;
            }

            if ((duration > 0f && elapsed > duration + 0.01f)
                || (maxGrants > 0 && grantsSoFar >= maxGrants))
            {
                stopped = true;
                services.State.CancelPlayerTimers(owner, tag);
                return;
            }

            if (GrantOnce() > 0)
                grantsSoFar++;

            if ((maxGrants > 0 && grantsSoFar >= maxGrants)
                || (duration > 0f && elapsed >= duration))
            {
                stopped = true;
                services.State.CancelPlayerTimers(owner, tag);
            }
        }, repeat: true, tag: tag);
    }
}

public sealed class ExplosiveMolotovPrimitive : ISkillPrimitive
{
    public string Type => "explosive_molotov";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target))
        {
            ctx.Activated = false;
            return;
        }

        float baseDamage = MathF.Max(0f, action.GetLevelFloat("damage", ctx.SkillLevel, 80f));
        float minDamage = MathF.Max(0f, action.GetLevelFloat("min_damage", ctx.SkillLevel, 5f));
        float radius = MathF.Max(1f, action.GetLevelFloat("radius", ctx.SkillLevel, 250f));
        float launchSpeed = Math.Clamp(action.GetLevelFloat("speed", ctx.SkillLevel, 1800f), 0f, 4000f);
        float duration = MathF.Max(0f, action.GetLevelFloat("duration", ctx.SkillLevel, 0f));
        bool includeIncendiary = action.GetBool("include_incendiary", true);
        bool fireballVisuals = action.GetBool("fireball_visuals", true);

        var defaults = new ExplosiveMolotovConfig();
        string trailParticle = action.GetString("trail_particle");
        string impact1 = action.GetString("impact_particle_1");
        string impact2 = action.GetString("impact_particle_2");
        string fireballModel = action.GetString("fireball_model");

        var cfg = new ExplosiveMolotovConfig(
            BaseDamage: baseDamage,
            MinDamage: minDamage,
            Radius: radius,
            LaunchSpeed: launchSpeed,
            IncludeIncendiary: includeIncendiary,
            FireballVisuals: fireballVisuals,
            TrailParticle: string.IsNullOrWhiteSpace(trailParticle) ? defaults.TrailParticle : trailParticle,
            ImpactParticle1: string.IsNullOrWhiteSpace(impact1) ? defaults.ImpactParticle1 : impact1,
            ImpactParticle2: string.IsNullOrWhiteSpace(impact2) ? defaults.ImpactParticle2 : impact2,
            FireballModel: string.IsNullOrWhiteSpace(fireballModel) ? defaults.FireballModel : fireballModel);

        ctx.Services.State.SetExplosiveMolotov(target!, cfg, duration);

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
        {
            string dmgStr = ((int)Math.Round(baseDamage)).ToString();
            string durStr = duration.ToString("0.#");
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx, value: dmgStr, damage: dmgStr, duration: durStr));
        }
    }
}
