using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

internal static class Program
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint SMART_SEND_DRIVE_COMMAND = 0x0007C084;
    private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    private const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x002D5140;
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_LENGTH_INFORMATION
    {
        public long Length;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.I1)]
        public bool RemovableMedia;
        [MarshalAs(UnmanagedType.I1)]
        public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;
        public uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_GEOMETRY
    {
        public long Cylinders;
        public int MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct IDEREGS
    {
        public byte bFeaturesReg;
        public byte bSectorCountReg;
        public byte bSectorNumberReg;
        public byte bCylLowReg;
        public byte bCylHighReg;
        public byte bDriveHeadReg;
        public byte bCommandReg;
        public byte bReserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SENDCMDINPARAMS
    {
        public uint cBufferSize;
        public IDEREGS irDriveRegs;
        public byte bDriveNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] bReserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] dwReserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ATA_PASS_THROUGH_EX
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public IntPtr DataBufferOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PreviousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] CurrentTaskFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
    {
        public SCSI_PASS_THROUGH_DIRECT Spt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseBuf;
    }

    internal static bool SilentMode = false;
    internal static bool VerboseMode = false;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetErrorMode(uint uMode);
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryElevate(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0],
                Arguments = string.Join(" ", args),
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Main(string[] args)
    {
        try
        {
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
            Console.OutputEncoding = Encoding.UTF8;

            foreach (var arg in args)
            {
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase) || arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return;
                }
                if (arg.Equals("--verify-all", StringComparison.OrdinalIgnoreCase))
                {
                    var verifyReports = StoragePipeline.ExecutePipeline(
                        () => ScanDirectPhysicalDrives(null, false, false),
                        () => RaidStorageEngine.ScanRaidAdapters()
                    );
                    TuiIndustrialEngine.RunAutomatedVerification(verifyReports, fullSurface: false);

                    string artDir = @"C:\Users\Zappetinho\.gemini\antigravity\brain\7a0c14b4-3553-4dab-903c-064ccab2fca9";
                    if (!Directory.Exists(artDir)) artDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
                    string gDrive = @"G:\Meu Drive\VESPER_SCREENSHOTS";
                    ScreenshotRenderer.GenerateAllScreenshots(verifyReports, artDir, gDrive);
                    return;
                }
                if (arg.Equals("--verify-all-full", StringComparison.OrdinalIgnoreCase))
                {
                    bool fullAutoYes = args.Any(a => a.Equals("-y", StringComparison.OrdinalIgnoreCase) || a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
                    var verifyReports = StoragePipeline.ExecutePipeline(
                        () => ScanDirectPhysicalDrives(null, false, false),
                        () => RaidStorageEngine.ScanRaidAdapters()
                    );

                    long totalBytes = verifyReports.Sum(r => r.DiskSizeBytes);
                    double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
                    if (!fullAutoYes && !Console.IsInputRedirected && totalGb > 50.0)
                    {
                        double totalEstSec = 0;
                        foreach (var rep in verifyReports)
                        {
                            var (_, _, _, _, fSec) = SurfaceTestEngine.EstimateDriveSurfaceMetrics(rep);
                            totalEstSec += fSec;
                        }
                        double estMins = totalEstSec / 60.0;
                        Console.WriteLine("\n[EXHAUSTIVE 100% LBA SCAN WARNING]");
                        Console.WriteLine($"Total combined capacity: {totalGb:F1} GB across {verifyReports.Count} drive(s).");
                        Console.WriteLine($"Estimated time: ~{estMins:F0} minutes ({(estMins / 60.0):F1} hours).");
                        Console.Write("Are you sure you want to run exhaustive linear 100% scan? (Y/N): ");
                        var resp = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(resp) || (!resp.StartsWith("s", StringComparison.OrdinalIgnoreCase) && !resp.StartsWith("y", StringComparison.OrdinalIgnoreCase)))
                        {
                            Console.WriteLine("Operation cancelled by user.");
                            return;
                        }
                    }

                    TuiIndustrialEngine.RunAutomatedVerification(verifyReports, fullSurface: true);

                    string artDir = @"C:\Users\Zappetinho\.gemini\antigravity\brain\7a0c14b4-3553-4dab-903c-064ccab2fca9";
                    if (!Directory.Exists(artDir)) artDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
                    string gDrive = @"G:\Meu Drive\VESPER_SCREENSHOTS";
                    ScreenshotRenderer.GenerateAllScreenshots(verifyReports, artDir, gDrive);
                    return;
                }
                if (arg.Equals("--generate-screenshots", StringComparison.OrdinalIgnoreCase))
                {
                    var shotReports = StoragePipeline.ExecutePipeline(
                        () => ScanDirectPhysicalDrives(null, false, false),
                        () => RaidStorageEngine.ScanRaidAdapters()
                    );
                    string artDir = @"C:\Users\Zappetinho\.gemini\antigravity\brain\7a0c14b4-3553-4dab-903c-064ccab2fca9";
                    if (!Directory.Exists(artDir)) artDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
                    string gDrive = @"G:\Meu Drive\VESPER_SCREENSHOTS";
                    ScreenshotRenderer.GenerateAllScreenshots(shotReports, artDir, gDrive);
                    return;
                }
            }

            bool isInteractiveTui = args.Length == 0 || (args.Length == 1 && args[0].Equals("--tui", StringComparison.OrdinalIgnoreCase));

            if (isInteractiveTui && !IsAdministrator() && !Console.IsOutputRedirected)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0],
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
                    };
                    var proc = Process.Start(psi);
                    if (proc != null) return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            if (args.Length == 0 || (args.Length == 1 && args[0].Equals("--tui", StringComparison.OrdinalIgnoreCase)))
            {
                var reports = StoragePipeline.ExecutePipeline(
                    () => ScanDirectPhysicalDrives(null, false, false),
                    () => RaidStorageEngine.ScanRaidAdapters()
                );

                TuiIndustrialEngine.Run(reports);
                return;
            }

            if (!IsAdministrator())
            {
                Console.WriteLine("WARNING: Administrator privileges not detected. Direct kernel access requires elevation (UAC).\n");
            }

        bool jsonOutput = false;
        bool strictMode = false;
        bool scanOnly = false;
        bool dumpRaw = false;
        bool surfaceTest = false;
        bool fullScan = false;
        bool autoYes = false;
        bool selftestLog = false;
        bool seekTest = false;
        bool benchmarkTest = false;
        string? selfTestArg = null;
        int? targetDrive = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase) || arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return;
            }
            else if (arg.Equals("--tui", StringComparison.OrdinalIgnoreCase))
            {
                var reports = StoragePipeline.ExecutePipeline(
                    () => ScanDirectPhysicalDrives(null, false, false),
                    () => RaidStorageEngine.ScanRaidAdapters()
                );
                TuiIndustrialEngine.Run(reports);
                return;
            }
            else if (arg.Equals("--surface", StringComparison.OrdinalIgnoreCase))
                surfaceTest = true;
            else if (arg.Equals("--surface-quick", StringComparison.OrdinalIgnoreCase))
            {
                surfaceTest = true;
                fullScan = false;
            }
            else if (arg.Equals("--surface-full", StringComparison.OrdinalIgnoreCase))
            {
                surfaceTest = true;
                fullScan = true;
            }
            else if (arg.Equals("--full", StringComparison.OrdinalIgnoreCase))
                fullScan = true;
            else if (arg.Equals("-y", StringComparison.OrdinalIgnoreCase) || arg.Equals("--yes", StringComparison.OrdinalIgnoreCase))
                autoYes = true;
            else if (arg.Equals("--selftest-log", StringComparison.OrdinalIgnoreCase) || arg.Equals("--log", StringComparison.OrdinalIgnoreCase))
                selftestLog = true;
            else if (arg.Equals("--seek", StringComparison.OrdinalIgnoreCase))
                seekTest = true;
            else if (arg.Equals("--benchmark", StringComparison.OrdinalIgnoreCase))
                benchmarkTest = true;
            else if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                selfTestArg = args[++i];
            }
            else if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase) || arg.Equals("-j", StringComparison.OrdinalIgnoreCase))
                jsonOutput = true;
            else if (arg.Equals("--strict", StringComparison.OrdinalIgnoreCase))
                strictMode = true;
            else if (arg.Equals("--scan", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                scanOnly = true;
            else if (arg.Equals("--raw", StringComparison.OrdinalIgnoreCase) || arg.Equals("-r", StringComparison.OrdinalIgnoreCase))
                dumpRaw = true;
            else if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
                VerboseMode = true;
            else if (int.TryParse(arg, out int idx))
                targetDrive = idx;
        }

        SilentMode = jsonOutput;

        if (selftestLog)
        {
            RunCliSelfTestLog(targetDrive ?? 0);
            return;
        }

        if (surfaceTest)
        {
            RunCliSurfaceTest(targetDrive ?? 0, fullScan, autoYes);
            return;
        }

        if (benchmarkTest)
        {
            var benchReports = StoragePipeline.ExecutePipeline(
                () => ScanDirectPhysicalDrives(targetDrive, strictMode, false),
                () => RaidStorageEngine.ScanRaidAdapters()
            );
            if (benchReports.Count > 0)
            {
                var rep = benchReports[0];
                Console.WriteLine($"\n[BENCHMARK] Running Default Profile on {rep.Model} ({rep.DevicePath})...\n");
                using var cts = new CancellationTokenSource();
                BenchmarkProgress? lastP = null;
                VesperBenchmarkEngine.RunBenchmark(rep, cts.Token, p =>
                {
                    lastP = p;
                    Console.Write($"\rProgress: {p.OverallPercent:F1}% | {p.CurrentOperationDescription,-30} | {p.CurrentSpeedMBps,8:F1} MB/s   ");
                }, smokeTestMode: false);
                Console.WriteLine("\n\n[BENCHMARK RESULTS]:");
                if (lastP != null)
                {
                    foreach (var itm in lastP.Items)
                    {
                        Console.WriteLine($"  {itm.Name,-18} | Read: {itm.ReadMBps,8:F1} MB/s ({itm.ReadIops,8:F0} IOPS, {itm.ReadLatencyUs,6:F1} us) | Write: {itm.WriteMBps,8:F1} MB/s ({itm.WriteIops,8:F0} IOPS, {itm.WriteLatencyUs,6:F1} us)");
                    }
                }
            }
            return;
        }
        if (surfaceTest)
        {
            RunCliSurfaceTest(targetDrive ?? 0, fullScan);
            return;
        }

        if (seekTest)
        {
            RunCliSeekTest(targetDrive ?? 0);
            return;
        }

        if (selfTestArg != null)
        {
            RunCliSelfTest(targetDrive ?? 0, selfTestArg);
            return;
        }

        if (!jsonOutput && !scanOnly)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("  VESPER 1.1.0  \u2022  @_fxgiovane");
            Console.WriteLine("  Storage Telemetry & Diagnostics");
            Console.WriteLine("================================================================================\n");
        }

        if (scanOnly)
        {
            ScanDirectPhysicalDrives(targetDrive, strictMode, true);
            return;
        }

        var allReports = StoragePipeline.ExecutePipeline(
            () => ScanDirectPhysicalDrives(targetDrive, strictMode, false),
            () => RaidStorageEngine.ScanRaidAdapters()
        );

        if (targetDrive.HasValue)
        {
            allReports = allReports.FindAll(r => r.DevicePath.EndsWith($"PhysicalDrive{targetDrive.Value}", StringComparison.OrdinalIgnoreCase) || r.DevicePath.EndsWith($"{targetDrive.Value}", StringComparison.OrdinalIgnoreCase));
        }

        if (!SilentMode)
        {
            foreach (var rep in allReports)
            {
                PrintVesperReport(rep, dumpRaw);
                Console.WriteLine();
            }
        }

        if (jsonOutput)
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(allReports, options));
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n[INITIALIZATION ERROR]: {ex.Message}\n{ex.StackTrace}");
        Console.ResetColor();
        Console.WriteLine("\nPress any key to exit...");
        try { Console.ReadKey(true); } catch { }
    }
}

    private static void RunCliSurfaceTest(int driveIndex, bool fullScan = false, bool autoYes = false)
    {
        string devPath = $@"\\.\PhysicalDrive{driveIndex}";
        string modeDesc = fullScan ? "Full Linear 100% LBA" : "Quick Dense Sampling";

        long diskSize = 0;
        uint bytesPerSector = 512;
        using (SafeFileHandle h = CreateFile(devPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (!h.IsInvalid)
            {
                QueryDiskGeometry(h, out diskSize, out bytesPerSector);
            }
        }

        if (fullScan)
        {
            double capGb = diskSize / (1024.0 * 1024.0 * 1024.0);
            double estMinutes = (capGb * 1024.0) / (150.0 * 60.0);
            if (!autoYes && !Console.IsInputRedirected && capGb > 50.0)
            {
                Console.WriteLine("\n[EXHAUSTIVE 100% LBA SCAN WARNING]");
                Console.WriteLine($"Drive: {devPath} | Total Capacity: {capGb:F1} GB");
                Console.WriteLine($"Estimated Time: ~{estMinutes:F0} minutes ({(estMinutes / 60.0):F1} hours).");
                Console.Write("Are you sure you want to run exhaustive linear 100% scan? (Y/N): ");
                var resp = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(resp) || (!resp.StartsWith("s", StringComparison.OrdinalIgnoreCase) && !resp.StartsWith("y", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("Operation cancelled by user.");
                    return;
                }
            }
        }

        Console.WriteLine($"Starting Surface Scan ({modeDesc}) on {devPath}...");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        SurfaceTestEngine.RunTest(
            devPath,
            diskSize,
            bytesPerSector,
            350,
            cts.Token,
            (res, prog) =>
            {
                Console.Write($"\rBlock {prog.CurrentBlock,4}/{prog.TotalBlocks} [{prog.Percent,5:F1}%] Latency: {res.LatencyMs,6:F2} ms | Speed: {prog.CurrentSpeedMBps,6:F1} MB/s | Avg: {prog.AvgSpeedMBps,6:F1} MB/s | Health: {prog.SurfaceHealthPercent:F1}%");
            },
            coverage: fullScan ? SurfaceScanCoverage.FullLinear : SurfaceScanCoverage.QuickDense);

        Console.WriteLine("\nSurface scan complete.");
    }

    private static void RunCliSelfTestLog(int driveIndex)
    {
        var reports = StoragePipeline.ExecutePipeline(
            () => ScanDirectPhysicalDrives(driveIndex, false, false),
            () => RaidStorageEngine.ScanRaidAdapters()
        );

        if (reports.Count == 0)
        {
            Console.WriteLine($"[FIRMWARE LOG] No drive found with index {driveIndex}.");
            return;
        }

        var rep = reports[0];
        Console.WriteLine("\n================================================================================");
        Console.WriteLine($"  FIRMWARE SELF-TEST HISTORY (LOG 0x06)");
        Console.WriteLine($"  Device: {rep.Model} ({rep.DevicePath}) | Interface: {rep.BusTypeName}");
        Console.WriteLine("================================================================================\n");

        var entries = FirmwareSelfTestEngine.QuerySelfTestLog(rep);
        if (entries == null || entries.Count == 0)
        {
            Console.WriteLine("  No self-tests logged in firmware non-volatile memory on this drive.\n");
            return;
        }

        Console.WriteLine("  +----+----------+-------------------------------------+------------+--------------------+");
        Console.WriteLine("  | #  | Type     | Diagnostic Result                   | POH Hours  | Failing LBA        |");
        Console.WriteLine("  +----+----------+-------------------------------------+------------+--------------------+");
        foreach (var ent in entries)
        {
            string lbaStr = ent.HasFailingLba ? $"0x{ent.FailingLba:X10}" : "None (OK)";
            Console.WriteLine($"  | {ent.Index,2} | {ent.TestType,-8} | {ent.ResultMessage,-35} | {ent.PowerOnHours,7:N0} h   | {lbaStr,-18} |");
        }
        Console.WriteLine("  +----+----------+-------------------------------------+------------+--------------------+\n");
    }

    private static void RunCliSeekTest(int driveIndex)
    {
        string devPath = $@"\\.\PhysicalDrive{driveIndex}";
        Console.WriteLine($"Starting Random Seek Test (QPC IOPS) on {devPath} (dynamic calibration)...");

        long diskSize = 0;
        uint bytesPerSector = 512;
        using (SafeFileHandle h = CreateFile(devPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (!h.IsInvalid)
            {
                QueryDiskGeometry(h, out diskSize, out bytesPerSector);
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        RandomSeekTestEngine.RunTest(
            devPath,
            diskSize,
            bytesPerSector,
            0,
            cts.Token,
            (sample, prog) =>
            {
                if (prog.OperationsCompleted % 50 == 0 || prog.OperationsCompleted == prog.TotalOperationsTarget)
                {
                    Console.Write($"\rOp {prog.OperationsCompleted,4} | Seek: {prog.CurrentSeekMs,6:F2} ms | Min: {prog.MinSeekMs,6:F2} ms | Avg: {prog.AvgSeekMs,6:F2} ms | Max: {prog.MaxSeekMs,6:F2} ms | {prog.CurrentIops,5:F0} IOPS");
                }
            },
            throttleMs: 0,
            durationMs: 8000);

        Console.WriteLine("\nRandom Seek Test complete.");
    }

    private static void RunCliSelfTest(int driveIndex, string modeArg)
    {
        string devPath = $@"\\.\PhysicalDrive{driveIndex}";
        var mode = modeArg.ToLowerInvariant() switch
        {
            "short" => SelfTestMode.Short,
            "extended" => SelfTestMode.Extended,
            "abort" => SelfTestMode.Abort,
            _ => SelfTestMode.Short
        };

        Console.WriteLine($"Dispatching {mode} diagnostic command to {devPath}...");
        bool ok = FirmwareSelfTestEngine.ExecuteSelfTest(devPath, false, false, mode, out string msg);
        Console.WriteLine(ok ? $"[SUCCESS]: {msg}" : $"[FAILED]: {msg}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  VESPER 1.1.0  \u2022  @_fxgiovane");
        Console.WriteLine("  Storage Telemetry & Diagnostics");
        Console.WriteLine("================================================================================");
        Console.WriteLine("USAGE:");
        Console.WriteLine("  Vesper.exe [options] [drive_index]\n");
        Console.WriteLine("COMMANDS & OPTIONS:");
        Console.WriteLine("  (none)          Launch interactive Terminal User Interface (TUI).");
        Console.WriteLine("  --tui           Force interactive TUI mode.");
        Console.WriteLine("  --verify-all    Runs non-interactive verification across all drives (Quick 300 zones).");
        Console.WriteLine("  --verify-all-full Runs exhaustive linear 100% LBA verification across all drives.");
        Console.WriteLine("  --surface       Runs quick surface scan (Unbuffered Direct Read).");
        Console.WriteLine("  --surface-quick Runs quick surface sampling (300 zones).");
        Console.WriteLine("  --surface-full  Runs exhaustive 100% LBA linear surface scan.");
        Console.WriteLine("  --seek          Runs random seek test (QPC Latency & IOPS calibration).");
        Console.WriteLine("  --benchmark     Runs multi-profile continuous throughput & IOPS test.");
        Console.WriteLine("  --selftest <m>  Dispatches firmware diagnostic command (short, extended, abort).");
        Console.WriteLine("  --selftest-log  Displays firmware self-test execution log table.");
        Console.WriteLine("  <number>        Physical drive index (e.g. 0 for PhysicalDrive0).");
        Console.WriteLine("  --raw, -r       Dumps raw 512-byte hex payload from hardware.");
        Console.WriteLine("  --json, -j      Exports full report and attributes in structured JSON.");
        Console.WriteLine("  --scan, -s      Fast 1-line drive inventory (model, serial, capacity).");
        Console.WriteLine("  --strict        Enforces conservative degradation model for mission-critical servers.");
        Console.WriteLine("  --verbose, -v   Shows low-level operational logs across detection tiers.");
        Console.WriteLine("  -y, --yes       Auto-confirms long-running exhaustive scans without prompting.");
        Console.WriteLine("  --help, -h, /?  Displays this help menu.\n");
        Console.WriteLine("EXAMPLES:");
        Console.WriteLine("  .\\Vesper.exe                   # Full interactive dashboard for all drives");
        Console.WriteLine("  .\\Vesper.exe 0                 # Report for physical drive 0 only");
        Console.WriteLine("  .\\Vesper.exe --verify-all      # Automated verification across all drives");
        Console.WriteLine("  .\\Vesper.exe 0 --surface-quick # Quick 300-zone surface test on drive 0");
        Console.WriteLine("  .\\Vesper.exe 0 --surface-full -y # Full 100% LBA surface scan on drive 0");
        Console.WriteLine("  .\\Vesper.exe --json            # Structured JSON output to stdout");
        Console.WriteLine("  .\\Vesper.exe --scan            # Quick drive inventory");
        Console.WriteLine("================================================================================");
    }

    internal static List<DriveSmartReport> ScanDirectPhysicalDrives(int? targetDrive, bool strictMode, bool scanOnly)
    {
        var directReports = new List<DriveSmartReport>();
        int consecutiveFailures = 0;
        int startIdx = targetDrive ?? 0;
        int maxIdx = targetDrive.HasValue ? targetDrive.Value + 1 : 16;

        for (int driveIndex = startIdx; driveIndex < maxIdx; driveIndex++)
        {
            string devicePath = $@"\\.\PhysicalDrive{driveIndex}";
            using var handle = CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                consecutiveFailures++;
                if (!targetDrive.HasValue && consecutiveFailures >= 2 && driveIndex >= 2) break;
                continue;
            }
            consecutiveFailures = 0;

            QueryDeviceDescriptor(handle, out uint busType, out string vendor, out string product, out string serial, out bool isRemovable);
            QueryDiskGeometry(handle, out long diskSize, out uint bytesPerSector);
            string fullModel = $"{vendor} {product}".Trim();

            if (isRemovable || IsPendriveOrUsbFlash(busType, fullModel, diskSize))
            {
                continue;
            }
            string busTypeName = GetBusTypeName(busType);

            if (scanOnly)
            {
                Console.WriteLine($"{devicePath} | {fullModel} | {serial} | {busTypeName} | {diskSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
                continue;
            }

            bool isSsd = fullModel.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                         fullModel.Contains("SanDisk", StringComparison.OrdinalIgnoreCase) ||
                         fullModel.Contains("Kingston", StringComparison.OrdinalIgnoreCase) ||
                         fullModel.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ||
                         fullModel.Contains("Crucial", StringComparison.OrdinalIgnoreCase) ||
                         busType == 17;

            var report = new DriveSmartReport
            {
                DevicePath = devicePath,
                Model = fullModel,
                Serial = serial,
                BusType = busType,
                BusTypeName = busTypeName,
                DiskSizeBytes = diskSize,
                BytesPerSector = bytesPerSector > 0 ? bytesPerSector : 512,
                IsSsd = isSsd,
                IsNvme = busType == 17
            };

            bool readOk = false;

            if (busType == 17)
            {
                readOk = ReadNvmeSmart(handle, report);
            }

            if (!readOk && busType == 7)
            {
                byte[] idBuf = new byte[512];
                if (ReadAtaIdentifyViaScsiSat(handle, idBuf))
                {
                    ParseAtaIdentifyPayload(idBuf, report);
                }
                readOk = ReadAtaSmartViaScsiSat(handle, report);
            }

            if (!readOk)
            {
                readOk = ReadAtaSmartViaLegacy(handle, (byte)driveIndex, report);
            }

            if (!readOk)
            {
                readOk = ReadAtaSmartViaPassThrough(handle, report);
            }

            if (readOk)
            {
                if (string.IsNullOrEmpty(report.FirmwareRevision))
                {
                    byte[] idBuf = new byte[512];
                    if (ReadAtaIdentifyViaScsiSat(handle, idBuf))
                    {
                        ParseAtaIdentifyPayload(idBuf, report);
                    }
                }
                VesperHealthCalculator.EvaluateDrive(report);
                if (strictMode) report.HealthDefault = report.HealthMoreStrict;
                directReports.Add(report);
            }
            else if (busType != 7 && (diskSize > 0 || !string.IsNullOrEmpty(report.Model)))
            {
                report.HealthDefault = 100.0;
                report.HealthMoreStrict = 100.0;
                report.Performance = 100.0;
                report.RemainingLifetimeText = "More than 1000 days (No degradation telemetry)";
                report.HealthVerdictText = "SMART telemetry unavailable on this bus or privilege level.";
                directReports.Add(report);
            }
        }

        return directReports;
    }

    private static void PrintVesperReport(DriveSmartReport report, bool dumpRaw)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine($"DRIVE: {report.DevicePath} | MODEL: {report.Model}");
        Console.WriteLine($"SERIAL: {report.Serial} | INTERFACE: {report.BusTypeName} | CAPACITY: {report.DiskSizeGbBinary:F2} GB");
        if (report.IsRaidMember)
        {
            Console.WriteLine($"RAID: Slot {report.RaidSlotNumber} ({report.RaidControllerName})");
        }
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Health (VESPER)       : {report.HealthDefault:F0} %");
        Console.WriteLine($"Performance           : {report.Performance:F0} %");
        Console.WriteLine($"Temperature           : {report.TemperatureCelsius} C");
        Console.WriteLine($"Power On Time         : {report.PowerOnHours:N0} h ({report.PowerOnHours / 24:N0} days) | Cycles: {report.PowerCycleCount:N0}");
        if (report.TotalTbWritten > 0.0)
            Console.WriteLine($"Total Written (TBW)   : {report.TotalTbWritten:F2} TB");
        if (report.TotalTbRead > 0.0)
            Console.WriteLine($"Total Read (TBR)      : {report.TotalTbRead:F2} TB");
        Console.WriteLine($"Estimated Lifetime    : {report.RemainingLifetimeText}");
        Console.WriteLine($"Diagnosis             : {report.HealthVerdictText}");
        Console.WriteLine("--------------------------------------------------------------------------------");

        if (VerboseMode && report.BreakdownLogs.Count > 0)
        {
            Console.WriteLine("\n[VESPER MATHEMATICAL BREAKDOWN]");
            foreach (var log in report.BreakdownLogs)
            {
                Console.WriteLine(log);
            }
        }

        if (report.Attributes.Count > 0)
        {
            if (report.IsNvme)
            {
                Console.WriteLine("\n[NVMe S.M.A.R.T. TELEMETRY (LOG PAGE 0x02)]");
                Console.WriteLine("+------+----------------------------------+-----------------+------------------+");
                Console.WriteLine("| ID   | NVMe Metric                      | Raw (Hex)       | Value / Detail   |");
                Console.WriteLine("+------+----------------------------------+-----------------+------------------+");
                foreach (var a in report.Attributes)
                {
                    string name = a.Name.Length > 32 ? a.Name.Substring(0, 32) : a.Name;
                    string interp = a.Interpretation.Length > 16 ? a.Interpretation.Substring(0, 16) : a.Interpretation;
                    Console.WriteLine($"| 0x{a.Id:X2} | {name,-32} | {a.RawHex,-15} | {interp,-16} |");
                }
                Console.WriteLine("+------+----------------------------------+-----------------+------------------+");
            }
            else
            {
                Console.WriteLine("\n[PHYSICAL S.M.A.R.T. ATTRIBUTES (ATA / SATA)]");
                Console.WriteLine("+------+-------------------------------+-------+------+-------------------------+");
                Console.WriteLine("| ID   | Attribute                     | Curr  | Worst| Raw (Hex / Dec)         |");
                Console.WriteLine("+------+-------------------------------+-------+------+-------------------------+");
                foreach (var a in report.Attributes)
                {
                    string name = a.Name.Length > 29 ? a.Name.Substring(0, 29) : a.Name;
                    string rawValStr = a.RawValue > 999999999 ? $"{a.RawValue / 1_000_000:N0}M" : $"{a.RawValue:N0}";
                    string rawCombo = $"{a.RawHex} ({rawValStr})";
                    if (rawCombo.Length > 23) rawCombo = rawCombo.Substring(0, 23);
                    Console.WriteLine($"| 0x{a.Id:X2} | {name,-29} | {a.Current,5} | {a.Worst,4} | {rawCombo,-23} |");
                }
                Console.WriteLine("+------+-------------------------------+-------+------+-------------------------+");
            }
        }

        if (dumpRaw && report.RawSmartPayload != null && report.RawSmartPayload.Length > 0)
        {
            Console.WriteLine("\n[HARDWARE RAW HEX DUMP (512 BYTES)]");
            Console.WriteLine("Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ASCII");
            Console.WriteLine("--------  -----------------------------------------------  ----------------");
            for (int r = 0; r < report.RawSmartPayload.Length; r += 16)
            {
                var hexPart = new System.Text.StringBuilder();
                var asciiPart = new System.Text.StringBuilder();
                for (int c = 0; c < 16; c++)
                {
                    if (r + c < report.RawSmartPayload.Length)
                    {
                        byte b = report.RawSmartPayload[r + c];
                        hexPart.Append($"{b:X2} ");
                        if (c == 7) hexPart.Append(" ");
                        asciiPart.Append(b >= 32 && b <= 126 ? (char)b : '.');
                    }
                    else
                    {
                        hexPart.Append("   ");
                        if (c == 7) hexPart.Append(" ");
                    }
                }
                Console.WriteLine($"0x{r:X4}    {hexPart.ToString().PadRight(49)}  {asciiPart}");
            }
        }
    }

    private static void QueryDiskGeometry(SafeFileHandle handle, out long diskSize, out uint bytesPerSector)
    {
        diskSize = 0;
        bytesPerSector = 512;

        int lenSize = Marshal.SizeOf<GET_LENGTH_INFORMATION>();
        IntPtr pLen = Marshal.AllocHGlobal(lenSize);
        try
        {
            if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, pLen, (uint)lenSize, out _, IntPtr.Zero))
            {
                var lenInfo = Marshal.PtrToStructure<GET_LENGTH_INFORMATION>(pLen);
                if (lenInfo.Length > 0)
                {
                    diskSize = lenInfo.Length;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pLen);
        }

        int size = Marshal.SizeOf<DISK_GEOMETRY>();
        IntPtr pOut = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, pOut, (uint)size, out _, IntPtr.Zero))
            {
                var geo = Marshal.PtrToStructure<DISK_GEOMETRY>(pOut);
                bytesPerSector = geo.BytesPerSector > 0 ? geo.BytesPerSector : 512;
                if (diskSize == 0)
                {
                    diskSize = geo.Cylinders * geo.TracksPerCylinder * geo.SectorsPerTrack * bytesPerSector;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pOut);
        }
    }

    private static void QueryDeviceDescriptor(SafeFileHandle handle, out uint busType, out string vendor, out string product, out string serial, out bool isRemovable)
    {
        busType = 0;
        vendor = "";
        product = "";
        serial = "";
        isRemovable = false;

        var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
        int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
        IntPtr pQuery = Marshal.AllocHGlobal(querySize);
        Marshal.StructureToPtr(query, pQuery, false);

        int bufferSize = 1024;
        IntPtr pOut = Marshal.AllocHGlobal(bufferSize);

        try
        {
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, pQuery, (uint)querySize, pOut, (uint)bufferSize, out uint returned, IntPtr.Zero))
            {
                var desc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(pOut);
                busType = desc.BusType;
                isRemovable = desc.RemovableMedia;

                byte[] raw = new byte[returned];
                Marshal.Copy(pOut, raw, 0, (int)returned);

                if (desc.VendorIdOffset > 0 && desc.VendorIdOffset < returned)
                    vendor = GetAsciiString(raw, (int)desc.VendorIdOffset);

                if (desc.ProductIdOffset > 0 && desc.ProductIdOffset < returned)
                    product = GetAsciiString(raw, (int)desc.ProductIdOffset);

                if (desc.SerialNumberOffset > 0 && desc.SerialNumberOffset < returned)
                    serial = GetAsciiString(raw, (int)desc.SerialNumberOffset);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pQuery);
            Marshal.FreeHGlobal(pOut);
        }
    }

    private static bool ExecuteScsiPassThrough(SafeFileHandle handle, byte[] cdb, byte cdbLength, byte[] dataBuf, bool validateSmart = true)
    {
        Array.Clear(dataBuf, 0, dataBuf.Length);
        GCHandle pinnedData = GCHandle.Alloc(dataBuf, GCHandleType.Pinned);
        try
        {
            var spt = new SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
            {
                Spt = new SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
                    ScsiStatus = 0,
                    PathId = 0,
                    TargetId = 1,
                    Lun = 0,
                    CdbLength = cdbLength,
                    SenseInfoLength = 32,
                    DataIn = 1,
                    DataTransferLength = (uint)dataBuf.Length,
                    TimeOutValue = 3,
                    DataBuffer = pinnedData.AddrOfPinnedObject(),
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>("SenseBuf"),
                    Cdb = new byte[16]
                },
                SenseBuf = new byte[32]
            };
            Array.Copy(cdb, spt.Spt.Cdb, Math.Min(cdb.Length, 16));

            int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>();
            IntPtr pStruct = Marshal.AllocHGlobal(structSize);
            Marshal.StructureToPtr(spt, pStruct, false);

            try
            {
                if (DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH_DIRECT, pStruct, (uint)structSize, pStruct, (uint)structSize, out _, IntPtr.Zero))
                {
                    return validateSmart ? IsValidSmartBuffer(dataBuf) : true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            pinnedData.Free();
        }
        return false;
    }

    private static bool IsValidSmartBuffer(byte[] buffer)
    {
        if (buffer == null || buffer.Length < 512) return false;
        bool allZero = true;
        bool allFF = true;
        for (int i = 0; i < 512; i++)
        {
            if (buffer[i] != 0) allZero = false;
            if (buffer[i] != 0xFF) allFF = false;
            if (!allZero && !allFF) break;
        }
        if (allZero || allFF) return false;

        for (int i = 2; i <= 512 - 12; i += 12)
        {
            byte id = buffer[i];
            if (id > 0 && id < 0xFF) return true;
        }
        return false;
    }

    private static bool ReadAtaSmartViaScsiSat(SafeFileHandle handle, DriveSmartReport report)
    {
        byte[] dataBuf = new byte[512];

        byte[] cdbSat16 = new byte[16];
        cdbSat16[0] = 0x85;
        cdbSat16[1] = (4 << 1);
        cdbSat16[2] = 0x0E;
        cdbSat16[4] = 0xD0;
        cdbSat16[6] = 0x01;
        cdbSat16[8] = 0x01;
        cdbSat16[10] = 0x4F;
        cdbSat16[12] = 0xC2;
        cdbSat16[14] = 0xB0;
        if (ExecuteScsiPassThrough(handle, cdbSat16, 16, dataBuf))
        {
            ParseAtaSmartPayload(dataBuf, report);
            return true;
        }

        byte[] cdbSat12 = new byte[16];
        cdbSat12[0] = 0xA1;
        cdbSat12[1] = (4 << 1);
        cdbSat12[2] = 0x0E;
        cdbSat12[3] = 0xD0;
        cdbSat12[4] = 0x01;
        cdbSat12[5] = 0x01;
        cdbSat12[6] = 0x4F;
        cdbSat12[7] = 0xC2;
        cdbSat12[9] = 0xB0;
        if (ExecuteScsiPassThrough(handle, cdbSat12, 12, dataBuf))
        {
            ParseAtaSmartPayload(dataBuf, report);
            return true;
        }

        byte[] cdbJm = new byte[16];
        cdbJm[0] = 0xDF;
        cdbJm[1] = 0x10;
        cdbJm[3] = 0x02;
        cdbJm[4] = 0x00;
        cdbJm[5] = 0xD0;
        cdbJm[6] = 0x01;
        cdbJm[7] = 0x01;
        cdbJm[8] = 0x4F;
        cdbJm[9] = 0xC2;
        cdbJm[10] = 0xA0;
        cdbJm[11] = 0xB0;
        if (ExecuteScsiPassThrough(handle, cdbJm, 12, dataBuf))
        {
            ParseAtaSmartPayload(dataBuf, report);
            return true;
        }

        byte[] cdbSp = new byte[16];
        cdbSp[0] = 0xF4;
        cdbSp[1] = 0x00;
        cdbSp[2] = 0x24;
        cdbSp[4] = 0x01;
        cdbSp[5] = 0xD0;
        cdbSp[6] = 0x01;
        cdbSp[7] = 0x4F;
        cdbSp[8] = 0xC2;
        cdbSp[9] = 0xB0;
        if (ExecuteScsiPassThrough(handle, cdbSp, 12, dataBuf))
        {
            ParseAtaSmartPayload(dataBuf, report);
            return true;
        }

        byte[] cdbRtl = new byte[16];
        cdbRtl[0] = 0xE4;
        cdbRtl[1] = 0x01;
        cdbRtl[2] = 0x02;
        cdbRtl[4] = 0x02;
        cdbRtl[5] = 0x7F;
        cdbRtl[8] = 0x01;
        if (ExecuteScsiPassThrough(handle, cdbRtl, 12, dataBuf, validateSmart: false))
        {
            int kelvin = dataBuf[1] | (dataBuf[2] << 8);
            if (kelvin >= 250 && kelvin <= 373)
            {
                ParseNvmeSmartBuffer(dataBuf, report);
                return true;
            }
        }

        return false;
    }

    private static bool IsPayloadNonZero(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return false;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != 0 && buffer[i] != 0xFF) return true;
        }
        return false;
    }

    private static bool ReadAtaIdentifyViaScsiSat(SafeFileHandle handle, byte[] identifyBuf)
    {
        byte[] cdbSat16 = new byte[16];
        cdbSat16[0] = 0x85;
        cdbSat16[1] = (4 << 1);
        cdbSat16[2] = 0x0E;
        cdbSat16[6] = 0x01;
        cdbSat16[14] = 0xEC;
        if (ExecuteScsiPassThrough(handle, cdbSat16, 16, identifyBuf, validateSmart: false) && IsPayloadNonZero(identifyBuf))
            return true;

        byte[] cdbSat12 = new byte[16];
        cdbSat12[0] = 0xA1;
        cdbSat12[1] = (4 << 1);
        cdbSat12[2] = 0x0E;
        cdbSat12[4] = 0x01;
        cdbSat12[9] = 0xEC;
        if (ExecuteScsiPassThrough(handle, cdbSat12, 12, identifyBuf, validateSmart: false) && IsPayloadNonZero(identifyBuf))
            return true;

        byte[] cdbJm = new byte[16];
        cdbJm[0] = 0xDF;
        cdbJm[1] = 0x10;
        cdbJm[3] = 0x00;
        cdbJm[4] = 0x00;
        cdbJm[5] = 0xEC;
        cdbJm[6] = 0x01;
        cdbJm[10] = 0xA0;
        cdbJm[11] = 0xEC;
        if (ExecuteScsiPassThrough(handle, cdbJm, 12, identifyBuf, validateSmart: false) && IsPayloadNonZero(identifyBuf))
            return true;

        return false;
    }

    private static string ParseAtaString(byte[] buffer, int offset, int length)
    {
        char[] chars = new char[length];
        for (int i = 0; i < length; i += 2)
        {
            if (offset + i + 1 < buffer.Length)
            {
                chars[i] = (char)buffer[offset + i + 1];
                chars[i + 1] = (char)buffer[offset + i];
            }
        }
        return new string(chars).Trim();
    }

    private static void ParseAtaIdentifyPayload(byte[] buf, DriveSmartReport report)
    {
        string realModel = ParseAtaString(buf, 54, 40);
        string realSerial = ParseAtaString(buf, 20, 20);
        string realFirmware = ParseAtaString(buf, 46, 8);
        ushort rotRate = (ushort)(buf[434] | (buf[435] << 8));

        if (!string.IsNullOrWhiteSpace(realModel))
        {
            if (!string.IsNullOrEmpty(report.Model) && !report.Model.Equals(realModel, StringComparison.OrdinalIgnoreCase))
            {
                report.BridgeControllerName = report.Model;
            }
            report.Model = realModel;
            if (!string.IsNullOrWhiteSpace(realSerial)) report.Serial = realSerial;
            report.FirmwareRevision = realFirmware;

            if (rotRate == 1)
            {
                report.IsSsd = true;
                report.RotationRateRpm = 0;
            }
            else if (rotRate > 1 && rotRate < 0xFFFF)
            {
                report.IsSsd = false;
                report.RotationRateRpm = rotRate;
            }
            else if (realModel.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                     realModel.Contains("SanDisk", StringComparison.OrdinalIgnoreCase) ||
                     realModel.Contains("Kingston", StringComparison.OrdinalIgnoreCase) ||
                     realModel.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ||
                     realModel.Contains("Crucial", StringComparison.OrdinalIgnoreCase))
            {
                report.IsSsd = true;
            }

            if (realModel.Contains("S700", StringComparison.OrdinalIgnoreCase) ||
                realModel.Contains("SM22", StringComparison.OrdinalIgnoreCase) ||
                realModel.Contains("SMI", StringComparison.OrdinalIgnoreCase))
            {
                report.SsdControllerFamily = "Silicon Motion SM2258XT / SM2259";
            }
        }
    }

    private static void ParseNvmeSmartBuffer(byte[] raw, DriveSmartReport report)
    {
        byte criticalWarning = raw[0];
        int kelvin = raw[1] | (raw[2] << 8);
        int celsius = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
        byte percentageUsed = raw[5];

        ulong powerCycles = BitConverter.ToUInt64(raw, 96);
        ulong powerOnHours = BitConverter.ToUInt64(raw, 112);

        report.IsNvme = true;
        report.IsSsd = true;
        report.TemperatureCelsius = celsius;
        report.PowerOnHours = powerOnHours;
        report.PowerCycleCount = powerCycles;
        report.HealthDefault = Math.Max(0, 100 - percentageUsed);
        report.HealthMoreStrict = report.HealthDefault;

        if (!SilentMode)
        {
            Console.WriteLine("\n  [REAL NVMe SMART METRICS (VIA REALTEK USB BRIDGE)]");
            Console.WriteLine($"    Temperature       : {celsius} °C ({kelvin} K)");
            Console.WriteLine($"    Wear Percentage   : {percentageUsed}% used (Available: {100 - percentageUsed}%)");
            Console.WriteLine($"    Power On Hours    : {powerOnHours:N0} h");
            Console.WriteLine($"    Power Cycles      : {powerCycles:N0} times");
            Console.WriteLine($"    Critical Warning  : {(criticalWarning == 0 ? "None (Healthy)" : "CRITICAL ALERT: " + criticalWarning)}");
        }
    }

    private static bool ReadAtaSmartViaLegacy(SafeFileHandle handle, byte driveNumber, DriveSmartReport report)
    {
        var enableCmd = new SENDCMDINPARAMS
        {
            cBufferSize = 0,
            bDriveNumber = driveNumber,
            irDriveRegs = new IDEREGS
            {
                bFeaturesReg = 0xD8,
                bSectorCountReg = 1,
                bSectorNumberReg = 1,
                bCylLowReg = 0x4F,
                bCylHighReg = 0xC2,
                bCommandReg = 0xB0
            }
        };

        int enableSize = Marshal.SizeOf<SENDCMDINPARAMS>();
        IntPtr pEnable = Marshal.AllocHGlobal(enableSize);
        Marshal.StructureToPtr(enableCmd, pEnable, false);
        IntPtr pEnableOut = Marshal.AllocHGlobal(64);

        try
        {
            DeviceIoControl(handle, SMART_SEND_DRIVE_COMMAND, pEnable, (uint)enableSize, pEnableOut, 64, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pEnable);
            Marshal.FreeHGlobal(pEnableOut);
        }

        var readCmd = new SENDCMDINPARAMS
        {
            cBufferSize = 512,
            bDriveNumber = driveNumber,
            irDriveRegs = new IDEREGS
            {
                bFeaturesReg = 0xD0,
                bSectorCountReg = 1,
                bSectorNumberReg = 1,
                bCylLowReg = 0x4F,
                bCylHighReg = 0xC2,
                bCommandReg = 0xB0
            }
        };

        int inSize = Marshal.SizeOf<SENDCMDINPARAMS>();
        IntPtr pIn = Marshal.AllocHGlobal(inSize);
        Marshal.StructureToPtr(readCmd, pIn, false);

        int outSize = 16 + 512;
        IntPtr pOut = Marshal.AllocHGlobal(outSize);

        try
        {
            if (DeviceIoControl(handle, SMART_RCV_DRIVE_DATA, pIn, (uint)inSize, pOut, (uint)outSize, out _, IntPtr.Zero))
            {
                byte[] raw = new byte[512];
                Marshal.Copy(pOut + 16, raw, 0, 512);
                ParseAtaSmartPayload(raw, report);
                return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
            Marshal.FreeHGlobal(pOut);
        }

        return false;
    }

    private static bool ReadAtaSmartViaPassThrough(SafeFileHandle handle, DriveSmartReport report)
    {
        int headerSize = Marshal.SizeOf<ATA_PASS_THROUGH_EX>();
        int bufferSize = headerSize + 512;
        IntPtr pBuffer = Marshal.AllocHGlobal(bufferSize);

        for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(pBuffer, i, 0);

        var pte = new ATA_PASS_THROUGH_EX
        {
            Length = (ushort)headerSize,
            AtaFlags = 0x02,
            DataTransferLength = 512,
            TimeOutValue = 3,
            DataBufferOffset = (IntPtr)headerSize,
            CurrentTaskFile = new byte[8]
        };
        pte.CurrentTaskFile[0] = 0xD0;
        pte.CurrentTaskFile[1] = 1;
        pte.CurrentTaskFile[2] = 1;
        pte.CurrentTaskFile[3] = 0x4F;
        pte.CurrentTaskFile[4] = 0xC2;
        pte.CurrentTaskFile[6] = 0xB0;

        Marshal.StructureToPtr(pte, pBuffer, false);

        try
        {
            if (DeviceIoControl(handle, IOCTL_ATA_PASS_THROUGH, pBuffer, (uint)bufferSize, pBuffer, (uint)bufferSize, out _, IntPtr.Zero))
            {
                byte[] raw = new byte[512];
                Marshal.Copy(pBuffer + headerSize, raw, 0, 512);
                ParseAtaSmartPayload(raw, report);
                return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pBuffer);
        }

        return false;
    }

    private static bool ReadNvmeSmart(SafeFileHandle handle, DriveSmartReport report)
    {
        uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        int bufSize = 4096;
        IntPtr pBuf = Marshal.AllocHGlobal(bufSize);

        try
        {
            uint[] propertyIds = new uint[] { 50, 49 };
            int[] protoOffsets = new int[] { 8, 12, 16 };

            foreach (uint propId in propertyIds)
            {
                foreach (int protoOffset in protoOffsets)
                {
                    for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuf, i, 0);

                    Marshal.WriteInt32(pBuf, 0, (int)propId);
                    Marshal.WriteInt32(pBuf, 4, 0);

                    Marshal.WriteInt32(pBuf, protoOffset + 0, 3);
                    Marshal.WriteInt32(pBuf, protoOffset + 4, 2);
                    Marshal.WriteInt32(pBuf, protoOffset + 8, 2);
                    Marshal.WriteInt32(pBuf, protoOffset + 12, 0);
                    Marshal.WriteInt32(pBuf, protoOffset + 16, 40);
                    Marshal.WriteInt32(pBuf, protoOffset + 20, 512);

                    if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufSize, pBuf, (uint)bufSize, out uint ret, IntPtr.Zero) && ret > 0)
                    {
                        int dataOff = Marshal.ReadInt32(pBuf, protoOffset + 16);
                        int actualOffset = protoOffset + dataOff;
                        if (actualOffset + 512 <= bufSize)
                        {
                            byte[] raw = new byte[512];
                            Marshal.Copy(pBuf + actualOffset, raw, 0, 512);

                            report.RawSmartPayload = (byte[])raw.Clone();

                            byte criticalWarning = raw[0];
                            int kelvin = raw[1] | (raw[2] << 8);
                            int celsius = kelvin > 0 ? (int)Math.Round(kelvin - 273.15) : 0;
                            byte availSpare = raw[3];
                            byte spareThresh = raw[4];
                            byte percentageUsed = raw[5];

                            ulong duRead = BitConverter.ToUInt64(raw, 32);
                            ulong duWritten = BitConverter.ToUInt64(raw, 48);
                            ulong hostReadCmds = BitConverter.ToUInt64(raw, 64);
                            ulong hostWriteCmds = BitConverter.ToUInt64(raw, 80);
                            ulong ctrlBusyMin = BitConverter.ToUInt64(raw, 96);
                            ulong powerCycles = BitConverter.ToUInt64(raw, 112);
                            ulong powerOnHours = BitConverter.ToUInt64(raw, 128);
                            ulong unsafeShutdowns = BitConverter.ToUInt64(raw, 144);
                            ulong mediaErrors = BitConverter.ToUInt64(raw, 160);
                            ulong errorLogEntries = BitConverter.ToUInt64(raw, 176);

                            uint warnTempTime = BitConverter.ToUInt32(raw, 192);
                            uint critTempTime = BitConverter.ToUInt32(raw, 196);

                            int s1Kelvin = raw[200] | (raw[201] << 8);
                            int s2Kelvin = raw[202] | (raw[203] << 8);

                            report.TemperatureCelsius = celsius;
                            report.PowerOnHours = powerOnHours;
                            report.PowerCycleCount = powerCycles;
                            report.TotalTbRead = Math.Round((duRead * 512_000.0) / 1_000_000_000_000.0, 2);
                            report.TotalTbWritten = Math.Round((duWritten * 512_000.0) / 1_000_000_000_000.0, 2);
                            report.HealthDefault = Math.Max(0, 100 - percentageUsed);
                            report.HealthMoreStrict = report.HealthDefault;

                            report.Attributes.Clear();
                            report.Attributes.Add(new SmartAttribute { Id = 0x01, Name = "Critical Warning Flags", RawValue = criticalWarning, Interpretation = criticalWarning == 0 ? "Normal (0x00)" : $"WARNING (0x{criticalWarning:X2})" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x02, Name = "Composite Temperature", RawValue = (ulong)celsius, Interpretation = $"{celsius} °C ({kelvin} K)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x03, Name = "Available Spare", RawValue = availSpare, Interpretation = $"{availSpare}% (Threshold: {spareThresh}%)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x04, Name = "Available Spare Threshold", RawValue = spareThresh, Interpretation = $"{spareThresh}%" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x05, Name = "Percentage Used (Wear)", RawValue = percentageUsed, Interpretation = $"{percentageUsed}% used ({100 - percentageUsed}% remaining)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x06, Name = "Data Units Read", RawValue = duRead, Interpretation = $"{report.TotalTbRead:F2} TB read ({duRead:N0} x 512k)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x07, Name = "Data Units Written", RawValue = duWritten, Interpretation = $"{report.TotalTbWritten:F2} TB written ({duWritten:N0} x 512k)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x08, Name = "Host Read Commands", RawValue = hostReadCmds, Interpretation = $"{hostReadCmds:N0} read commands" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x09, Name = "Host Write Commands", RawValue = hostWriteCmds, Interpretation = $"{hostWriteCmds:N0} write commands" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0A, Name = "Controller Busy Time", RawValue = ctrlBusyMin, Interpretation = $"{ctrlBusyMin:N0} minutes under load" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0B, Name = "Power Cycles", RawValue = powerCycles, Interpretation = $"{powerCycles:N0} power cycles" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0C, Name = "Power On Hours", RawValue = powerOnHours, Interpretation = $"{powerOnHours:N0} hours ({powerOnHours / 24:N0} days)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0D, Name = "Unsafe Shutdowns", RawValue = unsafeShutdowns, Interpretation = $"{unsafeShutdowns:N0} unsafe shutdowns" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0E, Name = "Media & Integrity Errors", RawValue = mediaErrors, Interpretation = mediaErrors == 0 ? "0 (No block errors)" : $"{mediaErrors:N0} uncorrectable errors" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0F, Name = "Error Log Entries", RawValue = errorLogEntries, Interpretation = $"{errorLogEntries:N0} logged entries" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x10, Name = "Warning Composite Temp Time", RawValue = warnTempTime, Interpretation = $"{warnTempTime:N0} min in thermal warning" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x11, Name = "Critical Composite Temp Time", RawValue = critTempTime, Interpretation = $"{critTempTime:N0} min in critical thermal state" });

                            if (s1Kelvin > 0)
                            {
                                int s1C = (int)Math.Round(s1Kelvin - 273.15);
                                report.TemperatureSensor1 = s1C;
                                report.Attributes.Add(new SmartAttribute { Id = 0x12, Name = "Temperature Sensor 1", RawValue = (ulong)s1C, Interpretation = $"{s1C} °C ({s1Kelvin} K)" });
                            }
                            if (s2Kelvin > 0)
                            {
                                int s2C = (int)Math.Round(s2Kelvin - 273.15);
                                report.TemperatureSensor2 = s2C;
                                report.Attributes.Add(new SmartAttribute { Id = 0x13, Name = "Temperature Sensor 2", RawValue = (ulong)s2C, Interpretation = $"{s2C} °C ({s2Kelvin} K)" });
                            }

                            return true;
                        }
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        return false;
    }

    internal static void ParseAtaSmartPayload(byte[] raw, DriveSmartReport report)
    {
        report.RawSmartPayload = (byte[])raw.Clone();
        report.Attributes.Clear();
        var seenIds = new HashSet<byte>();
        report.RawSmartPayload = (byte[])raw.Clone();

        for (int i = 0; i < 30; i++)
        {
            int offset = 2 + (i * 12);
            byte id = raw[offset];
            if (id == 0 || !seenIds.Add(id)) continue;

            ushort flags = (ushort)(raw[offset + 1] | (raw[offset + 2] << 8));
            byte current = raw[offset + 3];
            byte worst = raw[offset + 4];
            ulong rawVal = raw[offset + 5] |
                           ((ulong)raw[offset + 6] << 8) |
                           ((ulong)raw[offset + 7] << 16) |
                           ((ulong)raw[offset + 8] << 24) |
                           ((ulong)raw[offset + 9] << 32) |
                           ((ulong)raw[offset + 10] << 40);

            string name = GetAtaAttributeName(id);
            string interp = GetAtaInterpretation(id, rawVal, current, worst, report.Model);

            var attr = new SmartAttribute
            {
                Id = id,
                Name = name,
                Current = current,
                Worst = worst,
                Flags = flags,
                RawValue = rawVal,
                Interpretation = interp
            };
            report.Attributes.Add(attr);

            if (id == 0xC2 || id == 0xE7 || id == 0xBE)
            {
                if (report.TemperatureCelsius == 0)
                    report.TemperatureCelsius = (int)(rawVal & 0xFF);
            }
            if (id == 0x09)
            {
                report.PowerOnHours = (rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal;
            }
            if (id == 0x0C)
            {
                report.PowerCycleCount = rawVal;
            }
        }
    }

    private static string GetAtaAttributeName(byte id) => id switch
    {
        0x01 => "Read Error Rate",
        0x03 => "Spin-Up Time",
        0x04 => "Start/Stop Count",
        0x05 => "Reallocated Sectors Count",
        0x07 => "Seek Error Rate",
        0x09 => "Power-On Hours",
        0x0A => "Spin Retry Count",
        0x0C => "Power Cycle Count",
        0xA5 => "Block Erase Count",
        0xA6 => "Minimum Erase Count",
        0xA7 => "Maximum Erase Count",
        0xA8 => "Average Erase Count",
        0xA9 => "Bad Block Count",
        0xAA => "Reserved Block Count",
        0xAB => "Program Fail Count",
        0xAC => "Erase Fail Count",
        0xAD => "Wear Leveling Count",
        0xAE => "Unexpected Power Loss Count",
        0xAF => "Program Fail Count (Chip)",
        0xB0 => "Erase Fail Count In Worst Die",
        0xB4 => "Unused Reserved Block Count",
        0xB7 => "SATA Downshift Count",
        0xB8 => "End-to-End Error",
        0xBB => "Reported Uncorrectable",
        0xBC => "Command Timeout",
        0xBD => "High Fly Writes",
        0xBE => "Airflow Temperature",
        0xBF => "G-Sense Error Rate",
        0xC0 => "Power-Off Retract Count",
        0xC1 => "Load/Unload Cycle Count",
        0xC2 => "Temperature",
        0xC3 => "Hardware ECC Recovered",
        0xC4 => "Reallocation Event Count",
        0xC5 => "Current Pending Sector Count",
        0xC6 => "Offline Uncorrectable",
        0xC7 => "UltraDMA CRC Error Count",
        0xE6 => "Media Wearout Indicator",
        0xE7 => "SSD Life Left / Temperature",
        0xE8 => "Available Reserved Space",
        0xE9 => "NAND GB Written TLC",
        0xEA => "NAND GB Written SLC",
        0xF0 => "Head Flying Hours",
        0xF1 => "Total LBAs Written",
        0xF2 => "Total LBAs Read",
        0xF3 => "Flash ECC Error Count",
        0xF4 => "Temperature Throttle Status",
        0xFE => "Free Fall Event Count",
        _ => $"Vendor-Specific Attribute (0x{id:X2})"
    };

    internal static string GetAtaInterpretation(byte id, ulong rawVal, byte current, byte worst, string? model = null)
    {
        bool isSeagate = model != null && (model.Contains("Seagate", StringComparison.OrdinalIgnoreCase) || model.StartsWith("ST", StringComparison.OrdinalIgnoreCase));

        return id switch
        {
            0x01 => isSeagate ? ((rawVal >> 32) == 0 ? "0 errors (Normal)" : $"WARNING: {(rawVal >> 32):N0} errors") : (rawVal == 0 ? "0 errors" : $"{rawVal:N0} errors"),
            0x03 => $"{rawVal:N0} ms",
            0x04 => $"{rawVal:N0} start stops",
            0x05 => rawVal == 0 ? "0 reallocated (OK)" : $"WARNING: {rawVal:N0} reallocated",
            0x07 => isSeagate ? ((rawVal >> 32) == 0 ? "0 errors (Normal)" : $"WARNING: {(rawVal >> 32):N0} errors") : (rawVal == 0 ? "0 errors" : $"{rawVal:N0} errors"),
            0x09 => $"{((rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal):N0} h ({((rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal) / 24:N0} d)",
            0x0A => rawVal == 0 ? "0 spin retries" : $"WARNING: {rawVal:N0}",
            0x0C => $"{rawVal:N0} cycles",
            0xA5 => $"{rawVal:N0} erases",
            0xA6 => $"{rawVal:N0} min cycles",
            0xA7 => $"{rawVal:N0} max cycles",
            0xA8 => $"{rawVal:N0} avg cycles",
            0xA9 => rawVal == 0 ? "0 bad blocks" : $"{rawVal:N0} bad blocks",
            0xAA => $"{rawVal:N0} reserved",
            0xAB => rawVal == 0 ? "0 write fails" : $"WARNING: {rawVal:N0}",
            0xAC => rawVal == 0 ? "0 erase fails" : $"WARNING: {rawVal:N0}",
            0xAD => $"{rawVal:N0} wear cycles",
            0xAE => $"{rawVal:N0} power losses",
            0xB0 => $"{rawVal:N0} worst die fails",
            0xB7 => $"{rawVal:N0} SATA slowdowns",
            0xB8 => rawVal == 0 ? "0 buffer errors" : $"WARNING: {rawVal:N0}",
            0xBB => rawVal == 0 ? "0 uncorrectable" : $"WARNING: {rawVal:N0}",
            0xBC => (rawVal == 0 || (rawVal & 0xFFFF) == 0) ? "0 timeouts" : $"WARNING: {(rawVal & 0xFFFF):N0} timeouts",
            0xBD => rawVal == 0 ? "0 unstable writes" : $"{rawVal:N0}",
            0xBE => $"{(int)(rawVal & 0xFF)} C airflow",
            0xBF => rawVal == 0 ? "0 shocks" : $"{rawVal:N0} shocks",
            0xC0 => $"{rawVal:N0} head retractions",
            0xC1 => $"{rawVal:N0} head load cycles",
            0xC2 => FormatTemperatureInterpretation(rawVal),
            0xC3 => isSeagate ? "Normal (Hardware ECC)" : $"{rawVal:N0} ECC corrections",
            0xC4 => rawVal == 0 ? "0 reallocation events" : $"{rawVal:N0} reallocations",
            0xC5 => rawVal == 0 ? "0 pending (OK)" : $"WARNING: {rawVal:N0} pending",
            0xC6 => rawVal == 0 ? "0 offline" : $"WARNING: {rawVal:N0} offline",
            0xC7 => rawVal == 0 ? "0 CRC errors (OK)" : $"WARNING: {rawVal:N0} CRC errors",
            0xE6 => $"{rawVal:N0} ({current}% life)",
            0xE7 => $"{rawVal:N0} ({current}% life)",
            0xE8 => $"{rawVal:N0}% spare remaining",
            0xE9 => $"{rawVal:N0} GB written",
            0xEA => $"{rawVal:N0} GB written",
            0xF0 => isSeagate ? $"{(rawVal & 0xFFFF):N0} flying hours" : $"{rawVal:N0} flying hours",
            0xF1 => FormatDataWritten(rawVal, model),
            0xF2 => FormatDataRead(rawVal, model),
            0xF3 => $"{rawVal:N0} flash ECC corrections",
            0xF4 => rawVal == 0 ? "0 throttling" : $"{rawVal:N0} throttles",
            _ => rawVal == 0 ? "OK" : $"{rawVal:N0}"
        };
    }

    private static string FormatDataWritten(ulong rawVal, string? model)
    {
        bool isSmi = model != null && (model.Contains("S700", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("SM22", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("SMI", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("Kingston", StringComparison.OrdinalIgnoreCase));
        if (isSmi)
        {
            double gb = (rawVal * 32.0 * 1048576.0) / 1_000_000_000.0;
            if (gb < 1000.0) return $"{gb:F2} GB written";
            return $"{gb / 1000.0:F2} TB written";
        }
        return $"{(rawVal * 512.0) / 1_000_000_000_000.0:F1} TB written";
    }

    private static string FormatDataRead(ulong rawVal, string? model)
    {
        bool isSmi = model != null && (model.Contains("S700", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("SM22", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("SMI", StringComparison.OrdinalIgnoreCase) ||
                                       model.Contains("Kingston", StringComparison.OrdinalIgnoreCase));
        if (isSmi)
        {
            double gb = (rawVal * 32.0 * 1048576.0) / 1_000_000_000.0;
            if (gb < 1000.0) return $"{gb:F2} GB read";
            return $"{gb / 1000.0:F2} TB read";
        }
        return $"{(rawVal * 512.0) / 1_000_000_000_000.0:F1} TB read";
    }

    private static string FormatTemperatureInterpretation(ulong rawVal)
    {
        int cur = (int)(rawVal & 0xFF);
        int min = (int)((rawVal >> 16) & 0xFF);
        int max = (int)((rawVal >> 32) & 0xFF);
        if (min > 0 && max > 0 && max >= min)
            return $"{cur} C (Min {min} / Max {max})";
        return $"{cur} C";
    }
    private static string GetBusTypeName(uint busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394 (FireWire)",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD Card",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "StorageSpaces",
        17 => "NVMe (PCI Express)",
        _ => "Unknown"
    };

    private static string GetAsciiString(byte[] buffer, int offset)
    {
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    public static bool IsPendriveOrUsbFlash(uint busType, string model, long diskSizeBytes)
    {
        string m = (model ?? "").Trim();
        if (m.StartsWith("USB DISK", StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith("USB Flash", StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith("Generic Flash", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("DataTraveler", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Cruzer", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("UDisk", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("PenDrive", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Flash Drive", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Flash Disk", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Ultra Fit", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Ultra Flair", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("TransMemory", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("JumpDrive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (busType == 7 || busType == 0)
        {
            if (diskSizeBytes > 0 && diskSizeBytes <= 274_877_906_944L)
            {
                if (m.Contains("SanDisk", StringComparison.OrdinalIgnoreCase) && !m.Contains("Extreme", StringComparison.OrdinalIgnoreCase) && !m.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (m.Contains("Kingston", StringComparison.OrdinalIgnoreCase) && !m.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (m.Contains("Kioxia", StringComparison.OrdinalIgnoreCase) && !m.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (m.Contains("USB", StringComparison.OrdinalIgnoreCase) && !m.Contains("SSD", StringComparison.OrdinalIgnoreCase) && !m.Contains("NVMe", StringComparison.OrdinalIgnoreCase) && !m.Contains("Elements", StringComparison.OrdinalIgnoreCase) && !m.Contains("Expansion", StringComparison.OrdinalIgnoreCase) && !m.Contains("Passport", StringComparison.OrdinalIgnoreCase) && !m.Contains("Canvio", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static bool IsPendriveOrRemovable(DriveSmartReport report)
    {
        if (report == null) return false;
        return IsPendriveOrUsbFlash(report.BusType, report.Model, report.DiskSizeBytes);
    }

    public static List<DriveSmartReport> RescanAllDrives()
    {
        return StoragePipeline.ExecutePipeline(
            () => ScanDirectPhysicalDrives(null, false, false),
            () => RaidStorageEngine.ScanRaidAdapters()
        );
    }

    public static bool RefreshDriveTelemetry(DriveSmartReport report)
    {
        if (report == null || string.IsNullOrEmpty(report.DevicePath)) return false;
        try
        {
            var h = CreateFile(
                report.DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (h.IsInvalid)
            {
                h = CreateFile(
                    report.DevicePath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            using (h)
            {
                if (h.IsInvalid) return false;

                bool ok = false;
                if (report.IsNvme)
                {
                    ok = ReadNvmeSmart(h, report);
                }
                else
                {
                    ok = ReadAtaSmartViaScsiSat(h, report) ||
                         ReadAtaSmartViaPassThrough(h, report) ||
                         ReadAtaSmartViaLegacy(h, 0, report);
                }

                if (ok)
                {
                    VesperHealthCalculator.EvaluateDrive(report);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
}
