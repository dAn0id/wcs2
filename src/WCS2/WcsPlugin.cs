using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using WCS2.Config;
using WCS2.Core;
using WCS2.Data;
using WCS2.Menus;
using WCS2.Services;
using WCS2.Skills;

namespace WCS2;

public sealed class WcsPlugin : BasePlugin, ISkillServices
{
    public override string ModuleName => "WCS2";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "dAn!EL";
    public override string ModuleDescription => "Warcraft:Source (WCS) RPG mod for CS2 CounterStrikeSharp";

    private WcsConfig _config = new();
    private RaceRegistry _registry = null!;
    private SqliteWcsRepository _repo = null!;
    private PlayerStateManager _state = null!;
    private EffectsService _effects = null!;
    private WeaponService _weapons = null!;
    private ProgressionService _progression = null!;
    private CombatService _combat = null!;
    private Source2TraceEngine _tracer = null!;
    private TeleportService _teleport = null!;
    private ShopService _shop = null!;
    private BotRaceService _bots = null!;
    private WcsMenuService _menus = null!;
    private ISkillExecutionEngine _engine = null!;
    private readonly Random _rng = new();

    // ISkillServices implementation
    WcsConfig ISkillServices.Config => _config;
    IPlayerStateManager ISkillServices.State => _state;
    ICombatService ISkillServices.Combat => _combat;
    IWeaponService ISkillServices.Weapons => _weapons;
    IEffectsService ISkillServices.Effects => _effects;
    ITeleportService ISkillServices.Teleport => _teleport;
    ISessionProvider ISkillServices.Sessions => _progression;
    Random ISkillServices.Rng => _rng;
    CounterStrikeSharp.API.Modules.Timers.Timer ISkillServices.AddTimer(float delaySeconds, Action callback, TimerFlags? flags)
        => AddTimer(delaySeconds, callback, flags);
    void ISkillServices.RunNextFrame(Action action) => Server.NextFrame(action);

    public override void Load(bool hotReload)
    {
        string configDir = Path.Combine(ModuleDirectory, "configs");
        _registry = new RaceRegistry(Logger);
        _config = _registry.LoadConfig(configDir);
        _registry.LoadAll(configDir);

        string dbPath = Path.Combine(ModuleDirectory, "data", _config.DatabaseFile);
        _repo = new SqliteWcsRepository(dbPath, Logger);
        _repo.InitializeAsync().GetAwaiter().GetResult();

        _effects = new EffectsService(this, _config);
        _weapons = new WeaponService();
        _state = new PlayerStateManager(this);
        _progression = new ProgressionService(this, _config, _registry, _repo, _effects, Logger);
        _combat = new CombatService(_state, _effects, _progression);
        var gamedata = GamedataConfig.Load(configDir, Logger);
        _tracer = new Source2TraceEngine(Logger, _config.UseNativeTrace, gamedata);
        _teleport = new TeleportService(_weapons, _tracer, _rng);
        _shop = new ShopService(_registry, _progression, _effects);
        _bots = new BotRaceService(_config, _registry, _progression, _rng);
        _menus = new WcsMenuService(this, _config, _registry, _progression, _shop, _repo, _effects);

        _engine = new SkillExecutionEngine(this);
        _shop.BindEngine(_engine);
        _bots.BindEngine(_engine);

        _progression.OnRaceChanged += HandleRaceChanged;
        _progression.OnSkillsReset += HandleSkillsReset;

        RegisterListeners();
        RegisterGameEvents();
        RegisterCommands();
        RegisterTimers();

        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);

        if (hotReload)
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (p != null && p.IsValid && p.Connected == PlayerConnectedState.Connected)
                    _progression.EnsureSession(p);
            }
        }

        Logger.LogInformation("[WCS2] Loaded cleanly: {Races} races, {Items} items, {Prims} primitives, {Handlers} custom handlers.",
            _registry.Races.Count, _registry.Items.Count, _engine.RegisteredPrimitives.Count, _engine.RegisteredHandlers.Count);
    }

    public override void Unload(bool hotReload)
    {
        VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre);

        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid)
            {
                var s = _progression.GetTypedSession(p);
                if (s != null) _engine.CleanupPlayer(p, s);
                _state.ResetTransientState(p, isDisconnect: false);
                _weapons.ClearRestrictions(p);
            }
        }

        _progression.SaveAllDirty();
        _repo.FlushAsync().GetAwaiter().GetResult();
        _repo.Dispose();
    }

    private void RegisterListeners()
    {
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            if (!string.IsNullOrWhiteSpace(_config.DefaultBarrelModel))
                manifest.AddResource(_config.DefaultBarrelModel);
            if (!string.IsNullOrWhiteSpace(_config.DefaultTAgentModel))
                manifest.AddResource(_config.DefaultTAgentModel);
            if (!string.IsNullOrWhiteSpace(_config.DefaultCtAgentModel))
                manifest.AddResource(_config.DefaultCtAgentModel);
            manifest.AddResource("models/chicken/chicken.vmdl");
            manifest.AddResource("particles/ui/ui_electric_exp_glow.vpcf");
            manifest.AddResource("particles/explosions_fx/explosion_smokegrenade_distort.vpcf");
        });

        RegisterListener<Listeners.OnTick>(() => _state.OnTick());

        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);

        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _progression.SaveAllDirty();
        });
    }

    private void RegisterGameEvents()
    {
        RegisterEventHandler<EventPlayerConnectFull>((ev, _) =>
        {
            var player = ev.Userid;
            if (player != null && player.IsValid)
                _progression.EnsureSession(player);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((ev, _) =>
        {
            var player = ev.Userid;
            if (player != null)
            {
                var session = _progression.GetTypedSession(player);
                if (session != null) _engine.CleanupPlayer(player, session, isDisconnect: true);
                _state.ResetTransientState(player, isDisconnect: true);
                _weapons.ClearRestrictions(player);
                _progression.OnPlayerDisconnect(player);
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            var player = ev.Userid;
            if (player != null && player.IsValid)
            {
                var session = _progression.GetTypedSession(player);
                if (session != null) _engine.CleanupPlayer(player, session);
                _state.ResetTransientState(player, isDisconnect: false);
                _weapons.ClearRestrictions(player);
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid) return HookResult.Continue;
            if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist)
                return HookResult.Continue;

            _progression.EnsureSession(player);
            int slot = player.Slot;

            // Defer 1 frame so pawn, health, and spawn weapons are fully initialized by CS2
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!PlayerStateManager.TryGetAlivePawn(p, out var _)) return;

                var session = _progression.GetTypedSession(p);
                if (session == null) return;

                _engine.CleanupPlayer(p!, session);
                _state.ResetTransientState(p!, isDisconnect: false);
                _weapons.ClearRestrictions(p!);
                session.UltimateBlocked = false;
                session.SkillsDisabled = false;

                if (session.IsBot)
                {
                    _bots.SetupBotOnSpawn(p!, session);
                }
                else
                {
                    if (!string.IsNullOrEmpty(session.PendingRaceKey))
                    {
                        var nextRace = _registry.GetRace(session.PendingRaceKey);
                        if (nextRace != null)
                            _progression.ApplyRaceChangeNow(p!, session, nextRace);
                    }
                    if (session.PendingSkillReset)
                    {
                        _progression.ApplySkillResetNow(p!, session);
                    }
                }

                var race = _registry.GetRace(session.CurrentRaceKey) ?? _registry.GetDefaultRace(_config.DefaultRace);
                if (race == null) return;

                // Apply race weapon restrictions
                if (race.AllowedWeapons.Count > 0)
                    _weapons.RestrictAllExcept(p!, race.AllowedWeapons);
                else if (race.RestrictedWeapons.Count > 0)
                    _weapons.RestrictWeapons(p!, race.RestrictedWeapons);

                _progression.ApplyClanTag(p!, session);

                // Fire race lifecycle spawn actions + skill player_spawn triggers
                _engine.TriggerRaceLifecycleActions(SkillTrigger.PlayerSpawn, p!, session, race);
                var spawnCtx = new SkillContext
                {
                    Services = this,
                    Owner = p!,
                    OwnerSession = session,
                    Race = race,
                    Trigger = SkillTrigger.PlayerSpawn
                };
                _engine.TriggerEvent(SkillTrigger.PlayerSpawn, spawnCtx);

                // Re-apply active UntilDeath shop items
                _shop.ReapplySpawnItems(p!, session);

                // Snapshot spawn weapons for Reincarnation
                session.LastDeathWeapons = _weapons.SnapshotWeapons(p!);

                if (!session.IsBot && _config.SpawnText && session.TotalLevel < _config.SpawnTextDisableLevel)
                {
                    _effects.PrintChat(p!, $"Playing {ChatColors.Green}{race.Name}{ChatColors.Default} (Lvl {session.RaceLevel}). Type {ChatColors.Yellow}!wcs{ChatColors.Default} for menu.");
                }
            });

            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            var victim = ev.Userid;
            var attacker = ev.Attacker;
            var assister = ev.Assister;

            if (victim != null && victim.IsValid)
            {
                var vSession = _progression.GetTypedSession(victim);
                var vRace = vSession != null ? _registry.GetRace(vSession.CurrentRaceKey) : null;

                if (vSession != null && victim.PlayerPawn?.Value?.AbsOrigin is { } deathPos)
                {
                    vSession.LastDeathOrigin = new Vector(deathPos.X, deathPos.Y, deathPos.Z);
                }

                if (vSession != null && vRace != null)
                {
                    // ALWAYS invoke PlayerDeath on every death (even if victim has no death skills) so Blood Mage Phoenix pool can run!
                    var deathCtx = new SkillContext
                    {
                        Services = this,
                        Owner = victim,
                        OwnerSession = vSession,
                        Race = vRace,
                        Trigger = SkillTrigger.PlayerDeath,
                        Attacker = attacker,
                        Victim = victim,
                        WeaponName = ev.Weapon ?? string.Empty,
                        IsHeadshot = ev.Headshot
                    };
                    _engine.TriggerEvent(SkillTrigger.PlayerDeath, deathCtx);
                    _engine.TriggerRaceLifecycleActions(SkillTrigger.PlayerDeath, victim, vSession, vRace);
                    _shop.OnPlayerDeath(vSession);
                    _engine.CleanupPlayer(victim, vSession);
                }

                _state.ResetTransientState(victim, isDisconnect: false);
                _weapons.ClearRestrictions(victim);
            }

            if (attacker != null && attacker.IsValid && victim != null && victim.IsValid && attacker.Slot != victim.Slot)
            {
                _progression.AwardKillXp(attacker, victim, ev.Weapon ?? string.Empty, ev.Headshot);

                var aSession = _progression.GetTypedSession(attacker);
                var aRace = aSession != null ? _registry.GetRace(aSession.CurrentRaceKey) : null;
                if (aSession != null && aRace != null)
                {
                    var killCtx = new SkillContext
                    {
                        Services = this,
                        Owner = attacker,
                        OwnerSession = aSession,
                        Race = aRace,
                        Trigger = SkillTrigger.PlayerKill,
                        Attacker = attacker,
                        Victim = victim,
                        WeaponName = ev.Weapon ?? string.Empty,
                        IsHeadshot = ev.Headshot
                    };
                    _engine.TriggerEvent(SkillTrigger.PlayerKill, killCtx);
                }
            }

            if (assister != null && assister.IsValid && victim != null && victim.IsValid
                && assister.Slot != victim.Slot && _config.AssistXp > 0
                && (_config.FfaEnabled || assister.Team != victim.Team))
            {
                _progression.GiveXp(assister, _config.AssistXp, "assist");
            }

            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerJump>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid)
                _state.OnPlayerJump(ev.Userid);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventWeaponFire>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid)
                _state.OnWeaponFire(ev.Userid);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventItemPickup>((ev, _) =>
        {
            var player = ev.Userid;
            if (player != null && player.IsValid)
            {
                int slot = player.Slot;
                Server.NextFrame(() =>
                {
                    var p = Utilities.GetPlayerFromSlot(slot);
                    if (p == null || !p.IsValid) return;
                    _state.ReapplyRenderState(p);
                    var s = _progression.GetTypedSession(p);
                    if (s != null && p.PawnIsAlive)
                        s.LastDeathWeapons = _weapons.SnapshotWeapons(p);
                });
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventItemEquip>((ev, _) =>
        {
            var player = ev.Userid;
            if (player != null && player.IsValid)
                _state.ReapplyRenderState(player);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundStart>((ev, _) =>
        {
            _engine.OnRoundStart();
            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid) continue;
                var s = _progression.GetTypedSession(p);
                if (s == null) continue;
                s.ResetAllCooldowns();
                var r = _registry.GetRace(s.CurrentRaceKey);
                if (r != null)
                    _engine.TriggerRaceLifecycleActions(SkillTrigger.PlayerRoundStart, p, s, r);
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundEnd>((ev, _) =>
        {
            _engine.OnRoundEnd();
            CsTeam winner = (CsTeam)ev.Winner;
            var sessions = new List<PlayerSession>();

            foreach (var p in Utilities.GetPlayers())
            {
                if (p == null || !p.IsValid || p.Connected != PlayerConnectedState.Connected) continue;
                var s = _progression.GetTypedSession(p);
                if (s == null) continue;
                sessions.Add(s);

                var r = _registry.GetRace(s.CurrentRaceKey);
                if (r != null)
                    _engine.TriggerRaceLifecycleActions(SkillTrigger.PlayerRoundEnd, p, s, r);

                if (p.Team == winner && _config.RoundWinXp > 0)
                    _progression.GiveXp(p, _config.RoundWinXp, "round win");
                else if (p.PawnIsAlive && p.Team != winner && (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist) && _config.RoundSurvivalXp > 0)
                    _progression.GiveXp(p, _config.RoundSurvivalXp, "surviving the round");

                _engine.CleanupPlayer(p, s);
            }

            _shop.OnRoundEnd(sessions);
            _progression.SaveAllDirty();
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombPlanted>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid && _config.BombPlantXp > 0)
                _progression.GiveXp(ev.Userid, _config.BombPlantXp, "planting the bomb");
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombExploded>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid && _config.BombExplodeXp > 0)
                _progression.GiveXp(ev.Userid, _config.BombExplodeXp, "bomb detonation");
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombDefused>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid && _config.BombDefuseXp > 0)
                _progression.GiveXp(ev.Userid, _config.BombDefuseXp, "defusing the bomb");
            return HookResult.Continue;
        });

        RegisterEventHandler<EventHostageRescued>((ev, _) =>
        {
            if (ev.Userid != null && ev.Userid.IsValid && _config.HostageRescueXp > 0)
                _progression.GiveXp(ev.Userid, _config.HostageRescueXp, "rescuing a hostage");
            return HookResult.Continue;
        });
    }

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn victimPawn, CTakeDamageInfo info)
    {
        if (victimPawn == null || !victimPawn.IsValid) return HookResult.Continue;
        if (_combat.IsApplyingSkillDamage) return HookResult.Continue;

        var victim = victimPawn.OriginalController?.Value;
        if (victim == null || !victim.IsValid) return HookResult.Continue;

        // 1. Godmode check
        if (_state.IsGodMode(victim))
        {
            info.Damage = 0f;
            return HookResult.Handled;
        }

        // 2. Fall damage immunity
        if (((DamageTypes_t)info.BitsDamageType & DamageTypes_t.DMG_FALL) != 0 && _state.IsFallDamageImmune(victim))
        {
            info.Damage = 0f;
            return HookResult.Handled;
        }

        // 3. Headshot immunity
        bool isHeadshot = info.GetHitGroup() == HitGroup_t.HITGROUP_HEAD;
        if (isHeadshot && _state.IsHeadshotImmune(victim))
        {
            info.Damage = 0f;
            return HookResult.Handled;
        }

        // Resolve attacker controller
        CCSPlayerController? attacker = null;
        var attackerEnt = info.Attacker?.Value;
        if (attackerEnt != null && attackerEnt.IsValid)
        {
            if (attackerEnt.DesignerName == "player")
            {
                var attPawn = new CCSPlayerPawn(attackerEnt.Handle);
                attacker = attPawn.OriginalController?.Value;
            }
            else if (attackerEnt.DesignerName == "cs_player_controller")
            {
                attacker = new CCSPlayerController(attackerEnt.Handle);
            }
        }

        bool isPvp = attacker != null && attacker.IsValid && attacker.Slot != victim.Slot
                     && (_config.FfaEnabled || attacker.Team != victim.Team);
        if (!isPvp) return HookResult.Continue;

        string weaponName = info.Ability?.Value?.DesignerName
                            ?? info.Inflictor?.Value?.DesignerName
                            ?? attacker!.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value?.DesignerName
                            ?? string.Empty;

        bool modified = false;

        // 4. Victim triggers (evasion, damage absorb, thorns, mirror, etc.)
        var vSession = _progression.GetTypedSession(victim);
        var vRace = vSession != null ? _registry.GetRace(vSession.CurrentRaceKey) : null;
        if (vSession != null && vRace != null)
        {
            var victimCtx = new SkillContext
            {
                Services = this,
                Owner = victim,
                OwnerSession = vSession,
                Race = vRace,
                Trigger = SkillTrigger.PlayerVictim,
                Attacker = attacker,
                Victim = victim,
                DamageInfo = info,
                DamageDealt = info.Damage,
                WeaponName = weaponName,
                IsHeadshot = isHeadshot,
                IsSkillDamage = false
            };
            _engine.TriggerEvent(SkillTrigger.PlayerVictim, victimCtx);

            if (victimCtx.CancelDamage)
            {
                info.Damage = 0f;
                return HookResult.Handled;
            }
            if (MathF.Abs(victimCtx.DamageMultiplier - 1.0f) > 0.001f)
            {
                info.Damage = MathF.Max(0f, info.Damage * victimCtx.DamageMultiplier);
                modified = true;
            }
        }

        // 5. Attacker triggers (fires ONCE on Pre-damage so bonus damage/vampiric/crit/slow/freeze/burn never double-fire!)
        var aSession = _progression.GetTypedSession(attacker!);
        var aRace = aSession != null ? _registry.GetRace(aSession.CurrentRaceKey) : null;
        if (aSession != null && aRace != null)
        {
            var attackerCtx = new SkillContext
            {
                Services = this,
                Owner = attacker!,
                OwnerSession = aSession,
                Race = aRace,
                Trigger = SkillTrigger.PlayerAttacker,
                Attacker = attacker,
                Victim = victim,
                DamageInfo = info,
                DamageDealt = info.Damage,
                WeaponName = weaponName,
                IsHeadshot = isHeadshot,
                IsSkillDamage = false
            };
            _engine.TriggerEvent(SkillTrigger.PlayerAttacker, attackerCtx);

            if (MathF.Abs(attackerCtx.DamageMultiplier - 1.0f) > 0.001f || attackerCtx.BonusDamage > 0f)
            {
                info.Damage = MathF.Max(0f, info.Damage * attackerCtx.DamageMultiplier + attackerCtx.BonusDamage);
                modified = true;
            }
        }

        return modified ? HookResult.Changed : HookResult.Continue;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (!_state.HasAnyCheckTransmitFilters()) return;

        // Cache players once per CheckTransmit batch
        var hiddenPawns = new List<(CCSPlayerController Player, CCSPlayerPawn Pawn)>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.PawnIsAlive) continue;
            if (_state.IsPlayerHiddenFromEnemies(p.Slot) && p.PlayerPawn?.Value is { IsValid: true } pawn)
            {
                hiddenPawns.Add((p, pawn));
            }
        }

        // Cache disguise props once per CheckTransmit batch (avoid FindAllEntitiesByDesignerName inside viewer loop)
        var disguiseProps = _state.GetActiveDisguiseProps();

        foreach ((CCheckTransmitInfo info, CCSPlayerController? viewer) in infoList)
        {
            if (viewer == null || !viewer.IsValid) continue;

            // Hide alpha==0 invisible players & their weapons/wearables from enemies
            foreach (var (hiddenPlayer, hiddenPawn) in hiddenPawns)
            {
                if (hiddenPlayer.Slot == viewer.Slot) continue;
                if (!_config.FfaEnabled && hiddenPlayer.Team == viewer.Team) continue;

                info.TransmitEntities.Remove(hiddenPawn);
                if (hiddenPawn.WeaponServices?.MyWeapons != null)
                {
                    foreach (var w in hiddenPawn.WeaponServices.MyWeapons)
                    {
                        if (w?.Value is { IsValid: true } weapon)
                            info.TransmitEntities.Remove(weapon);
                    }
                }
                if (hiddenPawn.MyWearables != null)
                {
                    foreach (var w in hiddenPawn.MyWearables)
                    {
                        if (w?.Value is { IsValid: true } wear)
                            info.TransmitEntities.Remove(wear);
                    }
                }
            }

            // Hide prop disguises from their own wearer so first-person view stays clear
            foreach (var (ownerSlot, prop) in disguiseProps)
            {
                if (ownerSlot == viewer.Slot)
                {
                    info.TransmitEntities.Remove(prop);
                }
            }
        }
    }

    private HookResult OnCanAcquire(DynamicHook hook)
    {
        var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
        var econView = hook.GetParam<CEconItemView>(1);
        if (itemServices == null || econView == null) return HookResult.Continue;

        var pawn = itemServices.Pawn?.Value;
        var player = pawn?.Controller?.Value?.As<CCSPlayerController>();
        if (player == null || !player.IsValid) return HookResult.Continue;

        var vdata = VirtualFunctions.GetCSWeaponDataFromKey(-1, econView.ItemDefinitionIndex.ToString());
        if (vdata == null || string.IsNullOrEmpty(vdata.Name)) return HookResult.Continue;

        if (!_weapons.IsWeaponAllowed(player, vdata.Name))
        {
            hook.SetReturn(AcquireResult.NotAllowedByProhibition);
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void RegisterCommands()
    {
        // Menus & Information
        AddCommand("css_wcs", "Open WCS main menu", (p, _) => { if (p != null) _menus.OpenMainMenu(p); });
        AddCommand("css_wcsmenu", "Open WCS main menu", (p, _) => { if (p != null) _menus.OpenMainMenu(p); });
        AddCommand("css_changerace", "Change WCS race", (p, info) =>
        {
            if (p == null) return;
            string? query = info.ArgCount > 1 ? info.ArgString.Trim() : null;
            _menus.OpenChangeRaceCategories(p, null, query);
        });
        AddCommand("css_spendskills", "Spend WCS skill points", (p, _) => { if (p != null) _menus.OpenSpendSkillsMenu(p); });
        AddCommand("css_showskills", "Show current race skills", (p, _) => { if (p != null) _menus.PrintMySkills(p); });
        AddCommand("css_resetskills", "Reset current race skills", (p, _) => { if (p != null) _progression.ResetSkills(p); });
        AddCommand("css_shopmenu", "Open WCS shop menu", (p, _) => { if (p != null) _menus.OpenShopMenu(p); });
        AddCommand("css_shopinfo", "Show WCS shop item info", (p, _) => { if (p != null) _menus.PrintShopInfo(p); });
        AddCommand("css_raceinfo", "View race details", (p, info) =>
        {
            if (p == null) return;
            string? query = info.ArgCount > 1 ? info.ArgString.Trim() : null;
            _menus.OpenRaceInfoCategories(p, null, query);
        });
        AddCommand("css_myraceinfo", "Show info for your current race", (p, _) =>
        {
            if (p == null) return;
            var s = _progression.GetTypedSession(p);
            var r = s != null ? _registry.GetRace(s.CurrentRaceKey) : null;
            if (r != null) _menus.PrintRaceInfo(p, r);
        });
        AddCommand("css_myinfo", "Show your skills and XP", (p, _) => { if (p != null) _menus.PrintMySkills(p); });
        AddCommand("css_showxp", "Show your XP and rank", (p, _) => { if (p != null) _menus.ShowMyRank(p); });
        AddCommand("css_playerinfo", "Inspect online players", (p, _) => { if (p != null) _menus.OpenPlayerInfoMenu(p); });
        AddCommand("css_wcstop", "Show WCS top 10", (p, _) => { if (p != null) _menus.ShowTopPlayers(p); });
        AddCommand("css_wcsrank", "Show your WCS rank", (p, _) => { if (p != null) _menus.ShowMyRank(p); });
        AddCommand("css_rank", "Show your WCS rank", (p, _) => { if (p != null) _menus.ShowMyRank(p); });
        AddCommand("css_wcshelp", "Show WCS help", (p, _) => { if (p != null) _menus.PrintHelp(p); });
        AddCommand("css_wcsbank", "Open WCS Level Bank", (p, _) => { if (p != null) _menus.OpenLevelBankMenu(p); });
        AddCommand("css_levelbank", "Open WCS Level Bank", (p, _) => { if (p != null) _menus.OpenLevelBankMenu(p); });
        AddCommand("css_wcsadmin", "Open WCS Admin menu", (p, _) => { if (p != null) _menus.OpenAdminMenu(p); });

        // Active Ultimate & Ability Binds (console & chat!)
        void TriggerUlt(CCSPlayerController? p, CommandInfo _)
        {
            if (p == null || !p.IsValid) return;
            var s = _progression.GetTypedSession(p);
            if (s != null) _engine.TryActivateUltimate(p, s);
        }
        void TriggerAbil(CCSPlayerController? p, CommandInfo _)
        {
            if (p == null || !p.IsValid) return;
            var s = _progression.GetTypedSession(p);
            if (s != null) _engine.TryActivateAbility(p, s);
        }

        AddCommand("ultimate", "Activate WCS Ultimate", TriggerUlt);
        AddCommand("css_ultimate", "Activate WCS Ultimate", TriggerUlt);
        AddCommand("ability", "Activate WCS Ability", TriggerAbil);
        AddCommand("css_ability", "Activate WCS Ability", TriggerAbil);

        // Optional Inspect-key (F / +lookatweapon) ultimate trigger for players who enable it in Settings
        AddCommandListener("+lookatweapon", (player, _) =>
        {
            if (_config.AllowInspectUltimateBind && player != null && player.IsValid && player.PawnIsAlive)
            {
                var s = _progression.GetTypedSession(player);
                if (s != null && s.InspectUltimate)
                    _engine.TryActivateUltimate(player, s);
            }
            return HookResult.Continue;
        }, HookMode.Pre);

        // Admin reload command (server console or WCS admin)
        AddCommand("wcs_reload", "Reload WCS configs, gamedata, races, and items", (p, _) =>
        {
            if (p != null && !_menus.IsWcsAdmin(p)) return;
            string configDir = Path.Combine(ModuleDirectory, "configs");
            _config = _registry.LoadConfig(configDir);
            _registry.LoadAll(configDir);
            var gamedata = GamedataConfig.Load(configDir, Logger);
            _tracer.Reload(_config.UseNativeTrace, gamedata);
            string msg = $"Reloaded WCS: {_registry.Races.Count} races, {_registry.Items.Count} items (use_native_trace={_config.UseNativeTrace}, gamedata_mode={gamedata.ResolutionMode}, tracer_available={_tracer.IsAvailable}).";
            if (p != null) _effects.PrintChat(p, msg);
            else Logger.LogInformation("[WCS2] {Msg}", msg);
        });

        // Diagnostic command (server console or WCS admin) to safely verify aligned Source2TraceEngine live
        AddCommand("wcs_tracetest", "Run Source2TraceEngine line+hull self-test (console/admin only)", (p, _) =>
        {
            if (p != null && !_menus.IsWcsAdmin(p)) return;
            var spawnOrigin = _teleport.GetRandomTeamSpawn(CsTeam.CounterTerrorist)
                              ?? _teleport.GetRandomTeamSpawn(CsTeam.Terrorist)
                              ?? new Vector(0f, 0f, 64f);
            var origin3 = new System.Numerics.Vector3(spawnOrigin.X, spawnOrigin.Y, spawnOrigin.Z);
            string report = _tracer.RunDiagnosticSelfTest(origin3);
            string msg = $"[WCS2] wcs_tracetest @ ({origin3.X:F0},{origin3.Y:F0},{origin3.Z:F0}): {report}";
            Logger.LogInformation("{Msg}", msg);
            if (p != null) _effects.PrintChat(p, msg);
            else Server.PrintToConsole(msg);
        });
    }

    private void RegisterTimers()
    {
        if (_config.RestedXpEnabled && _config.RestedXpOnlineIntervalSeconds > 0)
        {
            AddTimer(_config.RestedXpOnlineIntervalSeconds, () => _progression.TickOnlineRestedXp(), TimerFlags.REPEAT);
        }

        // Periodic background flush of dirty player sessions
        AddTimer(60f, () => _progression.SaveAllDirty(), TimerFlags.REPEAT);

        // Periodic bot ability / ultimate triggers
        AddTimer(8f, () => _bots.TryTriggerBotAbilities(), TimerFlags.REPEAT);
    }

    private void HandleRaceChanged(CCSPlayerController player, PlayerSession session, RaceDefinition newRace)
    {
        if (!player.IsValid) return;
        _engine.CleanupPlayer(player, session);
        _state.ResetTransientState(player, isDisconnect: false);
        _weapons.ClearRestrictions(player);

        if (player.PawnIsAlive)
        {
            // Slay or re-apply cleanly on immediate race change (classic WCS behavior when changerace_next_round=0)
            if (newRace.AllowedWeapons.Count > 0)
                _weapons.RestrictAllExcept(player, newRace.AllowedWeapons);
            else if (newRace.RestrictedWeapons.Count > 0)
                _weapons.RestrictWeapons(player, newRace.RestrictedWeapons);

            _engine.TriggerRaceLifecycleActions(SkillTrigger.PlayerSpawn, player, session, newRace);
            var ctx = new SkillContext
            {
                Services = this,
                Owner = player,
                OwnerSession = session,
                Race = newRace,
                Trigger = SkillTrigger.PlayerSpawn
            };
            _engine.TriggerEvent(SkillTrigger.PlayerSpawn, ctx);
        }
    }

    private void HandleSkillsReset(CCSPlayerController player, PlayerSession session)
    {
        if (!player.IsValid) return;
        _engine.CleanupPlayer(player, session);
        _state.ResetTransientState(player, isDisconnect: false);
    }
}
