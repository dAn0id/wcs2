using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;
using WcsCs2.Config;

namespace WcsCs2.Services;

/// <summary>
/// Source 2 interaction layers matching CS2 HL2SDK / libserver.so (CS2 1.42.0.1).
/// </summary>
[Flags]
public enum Cs2InteractionLayers : ulong
{
    None = 0,
    Solid = 0x1,
    Hitboxes = 0x2,
    Trigger = 0x4,
    Sky = 0x8,
    PlayerClip = 0x10,
    NpcClip = 0x20,
    BlockLos = 0x40,
    BlockLight = 0x80,
    Ladder = 0x100,
    Pickup = 0x200,
    BlockSound = 0x400,
    NoDraw = 0x800,
    Window = 0x1000,
    PassBullets = 0x2000,
    WorldGeometry = 0x4000,
    Water = 0x8000,
    Slime = 0x10000,
    TouchAll = 0x20000,
    Player = 0x40000,
    Npc = 0x80000,
    Debris = 0x100000,
    PhysicsProp = 0x200000,
    StaticLevel = 0x40000000,

    /// <summary>
    /// Valve CS2 1.42.0.1 live libserver.so MASK_PLAYERSOLID | 0x8 (0x1C300BUL):
    /// world walls/floors/ceilings (0x1), windows/glass (0x2), grates/pass-bullets (0x8),
    /// player clips (0x1000), monster/npc clip (0x2000), players (0x40000), NPCs (0x80000),
    /// and solid/physics props &amp; doors (0x100000).
    /// </summary>
    MaskPlayerSolid = 0x1C300BUL
}

public readonly struct TraceHitResult
{
    public Vector3 StartPos { get; init; }
    public Vector3 EndPos { get; init; }
    public Vector3 Normal { get; init; }
    public Vector3 HitPosition { get; init; }
    public float Fraction { get; init; }
    public bool StartInSolid { get; init; }
    public IntPtr HitEntity { get; init; }

    public bool DidHit => Fraction < 1.0f || StartInSolid;
}

/// <summary>
/// Self-contained, crash-safe Source 2 physics raycast &amp; AABB hull tracer for Linux x64 CS2.
/// Supports dynamic ELF64 auto-resolution (RTTI + VProf xref + caller consensus) with
/// configurable fallback/override via configs/gamedata.json and 32-byte aligned native buffers.
/// </summary>
public sealed unsafe class Source2TraceEngine
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool TraceShapeDelegate(
        IntPtr gameTraceManager,
        NativeRay* ray,
        Vector3* start,
        Vector3* end,
        NativeTraceFilter* filter,
        NativeGameTrace* trace);

    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    private struct NativeRay
    {
        [FieldOffset(0x00)] public Vector3 Mins;
        [FieldOffset(0x0C)] public Vector3 Maxs;
        [FieldOffset(0x28)] public byte Type; // 0 = Line, 2 = Hull

        public static NativeRay Line() => new() { Type = 0 };

        public static NativeRay Hull(Vector3 mins, Vector3 maxs) => new()
        {
            Mins = mins,
            Maxs = maxs,
            Type = 2
        };
    }

    // Verified against HL2SDK cs2 gametrace.h & CS2 1.42.0.1 TraceFunc (0x1742a82..0x1742acb)
    [StructLayout(LayoutKind.Explicit, Size = 0x48)]
    private struct NativeTraceFilter
    {
        [FieldOffset(0x00)] public IntPtr Vtable;
        [FieldOffset(0x08)] public ulong InteractsWith;
        [FieldOffset(0x10)] public ulong InteractsExclude;
        [FieldOffset(0x18)] public ulong InteractsAs;
        [FieldOffset(0x20)] public uint SkipEntity0;
        [FieldOffset(0x24)] public uint SkipEntity1;
        [FieldOffset(0x28)] public uint SkipOwner0;
        [FieldOffset(0x2C)] public uint SkipOwner1;
        [FieldOffset(0x30)] public ushort Hierarchy0;
        [FieldOffset(0x32)] public ushort Hierarchy1;
        [FieldOffset(0x34)] public ushort IncludedDetailLayers;
        [FieldOffset(0x36)] public byte TargetDetailLayer;
        [FieldOffset(0x37)] public byte ObjectSetMask;
        [FieldOffset(0x38)] public byte CollisionGroup;
        [FieldOffset(0x39)] public byte Bits;
        [FieldOffset(0x3A)] public byte IterateEntities;

        public static NativeTraceFilter Create(IntPtr vtable, ulong interactsWith, uint skipEntityId, ushort hierarchyId)
        {
            return new NativeTraceFilter
            {
                Vtable = vtable,
                InteractsWith = interactsWith,
                InteractsExclude = 0,
                InteractsAs = 0,
                SkipEntity0 = skipEntityId,
                SkipEntity1 = 0xFFFFFFFFu,
                SkipOwner0 = skipEntityId,
                SkipOwner1 = 0xFFFFFFFFu,
                Hierarchy0 = hierarchyId,
                Hierarchy1 = 0,
                IncludedDetailLayers = 0xFFFF,
                TargetDetailLayer = 0,
                ObjectSetMask = 0x0F, // RNQUERY_OBJECTS_ALL (static | keyframed | dynamic | locatable)
                CollisionGroup = 3,   // Matches TraceFunc 0x4903 at [0x38..0x39] in live CS2 1.42.0.1 libserver.so
                Bits = 0x49,          // HitSolid | ShouldIgnoreDisabledPairs | IterateEntities
                IterateEntities = 0   // Both native callers (TraceFunc 0x1742acb & 0xab7c28) write 0 at [0x3A]
            };
        }
    }

    // Verified against HL2SDK cs2 gametrace.h & CS2 1.42.0.1 CGameTrace::Init (0x1e79800).
    // Padded to 0x100 (256 bytes) and allocated 32-byte aligned so 0x30 movaps can never fault.
    [StructLayout(LayoutKind.Explicit, Size = 0x100)]
    private struct NativeGameTrace
    {
        [FieldOffset(0x08)] public IntPtr HitEntity;
        [FieldOffset(0x78)] public Vector3 StartPos;
        [FieldOffset(0x84)] public Vector3 EndPos;
        [FieldOffset(0x90)] public Vector3 Normal;
        [FieldOffset(0x9C)] public Vector3 HitPosition;
        [FieldOffset(0xAC)] public float Fraction;
        [FieldOffset(0xB0)] public int Unk001;
        [FieldOffset(0xB4)] public int Triangle;
        [FieldOffset(0xB8)] public short HitboxBoneIndex;
        [FieldOffset(0xBA)] public byte RayType;
        [FieldOffset(0xBB)] public byte StartInSolid;
        [FieldOffset(0xBC)] public byte ExactHitPoint;

        public static NativeGameTrace Initialized() => new()
        {
            Fraction = 1.0f,
            Unk001 = -1,
            Triangle = -1,
            HitboxBoneIndex = -1,
            RayType = 0,
            StartInSolid = 0,
            ExactHitPoint = 0
        };
    }

    private readonly ILogger _logger;
    private GamedataConfig _gamedata;
    private TraceShapeDelegate? _traceShape;
    private IntPtr _traceShapeAddr;
    private IntPtr _gameTraceManagerPtrAddr;
    private IntPtr _traceFilterVtable;
    private IntPtr _moduleRuntimeBase;
    private string _traceShapeSource = "unresolved";
    private string _gtmSource = "unresolved";
    private string _vtableSource = "unresolved";
    private bool _initialized;

    public bool IsEnabled { get; private set; }
    public bool IsAvailable => IsEnabled && _initialized;

    public Source2TraceEngine(ILogger logger, bool enabled = false, GamedataConfig? gamedata = null)
    {
        _logger = logger;
        _gamedata = gamedata ?? new GamedataConfig();
        IsEnabled = enabled;

        if (!enabled)
        {
            _logger.LogInformation("[WcsCs2] Source2TraceEngine disabled by config (use_native_trace=false); using safe managed teleport fallback.");
            return;
        }

        EnsureInitialized(forceReinitialize: true);
    }

    public void Reload(bool enabled, GamedataConfig gamedata)
    {
        _gamedata = gamedata ?? new GamedataConfig();
        IsEnabled = enabled;

        if (!enabled)
        {
            _initialized = false;
            _traceShape = null;
            _traceShapeAddr = IntPtr.Zero;
            _gameTraceManagerPtrAddr = IntPtr.Zero;
            _traceFilterVtable = IntPtr.Zero;
            _traceShapeSource = "disabled";
            _gtmSource = "disabled";
            _vtableSource = "disabled";
            _logger.LogInformation("[WcsCs2] Source2TraceEngine disabled on reload (use_native_trace=false); using safe managed teleport fallback.");
            return;
        }

        EnsureInitialized(forceReinitialize: true);
    }

    private bool EnsureInitialized(bool forceReinitialize = false)
    {
        if (_initialized && !forceReinitialize) return true;

        _initialized = false;
        _traceShape = null;
        _traceShapeAddr = IntPtr.Zero;
        _gameTraceManagerPtrAddr = IntPtr.Zero;
        _traceFilterVtable = IntPtr.Zero;
        _traceShapeSource = "unresolved";
        _gtmSource = "unresolved";
        _vtableSource = "unresolved";

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger.LogWarning("[WcsCs2] Source2TraceEngine: Linux x64 resolution only; tracing disabled on this OS.");
            return false;
        }

        try
        {
            string serverPath = Addresses.ServerPath;
            string mode = (_gamedata.ResolutionMode ?? "auto").Trim().ToLowerInvariant();
            bool allowDynamic = mode is "auto" or "dynamic";
            bool allowGamedata = mode is "auto" or "gamedata";

            IntPtr traceShapeAddr = IntPtr.Zero;
            IntPtr gtmPtrAddr = IntPtr.Zero;
            IntPtr vtableAddr = IntPtr.Zero;
            IntPtr runtimeBase = IntPtr.Zero;

            // 1. Dynamic ELF64 resolution (survives compiler/register updates across CS2 patches)
            if (allowDynamic)
            {
                var dyn = ElfSymbolResolver.Resolve(
                    serverPath,
                    _gamedata.Dynamic.TraceShapeVprofString,
                    _gamedata.Dynamic.TraceFilterRttiName,
                    _logger);

                runtimeBase = dyn.RuntimeBase;

                if (dyn.TraceShapeAddress != IntPtr.Zero)
                {
                    traceShapeAddr = dyn.TraceShapeAddress;
                    _traceShapeSource = "dynamic-elf";
                }
                if (dyn.GameTraceManagerAddress != IntPtr.Zero)
                {
                    gtmPtrAddr = dyn.GameTraceManagerAddress;
                    _gtmSource = "dynamic-elf";
                }
                if (dyn.TraceFilterVtableAddress != IntPtr.Zero)
                {
                    vtableAddr = dyn.TraceFilterVtableAddress;
                    _vtableSource = "dynamic-elf";
                }
            }

            if (runtimeBase == IntPtr.Zero)
                runtimeBase = ElfSymbolResolver.FindModuleRuntimeBase(serverPath);

            // 2. Fallback / override via configs/gamedata.json signatures
            if (allowGamedata)
            {
                var sigs = _gamedata.LinuxSignatures;

                if (traceShapeAddr == IntPtr.Zero)
                {
                    traceShapeAddr = ResolveSignatureEntry(serverPath, sigs.TraceShape);
                    if (traceShapeAddr != IntPtr.Zero) _traceShapeSource = "gamedata";
                }

                if (gtmPtrAddr == IntPtr.Zero)
                {
                    gtmPtrAddr = ResolveSignatureEntry(serverPath, sigs.GameTraceManager);
                    if (gtmPtrAddr != IntPtr.Zero) _gtmSource = "gamedata";
                }

                if (vtableAddr == IntPtr.Zero)
                {
                    vtableAddr = ResolveSignatureEntry(serverPath, sigs.TraceFilterVtable);
                    if (vtableAddr != IntPtr.Zero) _vtableSource = "gamedata";
                }
            }

            if (traceShapeAddr == IntPtr.Zero || gtmPtrAddr == IntPtr.Zero || vtableAddr == IntPtr.Zero)
            {
                _logger.LogError(
                    "[WcsCs2] Source2TraceEngine: resolution incomplete (mode={Mode}, TraceShape=[{TS}] 0x{T:X}, GTM*=[{GS}] 0x{G:X}, FilterVtable=[{VS}] 0x{V:X}). Native tracing disabled safely.",
                    mode, _traceShapeSource, traceShapeAddr.ToInt64(), _gtmSource, gtmPtrAddr.ToInt64(), _vtableSource, vtableAddr.ToInt64());
                return false;
            }

            _moduleRuntimeBase = runtimeBase;
            _traceShapeAddr = traceShapeAddr;
            _gameTraceManagerPtrAddr = gtmPtrAddr;
            _traceFilterVtable = vtableAddr;
            _traceShape = Marshal.GetDelegateForFunctionPointer<TraceShapeDelegate>(traceShapeAddr);
            _initialized = true;

            _logger.LogInformation(
                "[WcsCs2] Source2TraceEngine initialized cleanly (mode={Mode}): " +
                "TraceShape=[{TS}] 0x{T:X} ({TOff}), GTM*=[{GS}] 0x{G:X} ({GOff}), FilterVtable=[{VS}] 0x{V:X} ({VOff}).",
                mode,
                _traceShapeSource, traceShapeAddr.ToInt64(), FormatRelOffset(traceShapeAddr, runtimeBase),
                _gtmSource, gtmPtrAddr.ToInt64(), FormatRelOffset(gtmPtrAddr, runtimeBase),
                _vtableSource, vtableAddr.ToInt64(), FormatRelOffset(vtableAddr, runtimeBase));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WcsCs2] Source2TraceEngine failed to initialize; tracing disabled safely.");
            return false;
        }
    }

    private static IntPtr ResolveSignatureEntry(string serverPath, SignatureEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Pattern))
            return IntPtr.Zero;

        IntPtr match = NativeAPI.FindSignature(serverPath, entry.Pattern);
        if (match == IntPtr.Zero) return IntPtr.Zero;

        if (entry.InstrSize > 0)
            return ResolveRipRelative(match, entry.RipOffset, entry.InstrSize);

        return match;
    }

    private static string FormatRelOffset(IntPtr addr, IntPtr runtimeBase)
    {
        if (addr == IntPtr.Zero || runtimeBase == IntPtr.Zero) return "+?";
        long diff = addr.ToInt64() - runtimeBase.ToInt64();
        return diff >= 0 ? $"+0x{diff:x}" : $"-0x{-diff:x}";
    }

    private static IntPtr ResolveRipRelative(IntPtr instructionAddress, int relOffset, int instructionSize)
    {
        if (instructionAddress == IntPtr.Zero) return IntPtr.Zero;
        int rel = *(int*)(instructionAddress + relOffset);
        return instructionAddress + rel + instructionSize;
    }

    /// <summary>
    /// Diagnostic self-test invoked ONLY via explicit server console/admin command `wcs_tracetest`.
    /// Tests both Line and Player-BBox Hull traces from a known map origin using 32-byte aligned native buffers.
    /// </summary>
    public string RunDiagnosticSelfTest(Vector3 testOrigin)
    {
        if (!EnsureInitialized(forceReinitialize: false))
            return "FAIL: Source2TraceEngine failed to initialize symbols.";

        var mins = new Vector3(-16f, -16f, 0f);
        var maxs = new Vector3(16f, 16f, 72f);

        // 1. Line trace downward to floor
        var lineStart = testOrigin + new Vector3(0f, 0f, 64f);
        var lineEnd = testOrigin - new Vector3(0f, 0f, 256f);
        var lineRay = NativeRay.Line();
        bool lineOk = TryTraceInternal(ref lineRay, lineStart, lineEnd, null, (ulong)Cs2InteractionLayers.MaskPlayerSolid, bypassEnabledCheck: true, out var lineHit);

        // 2. Player-BBox hull clearance check at spawn origin + 4z
        var hullPos = testOrigin + new Vector3(0f, 0f, 4f);
        var hullRay = NativeRay.Hull(mins, maxs);
        bool hullClearOk = TryTraceInternal(ref hullRay, hullPos, hullPos + new Vector3(0f, 0f, 0.25f), null, (ulong)Cs2InteractionLayers.MaskPlayerSolid, bypassEnabledCheck: true, out var hullClearHit);

        // 3. Player-BBox hull sweep horizontally (+X 1500u) to hit map wall/prop
        var sweepEnd = hullPos + new Vector3(1500f, 0f, 0f);
        bool hullSweepOk = TryTraceInternal(ref hullRay, hullPos, sweepEnd, null, (ulong)Cs2InteractionLayers.MaskPlayerSolid, bypassEnabledCheck: true, out var hullSweepHit);

        return $"OK [{_traceShapeSource}/{_gtmSource}/{_vtableSource} " +
               $"({FormatRelOffset(_traceShapeAddr, _moduleRuntimeBase)}/{FormatRelOffset(_gameTraceManagerPtrAddr, _moduleRuntimeBase)}/{FormatRelOffset(_traceFilterVtable, _moduleRuntimeBase)})]: " +
               $"LineDown(ok={lineOk}, frac={lineHit.Fraction:F3}, solid={lineHit.StartInSolid}, normZ={lineHit.Normal.Z:F2}, endZ={lineHit.EndPos.Z:F1}) | " +
               $"HullClear(ok={hullClearOk}, frac={hullClearHit.Fraction:F3}, solid={hullClearHit.StartInSolid}) | " +
               $"HullSweepX(ok={hullSweepOk}, frac={hullSweepHit.Fraction:F3}, solid={hullSweepHit.StartInSolid}, end=({hullSweepHit.EndPos.X:F0},{hullSweepHit.EndPos.Y:F0},{hullSweepHit.EndPos.Z:F0}))";
    }

    public bool TryTraceLine(
        Vector3 start,
        Vector3 end,
        CCSPlayerPawn? ignorePawn,
        out TraceHitResult result,
        Cs2InteractionLayers mask = Cs2InteractionLayers.MaskPlayerSolid)
    {
        var ray = NativeRay.Line();
        return TryTraceInternal(ref ray, start, end, ignorePawn, (ulong)mask, bypassEnabledCheck: false, out result);
    }

    public bool TryTraceHull(
        Vector3 start,
        Vector3 end,
        Vector3 mins,
        Vector3 maxs,
        CCSPlayerPawn? ignorePawn,
        out TraceHitResult result,
        Cs2InteractionLayers mask = Cs2InteractionLayers.MaskPlayerSolid)
    {
        var ray = NativeRay.Hull(mins, maxs);
        return TryTraceInternal(ref ray, start, end, ignorePawn, (ulong)mask, bypassEnabledCheck: false, out result);
    }

    private bool TryTraceInternal(
        ref NativeRay ray,
        Vector3 start,
        Vector3 end,
        CCSPlayerPawn? ignorePawn,
        ulong interactsWith,
        bool bypassEnabledCheck,
        out TraceHitResult result)
    {
        result = default;
        if ((!bypassEnabledCheck && !IsAvailable) || !_initialized || _traceShape == null || _gameTraceManagerPtrAddr == IntPtr.Zero)
            return false;

        IntPtr gameTraceManager = *(IntPtr*)_gameTraceManagerPtrAddr;
        if (gameTraceManager == IntPtr.Zero)
            return false;

        uint skipId = 0xFFFFFFFFu;
        ushort hierarchyId = 0;
        if (ignorePawn != null && ignorePawn.IsValid)
        {
            uint attrId = ignorePawn.Collision?.CollisionAttribute?.EntityId ?? 0u;
            skipId = attrId != 0u ? attrId : ignorePawn.Index;
            hierarchyId = ignorePawn.Collision?.CollisionAttribute?.HierarchyId ?? 0;
        }

        // Allocate a single 32-byte-aligned unmanaged slab so CGameTrace (0x30 CTransform movaps!),
        // CTraceFilter, Ray, and start/end vectors are ALL guaranteed >= 16-byte aligned and never moved by GC.
        // Layout: [0x000] NativeGameTrace (0x100) | [0x100] NativeTraceFilter (0x50) | [0x150] NativeRay (0x30) | [0x180] start (0x10) | [0x190] end (0x10)
        const nuint SlabSize = 0x1A0;
        byte* slab = (byte*)NativeMemory.AlignedAlloc(SlabSize, 32);
        try
        {
            NativeMemory.Clear(slab, SlabSize);

            NativeGameTrace* pTrace = (NativeGameTrace*)(slab + 0x000);
            NativeTraceFilter* pFilter = (NativeTraceFilter*)(slab + 0x100);
            NativeRay* pRay = (NativeRay*)(slab + 0x150);
            Vector3* pStart = (Vector3*)(slab + 0x180);
            Vector3* pEnd = (Vector3*)(slab + 0x190);

            *pTrace = NativeGameTrace.Initialized();
            *pFilter = NativeTraceFilter.Create(_traceFilterVtable, interactsWith, skipId, hierarchyId);
            *pRay = ray;
            *pStart = start;
            *pEnd = end;

            _traceShape(gameTraceManager, pRay, pStart, pEnd, pFilter, pTrace);

            NativeGameTrace trace = *pTrace;

            // Guard against NaN/invalid outputs from engine
            if (!float.IsFinite(trace.Fraction) || !float.IsFinite(trace.EndPos.X) ||
                !float.IsFinite(trace.EndPos.Y) || !float.IsFinite(trace.EndPos.Z))
            {
                return false;
            }

            result = new TraceHitResult
            {
                StartPos = trace.StartPos,
                EndPos = trace.EndPos,
                Normal = trace.Normal,
                HitPosition = trace.HitPosition,
                Fraction = Math.Clamp(trace.Fraction, 0f, 1f),
                StartInSolid = trace.StartInSolid != 0,
                HitEntity = trace.HitEntity
            };
            return true;
        }
        finally
        {
            NativeMemory.AlignedFree(slab);
        }
    }
}
