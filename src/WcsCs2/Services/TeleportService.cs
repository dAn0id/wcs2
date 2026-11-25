using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Core;
using WcsCs2.Skills;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace WcsCs2.Services;

public sealed class TeleportService : ITeleportService
{
    private const float PlayerHalfWidth = 16.0f;
    private const float StandingHeight = 72.0f;
    private const float CrouchedHeight = 54.0f;
    private const float HullMargin = 0.5f;
    private const float MinTeleportDistance = 32.0f;
    private const float FloorSnapDistance = 96.0f;

    private readonly IWeaponService _weapons;
    private readonly Source2TraceEngine _tracer;
    private readonly Random _rng;

    public TeleportService(IWeaponService weapons, Source2TraceEngine tracer, Random rng)
    {
        _weapons = weapons;
        _tracer = tracer;
        _rng = rng;
    }

    public Vector? GetCrosshairPoint(CCSPlayerController player, float maxDistance = 1500f)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn) || pawn.AbsOrigin == null)
            return null;

        Vector3 eye = GetEyePosition(pawn);
        Vector3 dir = GetAimDirection(pawn.EyeAngles);
        float dist = Math.Clamp(maxDistance, 32f, 3000f);
        Vector3 targetEnd = eye + dir * dist;

        if (_tracer.IsAvailable && _tracer.TryTraceLine(eye, targetEnd, pawn, out var hit))
        {
            if (hit.StartInSolid)
                return null;

            Vector3 pt = hit.DidHit ? hit.EndPos : targetEnd;
            return new Vector(pt.X, pt.Y, pt.Z);
        }

        // Safe fallback if native tracer is unavailable
        return new Vector(targetEnd.X, targetEnd.Y, targetEnd.Z);
    }

    public bool TeleportToCrosshair(CCSPlayerController player, float maxDistance = 1200f)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn) || pawn.AbsOrigin == null)
            return false;

        // Safe managed path when native physics tracing is disabled or unavailable (ZERO native calls)
        if (!_tracer.IsAvailable)
            return TeleportManagedFallback(player, pawn, maxDistance);

        Vector3 origin = ToVec3(pawn.AbsOrigin);
        Vector3 eye = GetEyePosition(pawn);
        Vector3 dir = GetAimDirection(pawn.EyeAngles);
        float range = Math.Clamp(maxDistance, 64f, 2500f);
        Vector3 rayEnd = eye + dir * range;

        // Step 1: Raycast along EyeAngles against MASK_PLAYERSOLID (walls, clips, props, doors, players)
        if (!_tracer.TryTraceLine(eye, rayEnd, pawn, out var aimTrace) || aimTrace.StartInSolid)
            return false;

        Vector3 aimPoint = aimTrace.DidHit ? aimTrace.EndPos : rayEnd;
        float aimDist = Vector3.Distance(eye, aimPoint);
        if (aimDist < 36f)
            return false; // Staring point-blank at a wall/prop

        Vector3 hitNormal = aimTrace.DidHit && aimTrace.Normal.LengthSquared() > 0.01f
            ? Vector3.Normalize(aimTrace.Normal)
            : -dir;

        bool isCrouched = (pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0;
        float hullHeight = isCrouched ? CrouchedHeight : StandingHeight;
        var hullMins = new Vector3(-PlayerHalfWidth, -PlayerHalfWidth, 0f);
        var hullMaxs = new Vector3(PlayerHalfWidth, PlayerHalfWidth, hullHeight);
        var checkMins = new Vector3(-(PlayerHalfWidth + HullMargin), -(PlayerHalfWidth + HullMargin), HullMargin);
        var checkMaxs = new Vector3(PlayerHalfWidth + HullMargin, PlayerHalfWidth + HullMargin, hullHeight + HullMargin);

        // Step 2: Build prioritized candidate destinations near aimPoint (normal pullback + ray pullback + swept hull)
        var candidates = new List<Vector3>(20);

        // Primary candidate adjusted for surface normal so player bbox clears walls/ceilings/floors/props
        Vector3 primary = ComputeSurfaceAdjustedOrigin(aimPoint, hitNormal, aimTrace.DidHit, hullHeight);
        candidates.Add(primary);

        // Also test slightly raised & normal-nudged variants (helps on stairs, curbs, crates, sloped roofs)
        candidates.Add(primary + new Vector3(0f, 0f, 18f));
        if (aimTrace.DidHit)
        {
            candidates.Add(primary + hitNormal * 12f);
            candidates.Add(primary + hitNormal * 24f + new Vector3(0f, 0f, 12f));
        }

        // Step backwards along the aim ray from aimPoint toward eye (handles tight corners & props)
        float[] pullbacks = { 24f, 48f, 72f, 104f, 144f, 192f };
        foreach (float back in pullbacks)
        {
            if (aimDist - back < 36f) break;
            Vector3 p = aimPoint - dir * back;
            // Convert from ray/eye height to approximate feet origin
            Vector3 feetGuess = new(p.X, p.Y, p.Z - (hitNormal.Z >= 0.7f ? 0f : hullHeight * 0.45f));
            candidates.Add(feetGuess);
            candidates.Add(new Vector3(p.X, p.Y, p.Z));
        }

        // Try each candidate: validate hull fit -> snap to ground -> verify line-of-sight (no wall phasing!)
        foreach (var rawCandidate in candidates)
        {
            if (TryResolveSafeLanding(rawCandidate, origin, eye, hullHeight, hullMins, hullMaxs, checkMins, checkMaxs, pawn, out var safePos))
            {
                pawn.Teleport(new Vector(safePos.X, safePos.Y, safePos.Z), null, Vector.Zero);
                return true;
            }
        }

        // Step 3: Fallback swept player-hull trace directly from current position toward primary target
        // Slides cleanly up to the exact furthest solid surface the player's bbox can physically reach.
        Vector3 sweepStart = origin + new Vector3(0f, 0f, 4f);
        if (_tracer.TryTraceHull(sweepStart, primary, hullMins, hullMaxs, pawn, out var sweep) && !sweep.StartInSolid)
        {
            Vector3 sweptPos = sweep.EndPos;
            if (sweep.DidHit && sweep.Normal.LengthSquared() > 0.01f)
                sweptPos += Vector3.Normalize(sweep.Normal) * 2f;

            if (TryResolveSafeLanding(sweptPos, origin, eye, hullHeight, hullMins, hullMaxs, checkMins, checkMaxs, pawn, out var safeSwept))
            {
                pawn.Teleport(new Vector(safeSwept.X, safeSwept.Y, safeSwept.Z), null, Vector.Zero);
                return true;
            }
        }

        return false;
    }

    public bool TeleportSafe(CCSPlayerController player, Vector destination, QAngle? angles = null, Vector? velocity = null)
    {
        if (!PlayerStateManager.TryGetAlivePawn(player, out var pawn)) return false;

        Vector3 dest = new(destination.X, destination.Y, destination.Z + 4f);
        if (_tracer.IsAvailable)
        {
            bool isCrouched = (pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0;
            float hullHeight = isCrouched ? CrouchedHeight : StandingHeight;
            var checkMins = new Vector3(-(PlayerHalfWidth + HullMargin), -(PlayerHalfWidth + HullMargin), HullMargin);
            var checkMaxs = new Vector3(PlayerHalfWidth + HullMargin, PlayerHalfWidth + HullMargin, hullHeight + HullMargin);

            Vector3[] offsets =
            {
                Vector3.Zero,
                new(0f, 0f, 12f),
                new(0f, 0f, 28f),
                new(24f, 0f, 8f),
                new(-24f, 0f, 8f),
                new(0f, 24f, 8f),
                new(0f, -24f, 8f),
                new(24f, 24f, 12f),
                new(-24f, -24f, 12f)
            };

            bool foundClear = false;
            foreach (var off in offsets)
            {
                Vector3 test = dest + off;
                if (IsHullClear(test, checkMins, checkMaxs, pawn))
                {
                    dest = test;
                    foundClear = true;
                    break;
                }
            }

            if (!foundClear)
                return false;
        }

        pawn.Teleport(new Vector(dest.X, dest.Y, dest.Z), angles, velocity ?? Vector.Zero);
        return true;
    }

    public bool RespawnPlayer(CCSPlayerController player, Vector? atOrigin = null, IEnumerable<string>? restoreWeapons = null)
    {
        if (player == null || !player.IsValid) return false;
        if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist) return false;

        player.Respawn();

        int slot = player.Slot;
        var weaponCopy = restoreWeapons?.ToList();
        var originCopy = atOrigin != null ? new Vector(atOrigin.X, atOrigin.Y, atOrigin.Z + 12f) : null;

        Server.NextFrame(() =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (!PlayerStateManager.TryGetAlivePawn(p, out var pawn)) return;

            if (originCopy != null)
            {
                if (!TeleportSafe(p!, originCopy, null, Vector.Zero))
                    pawn.Teleport(originCopy, null, Vector.Zero);
            }

            if (weaponCopy != null && weaponCopy.Count > 0)
                _weapons.RestoreWeapons(p!, weaponCopy);
        });

        return true;
    }

    public Vector? GetRandomTeamSpawn(CsTeam team)
    {
        string entityName = team == CsTeam.Terrorist ? "info_player_terrorist" : "info_player_counterterrorist";
        var spawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(entityName)
            .Where(s => s != null && s.IsValid && s.AbsOrigin != null)
            .ToList();

        if (spawns.Count == 0) return null;
        var pick = spawns[_rng.Next(spawns.Count)];
        return pick.AbsOrigin != null
            ? new Vector(pick.AbsOrigin.X, pick.AbsOrigin.Y, pick.AbsOrigin.Z + 10f)
            : null;
    }

    private bool TryResolveSafeLanding(
        Vector3 candidate,
        Vector3 startOrigin,
        Vector3 startEye,
        float hullHeight,
        Vector3 hullMins,
        Vector3 hullMaxs,
        Vector3 checkMins,
        Vector3 checkMaxs,
        CCSPlayerPawn pawn,
        out Vector3 resolved)
    {
        resolved = candidate;

        // 1. Check the candidate hull is completely free of walls, props, doors, and players
        if (!IsHullClear(candidate, checkMins, checkMaxs, pawn))
            return false;

        // 2. Snap down gently onto a floor if one exists within FloorSnapDistance below
        Vector3 downEnd = candidate - new Vector3(0f, 0f, FloorSnapDistance);
        if (_tracer.TryTraceHull(candidate, downEnd, hullMins, hullMaxs, pawn, out var floorTrace)
            && !floorTrace.StartInSolid
            && floorTrace.DidHit
            && floorTrace.Normal.Z >= 0.65f)
        {
            Vector3 snapped = new(floorTrace.EndPos.X, floorTrace.EndPos.Y, floorTrace.EndPos.Z + 1.0f);
            if (IsHullClear(snapped, checkMins, checkMaxs, pawn))
                resolved = snapped;
        }

        // 3. Enforce minimum travel distance so we don't burn cooldown for a 0-distance teleport
        if (Vector3.Distance(resolved, startOrigin) < MinTeleportDistance)
            return false;

        // 4. Verify clear line-of-sight from player's eyes to landing center (prevents phasing through thin walls/doors)
        Vector3 landingCenter = resolved + new Vector3(0f, 0f, hullHeight * 0.55f);
        if (_tracer.TryTraceLine(startEye, landingCenter, pawn, out var losTrace))
        {
            if (losTrace.StartInSolid || losTrace.Fraction < 0.96f)
                return false;
        }
        else
        {
            return false;
        }

        // 5. Also verify clear line-of-sight to landing head so player can't clip above a low ceiling/vent
        Vector3 landingHead = resolved + new Vector3(0f, 0f, hullHeight - 4f);
        if (_tracer.TryTraceLine(landingCenter, landingHead, pawn, out var headTrace)
            && (headTrace.StartInSolid || headTrace.DidHit))
        {
            return false;
        }

        return true;
    }

    private bool IsHullClear(Vector3 feetOrigin, Vector3 mins, Vector3 maxs, CCSPlayerPawn pawn)
    {
        // Trace a tiny vertical epsilon so Source 2 evaluates both static & dynamic overlaps at feetOrigin
        Vector3 tinyEnd = feetOrigin + new Vector3(0f, 0f, 0.25f);
        if (!_tracer.TryTraceHull(feetOrigin, tinyEnd, mins, maxs, pawn, out var check))
            return false;

        return !check.StartInSolid && !check.DidHit;
    }

    private static Vector3 ComputeSurfaceAdjustedOrigin(Vector3 hitPoint, Vector3 normal, bool didHit, float hullHeight)
    {
        if (!didHit)
            return hitPoint - new Vector3(0f, 0f, hullHeight * 0.5f);

        // Floor or walkable ramp
        if (normal.Z >= 0.7f)
            return hitPoint + new Vector3(0f, 0f, 2.0f);

        // Ceiling / underside of overhang
        if (normal.Z <= -0.7f)
            return hitPoint - new Vector3(0f, 0f, hullHeight + 4.0f);

        // Vertical wall or prop side: push out horizontally by player radius + clearance margin
        float xyLen = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y);
        Vector3 horizNormal = xyLen > 0.01f
            ? new Vector3(normal.X / xyLen, normal.Y / xyLen, 0f)
            : new Vector3(normal.X, normal.Y, 0f);

        return hitPoint + horizNormal * (PlayerHalfWidth + 6.0f) - new Vector3(0f, 0f, hullHeight * 0.45f);
    }

    private static Vector3 GetEyePosition(CCSPlayerPawn pawn)
    {
        var origin = pawn.AbsOrigin!;
        float viewZ = pawn.ViewOffset.Z;
        if (!float.IsFinite(viewZ) || viewZ < 10f)
            viewZ = (pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0 ? 46f : 64f;

        return new Vector3(origin.X, origin.Y, origin.Z + viewZ);
    }

    private static Vector3 GetAimDirection(QAngle angles)
    {
        float pitchRad = angles.X * (MathF.PI / 180f);
        float yawRad = angles.Y * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitchRad);

        return Vector3.Normalize(new Vector3(
            cosPitch * MathF.Cos(yawRad),
            cosPitch * MathF.Sin(yawRad),
            -MathF.Sin(pitchRad)));
    }

    private bool TeleportManagedFallback(CCSPlayerController player, CCSPlayerPawn pawn, float maxDistance)
    {
        // Conservative pure-managed ray-march fallback (zero native pointers):
        // - Clamped to short conservative blink distance (<= 384u)
        // - Refuses steep downward/upward angles that risk floor/ceiling clipping
        // - Ray-marches in 32u steps from player origin and rejects any candidate overlapping alive players or physics/dynamic props
        var eyeAngles = pawn.EyeAngles;
        if (!float.IsFinite(eyeAngles.X) || !float.IsFinite(eyeAngles.Y))
            return false;

        // Refuse steep vertical pitches where blind projection is most likely to clip into floors/ceilings
        if (eyeAngles.X > 40f || eyeAngles.X < -40f)
            return false;

        Vector3 origin = ToVec3(pawn.AbsOrigin!);
        float clampedPitch = Math.Clamp(eyeAngles.X, -25f, 15f) * (MathF.PI / 180f);
        float yawRad = eyeAngles.Y * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(clampedPitch);
        Vector3 dir = Vector3.Normalize(new Vector3(
            cosPitch * MathF.Cos(yawRad),
            cosPitch * MathF.Sin(yawRad),
            -MathF.Sin(clampedPitch)));

        float safeMaxDist = Math.Clamp(maxDistance, 64f, 384f);

        // Snapshot nearby alive players & props once so we never land inside them
        var obstacles = CollectManagedObstacles(player);

        const float stepSize = 32f;
        Vector3? bestDest = null;

        for (float d = stepSize; d <= safeMaxDist; d += stepSize)
        {
            Vector3 candidate = new(
                origin.X + dir.X * d,
                origin.Y + dir.Y * d,
                origin.Z + MathF.Max(8f, dir.Z * d + 8f));

            if (HitsManagedObstacle(candidate, obstacles))
                break; // Stop immediately at the first player/prop obstacle along the ray

            if (d >= 64f)
                bestDest = candidate;
        }

        if (!bestDest.HasValue)
            return false;

        pawn.Teleport(new Vector(bestDest.Value.X, bestDest.Value.Y, bestDest.Value.Z), null, Vector.Zero);
        return true;
    }

    private static List<(Vector3 Pos, float Radius, float Height)> CollectManagedObstacles(CCSPlayerController self)
    {
        var list = new List<(Vector3, float, float)>();

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.Slot == self.Slot || !p.PawnIsAlive) continue;
            var pos = p.PlayerPawn?.Value?.AbsOrigin;
            if (pos != null)
                list.Add((new Vector3(pos.X, pos.Y, pos.Z), 38f, 76f));
        }

        foreach (var prop in Utilities.FindAllEntitiesByDesignerName<CBreakableProp>("prop_physics_override"))
        {
            if (prop != null && prop.IsValid && prop.AbsOrigin != null)
                list.Add((new Vector3(prop.AbsOrigin.X, prop.AbsOrigin.Y, prop.AbsOrigin.Z), 40f, 64f));
        }

        foreach (var prop in Utilities.FindAllEntitiesByDesignerName<CDynamicProp>("prop_dynamic_override"))
        {
            if (prop != null && prop.IsValid && prop.AbsOrigin != null)
                list.Add((new Vector3(prop.AbsOrigin.X, prop.AbsOrigin.Y, prop.AbsOrigin.Z), 40f, 64f));
        }

        return list;
    }

    private static bool HitsManagedObstacle(Vector3 candidate, List<(Vector3 Pos, float Radius, float Height)> obstacles)
    {
        foreach (var (pos, radius, height) in obstacles)
        {
            float dx = candidate.X - pos.X;
            float dy = candidate.Y - pos.Y;
            if (dx * dx + dy * dy < radius * radius)
            {
                float dz = candidate.Z - pos.Z;
                if (dz > -64f && dz < height)
                    return true;
            }
        }
        return false;
    }

    private static Vector3 ToVec3(Vector v) => new(v.X, v.Y, v.Z);
}
