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
            Console.WriteLine("[RAID] Sondando controladoras de armazenamento...");
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
                Console.WriteLine("  No dedicated SCSI/RAID host adapter active on the system.");
            }
            else if (raidDrives.Count == 0)
            {
                Console.WriteLine("  Present controllers operating in direct HBA/AHCI mode (drives managed via PhysicalDrive).");
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
                return $"Storport Miniport with {numBuses} bus(es)";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pOut);
        }
        return "Generic SCSI Host Adapter";
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
                            Console.WriteLine($"  [RAID MegaRAID/PERC]: Detectadas {driveCount} unidade(s) fisica(s) no enclosure.");

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
                            Console.WriteLine($"  [RAID CSMI ({sigName.Trim()})]: Controller detected at \\\\.\\Scsi{adapterIndex}:.");
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
