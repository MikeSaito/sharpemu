// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Audio;

public static class AudioPropagationExports
{
    // Soft sizes for Astro RoomLoad: QueryMemory/Create must succeed so
    // AudioPropagationContext does not soft-assert and tear down the guest.
    private const ulong SystemMemorySize = 0x100000;
    private const ulong SystemMemoryAlignment = 0x10000;
    private const int SystemBlockSize = 0x100;
    // Astro post-Room path touches fields past +0x100 (stack held room+0x130).
    // Undersized soft rooms left nested pointer slots as NULL → write AV at
    // 0x80000048F (mov [rsi], rdx after mov rsi,[rsi]).
    private const int OpaqueBlockSize = 0x1000;
    private const int OpaqueScratchOffset = 0x800;
    private static int _nextSystemHandle;
    private static int _nextOpaqueSerial;
    private static ulong _lastSystemAddress;
    private static uint _lastGrainCount;
    private static int _grainAssertPatched;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint hProcess, nint lpBaseAddress, nuint dwSize);

    [DllImport("kernel32")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32", SetLastError = true)]
    private static extern nuint VirtualQuery(nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private static void LogGrainPatchProbe(ulong address, string tag)
    {
        Span<byte> sample = stackalloc byte[16];
        var readable = TryReadGuestBytes(address, sample);
        var sampleHex = readable ? Convert.ToHexString(sample) : "unreadable";
        var vq = VirtualQuery(unchecked((nint)(long)address), out var mbi, (nuint)System.Runtime.CompilerServices.Unsafe.SizeOf<MemoryBasicInformation>());
        Console.Error.WriteLine(
            $"[LOADER][WARN] audio_prop.grain_assert_probe {tag} @0x{address:X} bytes={sampleHex} vq=0x{vq:X} state=0x{mbi.State:X} protect=0x{mbi.Protect:X} base=0x{(ulong)mbi.BaseAddress:X} size=0x{mbi.RegionSize:X}");
    }

    private static bool TryReadGuestBytes(ulong address, Span<byte> buffer)
    {
        try
        {
            unsafe
            {
                fixed (byte* dst = buffer)
                {
                    Buffer.MemoryCopy(
                        (void*)unchecked((nint)(long)address),
                        dst,
                        buffer.Length,
                        buffer.Length);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNopGuestBytes(ulong address, int length)
    {
        var nops = new byte[length];
        nops.AsSpan().Fill(0x90);
        if (!VirtualProtect(unchecked((nint)(long)address), (nuint)length, 0x40, out var oldProtect))
        {
            return false;
        }

        Marshal.Copy(nops, 0, unchecked((nint)(long)address), length);
        _ = VirtualProtect(unchecked((nint)(long)address), (nuint)length, oldProtect, out _);
        _ = FlushInstructionCache(GetCurrentProcess(), unchecked((nint)(long)address), (nuint)length);
        return true;
    }

    // AudioPropagationContext.cpp:326/327:
    //   cmp r15d,0x10 / jne soft_assert
    //   mov r12d,ebx / movsxd rsi,ebx / cmp [r13+0x20],rsi / jne soft_assert
    private static readonly byte[] GrainSoftAssertSignature =
    [
        0x41, 0x83, 0xFF, 0x10, 0x0F, 0x85, 0x9A, 0x00, 0x00, 0x00,
        0x41, 0x89, 0xDC, 0x48, 0x63, 0xF3, 0x49, 0x39, 0x75, 0x20,
        0x0F, 0x85, 0xCC, 0x00, 0x00, 0x00,
    ];

    private static bool TryFindGrainSoftAssertSites(out ulong jneChannels, out ulong jneGrains)
    {
        jneChannels = 0;
        jneGrains = 0;

        // Astro maps the soft-assert body near 0x800F61000 at runtime even
        // though the eboot dump places the same bytes near 0x800F982E1.
        ReadOnlySpan<ulong> probes =
        [
            0x0000000800F61271UL,
            0x0000000800F61000UL,
            0x0000000800F60000UL,
            0x0000000800F982E1UL,
            0x0000000800F98000UL,
            0x0000000800F90000UL,
        ];

        Span<byte> window = stackalloc byte[0x4000];
        foreach (var probe in probes)
        {
            if (!TryReadGuestBytes(probe, window))
            {
                continue;
            }

            var sig = GrainSoftAssertSignature.AsSpan();
            for (var i = 0; i <= window.Length - sig.Length; i++)
            {
                if (!window.Slice(i, sig.Length).SequenceEqual(sig))
                {
                    continue;
                }

                jneChannels = probe + (ulong)i + 4;
                jneGrains = probe + (ulong)i + 20;
                return true;
            }
        }

        return false;
    }

    private static void TryPatchGrainSoftAssertBranches()
    {
        if (Interlocked.Exchange(ref _grainAssertPatched, 1) != 0)
        {
            return;
        }

        if (!TryFindGrainSoftAssertSites(out var jneChannels, out var jneGrains))
        {
            Interlocked.Exchange(ref _grainAssertPatched, 0);
            LogGrainPatchProbe(0x0000000800F982E1UL, "expected_cmp");
            LogGrainPatchProbe(0x0000000800F6139EUL, "sndz_ref");
            LogGrainPatchProbe(0x0000000800002844UL, "font_ref");
            return;
        }

        Span<byte> opcode = stackalloc byte[6];
        foreach (var address in (ReadOnlySpan<ulong>)[jneChannels, jneGrains])
        {
            if (!TryReadGuestBytes(address, opcode) ||
                opcode[0] != 0x0F ||
                opcode[1] is not (0x84 or 0x85))
            {
                Interlocked.Exchange(ref _grainAssertPatched, 0);
                Console.Error.WriteLine(
                    $"[LOADER][WARN] audio_prop.grain_assert_patch_mismatch @0x{address:X} bytes={opcode[0]:X2}{opcode[1]:X2}");
                return;
            }
        }

        if (!TryNopGuestBytes(jneChannels, 6) || !TryNopGuestBytes(jneGrains, 6))
        {
            Interlocked.Exchange(ref _grainAssertPatched, 0);
            Console.Error.WriteLine(
                $"[LOADER][WARN] audio_prop.grain_assert_patch_protect_fail @0x{jneChannels:X}/0x{jneGrains:X}");
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] AudioPropagation: skipped grain soft-assert branches at 0x{jneChannels:X}/0x{jneGrains:X}");
    }


    private static void TraceAudioProp(string message)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_PROP"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] audio_prop.{message}");
    }

    private static int SoftOk(CpuContext ctx) => ctx.SetReturn(0);

    private static int SoftOkTrace(CpuContext ctx, string name)
    {
        TraceAudioProp(name);
        return SoftOk(ctx);
    }

    // Guest treats room/source/portal handles as pointers (same shape as SystemCreate).
    private static int WriteOpaqueOut(CpuContext ctx, ulong outAddress, uint kindTag)
    {
        if (outAddress == 0)
        {
            outAddress = ctx[CpuRegister.Rsp] + 0x20;
        }

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, OpaqueBlockSize, 0x10, out var address) ||
            address == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var serial = unchecked((ulong)(uint)Interlocked.Increment(ref _nextOpaqueSerial));
        if (serial == 0)
        {
            serial = 1;
        }

        var scratch = address + (ulong)OpaqueScratchOffset;
        Span<byte> header = stackalloc byte[OpaqueBlockSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x41505250); // 'APRP'
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], kindTag);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], serial);

        // Nested handle fields: point into a scratch region inside the same
        // allocation so double-derefs do not collapse to NULL before stores.
        for (var off = 0x10; off < 0x200; off += sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header[off..], scratch);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], address);
        if (kindTag == 0x4D4F4F52 && _lastSystemAddress != 0) // 'ROOM'
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], _lastSystemAddress);
        }

        for (var off = OpaqueScratchOffset; off < OpaqueScratchOffset + 0x100; off += sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header[off..], scratch);
        }

        if (!ctx.Memory.TryWrite(address, header) ||
            !ctx.TryWriteUInt64(outAddress, address))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "7xyAxrusLko",
        ExportName = "sceAudioPropagationSystemQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memoryInfoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> memoryInfo = stackalloc byte[0x20];
        memoryInfo.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x00..], SystemMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x08..], SystemMemoryAlignment);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x10..], SystemMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x18..], SystemMemoryAlignment);

        return ctx.Memory.TryWrite(memoryInfoAddress, memoryInfo)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // Astro guest pointers live in the low 4G arenas (stack ~0x0324..., heap
    // ~0x0326...). Size/alignment immediates such as 0x10010 must not qualify.
    private static bool IsLikelyGuestPointer(ulong address) =>
        address >= 0x0100_0000UL && address < 0x0000_8000_0000_0000UL;

    private static bool IsPlausibleGrainCount(uint value) =>
        value is >= 64 and <= 8192;

    private static ulong ResolveOutPointer(CpuContext ctx, ulong candidate)
    {
        if (IsLikelyGuestPointer(candidate))
        {
            return candidate;
        }

        var r8 = ctx[CpuRegister.R8];
        if (IsLikelyGuestPointer(r8))
        {
            return r8;
        }

        // Win64: [rsp]=ret, [rsp+8..0x20]=shadow, [rsp+0x28]=5th arg.
        // Astro SystemCreate puts size in RCX (0x10010) and system* on the stack.
        return ctx[CpuRegister.Rsp] + 0x28;
    }

    private static void WriteSystemOutCandidates(CpuContext ctx, ulong systemAddress, ulong primaryOut)
    {
        // Only the resolved out slot. Speculative rsp+ write-through of stack
        // garbage smashed caller canaries (__stack_chk_fail after RoomCreate);
        // empty-slot rsp stamps were not enough to avoid the NULL store AV and
        // re-introduced the canary hit when heap write-through was re-enabled.
        if (IsLikelyGuestPointer(primaryOut))
        {
            _ = ctx.TryWriteUInt64(primaryOut, systemAddress);
        }
    }

    // Deep caller locals (out pointers). Keep the stack-protector cookie
    // ([rbp-0x28]/[rbp-0x30]) and saved-register tail untouched.
    private static bool IsSafeOutLocalSlot(ulong rbp, ulong address) =>
        IsLikelyGuestPointer(rbp) &&
        address <= rbp - 0x40 &&
        address >= rbp - 0x200;

    // Astro title stacks sit near 0x0324.... Heap system/room arenas ~0x0326....
    private static bool IsGuestStackArena(ulong address) =>
        address >= 0x0000_0003_2000_0000UL && address < 0x0000_0003_2500_0000UL;


    // AudioPropagationContext.cpp:327 compares [context+0x20] to numSamples.
    // Astro places Context 8 bytes before the SCE system arena, so the field is
    // at system+0x18. Leave +0x28 zero: planting the sample-count immediate made
    // _Atomic_fetch_sub_4 target VA 0x208.
    private static void SeedSoftSystemWorkspace(Span<byte> header, ulong systemAddress, uint grainCount)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], grainCount);

        // Context object lives at system-8; keep a few early pointer slots
        // non-null so post-Room helpers do not store through NULL.
        var scratch = systemAddress + 0x80;
        for (var off = 0x30; off < 0x80; off += sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header[off..], scratch);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(header[0x80..], scratch);
    }

    private static void SeedSoftContextPrefix(CpuContext ctx, ulong systemAddress, uint grainCount)
    {
        if (systemAddress < 8)
        {
            return;
        }

        var contextAddress = systemAddress - 8;
        Span<byte> context = stackalloc byte[0x40];
        context.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(context, systemAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x08..], contextAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x10..], systemAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x20..], grainCount);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x28..], systemAddress + 0x80);
        _ = ctx.Memory.TryWrite(contextAddress, context);
    }

    private static void StampContextGrainCount(CpuContext ctx, ulong systemAddress, uint grainCount)
    {
        if (systemAddress == 0 || grainCount == 0)
        {
            return;
        }

        // context+0x20 == system+0x18 when context == system-8.
        _ = ctx.TryWriteUInt64(systemAddress + 0x18, grainCount);
        if (systemAddress >= 8)
        {
            _ = ctx.TryWriteUInt64(systemAddress - 8 + 0x20, grainCount);
        }
    }


    [SysAbiExport(
        Nid = "aNEqtSHdUSo",
        ExportName = "sceAudioPropagationSystemCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        var memoryAddress = ctx[CpuRegister.Rdx];
        var systemOutAddress = ResolveOutPointer(ctx, ctx[CpuRegister.Rcx]);

        if (paramAddress == 0 || memoryAddress == 0 || !IsLikelyGuestPointer(memoryAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((ulong)(uint)Interlocked.Increment(ref _nextSystemHandle));
        if (handle == 0)
        {
            handle = 1;
        }

        // Zero the guest-provided system arena. Uninitialized bytes were read
        // as an absurd memset length right after Create.
        var zeroArena = new byte[(int)SystemMemorySize];
        _ = ctx.Memory.TryWrite(memoryAddress, zeroArena);

        uint grainCount = 256;
        if (ctx.TryReadUInt32(paramAddress + 0x20, out var paramGrain) &&
            IsPlausibleGrainCount(paramGrain))
        {
            grainCount = paramGrain;
        }

        Span<byte> header = stackalloc byte[SystemBlockSize];
        header.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], paramAddress);
        SeedSoftSystemWorkspace(header, memoryAddress, grainCount);

        if (!ctx.Memory.TryWrite(memoryAddress, header))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SeedSoftContextPrefix(ctx, memoryAddress, grainCount);
        WriteSystemOutCandidates(ctx, memoryAddress, systemOutAddress);
        _lastSystemAddress = memoryAddress;
        _lastGrainCount = grainCount;
        StampContextGrainCount(ctx, memoryAddress, grainCount);
        TryPatchGrainSoftAssertBranches();

        // Stamp empty caller-frame out locals only (deep zone; not canary tail).
        var rbp = ctx[CpuRegister.Rbp];
        if (IsLikelyGuestPointer(rbp))
        {
            ReadOnlySpan<int> rbpOffs =
            [
                0x170, 0x168, 0x160, 0x158, 0x150, 0x148, 0x140, 0x138,
                // AV path after RoomCreate used rdi≈rbp-0x50 with a NULL pointee.
                0x78, 0x70, 0x68, 0x60, 0x58, 0x50, 0x48, 0x40,
            ];
            foreach (var off in rbpOffs)
            {
                var slot = rbp - (ulong)off;
                if (!IsSafeOutLocalSlot(rbp, slot) || !ctx.TryReadUInt64(slot, out var existing))
                {
                    continue;
                }

                if (existing == 0 || existing == memoryAddress)
                {
                    _ = ctx.TryWriteUInt64(slot, memoryAddress);
                    continue;
                }

                // Slot holds &outLocal — fill empty pointee (not the canary tail).
                if (IsLikelyGuestPointer(existing) &&
                    !IsGuestStackArena(existing) &&
                    ctx.TryReadUInt64(existing, out var pointed) &&
                    (pointed == 0 || pointed == memoryAddress))
                {
                    _ = ctx.TryWriteUInt64(existing, memoryAddress);
                }
            }
        }

        TraceAudioProp(
            $"system_create handle=0x{handle:X} memory=0x{memoryAddress:X} out=0x{systemOutAddress:X} grains={grainCount} rbp=0x{rbp:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "x5VPqg5iyAk",
        ExportName = "sceAudioPropagationSystemDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemDestroy(CpuContext ctx) => SoftOkTrace(ctx, "system_destroy");

    private static ulong ResolveSystemArena(ulong candidate)
    {
        if (candidate == 0)
        {
            return _lastSystemAddress;
        }

        // Astro Room/Source paths pass AudioPropagationContext*, which sits
        // 8 bytes before the SCE system arena stamped by SystemCreate.
        if (_lastSystemAddress != 0)
        {
            if (candidate == _lastSystemAddress ||
                candidate + 8 == _lastSystemAddress ||
                candidate == _lastSystemAddress - 8)
            {
                return _lastSystemAddress;
            }
        }

        return candidate;
    }

    // RoomCreate(system, roomOut, settings): unresolved returned 0x80020002 and
    // tripped AudioPropagationRoom.cpp soft-assert after SystemCreate succeeded.
    [SysAbiExport(
        Nid = "8bI5h8req30",
        ExportName = "sceAudioPropagationRoomCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationRoomCreate(CpuContext ctx)
    {
        var system = ResolveSystemArena(ctx[CpuRegister.Rdi]);
        var roomOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Guest init after Create can clobber context+0x20 (seen as 0x201 vs
        // expected 512). Re-stamp before returning so the post-Room Sndz path
        // does not soft-assert on numSamples == m_numOfGrains.
        if (_lastGrainCount != 0)
        {
            StampContextGrainCount(ctx, system, _lastGrainCount);
        }

        TraceAudioProp($"room_create system=0x{system:X} out=0x{roomOut:X} grains={_lastGrainCount}");
        return WriteOpaqueOut(ctx, roomOut, 0x4D4F4F52); // 'ROOM'
    }

    [SysAbiExport(
        Nid = "S0JwP2AFTTE",
        ExportName = "sceAudioPropagationRoomDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationRoomDestroy(CpuContext ctx) => SoftOkTrace(ctx, "room_destroy");

    [SysAbiExport(
        Nid = "d84otraxt2s",
        ExportName = "sceAudioPropagationSourceCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceCreate(CpuContext ctx)
    {
        var system = ResolveSystemArena(ctx[CpuRegister.Rdi]);
        var sourceOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (_lastGrainCount != 0)
        {
            StampContextGrainCount(ctx, system, _lastGrainCount);
        }

        TraceAudioProp($"source_create system=0x{system:X} out=0x{sourceOut:X} grains={_lastGrainCount}");
        return WriteOpaqueOut(ctx, sourceOut, 0x43525353); // 'SRCS'
    }

    [SysAbiExport(
        Nid = "wkseM3LWPuc",
        ExportName = "sceAudioPropagationSourceDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceDestroy(CpuContext ctx) => SoftOkTrace(ctx, "source_destroy");

    [SysAbiExport(
        Nid = "b-dYXrjSNZU",
        ExportName = "sceAudioPropagationPortalCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalCreate(CpuContext ctx)
    {
        var system = ResolveSystemArena(ctx[CpuRegister.Rdi]);
        var portalOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAudioProp($"portal_create system=0x{system:X} out=0x{portalOut:X}");
        return WriteOpaqueOut(ctx, portalOut, 0x504F5254); // 'PORT'
    }

    [SysAbiExport(
        Nid = "ZQXE-xS6MTE",
        ExportName = "sceAudioPropagationPortalDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalDestroy(CpuContext ctx) => SoftOkTrace(ctx, "portal_destroy");

    [SysAbiExport(
        Nid = "kIdb+iQUzCs",
        ExportName = "sceAudioPropagationSystemSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemSetAttributes(CpuContext ctx) => SoftOkTrace(ctx, "system_set_attributes");

    [SysAbiExport(
        Nid = "BbOT4vBwAjs",
        ExportName = "sceAudioPropagationResetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationResetAttributes(CpuContext ctx) => SoftOkTrace(ctx, "reset_attributes");

    [SysAbiExport(
        Nid = "WXMhENV2NcA",
        ExportName = "sceAudioPropagationPortalSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalSetAttributes(CpuContext ctx) => SoftOkTrace(ctx, "portal_set_attributes");

    [SysAbiExport(
        Nid = "-wsUTr31yeg",
        ExportName = "sceAudioPropagationSourceSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceSetAttributes(CpuContext ctx) => SoftOkTrace(ctx, "source_set_attributes");

    [SysAbiExport(
        Nid = "B2KI2AachWE",
        ExportName = "sceAudioPropagationSystemLock",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemLock(CpuContext ctx) => SoftOkTrace(ctx, "system_lock");

    [SysAbiExport(
        Nid = "CPLV6G-eXmk",
        ExportName = "sceAudioPropagationSystemRegisterMaterial",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemRegisterMaterial(CpuContext ctx) => SoftOkTrace(ctx, "register_material");

    [SysAbiExport(
        Nid = "XKCN4gpeYsM",
        ExportName = "sceAudioPropagationSystemUnregisterMaterial",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemUnregisterMaterial(CpuContext ctx) => SoftOkTrace(ctx, "unregister_material");

    [SysAbiExport(
        Nid = "PBcrVpEqUVY",
        ExportName = "sceAudioPropagationSourceCalculateAudioPaths",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceCalculateAudioPaths(CpuContext ctx) => SoftOkTrace(ctx, "source_calculate_paths");

    [SysAbiExport(
        Nid = "hhz9pITnC8k",
        ExportName = "sceAudioPropagationSourceRender",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceRender(CpuContext ctx)
    {
        var system = _lastSystemAddress;
        if (system != 0 && _lastGrainCount != 0)
        {
            StampContextGrainCount(ctx, system, _lastGrainCount);
        }

        TraceAudioProp($"source_render system=0x{system:X} grains={_lastGrainCount}");
        return SoftOk(ctx);
    }

    [SysAbiExport(
        Nid = "tKSmk2JsMAA",
        ExportName = "sceAudioPropagationSourceSetAudioPath",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceSetAudioPath(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "5vzOS2pHMFc",
        ExportName = "sceAudioPropagationSourceSetAudioPaths",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceSetAudioPaths(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "GrA9ke1QT+E",
        ExportName = "sceAudioPropagationSystemQueryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemQueryInfo(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "3aEY9tPXGKc",
        ExportName = "sceAudioPropagationSourceQueryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceQueryInfo(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "ht-QXT3zGxo",
        ExportName = "sceAudioPropagationSystemGetRays",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemGetRays(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "VlBT16890mA",
        ExportName = "sceAudioPropagationSystemSetRays",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemSetRays(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "aKJZx7wCma8",
        ExportName = "sceAudioPropagationSourceGetRays",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceGetRays(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "eEeKqFeNI3o",
        ExportName = "sceAudioPropagationSourceGetAudioPath",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceGetAudioPath(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "G+QLTfyLMYk",
        ExportName = "sceAudioPropagationSourceGetAudioPathCount",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceGetAudioPathCount(CpuContext ctx) =>
        ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "tL2AEPejVQE",
        ExportName = "sceAudioPropagationPathGetNumPoints",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPathGetNumPoints(CpuContext ctx) =>
        ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "gCmQm6dvMxw",
        ExportName = "sceAudioPropagationReportApi",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationReportApi(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "i-0aUex3zCE",
        ExportName = "sceAudioPropagationAudioPathInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationAudioPathInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "JZIkSbmt2BE",
        ExportName = "sceAudioPropagationAudioPathPointInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationAudioPathPointInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "2BSFmuKtRss",
        ExportName = "sceAudioPropagationMaterialInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationMaterialInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "i687TNRF+hw",
        ExportName = "sceAudioPropagationPortalSettingsInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalSettingsInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "0r2+9UTg1BA",
        ExportName = "sceAudioPropagationRayInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationRayInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "SoKPzY1-3SU",
        ExportName = "sceAudioPropagationSourceRenderInfoInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceRenderInfoInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "MNmGapXrYRs",
        ExportName = "sceAudioPropagationSourceSetAudioPathsParamInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceSetAudioPathsParamInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "cMl3u+7QBBM",
        ExportName = "sceAudioPropagationSystemMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemMemoryInit(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "3B9IabLByyM",
        ExportName = "sceAudioPropagationSystemOptionInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemOptionInit(CpuContext ctx) => SoftOk(ctx);
}
