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

        if (report.IsNvme)
        {
            if (report.HealthDefault <= 0.0) report.HealthDefault = 100.0;
            if (report.HealthMoreStrict <= 0.0) report.HealthMoreStrict = report.HealthDefault;
            report.Performance = 100.0;
            report.RemainingLifetimeText = CalculateRemainingLifetime(report.HealthDefault, report.PowerOnHours, report.TemperatureCelsius);
            report.HealthVerdictText = report.HealthDefault >= 100.0
                ? "A condicao do SSD NVMe esta PERFEITA. Nao foram encontrados setores fracos ou problematicos."
                : $"A condicao do SSD NVMe e estavel. Desgaste registrado de celulas flash: {100.0 - report.HealthDefault:F0}%.";
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
        report.RemainingLifetimeText = CalculateRemainingLifetime(report.HealthDefault, report.PowerOnHours, report.TemperatureCelsius);
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
                    report.TotalTbWritten = Math.Round((attrF1.RawValue * (double)report.BytesPerSector) / Math.Pow(1024, 4), 2);
                    if (report.TotalTbWritten == 0.0) report.TotalTbWritten = Math.Round(attrF1.RawValue / 1024.0, 2);
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
                report.TotalTbRead = Math.Round(attrF2.RawValue / 1024.0, 2);
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

        var criticalIds = new byte[] { 0x05, 0x07, 0x0A, 0xC4, 0xC5, 0xC6 };

        foreach (var id in criticalIds)
        {
            var attr = report.Attributes.Find(a => a.Id == id);
            if (attr == null) continue;

            ushort raw16 = attr.Raw16;

            if (isSeagate && id == 0x07)
            {
                report.BreakdownLogs.Add($"  [ID 0x07] Seek Error Rate (Seagate): Raw={attr.RawValue} filtrado (contador acumulado de operacoes/busca de cabeca).");
                continue;
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
                report.BreakdownLogs.Add($"  [ID 0x{id:X2}] {attr.Name}: Raw16={raw16} | Padrao: -{degDef:F1}% (Fator: {fDef:F4}) | Rigoroso: -{degStr:F1}% (Fator: {fStr:F4})");
            }
        }

        report.HealthDefault = Math.Round(factorDefault * 100.0, 1);
        report.HealthMoreStrict = Math.Round(factorStrict * 100.0, 1);

        var reallocated = report.Attributes.Find(a => a.Id == 0x05)?.Raw16 ?? 0;
        var pending = report.Attributes.Find(a => a.Id == 0xC5)?.Raw16 ?? 0;
        var offline = report.Attributes.Find(a => a.Id == 0xC6)?.Raw16 ?? 0;

        if (reallocated == 0 && pending == 0 && offline == 0)
        {
            report.HealthVerdictText = "A condicao do disco esta PERFEITA. Nao foram encontrados setores fracos ou problematicos. Superficie magnetica 100% integra.";
        }
        else
        {
            var sb = new StringBuilder();
            if (reallocated > 0)
                sb.Append($"Existem {reallocated} setores defeituosos na superficie do disco. O conteudo destes setores foi movido para a area de reserva. ");
            if (pending > 0)
                sb.Append($"Existem {pending} setores fracos encontrados na superficie do disco. Eles podem ser realocados a qualquer momento no uso posterior. ");
            if (offline > 0)
                sb.Append($"Existem {offline} setores incorrigiveis offline detectados pelo autoteste. ");
            report.HealthVerdictText = sb.ToString().Trim();
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
                wearSource = $"#230 Media Wearout Indicator (desgaste de celulas flash: {wearByte}%)";
            }
        }

        if (wearHealth == 100.0)
        {
            var attr231 = report.Attributes.Find(a => a.Id == 0xE7);
            if (attr231 != null && attr231.Current > 0 && attr231.Current <= 100)
            {
                wearHealth = attr231.Current;
                wearSource = $"#231 SSD Life Left (valor normalizado: {wearHealth}%)";
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
                wearSource = $"#173 Average P/E Cycles ({avgCycles:N0} ciclos de {nominalMaxCycles:N0} nominais)";
            }
        }

        var badBlocks = report.Attributes.Find(a => a.Id == 0x05)?.Raw16 ?? 0;
        var uncorrectable = report.Attributes.Find(a => a.Id == 0xBB)?.Raw16 ?? 0;
        var eraseFails = report.Attributes.Find(a => a.Id == 0xAC)?.Raw16 ?? 0;
        var progFails = report.Attributes.Find(a => a.Id == 0xAB)?.Raw16 ?? 0;

        double surfaceFactor = 1.0;
        if (badBlocks > 0) surfaceFactor *= (1.0 - Math.Min(0.50, badBlocks * 0.05));
        if (uncorrectable > 0) surfaceFactor *= (1.0 - Math.Min(0.50, uncorrectable * 0.10));
        if (eraseFails > 0) surfaceFactor *= (1.0 - Math.Min(0.30, eraseFails * 0.05));
        if (progFails > 0) surfaceFactor *= (1.0 - Math.Min(0.30, progFails * 0.05));

        report.HealthDefault = Math.Round(wearHealth * surfaceFactor, 1);
        report.HealthMoreStrict = report.HealthDefault;

        if (badBlocks == 0 && uncorrectable == 0)
        {
            report.HealthVerdictText = $"A condicao do disco de estado solido esta PERFEITA. Nao foram encontrados setores fracos ou problematicos. A saude e determinada pelo(s) atributo(s) especifico(s) do SSD: {wearSource}.";
        }
        else
        {
            report.HealthVerdictText = $"ALERTA DE DESGASTE E BLOCOS: O SSD apresenta blocos de memoria defeituosos ({badBlocks} blocos realocados, {uncorrectable} erros incorrigiveis).";
        }
    }

    private static double CalculatePerformance(DriveSmartReport report)
    {
        double perf = 100.0;

        // 1. UltraDMA CRC Error Count (ID 0xC7 / 199) â€” Integridade do cabo e barramento
        var crcAttr = report.Attributes.Find(a => a.Id == 0xC7);
        if (crcAttr != null && crcAttr.RawValue > 0)
        {
            ulong crcErrors = crcAttr.RawValue;
            double penalty = Math.Min(30.0, crcErrors * 1.5);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [DESEMPENHO]: {crcErrors} erros UltraDMA CRC detectados (ID 0xC7). Penalidade de {penalty:F1}% aplicada na interface de comunicacao.");
        }

        // 2. Command Timeout (ID 0xBC / 188) â€” Comandos abortados por atraso no controlador
        var timeoutAttr = report.Attributes.Find(a => a.Id == 0xBC);
        if (timeoutAttr != null && timeoutAttr.RawValue > 0)
        {
            ulong timeouts = timeoutAttr.RawValue;
            double penalty = Math.Min(25.0, timeouts * 2.0);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [DESEMPENHO]: {timeouts} timeouts de comando (ID 0xBC). Penalidade de {penalty:F1}%.");
        }

        // 3. Spin-Up Time (ID 0x03) â€” Degradacao na aceleracao mecanica do motor em HDD
        var spinUpAttr = report.Attributes.Find(a => a.Id == 0x03);
        if (spinUpAttr != null && spinUpAttr.Current > 0 && spinUpAttr.Current < 80)
        {
            double penalty = Math.Min(20.0, (80 - spinUpAttr.Current) * 0.5);
            perf -= penalty;
            report.BreakdownLogs.Add($"  [DESEMPENHO]: Aceleracao mecanica de motor degradada (ID 0x03). Penalidade de {penalty:F1}%.");
        }

        // 4. Throttling TÃ©rmico em SSDs NVMe (Temperatura >= 70 Â°C)
        if (report.IsNvme && report.TemperatureCelsius >= 70)
        {
            double thermalPenalty = Math.Min(40.0, (report.TemperatureCelsius - 69) * 3.0);
            perf -= thermalPenalty;
            report.BreakdownLogs.Add($"  [DESEMPENHO]: Estrangulamento termico do SSD ({report.TemperatureCelsius} Â°C). Penalidade de {thermalPenalty:F1}%.");
        }

        return Math.Max(10.0, Math.Round(perf, 1));
    }

    private static string CalculateRemainingLifetime(double health, ulong powerOnHours, int tempCelsius)
    {
        double daysOn = powerOnHours / 24.0;
        double tempPenalty = 0.0;
        if (tempCelsius > 45)
        {
            tempPenalty = (tempCelsius - 45) * 10.0;
        }

        double baseDays = 1825.0;
        double healthFactor = Math.Pow(health / 100.0, 2);
        double remainingDays = (baseDays - daysOn - tempPenalty) * healthFactor;

        if (health >= 90.0 && remainingDays > 1000.0)
        {
            return "mais que 1000 dias";
        }
        if (remainingDays <= 0.0)
        {
            return "mais que 100 dias (unidade operando estavel alem da vida nominal de 5 anos)";
        }
        if (remainingDays > 1000.0)
        {
            return "mais que 1000 dias";
        }

        return $"mais que {(int)Math.Floor(remainingDays)} dias";
    }

    private static double GetWeight(byte id, bool moreStrict) => id switch
    {
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
        0x05 => moreStrict ? 60.0 : 70.0,
        0x07 => moreStrict ? 20.0 : 20.0,
        0x0A => moreStrict ? 40.0 : 60.0,
        0xC4 => moreStrict ? 30.0 : 30.0,
        0xC5 => moreStrict ? 30.0 : 48.0,
        0xC6 => moreStrict ? 60.0 : 70.0,
        _ => 0.0
    };
}
