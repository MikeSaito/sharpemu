// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    // Keep the pad session on the same retail user id returned by
    // libSceUserService.  A mismatched emulator-local id makes games pass a
    // valid 0x10000000 user to scePadOpen and receive DEVICE_NOT_CONNECTED,
    // leaving every later keyboard/gamepad read on an invalid handle.
    private const int PrimaryUserId = 0x10000000;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;

    // Real firmware hands out small non-negative handles; 0 is valid. Some titles
    // (Monster Truck Championship) read pad state with handle 0, and rejecting it
    // leaves their controller/FFB init path polling a never-valid state forever.
    private static bool IsPrimaryPadHandle(int handle) => handle is 0 or PrimaryPadHandle;
    private static readonly long InputSampleIntervalTicks = Math.Max(1, Stopwatch.Frequency / 1000);

    [ThreadStatic]
    private static long _lastInputSampleTicks;

    [ThreadStatic]
    private static PadState _cachedInputState;

    [ThreadStatic]
    private static bool _cachedFocused;

    [ThreadStatic]
    private static bool _cachedKeyboardArmed;

    [ThreadStatic]
    private static uint _cachedInjected;

    private static bool _initialized;
    private static int _controlsAnnouncementLogged;
    private static long _padOpenedAtTicks;
    private static int _padWriteCount;
    private static uint _lastLoggedButtons = uint.MaxValue;
    private static int _crossDeliveredLogged;

    private static readonly bool LogPad =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PAD"), "1", StringComparison.Ordinal);

    // Headless runs often leave the present window unfocused; keyboard Cross
    // never reaches scePadRead unless this is set (or a controller is used).
    private static readonly bool AllowUnfocusedKeyboard =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PAD_ALLOW_UNFOCUSED"),
            "1",
            StringComparison.Ordinal);

    // Harness-only: pulse SCE_PAD_BUTTON_CROSS into scePadRead after open.
    private static readonly bool InjectCross =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PAD_INJECT_CROSS"),
            "1",
            StringComparison.Ordinal);

    private static readonly int InjectAfterMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_AFTER_MS", 8_000);
    // Delay after title_controller_ship before the re-armed Cross pulses.
    private static readonly int InjectTitleDelayMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_TITLE_DELAY_MS", 2_000);
    private static readonly int InjectHoldMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_HOLD_MS", 400);
    // Longer Options hold after Cross — title "press Options / hold" prompts.
    private static readonly int InjectOptionsHoldMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_OPTIONS_HOLD_MS", 1_500);
    private static readonly int InjectGapMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_GAP_MS", 2_000);
    private static readonly int InjectPulses = Math.Clamp(ReadNonNegativeEnvInt("SHARPEMU_PAD_INJECT_PULSES", 3), 0, 32);
    private static readonly int InjectOptionsPulses = Math.Clamp(ReadNonNegativeEnvInt("SHARPEMU_PAD_INJECT_OPTIONS_PULSES", 3), 0, 32);
    private static long _injectEpochTicks;
    private static int _injectDelayMs = InjectAfterMs;
    private static int _titleInjectArmed;
    private static int _optionsDeliveredLogged;

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        HostPlatform.Current.Input.EnsureStarted();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx) => PadOpenCore(ctx, extended: false);

    [SysAbiExport(
        Nid = "WFIiSfXGUq8",
        ExportName = "scePadOpenExt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpenExt(CpuContext ctx) => PadOpenCore(ctx, extended: true);

    // scePadOpen rejects a non-null 4th arg and non-standard ports; scePadOpenExt accepts a
    // ScePadOpenExtParam* plus ports 1/2 (racing titles retry scePadOpenExt(type=2) forever if rejected).
    private static int PadOpenCore(CpuContext ctx, bool extended)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var parameterAddress = ctx[CpuRegister.Rcx];
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        var typeAccepted = extended ? type is 0 or 1 or 2 : type == StandardPortType;
        if (userId != PrimaryUserId || !typeAccepted || index != 0 || (!extended && parameterAddress != 0))
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        var input = HostPlatform.Current.Input;
        input.EnsureStarted();
        if (_padOpenedAtTicks == 0)
        {
            _padOpenedAtTicks = Stopwatch.GetTimestamp();
            if (_injectEpochTicks == 0)
            {
                _injectEpochTicks = _padOpenedAtTicks;
                _injectDelayMs = InjectAfterMs;
            }
        }

        if (Interlocked.Exchange(ref _controlsAnnouncementLogged, 1) == 0)
        {
            Console.Error.WriteLine(input.DescribeConnectedGamepad() is { } gamepadName
                ? $"[LOADER][INFO] Controls: {gamepadName} connected (keyboard fallback also active)."
                : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
            if (AllowUnfocusedKeyboard)
            {
                Console.Error.WriteLine("[LOADER][INFO] pad.keyboard_unfocused=1 (SHARPEMU_PAD_ALLOW_UNFOCUSED)");
            }

            if (InjectCross)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] pad.inject_cross=1 after_ms={InjectAfterMs} title_delay_ms={InjectTitleDelayMs} " +
                    $"hold_ms={InjectHoldMs} options_hold_ms={InjectOptionsHoldMs} gap_ms={InjectGapMs} " +
                    $"pulses={InjectPulses} options_pulses={InjectOptionsPulses}");
            }
        }

        return ctx.SetReturn(PrimaryPadHandle);
    }

    [SysAbiExport(
        Nid = "6ncge5+l5Qs",
        ExportName = "scePadClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "vDLMoJLde8I",
        ExportName = "scePadSetTiltCorrectionState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTiltCorrectionState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "gjP9-KQzoUk",
        ExportName = "scePadGetControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[ControllerInformationSize];
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;
        information[0x0C] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "hGbf2QTBmqc",
        ExportName = "scePadGetExtControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetExtControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Base ScePadControllerInformation + device-class/connection fields: report a connected
        // DualSense so the guest's open -> get-ext-info -> close probe loop resolves.
        Span<byte> information = stackalloc byte[0x40];
        information.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;   // connected count
        information[0x0C] = 1;   // connected
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);
        information[0x1C] = 0;   // deviceClass: 0 = standard controller / DualSense
        information[0x1D] = 1;   // connected (ext)
        information[0x1E] = 0;   // connectionType: local

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress, "readState")
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "q1cHNfGycLI",
        ExportName = "scePadRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress, "read")
            ? ctx.SetReturn(1)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
    Nid = "W2G-yoyMF5U",
    ExportName = "scePadSetVibrationMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libScePad")]
    public static int PadSetVibrationMode(CpuContext ctx)
    {
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "2JgFB2n9oUM",
        ExportName = "scePadSetTriggerEffect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTriggerEffect(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> parameter = stackalloc byte[120];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var triggerMask = parameter[0];
        HostPlatform.Current.Input.SetTriggerRumble(
            (triggerMask & 0x01) != 0 ? DecodeTriggerVibration(parameter[8..64]) : null,
            (triggerMask & 0x02) != 0 ? DecodeTriggerVibration(parameter[64..120]) : null);
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "znaWI0gpuo8",
        ExportName = "scePadGetTriggerEffectState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetTriggerEffectState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        // Soft success only. Astro title polls this every frame; writing the
        // out-buffer is unsafe when rsi lands in host stack (0x7FFF…) as seen
        // on unresolved trampoline call sites.
        return ctx.SetReturn(0);
    }

    private static byte DecodeTriggerVibration(ReadOnlySpan<byte> command)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(command);
        var amplitude = mode switch
        {
            3 when command[10] != 0 => command[9],
            6 when command[8] != 0 => command[9..19].ToArray().Max(),
            _ => (byte)0,
        };
        return (byte)(Math.Min(amplitude, (byte)8) * 255 / 8);
    }

    [SysAbiExport(
        Nid = "yFVnOdGxvZY",
        ExportName = "scePadSetVibration",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetVibration(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadVibrationParam: { uint8_t largeMotor; uint8_t smallMotor; }
        Span<byte> parameter = stackalloc byte[2];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        HostPlatform.Current.Input.SetRumble(parameter[0], parameter[1]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RR4novUEENY",
        ExportName = "scePadSetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadColor: { uint8_t r; uint8_t g; uint8_t b; uint8_t reserved; }
        Span<byte> color = stackalloc byte[4];
        if (!ctx.Memory.TryRead(parameterAddress, color))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        HostPlatform.Current.Input.SetLightbar(color[0], color[1], color[2]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "DscD1i9HX1w",
        ExportName = "scePadResetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadResetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        HostPlatform.Current.Input.ResetLightbar();
        return ctx.SetReturn(0);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress, string api)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var sample = ReadHostInputSample();
        var input = sample.State;
        var buttons = input.Buttons;
        var leftX = input.LeftX;
        var leftY = input.LeftY;
        var rightX = input.RightX;
        var rightY = input.RightY;
        var l2 = input.L2;
        var r2 = input.R2;

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = l2;
        data[0x09] = r2;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        var timestampTicks = Stopwatch.GetTimestamp();
        var timestampMicroseconds =
            ((ulong)(timestampTicks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(timestampTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);
        data[0x68] = 1;

        if (!ctx.Memory.TryWrite(dataAddress, data))
        {
            return false;
        }

        TracePadWrite(api, sample);
        return true;
    }

    private readonly record struct HostInputSample(
        PadState State,
        bool Focused,
        bool KeyboardArmed,
        uint Injected);

    private static HostInputSample ReadHostInputSample()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastInputSampleTicks != 0 && now - _lastInputSampleTicks < InputSampleIntervalTicks)
        {
            return new HostInputSample(
                _cachedInputState,
                _cachedFocused,
                _cachedKeyboardArmed,
                _cachedInjected);
        }

        var input = HostPlatform.Current.Input;
        var focused = input.IsHostWindowFocused();
        var acceptsKeyboardInput = focused || AllowUnfocusedKeyboard;
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons(input) : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x41), input.IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x57), input.IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x4A), input.IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x49), input.IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && input.IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && input.IsKeyDown(0x46) ? (byte)255 : (byte)0;

        Span<HostGamepadState> gamepads = stackalloc HostGamepadState[2];
        var gamepadCount = input.GetGamepadStates(gamepads);
        for (var index = 0; index < gamepadCount; index++)
        {
            var pad = gamepads[index];
            buttons |= ToOrbisButtons(pad.Buttons);
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.LeftTrigger);
            r2 = Math.Max(r2, pad.RightTrigger);
        }

        if (IsAutoCrossActive())
        {
            buttons |= OrbisPadButton.Cross;
        }

        var injected = ResolveInjectedButtons(now);
        if (InjectCross)
        {
            // Tab/Backspace map to Options; with ALLOW_UNFOCUSED that bit often
            // sticks from the host IDE and opens pause on Astro's title so Cross
            // never starts the game. Harness inject re-adds Options when wanted.
            buttons &= ~OrbisPadButton.Options;
        }

        buttons |= injected;

        _cachedInputState = new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2);
        _cachedFocused = focused;
        _cachedKeyboardArmed = acceptsKeyboardInput;
        _cachedInjected = injected;
        _lastInputSampleTicks = now;
        return new HostInputSample(
            _cachedInputState,
            focused,
            acceptsKeyboardInput,
            injected);
    }

    private static readonly long PadStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double[] AutoCrossTimes = ParseAutoCrossTimes();

    private static double[] ParseAutoCrossTimes()
    {
        // SHARPEMU_AUTO_CROSS="40,52,64": presses Cross for 0.4s at each
        // second offset from process start. Debug aid for unattended runs.
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_AUTO_CROSS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var values = new List<double>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool IsAutoCrossActive()
    {
        var times = AutoCrossTimes;
        if (times.Length == 0)
        {
            return false;
        }

        var elapsed = (Stopwatch.GetTimestamp() - PadStartTimestamp) / (double)Stopwatch.Frequency;
        foreach (var time in times)
        {
            if (elapsed >= time && elapsed < time + 0.4)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps the host seam's neutral button flags onto SCE_PAD_BUTTON bits.</summary>
    private static uint ToOrbisButtons(HostGamepadButtons buttons)
    {
        uint result = 0;
        if ((buttons & HostGamepadButtons.Up) != 0) result |= OrbisPadButton.Up;
        if ((buttons & HostGamepadButtons.Down) != 0) result |= OrbisPadButton.Down;
        if ((buttons & HostGamepadButtons.Left) != 0) result |= OrbisPadButton.Left;
        if ((buttons & HostGamepadButtons.Right) != 0) result |= OrbisPadButton.Right;
        if ((buttons & HostGamepadButtons.Cross) != 0) result |= OrbisPadButton.Cross;
        if ((buttons & HostGamepadButtons.Circle) != 0) result |= OrbisPadButton.Circle;
        if ((buttons & HostGamepadButtons.Square) != 0) result |= OrbisPadButton.Square;
        if ((buttons & HostGamepadButtons.Triangle) != 0) result |= OrbisPadButton.Triangle;
        if ((buttons & HostGamepadButtons.L1) != 0) result |= OrbisPadButton.L1;
        if ((buttons & HostGamepadButtons.R1) != 0) result |= OrbisPadButton.R1;
        if ((buttons & HostGamepadButtons.L2) != 0) result |= OrbisPadButton.L2;
        if ((buttons & HostGamepadButtons.R2) != 0) result |= OrbisPadButton.R2;
        if ((buttons & HostGamepadButtons.L3) != 0) result |= OrbisPadButton.L3;
        if ((buttons & HostGamepadButtons.R3) != 0) result |= OrbisPadButton.R3;
        if ((buttons & HostGamepadButtons.Options) != 0) result |= OrbisPadButton.Options;
        if ((buttons & HostGamepadButtons.TouchPad) != 0) result |= OrbisPadButton.TouchPad;
        return result;
    }

    private static uint ReadKeyboardButtons(IHostInput input)
    {
        uint buttons = 0;
        // D-pad
        if (input.IsKeyDown(0x25)) buttons |= OrbisPadButton.Left;
        if (input.IsKeyDown(0x27)) buttons |= OrbisPadButton.Right;
        if (input.IsKeyDown(0x26)) buttons |= OrbisPadButton.Up;
        if (input.IsKeyDown(0x28)) buttons |= OrbisPadButton.Down;
        // Face buttons
        if (input.IsKeyDown(0x5A) || input.IsKeyDown(0x0D)) buttons |= OrbisPadButton.Cross;    // Z / Enter
        if (input.IsKeyDown(0x58) || input.IsKeyDown(0x1B)) buttons |= OrbisPadButton.Circle;   // X / Escape
        if (input.IsKeyDown(0x43)) buttons |= OrbisPadButton.Square;                            // C
        if (input.IsKeyDown(0x56)) buttons |= OrbisPadButton.Triangle;                          // V
        // Shoulder buttons
        if (input.IsKeyDown(0x51)) buttons |= OrbisPadButton.L1;                                // Q
        if (input.IsKeyDown(0x45)) buttons |= OrbisPadButton.R1;                                // E
        if (input.IsKeyDown(0x52)) buttons |= OrbisPadButton.L2;                                // R (digital)
        if (input.IsKeyDown(0x46)) buttons |= OrbisPadButton.R2;                                // F (digital)
        // Options (Start)
        if (input.IsKeyDown(0x09) || input.IsKeyDown(0x08)) buttons |= OrbisPadButton.Options;  // Tab / Backspace
        return buttons;
    }

    private static byte ReadAnalogStick(bool negative, bool positive)
    {
        if (negative && !positive) return 0;
        if (positive && !negative) return 255;
        return 128;
    }

    private static byte MergeAxis(byte controller, byte keyboard)
    {
        const int Deadzone = 10;
        return Math.Abs(controller - 128) > Deadzone ? controller : keyboard;
    }

    /// <summary>
    /// Re-arm Cross inject when the title screen is ready. Early pulses from
    /// pad-open often finish before <c>title_controller_ship</c> loads.
    /// </summary>
    /// <summary>True after title_controller_ship re-armed pad inject.</summary>
    public static bool IsTitleInjectArmed => Volatile.Read(ref _titleInjectArmed) != 0;

    public static void ArmCrossInjectAfterTitle()
    {
        if (!InjectCross)
        {
            // Still mark title ready for other subsystems (NpWebApi soft context).
            Interlocked.Exchange(ref _titleInjectArmed, 1);
            return;
        }

        _injectEpochTicks = Stopwatch.GetTimestamp();
        _injectDelayMs = InjectTitleDelayMs;
        Interlocked.Exchange(ref _crossDeliveredLogged, 0);
        Interlocked.Exchange(ref _optionsDeliveredLogged, 0);
        if (Interlocked.Exchange(ref _titleInjectArmed, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] pad.inject_cross armed after title delay_ms={InjectTitleDelayMs} " +
                $"pulses={InjectPulses} options_pulses={InjectOptionsPulses} options_hold_ms={InjectOptionsHoldMs}");
        }
    }

    private static uint ResolveInjectedButtons(long nowTicks)
    {
        if (!InjectCross)
        {
            return 0;
        }

        var epoch = _injectEpochTicks != 0 ? _injectEpochTicks : _padOpenedAtTicks;
        if (epoch == 0)
        {
            return 0;
        }

        var delayMs = _injectDelayMs > 0 ? _injectDelayMs : InjectAfterMs;
        var elapsedMs = (nowTicks - epoch) * 1000L / Stopwatch.Frequency;
        if (elapsedMs < delayMs)
        {
            return 0;
        }

        var sinceFirst = elapsedMs - delayMs;

        // Pre-title pulses often land on ps_logo / pause UI load and steal
        // focus before title_controller_ship. Only inject after title re-arm.
        if (_titleInjectArmed == 0)
        {
            return 0;
        }

        // Title: PadRead often starts late and misses short Cross pulses.
        // Alternate long Cross / Options holds so the first sample still hits Cross.
        var titleCycle = InjectOptionsHoldMs + InjectGapMs;
        if (titleCycle <= 0)
        {
            return 0;
        }

        var totalPulses = InjectPulses + InjectOptionsPulses;
        if (totalPulses <= 0)
        {
            return 0;
        }

        var pulse = (int)(sinceFirst / titleCycle);
        if (pulse < 0 || pulse >= totalPulses)
        {
            return 0;
        }

        var offset = (int)(sinceFirst % titleCycle);
        if (offset >= InjectOptionsHoldMs)
        {
            return 0;
        }

        // Cross first (InjectPulses), then Options (InjectOptionsPulses).
        return pulse < InjectPulses ? OrbisPadButton.Cross : OrbisPadButton.Options;
    }

    private static void TracePadWrite(string api, HostInputSample sample)
    {
        var count = Interlocked.Increment(ref _padWriteCount);
        var buttons = sample.State.Buttons;
        var cross = (buttons & OrbisPadButton.Cross) != 0;
        var options = (buttons & OrbisPadButton.Options) != 0;
        if (cross && Interlocked.Exchange(ref _crossDeliveredLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] pad.cross_delivered api={api} buttons=0x{buttons:X} " +
                $"focused={(sample.Focused ? 1 : 0)} kb={(sample.KeyboardArmed ? 1 : 0)} " +
                $"inject=0x{sample.Injected:X}");
        }

        if (options && Interlocked.Exchange(ref _optionsDeliveredLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] pad.options_delivered api={api} buttons=0x{buttons:X} " +
                $"focused={(sample.Focused ? 1 : 0)} kb={(sample.KeyboardArmed ? 1 : 0)} " +
                $"inject=0x{sample.Injected:X}");
        }

        if (!LogPad)
        {
            return;
        }

        var changed = buttons != _lastLoggedButtons;
        _lastLoggedButtons = buttons;
        if (count > 8 && count % 5000 != 0 && !changed && !cross && !options)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pad.read api={api} n={count} buttons=0x{buttons:X} cross={(cross ? 1 : 0)} " +
            $"options={(options ? 1 : 0)} focused={(sample.Focused ? 1 : 0)} kb={(sample.KeyboardArmed ? 1 : 0)} " +
            $"inject=0x{sample.Injected:X}");
    }

    private static int ReadPositiveEnvMs(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private static int ReadNonNegativeEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
    }
}
