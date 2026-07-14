// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Font;

internal static class FontGuestState
{
    private const int OrbisProtCpuRead = 0x01;
    private const int OrbisProtCpuWrite = 0x02;
    private const int OrbisProtCpuExec = 0x04;
    private const int ExecutableReadWrite = OrbisProtCpuRead | OrbisProtCpuWrite | OrbisProtCpuExec;
    private const int SysDriverSize = 0x120;
    private const ulong StubPageSize = 0x1000;
    private static readonly byte[] RetZeroStub = [0x31, 0xC0, 0xC3];
    private static readonly byte[] Ret64Stub = [0xB8, 0x40, 0x00, 0x00, 0x00, 0xC3];
    private static readonly int[] SysDriverFunctionOffsets =
    [
        0x18, 0x20, 0x28, 0x38, 0x40, 0x50, 0x60, 0x68, 0x78, 0x80, 0x88, 0xA0, 0xA8, 0xB8, 0xE0, 0x108, 0x118,
    ];

    private static readonly object Gate = new();
    private static ulong _driverTableAddress;
    private static ulong _regionBase;
    private static ulong _regionSize;
    private static ulong _bumpOffset;

    internal static void RecordMemoryInit(ulong regionBase, ulong regionSize)
    {
        lock (Gate)
        {
            _regionBase = regionBase;
            _regionSize = regionSize;
            _bumpOffset = 0;
        }
    }

    internal static bool TryEnsureFreeTypeDriverTable(CpuContext ctx, out ulong tableAddress)
    {
        lock (Gate)
        {
            if (_driverTableAddress != 0)
            {
                tableAddress = _driverTableAddress;
                return true;
            }

            if (!TryBuildFreeTypeDriverTable(ctx, out tableAddress))
            {
                tableAddress = 0;
                return false;
            }

            _driverTableAddress = tableAddress;
            return true;
        }
    }

    private static bool TryBuildFreeTypeDriverTable(CpuContext ctx, out ulong tableAddress)
    {
        tableAddress = 0;
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, StubPageSize, 0x1000, ExecutableReadWrite, out var stubPage))
        {
            return false;
        }

        if (!ctx.Memory.TryWrite(stubPage, RetZeroStub) ||
            !ctx.Memory.TryWrite(stubPage + 0x10, Ret64Stub))
        {
            return false;
        }

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, SysDriverSize, 0x10, out tableAddress))
        {
            return false;
        }

        Span<byte> driver = stackalloc byte[SysDriverSize];
        driver.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(driver[0x10..], stubPage + 0x10);
        foreach (var offset in SysDriverFunctionOffsets)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(driver[offset..], stubPage);
        }

        return ctx.Memory.TryWrite(tableAddress, driver);
    }

    internal static bool TryBumpAllocate(ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        if (_regionBase == 0 || _regionSize == 0 || size == 0)
        {
            return false;
        }

        var effectiveAlignment = Math.Max(alignment, 16UL);
        lock (Gate)
        {
            var alignedOffset = AlignUp(_bumpOffset, effectiveAlignment);
            if (alignedOffset > _regionSize || size > _regionSize - alignedOffset)
            {
                return false;
            }

            address = _regionBase + alignedOffset;
            _bumpOffset = alignedOffset + size;
            return true;
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }
}
