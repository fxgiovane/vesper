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
    private struct STORAGE_PROTOCOL_SPECIFIC_DATA
    {
        public uint ProtocolType;
        public uint DataType;
        public uint ProtocolDataRequestValue;
        public uint ProtocolDataRequestSubValue;
        public uint ProtocolDataOffset;
        public uint ProtocolDataLength;
        public uint FixedProtocolReturnData;
        public uint ProtocolDataRequestSubValue2;
        public uint ProtocolDataRequestSubValue3;
        public uint ProtocolDataRequestSubValue4;
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
            AtaFlags = 0x01, // ATA_FLAGS_DRDY_REQUIRED (0x01 para non-data SMART; 0x02 DATA_IN causa Win32 87)
            DataTransferLength = 0,
            TimeOutValue = 10,
            DataBufferOffset = IntPtr.Zero,
            PreviousTaskFile = new byte[8],
            CurrentTaskFile = new byte[8]
        };

        // Taskfile ATA SMART EXECUTE OFF-LINE
        pte.CurrentTaskFile[0] = 0xD4; // Feature: SMART EXECUTE OFF-LINE (0xD4)
        pte.CurrentTaskFile[1] = 0x00; // Sector Count
        pte.CurrentTaskFile[2] = subCommand; // LBA Low: Subcomando (0x01 = Short, 0x02 = Extended, 0x7F = Abort)
        pte.CurrentTaskFile[3] = 0x4F; // LBA Mid: SMART Cylinder Low (0x4F)
        pte.CurrentTaskFile[4] = 0xC2; // LBA High: SMART Cylinder High (0xC2)
        pte.CurrentTaskFile[5] = 0xA0; // Device/Head: 0xA0
        pte.CurrentTaskFile[6] = 0xB0; // Command: ATA SMART (0xB0)
        pte.CurrentTaskFile[7] = 0x00;

        int size = Marshal.SizeOf(pte);
        IntPtr pBuf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(pte, pBuf, false);
            bool ok = DeviceIoControl(hDevice, IOCTL_ATA_PASS_THROUGH, pBuf, (uint)size, pBuf, (uint)size, out _, IntPtr.Zero);
            if (!ok)
            {
                // Fallback para SCSI SAT-16 (para discos SATA conectados via ponte USB / StorPort)
                var sat = new SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
                    CdbLength = 16,
                    DataIn = 0,
                    TimeOutValue = 10,
                    Cdb = new byte[16]
                };
                sat.Cdb[0] = 0x85; // ATA PASS-THROUGH (16)
                sat.Cdb[1] = (3 << 1); // Non-data protocol
                sat.Cdb[2] = 0x20; // Check_cond
                sat.Cdb[3] = 0x00;
                sat.Cdb[4] = 0xD4; // Features
                sat.Cdb[6] = subCommand; // LBA Low
                sat.Cdb[8] = 0x4F; // LBA Mid
                sat.Cdb[10] = 0xC2; // LBA High
                sat.Cdb[12] = 0xA0; // Device
                sat.Cdb[14] = 0xB0; // Command: ATA SMART

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
                    message = $"Falha ao despachar ATA SMART EXECUTE OFF-LINE (0xB0/0xD4/{subCommand:X2}). Erro Win32: {Marshal.GetLastWin32Error()}";
                    return false;
                }
            }

            message = mode == SelfTestMode.Abort ? "Diagnostico ATA cancelado com sucesso." : $"Diagnostico ATA {mode} disparado com sucesso no firmware.";
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
                message = $"Falha ao despachar SCSI SEND DIAGNOSTIC (0x1D). Erro Win32: {Marshal.GetLastWin32Error()}";
                return false;
            }
            message = mode == SelfTestMode.Abort ? "Diagnostico SCSI cancelado com sucesso." : $"Diagnostico SCSI {mode} disparado com sucesso.";
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
                    message = "O controlador NVMe ou driver StorNVMe nao suporta autoteste interno de firmware neste modelo (OACS bit 4 inativo ou restrito). Utilize o Surface Test (Modulo 1) para varredura fisica direta dos setores.";
                }
                else if (err == 5)
                {
                    message = "Acesso negado pelo driver do kernel. O autoteste NVMe requer elevacao de Administrador (UAC).";
                }
                else
                {
                    message = $"Falha ao despachar comando NVMe Device Self-Test (0x14). Erro Win32: {err}";
                }
                return false;
            }
            message = mode == SelfTestMode.Abort ? "Diagnostico NVMe cancelado com sucesso." : $"Diagnostico NVMe {mode} disparado com sucesso no controlador.";
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pCmd);
        }
    }
}
