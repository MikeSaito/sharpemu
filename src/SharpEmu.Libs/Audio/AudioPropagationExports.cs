// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
    private const int OpaqueBlockSize = 0x100;
    private static int _nextSystemHandle;
    private static int _nextOpaqueSerial;

    private static int SoftOk(CpuContext ctx) => ctx.SetReturn(0);

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

        Span<byte> header = stackalloc byte[OpaqueBlockSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x41505250); // 'APRP'
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], kindTag);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], serial);
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

    // Astro guest pointers live in the low 4G arenas (stack ~0x0324…, heap
    // ~0x0326…). Size/alignment immediates such as 0x10010 must not qualify.
    private static bool IsLikelyGuestPointer(ulong address) =>
        address >= 0x0100_0000UL && address < 0x0000_8000_0000_0000UL;

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

        // 4th/5th pointer often lives in the caller frame when RCX holds a
        // size (Astro SystemCreate: rcx=0x10010) instead of an out address.
        return ctx[CpuRegister.Rsp] + 0x20;
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

        Span<byte> header = stackalloc byte[SystemBlockSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x41505250); // 'APRP'
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], paramAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], memoryInfoAddress);
        if (!ctx.Memory.TryWrite(memoryAddress, header) ||
            !ctx.TryWriteUInt64(systemOutAddress, memoryAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "x5VPqg5iyAk",
        ExportName = "sceAudioPropagationSystemDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemDestroy(CpuContext ctx) => SoftOk(ctx);

    // RoomCreate(system, roomOut, settings): unresolved returned 0x80020002 and
    // tripped AudioPropagationRoom.cpp soft-assert after SystemCreate succeeded.
    [SysAbiExport(
        Nid = "8bI5h8req30",
        ExportName = "sceAudioPropagationRoomCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationRoomCreate(CpuContext ctx)
    {
        var system = ctx[CpuRegister.Rdi];
        var roomOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteOpaqueOut(ctx, roomOut, 0x4D4F4F52); // 'ROOM'
    }

    [SysAbiExport(
        Nid = "S0JwP2AFTTE",
        ExportName = "sceAudioPropagationRoomDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationRoomDestroy(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "d84otraxt2s",
        ExportName = "sceAudioPropagationSourceCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceCreate(CpuContext ctx)
    {
        var system = ctx[CpuRegister.Rdi];
        var sourceOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteOpaqueOut(ctx, sourceOut, 0x43525353); // 'SRCS'
    }

    [SysAbiExport(
        Nid = "wkseM3LWPuc",
        ExportName = "sceAudioPropagationSourceDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceDestroy(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "b-dYXrjSNZU",
        ExportName = "sceAudioPropagationPortalCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalCreate(CpuContext ctx)
    {
        var system = ctx[CpuRegister.Rdi];
        var portalOut = ctx[CpuRegister.Rsi];
        if (system == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteOpaqueOut(ctx, portalOut, 0x504F5254); // 'PORT'
    }

    [SysAbiExport(
        Nid = "ZQXE-xS6MTE",
        ExportName = "sceAudioPropagationPortalDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalDestroy(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "kIdb+iQUzCs",
        ExportName = "sceAudioPropagationSystemSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemSetAttributes(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "BbOT4vBwAjs",
        ExportName = "sceAudioPropagationResetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationResetAttributes(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "WXMhENV2NcA",
        ExportName = "sceAudioPropagationPortalSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationPortalSetAttributes(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "-wsUTr31yeg",
        ExportName = "sceAudioPropagationSourceSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceSetAttributes(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "B2KI2AachWE",
        ExportName = "sceAudioPropagationSystemLock",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemLock(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "CPLV6G-eXmk",
        ExportName = "sceAudioPropagationSystemRegisterMaterial",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemRegisterMaterial(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "XKCN4gpeYsM",
        ExportName = "sceAudioPropagationSystemUnregisterMaterial",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSystemUnregisterMaterial(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "PBcrVpEqUVY",
        ExportName = "sceAudioPropagationSourceCalculateAudioPaths",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceCalculateAudioPaths(CpuContext ctx) => SoftOk(ctx);

    [SysAbiExport(
        Nid = "hhz9pITnC8k",
        ExportName = "sceAudioPropagationSourceRender",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int AudioPropagationSourceRender(CpuContext ctx) => SoftOk(ctx);

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
