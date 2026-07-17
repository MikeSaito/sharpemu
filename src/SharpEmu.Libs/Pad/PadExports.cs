// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    private const int PrimaryUserId = 1000;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;
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
    private static bool _cachedDualSense;

    [ThreadStatic]
    private static bool _cachedXInput;

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

    // Headless / agent runs often leave the present window unfocused; keyboard
    // Cross never reaches scePadRead unless this is set (or a controller is used).
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
    private static readonly int InjectHoldMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_HOLD_MS", 400);
    private static readonly int InjectGapMs = ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_GAP_MS", 2_000);
    private static readonly int InjectPulses = Math.Clamp(ReadPositiveEnvMs("SHARPEMU_PAD_INJECT_PULSES", 3), 1, 32);

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx)
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

        if (userId != PrimaryUserId || type != StandardPortType || index != 0 || parameterAddress != 0)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        if (_padOpenedAtTicks == 0)
        {
            _padOpenedAtTicks = Stopwatch.GetTimestamp();
        }

        if (Interlocked.Exchange(ref _controlsAnnouncementLogged, 1) == 0)
        {
            Console.Error.WriteLine(DualSenseReader.TryGetState(out _)
                ? "[LOADER][INFO] Controls: DualSense connected (keyboard fallback also active)."
                : XInputReader.TryGetState(out _)
                    ? "[LOADER][INFO] Controls: Xbox controller connected (keyboard fallback also active)."
                    : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
            if (AllowUnfocusedKeyboard)
            {
                Console.Error.WriteLine("[LOADER][INFO] pad.keyboard_unfocused=1 (SHARPEMU_PAD_ALLOW_UNFOCUSED)");
            }

            if (InjectCross)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] pad.inject_cross=1 after_ms={InjectAfterMs} hold_ms={InjectHoldMs} " +
                    $"gap_ms={InjectGapMs} pulses={InjectPulses}");
            }
        }

        return ctx.SetReturn(PrimaryPadHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return handle == PrimaryPadHandle
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
        var enabled = unchecked((int)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        TracePad(
            $"set_tilt_correction_state handle=0x{handle:X} enabled=0x{enabled:X} reserved=0x{reserved:X}");
        return ctx.SetReturn(0);
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
        if (handle != PrimaryPadHandle)
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
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WritePadData(ctx, dataAddress, "scePadReadState")
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
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WritePadData(ctx, dataAddress, "scePadRead")
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
        if (handle != PrimaryPadHandle)
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
        XInputReader.SetTriggerRumble(
            (triggerMask & 0x01) != 0 ? DecodeTriggerVibration(parameter[8..64]) : null,
            (triggerMask & 0x02) != 0 ? DecodeTriggerVibration(parameter[64..120]) : null);
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
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
        if (handle != PrimaryPadHandle)
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

        DualSenseReader.SetRumble(parameter[0], parameter[1]);
        XInputReader.SetRumble(parameter[0], parameter[1]);
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
        if (handle != PrimaryPadHandle)
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

        DualSenseReader.SetLightbar(color[0], color[1], color[2]);
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
        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        DualSenseReader.ResetLightbar();
        return ctx.SetReturn(0);
    }

    private static bool WritePadData(CpuContext ctx, ulong dataAddress, string api)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var sample = ReadHostInputSample();
        var input = sample.State;
        var buttons = input.Buttons;

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = input.LeftX;
        data[0x05] = input.LeftY;
        data[0x06] = input.RightX;
        data[0x07] = input.RightY;
        data[0x08] = input.L2;
        data[0x09] = input.R2;
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
        bool DualSense,
        bool XInput,
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
                _cachedDualSense,
                _cachedXInput,
                _cachedInjected);
        }

        var focused = IsEmulatorWindowFocused();
        var acceptsKeyboardInput = focused || AllowUnfocusedKeyboard;
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons() : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x41), IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x57), IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x4A), IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(IsKeyDown(0x49), IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && IsKeyDown(0x46) ? (byte)255 : (byte)0;
        var dualSense = false;
        var xInput = false;

        if (DualSenseReader.TryGetState(out var pad))
        {
            dualSense = true;
            buttons |= pad.Buttons;
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.L2);
            r2 = Math.Max(r2, pad.R2);
        }

        if (XInputReader.TryGetState(out var xpad))
        {
            xInput = true;
            buttons |= xpad.Buttons;
            leftX = MergeAxis(xpad.LeftX, leftX);
            leftY = MergeAxis(xpad.LeftY, leftY);
            rightX = MergeAxis(xpad.RightX, rightX);
            rightY = MergeAxis(xpad.RightY, rightY);
            l2 = Math.Max(l2, xpad.L2);
            r2 = Math.Max(r2, xpad.R2);
        }

        var injected = ResolveInjectedButtons(now);
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
        _cachedDualSense = dualSense;
        _cachedXInput = xInput;
        _cachedInjected = injected;
        _lastInputSampleTicks = now;
        return new HostInputSample(
            _cachedInputState,
            focused,
            acceptsKeyboardInput,
            dualSense,
            xInput,
            injected);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsKeyDown(int vk) =>
        (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsEmulatorWindowFocused()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static uint ReadKeyboardButtons()
    {
        uint buttons = 0;
        // D-pad
        if (IsKeyDown(0x25)) buttons |= 0x0080; // Left
        if (IsKeyDown(0x27)) buttons |= 0x0020; // Right
        if (IsKeyDown(0x26)) buttons |= 0x0010; // Up
        if (IsKeyDown(0x28)) buttons |= 0x0040; // Down
        // Face buttons
        if (IsKeyDown(0x5A) || IsKeyDown(0x0D)) buttons |= OrbisPadButton.Cross; // Z / Enter
        if (IsKeyDown(0x58) || IsKeyDown(0x1B)) buttons |= OrbisPadButton.Circle; // X / Escape
        if (IsKeyDown(0x43)) buttons |= OrbisPadButton.Square; // C
        if (IsKeyDown(0x56)) buttons |= OrbisPadButton.Triangle; // V
        // Shoulder buttons
        if (IsKeyDown(0x51)) buttons |= OrbisPadButton.L1; // Q
        if (IsKeyDown(0x45)) buttons |= OrbisPadButton.R1; // E
        if (IsKeyDown(0x52)) buttons |= OrbisPadButton.L2; // R (digital)
        if (IsKeyDown(0x46)) buttons |= OrbisPadButton.R2; // F (digital)
        // Options (Start)
        if (IsKeyDown(0x09) || IsKeyDown(0x08)) buttons |= OrbisPadButton.Options; // Tab / Backspace
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

    private static uint ResolveInjectedButtons(long nowTicks)
    {
        if (!InjectCross || _padOpenedAtTicks == 0)
        {
            return 0;
        }

        var elapsedMs = (nowTicks - _padOpenedAtTicks) * 1000L / Stopwatch.Frequency;
        if (elapsedMs < InjectAfterMs)
        {
            return 0;
        }

        var cycle = InjectHoldMs + InjectGapMs;
        if (cycle <= 0)
        {
            return 0;
        }

        var sinceFirst = elapsedMs - InjectAfterMs;
        var pulseIndex = (int)(sinceFirst / cycle);
        if (pulseIndex < 0 || pulseIndex >= InjectPulses)
        {
            return 0;
        }

        var offsetInCycle = (int)(sinceFirst % cycle);
        return offsetInCycle < InjectHoldMs ? OrbisPadButton.Cross : 0;
    }

    private static void TracePadWrite(string api, HostInputSample sample)
    {
        var count = Interlocked.Increment(ref _padWriteCount);
        var buttons = sample.State.Buttons;
        var cross = (buttons & OrbisPadButton.Cross) != 0;
        if (cross && Interlocked.Exchange(ref _crossDeliveredLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] pad.cross_delivered api={api} buttons=0x{buttons:X} " +
                $"focused={(sample.Focused ? 1 : 0)} kb={(sample.KeyboardArmed ? 1 : 0)} " +
                $"ds={(sample.DualSense ? 1 : 0)} xi={(sample.XInput ? 1 : 0)} inject=0x{sample.Injected:X}");
        }

        if (!LogPad)
        {
            return;
        }

        var changed = buttons != _lastLoggedButtons;
        _lastLoggedButtons = buttons;
        if (count > 8 && count % 5000 != 0 && !changed && !cross)
        {
            return;
        }

        TracePad(
            $"read api={api} n={count} buttons=0x{buttons:X} cross={(cross ? 1 : 0)} " +
            $"focused={(sample.Focused ? 1 : 0)} kb={(sample.KeyboardArmed ? 1 : 0)} " +
            $"ds={(sample.DualSense ? 1 : 0)} xi={(sample.XInput ? 1 : 0)} inject=0x{sample.Injected:X}");
    }

    private static int ReadPositiveEnvMs(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private static void TracePad(string message)
    {
        if (LogPad)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] pad.{message}");
        }
    }
}
