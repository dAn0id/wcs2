using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WCS2.Services;

public sealed class ElfResolvedSymbols
{
    public IntPtr RuntimeBase { get; init; }
    public ulong TraceShapeOffset { get; init; }
    public ulong GameTraceManagerOffset { get; init; }
    public ulong TraceFilterVtableOffset { get; init; }

    public IntPtr TraceShapeAddress => TraceShapeOffset != 0 && RuntimeBase != IntPtr.Zero
        ? (IntPtr)(RuntimeBase.ToInt64() + (long)TraceShapeOffset)
        : IntPtr.Zero;

    public IntPtr GameTraceManagerAddress => GameTraceManagerOffset != 0 && RuntimeBase != IntPtr.Zero
        ? (IntPtr)(RuntimeBase.ToInt64() + (long)GameTraceManagerOffset)
        : IntPtr.Zero;

    public IntPtr TraceFilterVtableAddress => TraceFilterVtableOffset != 0 && RuntimeBase != IntPtr.Zero
        ? (IntPtr)(RuntimeBase.ToInt64() + (long)TraceFilterVtableOffset)
        : IntPtr.Zero;
}

/// <summary>
/// Pure-managed, disk-only ELF64 analyzer for Linux x86_64 libserver.so.
/// Resolves TraceShape (via VProf string xref + .eh_frame_hdr/prologue),
/// GameTraceManager (via TraceShape caller consensus), and CTraceFilter vtable
/// (via Itanium ELF RTTI in .rodata / .data.rel.ro / .rela.dyn).
/// Never dereferences unmapped runtime memory.
/// </summary>
public static class ElfSymbolResolver
{
    private const uint ShtRela = 4;
    private const uint ShtNoBits = 8;
    private const uint PtLoad = 1;
    private const uint RelTypeX8664Relative = 8;

    private readonly struct ElfSection
    {
        public string Name { get; init; }
        public uint Type { get; init; }
        public ulong Vaddr { get; init; }
        public ulong FileOffset { get; init; }
        public ulong Size { get; init; }

        public bool ContainsVaddr(ulong addr) => Size > 0 && addr >= Vaddr && addr < Vaddr + Size;
    }

    public static IntPtr FindModuleRuntimeBase(string modulePath, ulong firstLoadVaddr = 0)
    {
        try
        {
            if (!File.Exists("/proc/self/maps")) return IntPtr.Zero;

            string fullTarget = Path.GetFullPath(modulePath);
            string fileName = "/" + Path.GetFileName(modulePath);
            IntPtr fallbackMatch = IntPtr.Zero;

            foreach (string line in File.ReadLines("/proc/self/maps"))
            {
                // Format: start-end perms offset dev inode pathname
                int firstSpace = line.IndexOf(' ');
                if (firstSpace <= 0) continue;

                int dash = line.IndexOf('-');
                if (dash <= 0 || dash >= firstSpace) continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;

                string mappedPath = parts[5];
                bool exact = string.Equals(mappedPath, fullTarget, StringComparison.Ordinal);
                bool suffix = !exact && mappedPath.EndsWith(fileName, StringComparison.Ordinal);
                if (!exact && !suffix) continue;

                if (!ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong fileOffset))
                    continue;
                if (fileOffset != 0) continue;

                if (!ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong mapStart))
                    continue;

                IntPtr loadBias = (IntPtr)unchecked((long)(mapStart - firstLoadVaddr));
                if (exact) return loadBias;
                if (fallbackMatch == IntPtr.Zero) fallbackMatch = loadBias;
            }

            return fallbackMatch;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static ElfResolvedSymbols Resolve(
        string serverPath,
        string traceShapeVprofString,
        string traceFilterRttiName,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: server binary not found at '{Path}'.", serverPath);
            return new ElfResolvedSymbols();
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(serverPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WCS2] ElfSymbolResolver: failed reading '{Path}'.", serverPath);
            return new ElfResolvedSymbols();
        }

        if (!TryParseElf64(data, out ulong firstLoadVaddr, out var sections))
        {
            logger.LogError("[WCS2] ElfSymbolResolver: '{Path}' is not a valid Linux x86_64 ELF64 binary.", serverPath);
            return new ElfResolvedSymbols();
        }

        IntPtr runtimeBase = FindModuleRuntimeBase(serverPath, firstLoadVaddr);
        if (runtimeBase == IntPtr.Zero)
        {
            logger.LogError("[WCS2] ElfSymbolResolver: could not locate runtime base of '{Path}' in /proc/self/maps.", serverPath);
            return new ElfResolvedSymbols();
        }

        sections.TryGetValue(".text", out var textSec);
        sections.TryGetValue(".data.rel.ro", out var dataRelRoSec);
        sections.TryGetValue(".data", out var dataSec);
        sections.TryGetValue(".bss", out var bssSec);
        sections.TryGetValue(".eh_frame_hdr", out var ehFrameHdrSec);
        sections.TryGetValue(".rela.dyn", out var relaDynSec);

        // Collect all read-only data/string sections (.rodata, .rodata.str1.1, .rodata.str1.8, etc.)
        var rodataSections = sections.Values
            .Where(s => s.Type != ShtNoBits && s.Size > 0 &&
                        (s.Name == ".rodata" || s.Name.StartsWith(".rodata.", StringComparison.Ordinal)))
            .ToList();

        ulong traceShapeOff = 0;
        ulong gtmOff = 0;
        ulong filterVtableOff = 0;

        // Symbol A: CTraceFilter vtable via ELF RTTI
        if (!string.IsNullOrWhiteSpace(traceFilterRttiName) && rodataSections.Count > 0 && dataRelRoSec.Size > 0 && textSec.Size > 0)
        {
            filterVtableOff = ResolveVtableByRtti(
                data, traceFilterRttiName, rodataSections, dataRelRoSec, relaDynSec, textSec, logger);
        }

        // Symbol B: TraceShape via unique VProf string xref + enclosing function start
        if (!string.IsNullOrWhiteSpace(traceShapeVprofString) && rodataSections.Count > 0 && textSec.Size > 0)
        {
            traceShapeOff = ResolveFunctionByStringXref(
                data, traceShapeVprofString, rodataSections, textSec, ehFrameHdrSec, logger);
        }

        // Symbol C: GameTraceManager via TraceShape caller consensus
        if (traceShapeOff != 0 && textSec.Size > 0 && (bssSec.Size > 0 || dataSec.Size > 0))
        {
            gtmOff = ResolveGlobalFromCallerConsensus(
                data, traceShapeOff, textSec, dataSec, bssSec, logger);
        }

        return new ElfResolvedSymbols
        {
            RuntimeBase = runtimeBase,
            TraceShapeOffset = traceShapeOff,
            GameTraceManagerOffset = gtmOff,
            TraceFilterVtableOffset = filterVtableOff
        };
    }

    private static bool TryParseElf64(
        byte[] data,
        out ulong firstLoadVaddr,
        out Dictionary<string, ElfSection> sections)
    {
        firstLoadVaddr = 0;
        sections = new Dictionary<string, ElfSection>(StringComparer.Ordinal);

        if (data.Length < 64) return false;
        // Magic 0x7F 'E' 'L' 'F', EI_CLASS=2 (64-bit), EI_DATA=1 (LSB)
        if (data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            return false;
        if (data[4] != 2 || data[5] != 1) return false;

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18, 2));
        if (machine != 62) return false; // EM_X86_64

        ulong phoff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32, 8));
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40, 8));
        ushort phentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(54, 2));
        ushort phnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(56, 2));
        ushort shentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(58, 2));
        ushort shnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(60, 2));
        ushort shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(62, 2));

        // Find first PT_LOAD segment with file offset 0 for exact load-bias calculation
        if (phoff > 0 && phentsize >= 56 && phnum > 0)
        {
            for (int i = 0; i < phnum; i++)
            {
                ulong off = phoff + (ulong)i * phentsize;
                if (off + 56 > (ulong)data.Length) break;
                uint pType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off, 4));
                if (pType != PtLoad) continue;
                ulong pOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(off + 8), 8));
                ulong pVaddr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(off + 16), 8));
                if (pOffset == 0)
                {
                    firstLoadVaddr = pVaddr;
                    break;
                }
            }
        }

        if (shoff == 0 || shentsize < 64 || shnum == 0 || shstrndx >= shnum)
            return false;

        ulong shstrHeaderOff = shoff + (ulong)shstrndx * shentsize;
        if (shstrHeaderOff + 64 > (ulong)data.Length) return false;

        ulong strTabFileOff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(shstrHeaderOff + 24), 8));
        ulong strTabSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(shstrHeaderOff + 32), 8));
        if (strTabFileOff + strTabSize > (ulong)data.Length) return false;

        for (int i = 0; i < shnum; i++)
        {
            ulong hdrOff = shoff + (ulong)i * shentsize;
            if (hdrOff + 64 > (ulong)data.Length) break;

            uint nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)hdrOff, 4));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(hdrOff + 4), 4));
            ulong vaddr = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(hdrOff + 16), 8));
            ulong fileOff = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(hdrOff + 24), 8));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)(hdrOff + 32), 8));

            if (type != ShtNoBits && (fileOff > (ulong)data.Length || size > (ulong)data.Length - fileOff))
                continue;

            string name = ReadNullTerminatedAscii(data, strTabFileOff, strTabSize, nameIdx);
            if (!string.IsNullOrEmpty(name))
            {
                sections[name] = new ElfSection
                {
                    Name = name,
                    Type = type,
                    Vaddr = vaddr,
                    FileOffset = fileOff,
                    Size = size
                };
            }
        }

        return sections.ContainsKey(".text") && sections.ContainsKey(".rodata");
    }

    private static string ReadNullTerminatedAscii(byte[] data, ulong tableOff, ulong tableSize, uint nameIdx)
    {
        if (nameIdx >= tableSize) return string.Empty;
        int start = (int)(tableOff + nameIdx);
        int maxEnd = (int)(tableOff + tableSize);
        int end = start;
        while (end < maxEnd && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, start, end - start);
    }

    /// <summary>
    /// Finds all exact null-terminated occurrences of an ASCII string across all read-only data sections
    /// (.rodata, .rodata.str1.1, etc.), requiring a NUL boundary before each match so suffixes never false-match.
    /// </summary>
    private static List<ulong> FindExactStringsInSections(byte[] data, IReadOnlyList<ElfSection> sections, string text)
    {
        var matches = new List<ulong>();
        byte[] needle = Encoding.ASCII.GetBytes(text + "\0");
        if (needle.Length == 0) return matches;

        foreach (var section in sections)
        {
            if (section.Size < (ulong)needle.Length) continue;
            ReadOnlySpan<byte> span = data.AsSpan((int)section.FileOffset, (int)section.Size);
            int searchFrom = 0;

            while (searchFrom <= span.Length - needle.Length)
            {
                int idx = span.Slice(searchFrom).IndexOf(needle);
                if (idx < 0) break;

                int absIdx = searchFrom + idx;
                // Ensure exact string boundary (start of section or preceded by NUL byte)
                if (absIdx == 0 || span[absIdx - 1] == 0)
                {
                    matches.Add(section.Vaddr + (ulong)absIdx);
                }
                searchFrom = absIdx + 1;
            }
        }

        return matches;
    }

    /// <summary>
    /// Resolves C++ primary vtable pointer (vtable_start + 0x10) for a given Itanium RTTI mangled name
    /// (e.g. "12CTraceFilter"). Supports both SHT_RELA (.rela.dyn R_X86_64_RELATIVE) and in-place SHT_RELR/raw pointers.
    /// </summary>
    private static ulong ResolveVtableByRtti(
        byte[] data,
        string rttiName,
        IReadOnlyList<ElfSection> rodataSections,
        ElfSection dataRelRo,
        ElfSection relaDyn,
        ElfSection textSec,
        ILogger logger)
    {
        var nameVaddrs = FindExactStringsInSections(data, rodataSections, rttiName);
        if (nameVaddrs.Count != 1)
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: RTTI name '{Name}' matched {Count} times in .rodata* (expected 1).",
                rttiName, nameVaddrs.Count);
            return 0;
        }
        ulong nameVaddr = nameVaddrs[0];

        // Index R_X86_64_RELATIVE relocations targeting .data.rel.ro slots (slot_vaddr -> target_vaddr)
        var relocMap = new Dictionary<ulong, ulong>();
        var reverseRelocs = new Dictionary<ulong, List<ulong>>();

        if (relaDyn.Size >= 24 && relaDyn.Type == ShtRela)
        {
            int count = (int)(relaDyn.Size / 24);
            int baseOff = (int)relaDyn.FileOffset;
            for (int i = 0; i < count; i++)
            {
                int off = baseOff + i * 24;
                ulong rOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off, 8));
                if (!dataRelRo.ContainsVaddr(rOffset)) continue;

                ulong rInfo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8, 8));
                if ((uint)rInfo != RelTypeX8664Relative) continue;

                ulong rAddend = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 16, 8));
                relocMap[rOffset] = rAddend;

                if (rAddend == nameVaddr || dataRelRo.ContainsVaddr(rAddend))
                {
                    if (!reverseRelocs.TryGetValue(rAddend, out var list))
                    {
                        list = new List<ulong>(2);
                        reverseRelocs[rAddend] = list;
                    }
                    list.Add(rOffset);
                }
            }
        }

        ulong ReadPointerInDataRelRo(ulong vaddr)
        {
            if (relocMap.TryGetValue(vaddr, out ulong relocTarget))
                return relocTarget;
            if (!dataRelRo.ContainsVaddr(vaddr) || vaddr + 8 > dataRelRo.Vaddr + dataRelRo.Size)
                return 0;
            int fileOff = (int)(dataRelRo.FileOffset + (vaddr - dataRelRo.Vaddr));
            return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(fileOff, 8));
        }

        // Step 1: Find typeinfo object where [typeinfo + 0x08] == nameVaddr
        var typeinfoCandidates = new HashSet<ulong>();
        if (reverseRelocs.TryGetValue(nameVaddr, out var relocSlots))
        {
            foreach (ulong slot in relocSlots)
            {
                if (slot >= dataRelRo.Vaddr + 8 && (slot & 7) == 0)
                    typeinfoCandidates.Add(slot - 8);
            }
        }

        // Also scan raw .data.rel.ro in case pointers are stored in-place (SHT_RELR)
        if (typeinfoCandidates.Count == 0)
        {
            int baseOff = (int)dataRelRo.FileOffset;
            int len = (int)dataRelRo.Size;
            for (int i = 8; i + 8 <= len; i += 8)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(baseOff + i, 8)) == nameVaddr)
                    typeinfoCandidates.Add(dataRelRo.Vaddr + (ulong)(i - 8));
            }
        }

        if (typeinfoCandidates.Count != 1)
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: RTTI '{Name}' found {Count} typeinfo candidates (expected 1).",
                rttiName, typeinfoCandidates.Count);
            return 0;
        }
        ulong typeinfoVaddr = typeinfoCandidates.First();

        // Step 2: Find primary vtable where [vtable + 0x00] == 0 (offset-to-top),
        // [vtable + 0x08] == typeinfoVaddr, and [vtable + 0x10] points into .text.
        var vtableSlots = new List<ulong>();
        if (reverseRelocs.TryGetValue(typeinfoVaddr, out var vtRelocSlots))
        {
            vtableSlots.AddRange(vtRelocSlots);
        }
        else
        {
            int baseOff = (int)dataRelRo.FileOffset;
            int len = (int)dataRelRo.Size;
            for (int i = 8; i + 16 <= len; i += 8)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(baseOff + i, 8)) == typeinfoVaddr)
                    vtableSlots.Add(dataRelRo.Vaddr + (ulong)i);
            }
        }

        var validVtables = new List<ulong>();
        foreach (ulong rttiPtrSlot in vtableSlots)
        {
            if (rttiPtrSlot < dataRelRo.Vaddr + 8 || (rttiPtrSlot & 7) != 0) continue;
            ulong vtableStart = rttiPtrSlot - 8;
            if (vtableStart + 24 > dataRelRo.Vaddr + dataRelRo.Size) continue;

            // Offset-to-top at [vtableStart + 0x00] must be 0 for primary vtable
            ulong offsetToTop = ReadPointerInDataRelRo(vtableStart);
            if (offsetToTop != 0) continue;

            // First virtual method pointer at [vtableStart + 0x10] must point into .text
            ulong firstMethod = ReadPointerInDataRelRo(vtableStart + 0x10);
            if (!textSec.ContainsVaddr(firstMethod)) continue;

            validVtables.Add(vtableStart + 0x10);
        }

        if (validVtables.Count != 1)
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: RTTI '{Name}' found {Count} primary vtables (expected 1).",
                rttiName, validVtables.Count);
            return 0;
        }

        return validVtables[0];
    }

    /// <summary>
    /// Resolves TraceShape function start by finding the unique RIP-relative LEA in .text
    /// referencing the VProf string, then locating the enclosing function via .eh_frame_hdr
    /// (with backward prologue scan fallback).
    /// </summary>
    private static ulong ResolveFunctionByStringXref(
        byte[] data,
        string vprofString,
        IReadOnlyList<ElfSection> rodataSections,
        ElfSection textSec,
        ElfSection ehFrameHdrSec,
        ILogger logger)
    {
        var strMatches = FindExactStringsInSections(data, rodataSections, vprofString);
        if (strMatches.Count != 1)
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: VProf string '{Str}' matched {Count} times in .rodata* (expected 1).",
                vprofString, strMatches.Count);
            return 0;
        }
        ulong strVaddr = strMatches[0];

        var xrefs = new List<ulong>();
        int textBase = (int)textSec.FileOffset;
        int textLen = (int)textSec.Size;

        for (int i = 0; i <= textLen - 7; i++)
        {
            byte b0 = data[textBase + i];
            if (b0 != 0x48 && b0 != 0x4C) continue;
            if (data[textBase + i + 1] != 0x8D) continue;
            if ((data[textBase + i + 2] & 0xC7) != 0x05) continue;

            int disp32 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(textBase + i + 3, 4));
            ulong target = unchecked(textSec.Vaddr + (ulong)i + 7UL + (ulong)(long)disp32);
            if (target == strVaddr)
            {
                xrefs.Add(textSec.Vaddr + (ulong)i);
            }
        }

        if (xrefs.Count != 1)
        {
            logger.LogWarning("[WCS2] ElfSymbolResolver: VProf string '{Str}' had {Count} .text LEA xrefs (expected 1).",
                vprofString, xrefs.Count);
            return 0;
        }

        ulong xrefVaddr = xrefs[0];

        // Primary: exact compiler unwind table lookup via .eh_frame_hdr binary search table
        if (ehFrameHdrSec.Size >= 12 &&
            TryFindFunctionStartFromEhFrameHdr(data, ehFrameHdrSec, xrefVaddr, out ulong ehFuncStart) &&
            textSec.ContainsVaddr(ehFuncStart) &&
            xrefVaddr >= ehFuncStart &&
            xrefVaddr - ehFuncStart <= 4096)
        {
            return ehFuncStart;
        }

        // Fallback: walk backward up to 2048 bytes to a 16-byte aligned function entry (`push rbp` 0x55
        // preceded by 0xCC/0x90 inter-function padding, with `mov rbp, rsp` 48 89 E5 within 16 bytes).
        // VProf string initialization often lives in a cold branch several hundred bytes past entry.
        ulong xrefTextOff = xrefVaddr - textSec.Vaddr;
        int maxBack = (int)Math.Min(2048UL, xrefTextOff);
        for (int back = 1; back <= maxBack; back++)
        {
            int pos = textBase + (int)xrefTextOff - back;
            if (data[pos] != 0x55) continue; // push rbp

            ulong candVaddr = textSec.Vaddr + xrefTextOff - (ulong)back;
            if ((candVaddr & 0xF) != 0) continue;

            bool padded = pos == textBase || data[pos - 1] == 0xCC || data[pos - 1] == 0x90;
            if (!padded) continue;

            // Confirm `mov rbp, rsp` (48 89 E5) appears within 16 bytes of function entry
            int checkLimit = Math.Min(textBase + textLen - 3, pos + 16);
            bool hasFrameSetup = false;
            for (int k = pos + 1; k <= checkLimit; k++)
            {
                if (data[k] == 0x48 && data[k + 1] == 0x89 && data[k + 2] == 0xE5)
                {
                    hasFrameSetup = true;
                    break;
                }
            }
            if (hasFrameSetup)
                return candVaddr;
        }

        logger.LogWarning("[WCS2] ElfSymbolResolver: could not determine enclosing function start for xref 0x{Xref:X}.", xrefVaddr);
        return 0;
    }

    private static bool TryFindFunctionStartFromEhFrameHdr(
        byte[] data,
        ElfSection ehHdr,
        ulong targetVaddr,
        out ulong funcStart)
    {
        funcStart = 0;
        int off = (int)ehHdr.FileOffset;
        int len = (int)ehHdr.Size;
        if (len < 12) return false;

        byte version = data[off];
        byte ehFramePtrEnc = data[off + 1];
        byte fdeCountEnc = data[off + 2];
        byte tableEnc = data[off + 3];

        // Standard Linux x86_64 GCC/Clang encoding:
        // version=1, eh_frame_ptr_enc=0x1B (pcrel|sdata4), fde_count_enc=0x03 (udata4), table_enc=0x3B (datarel|sdata4)
        if (version != 1 || (ehFramePtrEnc & 0x0F) != 0x0B || fdeCountEnc != 0x03 || tableEnc != 0x3B)
            return false;

        uint fdeCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8, 4));
        if (fdeCount == 0 || 12UL + (ulong)fdeCount * 8UL > (ulong)len)
            return false;

        int tableStart = off + 12;
        int lo = 0;
        int hi = (int)fdeCount - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int rel = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tableStart + mid * 8, 4));
            ulong entryVaddr = unchecked(ehHdr.Vaddr + (ulong)(long)rel);

            if (entryVaddr <= targetVaddr)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0) return false;
        int bestRel = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tableStart + best * 8, 4));
        funcStart = unchecked(ehHdr.Vaddr + (ulong)(long)bestRel);
        return true;
    }

    /// <summary>
    /// Resolves &amp;g_pGameTraceManager by scanning all .text call sites of TraceShape
    /// and taking strong consensus over RIP-relative .bss/.data globals loaded into rdi (or GP reg)
    /// within 64 bytes before each call.
    /// </summary>
    private static ulong ResolveGlobalFromCallerConsensus(
        byte[] data,
        ulong traceShapeVaddr,
        ElfSection textSec,
        ElfSection dataSec,
        ElfSection bssSec,
        ILogger logger)
    {
        int textBase = (int)textSec.FileOffset;
        int textLen = (int)textSec.Size;

        var rdiCounts = new Dictionary<ulong, int>();
        var anyRegCounts = new Dictionary<ulong, int>();
        int totalCallers = 0;
        int callersWithRdi = 0;
        int callersWithAny = 0;

        bool IsGlobalData(ulong addr) => bssSec.ContainsVaddr(addr) || dataSec.ContainsVaddr(addr);

        for (int i = 0; i <= textLen - 5; i++)
        {
            if (data[textBase + i] != 0xE8) continue; // call rel32
            int rel32 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(textBase + i + 1, 4));
            ulong callTarget = unchecked(textSec.Vaddr + (ulong)i + 5UL + (ulong)(long)rel32);
            if (callTarget != traceShapeVaddr) continue;

            totalCallers++;

            int winStart = Math.Max(0, i - 64);
            int winEnd = i - 7;
            var seenRdi = new HashSet<ulong>();
            var seenAny = new HashSet<ulong>();

            for (int j = winStart; j <= winEnd; j++)
            {
                byte b0 = data[textBase + j];
                if (b0 != 0x48 && b0 != 0x4C) continue;
                byte b1 = data[textBase + j + 1];
                if (b1 != 0x8B && b1 != 0x8D) continue; // mov or lea
                byte b2 = data[textBase + j + 2];
                if ((b2 & 0xC7) != 0x05) continue; // RIP-relative [rip + disp32]

                int disp32 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(textBase + j + 3, 4));
                ulong target = unchecked(textSec.Vaddr + (ulong)j + 7UL + (ulong)(long)disp32);
                if (!IsGlobalData(target) || (target & 7) != 0) continue;

                // Direct `mov rdi, [rip + disp32]` (48 8B 3D) loads arg0 directly
                if (b0 == 0x48 && b1 == 0x8B && b2 == 0x3D)
                {
                    if (seenRdi.Add(target))
                        rdiCounts[target] = rdiCounts.GetValueOrDefault(target) + 1;
                }

                if (seenAny.Add(target))
                    anyRegCounts[target] = anyRegCounts.GetValueOrDefault(target) + 1;
            }

            if (seenRdi.Count > 0) callersWithRdi++;
            if (seenAny.Count > 0) callersWithAny++;
        }

        // Prefer strict `mov rdi, [rip + disp32]` consensus (>= 5 callers AND > 60% of rdi-loading callers)
        if (TryPickConsensus(rdiCounts, callersWithRdi, minHits: 5, minRatio: 0.60, out ulong bestRdi, out int rdiHits))
        {
            logger.LogInformation(
                "[WCS2] ElfSymbolResolver: GameTraceManager resolved via mov-rdi caller consensus (+0x{Off:X}, {Hits}/{Total} rdi callers, {AllCallers} total TraceShape calls).",
                bestRdi, rdiHits, callersWithRdi, totalCallers);
            return bestRdi;
        }

        // Fallback: general mov/lea into .bss/.data consensus (>= 5 callers, > 50% of callers, and >= 2x runner-up)
        if (TryPickConsensus(anyRegCounts, callersWithAny, minHits: 5, minRatio: 0.50, out ulong bestAny, out int anyHits))
        {
            logger.LogInformation(
                "[WCS2] ElfSymbolResolver: GameTraceManager resolved via caller consensus (+0x{Off:X}, {Hits}/{Total} callers, {AllCallers} total TraceShape calls).",
                bestAny, anyHits, callersWithAny, totalCallers);
            return bestAny;
        }

        logger.LogWarning(
            "[WCS2] ElfSymbolResolver: GameTraceManager caller consensus insufficient (totalCallers={All}, rdiCallers={Rdi}, anyCallers={Any}).",
            totalCallers, callersWithRdi, callersWithAny);
        return 0;
    }

    private static bool TryPickConsensus(
        Dictionary<ulong, int> counts,
        int callerDenominator,
        int minHits,
        double minRatio,
        out ulong bestAddr,
        out int bestHits)
    {
        bestAddr = 0;
        bestHits = 0;
        if (counts.Count == 0 || callerDenominator < minHits) return false;

        int runnerUp = 0;
        foreach (var (addr, hits) in counts)
        {
            if (hits > bestHits)
            {
                runnerUp = bestHits;
                bestHits = hits;
                bestAddr = addr;
            }
            else if (hits > runnerUp)
            {
                runnerUp = hits;
            }
        }

        double ratio = (double)bestHits / callerDenominator;
        return bestHits >= minHits && ratio > minRatio && (runnerUp == 0 || bestHits >= runnerUp * 2);
    }
}
