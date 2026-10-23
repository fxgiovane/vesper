using System;
using System.Collections.Generic;

namespace Vesper;

public sealed class SmartAttribute
{
    public byte Id { get; init; }
    public string Name { get; init; } = "";
    public byte Current { get; init; }
    public byte Worst { get; init; }
    public ushort Flags { get; init; }
    public string FlagsHex => $"0x{Flags:X4}";
    public ulong RawValue { get; init; }
    public string RawHex => $"0x{RawValue:X12}";
    public ushort Raw16 => (ushort)(RawValue & 0xFFFF);
    public string Interpretation { get; init; } = "";
}

public sealed class DriveSmartReport
{
    public string DevicePath { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public uint BusType { get; set; }
    public string BusTypeName { get; set; } = "";
    public long DiskSizeBytes { get; set; }
    public double DiskSizeGbBinary => Math.Round(DiskSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
    public double DiskSizeGbDecimal => Math.Round(DiskSizeBytes / 1_000_000_000.0, 2);
    public uint BytesPerSector { get; set; } = 512;
    public string FirmwareRevision { get; set; } = "";
    public ushort RotationRateRpm { get; set; }
    public string BridgeControllerName { get; set; } = "";
    public string SsdControllerFamily { get; set; } = "";
    public bool IsSsd { get; set; }
    public bool IsNvme { get; set; }
    public bool IsRaidMember { get; set; }
    public string RaidControllerName { get; set; } = "";
    public int RaidSlotNumber { get; set; } = -1;
    public int TemperatureCelsius { get; set; }
    public int TemperatureSensor1 { get; set; }
    public int TemperatureSensor2 { get; set; }
    public ulong PowerOnHours { get; set; }
    public ulong PowerCycleCount { get; set; }
    public double TotalTbWritten { get; set; }
    public double TotalTbRead { get; set; }
    public List<SmartAttribute> Attributes { get; } = new();
    public byte[]? RawSmartPayload { get; set; }

    public ushort ReallocatedSectors => Attributes.Find(a => a.Id == 0x05)?.Raw16 ?? 0;
    public ushort CurrentPendingSectors => Attributes.Find(a => a.Id == 0xC5)?.Raw16 ?? 0;

    public double HealthDefault { get; set; }
    public bool TelemetryAvailable { get; set; } = true;
    public double HealthMoreStrict { get; set; }
    public double Performance { get; set; }
    public string RemainingLifetimeText { get; set; } = "";
    public string HealthVerdictText { get; set; } = "";
    public List<string> BreakdownLogs { get; } = new();
}
