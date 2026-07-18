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
    // Astro post-Room path touches fields past +0x100 (stack held room+0x130)
    // and Sndz clears ~0x8040 through a nested buffer pointer. Undersized soft
    // rooms left those slots NULL (write AV @0x80000048F) or pointed at a
    // tiny scratch that collapsed to absolute VA 0x800 on the AudioOut thread.
    private const int OpaqueBlockSize = 0x10000;
    private const int OpaqueScratchOffset = 0x2000;
    // Astro guest objects live in the low 4G arenas (~0x0324…/0x0326…). HLE
    // TryAllocateHleData starts at 0x1_0000_0000 on Windows — those high
    // addresses are ignored when Sndz copies buffer slots into its own low
    // objects, leaving memset(dst=NULL, len=0x8040). Carve work + room out of
    // the guest-provided system arena at high offsets: guest already places
    // helpers around system+0x19D88 (inside a +0x10000 work carve).
    private const ulong SoftWorkBufferOffset = 0x80000;
    private const int SoftWorkBufferBytes = 0xA000;
    private const ulong SoftRoomOffset = 0x90000;
    private const int SoftRoomBytes = OpaqueBlockSize;
    private static int _nextSystemHandle;
    private static int _nextOpaqueSerial;
    private static ulong _lastSystemAddress;
    private static ulong _lastWorkAddress;
    private static ulong _lastRoomAddress;
    private static uint _lastGrainCount;
    private static int _grainAssertPatched;
    private static int _sndzCookieAssertPatched;
    // Sndz clears ~0x8040 from the work base after RoomCreate.
    private const int SoftWorkClearableBytes = 0x8040;

    internal static ulong LastSoftWorkAddress => _lastWorkAddress;

    // Sndz post-Room: cmp rax,[rbp-0x30] / jne soft_assert @ ~0x800F7CCF9.
    // Soft work-buffer never installs the real cookie pair; NOP the jne so the
    // success epilogue runs without VEH resume (which still tore down AudioOut).
    // Full prologue varies across builds — match the cmp/jne tail only.
    private static readonly byte[] SndzCookieAssertTail =
    [
        0x48, 0x3B, 0x45, 0xD0, // cmp rax, [rbp-0x30]
        0x75,                   // jne rel8
    ];

    private static readonly byte[] SndzCookieAssertSignature =
    [
        0x48, 0x8B, 0x32, 0x48, 0x83, 0xC2, 0x08, 0x48, 0x89, 0x30, 0x48, 0x83, 0xC0, 0x08,
        0x48, 0x39, 0xCA, 0x75, 0xED, 0x49, 0x89, 0x46, 0x10, 0x49, 0x8B, 0x07, 0x48, 0x3B, 0x45, 0xD0,
        0x75, 0x12,
    ];

    // Sndz post-Room clear:
    //   mov rdx,[r13+0x20]  ; grain count
    //   mov rdi,[r13+0x18]  ; work buffer  (context+0x18 == system+0x10)
    //   shl rdx,6           ; len = grains*64  (512→0x8000; 0x201→0x8040)
    //   call memset
    // Stamp work at system+0x10 / context+0x18 only — not 0x28–0xF8 (+0xC8
    // is a table index; flooding it AV'd at 0x800F7CCF9).
    private static ReadOnlySpan<int> SoftWorkPointerOffsets =>
    [
        0x10,
    ];

    // Extended buffer slots observed NULL on Sndz objects (r14/rbx), past the
    // object header / index table region.
    private static ReadOnlySpan<int> SoftWorkExtendedPointerOffsets =>
    [
        0x110, 0x118, 0x120, 0x128, 0x130, 0x138, 0x140,
        0x200, 0x208, 0x210, 0x218,
    ];

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
        Span<byte> sample = stackalloc byte[32];
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

    private static int _priHooverSoftAssertPatched;

    // PriHooverSunshade.cpp:0x10A post-Room soft Result:
    //   xor eax,eax / call 0x800001AA0 / test eax,eax / jz cont / int 0x41
    // Helper returns nonzero → int41 AV @0x8073CF5E3. Skipping int41 still
    // quiet-exits; NOP the call so eax stays 0 and the jz success path runs.
    internal static void TryPatchPriHooverSunshadeSoftAssert()
    {
        if (Interlocked.CompareExchange(ref _priHooverSoftAssertPatched, 1, 0) != 0)
        {
            return;
        }

        const ulong CallRip = 0x00000008073CF5D6UL;
        Span<byte> window = stackalloc byte[16];
        if (!TryReadGuestBytes(CallRip - 2, window))
        {
            Interlocked.Exchange(ref _priHooverSoftAssertPatched, 0);
            return;
        }

        // Expect: xor eax,eax; call rel32; test eax,eax; jz near; int 0x41
        if (window[0] != 0x31 || window[1] != 0xC0 || window[2] != 0xE8 ||
            window[7] != 0x85 || window[8] != 0xC0 ||
            window[9] != 0x0F || window[10] != 0x84 ||
            window[15] != 0xCD)
        {
            Interlocked.Exchange(ref _priHooverSoftAssertPatched, 0);
            Console.Error.WriteLine(
                $"[LOADER][WARN] audio_prop.pri_hoover_soft_assert_mismatch @0x{CallRip:X} " +
                $"bytes={Convert.ToHexString(window)}");
            return;
        }

        if (!TryNopGuestBytes(CallRip, 5))
        {
            Interlocked.Exchange(ref _priHooverSoftAssertPatched, 0);
            Console.Error.WriteLine("[LOADER][WARN] audio_prop.pri_hoover_soft_assert_patch_failed");
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] AudioPropagation: NOP'd PriHooverSunshade soft Result call at 0x{CallRip:X} " +
            "(PriHooverSunshade.cpp:0x10A)");
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

    internal static void TryPatchSndzCookieSoftAssert()
    {
        if (Interlocked.CompareExchange(ref _sndzCookieAssertPatched, 1, 0) != 0)
        {
            return;
        }

        // Soft-assert AV lands at 0x800F7CCF9. Only accept a cmp/jne in that
        // immediate window — a short tail match elsewhere (e.g. 0x800F7CE6A)
        // NOP'd a live branch and reintroduced VEH_AV target=0x800.
        const ulong WindowLo = 0x0000000800F7CCE0UL;
        const ulong WindowHi = 0x0000000800F7CD10UL;
        ReadOnlySpan<ulong> probes =
        [
            0x0000000800F7CCE0UL,
            0x0000000800F7CCC0UL,
        ];

        Span<byte> window = stackalloc byte[0x80];
        foreach (var probe in probes)
        {
            if (!TryReadGuestBytes(probe, window))
            {
                continue;
            }

            foreach (var sig in (ReadOnlySpan<byte[]>)[SndzCookieAssertSignature, SndzCookieAssertTail])
            {
                for (var i = 0; i <= window.Length - sig.Length; i++)
                {
                    if (!window.Slice(i, sig.Length).SequenceEqual(sig))
                    {
                        continue;
                    }

                    var jneAddress = sig == SndzCookieAssertTail
                        ? probe + (ulong)i + (ulong)sig.Length - 1
                        : probe + (ulong)i + (ulong)sig.Length - 2;

                    if (jneAddress < WindowLo || jneAddress >= WindowHi)
                    {
                        continue;
                    }

                    Span<byte> jneOp = stackalloc byte[2];
                    if (!TryReadGuestBytes(jneAddress, jneOp) || jneOp[0] != 0x75)
                    {
                        continue;
                    }

                    if (!TryNopGuestBytes(jneAddress, 2))
                    {
                        Interlocked.Exchange(ref _sndzCookieAssertPatched, 0);
                        Console.Error.WriteLine("[LOADER][WARN] audio_prop.sndz_cookie_assert_patch_write_failed");
                        return;
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][INFO] AudioPropagation: skipped Sndz cookie soft-assert jne at 0x{jneAddress:X}");
                    return;
                }
            }
        }

        Interlocked.Exchange(ref _sndzCookieAssertPatched, 0);
        Console.Error.WriteLine("[LOADER][WARN] audio_prop.sndz_cookie_assert_not_found");
        LogGrainPatchProbe(0x0000000800F7CCE0UL, "sndz_cookie_expected");
        LogGrainPatchProbe(0x0000000800F7CCF0UL, "sndz_cookie_av");
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

        ulong address;
        if (kindTag == 0x4D4F4F52 && // 'ROOM'
            _lastSystemAddress != 0 &&
            SoftRoomOffset + (ulong)SoftRoomBytes <= SystemMemorySize)
        {
            // Keep Room in the low guest system arena (not HLE >4GB).
            address = _lastSystemAddress + SoftRoomOffset;
        }
        else if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, OpaqueBlockSize, 0x10, out address) ||
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
		var work = _lastWorkAddress != 0
			? _lastWorkAddress
			: (_lastSystemAddress != 0 ? _lastSystemAddress + SoftWorkBufferOffset : scratch);
		var header = GC.AllocateUninitializedArray<byte>(OpaqueBlockSize);
		header.AsSpan().Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(header, 0x41505250); // 'APRP'
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), kindTag);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(OpaqueScratchOffset + 4), unchecked((uint)serial));
		// +0x08 is a nested state pointer (not a serial). Post-Room guest code
		// does mov rax,[obj+8] / cmp byte [rax+0x204],0 on AudioOut.
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), work);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x10), address);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x18), address);

		// Leave 0x28–0x100 zeroed: +0xC8 is a DWORD table index into +0x90.
		// Flooding that range with scratch/work pointers made rcx huge and AV'd
		// at mov rbx,[rax+rcx+0x90] (0x800F7CCF9). Index 0 → load [obj+0x90].
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x90), work);

		// Scratch payload: self-pointer header only (clearable body stays zero).
		for (var off = OpaqueScratchOffset; off < OpaqueScratchOffset + 0x100; off += sizeof(ulong))
		{
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(off), scratch);
		}

		if (kindTag == 0x4D4F4F52 && _lastSystemAddress != 0) // 'ROOM'
		{
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x20), _lastSystemAddress);
			foreach (var off in SoftWorkPointerOffsets)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(off), work);
			}

			foreach (var off in SoftWorkExtendedPointerOffsets)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(off), work);
			}

			_lastRoomAddress = address;
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

    // Broad guest VA (includes HLE >4GB). Size/alignment immediates such as
    // 0x10010 must not qualify.
    private static bool IsLikelyGuestPointer(ulong address) =>
        address >= 0x0100_0000UL && address < 0x0000_8000_0000_0000UL;

    // Astro title objects for SoftWork stamping live in the low 4G arenas
    // (stack ~0x0324…, heap ~0x0326…). Host Windows stacks/heaps (~0x7FFF… /
    // CLR ≥4GB) must not be pointer-flooded — that corrupts the emulator and
    // leaves Sndz's real low object (r14) unfilled.
    private static bool IsAstroLowGuestPointer(ulong address) =>
        address >= 0x0100_0000UL && address < 0x0001_0000_0000UL;

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
    // Astro places Context 8 bytes before the SCE system arena, so grains are
    // at system+0x18. Work buffer for memset is system+0x10 (== context+0x18).
    private static void SeedSoftSystemWorkspace(Span<byte> header, ulong work, uint grainCount)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], grainCount);

        if (work == 0 || header.Length < 0x18)
        {
            return;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], work);
    }

    private static void SeedSoftContextPrefix(CpuContext ctx, ulong systemAddress, ulong work, uint grainCount)
    {
        if (systemAddress < 8)
        {
            return;
        }

        var contextAddress = systemAddress - 8;
        Span<byte> context = stackalloc byte[0x100];
        context.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(context, systemAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x08..], contextAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x10..], systemAddress);
        // Sndz: mov rdi,[r13+0x18] / mov rdx,[r13+0x20] / shl rdx,6 / memset
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x18..], work != 0 ? work : systemAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(context[0x20..], grainCount);

        _ = ctx.Memory.TryWrite(contextAddress, context);
    }

    private static void InstallSoftWorkBuffer(CpuContext ctx, ulong systemAddress)
    {
        if (!IsLikelyGuestPointer(systemAddress) ||
            SoftWorkBufferOffset + (ulong)SoftWorkBufferBytes > SystemMemorySize)
        {
            return;
        }

        // Low guest address inside the title-provided system arena.
        var work = systemAddress + SoftWorkBufferOffset;
        _lastWorkAddress = work;

        // Layout:
        //   [0, SoftWorkClearableBytes) — zeroed payload for Sndz memset ~0x8040
        //   leading 0x100               — self-pointers (restored after clear)
        // Do not pointer-flood the rest of the system arena (libc heap checker).
        var payload = GC.AllocateUninitializedArray<byte>(SoftWorkBufferBytes);
        payload.AsSpan().Clear();
        for (var off = 0; off < 0x100; off += sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(off), work);
        }

        _ = ctx.Memory.TryWrite(work, payload);

        StampSoftWorkPointers(ctx, systemAddress, work);
        StampSoftBufferSlots(ctx, systemAddress, work);
        if (systemAddress >= 8)
        {
            StampSoftWorkPointers(ctx, systemAddress - 8, work);
            StampSoftBufferSlots(ctx, systemAddress - 8, work);
        }

        // Grains at system+0x18 / context+0x20 — never overwrite with work.
        StampContextGrainCount(ctx, systemAddress, _lastGrainCount != 0 ? _lastGrainCount : 256);
    }

    // Re-stamp work head after Sndz clears the low clearable region (~0x8040).
    // The leading 0x100 self-pointers must survive; a bare memset of the work
    // base otherwise leaves nested loads as NULL and AVs at 0x8000006A1.
    internal static void RestoreSoftWorkBufferHeader(CpuContext ctx)
    {
        if (_lastWorkAddress == 0)
        {
            return;
        }

        SeedWorkBufferHighTail(ctx, _lastWorkAddress);
    }

    // Host-side clear of the soft work payload without handing the pointer back
    // through memset RAX/RDI (guest already loaded NULL into rdi).
    internal static void TryClearSoftWorkBuffer(CpuContext ctx, ulong length)
    {
        var work = _lastWorkAddress;
        if (work == 0 || length < 0x100UL || length > 0x20000UL)
        {
            return;
        }

        var clearLen = (int)Math.Min(length, (ulong)SoftWorkClearableBytes);
        var zeros = GC.AllocateUninitializedArray<byte>(clearLen);
        zeros.AsSpan().Clear();
        if (ctx.Memory.TryWrite(work, zeros.AsSpan(0, clearLen)))
        {
            SeedWorkBufferHighTail(ctx, work);
            Console.Error.WriteLine(
                $"[LOADER][WARNING] memset null-dst cleared work=0x{work:X} len=0x{clearLen:X} (rax kept 0)");
        }
    }

    private static void SeedWorkBufferHighTail(CpuContext ctx, ulong work)
    {
        if (!IsLikelyGuestPointer(work))
        {
            return;
        }

        for (var off = 0; off < 0x100; off += sizeof(ulong))
        {
            _ = ctx.TryWriteUInt64(work + (ulong)off, work);
        }
    }

    private static void StampSoftWorkPointers(CpuContext ctx, ulong baseAddress, ulong work)
    {
        if (!IsLikelyGuestPointer(baseAddress) || work == 0)
        {
            return;
        }

        // Context* sits 8 bytes before system: buffer at context+0x18.
        if (_lastSystemAddress != 0 && baseAddress + 8 == _lastSystemAddress)
        {
            _ = ctx.TryWriteUInt64(baseAddress + 0x18, work);
            return;
        }

        // System arena: buffer at system+0x10 (same slot as context+0x18).
        if (_lastSystemAddress != 0 && baseAddress == _lastSystemAddress)
        {
            _ = ctx.TryWriteUInt64(baseAddress + 0x10, work);
            return;
        }

        foreach (var off in SoftWorkPointerOffsets)
        {
            _ = ctx.TryWriteUInt64(baseAddress + (ulong)off, work);
        }

        // Room / opaque: keep +0x08 nested state pointer.
        _ = ctx.TryWriteUInt64(baseAddress + 0x08, work);
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

        _lastSystemAddress = memoryAddress;
        _lastGrainCount = grainCount;
        InstallSoftWorkBuffer(ctx, memoryAddress);
        var work = _lastWorkAddress;
        SeedSoftSystemWorkspace(header, work, grainCount);

        if (!ctx.Memory.TryWrite(memoryAddress, header))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SeedSoftContextPrefix(ctx, memoryAddress, work, grainCount);
        WriteSystemOutCandidates(ctx, memoryAddress, systemOutAddress);
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
        // Guests also pass the soft work carve (system+0x80000) or room
        // (system+0x90000) as the "system" argument after SoftWork stamps —
        // map any address inside the system arena back to the arena base.
        if (_lastSystemAddress != 0)
        {
            if (candidate == _lastSystemAddress ||
                candidate + 8 == _lastSystemAddress ||
                candidate == _lastSystemAddress - 8)
            {
                return _lastSystemAddress;
            }

            if (candidate >= _lastSystemAddress &&
                candidate < _lastSystemAddress + SystemMemorySize)
            {
                return _lastSystemAddress;
            }

            if (_lastWorkAddress != 0 &&
                (candidate == _lastWorkAddress ||
                 (_lastRoomAddress != 0 && candidate == _lastRoomAddress)))
            {
                return _lastSystemAddress;
            }
        }

        return candidate;
    }

    // Replace NULL-page pointer slots (observed absolute VA 0x800 after Room)
    // and re-stamp known buffer slots with the soft work buffer so Sndz
    // memset/memcpy do not touch the NULL page.
    internal static void TryHealSoftAudioPointers(CpuContext ctx)
    {
        if (_lastSystemAddress == 0)
        {
            return;
        }

        TryHealSoftAudioPointers(ctx, _lastSystemAddress);

        // Sndz keeps its live object in r14 (and siblings) across the post-Room
        // clear. Only stamp low Astro arenas — never host rbp/CLR heaps.
        foreach (var candidate in (ReadOnlySpan<ulong>)
                 [
                     ctx[CpuRegister.R12],
                     ctx[CpuRegister.R13],
                     ctx[CpuRegister.R14],
                     ctx[CpuRegister.R15],
                     ctx[CpuRegister.Rbx],
                     ctx[CpuRegister.Rdi],
                     ctx[CpuRegister.Rsi],
                 ])
        {
            if (!IsAstroLowGuestPointer(candidate))
            {
                continue;
            }

            var system = ResolveSystemArena(candidate);
            if (system != 0 && system != _lastSystemAddress)
            {
                TryHealSoftAudioPointers(ctx, system);
            }
            else if (candidate != _lastSystemAddress)
            {
                var work = _lastWorkAddress;
                if (work == 0)
                {
                    continue;
                }

                // Force SoftWork + extended slots: Sndz zeros the object after
                // RoomCreate, then loads a still-NULL buffer into memset rdi.
                HealLowPagePointerSlots(ctx, candidate, 0x400, work);
                StampSoftWorkPointers(ctx, candidate, work);
                StampSoftBufferSlots(ctx, candidate, work);
            }
        }
    }

    // Log which guest object/slot is still NULL when Sndz calls memset(NULL,0,0x8040).
    internal static void TraceNullMemsetContext(CpuContext ctx, ulong length)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_PROP"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Span<(string Name, ulong Value)> regs =
        [
            ("rax", ctx[CpuRegister.Rax]),
            ("rbx", ctx[CpuRegister.Rbx]),
            ("rcx", ctx[CpuRegister.Rcx]),
            ("rdx", ctx[CpuRegister.Rdx]),
            ("rsi", ctx[CpuRegister.Rsi]),
            ("rdi", ctx[CpuRegister.Rdi]),
            ("rbp", ctx[CpuRegister.Rbp]),
            ("rsp", ctx[CpuRegister.Rsp]),
            ("r8", ctx[CpuRegister.R8]),
            ("r9", ctx[CpuRegister.R9]),
            ("r12", ctx[CpuRegister.R12]),
            ("r13", ctx[CpuRegister.R13]),
            ("r14", ctx[CpuRegister.R14]),
            ("r15", ctx[CpuRegister.R15]),
        ];

        ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var ret);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] audio_prop.null_memset_ctx len=0x{length:X} ret=0x{ret:X} " +
            $"work=0x{_lastWorkAddress:X} room=0x{_lastRoomAddress:X} system=0x{_lastSystemAddress:X}");

        foreach (var (name, value) in regs)
        {
            Console.Error.Write($"{name}=0x{value:X} ");
        }

        Console.Error.WriteLine();

        ReadOnlySpan<int> probeOffs =
        [
            0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48,
            0x100, 0x108, 0x110, 0x118, 0x120, 0x128, 0x130, 0x138, 0x140,
            0x200, 0x208, 0x210,
        ];

        foreach (var (name, baseAddress) in regs)
        {
            if (!IsAstroLowGuestPointer(baseAddress))
            {
                continue;
            }

            foreach (var off in probeOffs)
            {
                if (!ctx.TryReadUInt64(baseAddress + (ulong)off, out var slot))
                {
                    continue;
                }

                if (slot == 0 || slot < 0x1000UL)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] audio_prop.null_slot {name}+0x{off:X}=0x{slot:X} (base=0x{baseAddress:X})");
                }
            }
        }

        if (_lastRoomAddress != 0)
        {
            foreach (var off in probeOffs)
            {
                if (ctx.TryReadUInt64(_lastRoomAddress + (ulong)off, out var slot) &&
                    (slot == 0 || slot < 0x1000UL))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] audio_prop.null_slot room+0x{off:X}=0x{slot:X}");
                }
            }
        }

        if (_lastSystemAddress != 0)
        {
            foreach (var off in probeOffs)
            {
                if (ctx.TryReadUInt64(_lastSystemAddress + (ulong)off, out var slot) &&
                    (slot == 0 || slot < 0x1000UL))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] audio_prop.null_slot system+0x{off:X}=0x{slot:X}");
                }
            }

            if (ctx.TryReadUInt64(_lastSystemAddress + SoftWorkBufferOffset, out var stub0))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] audio_prop.work_base system+0x{SoftWorkBufferOffset:X}=0x{stub0:X} expect_work=0x{_lastWorkAddress:X}");
            }
        }
    }

    // After heal, return the soft work buffer so a null memset(len≈0x8040) can
    // clear real memory. Also re-stamp room/system/Sndz-object buffer slots.
    internal static ulong TryRedirectNullMemset(CpuContext ctx, ulong length)
    {
        if (length < 0x1000UL || _lastWorkAddress == 0)
        {
            return 0;
        }

        var work = _lastWorkAddress;
        if (_lastSystemAddress != 0)
        {
            StampSoftBufferSlots(ctx, _lastSystemAddress, work);
            if (_lastSystemAddress >= 8)
            {
                StampSoftBufferSlots(ctx, _lastSystemAddress - 8, work);
            }
        }

        if (_lastRoomAddress != 0)
        {
            StampSoftBufferSlots(ctx, _lastRoomAddress, work);
            _ = ctx.TryWriteUInt64(_lastRoomAddress + 0x08, work);
            _ = ctx.TryWriteUInt64(_lastRoomAddress + 0x90, work);
            // Keep +0xC8 as a small index (0), never a pointer.
            _ = ctx.TryWriteUInt32(_lastRoomAddress + 0xC8, 0);
            StampSoftWorkPointers(ctx, _lastRoomAddress, work);
            foreach (var off in SoftWorkExtendedPointerOffsets)
            {
                _ = ctx.TryWriteUInt64(_lastRoomAddress + (ulong)off, work);
            }
        }

        foreach (var candidate in (ReadOnlySpan<ulong>)
                 [
                     ctx[CpuRegister.R12],
                     ctx[CpuRegister.R13],
                     ctx[CpuRegister.R14],
                     ctx[CpuRegister.R15],
                     ctx[CpuRegister.Rbx],
                 ])
        {
            if (!IsAstroLowGuestPointer(candidate) ||
                candidate == _lastSystemAddress ||
                candidate == _lastRoomAddress ||
                candidate == work)
            {
                continue;
            }

            // Sndz object (typically r14): only safe pointer slots + heal low pages.
            HealLowPagePointerSlots(ctx, candidate, 0x220, work);
            StampSoftWorkPointers(ctx, candidate, work);
            StampSoftBufferSlots(ctx, candidate, work);
            if (ctx.TryReadUInt64(candidate + 0xC8, out var indexSlot) &&
                indexSlot >= 0x1000UL)
            {
                // Pointer was wrongly stored as the table index — reset to 0.
                _ = ctx.TryWriteUInt32(candidate + 0xC8, 0);
                _ = ctx.TryWriteUInt64(candidate + 0x90, work);
            }
        }

        SeedWorkBufferHighTail(ctx, work);
        return work;
    }

    // Buffer pointer slots beyond the SoftWorkPointerOffsets header.
    // Only stamp these on soft Room/work objects — not on the system arena
    // (guest keeps live helpers at mid-arena offsets like +0x19D88).
    private static void StampSoftBufferSlots(CpuContext ctx, ulong baseAddress, ulong work)
    {
        if (!IsLikelyGuestPointer(baseAddress) || work == 0)
        {
            return;
        }

        // Refuse to stamp the system arena body — SoftWorkPointerOffsets on the
        // header/context are enough; mid-arena fills collide with guest objects.
        if (_lastSystemAddress != 0 &&
            baseAddress >= _lastSystemAddress &&
            baseAddress < _lastSystemAddress + SystemMemorySize &&
            baseAddress != _lastRoomAddress &&
            baseAddress != _lastWorkAddress &&
            baseAddress != _lastSystemAddress &&
            baseAddress != _lastSystemAddress - 8)
        {
            return;
        }

        if (baseAddress == _lastSystemAddress ||
            (_lastSystemAddress != 0 && baseAddress == _lastSystemAddress - 8))
        {
            StampSoftWorkPointers(ctx, baseAddress, work);
            return;
        }

        StampSoftWorkPointers(ctx, baseAddress, work);
        foreach (var off in SoftWorkExtendedPointerOffsets)
        {
            _ = ctx.TryWriteUInt64(baseAddress + (ulong)off, work);
        }
    }

    private static void TryHealSoftAudioPointers(CpuContext ctx, ulong system)
    {
        if (system == 0 || !IsLikelyGuestPointer(system))
        {
            return;
        }

        var work = _lastWorkAddress;
        if (work == 0)
        {
            InstallSoftWorkBuffer(ctx, system);
            work = _lastWorkAddress;
        }

        if (work == 0)
        {
            return;
        }

        if (system >= 8)
        {
            HealLowPagePointerSlots(ctx, system - 8, 0x200, work);
            StampSoftWorkPointers(ctx, system - 8, work);
            if (_lastGrainCount != 0)
            {
                _ = ctx.TryWriteUInt64(system - 8 + 0x20, _lastGrainCount);
            }
        }

        HealLowPagePointerSlots(ctx, system, 0x400, work);
        StampSoftWorkPointers(ctx, system, work);
        StampSoftBufferSlots(ctx, system, work);
        SeedWorkBufferHighTail(ctx, work);

        if (_lastRoomAddress != 0)
        {
            HealLowPagePointerSlots(ctx, _lastRoomAddress, OpaqueScratchOffset, work);
            StampSoftWorkPointers(ctx, _lastRoomAddress, work);
            StampSoftBufferSlots(ctx, _lastRoomAddress, work);
            _ = ctx.TryWriteUInt64(_lastRoomAddress + 0x08, work);
        }

        if (_lastGrainCount != 0)
        {
            _ = ctx.TryWriteUInt64(system + 0x18, _lastGrainCount);
            if (system >= 8)
            {
                _ = ctx.TryWriteUInt64(system - 8 + 0x20, _lastGrainCount);
            }
        }
    }

    private static void HealLowPagePointerSlots(
        CpuContext ctx,
        ulong baseAddress,
        int length,
        ulong replacement)
    {
        if (!IsLikelyGuestPointer(baseAddress) || length < sizeof(ulong) || replacement == 0)
        {
            return;
        }

        for (var off = 0; off + sizeof(ulong) <= length; off += sizeof(ulong))
        {
            var slot = baseAddress + (ulong)off;
            if (!ctx.TryReadUInt64(slot, out var value))
            {
                continue;
            }

            // Absolute VA 0x800 and other NULL-page leftovers from soft layout.
            // Also refill NULL at known nested-pointer offsets (AudioOut [obj+8]).
            var knownPointerSlot = off == 0x08 || IsSoftWorkPointerOffset(off);
            if ((value != 0 && value < 0x1000UL) || (knownPointerSlot && value == 0))
            {
                _ = ctx.TryWriteUInt64(slot, replacement);
            }
        }
    }

    private static bool IsSoftWorkPointerOffset(int offset)
    {
        foreach (var off in SoftWorkPointerOffsets)
        {
            if (off == offset)
            {
                return true;
            }
        }

        return false;
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
        var status = WriteOpaqueOut(ctx, roomOut, 0x4D4F4F52); // 'ROOM'
        if (status == 0)
        {
            InstallSoftWorkBuffer(ctx, system);
            TryHealSoftAudioPointers(ctx, system);
            TryPatchSndzCookieSoftAssert();
            TryPatchPriHooverSunshadeSoftAssert();
            if (IsLikelyGuestPointer(roomOut) &&
                ctx.TryReadUInt64(roomOut, out var room) &&
                IsLikelyGuestPointer(room))
            {
                var work = _lastWorkAddress;
                if (work == 0)
                {
                    InstallSoftWorkBuffer(ctx, system);
                    work = _lastWorkAddress;
                }

                if (work == 0)
                {
                    return status;
                }

                HealLowPagePointerSlots(ctx, room, 0x220, work);
                StampSoftWorkPointers(ctx, room, work);
                StampSoftBufferSlots(ctx, room, work);
                _ = ctx.TryWriteUInt64(room + 0x08, work);
                _ = ctx.TryWriteUInt64(room + 0x90, work);
                _ = ctx.TryWriteUInt32(room + 0xC8, 0);
            }
        }

        return status;
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
