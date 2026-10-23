using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public sealed class SeekTestSample
{
    public long OperationNumber { get; init; }
    public long TargetLba { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class SeekTestProgress
{
    public long OperationsCompleted { get; set; }
    public long TotalOperationsTarget { get; set; }
    public long DurationTargetMs { get; set; }
    public long CurrentTargetLba { get; set; }
    public double Percent => DurationTargetMs > 0
        ? Math.Clamp(Elapsed.TotalMilliseconds / DurationTargetMs * 100.0, 0.0, 100.0)
        : (TotalOperationsTarget > 0 ? Math.Clamp((double)OperationsCompleted / TotalOperationsTarget * 100.0, 0.0, 100.0) : 0.0);
    public double CurrentSeekMs { get; set; }
    public double MinSeekMs { get; set; } = double.MaxValue;
    public double AvgSeekMs { get; set; }
    public double MaxSeekMs { get; set; }
    public double CurrentIops { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int CurrentTemperature { get; set; }
    public long CountUnder5ms { get; set; }
    public long CountUnder10ms { get; set; }
    public long CountUnder20ms { get; set; }
    public long CountOver20ms { get; set; }
}

public sealed class RandomSeekTestEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_BEGIN = 0;

    public static void RunTest(
        string devicePath,
        long totalSizeBytes,
        uint sectorSize,
        long targetOperations,
        CancellationToken cancellationToken,
        Action<SeekTestSample, SeekTestProgress> onSampleMeasured,
        int throttleMs = 0,
        int durationMs = 0,
        bool isSsdOrNvme = false,
        Func<int>? getTemperatureCallback = null)
    {
        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;

        int maxSafeTempCelsius = isSsdOrNvme ? 78 : 55;
        uint safeAlignment = Math.Max(4096u, sectorSize);
        uint readSize = safeAlignment;

        long maxLba = totalSizeBytes / sectorSize;
        if (maxLba <= 1) maxLba = 1000;

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)readSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException("Failed to allocate buffer for Seek Test.");

        try
        {
            SafeFileHandle hDevice = CreateFileW(
                devicePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            using (hDevice)
            {
                if (hDevice.IsInvalid)
                    throw new IOException($"Could not open {devicePath} for Seek Test. Error: {Marshal.GetLastWin32Error()}");

                if (targetOperations <= 0 && durationMs <= 0)
                {
                    long probeStart = Stopwatch.GetTimestamp();
                    long probeLimitTicks = (long)(0.150 * Stopwatch.Frequency);
                    long probeOps = 0;

                    while ((Stopwatch.GetTimestamp() - probeStart) < probeLimitTicks && !cancellationToken.IsCancellationRequested)
                    {
                        long rLba = Random.Shared.NextInt64(0, Math.Max(1, maxLba - (readSize / sectorSize)));
                        long bOff = (rLba * sectorSize / safeAlignment) * safeAlignment;
                        SetFilePointerEx(hDevice, bOff, out _, FILE_BEGIN);
                        if (ReadFile(hDevice, buffer, readSize, out uint bRead, IntPtr.Zero) && bRead > 0)
                        {
                            probeOps++;
                        }
                    }

                    double probeElapsedSec = Math.Max(0.001, (Stopwatch.GetTimestamp() - probeStart) / (double)Stopwatch.Frequency);
                    double probeIops = probeOps / probeElapsedSec;
                    targetOperations = Math.Clamp((long)(probeIops * 18.0), 1500, 300_000);
                }

                var progress = new SeekTestProgress
                {
                    TotalOperationsTarget = targetOperations,
                    DurationTargetMs = durationMs
                };

                var swTotal = Stopwatch.StartNew();
                double cumulativeSeekMs = 0.0;
                long currentLba = 0;
                long op = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (durationMs > 0 && swTotal.ElapsedMilliseconds >= durationMs)
                        break;
                    if (targetOperations > 0 && op >= targetOperations)
                        break;

                    op++;

                    if (op % 50 == 0 && getTemperatureCallback != null)
                    {
                        int curTemp = getTemperatureCallback();
                        if (curTemp > 0)
                        {
                            progress.CurrentTemperature = curTemp;
                            if (curTemp >= maxSafeTempCelsius)
                            {
                                throw new InvalidOperationException($"Thermal protection triggered: current temperature ({curTemp}°C) reached safety limit of {maxSafeTempCelsius}°C.");
                            }
                        }
                    }

                    long randomLba = Random.Shared.NextInt64(0, Math.Max(1, maxLba - (readSize / sectorSize)));

                    currentLba = randomLba;
                    progress.CurrentTargetLba = randomLba;

                    long byteOffset = (randomLba * sectorSize / safeAlignment) * safeAlignment;
                    bool seekOk = SetFilePointerEx(hDevice, byteOffset, out _, FILE_BEGIN);

                    long startTimestamp = Stopwatch.GetTimestamp();
                    bool readOk = ReadFile(hDevice, buffer, readSize, out uint bytesRead, IntPtr.Zero);
                    long endTimestamp = Stopwatch.GetTimestamp();

                    if ((!seekOk || !readOk || bytesRead == 0) && op == 1)
                    {
                        throw new IOException($"Seek/read operation failed on device {devicePath}. Win32 error: {Marshal.GetLastWin32Error()}");
                    }

                    double latencyMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
                    cumulativeSeekMs += latencyMs;

                    progress.OperationsCompleted = op;
                    progress.CurrentSeekMs = latencyMs;
                    if (latencyMs < progress.MinSeekMs) progress.MinSeekMs = latencyMs;
                    if (latencyMs > progress.MaxSeekMs) progress.MaxSeekMs = latencyMs;
                    progress.AvgSeekMs = cumulativeSeekMs / op;

                    progress.Elapsed = swTotal.Elapsed;
                    double elapsedSec = Math.Max(0.0001, swTotal.Elapsed.TotalSeconds);
                    progress.CurrentIops = op / elapsedSec;
                    if (durationMs > 0) progress.TotalOperationsTarget = Math.Max(op, (long)(progress.CurrentIops * (durationMs / 1000.0)));

                    if (latencyMs < 5.0) progress.CountUnder5ms++;
                    else if (latencyMs < 10.0) progress.CountUnder10ms++;
                    else if (latencyMs < 20.0) progress.CountUnder20ms++;
                    else progress.CountOver20ms++;

                    var sample = new SeekTestSample
                    {
                        OperationNumber = op,
                        TargetLba = randomLba,
                        LatencyMs = latencyMs
                    };

                    onSampleMeasured(sample, progress);

                    if (throttleMs > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        Thread.Sleep(throttleMs);
                    }
                }

                progress.TotalOperationsTarget = progress.OperationsCompleted;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                VirtualFree(buffer, 0, MEM_RELEASE);
        }
    }
}
