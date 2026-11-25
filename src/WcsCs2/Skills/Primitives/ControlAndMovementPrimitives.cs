using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Config;

namespace WcsCs2.Skills.Primitives;

public sealed class FreezePrimitive : ISkillPrimitive
{
    public string Type => "freeze";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim)) return;

        bool preventStack = action.GetBool("prevent_stack", true);
        if (preventStack && ctx.Services.State.GetCustomData<bool>(victim!, SkillStateKeys.IsFrozen))
        {
            ctx.Activated = false;
            return;
        }

        float duration = Math.Max(0.1f, action.GetLevelFloat("duration", ctx.SkillLevel, 1.0f));
        ctx.Services.State.SetCustomData(victim!, SkillStateKeys.IsFrozen, true);
        ctx.Services.State.SetFreeze(victim!, true, duration);

        ctx.Services.State.SchedulePlayerTimer(victim!, duration, () =>
        {
            if (SkillHelpers.IsValidPlayer(victim))
                ctx.Services.State.SetCustomData(victim!, SkillStateKeys.IsFrozen, false);
        }, repeat: false, tag: "guard_is_frozen");

        var start = SkillHelpers.CloneOrigin(ctx.Owner);
        var end = SkillHelpers.CloneOrigin(victim);
        if (start != null && end != null)
        {
            ctx.Services.Effects.DrawBeamPoints(
                new Vector(start.X, start.Y, start.Z + 20f),
                new Vector(end.X, end.Y, end.Z + 20f),
                Color.FromArgb(255, 255, 255, 255), 2.5f, 0.5f);
            ctx.Services.Effects.DrawBeamRing(end, 45f, Color.FromArgb(255, 255, 255, 255), 2.5f, 0.5f, 14, 25f);
        }

        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(centerMsg, ctx, duration.ToString("0.#")));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintCenter(victim!, SkillHelpers.FormatMessage(victimMsg, ctx, duration.ToString("0.#")));
    }
}

public sealed class AoEFreezePrimitive : ISkillPrimitive
{
    public string Type => "aoe_freeze";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var origin = SkillHelpers.CloneOrigin(ctx.Owner);
        if (origin == null)
        {
            ctx.Activated = false;
            return;
        }

        float radius = action.GetLevelFloat("radius", ctx.SkillLevel, 250f);
        float duration = Math.Max(0.25f, action.GetLevelFloat("duration", ctx.SkillLevel, 2.0f));
        int maxTargets = action.GetLevelInt("max_targets", ctx.SkillLevel, 20);
        bool respectImmunity = action.GetBool("respect_immunity", true);
        string sound = action.GetString("sound");

        bool ffa = ctx.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(ctx.Owner.Team);
        if (!ffa && enemyTeam == null)
        {
            ctx.Activated = false;
            return;
        }

        var enemies = ctx.Services.Combat.GetPlayersInRadius(origin, radius, teamFilter: enemyTeam, exclude: ctx.Owner, aliveOnly: true);
        if (enemies.Count == 0)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(ctx.Owner, "No enemies within range to entangle.");
            return;
        }

        Color beamColor = SkillHelpers.ReadColor(action, "beam_color", Color.FromArgb(255, 0, 160, 0));
        Color ringColor = SkillHelpers.ReadColor(action, "ring_color", Color.FromArgb(255, 0, 180, 0));

        int affected = 0;
        int immuneCount = 0;
        var casterPos = new Vector(origin.X, origin.Y, origin.Z + 38f);

        foreach (var enemy in enemies)
        {
            if (affected >= maxTargets) break;
            if (!SkillHelpers.IsAlive(enemy)) continue;

            if (respectImmunity && SkillHelpers.IsUltimateImmune(ctx.Services, enemy))
            {
                immuneCount++;
                ctx.Services.Effects.PrintChat(enemy, "Your Ultimate Immunity blocked Entangling Roots!");
                continue;
            }

            if (ctx.Services.State.GetCustomData<bool>(enemy, SkillStateKeys.IsFrozen))
            {
                ctx.Services.Effects.PrintChat(ctx.Owner, $"{enemy.PlayerName} is already frozen.");
                continue;
            }

            ctx.Services.State.SetCustomData(enemy, SkillStateKeys.IsFrozen, true);
            ctx.Services.State.SetFreeze(enemy, true, duration);
            ctx.Services.State.SchedulePlayerTimer(enemy, duration, () =>
            {
                if (SkillHelpers.IsValidPlayer(enemy))
                    ctx.Services.State.SetCustomData(enemy, SkillStateKeys.IsFrozen, false);
            }, repeat: false, tag: "guard_is_frozen");

            ctx.Services.Effects.PrintCenter(enemy, $"Entangled for {duration:0.#}s!");
            if (!string.IsNullOrEmpty(sound))
                ctx.Services.Effects.PlaySound(enemy, sound);

            var enemyPos = SkillHelpers.CloneOrigin(enemy);
            if (enemyPos != null)
            {
                ctx.Services.Effects.DrawBeamPoints(casterPos, new Vector(enemyPos.X, enemyPos.Y, enemyPos.Z + 38f), beamColor, 3.0f, 1.0f);
                ctx.Services.Effects.DrawBeamRing(enemyPos, 45f, ringColor, 2.5f, 1.0f, 14, 25f);
                ctx.Services.Effects.DrawBeamRing(enemyPos, 55f, ringColor, 2.5f, 1.0f, 14, 38f);
            }

            affected++;
        }

        if (affected == 0)
        {
            if (immuneCount > 0)
                ctx.Services.Effects.PrintChat(ctx.Owner, "All nearby enemies are immune to ultimates!");
            ctx.Activated = false;
            return;
        }

        if (!string.IsNullOrEmpty(sound))
            ctx.Services.Effects.PlaySound(ctx.Owner, sound);

        ctx.Services.Effects.PrintCenter(ctx.Owner, $"Entangled {affected} enemy(s) for {duration:0.#}s!");
    }
}

public sealed class PushPrimitive : ISkillPrimitive
{
    public string Type => "push";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float vx = action.GetLevelFloat("vx", ctx.SkillLevel, 0f);
        float vy = action.GetLevelFloat("vy", ctx.SkillLevel, 0f);
        float vz = action.GetLevelFloat("vz", ctx.SkillLevel, 520f);
        bool additive = action.GetBool("additive", true);

        ctx.Services.Combat.PushPlayer(target!, new Vector(vx, vy, vz), additive: additive);

        var pos = SkillHelpers.CloneOrigin(target);
        if (pos != null)
        {
            ctx.Services.Effects.DrawBeamRing(pos, 75f, Color.FromArgb(200, 255, 40, 40), 3.0f, 0.5f, 14, 10f);
        }
    }
}

public sealed class TeleportPrimitive : ISkillPrimitive
{
    public string Type => "teleport";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        if (!SkillHelpers.IsAlive(owner))
        {
            ctx.Activated = false;
            return;
        }

        float range = action.GetLevelFloat("range", ctx.SkillLevel, 1200f);
        string mode = action.GetString("mode", "safe").ToLowerInvariant();
        string sound = action.GetString("sound");

        bool success;
        if (mode == "push")
        {
            var dest = ctx.Services.Teleport.GetCrosshairPoint(owner, Math.Min(range, 3000f));
            var origin = SkillHelpers.CloneOrigin(owner);
            if (dest != null && origin != null)
            {
                float factor = action.GetLevelFloat("push_factor", ctx.SkillLevel, 1.8f);
                var delta = new Vector(dest.X - origin.X, dest.Y - origin.Y, dest.Z - origin.Z);
                float dist = (float)Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
                float clampedDist = Math.Min(dist, range);
                if (dist > 1f)
                {
                    float scale = Math.Min(clampedDist * factor, 1600f) / dist;
                    var vel = new Vector(delta.X * scale, delta.Y * scale, Math.Max(delta.Z * scale, 220f));
                    ctx.Services.Combat.PushPlayer(owner, vel, additive: false);
                    success = true;
                }
                else
                {
                    success = false;
                }
            }
            else
            {
                success = false;
            }
        }
        else
        {
            // Safe hull-traced teleport toward crosshair up to range
            success = ctx.Services.Teleport.TeleportToCrosshair(owner, Math.Min(range, 2500f));
        }

        if (!success)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "Teleport failed — no safe destination in sight.");
            return;
        }

        if (!string.IsNullOrEmpty(sound))
            ctx.Services.Effects.PlaySound(owner, sound);

        var landed = SkillHelpers.CloneOrigin(owner);
        if (landed != null)
            ctx.Services.Effects.DrawBeamRing(landed, 55f, Color.FromArgb(220, 180, 220, 255), 2.5f, 0.7f, 16, 20f);
    }
}

public sealed class TeleportDashPrimitive : ISkillPrimitive
{
    public const string DefaultSound = "UIPanorama.equip_musicKit";
    public const string DefaultGlowParticle = "particles/ui/ui_electric_exp_glow.vpcf";
    public const string DefaultDistortParticle = "particles/explosions_fx/explosion_smokegrenade_distort.vpcf";

    public string Type => "teleport_dash";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null || pawn.AbsOrigin == null)
        {
            ctx.Activated = false;
            return;
        }

        // Capture launch origin before applying dash velocity
        var origin = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);

        float force = Math.Clamp(action.GetLevelFloat("force", ctx.SkillLevel, 1000f), 200f, 2500f);
        float zBoost = action.GetLevelFloat("z_boost", ctx.SkillLevel, 150f);

        // Source 2 AngleVectors forward calculation (negative pitch looks up)
        var eyeAngles = pawn.EyeAngles;
        float pitchRad = eyeAngles.X * (MathF.PI / 180f);
        float yawRad = eyeAngles.Y * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitchRad);

        float velX = cosPitch * MathF.Cos(yawRad) * force;
        float velY = cosPitch * MathF.Sin(yawRad) * force;
        float velZ = -MathF.Sin(pitchRad) * force;

        float currentVelZ = pawn.AbsVelocity?.Z ?? 0f;
        var newVel = new Vector(velX, velY, MathF.Max(currentVelZ, velZ + zBoost));

        pawn.Teleport(null, null, newVel);

        string sound = action.GetString("sound", DefaultSound);
        float soundVolume = Math.Clamp(action.GetLevelFloat("sound_volume", ctx.SkillLevel, 0.5f), 0.05f, 1.0f);
        if (!string.IsNullOrWhiteSpace(sound))
        {
            ctx.Services.Effects.EmitSound(owner, sound, soundVolume);
        }

        int particleCount = Math.Clamp(action.GetLevelInt("particle_count", ctx.SkillLevel, 2), 0, 4);
        string glowParticle = action.GetString("glow_particle", DefaultGlowParticle);
        float glowDuration = action.GetLevelFloat("glow_duration", ctx.SkillLevel, 3.0f);
        string distortParticle = action.GetString("distort_particle", DefaultDistortParticle);
        float distortDuration = action.GetLevelFloat("distort_duration", ctx.SkillLevel, 2.0f);

        for (int i = 0; i < particleCount; i++)
        {
            if (!string.IsNullOrWhiteSpace(glowParticle))
            {
                ctx.Services.Effects.SpawnParticle(
                    new Vector(origin.X, origin.Y, origin.Z + 20f),
                    glowParticle,
                    glowDuration);
            }
            if (!string.IsNullOrWhiteSpace(distortParticle))
            {
                ctx.Services.Effects.SpawnParticle(
                    new Vector(origin.X, origin.Y, origin.Z),
                    distortParticle,
                    distortDuration);
            }
        }

        ctx.Activated = true;
    }
}

public sealed class RespawnPrimitive : ISkillPrimitive
{
    private readonly RoundTracker _roundTracker;

    internal RespawnPrimitive(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Type => "respawn";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        if (!SkillHelpers.IsValidPlayer(owner) || !_roundTracker.IsRoundActive)
            return;

        // Prevent duplicate queued respawns (e.g. Phoenix + Reincarnation)
        if (ctx.Services.State.GetCustomData<bool>(owner, SkillStateKeys.PendingRespawn))
            return;

        float delay = Math.Max(0.5f, action.GetLevelFloat("delay", ctx.SkillLevel, 3.0f));
        bool atDeathOrigin = action.GetBool("at_death_origin", true);
        bool restoreWeapons = action.GetBool("restore_weapons", false);
        bool countdown = action.GetBool("countdown", true);
        Color ringColor = SkillHelpers.ReadColor(action, "ring_color", Color.FromArgb(220, 255, 71, 36));

        var deathOrigin = atDeathOrigin ? SkillHelpers.CloneOrigin(owner) : null;
        var savedWeapons = restoreWeapons ? ctx.Services.Weapons.SnapshotWeapons(owner) : null;
        int capturedRound = _roundTracker.CurrentRoundId;
        var services = ctx.Services;

        services.State.SetCustomData(owner, SkillStateKeys.PendingRespawn, true);
        services.Effects.PrintChat(owner, $"Reincarnation! You will respawn in {(int)Math.Ceiling(delay)} seconds.");

        if (deathOrigin != null)
        {
            services.Effects.DrawBeamRing(deathOrigin, 110f, ringColor, 3.0f, delay, 18, 15f);
        }

        if (countdown && delay >= 1.5f)
        {
            int secs = (int)Math.Ceiling(delay);
            for (int remaining = secs; remaining >= 1; remaining--)
            {
                int rem = remaining;
                float t = delay - rem;
                if (t <= 0.05f)
                {
                    services.Effects.PrintCenter(owner, $"Respawning in {rem}...");
                }
                else
                {
                    services.AddTimer(t, () =>
                    {
                        if (capturedRound != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive) return;
                        if (!SkillHelpers.IsValidPlayer(owner) || SkillHelpers.IsAlive(owner)) return;
                        services.Effects.PrintCenter(owner, $"Respawning in {rem}...");
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }
            }
        }

        services.AddTimer(delay, () =>
        {
            if (SkillHelpers.IsValidPlayer(owner))
                services.State.SetCustomData(owner, SkillStateKeys.PendingRespawn, false);

            if (capturedRound != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive)
                return;
            if (!SkillHelpers.IsValidPlayer(owner) || SkillHelpers.IsAlive(owner))
                return;
            if (!SkillHelpers.IsPlayableTeam(owner.Team))
                return;

            if (services.Teleport.RespawnPlayer(owner, deathOrigin, savedWeapons))
            {
                services.Effects.PrintCenter(owner, "You have been reincarnated!");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}

public sealed class ShakePrimitive : ISkillPrimitive
{
    public string Type => "shake";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        float amplitude = action.GetLevelFloat("amplitude", ctx.SkillLevel, 15f);
        float frequency = action.GetLevelFloat("frequency", ctx.SkillLevel, 13f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 3.0f);

        ctx.Services.Effects.SendShake(target!, amplitude, frequency, duration);

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg) && SkillHelpers.IsValidPlayer(ctx.Owner))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx, duration.ToString("0.#")));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintCenter(target!, SkillHelpers.FormatMessage(victimMsg, ctx, duration.ToString("0.#")));
    }
}

public sealed class FadePrimitive : ISkillPrimitive
{
    public string Type => "fade";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        Color color = SkillHelpers.ReadColor(action, "color", Color.FromArgb(175, 60, 200, 210));
        float duration = Math.Max(0.2f, action.GetLevelFloat("duration", ctx.SkillLevel, 1.5f));
        float holdRatio = Math.Clamp(action.GetLevelFloat("hold_ratio", ctx.SkillLevel, 0.7f), 0.1f, 0.95f);

        int holdMs = (int)Math.Round(duration * holdRatio * 1000f);
        int fadeMs = (int)Math.Round(duration * (1f - holdRatio) * 1000f);
        ctx.Services.Effects.SendFade(target!, color, fadeMs, holdMs);

        float shakeAmp = action.GetLevelFloat("shake_amplitude", ctx.SkillLevel, 0f);
        if (shakeAmp > 0f)
        {
            float shakeFreq = action.GetLevelFloat("shake_frequency", ctx.SkillLevel, 8f);
            ctx.Services.Effects.SendShake(target!, shakeAmp, shakeFreq, duration);
        }

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg) && SkillHelpers.IsValidPlayer(ctx.Owner))
            ctx.Services.Effects.PrintChat(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx, duration.ToString("0.#")));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(victimMsg, ctx, duration.ToString("0.#")));
    }
}
