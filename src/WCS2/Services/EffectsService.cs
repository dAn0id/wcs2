using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;
using WCS2.Skills;

namespace WCS2.Services;

public sealed class EffectsService : IEffectsService
{
    private readonly BasePlugin _plugin;
    private readonly WcsConfig _config;
    private int _activeBeamCount;
    private const int MaxConcurrentBeams = 120;

    public EffectsService(BasePlugin plugin, WcsConfig config)
    {
        _plugin = plugin;
        _config = config;
    }

    public void DrawBeamPoints(Vector start, Vector end, Color color, float width = 2f, float duration = 1f)
    {
        if (_activeBeamCount >= MaxConcurrentBeams) return;
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null || !beam.IsValid) return;

        beam.Render = color;
        beam.Width = Math.Clamp(width, 0.5f, 15f);
        beam.Teleport(start, QAngle.Zero, Vector.Zero);
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        beam.DispatchSpawn();

        _activeBeamCount++;
        float life = Math.Clamp(duration, 0.1f, 3.0f);
        _plugin.AddTimer(life, () =>
        {
            _activeBeamCount = Math.Max(0, _activeBeamCount - 1);
            try
            {
                if (beam.IsValid) beam.Remove();
            }
            catch { /* already removed */ }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void DrawBeamRing(
        Vector center,
        float radius,
        Color color,
        float width = 2f,
        float duration = 1f,
        int segments = 16,
        float zOffset = 10f)
    {
        int segs = Math.Clamp(segments, 6, 16);
        if (_activeBeamCount + segs > MaxConcurrentBeams)
            segs = Math.Max(0, MaxConcurrentBeams - _activeBeamCount);
        if (segs < 6) return;

        float z = center.Z + zOffset;
        float step = (2f * MathF.PI) / segs;

        Vector prev = new(center.X + MathF.Cos(0f) * radius, center.Y + MathF.Sin(0f) * radius, z);
        for (int i = 1; i <= segs; i++)
        {
            float angle = i * step;
            Vector next = new(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius, z);
            DrawBeamPoints(prev, next, color, width, duration);
            prev = next;
        }
    }

    public void SpawnParticle(string effectName, Vector origin, float duration = 2f)
        => SpawnParticle(origin, effectName, duration);

    public void SpawnParticle(Vector origin, string particlePath, float durationSeconds = 3f, CBaseEntity? parent = null)
    {
        if (string.IsNullOrWhiteSpace(particlePath)) return;
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return;

        particle.EffectName = particlePath;
        particle.StartActive = true;
        particle.Teleport(origin, QAngle.Zero, Vector.Zero);
        particle.DispatchSpawn();
        if (parent != null && parent.IsValid)
        {
            particle.AcceptInput("SetParent", parent, particle, "!activator");
        }
        particle.AcceptInput("Start");

        _plugin.AddTimer(Math.Clamp(durationSeconds, 0.2f, 10.0f), () =>
        {
            try
            {
                if (particle.IsValid)
                {
                    particle.AcceptInput("Stop");
                    particle.Remove();
                }
            }
            catch { /* already removed */ }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void SendFade(CCSPlayerController player, Color color, int durationMs, int holdTimeMs, int flags = 0x0001)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        try
        {
            var msg = UserMessage.FromPartialName("Fade");
            if (msg == null) return;
            msg.SetInt("duration", Math.Max(0, durationMs));
            msg.SetInt("hold_time", Math.Max(0, holdTimeMs));
            msg.SetInt("flags", flags);
            msg.SetInt("clr", (color.R) | (color.G << 8) | (color.B << 16) | (color.A << 24));
            msg.Send(player);
        }
        catch { /* ignore if message unavailable */ }
    }

    public void SendShake(CCSPlayerController player, float amplitude, float frequency, float duration)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        try
        {
            var msg = UserMessage.FromPartialName("Shake");
            if (msg == null) return;
            msg.SetInt("command", 0);
            msg.SetFloat("local_amplitude", Math.Clamp(amplitude, 1f, 30f));
            msg.SetFloat("frequency", Math.Clamp(frequency, 1f, 250f));
            msg.SetFloat("duration", Math.Clamp(duration, 0.1f, 5f));
            msg.Send(player);
        }
        catch { /* ignore if message unavailable */ }
    }

    public void PlaySound(CCSPlayerController player, string soundEvent)
    {
        if (player == null || !player.IsValid || player.IsBot || string.IsNullOrWhiteSpace(soundEvent)) return;
        player.ExecuteClientCommand($"play {soundEvent}");
    }

    public void EmitSound(CCSPlayerController player, string soundEvent, float volume = 1.0f)
    {
        if (player == null || !player.IsValid || string.IsNullOrWhiteSpace(soundEvent)) return;
        try
        {
            float clampedVol = Math.Clamp(volume, 0.05f, 1.0f);
            CBaseEntity emitter = player.PlayerPawn?.Value is { IsValid: true } pawn ? pawn : player;
            emitter.EmitSound(soundEvent, volume: clampedVol);
        }
        catch { /* ignore invalid sound event or entity */ }
    }

    public void PlaySoundAll(string soundEvent)
    {
        if (string.IsNullOrWhiteSpace(soundEvent)) return;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.Connected)
                p.ExecuteClientCommand($"play {soundEvent}");
        }
    }

    public CBreakableProp? SpawnPhysicsProp(
        Vector origin,
        QAngle angles,
        string modelPath,
        bool explosive = false,
        float explosionDamage = 90f,
        float explosionRadius = 220f,
        CCSPlayerController? owner = null,
        float lifetime = 30f)
    {
        var prop = Utilities.CreateEntityByName<CBreakableProp>("prop_physics_override");
        if (prop == null || !prop.IsValid) return null;

        string mdl = string.IsNullOrWhiteSpace(modelPath) ? _config.DefaultBarrelModel : modelPath;
        prop.SetModel(mdl);
        prop.Teleport(origin, angles, Vector.Zero);
        if (explosive)
        {
            prop.ExplodeDamage = explosionDamage;
            prop.ExplodeRadius = explosionRadius;
            prop.Health = 40;
        }
        prop.DispatchSpawn();

        if (lifetime > 0f)
        {
            _plugin.AddTimer(lifetime, () =>
            {
                try
                {
                    if (prop.IsValid) prop.Remove();
                }
                catch { /* already removed */ }
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        return prop;
    }

    public void PrintChat(CCSPlayerController player, string message)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        player.PrintToChat($" {ChatColors.Green}{_config.ChatPrefix} {ChatColors.Default}{message}");
    }

    public void PrintCenter(CCSPlayerController player, string message)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        player.PrintToCenter(message);
    }
}
