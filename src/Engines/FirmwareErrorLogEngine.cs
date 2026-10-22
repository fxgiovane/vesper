using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public sealed class FirmwareErrorEntry
{
    public int EntryIndex { get; set; }
    public ulong ErrorCount { get; set; }
    public ulong FailingLba { get; set; }
    public ulong PowerOnHours { get; set; }
    public string CommandDescription { get; set; } = "";
    public string StatusDescription { get; set; } = "";
    public string ErrorDetails { get; set; } = "";
}

public sealed class FirmwareErrorLogReport
{
    public string DevicePath { get; set; } = "";
    public string Model { get; set; } = "";
    public string Protocol { get; set; } = "";
    public ulong TotalLoggedErrors { get; set; }
    public bool LogSupported { get; set; }
    public string SummaryText { get; set; } = "";
    public List<FirmwareErrorEntry> Entries { get; } = new();
}

public static class FirmwareErrorLogEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
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

    public static FirmwareErrorLogReport QueryErrorLog(DriveSmartReport report)
    {
        var result = new FirmwareErrorLogReport
        {
            DevicePath = report.DevicePath,
            Model = report.Model,
            Protocol = report.IsNvme ? "NVMe" : "ATA/SATA",
            LogSupported = false
        };

        if (string.IsNullOrEmpty(report.DevicePath))
        {
            result.SummaryText = "Invalid device path.";
            return result;
        }

        if (report.IsRaidMember && !report.DevicePath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            result.LogSupported = true;
            result.Protocol = "RAID / SMART Pass-Through";
            var criticalAttrs = new (byte id, string name)[]
            {
                (0x05, "Reallocated Sectors Count"),
                (0xC5, "Current Pending Sector Count"),
                (0xC6, "Offline Uncorrectable Sector Count"),
                (0xC7, "UltraDMA CRC Error Count")
            };

            foreach (var (id, name) in criticalAttrs)
            {
                var attr = report.Attributes.FirstOrDefault(a => a.Id == id);
                if (attr != null && attr.RawValue > 0)
                {
                    result.Entries.Add(new FirmwareErrorEntry
                    {
                        EntryIndex = result.Entries.Count + 1,
                        ErrorCount = (ulong)(result.Entries.Count + 1),
                        FailingLba = 0,
                        PowerOnHours = (uint)report.PowerOnHours,
                        CommandDescription = $"SMART ID 0x{id:X2} ({name})",
                        StatusDescription = $"Raw Value: {attr.RawValue} (Current: {attr.Current}, Worst: {attr.Worst})",
                        ErrorDetails = $"Reliability attribute with active events ({attr.Interpretation})"
                    });
                }
            }

            result.TotalLoggedErrors = (ulong)result.Entries.Count;
            if (result.Entries.Count == 0)
            {
                result.SummaryText = "0 errors logged (clean SMART log).";
            }
            else
            {
                result.SummaryText = $"Detected {result.Entries.Count} anomalies in drive SMART attributes.";
            }
            return result;
        }

        using var handle = CreateFile(
            report.DevicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        SafeFileHandle activeHandle = handle;
        SafeFileHandle? fallbackHandle = null;

        if (activeHandle.IsInvalid)
        {
            fallbackHandle = CreateFile(
                report.DevicePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            activeHandle = fallbackHandle;
        }

        using (fallbackHandle)
        {
            if (activeHandle.IsInvalid)
            {
                result.SummaryText = "Access denied opening device handle. Run as Administrator.";
                return result;
            }

            if (report.IsNvme)
            {
                ReadNvmeErrorLog(activeHandle, result);
            }
            else
            {
                ReadAtaErrorLog(activeHandle, result);
            }
        }

        return result;
    }

    private static void ReadNvmeErrorLog(SafeFileHandle handle, FirmwareErrorLogReport report)
    {
        int bufSize = 8192;
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
                    Marshal.WriteInt32(pBuf, protoOffset + 8, 1);
                    Marshal.WriteInt32(pBuf, protoOffset + 12, 0);
                    Marshal.WriteInt32(pBuf, protoOffset + 16, 40);
                    Marshal.WriteInt32(pBuf, protoOffset + 20, 4096);

                    if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufSize, pBuf, (uint)bufSize, out uint ret, IntPtr.Zero) && ret > 0)
                    {
                        int dataOff = Marshal.ReadInt32(pBuf, protoOffset + 16);
                        int actualOffset = protoOffset + dataOff;

                        if (actualOffset + 64 <= bufSize)
                        {
                            report.LogSupported = true;
                            int totalEntries = 64;

                            for (int e = 0; e < totalEntries; e++)
                            {
                                int entryOffset = actualOffset + (e * 64);
                                if (entryOffset + 64 > bufSize) break;

                                byte[] entryBytes = new byte[64];
                                Marshal.Copy(pBuf + entryOffset, entryBytes, 0, 64);

                                ulong errorCount = BitConverter.ToUInt64(entryBytes, 0);
                                if (errorCount == 0) continue;

                                ushort sqId = BitConverter.ToUInt16(entryBytes, 8);
                                ushort cmdId = BitConverter.ToUInt16(entryBytes, 10);
                                ushort statusField = BitConverter.ToUInt16(entryBytes, 12);
                                ushort paramLoc = BitConverter.ToUInt16(entryBytes, 14);
                                ulong lba = BitConverter.ToUInt64(entryBytes, 16);
                                uint nsId = BitConverter.ToUInt32(entryBytes, 24);

                                int statusCodeType = (statusField >> 9) & 0x07;
                                int statusCode = (statusField >> 1) & 0xFF;

                                string statusDesc = DecodeNvmeStatus(statusCodeType, statusCode);
                                string cmdDesc = $"SubQ:{sqId} CmdId:0x{cmdId:X4} NS:{nsId}";
                                string errDetails = $"ParamLoc:0x{paramLoc:X4} SC:0x{statusCode:X2} SCT:{statusCodeType}";

                                report.Entries.Add(new FirmwareErrorEntry
                                {
                                    EntryIndex = report.Entries.Count + 1,
                                    ErrorCount = errorCount,
                                    FailingLba = lba,
                                    PowerOnHours = 0,
                                    CommandDescription = cmdDesc,
                                    StatusDescription = statusDesc,
                                    ErrorDetails = errDetails
                                });
                            }

                            report.TotalLoggedErrors = (ulong)report.Entries.Count;
                            if (report.Entries.Count == 0)
                            {
                                report.SummaryText = "0 errors logged (NVMe Log Page 0x01).";
                            }
                            else
                            {
                                report.SummaryText = $"{report.Entries.Count} errors logged in NVMe log (Log Page 0x01).";
                            }
                            return;
                        }
                    }
                }
            }

            report.LogSupported = false;
            report.SummaryText = "NVMe Error Information Log unavailable or not exposed by controller driver.";
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    private static void ReadAtaErrorLog(SafeFileHandle handle, FirmwareErrorLogReport report)
    {
        byte[] satCdb = new byte[16];
        satCdb[0] = 0x85;
        satCdb[1] = (4 << 1);
        satCdb[2] = 0x0E;
        satCdb[4] = 0xD0;
        satCdb[6] = 0x01;
        satCdb[8] = 0x01;
        satCdb[10] = 0x4F;
        satCdb[12] = 0xC2;
        satCdb[14] = 0x2F;

        byte[] dataBuf = new byte[512];
        bool ok = ExecuteScsiPassThrough(handle, satCdb, 16, dataBuf);

        if (ok)
        {
            report.LogSupported = true;
            byte logVersion = dataBuf[0];
            byte errorIndex = dataBuf[1];

            for (int e = 0; e < 5; e++)
            {
                int baseOff = 2 + (e * 90);
                if (baseOff + 90 > dataBuf.Length) break;

                byte cmdCode = dataBuf[baseOff + 15];
                byte errReg = dataBuf[baseOff + 9];
                byte statusReg = dataBuf[baseOff + 8];

                if (cmdCode == 0 && errReg == 0 && statusReg == 0) continue;

                ulong lba = (ulong)(dataBuf[baseOff + 11] | (dataBuf[baseOff + 12] << 8) | (dataBuf[baseOff + 13] << 16) | ((dataBuf[baseOff + 14] & 0x0F) << 24));
                uint hours = BitConverter.ToUInt32(dataBuf, baseOff + 2);

                string cmdDesc = DecodeAtaCommand(cmdCode);
                string statusDesc = DecodeAtaError(errReg, statusReg);

                report.Entries.Add(new FirmwareErrorEntry
                {
                    EntryIndex = report.Entries.Count + 1,
                    ErrorCount = (ulong)(report.Entries.Count + 1),
                    FailingLba = lba,
                    PowerOnHours = hours,
                    CommandDescription = cmdDesc,
                    StatusDescription = statusDesc,
                    ErrorDetails = $"Status:0x{statusReg:X2} Error:0x{errReg:X2}"
                });
            }

            report.TotalLoggedErrors = (ulong)report.Entries.Count;
            if (report.Entries.Count == 0)
            {
                report.SummaryText = "0 errors logged (ATA Log Page 0x01).";
            }
            else
            {
                report.SummaryText = $"{report.Entries.Count} errors logged in ATA log (Log Page 0x01).";
            }
            return;
        }

        report.LogSupported = false;
        report.SummaryText = "ATA Error Log (Log Page 0x01) did not respond via SCSI/ATA transport.";
    }

    private static bool ExecuteScsiPassThrough(SafeFileHandle handle, byte[] cdb, byte cdbLength, byte[] dataBuf)
    {
        Array.Clear(dataBuf, 0, dataBuf.Length);
        int headerSize = 48;
        int totalSize = headerSize + dataBuf.Length;
        IntPtr pBuffer = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuffer, i, 0);

            Marshal.WriteInt16(pBuffer, 0, (short)headerSize);
            Marshal.WriteByte(pBuffer, 2, 0);
            Marshal.WriteByte(pBuffer, 3, cdbLength);
            Marshal.WriteByte(pBuffer, 4, 0);
            Marshal.WriteByte(pBuffer, 5, 1);
            Marshal.WriteInt32(pBuffer, 8, dataBuf.Length);
            Marshal.WriteInt32(pBuffer, 12, 10);
            Marshal.WriteIntPtr(pBuffer, 16, pBuffer + headerSize);

            for (int i = 0; i < cdbLength; i++)
            {
                Marshal.WriteByte(pBuffer, 32 + i, cdb[i]);
            }

            if (DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH, pBuffer, (uint)totalSize, pBuffer, (uint)totalSize, out uint returned, IntPtr.Zero))
            {
                Marshal.Copy(pBuffer + headerSize, dataBuf, 0, dataBuf.Length);
                return true;
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuffer);
        }
        return false;
    }

    private static string DecodeNvmeStatus(int sct, int sc)
    {
        if (sct == 0)
        {
            return sc switch
            {
                0x00 => "Successful Completion",
                0x01 => "Invalid Command Opcode",
                0x02 => "Invalid Field in Command",
                0x04 => "Data Transfer Error",
                0x05 => "Commands Aborted due to Power Loss",
                0x06 => "Internal Error",
                0x07 => "Command Abort Requested",
                _ => $"Generic Error (0x{sc:X2})"
            };
        }
        if (sct == 1)
        {
            return sc switch
            {
                0x80 => "Conflicting Attributes",
                0x81 => "Invalid Protection Information",
                0x82 => "Attempted Write to Read-Only Range",
                _ => $"Command Specific Error (0x{sc:X2})"
            };
        }
        if (sct == 2)
        {
            return sc switch
            {
                0x80 => "Write Fault / Flash Wear Out",
                0x81 => "Unrecovered Read Error (UNC)",
                0x82 => "End-to-End Guard Check Error",
                0x83 => "End-to-End Application Tag Check Error",
                0x84 => "End-to-End Reference Tag Check Error",
                0x85 => "Compare Failure",
                0x86 => "Access Denied / Deallocated LBA",
                _ => $"Media/Data Integrity Error (0x{sc:X2})"
            };
        }
        return $"Hardware Status (SCT:{sct} SC:0x{sc:X2})";
    }

    private static string DecodeAtaCommand(byte cmd)
    {
        return cmd switch
        {
            0x20 => "READ SECTORS",
            0x24 => "READ SECTORS EXT",
            0x25 => "READ DMA EXT",
            0x29 => "READ MULTIPLE EXT",
            0x30 => "WRITE SECTORS",
            0x34 => "WRITE SECTORS EXT",
            0x35 => "WRITE DMA EXT",
            0x39 => "WRITE MULTIPLE EXT",
            0x60 => "READ FPDMA QUEUED (NCQ)",
            0x61 => "WRITE FPDMA QUEUED (NCQ)",
            0xC8 => "READ DMA",
            0xCA => "WRITE DMA",
            0xEA => "FLUSH CACHE EXT",
            0xB0 => "SMART COMMAND",
            _ => $"Opcode 0x{cmd:X2}"
        };
    }

    private static string DecodeAtaError(byte err, byte status)
    {
        var sb = new StringBuilder();
        if ((err & 0x40) != 0) sb.Append("UNC(Uncorrectable Data) ");
        if ((err & 0x20) != 0) sb.Append("MC(Media Changed) ");
        if ((err & 0x10) != 0) sb.Append("IDNF(ID Not Found) ");
        if ((err & 0x08) != 0) sb.Append("MCR(Media Change Request) ");
        if ((err & 0x04) != 0) sb.Append("ABRT(Aborted Command) ");
        if ((err & 0x02) != 0) sb.Append("NM(No Media) ");
        if ((err & 0x01) != 0) sb.Append("ILI(Illegal Length) ");

        if ((status & 0x01) != 0) sb.Append("[ERR] ");
        if ((status & 0x20) != 0) sb.Append("[DF/DeviceFault] ");

        return sb.Length > 0 ? sb.ToString().Trim() : "No error details";
    }
}
