using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Skills;

namespace WCS2.Core;

public sealed class PlayerStateManager : IPlayerStateManager
{
    private sealed class TransientPlayerState
    {
        public float BaseSpeed { get; set; } = 1.0f;
        public float AdditiveSpeed { get; set; } = 0f;
        public float? OverrideSpeed { get; set; }
        public float TargetGravity { get; set; } = 1.0f;
        public bool WasOnLadder { get; set; }
        public float LongjumpMultiplier { get; set; } = 1.0f;
        public Color RenderColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
        public bool IsFrozen { get; set; }
        public bool IsGodMode { get; set; }
        public bool FallDamageImmune { get; set; }
        public bool HeadshotImmune { get; set; }
        public bool JetpackEnabled { get; set; }
        public float JetpackThrust { get; set; } = 320f;
        public bool NoBlockEnabled { get; set; }
        public float AttackSpeedMultiplier { get; set; } = 1.0f;
        public bool NoRecoilEnabled { get; set; }
        public float Scale { get; set; } = 1.0f;
        public float BaseScale { get; set; } = 1.0f;
        public float PassiveInfiniteAmmoChance { get; set; } = 0f;
        public float TimedInfiniteAmmoChance { get; set; } = 0f;
        public bool HiddenFromEnemies { get; set; }
        public CDynamicProp? DisguiseProp { get; set; }
        public uint DisguisePropIndex { get; set; }
        public List<(string? Tag, CounterStrikeSharp.API.Modules.Timers.Timer Timer)> Timers { get; } = new();
        public List<CBaseEntity> SpawnedEntities { get; } = new();
        public Dictionary<string, object?> CustomData { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> RoundProcCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, TransientPlayerState> _states = new();
    private readonly Dictionary<uint, int> _entityHiddenFromSlot = new();

    public PlayerStateManager(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    private TransientPlayerState GetOrCreate(int slot)
    {
        if (!_states.TryGetValue(slot, out var s))
        {
            s = new TransientPlayerState();
            _states[slot] = s;
        }
        return s;
    }

    public static bool TryGetAlivePawn(CCSPlayerController? player, out CCSPlayerPawn pawn)
    {
        pawn = null!;
        if (player == null || !player.IsValid || !player.PawnIsAlive) return false;
        var p = player.PlayerPawn?.Value;
        if (p == null || !p.IsValid || p.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;
        pawn = p;
        return true;
    }

    public void ResetTransientState(CCSPlayerController player, bool isDisconnect = false)
    {
        if (player == null) return;
        int slot = player.Slot;
        if (!_states.TryGetValue(slot, out var state)) return;

        // 1. Kill all player-scoped timers
        foreach (var (_, timer) in state.Timers)
        {
            timer.Kill();
        }
        state.Timers.Clear();

        // 2. Remove tracked spawned entities (props/wards/etc.)
        foreach (var ent in state.SpawnedEntities)
        {
            try
            {
                if (ent != null && ent.IsValid)
                    ent.Remove();
            }
            catch { /* entity may already be freed */ }
        }
        state.SpawnedEntities.Clear();

        // 3. Remove prop disguise
        ClearPropDisguiseInternal(state);

        // 4. Reset pawn properties if valid and alive
        if (!isDisconnect && player.IsValid && player.PlayerPawn?.Value is { IsValid: true } pawn)
        {
            pawn.VelocityModifier = 1.0f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");

            pawn.GravityScale = 1.0f;
            pawn.ActualGravityScale = 1.0f;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");

            if (state.IsFrozen || pawn.MoveType == MoveType_t.MOVETYPE_NONE || pawn.MoveType == MoveType_t.MOVETYPE_FLY || pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP)
            {
                ApplyMoveType(pawn, MoveType_t.MOVETYPE_WALK);
            }

            if (state.NoBlockEnabled && pawn.Collision != null)
            {
                pawn.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_PLAYER;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_Collision");
            }

            if (MathF.Abs(state.Scale - 1.0f) > 0.001f)
            {
                ApplyScale(pawn, 1.0f);
            }

            ApplyRenderToPlayerAndGear(pawn, Color.FromArgb(255, 255, 255, 255));

            player.DesiredFOV = 90;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");
        }

        if (isDisconnect)
        {
            _states.Remove(slot);
        }
        else
        {
            state.BaseSpeed = 1.0f;
            state.AdditiveSpeed = 0f;
            state.OverrideSpeed = null;
            state.TargetGravity = 1.0f;
            state.WasOnLadder = false;
            state.LongjumpMultiplier = 1.0f;
            state.RenderColor = Color.FromArgb(255, 255, 255, 255);
            state.IsFrozen = false;
            state.IsGodMode = false;
            state.FallDamageImmune = false;
            state.HeadshotImmune = false;
            state.JetpackEnabled = false;
            state.NoBlockEnabled = false;
            state.AttackSpeedMultiplier = 1.0f;
            state.NoRecoilEnabled = false;
            state.Scale = 1.0f;
            state.BaseScale = 1.0f;
            state.PassiveInfiniteAmmoChance = 0f;
            state.TimedInfiniteAmmoChance = 0f;
            state.HiddenFromEnemies = false;
            state.CustomData.Clear();
            state.RoundProcCounts.Clear();
        }
    }

    public void SetHealth(CCSPlayerController player, int value, bool add = false, bool updateMaxHealth = true)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        int newHp = add ? pawn.Health + value : value;
        newHp = Math.Max(1, newHp);
        pawn.Health = newHp;
        if (updateMaxHealth && newHp > pawn.MaxHealth)
        {
            pawn.MaxHealth = newHp;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        }
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    public void SetArmor(CCSPlayerController player, int value, bool add = false, bool giveHelmet = false)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        int newArmor = add ? pawn.ArmorValue + value : value;
        pawn.ArmorValue = Math.Clamp(newArmor, 0, 250);
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");

        if (giveHelmet && pawn.ItemServices != null)
        {
            var itemSvc = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            itemSvc.HasHelmet = true;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_pItemServices");
        }
    }

    public void SetSpeed(CCSPlayerController player, float value, bool additive = false, float duration = 0f, string? source = null)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        var state = GetOrCreate(player.Slot);

        if (duration > 0f)
        {
            string timerTag = source ?? "speed_temp";
            CancelPlayerTimers(player, timerTag);

            // Base additive timed speed on non-override speed so repeated slow ticks (e.g. Serpent Ward) never stack down to 0.1x
            float baseSpeed = state.BaseSpeed + state.AdditiveSpeed;
            state.OverrideSpeed = additive ? Math.Max(0.1f, baseSpeed + value) : Math.Max(0.1f, value);
            ApplySpeed(pawn, state);

            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!TryGetAlivePawn(p, out var livePawn)) return;
                if (_states.TryGetValue(slot, out var st))
                {
                    st.OverrideSpeed = null;
                    ApplySpeed(livePawn, st);
                }
            }, repeat: false, tag: timerTag);
        }
        else
        {
            if (additive)
                state.AdditiveSpeed += value;
            else
                state.BaseSpeed = Math.Max(0.1f, value);
            ApplySpeed(pawn, state);
        }
    }

    private static float ComputeEffectiveSpeed(TransientPlayerState state)
    {
        if (state.OverrideSpeed.HasValue) return Math.Clamp(state.OverrideSpeed.Value, 0.1f, 3.0f);
        return Math.Clamp(state.BaseSpeed + state.AdditiveSpeed, 0.1f, 3.0f);
    }

    private static void ApplySpeed(CCSPlayerPawn pawn, TransientPlayerState state)
    {
        pawn.VelocityModifier = ComputeEffectiveSpeed(state);
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
    }

    public void SetGravity(CCSPlayerController player, float scale, float duration = 0f, string? source = null)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        var state = GetOrCreate(player.Slot);
        float clamped = Math.Clamp(scale, 0.05f, 4.0f);
        float previous = state.TargetGravity;

        state.TargetGravity = clamped;
        ApplyGravity(pawn, clamped);

        if (duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!TryGetAlivePawn(p, out var livePawn)) return;
                if (_states.TryGetValue(slot, out var st))
                {
                    st.TargetGravity = previous;
                    ApplyGravity(livePawn, previous);
                }
            }, repeat: false, tag: source ?? "gravity_temp");
        }
    }

    private static void ApplyGravity(CCSPlayerPawn pawn, float scale)
    {
        pawn.GravityScale = scale;
        pawn.ActualGravityScale = scale;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
    }

    public void SetLongjump(CCSPlayerController player, float multiplier, bool additive = false)
    {
        if (!player.IsValid) return;
        var state = GetOrCreate(player.Slot);
        state.LongjumpMultiplier = additive
            ? Math.Clamp(state.LongjumpMultiplier + multiplier, 1.0f, 3.5f)
            : Math.Clamp(multiplier, 1.0f, 3.5f);
    }

    public void OnPlayerJump(CCSPlayerController player)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        if (!_states.TryGetValue(player.Slot, out var state)) return;
        if (state.LongjumpMultiplier <= 1.01f) return;

        var vel = pawn.AbsVelocity;
        var boosted = new Vector(
            vel.X * state.LongjumpMultiplier,
            vel.Y * state.LongjumpMultiplier,
            vel.Z);
        pawn.Teleport(null, null, boosted);
    }

    public void SetInvisibility(CCSPlayerController player, int alpha, int r = 255, int g = 255, int b = 255, float duration = 0f)
        => SetColor(player, r, g, b, alpha, duration);

    public void SetColor(CCSPlayerController player, int r, int g, int b, int a = 255, float duration = 0f)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        var state = GetOrCreate(player.Slot);
        var color = Color.FromArgb(
            Math.Clamp(a, 0, 255),
            Math.Clamp(r, 0, 255),
            Math.Clamp(g, 0, 255),
            Math.Clamp(b, 0, 255));

        var prevColor = state.RenderColor;
        var prevHidden = state.HiddenFromEnemies;

        state.RenderColor = color;
        state.HiddenFromEnemies = color.A == 0;
        ApplyRenderToPlayerAndGear(pawn, color);

        if (duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!TryGetAlivePawn(p, out var livePawn)) return;
                if (_states.TryGetValue(slot, out var st))
                {
                    st.RenderColor = prevColor;
                    st.HiddenFromEnemies = prevHidden;
                    ApplyRenderToPlayerAndGear(livePawn, prevColor);
                }
            }, repeat: false, tag: "render_temp");
        }
    }

    public void ReapplyRenderState(CCSPlayerController player)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        if (!_states.TryGetValue(player.Slot, out var state)) return;
        if (state.RenderColor.A < 255 || state.RenderColor.R < 255 || state.RenderColor.G < 255 || state.RenderColor.B < 255)
        {
            ApplyRenderToPlayerAndGear(pawn, state.RenderColor);
        }
        ApplySpeed(pawn, state);
    }

    private static void ApplyRenderToPlayerAndGear(CCSPlayerPawn pawn, Color color)
    {
        ApplyRenderToEntity(pawn, color);

        if (pawn.WeaponServices?.MyWeapons != null)
        {
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = handle?.Value;
                if (weapon != null && weapon.IsValid)
                    ApplyRenderToEntity(weapon, color);
            }
        }

        if (pawn.MyWearables != null)
        {
            foreach (var handle in pawn.MyWearables)
            {
                var wearable = handle?.Value;
                if (wearable != null && wearable.IsValid)
                    ApplyRenderToEntity(wearable, color);
            }
        }
    }

    private static void ApplyRenderToEntity(CBaseModelEntity entity, Color color)
    {
        entity.RenderMode = color.A < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
        entity.Render = color;
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_nRenderMode");
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_clrRender");
    }

    public void SetFreeze(CCSPlayerController player, bool frozen, float duration = 0f)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        var state = GetOrCreate(player.Slot);

        state.IsFrozen = frozen;
        ApplyMoveType(pawn, frozen ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK);

        if (frozen && duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!TryGetAlivePawn(p, out var livePawn)) return;
                if (_states.TryGetValue(slot, out var st) && st.IsFrozen)
                {
                    st.IsFrozen = false;
                    ApplyMoveType(livePawn, MoveType_t.MOVETYPE_WALK);
                }
            }, repeat: false, tag: "freeze_temp");
        }
    }

    public void SetMoveType(CCSPlayerController player, MoveType_t moveType)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        ApplyMoveType(pawn, moveType);
    }

    private static void ApplyMoveType(CCSPlayerPawn pawn, MoveType_t moveType)
    {
        pawn.MoveType = moveType;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", (byte)moveType);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    public void SetGodMode(CCSPlayerController player, bool enabled, float duration = 0f)
    {
        if (!player.IsValid) return;
        var state = GetOrCreate(player.Slot);
        state.IsGodMode = enabled;

        if (enabled && duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                if (_states.TryGetValue(slot, out var st))
                    st.IsGodMode = false;
            }, repeat: false, tag: "godmode_temp");
        }
    }

    public bool IsGodMode(CCSPlayerController player)
        => player.IsValid && _states.TryGetValue(player.Slot, out var st) && st.IsGodMode;

    public void SetJetpack(CCSPlayerController player, bool enabled, float thrust = 320f, float duration = 0f)
    {
        if (!player.IsValid) return;
        var state = GetOrCreate(player.Slot);
        state.JetpackEnabled = enabled;
        state.JetpackThrust = thrust;

        if (enabled && duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                if (_states.TryGetValue(slot, out var st))
                    st.JetpackEnabled = false;
            }, repeat: false, tag: "jetpack_temp");
        }
    }

    public void SetFallDamageImmune(CCSPlayerController player, bool immune)
    {
        if (!player.IsValid) return;
        GetOrCreate(player.Slot).FallDamageImmune = immune;
    }

    public bool IsFallDamageImmune(CCSPlayerController player)
        => player.IsValid && _states.TryGetValue(player.Slot, out var st) && st.FallDamageImmune;

    public void SetHeadshotImmune(CCSPlayerController player, bool immune)
    {
        if (!player.IsValid) return;
        GetOrCreate(player.Slot).HeadshotImmune = immune;
    }

    public bool IsHeadshotImmune(CCSPlayerController player)
        => player.IsValid && _states.TryGetValue(player.Slot, out var st) && st.HeadshotImmune;

    public void SetCash(CCSPlayerController player, int amount, bool add = true)
    {
        if (!player.IsValid || player.InGameMoneyServices == null) return;
        int cur = player.InGameMoneyServices.Account;
        player.InGameMoneyServices.Account = Math.Clamp(add ? cur + amount : amount, 0, 16000);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    public void SetNoBlock(CCSPlayerController player, bool enabled)
    {
        if (!TryGetAlivePawn(player, out var pawn) || pawn.Collision == null) return;
        var state = GetOrCreate(player.Slot);
        state.NoBlockEnabled = enabled;
        pawn.Collision.CollisionGroup = (byte)(enabled
            ? CollisionGroup.COLLISION_GROUP_DISSOLVING
            : CollisionGroup.COLLISION_GROUP_PLAYER);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_Collision");
    }

    public void SetAttackSpeed(CCSPlayerController player, float multiplier, float duration = 0f)
    {
        if (!player.IsValid) return;
        var state = GetOrCreate(player.Slot);
        float prev = state.AttackSpeedMultiplier;
        state.AttackSpeedMultiplier = Math.Clamp(multiplier, 0.5f, 2.0f);

        if (duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                if (_states.TryGetValue(slot, out var st))
                    st.AttackSpeedMultiplier = prev;
            }, repeat: false, tag: "attackspeed_temp");
        }
    }

    public void SetNoRecoil(CCSPlayerController player, bool enabled, float duration = 0f)
    {
        if (!player.IsValid) return;
        var state = GetOrCreate(player.Slot);
        state.NoRecoilEnabled = enabled;

        if (enabled && duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                if (_states.TryGetValue(slot, out var st))
                    st.NoRecoilEnabled = false;
            }, repeat: false, tag: "norecoil_temp");
        }
    }

    public void SetFov(CCSPlayerController player, uint fov, float duration = 0f)
    {
        if (!player.IsValid) return;
        player.DesiredFOV = fov;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");

        if (duration > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, duration, () =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p != null && p.IsValid)
                {
                    p.DesiredFOV = 90;
                    Utilities.SetStateChanged(p, "CBasePlayerController", "m_iDesiredFOV");
                }
            }, repeat: false, tag: "fov_temp");
        }
    }

    public void SetScale(CCSPlayerController player, float scale, float durationSeconds = 0f)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        float clamped = Math.Clamp(scale, 0.4f, 2.0f);
        var state = GetOrCreate(player.Slot);

        CancelPlayerTimers(player, "scale_temp");
        state.Scale = clamped;
        if (durationSeconds <= 0f)
        {
            state.BaseScale = clamped;
        }
        ApplyScale(pawn, clamped);

        if (durationSeconds > 0f)
        {
            int slot = player.Slot;
            SchedulePlayerTimer(player, durationSeconds, () =>
            {
                if (!_states.TryGetValue(slot, out var st)) return;
                float restore = st.BaseScale;
                st.Scale = restore;
                var p = Utilities.GetPlayerFromSlot(slot);
                if (TryGetAlivePawn(p, out var livePawn))
                {
                    ApplyScale(livePawn, restore);
                }
            }, repeat: false, tag: "scale_temp");
        }
    }

    private static void ApplyScale(CCSPlayerPawn pawn, float scale)
    {
        var skel = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance();
        if (skel != null)
        {
            skel.Scale = scale;
        }
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        pawn.AcceptInput("SetScale", null, null, scale.ToString(CultureInfo.InvariantCulture));
    }

    public void SetInfiniteAmmo(CCSPlayerController player, float chancePercent, float durationSeconds = 0f)
    {
        if (!player.IsValid) return;
        float clamped = Math.Clamp(chancePercent, 0f, 100f);
        var state = GetOrCreate(player.Slot);

        if (durationSeconds <= 0f)
        {
            state.PassiveInfiniteAmmoChance = clamped <= 0f
                ? 0f
                : MathF.Max(state.PassiveInfiniteAmmoChance, clamped);
            return;
        }

        CancelPlayerTimers(player, "infinite_ammo_temp");
        state.TimedInfiniteAmmoChance = clamped;
        int slot = player.Slot;
        SchedulePlayerTimer(player, durationSeconds, () =>
        {
            if (_states.TryGetValue(slot, out var st))
                st.TimedInfiniteAmmoChance = 0f;
        }, repeat: false, tag: "infinite_ammo_temp");
    }

    public float GetInfiniteAmmoChance(int slot)
    {
        if (!_states.TryGetValue(slot, out var st)) return 0f;
        return MathF.Max(st.PassiveInfiniteAmmoChance, st.TimedInfiniteAmmoChance);
    }

    public int GetRoundProcCount(int slot, string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (_states.TryGetValue(slot, out var st) && st.RoundProcCounts.TryGetValue(key, out int count))
            return count;
        return 0;
    }

    public int IncrementRoundProcCount(int slot, string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var st = GetOrCreate(slot);
        int next = st.RoundProcCounts.TryGetValue(key, out int cur) ? cur + 1 : 1;
        st.RoundProcCounts[key] = next;
        return next;
    }

    public void ClearAllRoundProcCounts()
    {
        foreach (var st in _states.Values)
            st.RoundProcCounts.Clear();
    }

    public void SetPropDisguise(CCSPlayerController player, string modelPath)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        var state = GetOrCreate(player.Slot);
        ClearPropDisguiseInternal(state);

        // Hide player model & weapons via alpha=0 and attach a dynamic prop hidden from the owner in CheckTransmit
        state.RenderColor = Color.FromArgb(0, 255, 255, 255);
        ApplyRenderToPlayerAndGear(pawn, state.RenderColor);

        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
        if (prop == null || !prop.IsValid) return;

        prop.SetModel(modelPath);
        prop.Teleport(pawn.AbsOrigin ?? Vector.Zero, pawn.AbsRotation ?? QAngle.Zero, Vector.Zero);
        prop.DispatchSpawn();
        prop.AcceptInput("SetParent", pawn, prop, "!activator");

        state.DisguiseProp = prop;
        state.DisguisePropIndex = prop.Index;
        _entityHiddenFromSlot[prop.Index] = player.Slot;
    }

    public void ClearPropDisguise(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        if (!_states.TryGetValue(player.Slot, out var state)) return;
        ClearPropDisguiseInternal(state);

        if (TryGetAlivePawn(player, out var pawn))
        {
            state.RenderColor = Color.FromArgb(255, 255, 255, 255);
            state.HiddenFromEnemies = false;
            ApplyRenderToPlayerAndGear(pawn, state.RenderColor);
        }
    }

    private void ClearPropDisguiseInternal(TransientPlayerState state)
    {
        if (state.DisguisePropIndex > 0)
        {
            _entityHiddenFromSlot.Remove(state.DisguisePropIndex);
            state.DisguisePropIndex = 0;
        }
        if (state.DisguiseProp != null)
        {
            try
            {
                if (state.DisguiseProp.IsValid)
                    state.DisguiseProp.Remove();
            }
            catch { /* already removed */ }
            state.DisguiseProp = null;
        }
    }

    public void TrackSpawnedEntity(CCSPlayerController player, CBaseEntity entity)
    {
        if (!player.IsValid || entity == null || !entity.IsValid) return;
        GetOrCreate(player.Slot).SpawnedEntities.Add(entity);
    }

    public void SchedulePlayerTimer(CCSPlayerController player, float delaySeconds, Action callback, bool repeat = false, string? tag = null)
    {
        if (!player.IsValid || delaySeconds <= 0f) return;
        var state = GetOrCreate(player.Slot);
        TimerFlags flags = repeat ? (TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE) : TimerFlags.STOP_ON_MAPCHANGE;

        CounterStrikeSharp.API.Modules.Timers.Timer? created = null;
        created = _plugin.AddTimer(delaySeconds, () =>
        {
            if (!repeat && created != null)
            {
                state.Timers.RemoveAll(t => ReferenceEquals(t.Timer, created));
            }
            callback();
        }, flags);

        state.Timers.Add((tag, created));
    }

    public void CancelPlayerTimers(CCSPlayerController player, string? tag = null)
    {
        if (!player.IsValid) return;
        if (!_states.TryGetValue(player.Slot, out var state)) return;

        for (int i = state.Timers.Count - 1; i >= 0; i--)
        {
            if (tag == null || string.Equals(state.Timers[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                state.Timers[i].Timer.Kill();
                state.Timers.RemoveAt(i);
            }
        }
    }

    public void SetCustomData<T>(CCSPlayerController player, string key, T value)
    {
        if (!player.IsValid) return;
        GetOrCreate(player.Slot).CustomData[key] = value;
    }

    public T? GetCustomData<T>(CCSPlayerController player, string key, T? defaultValue = default)
    {
        if (!player.IsValid) return defaultValue;
        if (_states.TryGetValue(player.Slot, out var st) && st.CustomData.TryGetValue(key, out var val) && val is T typed)
            return typed;
        return defaultValue;
    }

    public bool IsPlayerHiddenFromEnemies(int slot)
        => _states.TryGetValue(slot, out var st) && st.HiddenFromEnemies;

    public bool IsEntityHiddenFromPlayer(uint entityIndex, int viewerSlot)
        => _entityHiddenFromSlot.TryGetValue(entityIndex, out var hiddenSlot) && hiddenSlot == viewerSlot;

    public bool HasAnyCheckTransmitFilters()
    {
        if (_entityHiddenFromSlot.Count > 0) return true;
        foreach (var st in _states.Values)
            if (st.HiddenFromEnemies) return true;
        return false;
    }

    public List<(int OwnerSlot, CDynamicProp Prop)> GetActiveDisguiseProps()
    {
        var list = new List<(int, CDynamicProp)>();
        foreach (var (slot, st) in _states)
        {
            if (st.DisguiseProp != null && st.DisguiseProp.IsValid)
                list.Add((slot, st.DisguiseProp));
        }
        return list;
    }

    public void OnWeaponFire(CCSPlayerController player)
    {
        if (!TryGetAlivePawn(player, out var pawn)) return;
        if (!_states.TryGetValue(player.Slot, out var state)) return;

        if (state.NoRecoilEnabled && pawn.WeaponServices?.ActiveWeapon?.Value is { IsValid: true } rw)
        {
            var csWeapon = new CCSWeaponBase(rw.Handle);
            csWeapon.AccuracyPenalty = 0f;
            Utilities.SetStateChanged(csWeapon, "CCSWeaponBase", "m_fAccuracyPenalty");
        }

        if (state.AttackSpeedMultiplier > 1.01f && pawn.WeaponServices?.ActiveWeapon?.Value is { IsValid: true } weapon)
        {
            int now = Server.TickCount;
            int delta = weapon.NextPrimaryAttackTick - now;
            if (delta > 2)
            {
                weapon.NextPrimaryAttackTick = now + Math.Max(2, (int)(delta / state.AttackSpeedMultiplier));
                Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }
        }
    }

    public void OnTick()
    {
        if (_states.Count == 0) return;

        foreach (var (slot, state) in _states)
        {
            // Fast skip if nothing tick-relevant is active for this player
            bool needsGravityCheck = Math.Abs(state.TargetGravity - 1.0f) > 0.01f || state.WasOnLadder;
            if (!needsGravityCheck && !state.JetpackEnabled && !state.NoRecoilEnabled) continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (!TryGetAlivePawn(player, out var pawn)) continue;

            // Ladder exit gravity restore
            if (needsGravityCheck)
            {
                bool onLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER;
                if (state.WasOnLadder && !onLadder && Math.Abs(state.TargetGravity - 1.0f) > 0.01f)
                {
                    ApplyGravity(pawn, state.TargetGravity);
                }
                state.WasOnLadder = onLadder;
            }

            // Jetpack upward thrust when holding Jump
            if (state.JetpackEnabled && (player!.Buttons & PlayerButtons.Jump) != 0)
            {
                var vel = pawn.AbsVelocity;
                pawn.Teleport(null, null, new Vector(vel.X, vel.Y, Math.Max(vel.Z, state.JetpackThrust)));
            }

            if (state.NoRecoilEnabled && pawn.WeaponServices?.ActiveWeapon?.Value is { IsValid: true } rw)
            {
                var csWeapon = new CCSWeaponBase(rw.Handle);
                csWeapon.AccuracyPenalty = 0f;
            }
        }
    }
}
