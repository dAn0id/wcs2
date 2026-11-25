using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Skills.Primitives;

namespace WCS2.Skills.CustomHandlers;

public sealed class ShadowHunterWardHandler : ISkillHandler
{
    private static readonly float[] DefaultDurations = { 10f, 12f, 13f, 14f, 15f };
    private static readonly float[] DefaultDamages = { 10f, 13f, 16f, 18f, 20f };
    private static readonly float[] DefaultRadii = { 150f, 155f, 160f, 165f, 175f };
    private const int DefaultChargesPerRound = 3;

    private readonly RoundTracker _roundTracker;
    private readonly List<ActiveWard> _activeWards = new();
    private int _nextWardId = 1;

    private sealed class ActiveWard
    {
        public int Id;
        public required ISkillServices Services;
        public required CCSPlayerController Owner;
        public ulong OwnerKey;
        public CsTeam OwnerTeam;
        public required Vector Origin;
        public float Radius;
        public float Damage;
        public int RemainingTicks; // 0.25s ticks
        public int TickCounter;
        public int RoundId;
        public CounterStrikeSharp.API.Modules.Timers.Timer? Timer;
    }

    internal ShadowHunterWardHandler(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Key => "shadow_hunter_ward";

    public void InitializeSpawnCharges(CCSPlayerController player, ISkillServices services, int charges = DefaultChargesPerRound)
    {
        if (!SkillHelpers.IsValidPlayer(player)) return;
        services.State.SetCustomData(player, SkillStateKeys.WardCharges, charges);
    }

    public void Execute(SkillContext ctx)
    {
        var owner = ctx.Owner;
        if (ctx.Trigger == SkillTrigger.PlayerSpawn)
        {
            int charges = ctx.Skill?.Actions.FirstOrDefault()?.GetLevelInt("charges", ctx.SkillLevel, DefaultChargesPerRound)
                          ?? DefaultChargesPerRound;
            InitializeSpawnCharges(owner, ctx.Services, charges);
            return;
        }

        if (ctx.Trigger != SkillTrigger.PlayerAbility)
            return;

        if (!SkillHelpers.IsAlive(owner) || !SkillHelpers.IsPlayableTeam(owner.Team))
        {
            ctx.Activated = false;
            return;
        }

        int remaining = ctx.Services.State.GetCustomData<int>(owner, SkillStateKeys.WardCharges);
        if (remaining <= 0)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "You have no Serpent Wards remaining this round!");
            return;
        }

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin == null)
        {
            ctx.Activated = false;
            return;
        }

        remaining--;
        ctx.Services.State.SetCustomData(owner, SkillStateKeys.WardCharges, remaining);

        int lvlIdx = Math.Clamp(Math.Max(1, ctx.SkillLevel) - 1, 0, DefaultDurations.Length - 1);
        var action = ctx.Skill?.Actions.FirstOrDefault();

        float duration = action != null && SkillHelpers.HasKey(action, "duration")
            ? action.GetLevelFloat("duration", ctx.SkillLevel, DefaultDurations[lvlIdx])
            : DefaultDurations[lvlIdx];

        float damage = action != null && SkillHelpers.HasKey(action, "damage")
            ? action.GetLevelFloat("damage", ctx.SkillLevel, DefaultDamages[lvlIdx])
            : DefaultDamages[lvlIdx];

        float radius = action != null && SkillHelpers.HasKey(action, "radius")
            ? action.GetLevelFloat("radius", ctx.SkillLevel, DefaultRadii[lvlIdx])
            : DefaultRadii[lvlIdx];

        var ward = new ActiveWard
        {
            Id = _nextWardId++,
            Services = ctx.Services,
            Owner = owner,
            OwnerKey = GetOwnerKey(owner),
            OwnerTeam = owner.Team,
            Origin = origin,
            Radius = radius,
            Damage = damage,
            RemainingTicks = Math.Max(4, (int)Math.Round(duration / 0.25f)),
            TickCounter = 0,
            RoundId = _roundTracker.CurrentRoundId
        };

        _activeWards.Add(ward);
        DrawWardVisuals(ward);

        ward.Timer = ctx.Services.AddTimer(0.25f, () => TickWard(ward), TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        ctx.Services.Effects.PrintChat(owner, $"Serpent Ward placed! ({remaining} remaining this round)");
        ctx.Activated = true;
    }

    private void TickWard(ActiveWard ward)
    {
        if (ward.RoundId != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive)
        {
            DestroyWard(ward);
            return;
        }

        if (!SkillHelpers.IsValidPlayer(ward.Owner) || ward.Owner.Team != ward.OwnerTeam)
        {
            DestroyWard(ward);
            return;
        }

        if (ward.RemainingTicks <= 0)
        {
            DestroyWard(ward);
            return;
        }

        ward.RemainingTicks--;
        ward.TickCounter++;

        bool ffa = ward.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(ward.OwnerTeam);
        var enemies = ward.Services.Combat.GetPlayersInRadius(ward.Origin, ward.Radius, teamFilter: enemyTeam, exclude: ward.Owner, aliveOnly: true);

        string slowSource = $"serpent_ward_{ward.Id}";
        bool isDamageTick = (ward.TickCounter % 4) == 0; // Every 1.0s

        if (isDamageTick)
        {
            DrawWardVisuals(ward);
        }

        var wardTop = new Vector(ward.Origin.X, ward.Origin.Y, ward.Origin.Z + 80f);
        foreach (var enemy in enemies)
        {
            if (!SkillHelpers.IsAlive(enemy)) continue;

            // Apply -30% slow while inside ward radius; short duration auto-expires upon leaving
            ward.Services.State.SetSpeed(enemy, -0.30f, additive: true, duration: 0.4f, source: slowSource);

            if (isDamageTick)
            {
                ward.Services.Combat.DealDamage(enemy, ward.Owner, ward.Damage, "world", ignoreArmor: false, isSkillDamage: true);
                var enemyPos = SkillHelpers.CloneOrigin(enemy);
                if (enemyPos != null)
                {
                    ward.Services.Effects.DrawBeamPoints(
                        wardTop,
                        new Vector(enemyPos.X, enemyPos.Y, enemyPos.Z + 38f),
                        Color.FromArgb(255, 255, 160, 50), 2.5f, 0.4f);
                }
            }
        }
    }

    private static void DrawWardVisuals(ActiveWard ward)
    {
        Color pillarColor = ward.OwnerTeam == CsTeam.Terrorist
            ? Color.FromArgb(255, 255, 60, 40)
            : Color.FromArgb(255, 60, 140, 255);

        var bottom = new Vector(ward.Origin.X, ward.Origin.Y, ward.Origin.Z + 4f);
        var top = new Vector(ward.Origin.X, ward.Origin.Y, ward.Origin.Z + 120f);
        ward.Services.Effects.DrawBeamPoints(bottom, top, pillarColor, 4.0f, 1.05f);
        ward.Services.Effects.DrawBeamRing(ward.Origin, ward.Radius, Color.FromArgb(220, 255, 150, 70), 2.5f, 1.05f, 16, 12f);
    }

    private void DestroyWard(ActiveWard ward)
    {
        try { ward.Timer?.Kill(); } catch { /* ignore already-killed timer */ }
        ward.Timer = null;
        _activeWards.Remove(ward);
    }

    public void OnCleanup(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false)
    {
        // On disconnect or race change away from shadow_hunter (or team switch), destroy owned wards.
        // Note: on normal death, wards already placed remain active for their duration unless team/race/round changes.
        bool disconnected = isDisconnect || !SkillHelpers.IsValidPlayer(player);
        bool changedRace = !string.Equals(session.CurrentRaceKey, "shadow_hunter", StringComparison.OrdinalIgnoreCase);

        if (!disconnected && !changedRace)
            return;

        ulong key = session.SteamId != 0 ? session.SteamId : (ulong)(session.Slot + 1);
        for (int i = _activeWards.Count - 1; i >= 0; i--)
        {
            if (_activeWards[i].OwnerKey == key)
                DestroyWard(_activeWards[i]);
        }
    }

    public void OnRoundStart() => DestroyAllWards();
    public void OnRoundEnd() => DestroyAllWards();

    private void DestroyAllWards()
    {
        for (int i = _activeWards.Count - 1; i >= 0; i--)
        {
            DestroyWard(_activeWards[i]);
        }
    }

    private static ulong GetOwnerKey(CCSPlayerController player)
        => player.SteamID != 0 ? player.SteamID : (ulong)(player.Slot + 1);
}
