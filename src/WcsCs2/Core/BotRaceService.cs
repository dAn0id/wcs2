using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using WcsCs2.Config;
using WcsCs2.Skills;

namespace WcsCs2.Core;

public sealed class BotRaceService
{
    private readonly WcsConfig _config;
    private readonly RaceRegistry _registry;
    private readonly ProgressionService _progression;
    private readonly Random _rng;
    private ISkillExecutionEngine? _engine;

    public BotRaceService(WcsConfig config, RaceRegistry registry, ProgressionService progression, Random rng)
    {
        _config = config;
        _registry = registry;
        _progression = progression;
        _rng = rng;
    }

    public void BindEngine(ISkillExecutionEngine engine) => _engine = engine;

    public void SetupBotOnSpawn(CCSPlayerController bot, PlayerSession session)
    {
        if (!bot.IsValid || !bot.IsBot || bot.IsHLTV) return;

        if (_config.BotRandomRace && _registry.Races.Count > 0)
        {
            var eligible = _config.BotIgnoreLevelRequirement
                ? _registry.Races.ToList()
                : _registry.Races.Where(r => r.RequiredLevel == 0).ToList();

            if (eligible.Count == 0) eligible = _registry.Races.ToList();
            var pick = eligible[_rng.Next(eligible.Count)];
            session.CurrentRaceKey = pick.Key;
            var prog = session.GetOrCreateRaceProgress(pick.Key);

            // Give bots reasonable skill levels so bot fights showcase WCS abilities
            if (prog.Level <= 1 && prog.Skills.Count == 0)
            {
                int skillSumCap = Math.Max(4, pick.ComputedMaxLevel);
                int cap = pick.MaxLevel > 0 ? Math.Min(pick.MaxLevel, skillSumCap) : skillSumCap;
                int targetLvl = Math.Min(cap, _rng.Next(4, 14));
                prog.Level = targetLvl;
                prog.Unused = targetLvl;
            }

            // Auto-spend bot skill points randomly
            AutoSpendBotSkills(session, pick);
            _progression.ApplyClanTag(bot, session);
        }
    }

    private void AutoSpendBotSkills(PlayerSession session, RaceDefinition race)
    {
        var prog = session.CurrentProgress;
        int guard = 100;
        while (prog.Unused > 0 && guard-- > 0)
        {
            var upgradable = race.Skills
                .Where(s => session.GetSkillLevel(s.Key) < s.MaxLevel && prog.Level >= s.RequiredLevel)
                .ToList();
            if (upgradable.Count == 0) break;
            var s = upgradable[_rng.Next(upgradable.Count)];
            prog.Unused--;
            session.SetSkillLevel(s.Key, session.GetSkillLevel(s.Key) + 1);
        }
    }

    public void TryTriggerBotAbilities()
    {
        if (_engine == null || _config.BotAbilityChance <= 0f) return;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV || !p.PawnIsAlive) continue;
            if (_rng.NextDouble() > _config.BotAbilityChance) continue;

            var session = _progression.GetTypedSession(p);
            if (session == null) continue;

            if (_rng.NextDouble() < 0.7)
                _engine.TryActivateUltimate(p, session);
            else
                _engine.TryActivateAbility(p, session);
        }
    }
}
