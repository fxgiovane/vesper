using System;
using System.Collections.Generic;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vesper;

public static class EnterpriseWmiProbe
{
    public static List<DriveSmartReport> ScanEnterpriseStorage()
    {
        var list = new List<DriveSmartReport>();

        try
        {
            ScanDellOmsa(list);
        }
        catch { }

        try
        {
            ScanStorageSpaces(list);
        }
        catch { }

        try
        {
            ScanHpeIloRedfish(list);
        }
        catch { }

        return list;
    }

    private static void ScanDellOmsa(List<DriveSmartReport> list)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\dcim\sysman", "SELECT * FROM DCIM_PhysicalDiskView");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            foreach (ManagementObject obj in searcher.Get())
            {
                string fqdd = obj["FQDD"]?.ToString() ?? "";
                string model = obj["Model"]?.ToString() ?? obj["Manufacturer"]?.ToString() ?? "Dell PERC Physical Disk";
                string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                long size = 0;
                if (long.TryParse(obj["SizeInBytes"]?.ToString(), out long s)) size = s;

                int temp = 0;
                if (int.TryParse(obj["Temperature"]?.ToString(), out int t)) temp = t;

                string predFail = obj["PredictiveFailureState"]?.ToString() ?? "";
                bool isSmartOk = !predFail.Equals("1", StringComparison.OrdinalIgnoreCase) &&
                                 !predFail.Equals("True", StringComparison.OrdinalIgnoreCase);

                var report = new DriveSmartReport
                {
                    DevicePath = $@"\\.\DellPERC: {fqdd}",
                    Model = model,
                    Serial = serial,
                    DiskSizeBytes = size,
                    BusType = 8,
                    BusTypeName = "RAID (Dell PERC / OMSA)",
                    IsRaidMember = true,
                    RaidControllerName = "Dell PERC",
                    TemperatureCelsius = temp,
                    HealthDefault = isSmartOk ? 100.0 : 10.0,
                    HealthMoreStrict = isSmartOk ? 100.0 : 5.0,
                    Performance = null,
                    HealthVerdictText = isSmartOk ? "OK — Dell OMSA Telemetry." : "WARNING: PREDICTIVE FAILURE via Dell OMSA."
                };

                list.Add(report);
            }
        }
        catch { }
    }

    private static void ScanStorageSpaces(List<DriveSmartReport> list)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            foreach (ManagementObject obj in searcher.Get())
            {
                string model = obj["Model"]?.ToString()?.Trim() ?? "Storage Space Member";
                string serial = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                string devId = obj["DeviceId"]?.ToString() ?? "";
                long size = 0;
                if (long.TryParse(obj["Size"]?.ToString(), out long s)) size = s;

                int busType = 0;
                if (int.TryParse(obj["BusType"]?.ToString(), out int b)) busType = b;
                if (Program.IsPendriveOrUsbFlash((uint)busType, model, size)) continue;

                int mediaType = 0;
                if (int.TryParse(obj["MediaType"]?.ToString(), out int m)) mediaType = m;

                int healthStatus = 0;
                if (int.TryParse(obj["HealthStatus"]?.ToString(), out int h)) healthStatus = h;

                bool isSsd = mediaType == 4 || (mediaType != 3 && (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || busType == 17));

                var report = new DriveSmartReport
                {
                    DevicePath = $@"\\.\PhysicalDrive{devId}",
                    Model = model,
                    Serial = serial,
                    DiskSizeBytes = size,
                    BusType = (uint)busType,
                    BusTypeName = busType switch { 17 => "NVMe", 11 => "SATA", 8 => "RAID", 7 => "USB", _ => "StoragePool" },
                    IsSsd = isSsd,
                    IsNvme = busType == 17,
                    HealthDefault = null,
                    HealthMoreStrict = null,
                    Performance = null,
                    TelemetryAvailable = false,
                    HealthVerdictText = "WMI Status Only — Run as Administrator for physical SMART telemetry."
                };

                list.Add(report);
            }
        }
        catch { }
    }

    private static void ScanHpeIloRedfish(List<DriveSmartReport> list)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(800) };
            
            var task = client.GetStringAsync("https://localhost:63001/redfish/v1/Systems/1/Storage");
            if (!task.Wait(800)) return;
            string json = task.Result;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Members", out var members))
            {
                foreach (var member in members.EnumerateArray())
                {
                    if (member.TryGetProperty("@odata.id", out var odataId))
                    {
                        string subUrl = "https://localhost:63001" + odataId.GetString();
                        var subTask = client.GetStringAsync(subUrl);
                        if (!subTask.Wait(500)) continue;
                        using var subDoc = JsonDocument.Parse(subTask.Result);
                        if (subDoc.RootElement.TryGetProperty("Drives", out var drives))
                        {
                            foreach (var driveItem in drives.EnumerateArray())
                            {
                                if (driveItem.TryGetProperty("@odata.id", out var driveUrl))
                                {
                                    var drvTask = client.GetStringAsync("https://localhost:63001" + driveUrl.GetString());
                                    if (!drvTask.Wait(500)) continue;
                                    using var drvDoc = JsonDocument.Parse(drvTask.Result);
                                    var root = drvDoc.RootElement;
                                    string model = root.TryGetProperty("Model", out var m) ? m.GetString() ?? "HPE Drive" : "HPE Drive";
                                    string serial = root.TryGetProperty("SerialNumber", out var sn) ? sn.GetString() ?? "" : "";
                                    long capBytes = root.TryGetProperty("CapacityBytes", out var cb) ? cb.GetInt64() : 0;
                                    string state = "OK";
                                    if (root.TryGetProperty("Status", out var st) && st.TryGetProperty("Health", out var h))
                                    {
                                        state = h.GetString() ?? "OK";
                                    }

                                    bool isOk = state.Equals("OK", StringComparison.OrdinalIgnoreCase);

                                    var rep = new DriveSmartReport
                                    {
                                        DevicePath = driveUrl.GetString() ?? "",
                                        Model = model,
                                        Serial = serial.Trim(),
                                        DiskSizeBytes = capBytes,
                                        BusType = 8,
                                        BusTypeName = "RAID (HPE Smart Array)",
                                        IsRaidMember = true,
                                        RaidControllerName = "HPE Smart Array",
                                        HealthDefault = isOk ? 100.0 : 20.0,
                                        HealthMoreStrict = isOk ? 100.0 : 10.0,
                                        Performance = null,
                                        HealthVerdictText = isOk ? "OK — HPE iLO Telemetry." : $"WARNING: Degraded drive state ({state})."
                                    };
                                    list.Add(rep);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}
