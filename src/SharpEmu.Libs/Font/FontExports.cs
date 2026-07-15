// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const int OrbisFontErrorInvalidParameter = unchecked((int)0x80460002);
    private const int OrbisFontErrorInvalidMemory = unchecked((int)0x80460003);
    private const int OrbisFontErrorInvalidLibrary = unchecked((int)0x80460004);
    private const int OrbisFontErrorAllocationFailed = unchecked((int)0x80460010);
    private const int OrbisFontErrorAlreadyAttached = unchecked((int)0x80460022);
    private const int OrbisFontMemSize = 0x40;
    private const int FontLibraryBlockSize = 0x100;
    private const int FontLibraryMspaceSize = 0x4000;
    private const int FontLibraryTailOffset = 0xB8;
    private const int FontLibraryDeviceCacheOffset = 0xB0;
    private const int FontLibraryFlagsOffset = 0x08;
    private const int FontLibraryMagicOffset = 0x00;
    private const int DeviceCacheMinSize = 0x1020;
    private const ushort OrbisFontMemKindInitialized = 0x0F00;
    private const ushort OrbisFontLibraryMagic = 0x0F01;
    private const uint OrbisFontLibraryFlags = 0x60000000;
    private const uint OrbisFontDeviceCacheOwnedFlag = 1u;
    private const ulong OrbisFontDeviceCacheHeaderMarker = 0x0FF800001000UL;

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

    [SysAbiExport(
        Nid = "SsRbbCiWoGw",
        ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FontSupportSystemFonts(CpuContext ctx)
    {
        var libraryAddress = ctx[CpuRegister.Rdi];
        if (libraryAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        TraceFont($"support_system_fonts library=0x{libraryAddress:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "mz2iTY0MK4A",
        ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FontSupportExternalFonts(CpuContext ctx)
    {
        var libraryAddress = ctx[CpuRegister.Rdi];
        var fontMax = ctx[CpuRegister.Rsi];
        var formats = ctx[CpuRegister.Rdx];
        if (libraryAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        TraceFont(
            $"support_external_fonts library=0x{libraryAddress:X16} " +
            $"font_max=0x{fontMax:X} formats=0x{formats:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "CUKn5pX-NVY",
        ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FontAttachDeviceCacheBuffer(CpuContext ctx)
    {
        var libraryAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var cacheSize = ctx[CpuRegister.Rdx];
        if (libraryAddress == 0)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidLibrary);
        }

        if (!ctx.TryReadUInt16(libraryAddress + FontLibraryMagicOffset, out var magic) ||
            magic != OrbisFontLibraryMagic)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidLibrary);
        }

        if (!ctx.TryReadUInt64(libraryAddress + FontLibraryDeviceCacheOffset, out var existingCache))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (existingCache != 0)
        {
            return ctx.SetReturn(OrbisFontErrorAlreadyAttached);
        }

        if (cacheSize < DeviceCacheMinSize)
        {
            return ctx.SetReturn(OrbisFontErrorInvalidParameter);
        }

        var owned = false;
        if (bufferAddress == 0)
        {
            if (!FontGuestState.TryAllocateFontBuffer(ctx, cacheSize, 0x10, out bufferAddress))
            {
                return ctx.SetReturn(OrbisFontErrorAllocationFailed);
            }

            owned = true;
        }

        if (!TryWriteDeviceCacheHeader(ctx, bufferAddress, checked((uint)cacheSize)))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (owned)
        {
            if (!ctx.TryReadUInt32(libraryAddress + FontLibraryFlagsOffset, out var flags) ||
                !ctx.TryWriteUInt32(libraryAddress + FontLibraryFlagsOffset, flags | OrbisFontDeviceCacheOwnedFlag))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (!ctx.TryWriteUInt64(libraryAddress + FontLibraryDeviceCacheOffset, bufferAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceFont(
            $"attach_device_cache_buffer library=0x{libraryAddress:X16} " +
            $"buffer=0x{bufferAddress:X16} size=0x{cacheSize:X} owned={owned}");
        return ctx.SetReturn(0);
    }

    private static bool TryWriteDeviceCacheHeader(CpuContext ctx, ulong bufferAddress, uint size)
    {
        var pageCount = (size - 0x1000u) >> 12;
        Span<byte> header = stackalloc byte[0x18];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], pageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x08..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x0C..], pageCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], OrbisFontDeviceCacheHeaderMarker);
        return ctx.Memory.TryWrite(bufferAddress, header);
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
