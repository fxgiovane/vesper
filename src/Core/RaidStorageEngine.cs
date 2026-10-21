using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

internal static class RaidStorageEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_SCSI_GET_INQUIRY_DATA = 0x00041018;
    private const uint IOCTL_SCSI_MINIPORT = 0x0004D008;

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

    public static List<DriveSmartReport> ScanRaidAdapters()
    {
        var raidDrives = new List<DriveSmartReport>();

        if (Program.VerboseMode)
        {
            Console.WriteLine("\n--------------------------------------------------------------------------------");
            Console.WriteLine("[SUBSISTEMA DE ENGENHARIA RAID DEDICADO]");
            Console.WriteLine("--------------------------------------------------------------------------------");
        }

        int adaptersDetected = 0;

        for (int i = 0; i < 8; i++)
        {
            string adapterPath = $@"\\.\Scsi{i}:";
            using var handle = CreateFile(
                adapterPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid) continue;

            adaptersDetected++;
            string controllerType = IdentifyScsiController(handle, i);
            if (Program.VerboseMode)
                Console.WriteLine($"  [ADAPTADOR DE HOST ENCONTRADO]: {adapterPath} ({controllerType})");

            var physicalDrives = QueryRaidPhysicalDrives(handle, i, controllerType);
            raidDrives.AddRange(physicalDrives);
        }

        if (Program.VerboseMode)
        {
            if (adaptersDetected == 0)
            {
                Console.WriteLine("  Nenhum adaptador de host SCSI/RAID dedicado ativo no sistema.");
            }
            else if (raidDrives.Count == 0)
            {
                Console.WriteLine("  Controladores presentes operando em modo HBA/AHCI direto (discos gerenciados via PhysicalDrive).");
            }
        }

        return raidDrives;
    }

    private static string IdentifyScsiController(SafeFileHandle handle, int adapterIndex)
    {
        int bufSize = 4096;
        IntPtr pOut = Marshal.AllocHGlobal(bufSize);
        try
        {
            if (DeviceIoControl(handle, IOCTL_SCSI_GET_INQUIRY_DATA, IntPtr.Zero, 0, pOut, (uint)bufSize, out uint returned, IntPtr.Zero))
            {
                byte numBuses = Marshal.ReadByte(pOut, 0);
                return $"Storport Miniport com {numBuses} barramento(s)";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pOut);
        }
        return "Host Adapter SCSI GenÃ©rico";
    }

    private static List<DriveSmartReport> QueryRaidPhysicalDrives(SafeFileHandle handle, int adapterIndex, string controllerType)
    {
        var list = new List<DriveSmartReport>();
        QueryMegaRaidDrives(handle, adapterIndex, list);
        if (list.Count == 0)
        {
            QueryCsmiDrives(handle, adapterIndex, list);
        }
        return list;
    }

    private static void QueryAmdRaidDrives(int adapterIndex, List<DriveSmartReport> list)
    {
        string? rcadmPath = null;
        string p86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RAIDXpert2", "rcadm.exe");
        string p64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RAIDXpert2", "rcadm.exe");
        if (File.Exists(p86)) rcadmPath = p86;
        else if (File.Exists(p64)) rcadmPath = p64;

        if (rcadmPath == null) return;

        try
        {
            var psiList = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rcadmPath,
                Arguments = "-M -qa",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var procList = System.Diagnostics.Process.Start(psiList);
            if (procList == null) return;
            string listOut = procList.StandardOutput.ReadToEnd();
            procList.WaitForExit();

            var diskDict = new Dictionary<int, DriveSmartReport>();
            bool inDiskSection = false;
            foreach (var rawLine in listOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("<DISK LIST>")) { inDiskSection = true; continue; }
                if (line.StartsWith("<ARRAY LIST>") || line.StartsWith("<CONTROLLER LIST>")) { inDiskSection = false; continue; }
                if (!inDiskSection || line.StartsWith("Disk") || line.StartsWith("----")) continue;

                var mChan = System.Text.RegularExpressions.Regex.Match(line, @"\s(\d+:\d{2})\s+");
                if (mChan.Success)
                {
                    string beforeChan = line.Substring(0, mChan.Index).Trim();
                    string afterChan = line.Substring(mChan.Index + mChan.Length).Trim();
                    var headParts = System.Text.RegularExpressions.Regex.Split(beforeChan, @"\s+");
                    if (headParts.Length >= 1 && int.TryParse(headParts[0], out int diskId))
                    {
                        var tailParts = System.Text.RegularExpressions.Regex.Split(afterChan, @"\s{2,}");
                        string model = tailParts.Length >= 1 ? tailParts[0].Trim() : $"AMD-RAID Member {diskId}";
                        string fw = "";
                        string serial = "";
                        if (tailParts.Length >= 2)
                        {
                            var subParts = tailParts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (subParts.Length >= 2)
                            {
                                fw = subParts[0];
                                serial = subParts[1];
                            }
                            else if (subParts.Length == 1)
                            {
                                serial = subParts[0];
                            }
                        }
                        if (tailParts.Length >= 3)
                        {
                            serial = tailParts[2].Trim();
                        }

                        long sizeBytes = 1000204886016L;
                        if (beforeChan.Contains("1.0TB")) sizeBytes = 1000204886016L;

                        var report = new DriveSmartReport
                        {
                            DevicePath = $@"\\.\Scsi{adapterIndex}: Disk {diskId} ({serial})",
                            Model = model,
                            Serial = serial,
                            DiskSizeBytes = sizeBytes,
                            BusType = 8,
                            BusTypeName = "RAID (AMD RAIDXpert2)",
                            IsSsd = false,
                            IsRaidMember = true,
                            RaidControllerName = "AMD-RAID Storport",
                            RaidSlotNumber = diskId
                        };
                        diskDict[diskId] = report;
                    }
                }
            }

            if (diskDict.Count == 0) return;

            var psiSmart = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rcadmPath,
                Arguments = "-M -sg -d * -at",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var procSmart = System.Diagnostics.Process.Start(psiSmart);
            if (procSmart == null) return;
            string smartOut = procSmart.StandardOutput.ReadToEnd();
            procSmart.WaitForExit();

            int currentDisk = -1;
            foreach (var rawLine in smartOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                var matchDisk = System.Text.RegularExpressions.Regex.Match(line, @"\+\s*Disk\s*(\d+)\s*\+");
                if (matchDisk.Success)
                {
                    currentDisk = int.Parse(matchDisk.Groups[1].Value);
                    continue;
                }

                if (currentDisk >= 0 && diskDict.TryGetValue(currentDisk, out var rep) && line.StartsWith("- Attribute ID:"))
                {
                    var parts = line.Split(',');
                    if (parts.Length >= 4)
                    {
                        var mId = System.Text.RegularExpressions.Regex.Match(parts[0], @"0x([0-9a-fA-F]{2})");
                        int fP = parts[0].IndexOf('(');
                        int lP = parts[0].LastIndexOf(')');
                        string attrName = (fP >= 0 && lP > fP) ? parts[0].Substring(fP + 1, lP - fP - 1).Trim() : "Atributo SMART";

                        var mCur = System.Text.RegularExpressions.Regex.Match(parts[2], @"\d+");
                        var mRaw = System.Text.RegularExpressions.Regex.Match(parts[3], @"\d+");

                        if (mId.Success && mCur.Success && mRaw.Success)
                        {
                            byte id = Convert.ToByte(mId.Groups[1].Value, 16);
                            byte cur = byte.Parse(mCur.Value);
                            ulong rawVal = ulong.Parse(mRaw.Value);

                            rep.Attributes.Add(new SmartAttribute
                            {
                                Id = id,
                                Name = attrName,
                                Current = cur,
                                Worst = cur,
                                RawValue = rawVal,
                                Interpretation = Program.GetAtaInterpretation(id, rawVal, cur, cur)
                            });

                            if (id == 0xC2 || id == 0xBE)
                            {
                                rep.TemperatureCelsius = (int)(rawVal & 0xFF);
                            }
                            else if (id == 0x09)
                            {
                                rep.PowerOnHours = rawVal;
                            }
                            else if (id == 0x0C)
                            {
                                rep.PowerCycleCount = rawVal;
                            }
                        }
                    }
                }
            }

            if (!Program.SilentMode)
                Console.WriteLine($"  [RAID AMD RAIDXpert2]: Detectadas {diskDict.Count} unidade(s) fÃ­sicas no array.");

            foreach (var kvp in diskDict.OrderBy(k => k.Key))
            {
                VesperHealthCalculator.EvaluateDrive(kvp.Value);
                list.Add(kvp.Value);
            }
        }
        catch
        {
        }
    }

    private static void QueryMegaRaidDrives(SafeFileHandle handle, int adapterIndex, List<DriveSmartReport> list)
    {
        int srbSize = 28;
        int payloadSize = 4096;
        int totalSize = srbSize + payloadSize;
        IntPtr pBuf = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuf, i, 0);

            Marshal.WriteInt32(pBuf, 0, srbSize);
            byte[] sig = Encoding.ASCII.GetBytes("MegaRAID");
            Marshal.Copy(sig, 0, pBuf + 4, 8);
            Marshal.WriteInt32(pBuf, 12, 10);
            Marshal.WriteInt32(pBuf, 16, 0x00010000);
            Marshal.WriteInt32(pBuf, 24, payloadSize);

            Marshal.WriteInt32(pBuf + srbSize, 0x02010100);

            if (DeviceIoControl(handle, IOCTL_SCSI_MINIPORT, pBuf, (uint)totalSize, pBuf, (uint)totalSize, out uint returned, IntPtr.Zero))
            {
                uint retCode = (uint)Marshal.ReadInt32(pBuf, 20);
                if (retCode == 0 && returned >= srbSize + 4)
                {
                    uint driveCount = (uint)Marshal.ReadInt32(pBuf + srbSize + 4);
                    if (driveCount > 0 && driveCount <= 64)
                    {
                        if (!Program.SilentMode)
                            Console.WriteLine($"  [RAID MegaRAID/PERC]: Detectadas {driveCount} unidade(s) fÃ­sica(s) no enclosure.");

                        for (int d = 0; d < driveCount; d++)
                        {
                            int entryOffset = srbSize + 8 + (d * 32);
                            if (entryOffset + 32 > totalSize) break;

                            ushort devId = (ushort)Marshal.ReadInt16(pBuf + entryOffset);
                            byte slot = Marshal.ReadByte(pBuf + entryOffset + 2);

                            var report = new DriveSmartReport
                            {
                                DevicePath = $@"\\.\Scsi{adapterIndex}: Slot {slot} (DevId {devId})",
                                Model = $"MegaRAID Member Disk (Slot {slot})",
                                Serial = $"PD_{adapterIndex}_{slot}_{devId}",
                                BusType = 8,
                                BusTypeName = "RAID",
                                IsRaidMember = true,
                                RaidControllerName = "MegaRAID / Dell PERC",
                                RaidSlotNumber = slot
                            };

                            byte[]? smartData = QueryMegaRaidSmart(handle, devId);
                            if (smartData != null)
                            {
                                Program.ParseAtaSmartPayload(smartData, report);
                                list.Add(report);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    private static byte[]? QueryMegaRaidSmart(SafeFileHandle handle, ushort devId)
    {
        int srbSize = 28;
        int payloadSize = 512 + 64;
        int totalSize = srbSize + payloadSize;
        IntPtr pBuf = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuf, i, 0);

            Marshal.WriteInt32(pBuf, 0, srbSize);
            byte[] sig = Encoding.ASCII.GetBytes("MegaRAID");
            Marshal.Copy(sig, 0, pBuf + 4, 8);
            Marshal.WriteInt32(pBuf, 12, 10);
            Marshal.WriteInt32(pBuf, 16, 0x00010000);
            Marshal.WriteInt32(pBuf, 24, payloadSize);

            Marshal.WriteInt32(pBuf + srbSize, 0x02010200);
            Marshal.WriteInt16(pBuf + srbSize + 4, (short)devId);

            Marshal.WriteByte(pBuf + srbSize + 8, 0xD0);
            Marshal.WriteByte(pBuf + srbSize + 9, 0x01);
            Marshal.WriteByte(pBuf + srbSize + 10, 0x4F);
            Marshal.WriteByte(pBuf + srbSize + 11, 0xC2);
            Marshal.WriteByte(pBuf + srbSize + 12, 0xB0);

            if (DeviceIoControl(handle, IOCTL_SCSI_MINIPORT, pBuf, (uint)totalSize, pBuf, (uint)totalSize, out uint returned, IntPtr.Zero))
            {
                uint retCode = (uint)Marshal.ReadInt32(pBuf, 20);
                if (retCode == 0)
                {
                    byte[] smart = new byte[512];
                    Marshal.Copy(pBuf + srbSize + 64, smart, 0, 512);
                    return smart;
                }
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        return null;
    }

    private static void QueryCsmiDrives(SafeFileHandle handle, int adapterIndex, List<DriveSmartReport> list)
    {
        string[] sigs = new string[] { "CSMI    ", "CSMIARY ", "CSMIALL " };
        foreach (var sigName in sigs)
        {
            int srbSize = 28;
            int payloadSize = 2048;
            int totalSize = srbSize + payloadSize;
            IntPtr pBuf = Marshal.AllocHGlobal(totalSize);

            try
            {
                for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuf, i, 0);

                Marshal.WriteInt32(pBuf, 0, srbSize);
                byte[] sig = Encoding.ASCII.GetBytes(sigName);
                Marshal.Copy(sig, 0, pBuf + 4, 8);
                Marshal.WriteInt32(pBuf, 12, 10);
                Marshal.WriteInt32(pBuf, 16, 0x00000001);
                Marshal.WriteInt32(pBuf, 24, payloadSize);

                if (DeviceIoControl(handle, IOCTL_SCSI_MINIPORT, pBuf, (uint)totalSize, pBuf, (uint)totalSize, out uint returned, IntPtr.Zero))
                {
                    uint retCode = (uint)Marshal.ReadInt32(pBuf, 20);
                    if (retCode == 0)
                    {
                        if (!Program.SilentMode)
                            Console.WriteLine($"  [RAID CSMI ({sigName.Trim()})]: Controlador detectado em \\\\.\\Scsi{adapterIndex}:.");
                        break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
    }
}
