// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// POSIX unnamed semaphore HLE (<c>sem_init</c> / <c>sem_wait</c> / <c>sem_post</c>).
/// Distinct from <see cref="KernelSemaphoreCompatExports"/> (handle-based sceKernel*Sema).
/// </summary>
public static class KernelPosixSemaphoreCompatExports
{
    // FreeBSD / Orbis SEM_VALUE_MAX.
    private const int SemValueMax = 32767;
    private const int Einval = 22;
    private const int Efault = 14;
    private const int Eagain = 35;
    private const int Ebusy = 16;
    private const int Etimedout = 60;
    private const int Eoverflow = 84;

    private static readonly ConcurrentDictionary<ulong, PosixSemaphoreState> _semaphores = new();
    private static readonly ConcurrentDictionary<ulong, string> _wakeKeys = new();
    private static readonly bool _traceSemaphores =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_POSIX_SEM"), "1", StringComparison.Ordinal);

    [ThreadStatic]
    private static int _semPollBackoffCount;

    private sealed class PosixSemaphoreState
    {
        public object Gate { get; } = new();
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public bool Destroyed { get; set; }
        public bool Initialized { get; set; }
    }

    private sealed class PosixSemaphoreWaiter
    {
        public bool Timed { get; init; }
        public int? Result { get; set; }
    }

    private static string GetWakeKey(ulong semAddress) =>
        _wakeKeys.GetOrAdd(semAddress, static address => string.Create(
            28,
            address,
            static (destination, value) =>
            {
                "posix_sem:0x".AsSpan().CopyTo(destination);
                _ = value.TryFormat(destination[12..], out _, "X16");
            }));

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemInit(CpuContext ctx)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        var pshared = unchecked((int)ctx[CpuRegister.Rsi]);
        var value = unchecked((uint)ctx[CpuRegister.Rdx]);

        if (semAddress == 0 || pshared < 0 || pshared > 1 || value > SemValueMax)
        {
            return PosixFailure(ctx, Einval);
        }

        var state = _semaphores.GetOrAdd(semAddress, static _ => new PosixSemaphoreState());
        lock (state.Gate)
        {
            if (state.Destroyed)
            {
                return PosixFailure(ctx, Einval);
            }

            if (state.Initialized && state.WaitingThreads != 0)
            {
                return PosixFailure(ctx, Ebusy);
            }

            state.Count = unchecked((int)value);
            state.Initialized = true;
            state.Destroyed = false;
        }

        TraceSem($"init sem=0x{semAddress:X16} pshared={pshared} value={value}");
        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemWait(CpuContext ctx) => SemWaitCore(ctx, timed: false, timeoutAddress: 0);

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemTimedWait(CpuContext ctx) => SemWaitCore(ctx, timed: true, timeoutAddress: ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemTryWait(CpuContext ctx)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        if (semAddress == 0)
        {
            return PosixFailure(ctx, Einval);
        }

        if (!TryGetOrCreateState(semAddress, out var state))
        {
            return PosixFailure(ctx, Einval);
        }

        lock (state.Gate)
        {
            if (state.Destroyed || !state.Initialized)
            {
                return PosixFailure(ctx, Einval);
            }

            if (state.Count <= 0)
            {
                TraceSem($"trywait-busy sem=0x{semAddress:X16} count={state.Count}");
                return PosixFailure(ctx, Eagain);
            }

            state.Count--;
            TraceSem($"trywait sem=0x{semAddress:X16} count={state.Count}");
            return PosixSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemPost(CpuContext ctx)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        if (semAddress == 0)
        {
            return PosixFailure(ctx, Einval);
        }

        if (!TryGetOrCreateState(semAddress, out var state))
        {
            return PosixFailure(ctx, Einval);
        }

        lock (state.Gate)
        {
            if (state.Destroyed || !state.Initialized)
            {
                return PosixFailure(ctx, Einval);
            }

            if (state.Count >= SemValueMax)
            {
                return PosixFailure(ctx, Eoverflow);
            }

            state.Count++;
            TraceSem($"post sem=0x{semAddress:X16} count={state.Count} waiters={state.WaitingThreads}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetWakeKey(semAddress), maxCount: 1);
        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SemDestroy(CpuContext ctx)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        if (semAddress == 0)
        {
            return PosixFailure(ctx, Einval);
        }

        if (!_semaphores.TryGetValue(semAddress, out var state))
        {
            return PosixFailure(ctx, Einval);
        }

        lock (state.Gate)
        {
            if (!state.Initialized || state.Destroyed)
            {
                return PosixFailure(ctx, Einval);
            }

            if (state.WaitingThreads != 0)
            {
                return PosixFailure(ctx, Ebusy);
            }

            state.Destroyed = true;
            state.Initialized = false;
            state.Count = 0;
        }

        _ = _semaphores.TryRemove(semAddress, out _);
        TraceSem($"destroy sem=0x{semAddress:X16}");
        return PosixSuccess(ctx);
    }

    private static int SemWaitCore(CpuContext ctx, bool timed, ulong timeoutAddress)
    {
        var semAddress = ctx[CpuRegister.Rdi];
        if (semAddress == 0)
        {
            return PosixFailure(ctx, Einval);
        }

        if (!TryGetOrCreateState(semAddress, out var state))
        {
            return PosixFailure(ctx, Einval);
        }

        long deadlineTimestamp = 0;
        if (timed)
        {
            if (timeoutAddress == 0)
            {
                return PosixFailure(ctx, Einval);
            }

            if (!TryReadAbstimeDeadline(ctx, timeoutAddress, out deadlineTimestamp, out var errno))
            {
                return PosixFailure(ctx, errno);
            }
        }

        lock (state.Gate)
        {
            if (state.Destroyed || !state.Initialized)
            {
                return PosixFailure(ctx, Einval);
            }

            if (state.Count > 0)
            {
                state.Count--;
                TraceSem($"wait sem=0x{semAddress:X16} count={state.Count}");
                return PosixSuccess(ctx);
            }

            var waiter = new PosixSemaphoreWaiter { Timed = timed };
            if (GuestThreadExecution.RequestCurrentThreadBlock(
                    ctx,
                    timed ? "sem_timedwait" : "sem_wait",
                    GetWakeKey(semAddress),
                    resumeHandler: () => CompleteBlockedSemWait(ctx, state, waiter),
                    wakeHandler: () => TryConsumeBlockedSemWait(state, waiter),
                    blockDeadlineTimestamp: timed ? deadlineTimestamp : 0))
            {
                state.WaitingThreads++;
                TraceSem(
                    timed
                        ? $"wait-block-timed sem=0x{semAddress:X16} waiters={state.WaitingThreads}"
                        : $"wait-block sem=0x{semAddress:X16} waiters={state.WaitingThreads}");
                return PosixSuccess(ctx);
            }
        }

        // Host-owned threads cannot park in the guest scheduler; spin with Pump
        // until a post arrives (or the absolute deadline passes).
        return HostFallbackWait(ctx, semAddress, state, timed, deadlineTimestamp);
    }

    private static int HostFallbackWait(
        CpuContext ctx,
        ulong semAddress,
        PosixSemaphoreState state,
        bool timed,
        long deadlineTimestamp)
    {
        while (true)
        {
            lock (state.Gate)
            {
                if (state.Destroyed || !state.Initialized)
                {
                    return PosixFailure(ctx, Einval);
                }

                if (state.Count > 0)
                {
                    state.Count--;
                    TraceSem($"wait-host sem=0x{semAddress:X16} count={state.Count}");
                    return PosixSuccess(ctx);
                }
            }

            if (timed && Stopwatch.GetTimestamp() >= deadlineTimestamp)
            {
                TraceSem($"wait-timeout sem=0x{semAddress:X16}");
                return PosixFailure(ctx, Etimedout);
            }

            GuestThreadExecution.Scheduler?.Pump(ctx, timed ? "sem_timedwait" : "sem_wait");
            if ((++_semPollBackoffCount & 255) == 0)
            {
                Thread.Sleep(0);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private static bool TryConsumeBlockedSemWait(PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        lock (state.Gate)
        {
            return TryConsumeBlockedSemWaitLocked(state, waiter);
        }
    }

    private static bool TryConsumeBlockedSemWaitLocked(PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        if (waiter.Result is not null)
        {
            return true;
        }

        if (state.Destroyed || !state.Initialized)
        {
            waiter.Result = -Einval;
            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            return true;
        }

        if (state.Count > 0)
        {
            state.Count--;
            waiter.Result = 0;
            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            TraceSem($"wake-consume count={state.Count} waiters={state.WaitingThreads}");
            return true;
        }

        return false;
    }

    private static int CompleteBlockedSemWait(CpuContext ctx, PosixSemaphoreState state, PosixSemaphoreWaiter waiter)
    {
        lock (state.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedSemWaitLocked(state, waiter))
            {
                if (waiter.Timed)
                {
                    waiter.Result = -Etimedout;
                    state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                    TraceSem($"wake-timeout count={state.Count} waiters={state.WaitingThreads}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[LOADER][GAP] posix_sem.resume-no-outcome count={state.Count} waiters={state.WaitingThreads}");
                    waiter.Result = -Eagain;
                    state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                }
            }

            var result = waiter.Result!.Value;
            if (result == 0)
            {
                return PosixSuccess(ctx);
            }

            return PosixFailure(ctx, -result);
        }
    }

    private static bool TryGetOrCreateState(ulong semAddress, out PosixSemaphoreState state)
    {
        // Lazy create covers the gap where an earlier unresolved sem_init left a
        // zeroed guest object that the engine still waits/posts on.
        state = _semaphores.GetOrAdd(semAddress, static _ => new PosixSemaphoreState
        {
            Initialized = true,
            Count = 0,
        });
        return true;
    }

    private static bool TryReadAbstimeDeadline(CpuContext ctx, ulong timeoutAddress, out long deadlineTimestamp, out int errno)
    {
        deadlineTimestamp = 0;
        errno = 0;
        Span<byte> buffer = stackalloc byte[16];
        if (!ctx.Memory.TryRead(timeoutAddress, buffer))
        {
            errno = Efault;
            return false;
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var nanoseconds = BinaryPrimitives.ReadInt64LittleEndian(buffer[sizeof(long)..]);
        if (seconds < 0 || nanoseconds < 0 || nanoseconds >= 1_000_000_000L)
        {
            errno = Einval;
            return false;
        }

        if (!KernelRuntimeCompatExports.ResolveClockTime(KernelRuntimeCompatExports.ClockRealtime, out var nowSec, out var nowNsec))
        {
            errno = Einval;
            return false;
        }

        var deltaSec = seconds - nowSec;
        var deltaNsec = nanoseconds - nowNsec;
        if (deltaNsec < 0)
        {
            deltaSec--;
            deltaNsec += 1_000_000_000L;
        }

        if (deltaSec < 0 || (deltaSec == 0 && deltaNsec <= 0))
        {
            deadlineTimestamp = Stopwatch.GetTimestamp();
            return true;
        }

        var delay = TimeSpan.FromSeconds(deltaSec) + TimeSpan.FromTicks(Math.Max(deltaNsec / 100L, 1L));
        deadlineTimestamp = GuestThreadExecution.ComputeDeadlineTimestamp(delay);
        return true;
    }

    private static int PosixSuccess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int PosixFailure(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
        return -1;
    }

    private static void TraceSem(string message)
    {
        if (!_traceSemaphores)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] posix_sem.{message}");
    }
}
