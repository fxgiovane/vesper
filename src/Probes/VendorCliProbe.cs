using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Vesper;

public static class VendorCliProbe
{
    public static List<DriveSmartReport> ScanVendorCliDrives()
    {
        var list = new List<DriveSmartReport>();

        try { ScanAmdRaidXpert(list); } catch { }
        try { ScanHpeSmartArrayCli(list); } catch { }
        try { ScanIntelVrocCli(list); } catch { }
        try { ScanMarvellRaidCli(list); } catch { }

        return list;
    }

    private static string? LocateBinary(string binaryName, params string[] candidatePaths)
    {
        foreach (var p in candidatePaths)
        {
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string toolSub = Path.Combine(baseDir, "tools", binaryName);
        if (File.Exists(toolSub)) return toolSub;

        string toolLocal = Path.Combine(baseDir, binaryName);
        if (File.Exists(toolLocal)) return toolLocal;

        var envPath = Environment.GetEnvironmentVariable("PATH");
        if (envPath != null)
        {
            foreach (var part in envPath.Split(Path.PathSeparator))
            {
                string combined = Path.Combine(part.Trim(), binaryName);
                if (File.Exists(combined)) return combined;
            }
        }

        return ExtractEmbeddedResourceSafely(binaryName);
    }

    private static string? ExtractEmbeddedResourceSafely(string binaryName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string expectedPrefix = "Vesper.tools." + binaryName;

            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                     n.EndsWith("." + binaryName, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null) return null;

            using var resStream = asm.GetManifestResourceStream(resourceName);
            if (resStream == null) return null;

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string secureDir = Path.Combine(localApp, "Vesper", "runtimes", "v1");
            Directory.CreateDirectory(secureDir);

            string targetPath = Path.Combine(secureDir, binaryName);

            if (File.Exists(targetPath))
            {
                var fi = new FileInfo(targetPath);
                if (fi.Length == resStream.Length)
                {
                    return targetPath;
                }
            }

            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                resStream.CopyTo(fs);
            }

            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private static string? RunProcessInMemory(string fileName, string arguments, int timeoutMs = 3000)
    {
        try
        {
            SetErrorMode(0x0001 | 0x0002 | 0x8000);
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return output;
        }
        catch
        {
            return null;
        }
    }

    private static void ScanAmdRaidXpert(List<DriveSmartReport> list)
    {
        string? rcadmPath = LocateBinary(
            "rcadm.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "RAIDXpert2", "bin", "rcadm.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AMD", "RAID Software", "RAIDXpert2_Utility", "WT", "RAIDXpert2_NVMe", "rcadm.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RAIDXpert2", "rcadm.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RAIDXpert2", "rcadm.exe")
        );

        if (rcadmPath == null) return;

        string? listOut = RunProcessInMemory(rcadmPath, "-M -qa", 3000);
        if (string.IsNullOrWhiteSpace(listOut)) return;

        var diskDict = new Dictionary<int, DriveSmartReport>();
        bool inDiskSection = false;

        foreach (var rawLine in listOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("<DISK LIST>")) { inDiskSection = true; continue; }
            if (line.StartsWith("<ARRAY LIST>") || line.StartsWith("<CONTROLLER LIST>")) { inDiskSection = false; continue; }
            if (!inDiskSection || line.StartsWith("Disk") || line.StartsWith("----")) continue;

            var mChan = Regex.Match(line, @"\s(\d+:\d{2})\s+");
            if (mChan.Success)
            {
                string beforeChan = line.Substring(0, mChan.Index).Trim();
                string afterChan = line.Substring(mChan.Index + mChan.Length).Trim();
                var headParts = Regex.Split(beforeChan, @"\s+");
                if (headParts.Length >= 1 && int.TryParse(headParts[0], out int diskId))
                {
                    var tailParts = Regex.Split(afterChan, @"\s{2,}");
                    string model = tailParts.Length >= 1 ? tailParts[0].Trim() : $"AMD-RAID Member {diskId}";
                    string serial = "";
                    if (tailParts.Length >= 2)
                    {
                        var subParts = tailParts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (subParts.Length >= 2) serial = subParts[1];
                        else if (subParts.Length == 1) serial = subParts[0];
                    }
                    if (tailParts.Length >= 3)
                    {
                        serial = tailParts[2].Trim();
                    }

                    long sizeBytes = 0;
                    var mSize = Regex.Match(beforeChan, @"\b([\d\.]+)\s*([KMGT]i?B)\b(?!\s*/)", RegexOptions.IgnoreCase);
                    if (mSize.Success && double.TryParse(mSize.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num))
                    {
                        string unit = mSize.Groups[2].Value.ToUpperInvariant();
                        if (unit.StartsWith("T")) sizeBytes = (long)(num * 1_000_000_000_000L);
                        else if (unit.StartsWith("G")) sizeBytes = (long)(num * 1_000_000_000L);
                        else if (unit.StartsWith("M")) sizeBytes = (long)(num * 1_000_000L);
                        else if (unit.StartsWith("K")) sizeBytes = (long)(num * 1_000L);
                    }
                    if (sizeBytes <= 0) sizeBytes = 1_000_000_000_000L;

                    var report = new DriveSmartReport
                    {
                        DevicePath = $@"\\.\Scsi0: Disk {diskId} ({serial})",
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

        string? smartOut = RunProcessInMemory(rcadmPath, "-M -sg -d * -at", 4000);
        if (!string.IsNullOrWhiteSpace(smartOut))
        {
            int currentDisk = -1;
            foreach (var rawLine in smartOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                var matchDisk = Regex.Match(line, @"\+\s*Disk\s*(\d+)\s*\+");
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
                        var mId = Regex.Match(parts[0], @"0x([0-9a-fA-F]{2})");
                        int fP = parts[0].IndexOf('(');
                        int lP = parts[0].LastIndexOf(')');
                        string attrName = (fP >= 0 && lP > fP) ? parts[0].Substring(fP + 1, lP - fP - 1).Trim() : "SMART Attribute";

                        var mCur = Regex.Match(parts[2], @"\d+");
                        var mRaw = Regex.Match(parts[parts.Length - 1], @"\d+");

                        if (mId.Success && mCur.Success && mRaw.Success)
                        {
                            byte id = Convert.ToByte(mId.Groups[1].Value, 16);
                            byte cur = byte.Parse(mCur.Value);
                            ulong rawVal = ulong.Parse(mRaw.Value);

                            if (id == 0xC2 || id == 0xBE) rep.TemperatureCelsius = (int)(rawVal & 0xFF);
                            else if (id == 0x09) rep.PowerOnHours = (rawVal > 1000000) ? (rawVal & 0xFFFF) : rawVal;
                            else if (id == 0x0C) rep.PowerCycleCount = rawVal;

                            string interp = Program.GetAtaInterpretation(id, rawVal, cur, cur, rep.Model);

                            rep.Attributes.Add(new SmartAttribute
                            {
                                Id = id,
                                Name = attrName,
                                Current = cur,
                                Worst = cur,
                                RawValue = rawVal,
                                Interpretation = interp
                            });
                        }
                    }
                }
            }
        }

        foreach (var kvp in diskDict.OrderBy(k => k.Key))
        {
            VesperHealthCalculator.EvaluateDrive(kvp.Value);
            list.Add(kvp.Value);
        }
    }

    private static void ScanHpeSmartArrayCli(List<DriveSmartReport> list)
    {
        string? ssacliPath = LocateBinary(
            "ssacli.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Smart Storage Administrator", "ssacli", "bin", "ssacli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HP", "hpssacli", "bin", "hpssacli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Compaq", "Hpacucli", "Bin", "hpacucli.exe")
        );

        if (ssacliPath == null) return;

        string? detailOut = RunProcessInMemory(ssacliPath, "ctrl all pd all show detail", 3500);
        if (string.IsNullOrWhiteSpace(detailOut))
        {
            detailOut = RunProcessInMemory(ssacliPath, "ctrl all show config detail", 3500);
        }
        if (string.IsNullOrWhiteSpace(detailOut)) return;

        DriveSmartReport? current = null;

        foreach (var rawLine in detailOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();

            if (line.StartsWith("physicaldrive ", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null && !string.IsNullOrEmpty(current.Serial))
                {
                    VesperHealthCalculator.EvaluateDrive(current);
                    list.Add(current);
                }

                string slot = line.Substring("physicaldrive ".Length).Trim();
                current = new DriveSmartReport
                {
                    DevicePath = $@"\\.\HpeSmartArray: {slot}",
                    BusType = 8,
                    BusTypeName = "RAID (HPE Smart Array)",
                    IsRaidMember = true,
                    RaidControllerName = "HPE Smart Array",
                    HealthDefault = 100.0,
                    HealthMoreStrict = 100.0,
                    Performance = 100.0
                };
                continue;
            }

            if (current == null) continue;

            if (line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
            {
                current.Model = line.Substring("Model:".Length).Trim();
                if (current.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase)) current.IsSsd = true;
            }
            else if (line.StartsWith("Serial Number:", StringComparison.OrdinalIgnoreCase))
            {
                current.Serial = line.Substring("Serial Number:".Length).Trim();
            }
            else if (line.StartsWith("Bay:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.Substring("Bay:".Length).Trim(), out int bay))
                    current.RaidSlotNumber = bay;
            }
            else if (line.StartsWith("Interface Type:", StringComparison.OrdinalIgnoreCase))
            {
                string iface = line.Substring("Interface Type:".Length).Trim();
                current.BusTypeName = $"RAID (HPE {iface})";
            }
            else if (line.StartsWith("Current Temperature (C):", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.Substring("Current Temperature (C):".Length).Trim(), out int t))
                    current.TemperatureCelsius = t;
            }
            else if (line.StartsWith("Power On Hours:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Power-On Hours:", StringComparison.OrdinalIgnoreCase))
            {
                string pohStr = line.Substring(line.IndexOf(':') + 1).Trim();
                var digits = System.Text.RegularExpressions.Regex.Match(pohStr, @"\d+");
                if (digits.Success && ulong.TryParse(digits.Value, out ulong poh))
                    current.PowerOnHours = poh;
            }
            else if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                string st = line.Substring("Status:".Length).Trim();
                bool isOk = st.Equals("OK", StringComparison.OrdinalIgnoreCase);
                if (!isOk)
                {
                    current.HealthDefault = 20.0;
                    current.HealthMoreStrict = 10.0;
                    current.HealthVerdictText = $"WARNING: HPE Status = {st}";
                }
            }
            else if (line.StartsWith("Size:", StringComparison.OrdinalIgnoreCase))
            {
                string szStr = line.Substring("Size:".Length).Trim();
                if (szStr.EndsWith("TB", StringComparison.OrdinalIgnoreCase) && double.TryParse(szStr.Replace("TB", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tb))
                {
                    current.DiskSizeBytes = (long)(tb * 1_000_000_000_000L);
                }
                else if (szStr.EndsWith("TiB", StringComparison.OrdinalIgnoreCase) && double.TryParse(szStr.Replace("TiB", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double tib))
                {
                    current.DiskSizeBytes = (long)(tib * 1024L * 1024L * 1024L * 1024L);
                }
                else if (szStr.EndsWith("GB", StringComparison.OrdinalIgnoreCase) && double.TryParse(szStr.Replace("GB", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double gb))
                {
                    current.DiskSizeBytes = (long)(gb * 1_000_000_000L);
                }
                else if (szStr.EndsWith("GiB", StringComparison.OrdinalIgnoreCase) && double.TryParse(szStr.Replace("GiB", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double gib))
                {
                    current.DiskSizeBytes = (long)(gib * 1024L * 1024L * 1024L);
                }
                else if (szStr.EndsWith("MB", StringComparison.OrdinalIgnoreCase) && double.TryParse(szStr.Replace("MB", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double mb))
                {
                    current.DiskSizeBytes = (long)(mb * 1_000_000L);
                }
            }
        }

        if (current != null && !string.IsNullOrEmpty(current.Serial))
        {
            VesperHealthCalculator.EvaluateDrive(current);
            list.Add(current);
        }
    }

    private static void ScanIntelVrocCli(List<DriveSmartReport> list)
    {
        string? vrocPath = LocateBinary("IntelVROCCli.exe");
        if (vrocPath == null) return;

        string? outText = RunProcessInMemory(vrocPath, "--manage -i", 3000);
        if (string.IsNullOrWhiteSpace(outText)) return;

        // Parse Intel VROC physical drives if present
    }

    private static void ScanMarvellRaidCli(List<DriveSmartReport> list)
    {
        string? mvPath = LocateBinary("mvsetup.exe");
        if (mvPath == null) return;

        string dir = Path.GetDirectoryName(mvPath) ?? "";
        if (!File.Exists(Path.Combine(dir, "mv_api.dll"))) return;

        string? outText = RunProcessInMemory(mvPath, "adapter -i", 3000);
        if (string.IsNullOrWhiteSpace(outText)) return;

        // Parse Marvell adapters if present
    }
}
