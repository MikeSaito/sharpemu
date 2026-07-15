// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ContentExport;

public static class ContentExportExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "0GnN4QCgIfs",
        ExportName = "sceContentExportInit2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportInit2(CpuContext ctx)
    {
        var initParamAddress = ctx[CpuRegister.Rdi];
        var arg1 = ctx[CpuRegister.Rsi];
        var arg2 = ctx[CpuRegister.Rdx];
        if (initParamAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Interlocked.Exchange(ref _initialized, 1);
        TraceContentExport(
            $"init2 param=0x{initParamAddress:X16} arg1=0x{arg1:X} arg2=0x{arg2:X}");
        return ctx.SetReturn(0);
    }

    private static void TraceContentExport(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CONTENT_EXPORT"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] content_export.{message}");
        }
    }
}
