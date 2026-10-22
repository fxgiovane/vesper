using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Rendering;
using Color = System.Drawing.Color;

namespace Vesper;

public static class ScreenshotRenderer
{
    private static Color HexToColor(string hex)
    {
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);
        return Color.FromArgb(r, g, b);
    }

    private static Color GetAnsiBasicColor(int index, bool bright)
    {
        return (index, bright) switch
        {
            (0, false) => HexToColor("0B0F19"),
            (1, false) => HexToColor("F87171"),
            (2, false) => HexToColor("4ADE80"),
            (3, false) => HexToColor("FBBF24"),
            (4, false) => HexToColor("60A5FA"),
            (5, false) => HexToColor("C084FC"),
            (6, false) => HexToColor("38BDF8"),
            (7, false) => HexToColor("E2E8F0"),

            (0, true)  => HexToColor("64748B"),
            (1, true)  => HexToColor("EF4444"),
            (2, true)  => HexToColor("22C55E"),
            (3, true)  => HexToColor("EAB308"),
            (4, true)  => HexToColor("3B82F6"),
            (5, true)  => HexToColor("A855F7"),
            (6, true)  => HexToColor("06B6D4"),
            (7, true)  => HexToColor("FFFFFF"),
            _ => HexToColor("CBD5E1")
        };
    }

    private static Color Get256Color(int idx)
    {
        if (idx < 16) return GetAnsiBasicColor(idx % 8, idx >= 8);
        if (idx >= 232)
        {
            int gray = (idx - 232) * 10 + 8;
            return Color.FromArgb(gray, gray, gray);
        }
        int code = idx - 16;
        int b = (code % 6) * 51;
        int g = ((code / 6) % 6) * 51;
        int r = (code / 36) * 51;
        return Color.FromArgb(r, g, b);
    }

    private static void ApplySgr(string paramStr, ref Color curFg, ref Color? curBg, ref bool isBold, Color defaultFg)
    {
        if (string.IsNullOrEmpty(paramStr))
        {
            curFg = defaultFg;
            curBg = null;
            isBold = false;
            return;
        }

        var parts = paramStr.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
        for (int p = 0; p < parts.Length; p++)
        {
            if (!int.TryParse(parts[p], out int code)) continue;

            switch (code)
            {
                case 0:
                    curFg = defaultFg;
                    curBg = null;
                    isBold = false;
                    break;
                case 1:
                    isBold = true;
                    break;
                case 22:
                    isBold = false;
                    break;
                case 39:
                    curFg = defaultFg;
                    break;
                case 49:
                    curBg = null;
                    break;
                case >= 30 and <= 37:
                    curFg = GetAnsiBasicColor(code - 30, false);
                    break;
                case >= 90 and <= 97:
                    curFg = GetAnsiBasicColor(code - 90, true);
                    break;
                case >= 40 and <= 47:
                    curBg = GetAnsiBasicColor(code - 40, false);
                    break;
                case >= 100 and <= 107:
                    curBg = GetAnsiBasicColor(code - 100, true);
                    break;
                case 38:
                    if (p + 1 < parts.Length && parts[p + 1] == "2" && p + 4 < parts.Length)
                    {
                        if (int.TryParse(parts[p + 2], out int r) &&
                            int.TryParse(parts[p + 3], out int g) &&
                            int.TryParse(parts[p + 4], out int b))
                        {
                            curFg = Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
                        }
                        p += 4;
                    }
                    else if (p + 1 < parts.Length && parts[p + 1] == "5" && p + 2 < parts.Length)
                    {
                        if (int.TryParse(parts[p + 2], out int cIdx))
                        {
                            curFg = Get256Color(cIdx);
                        }
                        p += 2;
                    }
                    break;
                case 48:
                    if (p + 1 < parts.Length && parts[p + 1] == "2" && p + 4 < parts.Length)
                    {
                        if (int.TryParse(parts[p + 2], out int r) &&
                            int.TryParse(parts[p + 3], out int g) &&
                            int.TryParse(parts[p + 4], out int b))
                        {
                            curBg = Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
                        }
                        p += 4;
                    }
                    else if (p + 1 < parts.Length && parts[p + 1] == "5" && p + 2 < parts.Length)
                    {
                        if (int.TryParse(parts[p + 2], out int cIdx))
                        {
                            curBg = Get256Color(cIdx);
                        }
                        p += 2;
                    }
                    break;
            }
        }
    }

    public static int GetVisibleLength(string line)
    {
        int len = 0;
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '\x1b' && (i + 1) < line.Length && line[i + 1] == '[')
            {
                int termIdx = i + 2;
                while (termIdx < line.Length && line[termIdx] >= 0x20 && line[termIdx] <= 0x3F) termIdx++;
                if (termIdx < line.Length && line[termIdx] >= 0x40 && line[termIdx] <= 0x7E)
                {
                    i = termIdx + 1;
                    continue;
                }
            }
            if (line[i] == '[' && (i + 1) < line.Length)
            {
                int closeIdx = line.IndexOf(']', i);
                if (closeIdx > i)
                {
                    string tag = line.Substring(i + 1, closeIdx - i - 1);
                    if (tag == "/" || tag.Contains("#") || tag.StartsWith("bg:") || tag == "bold")
                    {
                        i = closeIdx + 1;
                        continue;
                    }
                }
            }
            len++;
            i++;
        }
        return len;
    }

    public static string PadRightVisible(string line, int targetWidth, char padChar = ' ')
    {
        int cur = GetVisibleLength(line);
        if (cur < targetWidth)
        {
            return line + new string(padChar, targetWidth - cur);
        }
        return line;
    }

    public static void RenderTerminalWindow(string title, string[] lines, string outputPath, int width, int height)
    {
        float colWidth = 9.8f;
        float rowHeight = 20.0f;
        float startX = 30.0f;
        float startY = 52.0f;

        int requiredHeight = (int)(startY + (lines.Length * rowHeight) + 40);
        int finalHeight = Math.Max(height, requiredHeight);
        int finalWidth = Math.Max(width, 1200);

        using var bmp = new Bitmap(finalWidth, finalHeight);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var bgBrush = new SolidBrush(HexToColor("0B0F19")))
        {
            g.FillRectangle(bgBrush, 0, 0, finalWidth, finalHeight);
        }

        using (var titleBarBrush = new SolidBrush(HexToColor("06090E")))
        {
            g.FillRectangle(titleBarBrush, 0, 0, finalWidth, 38);
        }

        using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        var titleSize = g.MeasureString(title, titleFont);
        float tabWidth = Math.Max(190, titleSize.Width + 65);

        using (var tabBrush = new SolidBrush(HexToColor("161B22")))
        using (var tabBorderPen = new Pen(HexToColor("30363D"), 1))
        {
            g.FillRectangle(tabBrush, 12, 6, tabWidth, 32);
            g.DrawRectangle(tabBorderPen, 12, 6, tabWidth, 32);
        }

        using (var cyanBrush = new SolidBrush(HexToColor("38BDF8")))
        using (var whiteBrush = new SolidBrush(HexToColor("E2E8F0")))
        using (var grayBrush = new SolidBrush(HexToColor("64748B")))
        {
            g.DrawString(">_ ", titleFont, cyanBrush, 22, 13);
            g.DrawString(title, titleFont, whiteBrush, 45, 13);
            g.DrawString("×", titleFont, grayBrush, 12 + tabWidth - 18, 12);
            g.DrawString("+", titleFont, grayBrush, 12 + tabWidth + 14, 13);

            g.DrawString("─", titleFont, grayBrush, finalWidth - 85, 12);
            g.DrawString("□", titleFont, grayBrush, finalWidth - 55, 12);
            g.DrawString("✕", titleFont, grayBrush, finalWidth - 25, 12);
        }

        using (var borderPen = new Pen(HexToColor("21262D"), 1))
        {
            g.DrawLine(borderPen, 0, 38, finalWidth, 38);
        }

        using var termFont = new Font("Consolas", 10.5f, FontStyle.Regular);
        using var termFontBold = new Font("Consolas", 10.5f, FontStyle.Bold);
        Color defaultFg = HexToColor("CBD5E1");

        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            float curY = startY + (row * rowHeight);

            int i = 0;
            int col = 0;
            Color curFg = defaultFg;
            Color? curBg = null;
            bool isBold = false;

            while (i < line.Length)
            {
                if (line[i] == '\x1b' && (i + 1) < line.Length && line[i + 1] == '[')
                {
                    int termIdx = i + 2;
                    while (termIdx < line.Length && line[termIdx] >= 0x20 && line[termIdx] <= 0x3F) termIdx++;
                    if (termIdx < line.Length && line[termIdx] >= 0x40 && line[termIdx] <= 0x7E)
                    {
                        char cmd = line[termIdx];
                        if (cmd == 'm')
                        {
                            string paramStr = line.Substring(i + 2, termIdx - (i + 2));
                            ApplySgr(paramStr, ref curFg, ref curBg, ref isBold, defaultFg);
                        }
                        i = termIdx + 1;
                        continue;
                    }
                }

                if (line[i] == '[' && (i + 1) < line.Length)
                {
                    int closeIdx = line.IndexOf(']', i);
                    if (closeIdx > i)
                    {
                        string tag = line.Substring(i + 1, closeIdx - i - 1);
                        if (tag == "/")
                        {
                            curFg = defaultFg;
                            curBg = null;
                            isBold = false;
                            i = closeIdx + 1;
                            continue;
                        }
                        else if (tag.Contains("#") && !tag.StartsWith("bg:"))
                        {
                            int hashIdx = tag.IndexOf('#');
                            string hex = tag.Substring(hashIdx, Math.Min(7, tag.Length - hashIdx));
                            curFg = HexToColor(hex);
                            i = closeIdx + 1;
                            continue;
                        }
                        else if (tag == "bold")
                        {
                            isBold = true;
                            i = closeIdx + 1;
                            continue;
                        }
                        else if (tag.StartsWith("bg:"))
                        {
                            var parts = tag.Split(' ');
                            curBg = HexToColor(parts[0].Substring(3));
                            if (parts.Length > 1 && parts[1].StartsWith("fg:"))
                            {
                                curFg = HexToColor(parts[1].Substring(3));
                            }
                            i = closeIdx + 1;
                            continue;
                        }
                    }
                }

                char ch = line[i];
                if (ch == '\r' || ch == '\n')
                {
                    i++;
                    continue;
                }

                float charX = startX + (col * colWidth);
                if (curBg.HasValue)
                {
                    using var bBrush = new SolidBrush(curBg.Value);
                    g.FillRectangle(bBrush, charX, curY + 1, colWidth, rowHeight - 1);
                }

                using var fBrush = new SolidBrush(curFg);
                var activeFont = isBold ? termFontBold : termFont;
                g.DrawString(ch.ToString(), activeFont, fBrush, charX, curY);

                col++;
                i++;
            }
        }

        using (var windowPen = new Pen(HexToColor("30363D"), 1))
        {
            g.DrawRectangle(windowPen, 0, 0, finalWidth - 1, finalHeight - 1);
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bmp.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"Screenshot saved: {outputPath}");
    }

    public static void PhotographRenderable(string title, IRenderable renderable, string outputPath, int width = 1200, int height = 600, int consoleWidth = 104)
    {
        using var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(sw)
        });
        console.Profile.Width = consoleWidth;
        console.Write(renderable);

        string raw = sw.ToString();
        string[] lines = raw.Replace("\r", "").Split('\n');

        int lastNonEmpty = lines.Length - 1;
        while (lastNonEmpty >= 0 && string.IsNullOrWhiteSpace(lines[lastNonEmpty]))
        {
            lastNonEmpty--;
        }
        if (lastNonEmpty < lines.Length - 1)
        {
            Array.Resize(ref lines, lastNonEmpty + 1);
        }

        RenderTerminalWindow(title, lines, outputPath, width, height);
    }

    public static void GenerateAllScreenshots(List<DriveSmartReport> reports, string targetDir, string? gDriveDir = null)
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine("  CAPTURING LIVE TUI SCREENSHOTS (HARDWARE TELEMETRY)");
        Console.WriteLine("========================================================\n");

        Directory.CreateDirectory(targetDir);

        if (reports == null || reports.Count == 0)
        {
            reports = StoragePipeline.ExecutePipeline(
                () => Program.ScanDirectPhysicalDrives(null, false, false),
                () => RaidStorageEngine.ScanRaidAdapters()
            );
        }

        if (reports.Count == 0)
        {
            Console.WriteLine("No drives detected to generate screenshots.");
            return;
        }

        var rep = reports[0];

        var dashRenderable = TuiIndustrialEngine.BuildMainDashboard(reports, 0, 0, isElevated: true);
        string dashPath = Path.Combine(targetDir, "screenshot_01_tui_dashboard.png");
        PhotographRenderable("VESPER - Main Dashboard", dashRenderable, dashPath, 1280, 720);

        int surfBlocks = 300;
        var surfGrid = new SurfaceBlockStatus[surfBlocks];
        var speedSpark = new Queue<double>();
        var latencySpark = new Queue<double>();
        SurfaceTestProgress? surfProg = null;
        try
        {
            SurfaceTestEngine.RunTest(
                rep.DevicePath,
                rep.DiskSizeBytes,
                rep.BytesPerSector,
                surfBlocks,
                CancellationToken.None,
                (res, p) =>
                {
                    surfProg = p;
                    if (res.BlockIndex < surfBlocks) surfGrid[res.BlockIndex] = res.Status;
                    speedSpark.Enqueue(res.SpeedMBps);
                    if (speedSpark.Count > 24) speedSpark.Dequeue();
                    latencySpark.Enqueue(res.LatencyMs);
                    if (latencySpark.Count > 24) latencySpark.Dequeue();
                },
                onBlockStarting: null,
                throttleMs: 0,
                scanMode: SurfaceScanMode.VerifyRead,
                coverage: SurfaceScanCoverage.QuickDense);
        }
        catch { }

        var surfRenderable = TuiIndustrialEngine.BuildSurfaceRenderable(
            rep, surfGrid, speedSpark, latencySpark, surfProg ?? new SurfaceTestProgress { TotalBlocks = surfBlocks },
            activeBlock: surfBlocks, cancelled: false, coverage: SurfaceScanCoverage.QuickDense, isFinished: true);
        string surfPath = Path.Combine(targetDir, "screenshot_02_surface_test.png");
        PhotographRenderable("VESPER - Surface Scan (Safe Read)", surfRenderable, surfPath, 1200, 600);

        SeekTestProgress? seekProg = null;
        var latencyHistory = new List<double>();
        var seekSpark = new Queue<double>();
        using (var ctsSeek = new CancellationTokenSource(2000))
        {
            try
            {
                RandomSeekTestEngine.RunTest(
                    rep.DevicePath,
                    rep.DiskSizeBytes,
                    rep.BytesPerSector,
                    targetOperations: 0,
                    ctsSeek.Token,
                    (sample, p) =>
                    {
                        seekProg = p;
                        latencyHistory.Add(sample.LatencyMs);
                        if (seekSpark.Count >= 24) seekSpark.Dequeue();
                        seekSpark.Enqueue(sample.LatencyMs);
                    },
                    throttleMs: 0,
                    durationMs: 1500,
                    isSsdOrNvme: rep.IsSsd || rep.IsNvme,
                    getTemperatureCallback: () => rep.TemperatureCelsius);
            }
            catch { }
        }
        var seekVerdict = TuiIndustrialEngine.GetSeekVerdict(rep, seekProg ?? new SeekTestProgress());
        var seekRenderable = TuiIndustrialEngine.BuildSeekRenderable(
            rep, seekSpark, latencyHistory, seekProg ?? new SeekTestProgress(), cancelled: false, isFinished: true, finalMessage: seekVerdict.text);
        string seekPath = Path.Combine(targetDir, "screenshot_03_seek_test.png");
        PhotographRenderable("VESPER - Random Seek Test", seekRenderable, seekPath, 1200, 680);

        var healthRenderable = TuiIndustrialEngine.BuildHealthDetailsRenderable(rep);
        string healthPath = Path.Combine(targetDir, "screenshot_04_health_details.png");
        PhotographRenderable("VESPER - Drive Health", healthRenderable, healthPath, 1200, 520);

        var smartRenderable = TuiIndustrialEngine.BuildSmartAttributesRenderable(rep);
        string smartPath = Path.Combine(targetDir, "screenshot_05_smart_table.png");
        PhotographRenderable("VESPER - S.M.A.R.T. Attributes", smartRenderable, smartPath, 1200, 680);

        var hexRenderable = TuiIndustrialEngine.BuildHexDumpRenderable(rep);
        string hexPath = Path.Combine(targetDir, "screenshot_06_hex_dump.png");
        PhotographRenderable("VESPER - Hex Dump (Sector 0)", hexRenderable, hexPath, 1200, 520);

        BenchmarkProgress? benchProg = null;
        try
        {
            VesperBenchmarkEngine.RunBenchmark(rep, CancellationToken.None, p => { benchProg = p; }, smokeTestMode: true);
        }
        catch { }
        var benchRenderable = TuiIndustrialEngine.BuildBenchmarkRenderable(benchProg ?? new BenchmarkProgress { TargetPath = rep.DevicePath, ModelName = rep.Model }, isCompleted: true);
        string benchPath = Path.Combine(targetDir, "screenshot_07_io_benchmark.png");
        PhotographRenderable("VESPER - Sequential Speed & IOPS", benchRenderable, benchPath, 1200, 560);

        IoBlockLadderProgress? ladderProg = null;
        try
        {
            VesperBenchmarkEngine.RunIoLadderBenchmark(rep, CancellationToken.None, p => { ladderProg = p; }, smokeTestMode: true);
        }
        catch { }
        var ladderRenderable = TuiIndustrialEngine.BuildIoLadderRenderable(ladderProg ?? new IoBlockLadderProgress { TargetPath = rep.DevicePath, ModelName = rep.Model }, isCompleted: true);
        string ladderPath = Path.Combine(targetDir, "screenshot_08_io_ladder.png");
        PhotographRenderable("VESPER - Block Ladder Test", ladderRenderable, ladderPath, 1200, 600);

        LbaCurveProgress? lbaProg = null;
        try
        {
            VesperBenchmarkEngine.RunLbaCurveBenchmark(rep.DevicePath, rep.Model, rep.DiskSizeBytes, rep.BytesPerSector, CancellationToken.None, p => { lbaProg = p; }, smokeTestMode: true);
        }
        catch { }
        var lbaRenderable = TuiIndustrialEngine.BuildLbaCurveRenderable(lbaProg ?? new LbaCurveProgress { DevicePath = rep.DevicePath, ModelName = rep.Model }, isCompleted: true);
        string lbaPath = Path.Combine(targetDir, "screenshot_09_lba_curve.png");
        PhotographRenderable("VESPER - LBA Speed Curve", lbaRenderable, lbaPath, 1200, 560);

        var errReport = FirmwareErrorLogEngine.QueryErrorLog(rep);
        var errRenderable = TuiIndustrialEngine.BuildFirmwareErrorLogRenderable(errReport);
        string errPath = Path.Combine(targetDir, "screenshot_10_firmware_error_log.png");
        PhotographRenderable("VESPER - Firmware Error Logs", errRenderable, errPath, 1200, 520);

        if (!string.IsNullOrEmpty(gDriveDir) && Directory.Exists(gDriveDir))
        {
            File.Copy(dashPath, Path.Combine(gDriveDir, "screenshot_01_tui_dashboard.png"), true);
            File.Copy(surfPath, Path.Combine(gDriveDir, "screenshot_02_surface_test.png"), true);
            File.Copy(seekPath, Path.Combine(gDriveDir, "screenshot_03_seek_test.png"), true);
            File.Copy(healthPath, Path.Combine(gDriveDir, "screenshot_04_health_details.png"), true);
            File.Copy(smartPath, Path.Combine(gDriveDir, "screenshot_05_smart_table.png"), true);
            File.Copy(hexPath, Path.Combine(gDriveDir, "screenshot_06_hex_dump.png"), true);
            File.Copy(benchPath, Path.Combine(gDriveDir, "screenshot_07_io_benchmark.png"), true);
            File.Copy(ladderPath, Path.Combine(gDriveDir, "screenshot_08_io_ladder.png"), true);
            File.Copy(lbaPath, Path.Combine(gDriveDir, "screenshot_09_lba_curve.png"), true);
            File.Copy(errPath, Path.Combine(gDriveDir, "screenshot_10_firmware_error_log.png"), true);
            Console.WriteLine($"Screenshots synced with Google Drive: {gDriveDir}");
        }

        Console.WriteLine("\nAll screenshots generated with live hardware data!");
    }

    public static void GenerateAllScreenshots(string targetDir, string? gDriveDir = null)
    {
        GenerateAllScreenshots(new List<DriveSmartReport>(), targetDir, gDriveDir);
    }
}
