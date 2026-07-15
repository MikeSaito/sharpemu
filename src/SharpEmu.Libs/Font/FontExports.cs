// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const int OrbisFontErrorInvalidParameter = unchecked((int)0x80460002);
    private const int OrbisFontErrorInvalidMemory = unchecked((int)0x80460003);
    private const int OrbisFontErrorAllocationFailed = unchecked((int)0x80460010);
    private const int OrbisFontMemSize = 0x40;
    private const int FontLibraryBlockSize = 0x100;
    private const int FontLibraryMspaceSize = 0x4000;
    private const int FontLibraryTailOffset = 0xB8;
    private const ushort OrbisFontMemKindInitialized = 0x0F00;
    private const ushort OrbisFontLibraryMagic = 0x0F01;
    private const uint OrbisFontLibraryFlags = 0x60000000;

    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FontMemoryInit(CpuContext ctx)
    {
        var memDescAddress = ctx[CpuRegister.Rdi];
        var regionAddress = ctx[CpuRegister.Rsi];
        var regionSize = ctx[CpuRegister.Rdx];
        var ifaceAddress = ctx[CpuRegister.Rcx];
        var mspaceAddress = ctx[CpuRegister.R8];
        var destroyCallbackAddress = ctx[CpuRegister.R9];
        if (memDescAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        if (ifaceAddress == 0 && (regionAddress == 0 || regionSize == 0))
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + 0x08, out var destroyContextAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> memDesc = stackalloc byte[OrbisFontMemSize];
        memDesc.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(memDesc, OrbisFontMemKindInitialized);
        BinaryPrimitives.WriteUInt32LittleEndian(memDesc[0x04..], checked((uint)regionSize));
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x08..], regionAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x10..], mspaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x18..], ifaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x20..], destroyCallbackAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x28..], destroyContextAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(memDesc[0x38..], mspaceAddress);

        if (!ctx.Memory.TryWrite(memDescAddress, memDesc))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        FontGuestState.RecordMemoryInit(regionAddress, regionSize);

        TraceFont(
            $"memory_init mem_desc=0x{memDescAddress:X16} region=0x{regionAddress:X16} " +
            $"region_size=0x{regionSize:X} iface=0x{ifaceAddress:X16} mspace=0x{mspaceAddress:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "n590hj5Oe-k",
        ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FontCreateLibraryWithEdition(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rdi];
        var driverTableAddress = ctx[CpuRegister.Rsi];
        var edition = ctx[CpuRegister.Rdx];
        var libraryOutAddress = ctx[CpuRegister.Rcx];
        if (libraryOutAddress != 0)
        {
            _ = ctx.TryWriteUInt64(libraryOutAddress, 0);
        }

        if (memoryAddress == 0 || driverTableAddress == 0 || libraryOutAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        if (!TryReadMemoryDescriptor(ctx, memoryAddress, out var memory) ||
            memory.MemKind != OrbisFontMemKindInitialized ||
            memory.IfaceAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidMemory);
        }

        if (!TryReadMemoryInterface(ctx, memory.IfaceAddress, out var iface) ||
            iface.AllocAddress == 0 ||
            iface.DeallocAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidMemory);
        }

        if (!FontGuestState.TryBumpAllocateZeroed(ctx, FontLibraryBlockSize, 0x10, out var libraryAddress) ||
            !FontGuestState.TryBumpAllocateZeroed(ctx, FontLibraryMspaceSize, 0x10, out var mspaceAddress))
        {
            return ctx.SetReturn(OrbisFontErrorAllocationFailed);
        }

        if (!TryWriteFontLibrary(
                ctx,
                libraryAddress,
                mspaceAddress,
                memory,
                iface,
                driverTableAddress) ||
            !ctx.TryWriteUInt64(libraryOutAddress, libraryAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceFont(
            $"create_library_with_edition memory=0x{memoryAddress:X16} driver=0x{driverTableAddress:X16} " +
            $"edition=0x{edition:X16} library=0x{libraryAddress:X16} mspace=0x{mspaceAddress:X16}");
        return ctx.SetReturn(0);
    }

    private static bool TryReadMemoryDescriptor(CpuContext ctx, ulong address, out FontMemoryDescriptor memory)
    {
        memory = default;
        Span<byte> buffer = stackalloc byte[OrbisFontMemSize];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        memory = new FontMemoryDescriptor(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[0x04..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x08..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x18..]));
        return true;
    }

    private static bool TryReadMemoryInterface(CpuContext ctx, ulong address, out FontMemoryInterface iface)
    {
        iface = default;
        Span<byte> buffer = stackalloc byte[0x30];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        iface = new FontMemoryInterface(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x08..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[0x18..]));
        return true;
    }

    private static bool TryWriteFontLibrary(
        CpuContext ctx,
        ulong libraryAddress,
        ulong mspaceAddress,
        FontMemoryDescriptor memory,
        FontMemoryInterface iface,
        ulong driverTableAddress)
    {
        Span<byte> library = stackalloc byte[FontLibraryBlockSize];
        library.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(library, OrbisFontLibraryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(library[0x08..], OrbisFontLibraryFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(library[0x0C..], OrbisFontMemKindInitialized);
        BinaryPrimitives.WriteUInt32LittleEndian(library[0x10..], memory.RegionSize);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x14..], memory.RegionBase);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x20..], memory.MspaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x28..], memory.IfaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x50..], iface.AllocAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x58..], iface.DeallocAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x60..], iface.ReallocAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x68..], iface.CallocAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[0x80..], driverTableAddress);

        var tailAddress = libraryAddress + FontLibraryTailOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(library[(FontLibraryTailOffset + 0x04)..], FontLibraryMspaceSize);
        BinaryPrimitives.WriteUInt64LittleEndian(library[(FontLibraryTailOffset + 0x08)..], mspaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(library[(FontLibraryTailOffset + 0x20)..], tailAddress + 0x28);

        return ctx.Memory.TryWrite(libraryAddress, library);
    }

    private readonly record struct FontMemoryDescriptor(
        ushort MemKind,
        uint RegionSize,
        ulong RegionBase,
        ulong MspaceAddress,
        ulong IfaceAddress);

    private readonly record struct FontMemoryInterface(
        ulong AllocAddress,
        ulong DeallocAddress,
        ulong ReallocAddress,
        ulong CallocAddress);

    private static void TraceFont(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FONT"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] font.{message}");
        }
    }
}
