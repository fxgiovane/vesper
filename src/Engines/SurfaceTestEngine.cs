using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public enum SurfaceScanMode
{
    VerifyRead,
    DataRefresh,
    DestructiveErase
}

public enum SurfaceScanCoverage
{
    QuickDense,
    FullLinear
}

public enum SurfaceBlockStatus
{
    Unchecked,
    Excellent,
    Good,
    Slow,
    Lagging,
    Damaged,
    BadSector
}

public sealed class SurfaceBlockResult
{
    public long BlockIndex { get; init; }
    public long StartSector { get; init; }
    public long EndSector { get; init; }
    public double LatencyMs { get; init; }
    public double SpeedMBps { get; init; }
    public SurfaceBlockStatus Status { get; init; }
}

public sealed class SurfaceTestProgress
{
    public long TotalBlocks { get; set; }
    public long CurrentBlock { get; set; }
    public long TotalSectors { get; set; }
    public long BlockSectors { get; set; }
    public double BlockSizeMB { get; set; }

    public long GoodBlocks { get; set; }
    public long SlowBlocks { get; set; }
    public long DamagedBlocks { get; set; }
    public long BadBlocks { get; set; }
    public long MultiPhaseRecoveredBlocks { get; set; }

    public double CurrentLatencyMs { get; set; }
    public double CurrentSpeedMBps { get; set; }
    public double MinSpeedMBps { get; set; } = double.MaxValue;
    public double AvgSpeedMBps { get; set; }
    public double MaxSpeedMBps { get; set; }

    public long CurrentLba { get; set; }
    public long CurrentSectorStart { get; set; }
    public long CurrentSectorEnd { get; set; }
    public TimeSpan Elapsed { get; set; }

    public double Percent => TotalBlocks > 0 ? (double)CurrentBlock / TotalBlocks * 100.0 : 0.0;
    public TimeSpan EstimatedRemaining => Percent > 0 && Elapsed.TotalSeconds > 0
        ? TimeSpan.FromSeconds(Math.Max(0, (Elapsed.TotalSeconds / (Percent / 100.0)) - Elapsed.TotalSeconds))
        : TimeSpan.Zero;
    public double SurfaceHealthPercent => TotalBlocks > 0
        ? Math.Max(0.0, 100.0 - ((BadBlocks * 100.0) / TotalBlocks) - ((DamagedBlocks * 50.0) / TotalBlocks))
        : 100.0;
}

public static class SurfaceTestEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

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
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
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

    public static readonly ConcurrentDictionary<string, SurfaceBlockStatus[]> DiskSurfaceCache = new();

    public static SurfaceBlockStatus[] ProbeSurfaceSample(
        string devicePath,
        long totalSizeBytes,
        uint sectorSize,
        int blockCount,
        CancellationToken token,
        Action<int, SurfaceBlockStatus>? onBlockDone = null,
        int startBlock = 0)
    {
        var blocks = new SurfaceBlockStatus[blockCount];
        Array.Fill(blocks, SurfaceBlockStatus.Unchecked);

        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;

        uint safeAlignment = Math.Max(4096u, sectorSize);
        uint alignedReadBytes = ((1024 * 1024 + safeAlignment - 1) / safeAlignment) * safeAlignment;

        long stepBytes = totalSizeBytes / blockCount;
        stepBytes = ((stepBytes + safeAlignment - 1) / safeAlignment) * safeAlignment;

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)alignedReadBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            return blocks;

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
                return blocks;

            using (hDevice)
            {
                for (int b = Math.Clamp(startBlock, 0, blockCount); b < blockCount; b++)
                {
                    if (token.IsCancellationRequested) break;

                    long currentOffset = b * stepBytes;
                    if (currentOffset + alignedReadBytes > totalSizeBytes)
                        currentOffset = Math.Max(0, totalSizeBytes - alignedReadBytes);
                    currentOffset = (currentOffset / safeAlignment) * safeAlignment;

                    double maxChunkLatencyMs = 0;
                    bool hadError = false;

                    SetFilePointerEx(hDevice, currentOffset, out _, FILE_BEGIN);
                    long chunkStart = Stopwatch.GetTimestamp();
                    bool readOk = ReadFile(hDevice, buffer, alignedReadBytes, out uint bytesRead, IntPtr.Zero);
                    long chunkEnd = Stopwatch.GetTimestamp();

                    if (!readOk || bytesRead == 0)
                    {
                        int errCode = Marshal.GetLastWin32Error();
                        if (errCode == 1167 || errCode == 433 || errCode == 2 || errCode == 6 || errCode == 5)
                        {
                            return blocks;
                        }
                        hadError = true;
                    }
                    else
                    {
                        maxChunkLatencyMs = (chunkEnd - chunkStart) * 1000.0 / Stopwatch.Frequency;
                    }

                    if (token.IsCancellationRequested) break;

                    SurfaceBlockStatus st;
                    if (hadError)
                    {
                        st = SurfaceBlockStatus.BadSector;
                    }
                    else
                    {
                        if (maxChunkLatencyMs < 20.0)
                            st = SurfaceBlockStatus.Excellent;
                        else if (maxChunkLatencyMs < 60.0)
                            st = SurfaceBlockStatus.Good;
                        else if (maxChunkLatencyMs < 150.0)
                            st = SurfaceBlockStatus.Slow;
                        else if (maxChunkLatencyMs < 500.0)
                            st = SurfaceBlockStatus.Lagging;
                        else
                            st = SurfaceBlockStatus.Damaged;
                    }

                    blocks[b] = st;
                    onBlockDone?.Invoke(b, st);

                    Thread.Sleep(12);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                VirtualFree(buffer, 0, MEM_RELEASE);
        }

        return blocks;
    }

    public static void RunTest(
        string devicePath,
        long totalSizeBytes,
        uint sectorSize,
        int sampleGridSize,
        CancellationToken cancellationToken,
        Action<SurfaceBlockResult, SurfaceTestProgress> onBlockTested,
        Action<long>? onBlockStarting = null,
        int throttleMs = 0,
        SurfaceScanMode scanMode = SurfaceScanMode.VerifyRead,
        SurfaceScanCoverage coverage = SurfaceScanCoverage.QuickDense)
    {
        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;

        uint safeAlignment = Math.Max(4096u, sectorSize);
        uint alignedBlockBytes = ((4 * 1024 * 1024 + safeAlignment - 1) / safeAlignment) * safeAlignment;

        long totalBlocks;
        long stepBytes;

        if (coverage == SurfaceScanCoverage.FullLinear)
        {
            totalBlocks = Math.Max(1, totalSizeBytes / alignedBlockBytes);
            stepBytes = alignedBlockBytes;
        }
        else
        {
            totalBlocks = sampleGridSize > 0 ? sampleGridSize : 350;
            stepBytes = totalSizeBytes / totalBlocks;
            stepBytes = ((stepBytes + safeAlignment - 1) / safeAlignment) * safeAlignment;
            if (stepBytes < alignedBlockBytes) stepBytes = alignedBlockBytes;
        }

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)alignedBlockBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException("Falha ao alocar buffer alinhado na memoria fisica.");

        try
        {
            uint desiredAccess = scanMode == SurfaceScanMode.VerifyRead ? GENERIC_READ : (GENERIC_READ | GENERIC_WRITE);
            SafeFileHandle hDevice = CreateFileW(
                devicePath,
                desiredAccess,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_NO_BUFFERING | (scanMode != SurfaceScanMode.VerifyRead ? FILE_FLAG_WRITE_THROUGH : 0),
                IntPtr.Zero);

            if (hDevice.IsInvalid && desiredAccess != GENERIC_READ)
            {
                desiredAccess = GENERIC_READ;
                hDevice = CreateFileW(
                    devicePath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_NO_BUFFERING,
                    IntPtr.Zero);
            }

            using (hDevice)
            {
                if (hDevice.IsInvalid)
                    throw new IOException($"Nao foi possivel abrir {devicePath} para leitura direta. Erro Win32: {Marshal.GetLastWin32Error()}");

                long totalSectors = totalSizeBytes / sectorSize;
                long blockSectors = alignedBlockBytes / sectorSize;

                var progress = new SurfaceTestProgress
                {
                    TotalBlocks = totalBlocks,
                    TotalSectors = totalSectors,
                    BlockSectors = blockSectors,
                    BlockSizeMB = alignedBlockBytes / 1_000_000.0
                };

                var swTotal = Stopwatch.StartNew();
                long totalBytesRead = 0;
                double cumulativeReadSeconds = 0.0;
                int consecutiveDynaFails = 0;

                int chunksPerZone = 1;

                for (long b = 0; b < totalBlocks; b++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    long baseByteOffset = (coverage == SurfaceScanCoverage.FullLinear)
                        ? b * alignedBlockBytes
                        : b * stepBytes;

                    if (baseByteOffset + alignedBlockBytes > totalSizeBytes)
                        baseByteOffset = Math.Max(0, totalSizeBytes - alignedBlockBytes);

                    baseByteOffset = (baseByteOffset / safeAlignment) * safeAlignment;
                    long startSector = baseByteOffset / sectorSize;
                    long endSector = (baseByteOffset + (alignedBlockBytes * chunksPerZone)) / sectorSize - 1;

                    progress.CurrentLba = startSector;
                    progress.CurrentSectorStart = startSector;
                    progress.CurrentSectorEnd = endSector;

                    onBlockStarting?.Invoke(b);

                    long zoneBytesRead = 0;
                    double zoneDurationSec = 0.0;
                    double maxChunkLatMs = 0.0;
                    bool zoneFailed = false;
                    int win32Err = 0;

                    for (int c = 0; c < chunksPerZone; c++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        long currentChunkOffset = baseByteOffset + (c * alignedBlockBytes);
                        if (currentChunkOffset + alignedBlockBytes > totalSizeBytes)
                            break;

                        SetFilePointerEx(hDevice, currentChunkOffset, out _, FILE_BEGIN);

                        long startTimestamp = Stopwatch.GetTimestamp();
                        bool readOk = ReadFile(hDevice, buffer, alignedBlockBytes, out uint bytesRead, IntPtr.Zero);
                        long endTimestamp = Stopwatch.GetTimestamp();

                        if (!readOk || bytesRead == 0)
                        {
                            win32Err = Marshal.GetLastWin32Error();
                            if (win32Err == 1167 || win32Err == 433 || win32Err == 2 || win32Err == 6 || win32Err == 5)
                            {
                                cancellationToken = new CancellationToken(true);
                                break;
                            }
                            zoneFailed = true;
                            break;
                        }

                        double chunkSec = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                        double chunkLatMs = chunkSec * 1000.0;
                        if (chunkLatMs > maxChunkLatMs) maxChunkLatMs = chunkLatMs;

                        zoneBytesRead += bytesRead;
                        zoneDurationSec += chunkSec;

                        if (scanMode == SurfaceScanMode.DataRefresh && desiredAccess == (GENERIC_READ | GENERIC_WRITE))
                        {
                            SetFilePointerEx(hDevice, currentChunkOffset, out _, FILE_BEGIN);
                            WriteFile(hDevice, buffer, bytesRead, out _, IntPtr.Zero);
                            SetFilePointerEx(hDevice, currentChunkOffset, out _, FILE_BEGIN);
                            ReadFile(hDevice, buffer, alignedBlockBytes, out _, IntPtr.Zero);
                        }
                        else if (scanMode == SurfaceScanMode.DestructiveErase && desiredAccess == (GENERIC_READ | GENERIC_WRITE))
                        {
                            byte[] zeros = new byte[alignedBlockBytes];
                            Marshal.Copy(zeros, 0, buffer, (int)alignedBlockBytes);
                            SetFilePointerEx(hDevice, currentChunkOffset, out _, FILE_BEGIN);
                            WriteFile(hDevice, buffer, alignedBlockBytes, out _, IntPtr.Zero);
                        }
                    }

                    if (cancellationToken.IsCancellationRequested) break;

                    SurfaceBlockStatus status;
                    double latencyMs = maxChunkLatMs;
                    double speedMBps = zoneDurationSec > 0 ? (zoneBytesRead / 1_000_000.0) / zoneDurationSec : 0.0;

                    if (zoneFailed)
                    {
                        consecutiveDynaFails++;
                        if (consecutiveDynaFails >= 8) break;

                        var badLbas = new List<long>();
                        IsolateFailingSectors(hDevice, baseByteOffset, alignedBlockBytes, safeAlignment, buffer, badLbas);

                        status = SurfaceBlockStatus.BadSector;
                        progress.BadBlocks++;
                    }
                    else
                    {
                        consecutiveDynaFails = 0;
                        totalBytesRead += zoneBytesRead;
                        cumulativeReadSeconds += zoneDurationSec;

                        if (latencyMs < 20.0)
                        {
                            status = SurfaceBlockStatus.Excellent;
                            progress.GoodBlocks++;
                        }
                        else if (latencyMs < 60.0)
                        {
                            status = SurfaceBlockStatus.Good;
                            progress.GoodBlocks++;
                        }
                        else if (latencyMs < 150.0)
                        {
                            status = SurfaceBlockStatus.Slow;
                            progress.SlowBlocks++;
                        }
                        else if (latencyMs < 500.0)
                        {
                            status = SurfaceBlockStatus.Lagging;
                            progress.SlowBlocks++;
                        }
                        else
                        {
                            status = SurfaceBlockStatus.Damaged;
                            progress.BadBlocks++;
                        }
                    }

                    progress.CurrentBlock = b + 1;
                    progress.CurrentLatencyMs = latencyMs;
                    progress.CurrentSpeedMBps = speedMBps;
                    if (speedMBps > 0 && speedMBps < progress.MinSpeedMBps) progress.MinSpeedMBps = speedMBps;
                    if (speedMBps > progress.MaxSpeedMBps) progress.MaxSpeedMBps = speedMBps;

                    progress.Elapsed = swTotal.Elapsed;
                    progress.AvgSpeedMBps = cumulativeReadSeconds > 0 ? (totalBytesRead / 1_000_000.0) / cumulativeReadSeconds : 0.0;

                    long mappedBlockIndex = (coverage == SurfaceScanCoverage.FullLinear && sampleGridSize > 0)
                        ? Math.Clamp((b * sampleGridSize) / totalBlocks, 0, sampleGridSize - 1)
                        : b;

                    var result = new SurfaceBlockResult
                    {
                        BlockIndex = mappedBlockIndex,
                        StartSector = startSector,
                        EndSector = endSector,
                        LatencyMs = latencyMs,
                        SpeedMBps = speedMBps,
                        Status = status
                    };

                    onBlockTested(result, progress);

                    if (DiskSurfaceCache.TryGetValue(devicePath, out var cachedArr) && mappedBlockIndex < cachedArr.Length)
                    {
                        cachedArr[mappedBlockIndex] = status;
                    }

                    if (throttleMs > 0 && !cancellationToken.IsCancellationRequested)
                        Thread.Sleep(throttleMs);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                VirtualFree(buffer, 0, MEM_RELEASE);
        }
    }

    private static void IsolateFailingSectors(
        SafeFileHandle hDevice,
        long blockByteOffset,
        uint blockBytes,
        uint safeAlignment,
        IntPtr sectorBuf,
        List<long> badLbas)
    {
        uint sectorsInBlock = blockBytes / safeAlignment;
        for (uint s = 0; s < sectorsInBlock; s++)
        {
            long secOffset = blockByteOffset + (s * safeAlignment);
            SetFilePointerEx(hDevice, secOffset, out _, FILE_BEGIN);
            bool ok = ReadFile(hDevice, sectorBuf, safeAlignment, out uint bRead, IntPtr.Zero);
            if (!ok || bRead < safeAlignment)
            {
                badLbas.Add(secOffset / safeAlignment);
            }
        }
    }
}
