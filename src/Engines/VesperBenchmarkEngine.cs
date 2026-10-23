using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public enum BenchmarkProfileId : byte
{
    Seq1MQ8T1 = 0,
    Seq128KQ32T1 = 1,
    Rnd4KQ32T16 = 2,
    Rnd4KQ1T1 = 3
}

public sealed class BenchmarkItemResult
{
    public BenchmarkProfileId Id { get; init; }
    public string Name { get; set; } = "";
    public string QueueThreadInfo { get; set; } = "";
    public double ReadMBps { get; set; }
    public double WriteMBps { get; set; }
    public double ReadIops { get; set; }
    public double WriteIops { get; set; }
    public double ReadLatencyUs { get; set; }
    public double WriteLatencyUs { get; set; }
    public bool ReadDone { get; set; }
    public bool WriteDone { get; set; }
}

public sealed class BenchmarkProgress
{
    public string TargetPath { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string CpuName { get; set; } = "";
    public long TestFileSizeBytes { get; set; }
    public int ActiveItemIndex { get; set; }
    public bool IsWriting { get; set; }
    public string CurrentOperationDescription { get; set; } = "";
    public double CurrentSpeedMBps { get; set; }
    public double OverallPercent { get; set; }
    public TimeSpan Elapsed { get; set; }
    public BenchmarkItemResult[] Items { get; } = new BenchmarkItemResult[4]
    {
        new BenchmarkItemResult { Id = BenchmarkProfileId.Seq1MQ8T1, Name = "SEQ1M", QueueThreadInfo = "Q8T1" },
        new BenchmarkItemResult { Id = BenchmarkProfileId.Seq128KQ32T1, Name = "SEQ128K", QueueThreadInfo = "Q32T1" },
        new BenchmarkItemResult { Id = BenchmarkProfileId.Rnd4KQ32T16, Name = "RND4K", QueueThreadInfo = "Q32T16" },
        new BenchmarkItemResult { Id = BenchmarkProfileId.Rnd4KQ1T1, Name = "RND4K", QueueThreadInfo = "Q1T1" }
    };
}

public sealed class IoBlockLadderStep
{
    public int BlockSizeBytes { get; init; }
    public string Label { get; init; } = "";
    public double WriteMBps { get; set; }
    public double ReadMBps { get; set; }
    public double WriteIops { get; set; }
    public double ReadIops { get; set; }
    public bool Done { get; set; }
}

public sealed class IoBlockLadderProgress
{
    public string TargetPath { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int ActiveStepIndex { get; set; }
    public bool IsWriting { get; set; }
    public double CurrentSpeedMBps { get; set; }
    public double OverallPercent { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<IoBlockLadderStep> Steps { get; } = new();
}

public sealed class LbaCurvePoint
{
    public int Percentile { get; init; }
    public long Lba { get; init; }
    public double SpeedMBps { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class LbaCurveProgress
{
    public string DevicePath { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int CurrentPercentile { get; set; }
    public double CurrentSpeedMBps { get; set; }
    public double MinSpeedMBps { get; set; } = double.MaxValue;
    public double MaxSpeedMBps { get; set; }
    public double AvgSpeedMBps { get; set; }
    public double BurstRateMBps { get; set; }
    public double AccessTimeMs { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<LbaCurvePoint> Points { get; } = new();
}

public static class VesperBenchmarkEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_BEGIN = 0;

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint PAGE_READWRITE = 0x04;

    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint ERROR_IO_PENDING = 997;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED_ENTRY
    {
        public UIntPtr lpCompletionKey;
        public IntPtr lpOverlapped;
        public UIntPtr Internal;
        public uint dwNumberOfBytesTransferred;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateIoCompletionPort(
        IntPtr FileHandle,
        IntPtr ExistingCompletionPort,
        UIntPtr CompletionKey,
        uint NumberOfConcurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatusEx(
        IntPtr CompletionPort,
        [Out] OVERLAPPED_ENTRY[] lpCompletionPortEntries,
        uint ulCount,
        out uint ulNumEntriesRemoved,
        uint dwMilliseconds,
        bool fAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

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
    private static extern bool SetEndOfFile(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        IntPtr lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public static string DetectCpuModel()
    {
        try
        {
            string? keyVal = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                "Processador x86_64") as string;
            if (!string.IsNullOrWhiteSpace(keyVal))
            {
                return keyVal.Trim();
            }
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "AMD / Intel Host CPU";
    }

    public static int ExtractPhysicalDiskIndex(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return -1;
        var match = Regex.Match(devicePath, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int idx))
        {
            return idx;
        }
        return -1;
    }

    private static string GetWritableBenchmarkDirectoryForDrive(DriveInfo drive)
    {
        string sysDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        if (string.Equals(drive.Name, sysDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "VesperBenchmark");
            try { Directory.CreateDirectory(tempDir); } catch { }
            return Directory.Exists(tempDir) ? tempDir : Path.GetTempPath();
        }

        string targetDir = Path.Combine(drive.RootDirectory.FullName, ".vesper_bench");
        try
        {
            Directory.CreateDirectory(targetDir);
            return targetDir;
        }
        catch
        {
            return drive.RootDirectory.FullName;
        }
    }

    public static string? ResolveBenchmarkDirectory(DriveSmartReport rep)
    {
        int targetDiskIndex = ExtractPhysicalDiskIndex(rep.DevicePath);
        if (targetDiskIndex < 0) return null;

        DriveInfo? bestDrive = null;
        long maxFreeSpace = 0;

        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;

            string volPath = @"\\.\" + d.Name.TrimEnd('\\');
            using SafeFileHandle hVol = CreateFileW(
                volPath,
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hVol.IsInvalid) continue;

            int bufSize = 2048;
            IntPtr pExtents = Marshal.AllocHGlobal(bufSize);
            try
            {
                if (DeviceIoControl(hVol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, pExtents, (uint)bufSize, out _, IntPtr.Zero))
                {
                    int extentCount = Marshal.ReadInt32(pExtents);
                    for (int i = 0; i < extentCount; i++)
                    {
                        int diskNum = Marshal.ReadInt32(pExtents, 8 + (i * 24));
                        if (diskNum == targetDiskIndex)
                        {
                            if (d.AvailableFreeSpace > maxFreeSpace)
                            {
                                maxFreeSpace = d.AvailableFreeSpace;
                                bestDrive = d;
                            }
                            break;
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pExtents);
            }
        }

        if (bestDrive != null && maxFreeSpace >= 600L * 1024 * 1024)
        {
            return GetWritableBenchmarkDirectoryForDrive(bestDrive);
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\\\PhysicalDrive{targetDiskIndex}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            foreach (var part in searcher.Get())
            {
                using var partSearcher = new System.Management.ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                foreach (var logDisk in partSearcher.Get())
                {
                    string? devId = logDisk["DeviceID"]?.ToString();
                    if (!string.IsNullOrEmpty(devId))
                    {
                        var d = new DriveInfo(devId);
                        if (d.IsReady && d.AvailableFreeSpace > maxFreeSpace)
                        {
                            maxFreeSpace = d.AvailableFreeSpace;
                            bestDrive = d;
                        }
                    }
                }
            }
            if (bestDrive != null && maxFreeSpace >= 600L * 1024 * 1024)
            {
                return GetWritableBenchmarkDirectoryForDrive(bestDrive);
            }
        }
        catch { }

        if (targetDiskIndex == 0)
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var dSys = new DriveInfo(sysRoot);
            if (dSys.IsReady && dSys.AvailableFreeSpace >= 600L * 1024 * 1024)
            {
                return GetWritableBenchmarkDirectoryForDrive(dSys);
            }
        }

        if (bestDrive != null && maxFreeSpace >= 600L * 1024 * 1024)
        {
            return GetWritableBenchmarkDirectoryForDrive(bestDrive);
        }

        return null;
    }

    public static void RunBenchmark(
        DriveSmartReport report,
        CancellationToken cancellationToken,
        Action<BenchmarkProgress> onProgress,
        bool smokeTestMode = false)
    {
        string? targetDir = ResolveBenchmarkDirectory(report);
        if (targetDir == null)
        {
            RunRawPhysicalDiskBenchmark(report, cancellationToken, onProgress, smokeTestMode);
            return;
        }

        string tempFile = Path.Combine(targetDir, $".vesper_bench_{Environment.ProcessId:X8}_{Guid.NewGuid():N}.tmp");
        long targetFileSize = smokeTestMode ? (64L * 1024 * 1024) : (1024L * 1024 * 1024);
        var progress = new BenchmarkProgress
        {
            TargetPath = targetDir,
            ModelName = report.Model,
            CpuName = DetectCpuModel(),
            TestFileSizeBytes = targetFileSize
        };
        ApplyMediaProfile(progress, report.IsNvme);

        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            PrepareTestFile(tempFile, targetFileSize, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(smokeTestMode ? 500 : 3000);
            }

            for (int stage = 0; stage < 4; stage++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                progress.ActiveItemIndex = stage;
                var item = progress.Items[stage];

                uint blockSize;
                int queueDepth;
                int threadCount;
                bool isRandom;
                int durationMs = smokeTestMode ? 1000 : 5000;

                switch (stage)
                {
                    case 0:
                        blockSize = 1024 * 1024;
                        queueDepth = 8;
                        threadCount = 1;
                        isRandom = false;
                        break;
                    case 1:
                        blockSize = report.IsNvme ? 128u * 1024u : 1024u * 1024u;
                        queueDepth = report.IsNvme ? 32 : 1;
                        threadCount = 1;
                        isRandom = false;
                        break;
                    case 2:
                        blockSize = 4096;
                        queueDepth = 32;
                        threadCount = report.IsNvme ? 16 : 1;
                        isRandom = true;
                        break;
                    case 3:
                    default:
                        blockSize = 4096;
                        queueDepth = 1;
                        threadCount = 1;
                        isRandom = true;
                        break;
                }

                int totalPasses = smokeTestMode ? 1 : 3;
                double peakReadMBps = 0, peakWriteMBps = 0;
                double peakReadIops = 0, peakWriteIops = 0;
                double sumReadLatUs = 0, sumWriteLatUs = 0;

                for (int pass = 0; pass <= totalPasses && !cancellationToken.IsCancellationRequested; pass++)
                {
                    bool isWarmup = pass == 0;

                    progress.IsWriting = false;
                    progress.CurrentOperationDescription = isWarmup
                        ? $"Warmup {item.Name} {item.QueueThreadInfo}"
                        : $"Read {item.Name} {item.QueueThreadInfo} [{pass}/{totalPasses}]";
                    progress.OverallPercent = (stage * 25.0) + (pass * 12.5 / (totalPasses + 1));
                    progress.Elapsed = totalStopwatch.Elapsed;
                    onProgress?.Invoke(progress);

                    var readResult = MeasureIoAsync(
                        tempFile,
                        targetFileSize,
                        blockSize,
                        queueDepth,
                        threadCount,
                        isWarmup ? Math.Max(500, durationMs / 5) : durationMs,
                        isRandom,
                        isWrite: false,
                        cancellationToken,
                        (speed, pLocal) =>
                        {
                            progress.CurrentSpeedMBps = speed;
                            progress.OverallPercent = (stage * 25.0) + ((pass + pLocal) * 12.5 / (totalPasses + 1));
                            progress.Elapsed = totalStopwatch.Elapsed;
                            onProgress?.Invoke(progress);
                        });

                    if (!isWarmup)
                    {
                        if (readResult.SpeedMBps > peakReadMBps) peakReadMBps = readResult.SpeedMBps;
                        if (readResult.Iops > peakReadIops) peakReadIops = readResult.Iops;
                        sumReadLatUs += readResult.LatencyUs;
                    }

                    if (cancellationToken.IsCancellationRequested) break;

                    Thread.Sleep(smokeTestMode ? 300 : 2500);

                    progress.IsWriting = true;
                    progress.CurrentOperationDescription = isWarmup
                        ? $"Warmup W {item.Name} {item.QueueThreadInfo}"
                        : $"Write {item.Name} {item.QueueThreadInfo} [{pass}/{totalPasses}]";
                    progress.OverallPercent = (stage * 25.0) + ((pass + 0.5) * 12.5 / (totalPasses + 1));
                    progress.Elapsed = totalStopwatch.Elapsed;
                    onProgress?.Invoke(progress);

                    var writeResult = MeasureIoAsync(
                        tempFile,
                        targetFileSize,
                        blockSize,
                        queueDepth,
                        threadCount,
                        isWarmup ? Math.Max(500, durationMs / 5) : durationMs,
                        isRandom,
                        isWrite: true,
                        cancellationToken,
                        (speed, pLocal) =>
                        {
                            progress.CurrentSpeedMBps = speed;
                            progress.OverallPercent = (stage * 25.0) + ((pass + 0.5 + pLocal * 0.5) * 12.5 / (totalPasses + 1));
                            progress.Elapsed = totalStopwatch.Elapsed;
                            onProgress?.Invoke(progress);
                        });

                    if (!isWarmup)
                    {
                        if (writeResult.SpeedMBps > peakWriteMBps) peakWriteMBps = writeResult.SpeedMBps;
                        if (writeResult.Iops > peakWriteIops) peakWriteIops = writeResult.Iops;
                        sumWriteLatUs += writeResult.LatencyUs;
                    }

                    if (pass < totalPasses && !cancellationToken.IsCancellationRequested)
                        Thread.Sleep(smokeTestMode ? 200 : 1000);
                }

                item.ReadMBps = peakReadMBps;
                item.ReadIops = peakReadIops;
                item.ReadLatencyUs = totalPasses > 0 ? sumReadLatUs / totalPasses : 0;
                item.ReadDone = true;

                item.WriteMBps = peakWriteMBps;
                item.WriteIops = peakWriteIops;
                item.WriteLatencyUs = totalPasses > 0 ? sumWriteLatUs / totalPasses : 0;
                item.WriteDone = true;

                progress.OverallPercent = (stage + 1) * 25.0;
                progress.Elapsed = totalStopwatch.Elapsed;
                onProgress?.Invoke(progress);

                if (stage < 3 && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(smokeTestMode ? 300 : 2500);
                }
            }

            progress.CurrentOperationDescription = "Benchmark complete.";
            progress.OverallPercent = 100.0;
            progress.Elapsed = totalStopwatch.Elapsed;
            onProgress?.Invoke(progress);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch { }
        }
    }

    private static void ApplyMediaProfile(BenchmarkProgress progress, bool isNvme)
    {
        if (isNvme) return;
        progress.Items[1].Name = "SEQ1M";
        progress.Items[1].QueueThreadInfo = "Q1T1";
        progress.Items[2].QueueThreadInfo = "Q32T1";
    }

    public static void RunRawPhysicalDiskBenchmark(
        DriveSmartReport report,
        CancellationToken cancellationToken,
        Action<BenchmarkProgress> onProgress,
        bool smokeTestMode = false)
    {
        var progress = new BenchmarkProgress
        {
            TargetPath = $"{report.DevicePath} [MODO FISICO RAW]",
            ModelName = report.Model,
            CpuName = DetectCpuModel(),
            TestFileSizeBytes = report.DiskSizeBytes
        };
        ApplyMediaProfile(progress, report.IsNvme);

        var totalStopwatch = Stopwatch.StartNew();

        for (int stage = 0; stage < 4; stage++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            progress.ActiveItemIndex = stage;
            var item = progress.Items[stage];

            uint blockSize;
            int queueDepth;
            int threadCount;
            bool isRandom;
            int durationMs = smokeTestMode ? 1000 : 5000;

            switch (stage)
            {
                case 0:
                    blockSize = 1024 * 1024;
                    queueDepth = 8;
                    threadCount = 1;
                    isRandom = false;
                    break;
                case 1:
                    blockSize = report.IsNvme ? 128u * 1024u : 1024u * 1024u;
                    queueDepth = report.IsNvme ? 32 : 1;
                    threadCount = 1;
                    isRandom = false;
                    break;
                case 2:
                    blockSize = 4096;
                    queueDepth = 32;
                    threadCount = report.IsNvme ? 16 : 1;
                    isRandom = true;
                    break;
                case 3:
                default:
                    blockSize = 4096;
                    queueDepth = 1;
                    threadCount = 1;
                    isRandom = true;
                    break;
            }

            progress.IsWriting = false;
            progress.CurrentOperationDescription = $"Raw Physical Read {item.Name} {item.QueueThreadInfo}";
            progress.OverallPercent = (stage * 25.0) + 2.0;
            progress.Elapsed = totalStopwatch.Elapsed;
            onProgress?.Invoke(progress);

            var readResult = MeasureIoAsync(
                report.DevicePath,
                report.DiskSizeBytes,
                blockSize,
                queueDepth,
                threadCount,
                durationMs,
                isRandom,
                isWrite: false,
                cancellationToken,
                (speed, pLocal) =>
                {
                    progress.CurrentSpeedMBps = speed;
                    progress.OverallPercent = (stage * 25.0) + (pLocal * 12.5);
                    progress.Elapsed = totalStopwatch.Elapsed;
                    onProgress?.Invoke(progress);
                });

            item.ReadMBps = readResult.SpeedMBps;
            item.ReadIops = readResult.Iops;
            item.ReadLatencyUs = readResult.LatencyUs;
            item.ReadDone = true;

            item.WriteMBps = 0.0;
            item.WriteIops = 0.0;
            item.WriteLatencyUs = 0.0;
            item.WriteDone = true;

            progress.OverallPercent = (stage + 1) * 25.0;
            progress.Elapsed = totalStopwatch.Elapsed;
            onProgress?.Invoke(progress);

            if (!smokeTestMode && stage < 3 && !cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(300);
            }
        }

        progress.CurrentOperationDescription = "Raw physical benchmark complete (write protected).";
        progress.OverallPercent = 100.0;
        progress.Elapsed = totalStopwatch.Elapsed;
        onProgress?.Invoke(progress);
    }

    private static void PrepareTestFile(string path, long totalSize, CancellationToken token)
    {
        uint allocSize = 1024 * 1024;
        IntPtr buffer = VirtualAlloc(IntPtr.Zero, (nuint)allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (buffer == IntPtr.Zero) throw new OutOfMemoryException("Buffer pre-alloc failed");

        try
        {
            byte[] initPattern = new byte[allocSize];
            Random.Shared.NextBytes(initPattern);
            Marshal.Copy(initPattern, 0, buffer, (int)allocSize);

            using SafeFileHandle handle = CreateFileW(
                path,
                GENERIC_READ | GENERIC_WRITE,
                0,
                IntPtr.Zero,
                CREATE_ALWAYS,
                FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                throw new IOException($"Failed to create unbuffered benchmark file at {path}. Win32 error: {Marshal.GetLastWin32Error()}");
            }

            SetFilePointerEx(handle, totalSize, out _, FILE_BEGIN);
            SetEndOfFile(handle);
            SetFilePointerEx(handle, 0, out _, FILE_BEGIN);

            long written = 0;
            while (written < totalSize)
            {
                if (token.IsCancellationRequested) return;

                uint toWrite = (uint)Math.Min(allocSize, totalSize - written);
                if (!WriteFile(handle, buffer, toWrite, out uint bytesOut, IntPtr.Zero) || bytesOut == 0)
                {
                    break;
                }
                written += bytesOut;
            }

            SetEndOfFile(handle);
            FlushFileBuffers(handle);
        }
        finally
        {
            VirtualFree(buffer, 0, MEM_RELEASE);
        }
    }

    private sealed record IoMeasurement(double SpeedMBps, double Iops, double LatencyUs);

    private static IoMeasurement MeasureIoAsync(
        string filePath,
        long fileSize,
        uint blockSize,
        int queueDepth,
        int threadCount,
        int durationMs,
        bool isRandom,
        bool isWrite,
        CancellationToken token,
        Action<double, double> onProgressSub)
    {
        long maxOffset = Math.Max(0, fileSize - blockSize);
        long totalBlocks = maxOffset / blockSize;
        if (totalBlocks <= 0) totalBlocks = 1;

        long globalBytesTransferred = 0;
        long globalOpsCompleted = 0;
        long globalLatencyTicks = 0;
        long globalOffsetCounter = 0;

        using var barrier = new Barrier(threadCount + 1);
        Stopwatch sw = new();
        int warmupMs = durationMs <= 1000 ? 100 : 1000;
        long warmupTicks = (long)(warmupMs * (Stopwatch.Frequency / 1000.0));
        long measureTicks = (long)(durationMs * (Stopwatch.Frequency / 1000.0));
        bool stopRequested = false;

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                uint access = isWrite ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
                using SafeFileHandle handle = CreateFileW(
                    filePath,
                    access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    barrier.RemoveParticipant();
                    return;
                }

                IntPtr hPort = CreateIoCompletionPort(handle.DangerousGetHandle(), IntPtr.Zero, UIntPtr.Zero, 1);
                if (hPort == IntPtr.Zero)
                {
                    barrier.RemoveParticipant();
                    return;
                }

                nuint totalBufSize = (nuint)(blockSize * queueDepth);
                IntPtr pBaseBuffer = VirtualAlloc(IntPtr.Zero, totalBufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (pBaseBuffer == IntPtr.Zero)
                {
                    CloseHandle(hPort);
                    barrier.RemoveParticipant();
                    return;
                }

                IntPtr pOverlappedBase = Marshal.AllocHGlobal(32 * queueDepth);

                try
                {
                    if (isWrite)
                    {
                        byte[] pat = new byte[blockSize];
                        for (int b = 0; b < blockSize; b++) pat[b] = (byte)(b ^ (threadId + 1));
                        for (int q = 0; q < queueDepth; q++)
                        {
                            Marshal.Copy(pat, 0, IntPtr.Add(pBaseBuffer, (int)(q * blockSize)), (int)blockSize);
                        }
                    }

                    for (int q = 0; q < queueDepth; q++)
                    {
                        IntPtr pOv = IntPtr.Add(pOverlappedBase, q * 32);
                        Marshal.WriteInt64(pOv, 0, 0);
                        Marshal.WriteInt64(pOv, 8, 0);
                        Marshal.WriteInt64(pOv, 16, 0);
                        Marshal.WriteInt64(pOv, 24, 0);
                    }

                    var entries = new OVERLAPPED_ENTRY[queueDepth];
                    var opStartTicks = new long[queueDepth];
                    var slotInFlight = new bool[queueDepth];
                    int inFlight = 0;

                    void DispatchSlot(int slot)
                    {
                        long blockIdx = isRandom
                            ? Random.Shared.NextInt64(0, totalBlocks)
                            : (Interlocked.Increment(ref globalOffsetCounter) - 1) % totalBlocks;
                        long targetOffset = blockIdx * (long)blockSize;

                        IntPtr pOv = IntPtr.Add(pOverlappedBase, slot * 32);
                        Marshal.WriteInt64(pOv, 0, 0);
                        Marshal.WriteInt64(pOv, 8, 0);
                        Marshal.WriteInt32(pOv, 16, (int)(targetOffset & 0xFFFFFFFF));
                        Marshal.WriteInt32(pOv, 20, (int)((targetOffset >> 32) & 0xFFFFFFFF));
                        Marshal.WriteIntPtr(pOv, 24, IntPtr.Zero);

                        opStartTicks[slot] = Stopwatch.GetTimestamp();
                        IntPtr slotBuffer = IntPtr.Add(pBaseBuffer, (int)(slot * blockSize));

                        bool ok = isWrite
                            ? WriteFile(handle, slotBuffer, blockSize, out _, pOv)
                            : ReadFile(handle, slotBuffer, blockSize, out _, pOv);

                        if (ok || Marshal.GetLastWin32Error() == ERROR_IO_PENDING)
                        {
                            inFlight++;
                            slotInFlight[slot] = true;
                        }
                    }

                    for (int q = 0; q < queueDepth; q++)
                    {
                        DispatchSlot(q);
                    }

                    barrier.SignalAndWait();

                    while (!token.IsCancellationRequested && !stopRequested)
                    {
                        if (inFlight == 0)
                        {
                            for (int q = 0; q < queueDepth; q++)
                            {
                                if (!slotInFlight[q]) DispatchSlot(q);
                            }
                            if (inFlight == 0)
                            {
                                Thread.Sleep(1);
                                continue;
                            }
                        }

                        uint toFetch = (uint)Math.Min(entries.Length, Math.Max(1, inFlight));
                        if (GetQueuedCompletionStatusEx(hPort, entries, toFetch, out uint removed, 50, false))
                        {
                            for (uint i = 0; i < removed; i++)
                            {
                                inFlight--;
                                long diff = entries[i].lpOverlapped.ToInt64() - pOverlappedBase.ToInt64();
                                int slot = (int)(diff / 32);
                                if (slot >= 0 && slot < queueDepth)
                                {
                                    slotInFlight[slot] = false;
                                    long endTick = Stopwatch.GetTimestamp();
                                    long opLatency = endTick - opStartTicks[slot];
                                    uint transferred = entries[i].dwNumberOfBytesTransferred;
                                    if (transferred > 0)
                                    {
                                        Interlocked.Add(ref globalBytesTransferred, transferred);
                                        Interlocked.Increment(ref globalOpsCompleted);
                                        Interlocked.Add(ref globalLatencyTicks, opLatency);
                                    }

                                    if (!stopRequested && !token.IsCancellationRequested)
                                    {
                                        DispatchSlot(slot);
                                    }
                                }
                            }
                        }
                    }

                    CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero);
                    for (int iter = 0; iter < 10 && inFlight > 0; iter++)
                    {
                        if (GetQueuedCompletionStatusEx(hPort, entries, (uint)Math.Min(entries.Length, inFlight), out uint rem, 100, false))
                        {
                            inFlight -= (int)rem;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    if (hPort != IntPtr.Zero) CloseHandle(hPort);
                    if (pOverlappedBase != IntPtr.Zero) Marshal.FreeHGlobal(pOverlappedBase);
                    if (pBaseBuffer != IntPtr.Zero) VirtualFree(pBaseBuffer, 0, MEM_RELEASE);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            threads[t].Start();
        }

        barrier.SignalAndWait();
        sw.Start();

        while (sw.ElapsedTicks < warmupTicks && !token.IsCancellationRequested)
        {
            Thread.Sleep(20);
        }

        Interlocked.Exchange(ref globalBytesTransferred, 0);
        Interlocked.Exchange(ref globalOpsCompleted, 0);
        Interlocked.Exchange(ref globalLatencyTicks, 0);
        sw.Restart();

        while (sw.ElapsedTicks < measureTicks && !token.IsCancellationRequested)
        {
            Thread.Sleep(50);
            double curSec = sw.Elapsed.TotalSeconds;
            double curSpeed = curSec > 0 ? (Interlocked.Read(ref globalBytesTransferred) / 1_000_000.0) / curSec : 0.0;
            double pLocal = Math.Clamp((double)sw.ElapsedTicks / measureTicks, 0.0, 1.0);
            onProgressSub?.Invoke(curSpeed, pLocal);
        }

        stopRequested = true;

        foreach (var t in threads)
        {
            t.Join(1500);
        }

        sw.Stop();
        double elapsedSec = Math.Max(sw.Elapsed.TotalSeconds, 0.0001);
        long finalBytes = Interlocked.Read(ref globalBytesTransferred);
        long finalOps = Interlocked.Read(ref globalOpsCompleted);
        long finalLatencyTicks = Interlocked.Read(ref globalLatencyTicks);

        double finalMBps = (finalBytes / 1_000_000.0) / elapsedSec;
        double finalIops = finalOps / elapsedSec;
        double avgLatencyUs = finalOps > 0 ? (finalLatencyTicks * 1_000_000.0 / Stopwatch.Frequency) / finalOps : 0.0;

        return new IoMeasurement(Math.Round(finalMBps, 2), Math.Round(finalIops, 1), Math.Round(avgLatencyUs, 1));
    }

    public static void RunIoLadderBenchmark(
        DriveSmartReport rep,
        CancellationToken cancellationToken,
        Action<IoBlockLadderProgress>? onProgress,
        bool smokeTestMode = false,
        int testFileSizeBytes = 512 * 1024 * 1024)
    {
        string? targetDir = ResolveBenchmarkDirectory(rep);
        if (targetDir == null)
        {
            RunRawIoLadderBenchmark(rep.DevicePath, rep.Model, cancellationToken, onProgress, smokeTestMode);
            return;
        }
        RunIoLadderBenchmarkInternal(targetDir, rep.Model, cancellationToken, onProgress, smokeTestMode, testFileSizeBytes);
    }

    public static void RunIoLadderBenchmark(
        string driveLetterOrPath,
        string modelName,
        CancellationToken cancellationToken,
        Action<IoBlockLadderProgress>? onProgress,
        bool smokeTestMode = false,
        int testFileSizeBytes = 512 * 1024 * 1024)
    {
        string targetDir = Directory.Exists(driveLetterOrPath)
            ? driveLetterOrPath
            : (Path.GetPathRoot(driveLetterOrPath) != null && Directory.Exists(Path.GetPathRoot(driveLetterOrPath))
                ? Path.GetPathRoot(driveLetterOrPath)!
                : Path.GetTempPath());
        RunIoLadderBenchmarkInternal(targetDir, modelName, cancellationToken, onProgress, smokeTestMode, testFileSizeBytes);
    }

    private static void RunRawIoLadderBenchmark(
        string devicePath,
        string modelName,
        CancellationToken cancellationToken,
        Action<IoBlockLadderProgress>? onProgress,
        bool smokeTestMode)
    {
        var blockSizes = new (int size, string label)[]
        {
            (4096, "4 KB"),
            (8192, "8 KB"),
            (16384, "16 KB"),
            (32768, "32 KB"),
            (65536, "64 KB"),
            (131072, "128 KB"),
            (262144, "256 KB"),
            (524288, "512 KB"),
            (1048576, "1 MB"),
            (2097152, "2 MB"),
            (4194304, "4 MB"),
            (8388608, "8 MB"),
            (16777216, "16 MB"),
            (33554432, "32 MB"),
            (67108864, "64 MB")
        };

        var progress = new IoBlockLadderProgress
        {
            TargetPath = $"{devicePath} [MODO FISICO RAW]",
            ModelName = modelName
        };

        foreach (var (size, label) in blockSizes)
        {
            progress.Steps.Add(new IoBlockLadderStep
            {
                BlockSizeBytes = size,
                Label = label
            });
        }

        var swTotal = Stopwatch.StartNew();
        int stepDurationMs = smokeTestMode ? 50 : 350;
        long targetStepTicks = (long)(stepDurationMs * (Stopwatch.Frequency / 1000.0));

        using var hDevice = CreateFileW(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_NO_BUFFERING,
            IntPtr.Zero);

        if (hDevice.IsInvalid) return;

        for (int i = 0; i < progress.Steps.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var step = progress.Steps[i];
            progress.ActiveStepIndex = i;

            step.WriteMBps = 0.0;
            step.WriteIops = 0.0;

            progress.IsWriting = false;
            progress.OverallPercent = ((i * 2.0) + 1.0) / (progress.Steps.Count * 2.0) * 100.0;
            progress.Elapsed = swTotal.Elapsed;
            onProgress?.Invoke(progress);

            uint allocSize = (uint)step.BlockSizeBytes;
            IntPtr pBuf = VirtualAlloc(IntPtr.Zero, allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (pBuf == IntPtr.Zero) continue;

            try
            {
                long readBytes = 0;
                long readOps = 0;
                var swRead = Stopwatch.StartNew();

                while ((swRead.ElapsedTicks < targetStepTicks || readOps < 4) && !cancellationToken.IsCancellationRequested)
                {
                    if (!ReadFile(hDevice, pBuf, allocSize, out uint bIn, IntPtr.Zero) || bIn == 0)
                    {
                        SetFilePointerEx(hDevice, 0, out _, FILE_BEGIN);
                        continue;
                    }
                    readBytes += bIn;
                    readOps++;
                }
                swRead.Stop();

                double sec = Math.Max(swRead.Elapsed.TotalSeconds, 0.0001);
                step.ReadMBps = (readBytes / 1_000_000.0) / sec;
                step.ReadIops = readOps / sec;
                step.Done = true;

                progress.OverallPercent = ((i + 1) * 2.0) / (progress.Steps.Count * 2.0) * 100.0;
                progress.Elapsed = swTotal.Elapsed;
                onProgress?.Invoke(progress);
            }
            finally
            {
                VirtualFree(pBuf, 0, MEM_RELEASE);
            }
        }
    }

    private static void RunIoLadderBenchmarkInternal(
        string targetDir,
        string modelName,
        CancellationToken cancellationToken,
        Action<IoBlockLadderProgress>? onProgress,
        bool smokeTestMode,
        int testFileSizeBytes)
    {
        var blockSizes = new (int size, string label)[]
        {
            (4096, "4 KB"),
            (8192, "8 KB"),
            (16384, "16 KB"),
            (32768, "32 KB"),
            (65536, "64 KB"),
            (131072, "128 KB"),
            (262144, "256 KB"),
            (524288, "512 KB"),
            (1048576, "1 MB"),
            (2097152, "2 MB"),
            (4194304, "4 MB"),
            (8388608, "8 MB"),
            (16777216, "16 MB"),
            (33554432, "32 MB"),
            (67108864, "64 MB")
        };

        var progress = new IoBlockLadderProgress
        {
            TargetPath = targetDir,
            ModelName = modelName
        };

        foreach (var (size, label) in blockSizes)
        {
            progress.Steps.Add(new IoBlockLadderStep
            {
                BlockSizeBytes = size,
                Label = label
            });
        }

        string tempFile = Path.Combine(targetDir, $"vesper_ioladder_{Guid.NewGuid():N}.tmp");

        var swTotal = Stopwatch.StartNew();
        int stepDurationMs = smokeTestMode ? 30 : 350;
        long targetStepTicks = (long)(stepDurationMs * (Stopwatch.Frequency / 1000.0));

        try
        {
            PrepareTestFile(tempFile, testFileSizeBytes, cancellationToken);

            for (int i = 0; i < progress.Steps.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var step = progress.Steps[i];
                progress.ActiveStepIndex = i;

                progress.IsWriting = true;
                progress.OverallPercent = (i * 2.0) / (progress.Steps.Count * 2.0) * 100.0;
                progress.Elapsed = swTotal.Elapsed;
                onProgress?.Invoke(progress);

                uint allocSize = (uint)step.BlockSizeBytes;
                IntPtr pBuf = VirtualAlloc(IntPtr.Zero, allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (pBuf == IntPtr.Zero) continue;

                try
                {
                    using (var hWrite = CreateFileW(
                        tempFile,
                        GENERIC_WRITE,
                        FILE_SHARE_READ,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_NO_BUFFERING,
                        IntPtr.Zero))
                    {
                        if (!hWrite.IsInvalid)
                        {
                            long written = 0;
                            long writeOps = 0;
                            var swWrite = Stopwatch.StartNew();

                            while ((swWrite.ElapsedTicks < targetStepTicks || writeOps < 4) && !cancellationToken.IsCancellationRequested)
                            {
                                if (!WriteFile(hWrite, pBuf, allocSize, out uint bOut, IntPtr.Zero) || bOut == 0)
                                {
                                    SetFilePointerEx(hWrite, 0, out _, FILE_BEGIN);
                                    continue;
                                }
                                written += bOut;
                                writeOps++;
                            }
                            swWrite.Stop();

                            double sec = Math.Max(swWrite.Elapsed.TotalSeconds, 0.0001);
                            step.WriteMBps = (written / 1_000_000.0) / sec;
                            step.WriteIops = writeOps / sec;
                        }
                    }

                    progress.IsWriting = false;
                    progress.OverallPercent = (i * 2.0 + 1.0) / (progress.Steps.Count * 2.0) * 100.0;
                    progress.Elapsed = swTotal.Elapsed;
                    onProgress?.Invoke(progress);

                    using (var hRead = CreateFileW(
                        tempFile,
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_NO_BUFFERING,
                        IntPtr.Zero))
                    {
                        if (!hRead.IsInvalid)
                        {
                            long readBytes = 0;
                            long readOps = 0;
                            var swRead = Stopwatch.StartNew();

                            while ((swRead.ElapsedTicks < targetStepTicks || readOps < 4) && !cancellationToken.IsCancellationRequested)
                            {
                                if (!ReadFile(hRead, pBuf, allocSize, out uint bIn, IntPtr.Zero) || bIn == 0)
                                {
                                    SetFilePointerEx(hRead, 0, out _, FILE_BEGIN);
                                    continue;
                                }
                                readBytes += bIn;
                                readOps++;
                            }
                            swRead.Stop();

                            double sec = Math.Max(swRead.Elapsed.TotalSeconds, 0.0001);
                            step.ReadMBps = (readBytes / 1_000_000.0) / sec;
                            step.ReadIops = readOps / sec;
                        }
                    }

                    step.Done = true;
                    progress.OverallPercent = ((i + 1) * 2.0) / (progress.Steps.Count * 2.0) * 100.0;
                    progress.Elapsed = swTotal.Elapsed;
                    onProgress?.Invoke(progress);
                }
                finally
                {
                    VirtualFree(pBuf, 0, MEM_RELEASE);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    public static void RunLbaCurveBenchmark(
        string devicePath,
        string modelName,
        long diskSizeBytes,
        uint sectorSize,
        CancellationToken cancellationToken,
        Action<LbaCurveProgress>? onProgress,
        bool smokeTestMode = false)
    {
        if (sectorSize == 0) sectorSize = 512;
        if (diskSizeBytes <= 0) diskSizeBytes = 100L * 1024 * 1024 * 1024;

        var progress = new LbaCurveProgress
        {
            DevicePath = devicePath,
            ModelName = modelName
        };

        uint chunkSize = 4 * 1024 * 1024;
        if (chunkSize % sectorSize != 0)
            chunkSize = ((chunkSize / sectorSize) + 1) * sectorSize;

        IntPtr pBuf = VirtualAlloc(IntPtr.Zero, chunkSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (pBuf == IntPtr.Zero) return;

        var swTotal = Stopwatch.StartNew();
        int sampleDurationMs = smokeTestMode ? 20 : 50;
        long sampleTicks = (long)(sampleDurationMs * (Stopwatch.Frequency / 1000.0));
        long minBytesPerPoint = smokeTestMode ? (4L * 1024 * 1024) : (16L * 1024 * 1024);

        try
        {
            using var hDevice = CreateFileW(
                devicePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (hDevice.IsInvalid) return;

            double totalSpeed = 0;
            double totalLatency = 0;
            int validPoints = 0;

            for (int pct = 0; pct <= 99; pct++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                long targetByteOffset = (long)((pct / 100.0) * diskSizeBytes);
                if (targetByteOffset + chunkSize > diskSizeBytes)
                    targetByteOffset = Math.Max(0, diskSizeBytes - chunkSize);

                targetByteOffset = (targetByteOffset / sectorSize) * sectorSize;
                long startLba = targetByteOffset / sectorSize;

                SetFilePointerEx(hDevice, targetByteOffset, out _, FILE_BEGIN);

                long startTimestamp = Stopwatch.GetTimestamp();
                long bytesInSample = 0;

                while ((Stopwatch.GetTimestamp() - startTimestamp < sampleTicks || bytesInSample < minBytesPerPoint) && !cancellationToken.IsCancellationRequested)
                {
                    if (targetByteOffset + bytesInSample + chunkSize > diskSizeBytes) break;
                    bool ok = ReadFile(hDevice, pBuf, chunkSize, out uint bytesRead, IntPtr.Zero);
                    if (!ok || bytesRead == 0) break;
                    bytesInSample += bytesRead;
                }

                long endTimestamp = Stopwatch.GetTimestamp();
                double readSec = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                double latencyMs = (bytesInSample > 0) ? (readSec / (bytesInSample / (double)chunkSize)) * 1000.0 : 0.0;
                double speedMBps = (bytesInSample > 0 && readSec > 0) ? (bytesInSample / 1_000_000.0) / readSec : 0.0;

                if (speedMBps > 0)
                {
                    validPoints++;
                    totalSpeed += speedMBps;
                    totalLatency += latencyMs;

                    if (speedMBps < progress.MinSpeedMBps) progress.MinSpeedMBps = speedMBps;
                    if (speedMBps > progress.MaxSpeedMBps) progress.MaxSpeedMBps = speedMBps;
                }

                progress.CurrentPercentile = pct;
                progress.CurrentSpeedMBps = speedMBps;
                progress.AvgSpeedMBps = validPoints > 0 ? (totalSpeed / validPoints) : 0.0;
                progress.Elapsed = swTotal.Elapsed;

                progress.Points.Add(new LbaCurvePoint
                {
                    Percentile = pct,
                    Lba = startLba,
                    SpeedMBps = Math.Round(speedMBps, 1),
                    LatencyMs = Math.Round(latencyMs, 2)
                });

                onProgress?.Invoke(progress);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                uint seekBlockSize = 4096;
                IntPtr pSeekBuf = VirtualAlloc(IntPtr.Zero, seekBlockSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (pSeekBuf != IntPtr.Zero)
                {
                    try
                    {
                        int seekCount = smokeTestMode ? 10 : 300;
                        long totalSeekTicks = 0;
                        int validSeeks = 0;

                        for (int s = 0; s < seekCount; s++)
                        {
                            long randLba = (long)(Random.Shared.NextDouble() * (diskSizeBytes / sectorSize - 8));
                            long offset = randLba * sectorSize;
                            offset = (offset / sectorSize) * sectorSize;

                            SetFilePointerEx(hDevice, offset, out _, FILE_BEGIN);
                            long sStart = Stopwatch.GetTimestamp();
                            if (ReadFile(hDevice, pSeekBuf, seekBlockSize, out uint bRead, IntPtr.Zero) && bRead > 0)
                            {
                                long sEnd = Stopwatch.GetTimestamp();
                                totalSeekTicks += (sEnd - sStart);
                                validSeeks++;
                            }
                        }

                        if (validSeeks > 0)
                        {
                            progress.AccessTimeMs = (totalSeekTicks * 1000.0 / Stopwatch.Frequency) / validSeeks;
                        }
                    }
                    finally
                    {
                        VirtualFree(pSeekBuf, 0, MEM_RELEASE);
                    }
                }

                uint burstBlockSize = 128 * 1024;
                IntPtr pBurstBuf = VirtualAlloc(IntPtr.Zero, burstBlockSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (pBurstBuf != IntPtr.Zero)
                {
                    try
                    {
                        int burstCount = smokeTestMode ? 10 : 300;
                        long burstBytes = 0;
                        long burstStart = Stopwatch.GetTimestamp();
                        for (int b = 0; b < burstCount; b++)
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            SetFilePointerEx(hDevice, 0, out _, FILE_BEGIN);
                            if (ReadFile(hDevice, pBurstBuf, burstBlockSize, out uint bRead, IntPtr.Zero) && bRead > 0)
                            {
                                burstBytes += bRead;
                            }
                        }
                        long burstEnd = Stopwatch.GetTimestamp();
                        double burstSec = (burstEnd - burstStart) / (double)Stopwatch.Frequency;
                        if (burstSec > 0 && burstBytes > 0)
                        {
                            progress.BurstRateMBps = (burstBytes / 1_000_000.0) / burstSec;
                        }
                    }
                    finally
                    {
                        VirtualFree(pBurstBuf, 0, MEM_RELEASE);
                    }
                }
            }
        }
        finally
        {
            VirtualFree(pBuf, 0, MEM_RELEASE);
        }
    }
}
