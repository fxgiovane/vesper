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
                    TuiIndustrialEngine.RunAutomatedVerification(verifyReports);

                    string artDir = @"C:\Users\Zappetinho\.gemini\antigravity\brain\7a0c14b4-3553-4dab-903c-064ccab2fca9";
                    string gDrive = @"G:\Meu Drive\VESPER_SCREENSHOTS";
                    ScreenshotRenderer.GenerateAllScreenshots(artDir, gDrive);
                    return;
                }
                if (arg.Equals("--generate-screenshots", StringComparison.OrdinalIgnoreCase))
                {
                    string artDir = @"C:\Users\Zappetinho\.gemini\antigravity\brain\7a0c14b4-3553-4dab-903c-064ccab2fca9";
                    string gDrive = @"G:\Meu Drive\VESPER_SCREENSHOTS";
                    ScreenshotRenderer.GenerateAllScreenshots(artDir, gDrive);
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
                    // Usuario cancelou o prompt UAC - prossegue em modo local
                }
            }

            // Se iniciado sem argumentos ou com --tui, abre a Interface Interativa (TUI) diretamente
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
                Console.WriteLine("AVISO: Privilegios de Administrador nao detectados. O acesso direto ao kernel requer elevacao (UAC).\n");
            }

        bool jsonOutput = false;
        bool strictMode = false;
        bool scanOnly = false;
        bool dumpRaw = false;
        bool surfaceTest = false;
        bool seekTest = false;
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
            else if (arg.Equals("--seek", StringComparison.OrdinalIgnoreCase))
                seekTest = true;
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

        if (surfaceTest)
        {
            RunCliSurfaceTest(targetDrive ?? 0);
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
            Console.WriteLine("  VESPER 1.0  \u2022  @_fxgiovane");
            Console.WriteLine("  Telemetria e Diagnostico de Armazenamento");
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

        if (!SilentMode)
        {
            foreach (var rep in allReports)
            {
                PrintSentinelReport(rep, dumpRaw);
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
        Console.WriteLine($"\n[ERRO NA INICIALIZACAO]: {ex.Message}\n{ex.StackTrace}");
        Console.ResetColor();
        Console.WriteLine("\nPressione qualquer tecla para sair...");
        try { Console.ReadKey(true); } catch { }
    }
}

    private static void RunCliSurfaceTest(int driveIndex)
    {
        string devPath = $@"\\.\PhysicalDrive{driveIndex}";
        Console.WriteLine($"Iniciando Surface Test (Leitura Nao-Bufferizada) em {devPath}...");

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

        SurfaceTestEngine.RunTest(
            devPath,
            diskSize,
            bytesPerSector,
            60,
            cts.Token,
            (res, prog) =>
            {
                Console.Write($"\rBloco {prog.CurrentBlock,3}/{prog.TotalBlocks} [{prog.Percent,5:F1}%] Latencia: {res.LatencyMs,6:F2} ms | Vel: {prog.CurrentSpeedMBps,6:F1} MB/s | Media: {prog.AvgSpeedMBps,6:F1} MB/s | Integridade: {prog.SurfaceHealthPercent:F1}%");
            });

        Console.WriteLine("\nSurface Test finalizado.");
    }

    private static void RunCliSeekTest(int driveIndex)
    {
        string devPath = $@"\\.\PhysicalDrive{driveIndex}";
        Console.WriteLine($"Iniciando Random Seek Test (QPC IOPS) em {devPath} (500 amostras)...");

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
            500,
            cts.Token,
            (sample, prog) =>
            {
                if (prog.OperationsCompleted % 50 == 0 || prog.OperationsCompleted == prog.TotalOperationsTarget)
                {
                    Console.Write($"\rOp {prog.OperationsCompleted,4}/{prog.TotalOperationsTarget} | Seek Atual: {prog.CurrentSeekMs,6:F2} ms | Min: {prog.MinSeekMs,6:F2} ms | Avg: {prog.AvgSeekMs,6:F2} ms | Max: {prog.MaxSeekMs,6:F2} ms | {prog.CurrentIops,5:F0} IOPS");
                }
            });

        Console.WriteLine("\nRandom Seek Test finalizado.");
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

        Console.WriteLine($"Despachando comando de diagnostico {mode} para {devPath}...");
        bool ok = FirmwareSelfTestEngine.ExecuteSelfTest(devPath, false, false, mode, out string msg);
        Console.WriteLine(ok ? $"[SUCESSO]: {msg}" : $"[FALHA]: {msg}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  VESPER 1.0  \u2022  @_fxgiovane");
        Console.WriteLine("  Telemetria e Diagnostico de Armazenamento");
        Console.WriteLine("================================================================================");
        Console.WriteLine("USO:");
        Console.WriteLine("  Vesper.exe [opcoes] [indice_disco]\n");
        Console.WriteLine("COMANDOS E PARAMETROS:");
        Console.WriteLine("  (nenhum)        Abre a interface interativa (TUI).");
        Console.WriteLine("  --tui           Forca abertura da interface interativa.");
        Console.WriteLine("  --surface       Executa o Surface Test (Leitura Sequencial Nao-Bufferizada).");
        Console.WriteLine("  --seek          Executa o Random Seek Test (Medicao QPC de Latencia e IOPS).");
        Console.WriteLine("  --selftest <m>  Dispara teste no firmware (modos: short, extended, abort).");
        Console.WriteLine("  <numero>        Especifica o indice do disco fisico (ex: 0 para PhysicalDrive0).");
        Console.WriteLine("  --raw, -r       Adiciona o dump hexadecimal bruto de 512 bytes lido do hardware.");
        Console.WriteLine("  --json, -j      Exporta todo o relatorio e atributos em formato JSON estruturado.");
        Console.WriteLine("  --scan, -s      Varredura rapida em 1 linha por disco (modelo, serial, capacidade).");
        Console.WriteLine("  --strict        Aplica modelo rigoroso de degradacao para servidores/missao critica.");
        Console.WriteLine("  --verbose, -v   Exibe logs operacionais da cascata de deteccao (Tier 0, 1 e 2).");
        Console.WriteLine("  --help, -h, /?  Exibe este menu de ajuda.\n");
        Console.WriteLine("EXEMPLOS:");
        Console.WriteLine("  .\\Vesper.exe                   # Relatorio completo de todos os discos");
        Console.WriteLine("  .\\Vesper.exe 0                 # Relatorio apenas do disco fisico 0");
        Console.WriteLine("  .\\Vesper.exe --raw             # Relatorio + dump hexadecimal de 512 bytes");
        Console.WriteLine("  .\\Vesper.exe 0 --raw           # Disco 0 com dump hexadecimal completo");
        Console.WriteLine("  .\\Vesper.exe --json            # Despeja JSON estruturado no terminal");
        Console.WriteLine("  .\\Vesper.exe --scan            # Lista rapida de discos");
        Console.WriteLine("================================================================================");
    }

    private static List<DriveSmartReport> ScanDirectPhysicalDrives(int? targetDrive, bool strictMode, bool scanOnly)
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

            QueryDeviceDescriptor(handle, out uint busType, out string vendor, out string product, out string serial);
            QueryDiskGeometry(handle, out long diskSize, out uint bytesPerSector);

            string fullModel = $"{vendor} {product}".Trim();
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
                VesperHealthCalculator.EvaluateDrive(report);
                if (strictMode) report.HealthDefault = report.HealthMoreStrict;
                directReports.Add(report);
            }
            else if (diskSize > 0 || !string.IsNullOrEmpty(report.Model))
            {
                report.HealthDefault = 100.0;
                report.HealthMoreStrict = 100.0;
                report.Performance = 100.0;
                report.RemainingLifetimeText = "Mais de 1000 dias (Sem telemetria de degradacao)";
                report.HealthVerdictText = "Telemetria SMART indisponivel neste barramento/nivel de privilegio.";
                directReports.Add(report);
            }
        }

        return directReports;
    }

    private static void PrintSentinelReport(DriveSmartReport report, bool dumpRaw)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine($"DISCO: {report.DevicePath} | MODELO: {report.Model}");
        Console.WriteLine($"SERIAL: {report.Serial} | INTERFACE: {report.BusTypeName} | CAPACIDADE: {report.DiskSizeGbBinary:F2} GB");
        if (report.IsRaidMember)
        {
            Console.WriteLine($"RAID: Slot {report.RaidSlotNumber} ({report.RaidControllerName})");
        }
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Saude (VESPER)        : {report.HealthDefault:F0} %");
        Console.WriteLine($"Desempenho            : {report.Performance:F0} %");
        Console.WriteLine($"Temperatura           : {report.TemperatureCelsius} C");
        Console.WriteLine($"Tempo em Operacao     : {report.PowerOnHours:N0} h ({report.PowerOnHours / 24:N0} dias) | Ciclos: {report.PowerCycleCount:N0}");
        if (report.TotalTbWritten > 0.0)
            Console.WriteLine($"Total Gravado (TBW)   : {report.TotalTbWritten:F2} TB");
        if (report.TotalTbRead > 0.0)
            Console.WriteLine($"Total Lido (TBR)      : {report.TotalTbRead:F2} TB");
        Console.WriteLine($"Estimativa de Vida    : {report.RemainingLifetimeText}");
        Console.WriteLine($"Diagnostico           : {report.HealthVerdictText}");
        Console.WriteLine("--------------------------------------------------------------------------------");

        if (VerboseMode && report.BreakdownLogs.Count > 0)
        {
            Console.WriteLine("\n[DETALHAMENTO MATEMATICO VESPER]");
            foreach (var log in report.BreakdownLogs)
            {
                Console.WriteLine(log);
            }
        }

        if (report.Attributes.Count > 0)
        {
            if (report.IsNvme)
            {
                Console.WriteLine("\n[TELEMETRIA S.M.A.R.T. NVMe (LOG PAGE 0x02)]");
                Console.WriteLine("+------+----------------------------------+-----------------+------------------+");
                Console.WriteLine("| ID   | Metrica NVMe                     | Raw (Hex)       | Valor / Detalhe  |");
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
                Console.WriteLine("\n[ATRIBUTOS S.M.A.R.T. REAIS (ATA / SATA)]");
                Console.WriteLine("+------+-------------------------------+-------+------+-------------------------+");
                Console.WriteLine("| ID   | Atributo                      | Atual | Pior | Raw (Hex / Dec)         |");
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
            Console.WriteLine("\n[DUMP HEXADECIMAL BRUTO DO HARDWARE (512 BYTES)]");
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

    private static void QueryDeviceDescriptor(SafeFileHandle handle, out uint busType, out string vendor, out string product, out string serial)
    {
        busType = 0;
        vendor = "";
        product = "";
        serial = "";

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
            Console.WriteLine("\n  [METRICAS NVMe SMART REAIS (VIA PONTE USB REALTEK)]");
            Console.WriteLine($"    Temperatura       : {celsius} Â°C ({kelvin} K)");
            Console.WriteLine($"    Desgaste de Vida  : {percentageUsed}% usado (Disponivel: {100 - percentageUsed}%)");
            Console.WriteLine($"    Horas Ligado      : {powerOnHours:N0} h");
            Console.WriteLine($"    Ciclos de Energia : {powerCycles:N0} vezes");
            Console.WriteLine($"    Aviso Critico     : {(criticalWarning == 0 ? "Nenhum (Saudavel)" : "ALERTA CRITICO: " + criticalWarning)}");
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
                            report.Attributes.Add(new SmartAttribute { Id = 0x01, Name = "Critical Warning Flags", RawValue = criticalWarning, Interpretation = criticalWarning == 0 ? "Normal (0x00)" : $"ALERTA (0x{criticalWarning:X2})" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x02, Name = "Composite Temperature", RawValue = (ulong)celsius, Interpretation = $"{celsius} Â°C ({kelvin} K)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x03, Name = "Available Spare", RawValue = availSpare, Interpretation = $"{availSpare}% (Limiar: {spareThresh}%)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x04, Name = "Available Spare Threshold", RawValue = spareThresh, Interpretation = $"{spareThresh}%" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x05, Name = "Percentage Used (Wear)", RawValue = percentageUsed, Interpretation = $"{percentageUsed}% usado ({100 - percentageUsed}% vida restante)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x06, Name = "Data Units Read", RawValue = duRead, Interpretation = $"{report.TotalTbRead:F2} TB lidos ({duRead:N0} x 512k)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x07, Name = "Data Units Written", RawValue = duWritten, Interpretation = $"{report.TotalTbWritten:F2} TB gravados ({duWritten:N0} x 512k)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x08, Name = "Host Read Commands", RawValue = hostReadCmds, Interpretation = $"{hostReadCmds:N0} comandos de leitura" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x09, Name = "Host Write Commands", RawValue = hostWriteCmds, Interpretation = $"{hostWriteCmds:N0} comandos de escrita" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0A, Name = "Controller Busy Time", RawValue = ctrlBusyMin, Interpretation = $"{ctrlBusyMin:N0} minutos sob carga" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0B, Name = "Power Cycles", RawValue = powerCycles, Interpretation = $"{powerCycles:N0} ciclos liga/desliga" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0C, Name = "Power On Hours", RawValue = powerOnHours, Interpretation = $"{powerOnHours:N0} horas ({powerOnHours / 24:N0} dias)" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0D, Name = "Unsafe Shutdowns", RawValue = unsafeShutdowns, Interpretation = $"{unsafeShutdowns:N0} desligamentos anormais" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0E, Name = "Media & Integrity Errors", RawValue = mediaErrors, Interpretation = mediaErrors == 0 ? "0 (Sem erros de bloco)" : $"{mediaErrors:N0} erros incorrigiveis" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x0F, Name = "Error Log Entries", RawValue = errorLogEntries, Interpretation = $"{errorLogEntries:N0} entradas registradas" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x10, Name = "Warning Composite Temp Time", RawValue = warnTempTime, Interpretation = $"{warnTempTime:N0} min em advertencia termica" });
                            report.Attributes.Add(new SmartAttribute { Id = 0x11, Name = "Critical Composite Temp Time", RawValue = critTempTime, Interpretation = $"{critTempTime:N0} min em estado termico critico" });

                            if (s1Kelvin > 0)
                            {
                                int s1C = (int)Math.Round(s1Kelvin - 273.15);
                                report.Attributes.Add(new SmartAttribute { Id = 0x12, Name = "Temperature Sensor 1", RawValue = (ulong)s1C, Interpretation = $"{s1C} Â°C ({s1Kelvin} K)" });
                            }
                            if (s2Kelvin > 0)
                            {
                                int s2C = (int)Math.Round(s2Kelvin - 273.15);
                                report.Attributes.Add(new SmartAttribute { Id = 0x13, Name = "Temperature Sensor 2", RawValue = (ulong)s2C, Interpretation = $"{s2C} Â°C ({s2Kelvin} K)" });
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

        for (int i = 0; i < 30; i++)
        {
            int offset = 2 + (i * 12);
            byte id = raw[offset];
            if (id == 0) continue;

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
            string interp = GetAtaInterpretation(id, rawVal, current, worst);

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
        0xF4 => "Temperature Throttle Status",
        0xFE => "Free Fall Event Count",
        _ => $"Atributo Vendor-Specific (0x{id:X2})"
    };

    internal static string GetAtaInterpretation(byte id, ulong rawVal, byte current, byte worst) => id switch
    {
        0x01 => rawVal == 0 ? "0 erros de leitura fÃ­sica" : $"{rawVal:N0} leituras com erro",
        0x03 => rawVal == 0 ? $"{rawVal:N0}" : $"{rawVal:N0} ms tempo de spin-up",
        0x04 => $"{rawVal:N0} partidas de motor",
        0x05 => rawVal == 0 ? "0 setores realocados (SuperfÃ­cie Ã­ntegra)" : $"ALERTA CRÃTICO: {rawVal:N0} setores realocados",
        0x07 => rawVal == 0 ? "0 erros de busca" : $"{rawVal:N0} (Contador acumulado)",
        0x09 => $"{((rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal):N0} horas ({((rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal) / 24:N0} dias)",
        0x0A => rawVal == 0 ? "0 retentativas de rotaÃ§Ã£o" : $"ALERTA: {rawVal:N0} retentativas de spin",
        0x0C => $"{rawVal:N0} ciclos liga/desliga",
        0xA5 => $"{rawVal:N0} blocos apagados",
        0xA6 => $"{rawVal:N0} ciclos mÃ­nimos de apagamento",
        0xA7 => $"{rawVal:N0} ciclos mÃ¡ximos de apagamento",
        0xA8 => $"{rawVal:N0} ciclos mÃ©dios de apagamento",
        0xA9 => rawVal == 0 ? "0 blocos defeituosos" : $"{rawVal:N0} blocos defeituosos de fÃ¡brica",
        0xAA => $"{rawVal:N0} blocos de reserva",
        0xAB => rawVal == 0 ? "0 falhas de gravaÃ§Ã£o" : $"ALERTA: {rawVal:N0} falhas de gravaÃ§Ã£o flash",
        0xAC => rawVal == 0 ? "0 falhas de apagamento" : $"ALERTA: {rawVal:N0} falhas de apagamento flash",
        0xAD => $"{rawVal:N0} ciclos mÃ©dios de desgaste",
        0xAE => $"{rawVal:N0} desligamentos repentinos",
        0xB7 => $"{rawVal:N0} quedas de link SATA",
        0xB8 => rawVal == 0 ? "0 erros end-to-end" : $"ALERTA: {rawVal:N0} erros de buffer interno",
        0xBB => rawVal == 0 ? "0 erros incorrigÃ­veis reportados" : $"ALERTA CRÃTICO: {rawVal:N0} erros incorrigÃ­veis",
        0xBC => rawVal == 0 ? "0 comandos com timeout" : $"ALERTA: {rawVal:N0} comandos abortados",
        0xBD => rawVal == 0 ? "0 gravaÃ§Ãµes fora de altura" : $"{rawVal:N0} gravaÃ§Ãµes com cabeÃ§a instÃ¡vel",
        0xBE => $"{(int)(rawVal & 0xFF)} Â°C temperatura do fluxo de ar",
        0xBF => rawVal == 0 ? "0 eventos de choque/vibraÃ§Ã£o" : $"{rawVal:N0} choques fÃ­sicos registrados",
        0xC0 => $"{rawVal:N0} retornos de emergÃªncia da cabeÃ§a",
        0xC1 => $"{rawVal:N0} ciclos de pouso/estacionamento",
        0xC2 => FormatTemperatureInterpretation(rawVal),
        0xC3 => $"{rawVal:N0} correÃ§Ãµes ECC em hardware",
        0xC4 => rawVal == 0 ? "0 eventos de realocaÃ§Ã£o" : $"{rawVal:N0} eventos de realocaÃ§Ã£o",
        0xC5 => rawVal == 0 ? "0 setores pendentes (SuperfÃ­cie estÃ¡vel)" : $"ALERTA CRÃTICO: {rawVal:N0} setores instÃ¡veis pendentes",
        0xC6 => rawVal == 0 ? "0 setores incorrigÃ­veis offline" : $"ALERTA CRÃTICO: {rawVal:N0} setores incorrigÃ­veis",
        0xC7 => rawVal == 0 ? "0 erros CRC (Interface e cabo perfeitos)" : $"ALERTA: {rawVal:N0} erros CRC (Verificar cabo SATA)",
        0xE6 => $"{rawVal:N0} ({current}% vida restante)",
        0xE7 => $"{rawVal:N0} ({current}% vida restante)",
        0xE8 => $"{rawVal:N0}% espaÃ§o de reserva disponÃ­vel",
        0xE9 => $"{rawVal:N0} GB escritos em TLC",
        0xEA => $"{rawVal:N0} GB escritos em SLC",
        0xF0 => $"{rawVal:N0} horas de vÃ´o das cabeÃ§as",
        0xF1 => $"{(rawVal * 512.0) / 1_000_000_000_000.0:F2} TB gravados ({rawVal:N0} setores)",
        0xF2 => $"{(rawVal * 512.0) / 1_000_000_000_000.0:F2} TB lidos ({rawVal:N0} setores)",
        0xF4 => rawVal == 0 ? "0 (Sem throttling tÃ©rmico)" : $"{rawVal:N0} eventos de corte tÃ©rmico",
        _ => rawVal == 0 ? "0 (Sem anomalias)" : $"{rawVal:N0}"
    };

    private static string FormatTemperatureInterpretation(ulong rawVal)
    {
        int cur = (int)(rawVal & 0xFF);
        int min = (int)((rawVal >> 16) & 0xFF);
        int max = (int)((rawVal >> 32) & 0xFF);
        if (min > 0 && max > 0 && max >= min)
            return $"{cur} Â°C (Min: {min} Â°C, Max: {max} Â°C)";
        return $"{cur} Â°C";
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
        _ => "Desconhecido"
    };

    private static string GetAsciiString(byte[] buffer, int offset)
    {
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
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
