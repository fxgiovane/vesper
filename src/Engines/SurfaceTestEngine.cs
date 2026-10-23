using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    public long BytesVerified { get; set; }
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
        ? Math.Min(BadBlocks > 0 ? 60.0 : 100.0, Math.Max(0.0, 100.0 - ((BadBlocks * 100.0) / TotalBlocks) - ((DamagedBlocks * 50.0) / TotalBlocks)))
        : 100.0;
}

internal sealed class LatencyBaseline
{
    private double _sum;
    private int _count;

    public SurfaceBlockStatus Classify(double ms)
    {
        double excellent = 20.0, good = 60.0, slow = 150.0;
        if (_count >= 8)
        {
            double avg = _sum / _count;
            excellent = Math.Max(avg * 2.0, 2.0);
            good = Math.Max(avg * 4.0, 6.0);
            slow = Math.Max(avg * 10.0, 20.0);
        }

        SurfaceBlockStatus st;
        if (ms < excellent) st = SurfaceBlockStatus.Excellent;
        else if (ms < good) st = SurfaceBlockStatus.Good;
        else if (ms < slow) st = SurfaceBlockStatus.Slow;
        else st = SurfaceBlockStatus.Lagging;

        if (_count < 8 || ms < slow)
        {
            _sum += ms;
            _count++;
        }
        return st;
    }
}
 static class SurfaceTestEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    public static void BeginHighResTimer() { try { timeBeginPeriod(1); } catch { } }

    public static void EndHighResTimer() { try { timeEndPeriod(1); } catch { } }
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

    private static void WarmUpDriveZones(SafeFileHandle hDevice, long totalSizeBytes, uint safeAlignment, IntPtr buffer)
    {
        for (int z = 0; z < 4; z++)
        {
            long probeOffset = (totalSizeBytes * z) / 4;
            probeOffset = (probeOffset / safeAlignment) * safeAlignment;
            if (probeOffset + safeAlignment <= totalSizeBytes)
            {
                SetFilePointerEx(hDevice, probeOffset, out _, FILE_BEGIN);
                ReadFile(hDevice, buffer, safeAlignment, out _, IntPtr.Zero);
            }
        }
        SetFilePointerEx(hDevice, 0, out _, FILE_BEGIN);
    }

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
                if (startBlock == 0)
                {
                    WarmUpDriveZones(hDevice, totalSizeBytes, safeAlignment, buffer);
                }

                var latBaseline = new LatencyBaseline();

                for (int b = Math.Clamp(startBlock, 0, blockCount); b < blockCount; b++)
                {
                    if (token.IsCancellationRequested) break;

                    long currentOffset = b * stepBytes;
                    if (currentOffset + alignedReadBytes > totalSizeBytes)
                        currentOffset = Math.Max(0, totalSizeBytes - alignedReadBytes);
                    currentOffset = (currentOffset / safeAlignment) * safeAlignment;

                    double maxChunkLatencyMs = 0;
                    bool hadError = false;
                    bool mediaErr = false;

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

                        if (RecoverFailedRead(hDevice, buffer, alignedReadBytes, currentOffset, token, out long recTicks, out _, out int recErr))
                        {
                            maxChunkLatencyMs = recTicks * 1000.0 / Stopwatch.Frequency;
                        }
                        else
                        {
                            hadError = true;
                            mediaErr = IsMediaError(errCode) || IsMediaError(recErr);
                        }
                    }
                    else
                    {
                        maxChunkLatencyMs = (chunkEnd - chunkStart) * 1000.0 / Stopwatch.Frequency;

                        if (maxChunkLatencyMs >= 50.0 && !token.IsCancellationRequested)
                        {
                            maxChunkLatencyMs = ConfirmLatencyMs(hDevice, buffer, alignedReadBytes, currentOffset, maxChunkLatencyMs, 1.0, token);
                        }
                    }

                    if (token.IsCancellationRequested) break;

                    SurfaceBlockStatus st;
                    if (hadError)
                    {
                        st = mediaErr ? SurfaceBlockStatus.BadSector : SurfaceBlockStatus.Lagging;
                    }
                    else
                    {
                        st = latBaseline.Classify(maxChunkLatencyMs);
                    }

                    blocks[b] = st;
                    onBlockDone?.Invoke(b, st);

                    if (onBlockDone != null)
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
        uint rawBlockBytes = coverage == SurfaceScanCoverage.FullLinear ? (4u * 1024u * 1024u) : (1024u * 1024u);
        uint alignedBlockBytes = ((rawBlockBytes + safeAlignment - 1) / safeAlignment) * safeAlignment;

        long totalBlocks;
        long stepBytes;

        if (coverage == SurfaceScanCoverage.FullLinear)
        {
            totalBlocks = Math.Max(1, totalSizeBytes / alignedBlockBytes);
            stepBytes = alignedBlockBytes;
        }
        else
        {
            totalBlocks = sampleGridSize > 0 ? sampleGridSize : 300;
            stepBytes = totalSizeBytes / totalBlocks;
            stepBytes = ((stepBytes + safeAlignment - 1) / safeAlignment) * safeAlignment;
            if (stepBytes < alignedBlockBytes) stepBytes = alignedBlockBytes;
        }

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)alignedBlockBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException("Failed to allocate physical aligned buffer.");

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
                    throw new IOException($"Could not open {devicePath} for direct unbuffered read. Win32 Error: {Marshal.GetLastWin32Error()}");

                long totalSectors = totalSizeBytes / sectorSize;
                long blockSectors = alignedBlockBytes / sectorSize;

                var progress = new SurfaceTestProgress
                {
                    TotalBlocks = totalBlocks,
                    TotalSectors = totalSectors,
                    BlockSectors = blockSectors,
                    BlockSizeMB = alignedBlockBytes / 1_048_576.0
                };

                WarmUpDriveZones(hDevice, totalSizeBytes, safeAlignment, buffer);

                var swTotal = Stopwatch.StartNew();
        var runBaseline = new LatencyBaseline();
                long totalBytesRead = 0;
                double cumulativeReadSeconds = 0.0;
                int consecutiveDynaFails = 0;

                int chunksPerZone = 1;
                double normDivisor = Math.Max(1.0, alignedBlockBytes / 1_048_576.0);

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
                    long failChunkOffset = baseByteOffset;
                    double zoneDurationSec = 0.0;
                    double maxChunkLatMs = 0.0;
                    bool zoneFailed = false;
                    bool zoneMediaError = false;
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

                            if (RecoverFailedRead(hDevice, buffer, alignedBlockBytes, currentChunkOffset, cancellationToken, out long recTicks, out uint recBytes, out int recErr))
                            {
                                startTimestamp = 0;
                                endTimestamp = recTicks;
                                bytesRead = recBytes;
                            }
                            else
                            {
                                zoneFailed = true;
                                zoneMediaError = IsMediaError(win32Err) || IsMediaError(recErr);
                                failChunkOffset = currentChunkOffset;
                                break;
                            }
                        }

                        double chunkSec = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                        double chunkLatMs = (chunkSec * 1000.0) / normDivisor;

                        if (chunkLatMs >= 50.0 && scanMode == SurfaceScanMode.VerifyRead && !cancellationToken.IsCancellationRequested)
                        {
                            double confirmedMs = ConfirmLatencyMs(hDevice, buffer, alignedBlockBytes, currentChunkOffset, chunkLatMs, normDivisor, cancellationToken);
                            if (confirmedMs < chunkLatMs)
                            {
                                chunkLatMs = confirmedMs;
                                chunkSec = (confirmedMs * normDivisor) / 1000.0;
                            }
                        }

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
                        var badLbas = new List<long>();
                        if (zoneMediaError)
                        {
                            IsolateFailingSectors(hDevice, failChunkOffset, alignedBlockBytes, safeAlignment, buffer, badLbas);
                        }

                        if (badLbas.Count > 0)
                        {
                            consecutiveDynaFails++;
                            status = SurfaceBlockStatus.BadSector;
                            progress.BadBlocks++;
                            if (consecutiveDynaFails >= 8) break;
                        }
                        else
                        {
                            consecutiveDynaFails = 0;
                            progress.MultiPhaseRecoveredBlocks++;
                            status = SurfaceBlockStatus.Slow;
                            progress.SlowBlocks++;
                        }
                    }
                    else
                    {
                        consecutiveDynaFails = 0;
                        totalBytesRead += zoneBytesRead;
                        cumulativeReadSeconds += zoneDurationSec;

                        status = runBaseline.Classify(latencyMs);
                        if (status <= SurfaceBlockStatus.Good) progress.GoodBlocks++;
                        else progress.SlowBlocks++;
                    }

                    progress.BytesVerified = totalBytesRead;

                    if (coverage == SurfaceScanCoverage.QuickDense && scanMode == SurfaceScanMode.VerifyRead && b == 9 && totalBlocks > 10)
                    {
                        double elapsedSoFar = swTotal.Elapsed.TotalSeconds;
                        double perChunkSec = Math.Max(zoneDurationSec / Math.Max(1, chunksPerZone), 0.0005);
                        double overheadPerZone = Math.Max(0.0, (elapsedSoFar / 10.0) - perChunkSec);
                        double budgetPerZone = (45.0 - elapsedSoFar) / (totalBlocks - 10);
                        int maxChunks = (int)Math.Clamp(stepBytes / alignedBlockBytes, 1, 64);
                        chunksPerZone = (int)Math.Clamp((budgetPerZone - overheadPerZone) / perChunkSec, 1, maxChunks);
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
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            for (int r = 0; r < 2 && (!ok || bRead < safeAlignment); r++)
            {
                Thread.Sleep(20);
                SetFilePointerEx(hDevice, secOffset, out _, FILE_BEGIN);
                ok = ReadFile(hDevice, sectorBuf, safeAlignment, out bRead, IntPtr.Zero);
                err = ok ? 0 : Marshal.GetLastWin32Error();
            }
            if ((!ok || bRead < safeAlignment) && (err == 0 || IsMediaError(err)))
            {
                badLbas.Add(secOffset / safeAlignment);
            }
        }
    }

    private static bool IsMediaError(int err)
    {
        return err == 23 || err == 27 || err == 30 || err == 1117;
    }

    private static bool RecoverFailedRead(
        SafeFileHandle hDevice,
        IntPtr buffer,
        uint bytes,
        long offset,
        CancellationToken token,
        out long ticks,
        out uint bytesRead,
        out int lastErr)
    {
        ticks = 0;
        bytesRead = 0;
        lastErr = 0;
        for (int attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
        {
            Thread.Sleep(50);
            SetFilePointerEx(hDevice, offset, out _, FILE_BEGIN);
            long start = Stopwatch.GetTimestamp();
            bool ok = ReadFile(hDevice, buffer, bytes, out uint br, IntPtr.Zero);
            long end = Stopwatch.GetTimestamp();
            if (ok && br > 0)
            {
                ticks = end - start;
                bytesRead = br;
                return true;
            }
            lastErr = Marshal.GetLastWin32Error();
        }
        return false;
    }

    private static double ConfirmLatencyMs(
        SafeFileHandle hDevice,
        IntPtr buffer,
        uint bytes,
        long offset,
        double firstMs,
        double normDivisor,
        CancellationToken token)
    {
        double best = firstMs;
        for (int attempt = 0; attempt < 3 && best >= (attempt == 0 ? 50.0 : 150.0) && !token.IsCancellationRequested; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            SetFilePointerEx(hDevice, offset, out _, FILE_BEGIN);
            long start = Stopwatch.GetTimestamp();
            bool ok = ReadFile(hDevice, buffer, bytes, out uint br, IntPtr.Zero);
            long end = Stopwatch.GetTimestamp();
            if (ok && br > 0)
            {
                double ms = ((end - start) * 1000.0 / Stopwatch.Frequency) / normDivisor;
                if (ms < best) best = ms;
            }
        }
        return best;
    }

    public static (string quickText, string fullText, string profile, double estSequentialMBps, double fullSeconds) EstimateDriveSurfaceMetrics(DriveSmartReport rep)
    {
        double capBytes = rep.DiskSizeBytes > 0 ? (double)rep.DiskSizeBytes : 10.0 * 1024 * 1024 * 1024;
        double capMb = capBytes / (1024.0 * 1024.0);

        double estSequentialMBps;
        double quickDurationSec;
        string profile;

        if (rep.IsNvme)
        {
            estSequentialMBps = 1800.0;
            quickDurationSec = 0.8;
            profile = "NVMe PCIe";
        }
        else if (rep.IsSsd || rep.RotationRateRpm == 1)
        {
            if (rep.BusTypeName.Equals("USB", StringComparison.OrdinalIgnoreCase))
            {
                estSequentialMBps = 420.0;
                quickDurationSec = 2.0;
                profile = "External USB SSD";
            }
            else
            {
                estSequentialMBps = 480.0;
                quickDurationSec = 1.2;
                profile = "SATA / SAS SSD";
            }
        }
        else if (rep.RotationRateRpm >= 7200)
        {
            estSequentialMBps = 165.0;
            quickDurationSec = 6.0;
            profile = $"HDD {rep.RotationRateRpm} RPM";
        }
        else if (rep.RotationRateRpm > 0 && rep.RotationRateRpm < 7200)
        {
            estSequentialMBps = 100.0;
            quickDurationSec = 11.0;
            profile = $"HDD {rep.RotationRateRpm} RPM";
        }
        else if (rep.BusTypeName.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            estSequentialMBps = 35.0;
            quickDurationSec = 14.0;
            profile = "USB Storage Device";
        }
        else
        {
            estSequentialMBps = 130.0;
            quickDurationSec = 7.5;
            profile = "SATA Mechanical HDD";
        }

        string quickText = quickDurationSec < 1.5
            ? "~0.5s to ~1.5s"
            : $"~{quickDurationSec:F0}s to ~{(quickDurationSec * 1.4):F0}s";

        double fullSec = capMb / estSequentialMBps;
        string fullText;
        if (fullSec < 60.0)
        {
            fullText = $"~{fullSec:F0}s ({estSequentialMBps:F0} MB/s)";
        }
        else if (fullSec < 3600.0)
        {
            double mins = fullSec / 60.0;
            fullText = mins < 10.0
                ? $"~{mins:F1} min ({estSequentialMBps:F0} MB/s)"
                : $"~{mins:F0} min ({estSequentialMBps:F0} MB/s)";
        }
        else
        {
            double hours = fullSec / 3600.0;
            fullText = $"~{hours:F1} hours ({estSequentialMBps:F0} MB/s)";
        }

        return (quickText, fullText, profile, estSequentialMBps, fullSec);
    }
}
