// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Psml;

public static class PsmlExports
{
    // Empirically for Astro Bot (PPSA21567):
    //   [0] must be 0x80 (sizeThis / r9); any other value → Allocate length=0
    //   AllocateMainDirectMemory(length=[0]*[16], alignment=[8], type=0xC)
    // So put desired byte size in [8] (becomes alignment) and page size in [16].
    private const ulong RequirementStructSize = 0x80;
    private const ulong SharedResourcesPageSize = 0x10000;
    private const ulong SharedResourcesBufferSizeBytes = 0x2000000;
    private const ulong SharedResourcesContextSizeBytes = 0x100000;
    private const ulong ContextBufferSizeBytes = 0x800000;
    private const ulong ContextBufferAuxSizeBytes = 0x100000;

    private static int _mfsrInitialized;
    private static readonly Lock SharedResourcesGate = new();
    private static readonly Dictionary<ulong, SharedResourcesState> SharedResourcesByDescriptor = new();

    [SysAbiExport(
        Nid = "3WVD91e12ZQ",
        ExportName = "scePsmlMfsrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrInit(CpuContext ctx)
    {
        var arg0 = ctx[CpuRegister.Rdi];
        var arg1 = ctx[CpuRegister.Rsi];
        var arg2 = ctx[CpuRegister.Rdx];
        Interlocked.Exchange(ref _mfsrInitialized, 1);
        TracePsml($"mfsr_init arg0=0x{arg0:X} arg1=0x{arg1:X} arg2=0x{arg2:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "+2KpvixvL6E",
        ExportName = "scePsmlMfsrGetSharedResourcesInitRequirement",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetSharedResourcesInitRequirement(CpuContext ctx)
    {
        var bufferRequirementAddress = ctx[CpuRegister.Rdi];
        var contextRequirementAddress = ctx[CpuRegister.Rsi];
        var flags = ctx[CpuRegister.Rdx];
        var configAddress = ctx[CpuRegister.Rcx];
        if (bufferRequirementAddress == 0 || contextRequirementAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!WriteMemoryRequirement(ctx, bufferRequirementAddress, SharedResourcesBufferSizeBytes) ||
            !WriteMemoryRequirement(ctx, contextRequirementAddress, SharedResourcesContextSizeBytes))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TracePsml(
            $"mfsr_get_shared_resources_init_requirement buf=0x{bufferRequirementAddress:X16} " +
            $"ctx=0x{contextRequirementAddress:X16} flags=0x{flags:X} config=0x{configAddress:X16} " +
            $"buf_size=0x{SharedResourcesBufferSizeBytes:X} ctx_size=0x{SharedResourcesContextSizeBytes:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "eWoKNeB6V-k",
        ExportName = "scePsmlMfsrCreateSharedResources",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrCreateSharedResources(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var contextRequirementAddress = ctx[CpuRegister.Rsi];
        var directMemoryAddress = ctx[CpuRegister.Rdx];
        if (descriptorAddress == 0 || contextRequirementAddress == 0 || directMemoryAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var contextSizeBytes = ReadRequirementSize(ctx, contextRequirementAddress, SharedResourcesContextSizeBytes);
        var state = new SharedResourcesState(
            DescriptorAddress: descriptorAddress,
            DirectMemoryAddress: directMemoryAddress,
            BufferSizeBytes: SharedResourcesBufferSizeBytes,
            ContextSizeBytes: contextSizeBytes,
            PageSizeBytes: SharedResourcesPageSize);

        if (!WriteSharedResourcesDescriptor(ctx, state))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (SharedResourcesGate)
        {
            SharedResourcesByDescriptor[descriptorAddress] = state;
        }

        TracePsml(
            $"mfsr_create_shared_resources desc=0x{descriptorAddress:X16} req=0x{contextRequirementAddress:X16} " +
            $"direct=0x{directMemoryAddress:X16} buf_size=0x{state.BufferSizeBytes:X} " +
            $"ctx_size=0x{state.ContextSizeBytes:X} page=0x{state.PageSizeBytes:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ArakEpzsZo0",
        ExportName = "scePsmlMfsrGetContextBufferRequirement800M3_2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int PsmlMfsrGetContextBufferRequirement800M3_2(CpuContext ctx)
    {
        var bufferRequirementAddress = ctx[CpuRegister.Rdi];
        var contextRequirementAddress = ctx[CpuRegister.Rsi];
        var directMemoryAddress = ctx[CpuRegister.Rdx];
        if (bufferRequirementAddress == 0 || contextRequirementAddress == 0 || directMemoryAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!WriteMemoryRequirement(ctx, bufferRequirementAddress, ContextBufferSizeBytes) ||
            !WriteMemoryRequirement(ctx, contextRequirementAddress, ContextBufferAuxSizeBytes))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TracePsml(
            $"mfsr_get_context_buffer_requirement_800m3_2 buf=0x{bufferRequirementAddress:X16} " +
            $"ctx=0x{contextRequirementAddress:X16} direct=0x{directMemoryAddress:X16} " +
            $"buf_size=0x{ContextBufferSizeBytes:X} aux_size=0x{ContextBufferAuxSizeBytes:X}");
        return ctx.SetReturn(0);
    }

    private static bool WriteMemoryRequirement(CpuContext ctx, ulong address, ulong sizeBytes)
    {
        return ctx.TryWriteUInt64(address, RequirementStructSize) &&
               ctx.TryWriteUInt64(address + 0x08, sizeBytes) &&
               ctx.TryWriteUInt64(address + 0x10, SharedResourcesPageSize);
    }

    private static ulong ReadRequirementSize(CpuContext ctx, ulong address, ulong fallback)
    {
        return ctx.TryReadUInt64(address + 0x08, out var sizeBytes) && sizeBytes != 0
            ? sizeBytes
            : fallback;
    }

    private static bool WriteSharedResourcesDescriptor(CpuContext ctx, SharedResourcesState state)
    {
        // Stamp a compact self-describing blob so follow-up PSML calls can treat the
        // descriptor as initialized guest memory instead of an all-zero placeholder.
        return ctx.TryWriteUInt64(state.DescriptorAddress + 0x00, RequirementStructSize) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x08, state.DirectMemoryAddress) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x10, state.BufferSizeBytes) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x18, state.ContextSizeBytes) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x20, state.PageSizeBytes) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x28, state.DescriptorAddress) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x30, state.DirectMemoryAddress) &&
               ctx.TryWriteUInt64(state.DescriptorAddress + 0x38, state.BufferSizeBytes + state.ContextSizeBytes);
    }

    private static void TracePsml(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PSML"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] psml.{message}");
        }
    }

    private readonly record struct SharedResourcesState(
        ulong DescriptorAddress,
        ulong DirectMemoryAddress,
        ulong BufferSizeBytes,
        ulong ContextSizeBytes,
        ulong PageSizeBytes);
}
