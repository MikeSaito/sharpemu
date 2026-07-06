// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AudioOutExports
{
    private static readonly ConcurrentDictionary<int, PortState> Ports = new();
    private static int _nextPortHandle;

    private sealed record PortState(int UserId, int Type, uint BufferLength, uint Frequency, int Format);

    [SysAbiExport(
        Nid = "JfEPXVxhFqA",
        ExportName = "sceAudioOutInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutInit(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "ekNvsT22rsY",
        ExportName = "sceAudioOutOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var bufferLength = unchecked((uint)ctx[CpuRegister.Rcx]);
        var frequency = unchecked((uint)ctx[CpuRegister.R8]);
        var format = unchecked((int)ctx[CpuRegister.R9]);
        if (bufferLength == 0 || frequency == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = Interlocked.Increment(ref _nextPortHandle);
        Ports[handle] = new PortState(userId, type, bufferLength, frequency, format);
        return SetReturn(ctx, handle);
    }

    [SysAbiExport(
        Nid = "s1--uE9mBFw",
        ExportName = "sceAudioOutClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return SetReturn(
            ctx,
            Ports.TryRemove(handle, out _)
                ? 0
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "QOQtbeDqsT4",
        ExportName = "sceAudioOutOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutOutput(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "b+uAV89IlxE",
        ExportName = "sceAudioOutSetVolume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int AudioOutSetVolume(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return SetReturn(
            ctx,
            Ports.ContainsKey(handle)
                ? 0
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
