using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vesper;

public enum SelfTestMode : byte
{
    Short = 1,
    Extended = 2,
    Abort = 0
}

public sealed class SelfTestStatus
{
    public bool IsInProgress { get; set; }
    public int PercentRemaining { get; set; }
    public string Message { get; set; } = "";
    public bool Succeeded { get; set; }
    public bool Failed { get; set; }
}

public sealed class FirmwareSelfTestEntry
{
    public int Index { get; set; }
    public string TestType { get; set; } = "";
    public string ResultMessage { get; set; } = "";
    public bool Succeeded { get; set; }
    public ulong PowerOnHours { get; set; }
    public ulong FailingLba { get; set; }
    public bool HasFailingLba { get; set; }
}

public static class FirmwareSelfTestEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_ATA_PASS_THROUGH = 0x04D02C;
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x04D014;
    private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x002DD3C0;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

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
    private struct STORAGE_PROTOCOL_COMMAND
    {
        public uint Version;
        public uint Length;
        public uint ProtocolType;
        public uint Flags;
        public uint ReturnStatus;
        public uint ErrorCode;
        public uint CommandLength;
        public uint ErrorInfoLength;
        public uint DataToDeviceTransferLength;
        public uint DataFromDeviceTransferLength;
        public uint TimeOutValue;
        public uint ErrorInfoOffset;
        public uint DataToDeviceBufferOffset;
        public uint DataFromDeviceBufferOffset;
        public uint CommandSpecific;
        public uint Reserved0;
        public uint FixedProtocolReturnData;
        public uint FixedProtocolReturnData2;
        public uint Reserved1;
        public uint Reserved2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] Command;
    }

    public static SelfTestStatus QuerySelfTestStatus(DriveSmartReport rep)
    {
        var status = new SelfTestStatus();
        if (rep == null || string.IsNullOrEmpty(rep.DevicePath))
        {
            status.Message = "Device unavailable.";
            return status;
        }

        if (!rep.IsRaidMember && (rep.Model.Contains("RAID Array", StringComparison.OrdinalIgnoreCase) || rep.BusTypeName == "RAID"))
        {
            status.IsInProgress = false;
            status.PercentRemaining = 100;
            status.Message = "Not supported on virtual RAID volume.";
            return status;
        }

        using SafeFileHandle hDevice = CreateFileW(
            rep.DevicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hDevice.IsInvalid)
        {
            status.Message = "Access unavailable.";
            return status;
        }

        if (rep.IsNvme)
        {
            return QueryNvmeSelfTestStatus(hDevice);
        }
        else
        {
            return QueryAtaSelfTestStatus(hDevice);
        }
    }

    private static SelfTestStatus QueryNvmeSelfTestStatus(SafeFileHandle hDevice)
    {
        var status = new SelfTestStatus();
        int bufSize = 4096;
        IntPtr pBuf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuf, i, 0);

            uint[] propertyIds = new uint[] { 50, 49 };
            int protoOffset = 8;

            foreach (uint propId in propertyIds)
            {
                for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuf, i, 0);

                Marshal.WriteInt32(pBuf, 0, (int)propId);
                Marshal.WriteInt32(pBuf, 4, 0);

                Marshal.WriteInt32(pBuf, protoOffset + 0, 3);
                Marshal.WriteInt32(pBuf, protoOffset + 4, 2);
                Marshal.WriteInt32(pBuf, protoOffset + 8, 6);
                Marshal.WriteInt32(pBuf, protoOffset + 12, 0);
                Marshal.WriteInt32(pBuf, protoOffset + 16, 40);
                Marshal.WriteInt32(pBuf, protoOffset + 20, 512);

                if (DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufSize, pBuf, (uint)bufSize, out uint ret, IntPtr.Zero) && ret > 0)
                {
                    int dataOff = Marshal.ReadInt32(pBuf, protoOffset + 16);
                    int actualOffset = protoOffset + dataOff;
                    if (actualOffset + 512 <= bufSize)
                    {
                        byte currOp = Marshal.ReadByte(pBuf, actualOffset + 0);
                        byte completion = Marshal.ReadByte(pBuf, actualOffset + 1);
                        if (currOp != 0)
                        {
                            status.IsInProgress = true;
                            status.PercentRemaining = Math.Clamp(100 - completion, 0, 100);
                            status.Message = $"RUNNING ({completion}% done)";
                            return status;
                        }

                        byte resultStatus = (byte)(Marshal.ReadByte(pBuf, actualOffset + 4) & 0x0F);
                        status.IsInProgress = false;
                        status.PercentRemaining = 0;
                        status.Succeeded = resultStatus == 0;
                        status.Failed = resultStatus > 0;
                        status.Message = status.Succeeded ? "COMPLETED (OK)" : $"FAILED (0x{resultStatus:X2})";
                        return status;
                    }
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        status.IsInProgress = false;
        status.PercentRemaining = 100;
        status.Failed = true;
        status.Message = "SMART command not supported by bus.";
        return status;
    }

    private static SelfTestStatus QueryAtaSelfTestStatus(SafeFileHandle hDevice)
    {
        var status = new SelfTestStatus();
        int dataSize = 512;
        int pteSize = Marshal.SizeOf<ATA_PASS_THROUGH_EX>();
        int totalSize = pteSize + dataSize;
        IntPtr pBuf = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuf, i, 0);

            var pte = new ATA_PASS_THROUGH_EX
            {
                Length = (ushort)pteSize,
                AtaFlags = 0x02 | 0x01,
                DataTransferLength = (uint)dataSize,
                TimeOutValue = 5,
                DataBufferOffset = (IntPtr)pteSize,
                PreviousTaskFile = new byte[8],
                CurrentTaskFile = new byte[8]
            };
            pte.CurrentTaskFile[0] = 0xD0;
            pte.CurrentTaskFile[1] = 0x01;
            pte.CurrentTaskFile[2] = 0x01;
            pte.CurrentTaskFile[3] = 0x4F;
            pte.CurrentTaskFile[4] = 0xC2;
            pte.CurrentTaskFile[5] = 0xA0;
            pte.CurrentTaskFile[6] = 0xB0;

            Marshal.StructureToPtr(pte, pBuf, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_ATA_PASS_THROUGH, pBuf, (uint)totalSize, pBuf, (uint)totalSize, out _, IntPtr.Zero);
            if (ok)
            {
                byte statusByte = Marshal.ReadByte(pBuf, pteSize + 363);
                byte hi = (byte)((statusByte >> 4) & 0x0F);
                byte lo = (byte)(statusByte & 0x0F);

                if (hi == 0x0F)
                {
                    status.IsInProgress = true;
                    status.PercentRemaining = Math.Clamp((10 - lo) * 10, 0, 100);
                    status.Message = $"RUNNING ({100 - status.PercentRemaining}% done)";
                    return status;
                }

                status.IsInProgress = false;
                status.PercentRemaining = 0;
                status.Succeeded = hi == 0;
                status.Failed = hi > 0 && hi <= 8;
                status.Message = status.Succeeded ? "COMPLETED (OK)" : $"FAILED (0x{hi:X2})";
                return status;
            }
            else
            {
                int satSize = Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>();
                IntPtr pSat = Marshal.AllocHGlobal(satSize);
                IntPtr pSatData = Marshal.AllocHGlobal(512);
                try
                {
                    for (int i = 0; i < 512; i++) Marshal.WriteByte(pSatData, i, 0);

                    var sat = new SCSI_PASS_THROUGH_DIRECT
                    {
                        Length = (ushort)satSize,
                        CdbLength = 16,
                        DataIn = 1,
                        DataTransferLength = 512,
                        TimeOutValue = 5,
                        DataBuffer = pSatData,
                        Cdb = new byte[16]
                    };
                    sat.Cdb[0] = 0x85;
                    sat.Cdb[1] = (4 << 1);
                    sat.Cdb[2] = 0x2E;
                    sat.Cdb[3] = 0x00;
                    sat.Cdb[4] = 0xD0;
                    sat.Cdb[6] = 0x01;
                    sat.Cdb[8] = 0x4F;
                    sat.Cdb[10] = 0xC2;
                    sat.Cdb[12] = 0xA0;
                    sat.Cdb[14] = 0xB0;

                    Marshal.StructureToPtr(sat, pSat, false);
                    ok = DeviceIoControl(hDevice, IOCTL_SCSI_PASS_THROUGH_DIRECT, pSat, (uint)satSize, pSat, (uint)satSize, out _, IntPtr.Zero);
                    if (ok)
                    {
                        byte statusByte = Marshal.ReadByte(pSatData, 363);
                        byte hi = (byte)((statusByte >> 4) & 0x0F);
                        byte lo = (byte)(statusByte & 0x0F);

                        if (hi == 0x0F)
                        {
                            status.IsInProgress = true;
                            status.PercentRemaining = Math.Clamp((10 - lo) * 10, 0, 100);
                            status.Message = $"RUNNING ({100 - status.PercentRemaining}% done)";
                            return status;
                        }

                        status.IsInProgress = false;
                        status.PercentRemaining = 0;
                        status.Succeeded = hi == 0;
                        status.Failed = hi > 0 && hi <= 8;
                        status.Message = status.Succeeded ? "COMPLETED (OK)" : $"FAILED (0x{hi:X2})";
                        return status;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pSat);
                    Marshal.FreeHGlobal(pSatData);
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        status.IsInProgress = false;
        status.PercentRemaining = 100;
        status.Failed = true;
        status.Message = "SMART command not supported by bus.";
        return status;
    }

    public static bool ExecuteSelfTest(string devicePath, bool isNvme, bool isScsi, SelfTestMode mode, out string message)
    {
        using SafeFileHandle hDevice = CreateFileW(
            devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hDevice.IsInvalid)
        {
            message = $"Access denied or device unavailable at {devicePath}. Error: {Marshal.GetLastWin32Error()}";
            return false;
        }

        if (isNvme)
        {
            return ExecuteNvmeSelfTest(hDevice, mode, out message);
        }
        else if (isScsi)
        {
            return ExecuteScsiSelfTest(hDevice, mode, out message);
        }
        else
        {
            return ExecuteAtaSelfTest(hDevice, mode, out message);
        }
    }

    private static bool ExecuteAtaSelfTest(SafeFileHandle hDevice, SelfTestMode mode, out string message)
    {
        byte subCommand = mode switch
        {
            SelfTestMode.Short => 0x01,
            SelfTestMode.Extended => 0x02,
            _ => 0x7F
        };

        var pte = new ATA_PASS_THROUGH_EX
        {
            Length = (ushort)Marshal.SizeOf<ATA_PASS_THROUGH_EX>(),
            AtaFlags = 0x01,
            DataTransferLength = 0,
            TimeOutValue = 10,
            DataBufferOffset = IntPtr.Zero,
            PreviousTaskFile = new byte[8],
            CurrentTaskFile = new byte[8]
        };

        pte.CurrentTaskFile[0] = 0xD4;
        pte.CurrentTaskFile[1] = 0x00;
        pte.CurrentTaskFile[2] = subCommand;
        pte.CurrentTaskFile[3] = 0x4F;
        pte.CurrentTaskFile[4] = 0xC2;
        pte.CurrentTaskFile[5] = 0xA0;
        pte.CurrentTaskFile[6] = 0xB0;
        pte.CurrentTaskFile[7] = 0x00;

        int size = Marshal.SizeOf(pte);
        IntPtr pBuf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(pte, pBuf, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_ATA_PASS_THROUGH, pBuf, (uint)size, pBuf, (uint)size, out _, IntPtr.Zero);
            if (!ok)
            {
                var sat = new SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
                    CdbLength = 16,
                    DataIn = 0,
                    TimeOutValue = 10,
                    Cdb = new byte[16]
                };
                sat.Cdb[0] = 0x85;
                sat.Cdb[1] = (3 << 1);
                sat.Cdb[2] = 0x20;
                sat.Cdb[3] = 0x00;
                sat.Cdb[4] = 0xD4;
                sat.Cdb[6] = subCommand;
                sat.Cdb[8] = 0x4F;
                sat.Cdb[10] = 0xC2;
                sat.Cdb[12] = 0xA0;
                sat.Cdb[14] = 0xB0;

                int satSize = Marshal.SizeOf(sat);
                IntPtr pSat = Marshal.AllocHGlobal(satSize);
                try
                {
                    Marshal.StructureToPtr(sat, pSat, false);
                    ok = DeviceIoControl(hDevice, IOCTL_SCSI_PASS_THROUGH_DIRECT, pSat, (uint)satSize, pSat, (uint)satSize, out _, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(pSat);
                }

                if (!ok)
                {
                    message = $"SMART command failed (0xB0/0xD4/{subCommand:X2}). Win32 error: {Marshal.GetLastWin32Error()}";
                    return false;
                }
            }

            message = mode == SelfTestMode.Abort ? "ATA diagnostics cancelled." : $"ATA {mode} diagnostics started.";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    private static bool ExecuteScsiSelfTest(SafeFileHandle hDevice, SelfTestMode mode, out string message)
    {
        var spt = new SCSI_PASS_THROUGH_DIRECT
        {
            Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
            CdbLength = 6,
            DataIn = 0,
            TimeOutValue = 10,
            Cdb = new byte[16]
        };

        byte selfTestCode = mode switch
        {
            SelfTestMode.Short => 0x20,
            SelfTestMode.Extended => 0x40,
            _ => 0x80
        };

        spt.Cdb[0] = 0x1D;
        spt.Cdb[1] = selfTestCode;

        int size = Marshal.SizeOf(spt);
        IntPtr pBuf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(spt, pBuf, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_SCSI_PASS_THROUGH_DIRECT, pBuf, (uint)size, pBuf, (uint)size, out _, IntPtr.Zero);
            if (!ok)
            {
                message = $"SCSI SEND DIAGNOSTIC (0x1D) failed. Win32 error: {Marshal.GetLastWin32Error()}";
                return false;
            }
            message = mode == SelfTestMode.Abort ? "SCSI diagnostics cancelled." : $"SCSI {mode} diagnostics started.";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    private static bool ExecuteNvmeSelfTest(SafeFileHandle hDevice, SelfTestMode mode, out string message)
    {
        int cmdSize = Marshal.SizeOf<STORAGE_PROTOCOL_COMMAND>();
        IntPtr pCmd = Marshal.AllocHGlobal(cmdSize);
        try
        {
            var cmd = new STORAGE_PROTOCOL_COMMAND
            {
                Version = 1,
                Length = (uint)cmdSize,
                ProtocolType = 3,
                Flags = 0x80000000,
                CommandSpecific = 1,
                CommandLength = 64,
                TimeOutValue = 10,
                Command = new byte[64]
            };

            byte stc = mode switch
            {
                SelfTestMode.Short => 0x01,
                SelfTestMode.Extended => 0x02,
                _ => 0x0F
            };

            cmd.Command[0] = 0x14;
            cmd.Command[4] = 0xFF;
            cmd.Command[5] = 0xFF;
            cmd.Command[6] = 0xFF;
            cmd.Command[7] = 0xFF;
            cmd.Command[40] = stc;

            Marshal.StructureToPtr(cmd, pCmd, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND, pCmd, (uint)cmdSize, pCmd, (uint)cmdSize, out _, IntPtr.Zero);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 1 || err == 50 || err == 87)
                {
                    message = "Self-test not supported by NVMe controller on this drive.";
                }
                else if (err == 5)
                {
                    message = "Access denied. Administrator privileges required.";
                }
                else
                {
                    message = $"NVMe Self-Test command (0x14) failed. Win32 error: {err}";
                }
                return false;
            }
            message = mode == SelfTestMode.Abort ? "NVMe diagnostics cancelled." : $"NVMe {mode} diagnostics started.";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pCmd);
        }
    }

    public static List<FirmwareSelfTestEntry> QuerySelfTestLog(DriveSmartReport rep)
    {
        var list = new List<FirmwareSelfTestEntry>();
        if (rep == null || string.IsNullOrEmpty(rep.DevicePath)) return list;
        if (!rep.IsRaidMember && (rep.Model.Contains("RAID Array", StringComparison.OrdinalIgnoreCase) || rep.BusTypeName == "RAID"))
            return list;

        using SafeFileHandle hDevice = CreateFileW(
            rep.DevicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hDevice.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Failed to open physical device {rep.DevicePath} for firmware self-test log query. Win32 error: {err}");
        }

        if (rep.IsNvme)
        {
            return QueryNvmeSelfTestLog(hDevice);
        }
        else
        {
            bool isScsiOrUsb = rep.BusTypeName == "SCSI" || rep.BusTypeName == "USB" || rep.BusTypeName == "RAID";
            var entries = QueryAtaSelfTestLog(hDevice);
            if (entries.Count == 0 && isScsiOrUsb)
            {
                entries = QueryScsiSatSelfTestLog(hDevice);
            }
            return entries;
        }
    }

    private static List<FirmwareSelfTestEntry> QueryNvmeSelfTestLog(SafeFileHandle hDevice)
    {
        var list = new List<FirmwareSelfTestEntry>();
        int bufSize = 4096;
        IntPtr pBuf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuf, i, 0);

            uint[] propertyIds = new uint[] { 50, 49 };
            int protoOffset = 8;

            foreach (uint propId in propertyIds)
            {
                for (int i = 0; i < bufSize; i++) Marshal.WriteByte(pBuf, i, 0);

                Marshal.WriteInt32(pBuf, 0, (int)propId);
                Marshal.WriteInt32(pBuf, 4, 0);

                Marshal.WriteInt32(pBuf, protoOffset + 0, 3);
                Marshal.WriteInt32(pBuf, protoOffset + 4, 2);
                Marshal.WriteInt32(pBuf, protoOffset + 8, 6);
                Marshal.WriteInt32(pBuf, protoOffset + 12, 0);
                Marshal.WriteInt32(pBuf, protoOffset + 16, 40);
                Marshal.WriteInt32(pBuf, protoOffset + 20, 512);

                if (DeviceIoControl(hDevice, IOCTL_STORAGE_QUERY_PROPERTY, pBuf, (uint)bufSize, pBuf, (uint)bufSize, out uint ret, IntPtr.Zero) && ret > 0)
                {
                    int dataOff = Marshal.ReadInt32(pBuf, protoOffset + 16);
                    int actualOffset = protoOffset + dataOff;
                    if (actualOffset + 512 <= bufSize)
                    {
                        for (int idx = 0; idx < 20; idx++)
                        {
                            int entryOff = actualOffset + 4 + (idx * 28);
                            byte statusByte = Marshal.ReadByte(pBuf, entryOff + 0);
                            if (statusByte == 0 && Marshal.ReadByte(pBuf, entryOff + 1) == 0 && Marshal.ReadInt64(pBuf, entryOff + 4) == 0)
                                continue;

                            byte opCode = (byte)((statusByte >> 4) & 0x0F);
                            byte resCode = (byte)(statusByte & 0x0F);

                            string testType = opCode switch
                            {
                                1 => "Short",
                                2 => "Extended",
                                0xE => "Vendor Specific",
                                _ => $"Op 0x{opCode:X}"
                            };

                            bool succeeded = resCode == 0;
                            string resMsg = resCode switch
                            {
                                0 => "Completed successfully",
                                1 => "Aborted by host",
                                2 => "Interrupted by reset",
                                3 => "Unknown failure",
                                4 => "Electrical segment failure",
                                5 => "Read element failure",
                                6 => "Write element failure",
                                7 => "Media failure / unreadable sector",
                                _ => $"Failed (Code 0x{resCode:X})"
                            };

                            ulong poh = (ulong)Marshal.ReadInt64(pBuf, entryOff + 4);
                            ulong failLba = (ulong)Marshal.ReadInt64(pBuf, entryOff + 12);

                            list.Add(new FirmwareSelfTestEntry
                            {
                                Index = idx + 1,
                                TestType = testType,
                                ResultMessage = resMsg,
                                Succeeded = succeeded,
                                PowerOnHours = poh,
                                FailingLba = failLba,
                                HasFailingLba = resCode >= 4 && failLba > 0
                            });
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
        return list;
    }

    private static List<FirmwareSelfTestEntry> QueryAtaSelfTestLog(SafeFileHandle hDevice)
    {
        var list = new List<FirmwareSelfTestEntry>();
        int dataSize = 512;
        int pteSize = Marshal.SizeOf<ATA_PASS_THROUGH_EX>();
        int totalSize = pteSize + dataSize;
        IntPtr pBuf = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(pBuf, i, 0);

            var pte = new ATA_PASS_THROUGH_EX
            {
                Length = (ushort)pteSize,
                AtaFlags = 0x02 | 0x01,
                DataTransferLength = (uint)dataSize,
                TimeOutValue = 5,
                DataBufferOffset = (IntPtr)pteSize,
                PreviousTaskFile = new byte[8],
                CurrentTaskFile = new byte[8]
            };
            pte.CurrentTaskFile[0] = 0xD5;
            pte.CurrentTaskFile[1] = 0x01;
            pte.CurrentTaskFile[2] = 0x06;
            pte.CurrentTaskFile[3] = 0x4F;
            pte.CurrentTaskFile[4] = 0xC2;
            pte.CurrentTaskFile[5] = 0xA0;
            pte.CurrentTaskFile[6] = 0xB0;

            Marshal.StructureToPtr(pte, pBuf, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_ATA_PASS_THROUGH, pBuf, (uint)totalSize, pBuf, (uint)totalSize, out _, IntPtr.Zero);
            if (ok)
            {
                for (int idx = 0; idx < 21; idx++)
                {
                    int off = pteSize + 2 + (idx * 24);
                    byte contentByte = Marshal.ReadByte(pBuf, off + 0);
                    byte statusByte = Marshal.ReadByte(pBuf, off + 1);
                    if (contentByte == 0 && statusByte == 0) continue;

                    byte resNibble = (byte)((statusByte >> 4) & 0x0F);
                    string testType = (contentByte & 0x7F) switch
                    {
                        1 => "Short",
                        2 => "Extended",
                        _ => $"Op 0x{contentByte:X2}"
                    };

                    bool succeeded = resNibble == 0;
                    string resMsg = resNibble switch
                    {
                        0 => "Completed without error",
                        1 => "Aborted by host",
                        2 => "Interrupted by hardware reset",
                        3 => "Fatal failure / unknown element",
                        4 => "Unknown hardware failure",
                        5 => "Electrical failure",
                        6 => "Servo / mechanical failure",
                        7 => "Read sector failure",
                        8 => "Handling failure / physical damage",
                        _ => $"Failed (Status 0x{resNibble:X})"
                    };

                    ushort lifeHours = (ushort)(Marshal.ReadByte(pBuf, off + 2) | (Marshal.ReadByte(pBuf, off + 3) << 8));
                    uint failLba = (uint)(Marshal.ReadByte(pBuf, off + 5) | (Marshal.ReadByte(pBuf, off + 6) << 8) | (Marshal.ReadByte(pBuf, off + 7) << 16) | (Marshal.ReadByte(pBuf, off + 8) << 24));

                    list.Add(new FirmwareSelfTestEntry
                    {
                        Index = idx + 1,
                        TestType = testType,
                        ResultMessage = resMsg,
                        Succeeded = succeeded,
                        PowerOnHours = lifeHours,
                        FailingLba = failLba,
                        HasFailingLba = resNibble >= 4 && failLba > 0
                    });
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
        return list;
    }

    private static List<FirmwareSelfTestEntry> QueryScsiSatSelfTestLog(SafeFileHandle hDevice)
    {
        var list = new List<FirmwareSelfTestEntry>();
        int dataSize = 512;
        int sptdSize = Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>();
        IntPtr pData = Marshal.AllocHGlobal(dataSize);
        IntPtr pSptd = Marshal.AllocHGlobal(sptdSize);

        try
        {
            for (int i = 0; i < dataSize; i++) Marshal.WriteByte(pData, i, 0);
            for (int i = 0; i < sptdSize; i++) Marshal.WriteByte(pSptd, i, 0);

            var sptd = new SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)sptdSize,
                CdbLength = 16,
                DataIn = 1,
                DataTransferLength = (uint)dataSize,
                TimeOutValue = 5,
                DataBuffer = pData,
                Cdb = new byte[16]
            };

            sptd.Cdb[0] = 0x85;
            sptd.Cdb[1] = (4 << 1) | 0;
            sptd.Cdb[2] = 0x0E;
            sptd.Cdb[4] = 0xD5;
            sptd.Cdb[6] = 0x01;
            sptd.Cdb[8] = 0x06;
            sptd.Cdb[10] = 0x4F;
            sptd.Cdb[12] = 0xC2;
            sptd.Cdb[13] = 0xA0;
            sptd.Cdb[14] = 0xB0;

            Marshal.StructureToPtr(sptd, pSptd, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_SCSI_PASS_THROUGH_DIRECT, pSptd, (uint)sptdSize, pSptd, (uint)sptdSize, out _, IntPtr.Zero);
            if (ok)
            {
                for (int idx = 0; idx < 21; idx++)
                {
                    int off = 2 + (idx * 24);
                    byte contentByte = Marshal.ReadByte(pData, off + 0);
                    byte statusByte = Marshal.ReadByte(pData, off + 1);
                    if (contentByte == 0 && statusByte == 0) continue;

                    byte resNibble = (byte)((statusByte >> 4) & 0x0F);
                    string testType = (contentByte & 0x7F) switch
                    {
                        1 => "Short",
                        2 => "Extended",
                        _ => $"Op 0x{contentByte:X2}"
                    };

                    bool succeeded = resNibble == 0;
                    string resMsg = resNibble switch
                    {
                        0 => "Completed without error",
                        1 => "Aborted by host",
                        2 => "Interrupted by hardware reset",
                        3 => "Fatal failure / unknown element",
                        4 => "Unknown hardware failure",
                        5 => "Electrical failure",
                        6 => "Servo / mechanical failure",
                        7 => "Read sector failure",
                        8 => "Handling failure / physical damage",
                        _ => $"Failed (Status 0x{resNibble:X})"
                    };

                    ushort lifeHours = (ushort)(Marshal.ReadByte(pData, off + 2) | (Marshal.ReadByte(pData, off + 3) << 8));
                    uint failLba = (uint)(Marshal.ReadByte(pData, off + 5) | (Marshal.ReadByte(pData, off + 6) << 8) | (Marshal.ReadByte(pData, off + 7) << 16) | (Marshal.ReadByte(pData, off + 8) << 24));

                    list.Add(new FirmwareSelfTestEntry
                    {
                        Index = idx + 1,
                        TestType = testType,
                        ResultMessage = resMsg,
                        Succeeded = succeeded,
                        PowerOnHours = lifeHours,
                        FailingLba = failLba,
                        HasFailingLba = resNibble >= 4 && failLba > 0
                    });
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pData);
            Marshal.FreeHGlobal(pSptd);
        }
        return list;
    }
}
