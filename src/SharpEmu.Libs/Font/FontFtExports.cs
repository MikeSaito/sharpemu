// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static class FontFtExports
{
    [SysAbiExport(
        Nid = "oM+XCzVG3oM",
        ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int FontSelectLibraryFt(CpuContext ctx)
    {
        var value = unchecked((int)ctx[CpuRegister.Rdi]);
        if (value != 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (!FontGuestState.TryEnsureFreeTypeDriverTable(ctx, out var tableAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = tableAddress;
        TraceFontFt($"select_library_ft value={value} table=0x{tableAddress:X16}");
        return 0;
    }

    private static void TraceFontFt(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FONT"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] font.{message}");
        }
    }
}
