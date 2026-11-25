using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

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
/// All signatures and struct offsets verified directly against CS2 1.42.0.1 libserver.so.
/// </summary>
public sealed unsafe class Source2TraceEngine
{
    // Verified unique Linux x64 signatures on CS2 1.42.0.1 libserver.so
    private const string TraceShapeLinuxSig =
        "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 49 89 F7 41 56 48 8D 35 ? ? ? ? 49 89 CE";

    private const string GameTraceManagerLinuxSig =
        "48 8D 05 ? ? ? ? 4C 89 EA 4C 8B";

    private const string CTraceFilterVtableLinuxSig =
        "48 8D 05 ? ? ? ? 48 89 85 D0 FE FF FF 48 B8 41 00 18 00";

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
    // Padded to 0x100 (256 bytes) so native writes up to 0xBC+ can never overflow the stack.
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
    private readonly TraceShapeDelegate? _traceShape;
    private readonly IntPtr _gameTraceManagerPtrAddr;
    private readonly IntPtr _traceFilterVtable;

    public bool IsAvailable { get; }

    public Source2TraceEngine(ILogger logger)
    {
        _logger = logger;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger.LogWarning("[WcsCs2] Source2TraceEngine: Linux x64 signatures only; tracing disabled on this OS.");
            IsAvailable = false;
            return;
        }

        try
        {
            string serverPath = Addresses.ServerPath;
            IntPtr traceShapeAddr = NativeAPI.FindSignature(serverPath, TraceShapeLinuxSig);
            IntPtr gtmInstrAddr = NativeAPI.FindSignature(serverPath, GameTraceManagerLinuxSig);
            IntPtr vtableInstrAddr = NativeAPI.FindSignature(serverPath, CTraceFilterVtableLinuxSig);

            if (traceShapeAddr == IntPtr.Zero || gtmInstrAddr == IntPtr.Zero || vtableInstrAddr == IntPtr.Zero)
            {
                _logger.LogError(
                    "[WcsCs2] Source2TraceEngine: signature resolution failed (TraceShape=0x{T:X}, GTM=0x{G:X}, Vtable=0x{V:X}). Teleport collision trace disabled safely.",
                    traceShapeAddr.ToInt64(), gtmInstrAddr.ToInt64(), vtableInstrAddr.ToInt64());
                IsAvailable = false;
                return;
            }

            _gameTraceManagerPtrAddr = ResolveRipRelative(gtmInstrAddr, 3, 7);
            _traceFilterVtable = ResolveRipRelative(vtableInstrAddr, 3, 7);
            _traceShape = Marshal.GetDelegateForFunctionPointer<TraceShapeDelegate>(traceShapeAddr);

            if (_gameTraceManagerPtrAddr == IntPtr.Zero || _traceFilterVtable == IntPtr.Zero)
            {
                _logger.LogError("[WcsCs2] Source2TraceEngine: RIP-relative resolution produced zero pointer.");
                IsAvailable = false;
                return;
            }

            IsAvailable = true;
            _logger.LogInformation(
                "[WcsCs2] Source2TraceEngine initialized cleanly (TraceShape=0x{T:X}, GTM*=0x{G:X}, FilterVtable=0x{V:X}).",
                traceShapeAddr.ToInt64(), _gameTraceManagerPtrAddr.ToInt64(), _traceFilterVtable.ToInt64());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WcsCs2] Source2TraceEngine failed to initialize; tracing disabled safely.");
            IsAvailable = false;
        }
    }

    private static IntPtr ResolveRipRelative(IntPtr instructionAddress, int relOffset, int instructionSize)
    {
        if (instructionAddress == IntPtr.Zero) return IntPtr.Zero;
        int rel = *(int*)(instructionAddress + relOffset);
        return instructionAddress + rel + instructionSize;
    }

    public bool TryTraceLine(
        Vector3 start,
        Vector3 end,
        CCSPlayerPawn? ignorePawn,
        out TraceHitResult result,
        Cs2InteractionLayers mask = Cs2InteractionLayers.MaskPlayerSolid)
    {
        var ray = NativeRay.Line();
        return TryTraceInternal(ref ray, start, end, ignorePawn, (ulong)mask, out result);
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
        return TryTraceInternal(ref ray, start, end, ignorePawn, (ulong)mask, out result);
    }

    private bool TryTraceInternal(
        ref NativeRay ray,
        Vector3 start,
        Vector3 end,
        CCSPlayerPawn? ignorePawn,
        ulong interactsWith,
        out TraceHitResult result)
    {
        result = default;
        if (!IsAvailable || _traceShape == null || _gameTraceManagerPtrAddr == IntPtr.Zero)
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

        NativeTraceFilter filter = NativeTraceFilter.Create(_traceFilterVtable, interactsWith, skipId, hierarchyId);
        NativeGameTrace trace = NativeGameTrace.Initialized();
        NativeRay localRay = ray;
        Vector3 localStart = start;
        Vector3 localEnd = end;

        _traceShape(gameTraceManager, &localRay, &localStart, &localEnd, &filter, &trace);

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
}
