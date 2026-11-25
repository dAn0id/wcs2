using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Skills.Primitives;

namespace WcsCs2.Skills.CustomHandlers;

public sealed class DefiladedDestructorHandler : ISkillHandler
{
    private const int DefaultMaxBarrelsPerRound = 10;
    private static readonly string[] AllowedWeapons = { "weapon_p250", "weapon_knife", "weapon_c4" };

    private readonly RoundTracker _roundTracker;
    private readonly Dictionary<ulong, List<CBreakableProp>> _barrelsByOwner = new();

    internal DefiladedDestructorHandler(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Key => "defiladed_destructor";

    public void Execute(SkillContext ctx)
    {
        if (ctx.Skill == null && ctx.Trigger == SkillTrigger.PlayerSpawn)
        {
            HandleSpawn(ctx);
            return;
        }

        if (ctx.Trigger == SkillTrigger.PlayerUltimate)
        {
            HandleBuildBarrel(ctx);
        }
    }

    public void HandleSpawn(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null) return;

        var services = ctx.Services;
        int capturedRound = _roundTracker.CurrentRoundId;

        // 1. Barrel prop disguise (uses safe stock CS2 barrel model from WcsConfig)
        string barrelModel = services.Config.DefaultBarrelModel;
        if (!string.IsNullOrWhiteSpace(barrelModel))
        {
            services.State.SetPropDisguise(owner, barrelModel);
        }

        // 2. Restrict all weapons except P250 / knife / C4 and strip existing guns
        services.Weapons.RestrictAllExcept(owner, AllowedWeapons);
        services.Weapons.StripWeapons(owner, keepKnife: true, keepC4: true);

        // 3. Apply -60 HP penalty (100 -> 40 HP, min 1 HP)
        int penalizedHp = Math.Max(1, pawn.Health - 60);
        services.State.SetHealth(owner, penalizedHp, add: false, updateMaxHealth: true);

        // 4. Give P250 at +0.1s and set 250-round mega-clip with 0 reserve at +0.5s
        services.State.SchedulePlayerTimer(owner, 0.1f, () =>
        {
            if (capturedRound != _roundTracker.CurrentRoundId || !SkillHelpers.IsAlive(owner)) return;
            services.Weapons.GiveWeapon(owner, "weapon_p250");
        }, repeat: false, tag: "dd_give_p250");

        services.State.SchedulePlayerTimer(owner, 0.5f, () =>
        {
            if (capturedRound != _roundTracker.CurrentRoundId || !SkillHelpers.IsAlive(owner)) return;
            services.Weapons.SetClipAndAmmo(owner, "weapon_p250", clip: 250, reserve: 0);
        }, repeat: false, tag: "dd_p250_clip");

        services.Effects.PrintChat(owner, "You are now a Barrel!");
    }

    private void HandleBuildBarrel(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        var action = ctx.Skill?.Actions.FirstOrDefault();
        int maxBarrels = DefaultMaxBarrelsPerRound;
        if (action != null)
        {
            if (SkillHelpers.HasKey(action, "max_props"))
                maxBarrels = action.GetLevelInt("max_props", ctx.SkillLevel, DefaultMaxBarrelsPerRound);
            else if (SkillHelpers.HasKey(action, "max_barrels"))
                maxBarrels = action.GetLevelInt("max_barrels", ctx.SkillLevel, DefaultMaxBarrelsPerRound);
        }

        ulong ownerKey = GetOwnerKey(owner);
        if (!_barrelsByOwner.TryGetValue(ownerKey, out var list))
        {
            list = new List<CBreakableProp>();
            _barrelsByOwner[ownerKey] = list;
        }

        // Prune destroyed or invalid barrels
        list.RemoveAll(b => b is not { IsValid: true });
        if (list.Count >= maxBarrels)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, $"Barrel limit reached ({maxBarrels} active barrels max)!");
            return;
        }

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin == null)
        {
            ctx.Activated = false;
            return;
        }

        // Project spawn point ~85 units in front of where the player is looking
        var eyeAngles = pawn.EyeAngles;
        double yawRad = eyeAngles.Y * (Math.PI / 180.0);
        float forwardX = (float)Math.Cos(yawRad);
        float forwardY = (float)Math.Sin(yawRad);

        var spawnPos = new Vector(
            origin.X + forwardX * 85f,
            origin.Y + forwardY * 85f,
            origin.Z + 12f);

        // If crosshair ray hits an obstacle closer than 85u, pull back safely or refuse if point-blank against a wall
        var aimHit = ctx.Services.Teleport.GetCrosshairPoint(owner, 120f);
        if (aimHit != null)
        {
            float dx = aimHit.X - origin.X;
            float dy = aimHit.Y - origin.Y;
            float horizDist = (float)Math.Sqrt(dx * dx + dy * dy);
            bool lookingSteeplyDown = eyeAngles.X >= 45f;

            if (horizDist < 42f && !lookingSteeplyDown)
            {
                ctx.Activated = false;
                ctx.Services.Effects.PrintChat(owner, "Too close to a wall or obstacle to spawn a barrel here!");
                return;
            }

            if (horizDist >= 42f && horizDist < 85f)
            {
                float safeDist = Math.Max(36f, horizDist - 24f);
                float inv = 1f / horizDist;
                spawnPos = new Vector(
                    origin.X + dx * inv * safeDist,
                    origin.Y + dy * inv * safeDist,
                    origin.Z + 12f);
            }
        }

        var spawnAngles = new QAngle(0f, eyeAngles.Y, 0f);
        string modelPath = action?.GetString("model") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelPath))
            modelPath = ctx.Services.Config.DefaultBarrelModel;

        bool explosive = action?.GetBool("explosive", true) ?? true;
        float explosionDamage = action != null && SkillHelpers.HasKey(action, "explosion_damage")
            ? action.GetLevelFloat("explosion_damage", ctx.SkillLevel, 90f)
            : 90f;
        float explosionRadius = action != null && SkillHelpers.HasKey(action, "explosion_radius")
            ? action.GetLevelFloat("explosion_radius", ctx.SkillLevel, 220f)
            : 220f;
        float lifetime = action != null && SkillHelpers.HasKey(action, "lifetime")
            ? action.GetLevelFloat("lifetime", ctx.SkillLevel, 60f)
            : 60f;

        var prop = ctx.Services.Effects.SpawnPhysicsProp(
            spawnPos,
            spawnAngles,
            modelPath,
            explosive: explosive,
            explosionDamage: explosionDamage,
            explosionRadius: explosionRadius,
            owner: owner,
            lifetime: lifetime);

        if (prop == null || !prop.IsValid)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "Could not spawn barrel here.");
            return;
        }

        ctx.Services.State.TrackSpawnedEntity(owner, prop);
        list.Add(prop);

        ctx.Services.Effects.DrawBeamRing(spawnPos, 40f, Color.FromArgb(200, 255, 160, 40), 2.5f, 0.5f, 12, 8f);
        ctx.Services.Effects.PrintChat(owner, $"Oil barrel spawned! ({list.Count}/{maxBarrels})");
        ctx.Activated = true;
    }

    public void OnCleanup(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false)
    {
        bool disconnected = isDisconnect || !SkillHelpers.IsValidPlayer(player);
        bool changedRace = !string.Equals(session.CurrentRaceKey, "defiladed_destructor", StringComparison.OrdinalIgnoreCase);

        if (disconnected || changedRace)
        {
            ulong key = session.SteamId != 0 ? session.SteamId : (ulong)(session.Slot + 1);
            RemoveOwnerBarrels(key);
        }
    }

    public void OnRoundStart() => RemoveAllBarrels();
    public void OnRoundEnd() => RemoveAllBarrels();

    private void RemoveOwnerBarrels(ulong ownerKey)
    {
        if (!_barrelsByOwner.TryGetValue(ownerKey, out var list))
            return;

        foreach (var prop in list)
        {
            KillPropSafe(prop);
        }
        _barrelsByOwner.Remove(ownerKey);
    }

    private void RemoveAllBarrels()
    {
        foreach (var list in _barrelsByOwner.Values)
        {
            foreach (var prop in list)
            {
                KillPropSafe(prop);
            }
        }
        _barrelsByOwner.Clear();
    }

    private static void KillPropSafe(CBreakableProp prop)
    {
        try
        {
            if (prop.IsValid)
                prop.AcceptInput("Kill");
        }
        catch
        {
            // Ignore entity already destroyed by engine
        }
    }

    private static ulong GetOwnerKey(CCSPlayerController player)
        => player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1);
}
