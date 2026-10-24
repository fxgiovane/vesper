using System.Text;
using System;
using System.Collections.Generic;

namespace Vesper;

internal static class VesperHealthCalculator
{
    public static void EvaluateDrive(DriveSmartReport report)
    {
        bool isSeagate = report.Model.Contains("Seagate", StringComparison.OrdinalIgnoreCase) ||
                         report.Model.StartsWith("ST", StringComparison.OrdinalIgnoreCase);

        CalculateLifetimeVolume(report);

        if (report.Attributes.Count == 0)
        {
            report.TelemetryAvailable = false;
            report.HealthVerdictText = "INCONCLUSIVE — 0 SMART attributes decoded.";
            report.RemainingLifetimeText = "N/A (no SMART)";
            return;
        }

        report.TelemetryAvailable = true;

        if (report.IsNvme)
        {
            EvaluateNvme(report);
            report.Performance = CalculatePerformance(report);
            report.RemainingLifetimeText = CalculateRemainingLifetime(report);
            return;
        }

        if (report.IsSsd)
        {
            EvaluateSsd(report);
        }
        else
        {
            EvaluateHdd(report, isSeagate);
        }

        report.Performance = CalculatePerformance(report);
        report.RemainingLifetimeText = CalculateRemainingLifetime(report);
    }

    private static void EvaluateNvme(DriveSmartReport report)
    {
        double health = (report.HealthDefault.HasValue && report.HealthDefault.Value > 0.0) ? report.HealthDefault.Value : 100.0;

        var critAttr = report.Attributes.Find(a => a.Id == 0x01);
        if (critAttr != null && critAttr.RawValue > 0)
        {
            ulong cw = critAttr.RawValue;
            if ((cw & 0x01) != 0) health = Math.Min(health, 50.0);
            if ((cw & 0x04) != 0) health = Math.Min(health, 10.0);
            if ((cw & 0x08) != 0) health = Math.Min(health, 5.0);
            if ((cw & 0x10) != 0) health = Math.Min(health, 20.0);
            report.BreakdownLogs.Add($"  [NVME CRITICAL WARNING]: Flags 0x{cw:X2}. Penalidade aplicada.");
        }

        var spareAttr = report.Attributes.Find(a => a.Id == 0x03);
        var threshAttr = report.Attributes.Find(a => a.Id == 0x04);
        if (spareAttr != null && threshAttr != null && spareAttr.RawValue < threshAttr.RawValue)
        {
            health = Math.Min(health, (double)spareAttr.RawValue);
            report.BreakdownLogs.Add($"  [NVME SPARE]: Available Spare ({spareAttr.RawValue}%) abaixo do limiar ({threshAttr.RawValue}%).");
        }

        var mediaErrorsAttr = report.Attributes.Find(a => a.Id == 0x0E);
        if (mediaErrorsAttr != null && mediaErrorsAttr.RawValue > 0)
        {
            double penalty = Math.Min(80.0, mediaErrorsAttr.RawValue * 10.0);
            health = Math.Max(0.0, health - penalty);
            report.BreakdownLogs.Add($"  [NVME MEDIA ERRORS]: {mediaErrorsAttr.RawValue} uncorrectable errors detected. Penalty: {penalty:F1}%.");
        }

        report.HealthDefault = Math.Round(health, 1);
        report.HealthMoreStrict = report.HealthDefault;

        if (health >= 100.0)
        {
            report.HealthVerdictText = "OK — No media errors or critical alerts.";
        }
        else if (health >= 50.0)
        {
            report.HealthVerdictText = $"OK — NAND Wear: {100.0 - health:F0}%.";
        }
        else
        {
            report.HealthVerdictText = $"ALERT — Degraded NVMe Health: {health:F0}%.";
        }
    }

    private static void CalculateLifetimeVolume(DriveSmartReport report)
    {
        var attrF1 = report.Attributes.Find(a => a.Id == 0xF1);
        if (attrF1 != null && attrF1.RawValue > 0)
        {
            if (report.IsSsd)
            {
                var attrE9 = report.Attributes.Find(a => a.Id == 0xE9);
                if (attrE9 != null && attrE9.RawValue > 0)
                {
                    report.TotalTbWritten = Math.Round(attrE9.RawValue / 1024.0, 2);
                }
                else
                {
                    bool isSmi = report.Model.Contains("S700", StringComparison.OrdinalIgnoreCase) ||
                                 report.Model.Contains("SM22", StringComparison.OrdinalIgnoreCase) ||
                                 report.Model.Contains("SMI", StringComparison.OrdinalIgnoreCase) ||
                                 report.Model.Contains("Kingston", StringComparison.OrdinalIgnoreCase) ||
                                 (report.Attributes.Exists(a => a.Id == 0xB0) && report.Attributes.Exists(a => a.Id == 0xAD));

                    if (isSmi)
                    {
                        report.TotalTbWritten = Math.Round((attrF1.RawValue * 32.0) / 1048576.0, 3);
                    }
                    else
                    {
                        report.TotalTbWritten = Math.Round((attrF1.RawValue * (double)report.BytesPerSector) / Math.Pow(1024, 4), 2);
                        if (report.TotalTbWritten == 0.0) report.TotalTbWritten = Math.Round(attrF1.RawValue / 1024.0, 2);
                    }
                }
            }
            else
            {
                report.TotalTbWritten = Math.Round((attrF1.RawValue * (double)report.BytesPerSector) / Math.Pow(1024, 4), 2);
            }
        }

        var attrF2 = report.Attributes.Find(a => a.Id == 0xF2);
        if (attrF2 != null && attrF2.RawValue > 0)
        {
            if (report.IsSsd)
            {
                bool isSmi = report.Model.Contains("S700", StringComparison.OrdinalIgnoreCase) ||
                             report.Model.Contains("SM22", StringComparison.OrdinalIgnoreCase) ||
                             report.Model.Contains("SMI", StringComparison.OrdinalIgnoreCase) ||
                             report.Model.Contains("Kingston", StringComparison.OrdinalIgnoreCase) ||
                             (report.Attributes.Exists(a => a.Id == 0xB0) && report.Attributes.Exists(a => a.Id == 0xAD));

                if (isSmi)
                {
                    report.TotalTbRead = Math.Round((attrF2.RawValue * 32.0) / 1048576.0, 3);
                }
                else
                {
                    report.TotalTbRead = Math.Round(attrF2.RawValue / 1024.0, 2);
                }
            }
            else
            {
                report.TotalTbRead = Math.Round((attrF2.RawValue * (double)report.BytesPerSector) / Math.Pow(1024, 4), 2);
            }
        }
    }

    private static void EvaluateHdd(DriveSmartReport report, bool isSeagate)
    {
        double factorDefault = 1.0;
        double factorStrict = 1.0;

        var criticalIds = new byte[] { 0x01, 0x05, 0x07, 0x0A, 0xC4, 0xC5, 0xC6 };

        foreach (var id in criticalIds)
        {
            var attr = report.Attributes.Find(a => a.Id == id);
            if (attr == null) continue;

            ushort raw16 = attr.Raw16;

            if (isSeagate && (id == 0x01 || id == 0x07))
            {
                ulong raw48 = attr.RawValue;
                uint nOps = (uint)(raw48 & 0xFFFFFFFF);
                ushort realErrors = (ushort)((raw48 >> 32) & 0xFFFF);
                raw16 = realErrors;
                if (realErrors == 0)
                {
                    report.BreakdownLogs.Add($"  [ID 0x{id:X2}] {attr.Name} (Seagate): Raw={raw48} (nOps={nOps}, realErrors=0). Zero erros reais.");
                    continue;
                }
                report.BreakdownLogs.Add($"  [ID 0x{id:X2}] {attr.Name} (Seagate): Raw={raw48} (nOps={nOps}, realErrors={realErrors}). Penalidade sobre erros reais.");
            }

            double wDef = GetWeight(id, false);
            double lDef = GetLimit(id, false);
            double degDef = Math.Min(raw16 * wDef, lDef);
            double fDef = (100.0 - degDef) / 100.0;
            factorDefault *= fDef;

            double wStr = GetWeight(id, true);
            double lStr = GetLimit(id, true);
            double degStr = Math.Min(raw16 * wStr, lStr);
            double fStr = (100.0 - degStr) / 100.0;
            factorStrict *= fStr;

            if (raw16 > 0)
            {
                report.BreakdownLogs.Add($"  [ID 0x{id:X2}] {attr.Name}: Raw16={raw16} | Default: -{degDef:F1}% | Strict: -{degStr:F1}%");
            }
        }

        if (report.Attributes.Count == 0)
        {
            report.TelemetryAvailable = false;
            report.HealthVerdictText = "INCONCLUSIVE — 0 SMART attributes decoded.";
            return;
        }

        report.HealthDefault = Math.Round(factorDefault * 100.0, 1);
        report.HealthMoreStrict = Math.Round(factorStrict * 100.0, 1);

        var reallocated = report.Attributes.Find(a => a.Id == 0x05)?.Raw16 ?? 0;
        var pending = report.Attributes.Find(a => a.Id == 0xC5)?.Raw16 ?? 0;
        var offline = report.Attributes.Find(a => a.Id == 0xC6)?.Raw16 ?? 0;

        if (reallocated == 0 && pending == 0 && offline == 0)
        {
            report.HealthVerdictText = "OK — 0 reallocated, pending or uncorrectable sectors.";
        }
        else
        {
            report.HealthVerdictText = $"ALERT — Reallocated: {reallocated} | Pending: {pending} | Uncorrectable: {offline}";
        }
    }

    private static void EvaluateSsd(DriveSmartReport report)
    {
        double wearHealth = 100.0;
        string wearSource = "";

        var attr230 = report.Attributes.Find(a => a.Id == 0xE6);
        if (attr230 != null && attr230.RawValue > 0)
        {
            byte wearByte = (byte)((attr230.RawValue >> 40) & 0xFF);
            if (wearByte == 0) wearByte = (byte)(attr230.RawValue & 0xFF);

            if (wearByte > 0 && wearByte <= 100)
            {
                wearHealth = 100.0 - wearByte;
                wearSource = $"#230 Media Wearout Indicator ({wearByte}%)";
            }
        }

        if (wearHealth == 100.0)
        {
            var attr231 = report.Attributes.Find(a => a.Id == 0xE7);
            if (attr231 != null && attr231.Current > 0 && attr231.Current <= 100)
            {
                wearHealth = attr231.Current;
                wearSource = $"#231 SSD Life Left ({wearHealth}%)";
            }
        }

        if (wearHealth == 100.0 && report.Model.Contains("samsung", StringComparison.OrdinalIgnoreCase))
        {
            var attr177 = report.Attributes.Find(a => a.Id == 0xB1);
            if (attr177 != null && attr177.Current > 0 && attr177.Current < 100)
            {
                wearHealth = attr177.Current;
                wearSource = $"#177 Wear Leveling Count ({wearHealth}%)";
            }
        }

        if (wearHealth == 100.0)
        {
            var attr173 = report.Attributes.Find(a => a.Id == 0xAD);
            if (attr173 != null && attr173.RawValue > 0)
            {
                double avgCycles = attr173.RawValue;
                double nominalMaxCycles = 1500.0;
                double wearPct = Math.Min(100.0, (avgCycles / nominalMaxCycles) * 100.0);
                wearHealth = Math.Round(100.0 - wearPct, 1);
                wearSource = $"#173 Average P/E Cycles ({avgCycles:N0}/{nominalMaxCycles:N0})";
            }
        }

        var badBlocks = report.Attributes.Find(a => a.Id == 0x05)?.Raw16 ?? 0;
        var uncorrectable = report.Attributes.Find(a => a.Id == 0xBB)?.Raw16 ?? 0;
        var eraseFails = report.Attributes.Find(a => a.Id == 0xAC)?.Raw16 ?? 0;
        var progFails = report.Attributes.Find(a => a.Id == 0xAB)?.Raw16 ?? 0;

        double health = wearHealth;
        if (badBlocks > 0)
        {
            health = Math.Max(0.0, health - badBlocks);
        }
        if (uncorrectable > 0)
        {
            health = Math.Max(0.0, health - uncorrectable);
        }

        if (eraseFails > 0) health = Math.Max(0.0, health - (eraseFails * 0.5));
        if (progFails > 0) health = Math.Max(0.0, health - (progFails * 0.5));

        if (report.Attributes.Count == 0)
        {
            report.TelemetryAvailable = false;
            report.HealthVerdictText = "INCONCLUSIVE — 0 SMART attributes decoded.";
            return;
        }

        report.HealthDefault = Math.Round(health, 1);
        report.HealthMoreStrict = report.HealthDefault;

        if (badBlocks == 0 && uncorrectable == 0)
        {
            report.HealthVerdictText = string.IsNullOrEmpty(wearSource) ? "OK — No errors logged." : $"OK — Base: {wearSource}";
        }
        else
        {
            report.HealthVerdictText = $"ALERT — Reallocated: {badBlocks} | Uncorrectable: {uncorrectable}";
        }
    }

    private static double CalculatePerformance(DriveSmartReport report)
    {
        double perf = 100.0;

        var crcAttr = report.Attributes.Find(a => a.Id == 0xC7);
        if (crcAttr != null && crcAttr.RawValue > 0)
        {
            ulong crcErrors = crcAttr.RawValue;
            double penalty = Math.Min(30.0, crcErrors * 1.5);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [PERF]: {crcErrors} UltraDMA CRC errors. Penalty: {penalty:F1}% on bus.");
        }

        var timeoutAttr = report.Attributes.Find(a => a.Id == 0xBC);
        if (timeoutAttr != null && timeoutAttr.Raw16 > 0)
        {
            ulong timeouts = timeoutAttr.Raw16;
            double penalty = Math.Min(25.0, timeouts * 2.0);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [PERF]: {timeouts} command timeouts. Penalty: {penalty:F1}%.");
        }

        var spinUpAttr = report.Attributes.Find(a => a.Id == 0x03);
        if (spinUpAttr != null && spinUpAttr.Current > 0 && spinUpAttr.Current < 80)
        {
            double penalty = Math.Min(20.0, (80 - spinUpAttr.Current) * 0.5);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [PERF]: Degraded spin-up acceleration. Penalty: {penalty:F1}%.");
        }

        if (report.IsNvme && report.TemperatureCelsius >= 70)
        {
            double thermalPenalty = Math.Min(40.0, (report.TemperatureCelsius - 69) * 3.0);
            perf -= thermalPenalty;
            report.BreakdownLogs.Add($"  [PERF]: SSD thermal throttling ({report.TemperatureCelsius} °C). Penalty: {thermalPenalty:F1}%.");
        }

        return Math.Max(10.0, Math.Round(perf, 1));
    }

    private static string CalculateRemainingLifetime(DriveSmartReport report)
    {
        if (!report.TelemetryAvailable || report.Attributes.Count == 0 || !report.HealthDefault.HasValue)
        {
            return "N/A (no SMART)";
        }

        double health = report.HealthDefault.Value;

        if (report.IsSsd || report.IsNvme)
        {
            double wear = 100.0 - health;
            if (wear <= 0.0)
            {
                return "> 1,000 days (0% wear recorded)";
            }
            if (wear < 10.0)
            {
                return "> 1,000 days (minimal wear recorded)";
            }

            if (report.PowerOnHours > 0)
            {
                double wearPerHour = wear / (double)report.PowerOnHours;
                if (wearPerHour > 0.0)
                {
                    double remainingHours = health / wearPerHour;
                    double remainingDays = remainingHours / 24.0;
                    if (remainingDays > 1000.0)
                    {
                        return "> 1,000 days";
                    }
                    return $"> {Math.Max(1, (int)Math.Floor(remainingDays))} days";
                }
            }

            return health >= 90.0 ? "> 1,000 days" : $"> {(int)(health * 10)} days";
        }

        double daysOn = report.PowerOnHours / 24.0;
        double tempPenalty = 0.0;
        if (report.TemperatureCelsius > 45)
        {
            tempPenalty = (report.TemperatureCelsius - 45) * 10.0;
        }

        double baseDays = 1825.0;
        double healthFactor = Math.Pow(health / 100.0, 2);
        double remainingDaysHdd = (baseDays - daysOn - tempPenalty) * healthFactor;

        if (report.PowerOnHours >= 43800)
        {
            return "< 365 days (design life exceeded, high operational hours)";
        }
        if (remainingDaysHdd <= 0.0)
        {
            return "< 100 days (high operational hours / near end-of-service)";
        }
        if (remainingDaysHdd > 1000.0 && report.PowerOnHours < 25000 && health >= 90.0)
        {
            return "> 1,000 days";
        }

        return $"> {Math.Max(30, (int)Math.Floor(remainingDaysHdd))} days";
    }

    private static double GetWeight(byte id, bool moreStrict) => id switch
    {
        0x01 => moreStrict ? 2.0 : 0.5,
        0x05 => moreStrict ? 6.0 : 1.0,
        0x07 => moreStrict ? 2.0 : 0.5,
        0x0A => moreStrict ? 6.0 : 3.0,
        0xC4 => moreStrict ? 4.0 : 0.6,
        0xC5 => moreStrict ? 4.0 : 0.6,
        0xC6 => moreStrict ? 6.0 : 1.0,
        _ => 0.0
    };

    private static double GetLimit(byte id, bool moreStrict) => id switch
    {
        0x01 => moreStrict ? 20.0 : 20.0,
        0x05 => moreStrict ? 60.0 : 70.0,
        0x07 => moreStrict ? 20.0 : 20.0,
        0x0A => moreStrict ? 40.0 : 60.0,
        0xC4 => moreStrict ? 30.0 : 30.0,
        0xC5 => moreStrict ? 30.0 : 48.0,
        0xC6 => moreStrict ? 60.0 : 70.0,
        _ => 0.0
    };
}
