// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const int OrbisFontErrorInvalidParameter = unchecked((int)0x80460002);
    private const int OrbisFontMemSize = 0x40;
    private const ushort OrbisFontMemKindInitialized = 0x0F00;

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

        TraceFont(
            $"memory_init mem_desc=0x{memDescAddress:X16} region=0x{regionAddress:X16} " +
            $"region_size=0x{regionSize:X} iface=0x{ifaceAddress:X16} mspace=0x{mspaceAddress:X16}");
        return ctx.SetReturn(0);
    }

    private static void TraceFont(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FONT"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] font.{message}");
        }
    }
}
