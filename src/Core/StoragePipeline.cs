using System;
using System.Collections.Generic;
using System.Linq;

namespace Vesper;

public static class StoragePipeline
{
    public static List<DriveSmartReport> ExecutePipeline(Func<List<DriveSmartReport>> scanDirectDrivesFunc, Func<List<DriveSmartReport>> scanRaidDirectFunc)
    {
        var finalReports = new List<DriveSmartReport>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDeduplicated(IEnumerable<DriveSmartReport> drives, string tierName)
        {
            foreach (var d in drives)
            {
                if (d == null || string.IsNullOrEmpty(d.DevicePath)) continue;

                if (Program.IsPendriveOrRemovable(d)) continue;

                string normSerial = (d.Serial ?? "").Trim();
                bool hasSerial = !string.IsNullOrEmpty(normSerial) && normSerial != "0" && !normSerial.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

                var existing = finalReports.Find(x => string.Equals(x.DevicePath, d.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(d.Model) && (existing.Model.Contains("Bridge", StringComparison.OrdinalIgnoreCase) || existing.Model.Contains("Elements", StringComparison.OrdinalIgnoreCase) || existing.Model.StartsWith("USB", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!d.Model.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && !d.Model.Contains("Storage Space", StringComparison.OrdinalIgnoreCase))
                        {
                            existing.Model = d.Model;
                        }
                    }
                    if (existing.DiskSizeBytes <= 0 && d.DiskSizeBytes > 0) existing.DiskSizeBytes = d.DiskSizeBytes;
                    continue;
                }

                if (hasSerial && !seenSerials.Add(normSerial))
                {
                    continue;
                }

                if (seenPaths.Add(d.DevicePath))
                {
                    if (hasSerial) seenSerials.Add(normSerial);
                    finalReports.Add(d);
                    if (Program.VerboseMode)
                    {
                        Console.WriteLine($"  [{tierName} OK]: {d.Model} | S/N: {d.Serial} | Temp: {d.TemperatureCelsius}°C | Health: {d.HealthDefault:F0}%");
                    }
                }
            }
        }

        if (Program.VerboseMode)
        {
            Console.WriteLine("\n================================================================================");
            Console.WriteLine("  [SCAN] Scanning drives (Win32 / WMI / RAID CLI)...");
            Console.WriteLine("================================================================================");
        }

        // --- CAMADA 1: NATIVO TIER 0 (Kernel P/Invoke Win32 Direto) ---
        if (Program.VerboseMode) Console.WriteLine("\n>> [LAYER 1: TIER 0] Probing Win32 Kernel P/Invoke (NVMe, SATA, USB, MegaRAID, CSMI)...");
        try
        {
            var direct = scanDirectDrivesFunc();
            AddDeduplicated(direct, "TIER 0 - DIRECT P/INVOKE");

            var raidDirect = scanRaidDirectFunc();
            AddDeduplicated(raidDirect, "TIER 0 - RAID MINIPORT");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [LAYER 1 NOTICE]: {ex.Message}");
        }

        // --- CAMADA 2: WMI / CIM / REDFISH REST (Tier 1 Corporativo) ---
        if (Program.VerboseMode) Console.WriteLine("\n>> [LAYER 2: TIER 1] Probing Enterprise WMI/CIM (Dell OMSA, HPE Redfish, Storage Spaces)...");
        try
        {
            var wmiDrives = EnterpriseWmiProbe.ScanEnterpriseStorage();
            AddDeduplicated(wmiDrives, "TIER 1 - WMI/REDFISH");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [LAYER 2 NOTICE]: {ex.Message}");
        }

        if (Program.VerboseMode) Console.WriteLine("\n>> [LAYER 3: TIER 2] Probing Vendor Utilities in RAM (rcadm, ssacli)...");
        try
        {
            var cliDrives = VendorCliProbe.ScanVendorCliDrives();
            AddDeduplicated(cliDrives, "TIER 2 - VENDOR CLI");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [LAYER 3 NOTICE]: {ex.Message}");
        }

        var raidMembers = finalReports.Where(x => x.IsRaidMember).ToList();
        if (raidMembers.Count > 0)
        {
            foreach (var vol in finalReports.Where(x => !x.IsRaidMember && (x.BusType == 8 || x.BusTypeName.Contains("RAID", StringComparison.OrdinalIgnoreCase) || x.Model.Contains("RAID", StringComparison.OrdinalIgnoreCase))))
            {
                if (vol.TemperatureCelsius <= 0)
                    vol.TemperatureCelsius = raidMembers.Max(m => m.TemperatureCelsius);
                if (vol.PowerOnHours == 0)
                    vol.PowerOnHours = raidMembers.Max(m => m.PowerOnHours);
                if (vol.PowerCycleCount == 0)
                    vol.PowerCycleCount = raidMembers.Max(m => m.PowerCycleCount);
                vol.HealthDefault = Math.Min(vol.HealthDefault, raidMembers.Min(m => m.HealthDefault));
                VesperHealthCalculator.EvaluateDrive(vol);
            }
        }

        return finalReports;
    }
}
