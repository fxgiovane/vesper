using System;
using System.Collections.Generic;
using System.Linq;

namespace Vesper;

public static class StoragePipeline
{
    public static List<DriveSmartReport> ExecutePipeline(Func<List<DriveSmartReport>> scanDirectDrivesFunc, Func<List<DriveSmartReport>> scanRaidDirectFunc)
    {
        var finalReports = new List<DriveSmartReport>();
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDeduplicated(IEnumerable<DriveSmartReport> drives, string tierName)
        {
            foreach (var d in drives)
            {
                string normSerial = (d.Serial ?? "").Trim();
                if (string.IsNullOrEmpty(normSerial))
                {
                    normSerial = d.DevicePath;
                }

                if (seenSerials.Add(normSerial))
                {
                    finalReports.Add(d);
                    if (Program.VerboseMode)
                    {
                        Console.WriteLine($"  [{tierName} OK]: {d.Model} | S/N: {d.Serial} | Temp: {d.TemperatureCelsius}°C | Saúde: {d.HealthDefault:F0}%");
                    }
                }
            }
        }

        if (Program.VerboseMode)
        {
            Console.WriteLine("\n================================================================================");
            Console.WriteLine("  INICIANDO CASCATA MULTI-TIER (ESTRATÉGIA DE DEFESA EM 3 CAMADAS)");
            Console.WriteLine("================================================================================");
        }

        // --- CAMADA 1: NATIVO TIER 0 (Kernel P/Invoke Win32 Direto) ---
        if (Program.VerboseMode) Console.WriteLine("\n>> [CAMADA 1: TIER 0] Sondando Win32 Kernel P/Invoke (NVMe, SATA, USB, MegaRAID, CSMI)...");
        try
        {
            var direct = scanDirectDrivesFunc();
            AddDeduplicated(direct, "TIER 0 - P/INVOKE DIRETO");

            var raidDirect = scanRaidDirectFunc();
            AddDeduplicated(raidDirect, "TIER 0 - RAID MINIPORT");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [CAMADA 1 AVISO]: {ex.Message}");
        }

        // --- CAMADA 2: WMI / CIM / REDFISH REST (Tier 1 Corporativo) ---
        if (Program.VerboseMode) Console.WriteLine("\n>> [CAMADA 2: TIER 1] Sondando WMI/CIM Corporativo (Dell OMSA, HPE Redfish, Storage Spaces)...");
        try
        {
            var wmiDrives = EnterpriseWmiProbe.ScanEnterpriseStorage();
            AddDeduplicated(wmiDrives, "TIER 1 - WMI/REDFISH");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [CAMADA 2 AVISO]: {ex.Message}");
        }

        // --- CAMADA 3: VENDOR CLI ENGINES EM MEMÓRIA (Tier 2 Fallback) ---
        if (Program.VerboseMode) Console.WriteLine("\n>> [CAMADA 3: TIER 2] Sondando Utilitários de Fabricantes em RAM (rcadm, ssacli)...");
        try
        {
            var cliDrives = VendorCliProbe.ScanVendorCliDrives();
            AddDeduplicated(cliDrives, "TIER 2 - VENDOR CLI");
        }
        catch (Exception ex)
        {
            if (Program.VerboseMode) Console.WriteLine($"  [CAMADA 3 AVISO]: {ex.Message}");
        }

        return finalReports;
    }
}
