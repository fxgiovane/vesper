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
    public double Percent => TotalOperationsTarget > 0 ? (double)OperationsCompleted / TotalOperationsTarget * 100.0 : 0.0;
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
        int throttleMs = 15)
    {
        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;
        long maxLba = totalSizeBytes / sectorSize;
        if (maxLba <= 1) maxLba = 1000;

        uint readSize = sectorSize;
        if (readSize < 4096 && 4096 % sectorSize == 0) readSize = 4096;

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)readSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException("Falha ao alocar buffer para Seek Test.");

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

            if (hDevice.IsInvalid)
            {
                hDevice = CreateFileW(
                    devicePath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            using (hDevice)
            {
                if (hDevice.IsInvalid)
                    throw new IOException($"Nao foi possivel abrir {devicePath} para Seek Test. Erro: {Marshal.GetLastWin32Error()}");

                var progress = new SeekTestProgress
                {
                    TotalOperationsTarget = targetOperations
                };

            var swTotal = Stopwatch.StartNew();
            double cumulativeSeekMs = 0.0;

            for (long op = 0; op < targetOperations; op++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                long randomLba = Random.Shared.NextInt64(0, maxLba - (readSize / sectorSize));
                long byteOffset = randomLba * sectorSize;

                bool seekOk = SetFilePointerEx(hDevice, byteOffset, out _, FILE_BEGIN);

                long startTimestamp = Stopwatch.GetTimestamp();
                bool readOk = ReadFile(hDevice, buffer, readSize, out uint bytesRead, IntPtr.Zero);
                long endTimestamp = Stopwatch.GetTimestamp();

                if ((!seekOk || !readOk || bytesRead == 0) && op == 0)
                {
                    throw new IOException($"Falha na operacao de seek/read no dispositivo {devicePath}. Erro Win32: {Marshal.GetLastWin32Error()}");
                }

                double latencyMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
                cumulativeSeekMs += latencyMs;

                progress.OperationsCompleted = op + 1;
                progress.CurrentSeekMs = latencyMs;
                if (latencyMs < progress.MinSeekMs) progress.MinSeekMs = latencyMs;
                if (latencyMs > progress.MaxSeekMs) progress.MaxSeekMs = latencyMs;
                progress.AvgSeekMs = cumulativeSeekMs / progress.OperationsCompleted;

                progress.Elapsed = swTotal.Elapsed;
                double cumulativeSeekSec = cumulativeSeekMs / 1000.0;
                progress.CurrentIops = cumulativeSeekSec > 0 ? progress.OperationsCompleted / cumulativeSeekSec : 0.0;

                if (latencyMs < 5.0) progress.CountUnder5ms++;
                else if (latencyMs < 10.0) progress.CountUnder10ms++;
                else if (latencyMs < 20.0) progress.CountUnder20ms++;
                else progress.CountOver20ms++;

                var sample = new SeekTestSample
                {
                    OperationNumber = op + 1,
                    TargetLba = randomLba,
                    LatencyMs = latencyMs
                };

                onSampleMeasured(sample, progress);

                if (throttleMs > 0 && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(throttleMs);
                }
            }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                VirtualFree(buffer, 0, MEM_RELEASE);
        }
    }
}
