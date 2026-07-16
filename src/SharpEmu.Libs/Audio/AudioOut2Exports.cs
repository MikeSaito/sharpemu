// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // Sized from guest evidence, not SDK headers: Quake keeps its
    // SceAudioOut2ContextParam on the stack with the frame canary at param+0x60,
    // and an earlier 0x80-byte ResetParam write zeroed that canary
    // (__stack_chk_fail right after sceAudioOut2UserCreate, which silently killed
    // the whole audio init). Stay well below 0x60 and only write the prefix we
    // populate.
    private const int AudioOut2ContextParamSize = 0x30;
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const int PortStateSize = 0x20;
    // Observed call sites pass attribute arrays; entries are tagged unions of
    // { int32 attributeId; int32 pad; uint64 value; } (16 bytes).
    private const int AttributeEntrySize = 0x10;
    private const int MaxAttributeCount = 64;

    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static readonly ConcurrentDictionary<ulong, AudioOut2PortState> _ports = new();

    private sealed class AudioOut2PortState
    {
        public required int Type { get; init; }
        public ushort Output { get; set; }
        public byte Channels { get; set; }
        // GetState field at +0x04; -1 means "unset" in the previous stub defaults.
        public short Status { get; set; }
        public Dictionary<int, ulong> Attributes { get; } = new();
    }

    private static AudioOut2PortState CreateDefaultPortState(int type) => new()
    {
        Type = type,
        Output = unchecked((ushort)(type == 2 ? 0x40 : 0x01)),
        Channels = unchecked((byte)(type == 2 ? 1 : 2)),
        Status = -1,
    };

    private static void WritePortState(Span<byte> destination, AudioOut2PortState port)
    {
        destination.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x00..], port.Output);
        destination[0x02] = port.Channels;
        BinaryPrimitives.WriteInt16LittleEndian(destination[0x04..], port.Status);
    }

    private static void ApplyAttribute(AudioOut2PortState port, int attributeId, ulong value)
    {
        port.Attributes[attributeId] = value;

        // Map the fields PortGetState already exposes. Unknown tags stay in the
        // attribute table for later layout discovery without game-specific hacks.
        switch (attributeId)
        {
            case 0:
            case 1:
                port.Output = unchecked((ushort)value);
                break;
            case 2:
                port.Channels = unchecked((byte)value);
                break;
            case 3:
            case 4:
                port.Status = unchecked((short)value);
                break;
        }
    }

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], AudioOut2ContextParamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 0x400);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var outMemorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || outMemorySizeAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        
        return ctx.TryWriteUInt64(outMemorySizeAddress, AudioOut2ContextMemorySize)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        return ctx.TryWriteUInt64(outContextAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        if (contextHandle == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var queuedAddress = ctx[CpuRegister.Rsi];
        var availableAddress = ctx[CpuRegister.Rdx];
        if (contextHandle == 0 || queuedAddress == 0 || availableAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Report an empty queue so guests that spin until level==0 can proceed.
        // Leave available as a positive capacity so "wait for room" polls also exit.
        if (!ctx.TryWriteInt32(queuedAddress, 0) || !ctx.TryWriteInt32(availableAddress, 0x400))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var type = unchecked((int)ctx[CpuRegister.Rdi]);
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        var contextAddress = ctx[CpuRegister.Rcx];
        if (type < 0 || type > 255 || paramAddress == 0 || outPortAddress == 0 || contextAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)(uint)type << 16) | portId;
        _ports[handle] = CreateDefaultPortState(type);
        if (!ctx.TryWriteUInt64(outPortAddress, handle))
        {
            _ports.TryRemove(handle, out _);
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!_ports.TryGetValue(handle, out var port))
        {
            var type = (int)((handle >> 16) & 0xFF);
            port = CreateDefaultPortState(type);
            _ports[handle] = port;
        }

        Span<byte> state = stackalloc byte[PortStateSize];
        WritePortState(state, port);

        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var attributeAddress = ctx[CpuRegister.Rsi];
        var attributeCount = unchecked((int)ctx[CpuRegister.Rdx]);
        if (handle == 0 || attributeAddress == 0 || attributeCount < 0 || attributeCount > MaxAttributeCount)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!_ports.TryGetValue(handle, out var port))
        {
            var type = (int)((handle >> 16) & 0xFF);
            port = CreateDefaultPortState(type);
            _ports[handle] = port;
        }

        if (attributeCount == 0)
        {
            return ctx.SetReturn(0);
        }

        Span<byte> entry = stackalloc byte[AttributeEntrySize];
        for (var i = 0; i < attributeCount; i++)
        {
            if (!ctx.Memory.TryRead(attributeAddress + (ulong)(i * AttributeEntrySize), entry))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var attributeId = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x08..]);
            ApplyAttribute(port, attributeId, value);
        }

        // A successful SetAttributes clears the previous "unset" status so
        // GetState no longer looks like a fresh PortCreate default forever.
        if (port.Status == -1)
        {
            port.Status = 0;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[0x40];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 48000);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle != 0)
        {
            _ports.TryRemove(handle, out _);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 255) || outUserAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return ctx.TryWriteUInt64(outUserAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
