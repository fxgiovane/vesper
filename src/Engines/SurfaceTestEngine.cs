using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public enum SurfaceBlockStatus : byte
{
    Unchecked = 0,
    Excellent = 1,
    Good = 2,
    Slow = 3,
    Lagging = 4,
    Damaged = 5,
    BadSector = 6
}

public sealed class SurfaceBlockResult
{
    public long BlockIndex { get; init; }
    public long LbaStart { get; init; }
    public long SectorStart { get; init; }
    public long SectorEnd { get; init; }
    public double LatencyMs { get; init; }
    public double SpeedMBps { get; init; }
    public SurfaceBlockStatus Status { get; init; }
}

public sealed class SurfaceTestProgress
{
    public long CurrentBlock { get; set; }
    public long TotalBlocks { get; set; }
    public double Percent => TotalBlocks > 0 ? (double)CurrentBlock / TotalBlocks * 100.0 : 0.0;
    public long TotalSectors { get; set; }
    public long BlockSectors { get; set; }
    public double BlockSizeMB { get; set; }
    public long CurrentLba { get; set; }
    public long CurrentSectorStart { get; set; }
    public long CurrentSectorEnd { get; set; }
    public double CurrentSpeedMBps { get; set; }
    public double MinSpeedMBps { get; set; } = double.MaxValue;
    public double AvgSpeedMBps { get; set; }
    public double MaxSpeedMBps { get; set; }
    public double CurrentLatencyMs { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public long GoodBlocks { get; set; }
    public long SlowBlocks { get; set; }
    public long DamagedBlocks { get; set; }
    public long BadBlocks { get; set; }
    public double SurfaceHealthPercent => CurrentBlock > 0 ? ((double)(GoodBlocks + SlowBlocks) / CurrentBlock) * 100.0 : 100.0;
}

public sealed class SurfaceTestEngine
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

    public static readonly Dictionary<string, SurfaceBlockStatus[]> DiskSurfaceCache = new(StringComparer.OrdinalIgnoreCase);

    public static SurfaceBlockStatus[] GetOrProbeSurfaceSample(string devicePath, long totalSizeBytes, uint sectorSize, int blockCount)
    {
        if (DiskSurfaceCache.TryGetValue(devicePath, out var cached) && cached.Length == blockCount)
            return cached;

        var result = ProbeSurfaceSample(devicePath, totalSizeBytes, sectorSize, blockCount);
        DiskSurfaceCache[devicePath] = result;
        return result;
    }

    public static SurfaceBlockStatus[] ProbeSurfaceSample(string devicePath, long totalSizeBytes, uint sectorSize, int blockCount)
    {
        var blocks = new SurfaceBlockStatus[blockCount];
        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;

        uint alignedReadBytes = 4096;
        if (alignedReadBytes < sectorSize) alignedReadBytes = sectorSize;

        long stepBytes = totalSizeBytes / blockCount;
        stepBytes = (stepBytes / sectorSize) * sectorSize;
        if (stepBytes < alignedReadBytes) stepBytes = alignedReadBytes;

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)alignedReadBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
        {
            Array.Fill(blocks, SurfaceBlockStatus.Good);
            return blocks;
        }

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

            if (hDevice.IsInvalid)
            {
                Array.Fill(blocks, SurfaceBlockStatus.Good);
                return blocks;
            }

            using (hDevice)
            {
                for (int b = 0; b < blockCount; b++)
                {
                    long targetByteOffset = b * stepBytes;
                    if (targetByteOffset + alignedReadBytes > totalSizeBytes)
                        targetByteOffset = Math.Max(0, totalSizeBytes - alignedReadBytes);

                    targetByteOffset = (targetByteOffset / sectorSize) * sectorSize;

                    SetFilePointerEx(hDevice, targetByteOffset, out _, FILE_BEGIN);

                    long startTimestamp = Stopwatch.GetTimestamp();
                    bool readOk = ReadFile(hDevice, buffer, alignedReadBytes, out uint bytesRead, IntPtr.Zero);
                    long endTimestamp = Stopwatch.GetTimestamp();

                    double latencyMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

                    if (!readOk || bytesRead == 0)
                        blocks[b] = SurfaceBlockStatus.BadSector;
                    else if (latencyMs < 5.0)
                        blocks[b] = SurfaceBlockStatus.Excellent;
                    else if (latencyMs < 20.0)
                        blocks[b] = SurfaceBlockStatus.Good;
                    else if (latencyMs < 50.0)
                        blocks[b] = SurfaceBlockStatus.Slow;
                    else if (latencyMs < 500.0)
                        blocks[b] = SurfaceBlockStatus.Lagging;
                    else
                        blocks[b] = SurfaceBlockStatus.Damaged;
                }
            }
        }
        catch
        {
            Array.Fill(blocks, SurfaceBlockStatus.Good);
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
        int throttleMs = 0)
    {
        if (sectorSize == 0) sectorSize = 512;
        if (totalSizeBytes <= 0) totalSizeBytes = 10L * 1024 * 1024 * 1024;

        uint alignedBlockBytes = 64 * 1024;
        if (alignedBlockBytes % sectorSize != 0)
            alignedBlockBytes = ((alignedBlockBytes / sectorSize) + 1) * sectorSize;

        long totalBlocks = sampleGridSize > 0 ? sampleGridSize : (totalSizeBytes / alignedBlockBytes);
        if (totalBlocks <= 0) totalBlocks = 1;

        long stepBytes = totalSizeBytes / totalBlocks;
        stepBytes = (stepBytes / sectorSize) * sectorSize;
        if (stepBytes < alignedBlockBytes) stepBytes = alignedBlockBytes;

        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)alignedBlockBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero)
            throw new OutOfMemoryException("Falha ao alocar buffer alinhado na memoria fisica.");

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
                    throw new IOException($"Nao foi possivel abrir {devicePath} para leitura direta. Erro Win32: {Marshal.GetLastWin32Error()}");

                long totalSectors = totalSizeBytes / sectorSize;
                long blockSectors = alignedBlockBytes / sectorSize;

                var progress = new SurfaceTestProgress
                {
                    TotalBlocks = totalBlocks,
                    TotalSectors = totalSectors,
                    BlockSectors = blockSectors,
                    BlockSizeMB = alignedBlockBytes / (1024.0 * 1024.0)
                };

            var swTotal = Stopwatch.StartNew();
            long totalBytesRead = 0;
            double cumulativeReadSeconds = 0.0;

            for (long b = 0; b < totalBlocks; b++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                long targetByteOffset = b * stepBytes;
                if (targetByteOffset + alignedBlockBytes > totalSizeBytes)
                    targetByteOffset = Math.Max(0, totalSizeBytes - alignedBlockBytes);

                targetByteOffset = (targetByteOffset / sectorSize) * sectorSize;
                long startSector = targetByteOffset / sectorSize;
                long endSector = (targetByteOffset + alignedBlockBytes) / sectorSize - 1;

                progress.CurrentLba = startSector;
                progress.CurrentSectorStart = startSector;
                progress.CurrentSectorEnd = endSector;

                onBlockStarting?.Invoke(b);

                SetFilePointerEx(hDevice, targetByteOffset, out _, FILE_BEGIN);

                long startTimestamp = Stopwatch.GetTimestamp();
                bool readOk = ReadFile(hDevice, buffer, alignedBlockBytes, out uint bytesRead, IntPtr.Zero);
                long endTimestamp = Stopwatch.GetTimestamp();

                double readSeconds = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                cumulativeReadSeconds += readSeconds;
                double latencyMs = readSeconds * 1000.0;
                double speedMBps = bytesRead > 0 && readSeconds > 0 ? (bytesRead / 1048576.0) / readSeconds : 0.0;

                totalBytesRead += bytesRead;

                SurfaceBlockStatus status;
                if (!readOk || bytesRead == 0 || latencyMs >= 500.0)
                {
                    status = (readOk && bytesRead > 0) ? SurfaceBlockStatus.Damaged : SurfaceBlockStatus.BadSector;
                    if (status == SurfaceBlockStatus.BadSector) progress.BadBlocks++;
                    else progress.DamagedBlocks++;
                }
                else if (latencyMs < 5.0)
                {
                    status = SurfaceBlockStatus.Excellent;
                    progress.GoodBlocks++;
                }
                else if (latencyMs < 20.0)
                {
                    status = SurfaceBlockStatus.Good;
                    progress.GoodBlocks++;
                }
                else if (latencyMs < 50.0)
                {
                    status = SurfaceBlockStatus.Slow;
                    progress.SlowBlocks++;
                }
                else
                {
                    status = SurfaceBlockStatus.Lagging;
                    progress.SlowBlocks++;
                }

                progress.CurrentBlock = b + 1;
                progress.CurrentLatencyMs = latencyMs;
                progress.CurrentSpeedMBps = speedMBps;
                if (speedMBps > 0 && speedMBps < progress.MinSpeedMBps) progress.MinSpeedMBps = speedMBps;
                if (speedMBps > progress.MaxSpeedMBps) progress.MaxSpeedMBps = speedMBps;

                progress.Elapsed = swTotal.Elapsed;
                double totalElapsedSeconds = swTotal.Elapsed.TotalSeconds;
                progress.AvgSpeedMBps = cumulativeReadSeconds > 0 ? (totalBytesRead / 1048576.0) / cumulativeReadSeconds : 0.0;

                double remainingBlocks = totalBlocks - (b + 1);
                double avgTimePerBlockMs = totalElapsedSeconds * 1000.0 / (b + 1);
                progress.EstimatedRemaining = TimeSpan.FromMilliseconds(remainingBlocks * avgTimePerBlockMs);

                var result = new SurfaceBlockResult
                {
                    BlockIndex = b,
                    LbaStart = startSector,
                    SectorStart = startSector,
                    SectorEnd = endSector,
                    LatencyMs = latencyMs,
                    SpeedMBps = speedMBps,
                    Status = status
                };

                onBlockTested(result, progress);

                if (DiskSurfaceCache.TryGetValue(devicePath, out var cachedArr) && b < cachedArr.Length)
                {
                    cachedArr[b] = status;
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
}
