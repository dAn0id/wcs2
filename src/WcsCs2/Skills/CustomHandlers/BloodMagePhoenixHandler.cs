using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Skills.Primitives;

namespace WcsCs2.Skills.CustomHandlers;

public sealed class BloodMagePhoenixHandler : ISkillHandler, IHandlesOwnChance
{
    private static readonly float[] DefaultChances = { 15f, 30f, 45f, 60f, 75f, 90f };

    private readonly RoundTracker _roundTracker;
    private readonly Dictionary<CsTeam, int> _teamPhoenixPool = new();
    private readonly HashSet<ulong> _usedPhoenixThisRound = new();

    internal BloodMagePhoenixHandler(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Key => "blood_mage_phoenix";

    public void Execute(SkillContext ctx)
    {
        if (ctx.Trigger != SkillTrigger.PlayerSpawn)
            return;

        var owner = ctx.Owner;
        if (!SkillHelpers.IsAlive(owner) || !SkillHelpers.IsPlayableTeam(owner.Team))
            return;

        float chance = ctx.Skill is { Chances.Count: > 0 }
            ? ctx.Skill.GetChance(ctx.SkillLevel)
            : DefaultChances[Math.Clamp(Math.Max(1, ctx.SkillLevel) - 1, 0, DefaultChances.Length - 1)];

        bool ffa = ctx.Services.Sessions.IsFriendlyFireAllowed();
        if (ffa)
        {
            ctx.Services.State.SetCustomData(owner, SkillStateKeys.PhoenixFfa, false);
            if (SkillHelpers.RollChance(ctx.Services.Rng, chance) && ctx.Services.Rng.Next(0, 3) == 0)
            {
                ctx.Services.State.SetCustomData(owner, SkillStateKeys.PhoenixFfa, true);
                ctx.Services.Effects.PrintChat(owner, "Phoenix activated! You will revive at your death spot.");
            }
            return;
        }

        // Team mode: once per round per Blood Mage (matches has_used_phoenix)
        ulong key = owner.SteamID != 0 ? owner.SteamID : (ulong)(owner.Slot + 1);
        if (!_usedPhoenixThisRound.Add(key))
            return;

        if (SkillHelpers.RollChance(ctx.Services.Rng, chance))
        {
            _teamPhoenixPool.TryGetValue(owner.Team, out int current);
            _teamPhoenixPool[owner.Team] = current + 1;
            ctx.Services.Effects.PrintChat(owner, $"Phoenix activated! Your team now has {current + 1} revival charge(s).");
        }
    }

    /// <summary>
    /// Called by SkillExecutionEngine on EVERY player death so a Blood Mage's team Phoenix pool
    /// can revive any fallen teammate regardless of the teammate's race.
    /// </summary>
    internal void TryConsumeTeamPhoenix(SkillContext deathCtx)
    {
        var victim = deathCtx.Owner;
        if (!_roundTracker.IsRoundActive || !SkillHelpers.IsValidPlayer(victim))
            return;
        if (!SkillHelpers.IsPlayableTeam(victim.Team))
            return;

        // Don't waste a Phoenix charge if another skill (e.g. Orcish Reincarnation) is already reviving this player
        if (deathCtx.Services.State.GetCustomData<bool>(victim, SkillStateKeys.PendingRespawn))
            return;

        bool ffa = deathCtx.Services.Sessions.IsFriendlyFireAllowed();
        if (ffa)
        {
            if (!deathCtx.Services.State.GetCustomData<bool>(victim, SkillStateKeys.PhoenixFfa))
                return;

            deathCtx.Services.State.SetCustomData(victim, SkillStateKeys.PhoenixFfa, false);
            QueuePhoenixRespawn(deathCtx.Services, victim, atDeathOrigin: true);
            return;
        }

        if (_teamPhoenixPool.TryGetValue(victim.Team, out int charges) && charges > 0)
        {
            _teamPhoenixPool[victim.Team] = charges - 1;
            QueuePhoenixRespawn(deathCtx.Services, victim, atDeathOrigin: false);
        }
    }

    private void QueuePhoenixRespawn(ISkillServices services, CCSPlayerController player, bool atDeathOrigin)
    {
        var deathOrigin = atDeathOrigin ? SkillHelpers.CloneOrigin(player) : null;
        int capturedRound = _roundTracker.CurrentRoundId;

        services.State.SetCustomData(player, SkillStateKeys.PendingRespawn, true);
        services.Effects.PrintChat(player, "Phoenix — You will be resurrected in 3 seconds!");
        services.Effects.PrintCenter(player, "Phoenix revival in 3...");

        if (deathOrigin != null)
        {
            services.Effects.DrawBeamRing(deathOrigin, 120f, Color.FromArgb(200, 10, 213, 255), 3.0f, 3.0f, 18, 15f);
        }

        services.AddTimer(1.0f, () =>
        {
            if (capturedRound != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive) return;
            if (!SkillHelpers.IsValidPlayer(player) || SkillHelpers.IsAlive(player)) return;
            services.Effects.PrintCenter(player, "Phoenix revival in 2...");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        services.AddTimer(2.0f, () =>
        {
            if (capturedRound != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive) return;
            if (!SkillHelpers.IsValidPlayer(player) || SkillHelpers.IsAlive(player)) return;
            services.Effects.PrintCenter(player, "Phoenix revival in 1...");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        services.AddTimer(3.0f, () =>
        {
            if (SkillHelpers.IsValidPlayer(player))
                services.State.SetCustomData(player, SkillStateKeys.PendingRespawn, false);

            if (capturedRound != _roundTracker.CurrentRoundId || !_roundTracker.IsRoundActive)
                return;
            if (!SkillHelpers.IsValidPlayer(player) || SkillHelpers.IsAlive(player))
                return;
            if (!SkillHelpers.IsPlayableTeam(player.Team))
                return;

            if (services.Teleport.RespawnPlayer(player, deathOrigin))
            {
                services.Effects.PrintCenter(player, "Resurrected by Phoenix!");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public void OnRoundStart()
    {
        _teamPhoenixPool.Clear();
        _usedPhoenixThisRound.Clear();
    }

    public void OnRoundEnd()
    {
        _teamPhoenixPool.Clear();
        _usedPhoenixThisRound.Clear();
    }
}
