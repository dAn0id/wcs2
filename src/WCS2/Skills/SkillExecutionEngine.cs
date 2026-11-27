using CounterStrikeSharp.API.Core;
using WCS2.Config;
using WCS2.Skills.CustomHandlers;
using WCS2.Skills.Primitives;

namespace WCS2.Skills;

public sealed class SkillExecutionEngine : ISkillExecutionEngine
{
    private readonly ISkillServices _services;
    private readonly RoundTracker _roundTracker = new();
    private readonly EvasionHistory _evasionHistory = new();
    private readonly Dictionary<string, ISkillPrimitive> _primitives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ISkillHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    private readonly BloodMagePhoenixHandler _phoenixHandler;
    private readonly ShadowHunterWardHandler _wardHandler;
    private readonly SuccubusSkullsHandler _succubusHandler;
    private readonly DefiladedDestructorHandler _destructorHandler;

    public SkillExecutionEngine(ISkillServices services)
    {
        _services = services;

        // Register all composable Phase 1 skill primitives
        RegisterPrimitive(new HealthPrimitive());
        RegisterPrimitive(new SpeedPrimitive());
        RegisterPrimitive(new GravityPrimitive());
        RegisterPrimitive(new LongjumpPrimitive());
        RegisterPrimitive(new InvisibilityPrimitive());
        RegisterPrimitive(new EvasionPrimitive(_evasionHistory));
        RegisterPrimitive(new DamageAbsorbPrimitive());
        RegisterPrimitive(new UltimateImmunityPrimitive());
        RegisterPrimitive(new RegenPrimitive(_roundTracker));
        RegisterPrimitive(new SetUltimateCooldownPrimitive());
        RegisterPrimitive(new GiveLevelsPrimitive());
        RegisterPrimitive(new PlayerScalePrimitive());
        RegisterPrimitive(new InfiniteAmmoPrimitive());
        RegisterPrimitive(new AuraPrimitive(_roundTracker));

        RegisterPrimitive(new BonusDamagePrimitive());
        RegisterPrimitive(new ReflectDamagePrimitive());
        RegisterPrimitive(new VampiricPrimitive());
        RegisterPrimitive(new AoEDamagePrimitive());
        RegisterPrimitive(new BurnDotPrimitive(_roundTracker));
        RegisterPrimitive(new StealCashPrimitive());
        RegisterPrimitive(new CashToHealthPrimitive());
        RegisterPrimitive(new RandomEnemyDrainPrimitive());
        RegisterPrimitive(new CheatDeathPrimitive());
        RegisterPrimitive(new SplashDamagePrimitive());
        RegisterPrimitive(new AimExplosionPrimitive());

        RegisterPrimitive(new FreezePrimitive());
        RegisterPrimitive(new AoEFreezePrimitive());
        RegisterPrimitive(new PushPrimitive());
        RegisterPrimitive(new TeleportPrimitive());
        RegisterPrimitive(new TeleportDashPrimitive());
        RegisterPrimitive(new AirDashPrimitive());
        RegisterPrimitive(new RespawnPrimitive(_roundTracker));
        RegisterPrimitive(new ReviveTeammatePrimitive(_roundTracker));
        RegisterPrimitive(new ShakePrimitive());
        RegisterPrimitive(new FadePrimitive());

        RegisterPrimitive(new GiveWeaponPrimitive());
        RegisterPrimitive(new DropWeaponPrimitive());
        RegisterPrimitive(new SetClipAmmoPrimitive());
        RegisterPrimitive(new InstantRestockPrimitive(_roundTracker));
        RegisterPrimitive(new ExplosiveMolotovPrimitive());
        RegisterPrimitive(new BeamPrimitive());
        RegisterPrimitive(new BeamRingPrimitive());
        RegisterPrimitive(new SoundPrimitive());
        RegisterPrimitive(new MessagePrimitive());

        // Register all bespoke Phase 1 custom handlers
        _phoenixHandler = new BloodMagePhoenixHandler(_roundTracker);
        _wardHandler = new ShadowHunterWardHandler(_roundTracker);
        _succubusHandler = new SuccubusSkullsHandler();
        _destructorHandler = new DefiladedDestructorHandler(_roundTracker);

        RegisterHandler(new ArchmageLiftOffHandler());
        RegisterHandler(_phoenixHandler);
        RegisterHandler(_wardHandler);
        RegisterHandler(new WardenRestorationHandler());
        RegisterHandler(_succubusHandler);
        RegisterHandler(_destructorHandler);
    }

    public IReadOnlyCollection<string> RegisteredPrimitives => _primitives.Keys;
    public IReadOnlyCollection<string> RegisteredHandlers => _handlers.Keys;

    private void RegisterPrimitive(ISkillPrimitive primitive)
    {
        _primitives[primitive.Type] = primitive;
    }

    private void RegisterHandler(ISkillHandler handler)
    {
        _handlers[handler.Key] = handler;
    }

    public void TriggerEvent(SkillTrigger trigger, SkillContext ctx)
    {
        if (!SkillHelpers.IsValidPlayer(ctx.Owner))
            return;

        // On PlayerVictim pre-damage: process passive evasion, damage absorption, and Spiked Carapace reflection
        if (trigger == SkillTrigger.PlayerVictim)
        {
            if (ctx.OwnerSession.SkillsDisabled)
                return;

            // Avoid friendly fire & self-damage procs unless FFA enabled
            if (ctx.Attacker != null && !IsValidCombatPair(ctx.Attacker, ctx.Owner))
                return;

            if (ProcessVictimPassives(ctx))
                return; // Damage was completely evaded — skip victim skills
        }

        if (trigger == SkillTrigger.PlayerAttacker)
        {
            if (ctx.OwnerSession.SkillsDisabled || ctx.IsSkillDamage)
                return;

            if (!SkillHelpers.IsAlive(ctx.Owner) || !SkillHelpers.IsAlive(ctx.Victim))
                return;

            if (!IsValidCombatPair(ctx.Owner, ctx.Victim!))
                return;
        }

        if (trigger == SkillTrigger.PlayerVictim && ctx.IsSkillDamage)
        {
            // Prevent infinite reflection loops on skill-sourced damage
            return;
        }

        if (trigger == SkillTrigger.PlayerSpawn && !SkillHelpers.IsAlive(ctx.Owner))
            return;

        if (trigger == SkillTrigger.PlayerDeath)
        {
            // Save evasion count in engine-owned history so ResetTransientState doesn't wipe it before next spawn
            int evaded = _services.State.GetCustomData<int>(ctx.Owner, SkillStateKeys.EvasionCount);
            _evasionHistory.Record(ctx.Owner, ctx.OwnerSession, evaded);
        }

        // Execute matching skills on the owner's active race
        if (!ctx.OwnerSession.SkillsDisabled || trigger == SkillTrigger.PlayerDeath)
        {
            foreach (var skill in ctx.Race.Skills)
            {
                if (skill.Trigger != trigger)
                    continue;

                int level = ctx.OwnerSession.GetSkillLevel(skill.Key);
                if (level <= 0)
                    continue;

                // Weapon filter check (e.g. knife, hegrenade)
                if (skill.Weapons.Count > 0 && !SkillHelpers.MatchesWeaponFilter(_services.Weapons, skill.Weapons, ctx.WeaponName))
                    continue;

                // Cooldown check
                if (ctx.OwnerSession.IsOnCooldown(skill.Key, out _))
                    continue;

                // Prepare context for this skill
                ctx.Skill = skill;
                ctx.SkillLevel = level;
                ctx.Activated = true;

                // Resolve custom handler (if configured)
                ISkillHandler? handler = null;
                if (!string.IsNullOrWhiteSpace(skill.CustomHandler))
                    _handlers.TryGetValue(skill.CustomHandler, out handler);

                // Chance roll (skip if custom handler explicitly manages its own chance/state guard)
                if (handler is not IHandlesOwnChance)
                {
                    float chance = skill.GetChance(level);
                    if (!SkillHelpers.RollChance(_services.Rng, chance))
                        continue;
                }

                if (handler != null)
                {
                    handler.Execute(ctx);
                }
                else
                {
                    ExecuteActionList(ctx, skill.Actions);
                }

                float cd = skill.GetCooldown(level);
                if (cd > 0f && ctx.Activated)
                {
                    ctx.OwnerSession.SetCooldown(skill.Key, cd);
                }
            }
        }

        // Global Blood Mage Phoenix team revival check on EVERY player death (regardless of victim's race)
        if (trigger == SkillTrigger.PlayerDeath)
        {
            _phoenixHandler.TryConsumeTeamPhoenix(ctx);
        }
    }

    public void TriggerRaceLifecycleActions(SkillTrigger trigger, CCSPlayerController player, IPlayerSession session, RaceDefinition race)
    {
        if (!SkillHelpers.IsValidPlayer(player))
            return;

        if (trigger == SkillTrigger.PlayerSpawn)
        {
            if (!SkillHelpers.IsAlive(player))
                return;

            // Enforce declarative weapon allowlist / denylist
            if (race.AllowedWeapons.Count > 0)
            {
                _services.Weapons.RestrictAllExcept(player, race.AllowedWeapons);
            }
            else if (race.RestrictedWeapons.Count > 0)
            {
                _services.Weapons.RestrictWeapons(player, race.RestrictedWeapons);
            }

            // Ensure Shadow Hunter Serpent Ward charges initialize on spawn even if custom_handler is only on skill
            if (race.Skills.Any(s => string.Equals(s.CustomHandler, _wardHandler.Key, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(race.CustomHandler, _wardHandler.Key, StringComparison.OrdinalIgnoreCase))
            {
                _wardHandler.InitializeSpawnCharges(player, _services);
            }
        }

        var actions = trigger switch
        {
            SkillTrigger.PlayerSpawn => race.SpawnActions,
            SkillTrigger.PlayerDeath => race.DeathActions,
            SkillTrigger.PlayerRoundStart => race.RoundStartActions,
            SkillTrigger.PlayerRoundEnd => race.RoundEndActions,
            _ => null
        };

        var ctx = new SkillContext
        {
            Services = _services,
            Owner = player,
            OwnerSession = session,
            Race = race,
            Skill = null,
            SkillLevel = 1,
            Trigger = trigger
        };

        if (actions is { Count: > 0 })
        {
            ExecuteActionList(ctx, actions);
        }

        if (!string.IsNullOrWhiteSpace(race.CustomHandler) && _handlers.TryGetValue(race.CustomHandler, out var raceHandler))
        {
            raceHandler.Execute(ctx);
        }
        else if (race.Skills.Any(s => string.Equals(s.CustomHandler, _succubusHandler.Key, StringComparison.OrdinalIgnoreCase)))
        {
            // Safety fallback: ensure Succubus Hunter spawn skull status & 33% death skull loss run
            if (trigger == SkillTrigger.PlayerSpawn)
                _succubusHandler.HandleRaceSpawn(ctx);
            else if (trigger == SkillTrigger.PlayerDeath)
                _succubusHandler.HandleRaceDeath(ctx);
        }
    }

    public void ExecuteItemActions(CCSPlayerController player, IPlayerSession session, ItemDefinition item)
    {
        if (!SkillHelpers.IsValidPlayer(player))
            return;

        if (item.BuyMode == ItemBuyMode.AliveOnly && !SkillHelpers.IsAlive(player))
            return;

        var race = _services.Sessions.GetRace(session.CurrentRaceKey) ?? new RaceDefinition
        {
            Key = session.CurrentRaceKey,
            Name = session.CurrentRaceKey
        };

        var ctx = new SkillContext
        {
            Services = _services,
            Owner = player,
            OwnerSession = session,
            Race = race,
            Skill = null,
            SkillLevel = 1,
            Trigger = item.Trigger
        };

        ExecuteActionList(ctx, item.Actions);
    }

    public bool TryActivateUltimate(CCSPlayerController player, IPlayerSession session)
        => TryActivateActiveSkill(player, session, SkillTrigger.PlayerUltimate, "ultimate");

    public bool TryActivateAbility(CCSPlayerController player, IPlayerSession session)
        => TryActivateActiveSkill(player, session, SkillTrigger.PlayerAbility, "ability");

    private bool TryActivateActiveSkill(CCSPlayerController player, IPlayerSession session, SkillTrigger trigger, string label)
    {
        if (!SkillHelpers.IsAlive(player))
        {
            if (SkillHelpers.IsValidPlayer(player))
                _services.Effects.PrintChat(player, $"You must be alive to use your {label}.");
            return false;
        }

        if (session.SkillsDisabled)
        {
            _services.Effects.PrintChat(player, "Your skills are currently disabled.");
            return false;
        }

        if (trigger == SkillTrigger.PlayerUltimate && session.UltimateBlocked)
        {
            _services.Effects.PrintChat(player, "Your ultimate is currently blocked.");
            return false;
        }

        var race = _services.Sessions.GetRace(session.CurrentRaceKey);
        if (race == null)
            return false;

        var skill = race.Skills.FirstOrDefault(s => s.Trigger == trigger);
        if (skill == null)
        {
            _services.Effects.PrintChat(player, $"Your race ({race.Name}) does not have an {label}.");
            return false;
        }

        int level = session.GetSkillLevel(skill.Key);
        if (level <= 0)
        {
            _services.Effects.PrintChat(player, $"{skill.Name} is not leveled yet! Use !spendskills to upgrade it.");
            return false;
        }

        if (session.IsOnCooldown(skill.Key, out float remaining))
        {
            _services.Effects.PrintChat(player, $"{skill.Name} is on cooldown ({remaining:0.0}s remaining).");
            return false;
        }

        var ctx = new SkillContext
        {
            Services = _services,
            Owner = player,
            OwnerSession = session,
            Race = race,
            Skill = skill,
            SkillLevel = level,
            Trigger = trigger,
            Activated = true
        };

        if (!string.IsNullOrWhiteSpace(skill.CustomHandler) && _handlers.TryGetValue(skill.CustomHandler, out var handler))
        {
            handler.Execute(ctx);
        }
        else
        {
            ExecuteActionList(ctx, skill.Actions);
        }

        float cooldown = skill.GetCooldown(level);
        if (ctx.Activated && cooldown > 0f)
        {
            session.SetCooldown(skill.Key, cooldown);
        }

        return ctx.Activated;
    }

    public void CleanupPlayer(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false)
    {
        if (isDisconnect || !SkillHelpers.IsValidPlayer(player) || !string.Equals(session.CurrentRaceKey, "night_elves", StringComparison.OrdinalIgnoreCase))
        {
            _evasionHistory.Clear(player, session);
        }

        foreach (var handler in _handlers.Values)
        {
            handler.OnCleanup(player, session, isDisconnect);
        }
    }

    public void OnRoundStart()
    {
        _roundTracker.MarkRoundStart();
        foreach (var handler in _handlers.Values)
        {
            handler.OnRoundStart();
        }
    }

    public void OnRoundEnd()
    {
        _roundTracker.MarkRoundEnd();
        foreach (var handler in _handlers.Values)
        {
            handler.OnRoundEnd();
        }
    }

    /// <summary>
    /// Processes passive victim modifiers (Evasion, Damage Absorb, Spiked Carapace mirror) during OnPlayerTakeDamagePre.
    /// Returns true if incoming damage was completely evaded.
    /// </summary>
    private bool ProcessVictimPassives(SkillContext ctx)
    {
        var victim = ctx.Owner;
        if (!SkillHelpers.IsAlive(victim))
            return false;

        // Only evaluate pre-damage evasion/absorb when DamageInfo is present (prevents double-execution if called on Post)
        if (ctx.DamageInfo == null)
            return false;

        // 1. Evasion (Night Elves passive Evasion & Shadow Hunter Big Bad Voodoo 75%)
        float evasionChance = _services.State.GetCustomData<float>(victim, SkillStateKeys.EvasionChance);
        if (evasionChance > 0f && !ctx.IsSkillDamage && SkillHelpers.RollChance(_services.Rng, evasionChance))
        {
            ctx.CancelDamage = true;

            int count = _services.State.GetCustomData<int>(victim, SkillStateKeys.EvasionCount) + 1;
            _services.State.SetCustomData(victim, SkillStateKeys.EvasionCount, count);

            if (_services.State.GetCustomData<bool>(victim, SkillStateKeys.VoodooActive))
            {
                int vCount = _services.State.GetCustomData<int>(victim, SkillStateKeys.VoodooEvaded) + 1;
                _services.State.SetCustomData(victim, SkillStateKeys.VoodooEvaded, vCount);
            }

            _services.Effects.PrintCenter(victim, "Evaded attack!");
            return true;
        }

        // 2. Damage absorption (Crypt Lord Spiked Carapace 5%..20% incoming reduction)
        float absorb = _services.State.GetCustomData<float>(victim, SkillStateKeys.DamageAbsorb);
        if (absorb > 0f)
        {
            ctx.DamageMultiplier *= Math.Clamp(1.0f - absorb, 0.05f, 1.0f);
        }

        // 3. Spiked Carapace passive mirror damage (3..7 damage back to attacker)
        if (!ctx.IsSkillDamage && ctx.Attacker != null && SkillHelpers.IsAlive(ctx.Attacker) && ctx.Attacker.Slot != victim.Slot)
        {
            int mirrorMin = _services.State.GetCustomData<int>(victim, SkillStateKeys.SpikedCarapaceMin);
            int mirrorMax = _services.State.GetCustomData<int>(victim, SkillStateKeys.SpikedCarapaceMax);
            if (mirrorMax > 0)
            {
                int mirrorDmg = SkillHelpers.RollInclusive(_services.Rng, Math.Max(1, mirrorMin), mirrorMax);
                _services.Combat.DealDamage(ctx.Attacker, victim, mirrorDmg, "world", ignoreArmor: false, isSkillDamage: true);
                _services.Effects.PrintCenter(ctx.Attacker, $"Spiked Carapace reflected {mirrorDmg} damage!");
            }
        }

        return false;
    }

    private void ExecuteActionList(SkillContext ctx, IReadOnlyList<SkillActionConfig> actions)
    {
        if (actions.Count == 0)
            return;

        int capturedRound = _roundTracker.CurrentRoundId;

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Type))
                continue;

            if (!_primitives.TryGetValue(action.Type, out var primitive))
                continue;

            if (action.Delay > 0.001f)
            {
                var capturedAction = action;
                var capturedPrimitive = primitive;
                var capturedSkill = ctx.Skill;
                int capturedLevel = ctx.SkillLevel;
                var owner = ctx.Owner;

                _services.State.SchedulePlayerTimer(owner, action.Delay, () =>
                {
                    if (capturedRound != _roundTracker.CurrentRoundId)
                        return;
                    if (!SkillHelpers.IsValidPlayer(owner))
                        return;

                    var delayedCtx = new SkillContext
                    {
                        Services = ctx.Services,
                        Owner = ctx.Owner,
                        OwnerSession = ctx.OwnerSession,
                        Race = ctx.Race,
                        Skill = capturedSkill,
                        SkillLevel = capturedLevel,
                        Trigger = ctx.Trigger,
                        Attacker = ctx.Attacker,
                        Victim = ctx.Victim,
                        DamageDealt = ctx.DamageDealt,
                        WeaponName = ctx.WeaponName,
                        IsHeadshot = ctx.IsHeadshot,
                        IsSkillDamage = ctx.IsSkillDamage
                    };

                    capturedPrimitive.Execute(delayedCtx, capturedAction);
                }, repeat: false, tag: $"action_{capturedSkill?.Key ?? "race"}_{action.Type}");
            }
            else
            {
                primitive.Execute(ctx, action);
                // If a primary action rejected activation (e.g. insufficient HP/cash, target immune, already frozen/slowed),
                // stop executing trailing visual/secondary actions in this skill's action pipeline.
                if (!ctx.Activated)
                    break;
            }
        }
    }

    private bool IsValidCombatPair(CCSPlayerController attacker, CCSPlayerController victim)
    {
        bool ffa = _services.Sessions.IsFriendlyFireAllowed();
        return SkillHelpers.IsEnemy(attacker, victim, ffa);
    }
}
