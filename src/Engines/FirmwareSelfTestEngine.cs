using System;
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
            status.Message = "Dispositivo indisponivel.";
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
            status.Message = "Acesso indisponivel.";
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

            Marshal.WriteInt32(pBuf, 0, 49);
            Marshal.WriteInt32(pBuf, 4, 0);

            int protoOffset = 8;
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
                        status.Message = $"EXECUTANDO ({completion}% concluido)";
                        return status;
                    }

                    byte resultStatus = (byte)(Marshal.ReadByte(pBuf, actualOffset + 4) & 0x0F);
                    status.IsInProgress = false;
                    status.PercentRemaining = 0;
                    status.Succeeded = resultStatus == 0;
                    status.Failed = resultStatus > 0;
                    status.Message = status.Succeeded ? "CONCLUÍDO (OK)" : $"FALHA (0x{resultStatus:X2})";
                    return status;
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        status.Message = "OK";
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
                    status.Message = $"EXECUTANDO ({100 - status.PercentRemaining}% concluido)";
                    return status;
                }

                status.IsInProgress = false;
                status.PercentRemaining = 0;
                status.Succeeded = hi == 0;
                status.Failed = hi > 0 && hi <= 8;
                status.Message = status.Succeeded ? "CONCLUÍDO (OK)" : $"FALHA (0x{hi:X2})";
                return status;
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }

        status.Message = "OK";
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
            message = $"Acesso negado ou dispositivo indisponivel em {devicePath}. Erro: {Marshal.GetLastWin32Error()}";
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
                    message = $"Falha no comando SMART (0xB0/0xD4/{subCommand:X2}). Erro Win32: {Marshal.GetLastWin32Error()}";
                    return false;
                }
            }

            message = mode == SelfTestMode.Abort ? "Diagnostico ATA cancelado." : $"Diagnostico ATA {mode} iniciado.";
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
                message = $"Falha no comando SCSI SEND DIAGNOSTIC (0x1D). Erro Win32: {Marshal.GetLastWin32Error()}";
                return false;
            }
            message = mode == SelfTestMode.Abort ? "Diagnostico SCSI cancelado." : $"Diagnostico SCSI {mode} iniciado.";
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
                    message = "Autoteste nao suportado pelo controlador NVMe nesta unidade.";
                }
                else if (err == 5)
                {
                    message = "Acesso negado. Requer elevacao de Administrador.";
                }
                else
                {
                    message = $"Falha no comando NVMe Self-Test (0x14). Erro Win32: {err}";
                }
                return false;
            }
            message = mode == SelfTestMode.Abort ? "Diagnostico NVMe cancelado." : $"Diagnostico NVMe {mode} iniciado.";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pCmd);
        }
    }
}
