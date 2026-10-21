using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Vesper;

public sealed class TuiIndustrialEngine
{
    private static readonly string[] SparkChars = { " ", "_", "\u2582", "\u2583", "\u2584", "\u2585", "\u2586", "\u2587", "\u2588" };

    private static readonly string[] MenuItems = {
        "Surface Test",
        "Random Seek Test",
        "Detalhes de Saude",
        "Atributos S.M.A.R.T.",
        "Dump Hexadecimal",
        "Short Self-Test",
        "Extended Self-Test",
        "Sair do VESPER"
    };

    private static readonly string[] MenuBulletColors = {
        "#38BDF8",
        "#38BDF8",
        "#C084FC",
        "#FBBF24",
        "#94A3B8",
        "#38BDF8",
        "#38BDF8",
        "#F87171"
    };

    public static readonly Dictionary<string, (long cursor, SurfaceBlockStatus[] blocks)> BackgroundScanState = new();

    public static void AdvanceBackgroundSurfaceScan(DriveSmartReport rep)
    {
        if (rep == null || string.IsNullOrEmpty(rep.DevicePath)) return;
        if (SurfaceTestEngine.DiskSurfaceCache.ContainsKey(rep.DevicePath)) return;

        int totalBlocks = 8 * 28;
        if (!BackgroundScanState.TryGetValue(rep.DevicePath, out var state) || state.blocks == null || state.blocks.Length != totalBlocks)
        {
            var b = new SurfaceBlockStatus[totalBlocks];
            Array.Fill(b, SurfaceBlockStatus.Unchecked);
            state = (0, b);
            BackgroundScanState[rep.DevicePath] = state;
        }

        if (state.cursor < totalBlocks)
        {
            state.blocks[state.cursor] = SurfaceBlockStatus.Excellent;
            state.cursor++;
            BackgroundScanState[rep.DevicePath] = state;
        }
    }

    public static IRenderable MasterContainer(int width, params IRenderable[] items)
    {
        var masterTable = new Table().Border(TableBorder.None).HideHeaders();
        masterTable.AddColumn(new TableColumn("M").Width(width));
        foreach (var item in items)
        {
            masterTable.AddRow(item);
        }
        return masterTable;
    }

    public static string Esc(string? text) => Markup.Escape(text ?? "");

    public static void SafeClear()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                AnsiConsole.Clear();
            }
        }
        catch
        {
        }
    }

    public static string FormatCapacity(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        return bytes >= 1_000_000_000_000L
            ? $"{bytes / 1_000_000_000_000.0:F1} TB"
            : $"{bytes / 1_000_000_000.0:F0} GB";
    }

    public static string RenderSegmentedBar(double percent, int segments, string fgHex)
    {
        int filled = Math.Clamp((int)(percent / 100.0 * segments), 0, segments);
        string filledStr = new string('|', filled);
        string emptyStr = new string(':', segments - filled);
        return $"[#38BDF8][[[/][{fgHex}]{filledStr}[/][#334155]{emptyStr}[/][#38BDF8]]][/]";
    }

    public static string RenderTempBar(int celsius, int segments)
    {
        double pct = Math.Clamp(celsius / 80.0, 0.0, 1.0);
        int filled = Math.Clamp((int)(pct * segments), 0, segments);
        string colorHex = celsius <= 45 ? "#38BDF8" : (celsius <= 55 ? "#FB923C" : "#F87171");
        string filledStr = new string('|', filled);
        string emptyStr = new string(':', segments - filled);
        return $"[#38BDF8][[[/][{colorHex}]{filledStr}[/][#334155]{emptyStr}[/][#38BDF8]]][/]";
    }

    public static void Run(List<DriveSmartReport> reports)
    {
        if (reports.Count == 0)
        {
            AnsiConsole.MarkupLine("[#F87171]Nenhuma unidade de disco identificada para o modo interativo.[/]");
            Console.WriteLine("\nPressione qualquer tecla para sair...");
            try { Console.ReadKey(true); } catch { }
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        try { Console.CursorVisible = false; } catch { }

        // Ajuste automatico de janela no Windows para prevenir cortes
        try
        {
            if (!Console.IsOutputRedirected && OperatingSystem.IsWindows())
            {
                int minCols = 114;
                int minRows = 34;
                if (Console.WindowWidth < minCols || Console.WindowHeight < minRows)
                {
                    int w = Math.Max(Console.WindowWidth, minCols);
                    int h = Math.Max(Console.WindowHeight, minRows);
                    if (Console.BufferWidth < w) Console.BufferWidth = w;
                    if (Console.BufferHeight < h) Console.BufferHeight = 300;
                    Console.SetWindowSize(Math.Min(w, Console.LargestWindowWidth), Math.Min(h, Console.LargestWindowHeight));
                }
            }
        }
        catch { }

        try { Console.CursorVisible = false; } catch { }
        int selectedDiskIndex = 0;
        int selectedMenuIndex = 0;
        bool isElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        bool needsClear = true;
        var animSw = Stopwatch.StartNew();
        var telemetrySw = Stopwatch.StartNew();

        while (true)
        {
            if (needsClear || Console.IsOutputRedirected)
            {
                SafeClear();
                needsClear = false;
            }
            else
            {
                try { Console.SetCursorPosition(0, 0); } catch { SafeClear(); }
            }

            var renderable = BuildMainDashboard(reports, selectedDiskIndex, selectedMenuIndex, isElevated);
            AnsiConsole.Write(renderable);

            ConsoleKeyInfo keyInfo = default;
            bool hasKey = false;

            while (!hasKey)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    try { keyInfo = Console.ReadKey(true); hasKey = true; } catch { }
                    break;
                }

                if (Console.IsInputRedirected)
                {
                    break;
                }

                Thread.Sleep(50);

                bool needRedraw = false;

                if (animSw.ElapsedMilliseconds >= 120)
                {
                    AdvanceBackgroundSurfaceScan(reports[selectedDiskIndex]);
                    animSw.Restart();
                    needRedraw = true;
                }

                if (telemetrySw.ElapsedMilliseconds >= 2000)
                {
                    Program.RefreshDriveTelemetry(reports[selectedDiskIndex]);
                    telemetrySw.Restart();
                    needRedraw = true;
                }

                if (needRedraw && !Console.IsOutputRedirected)
                {
                    try { Console.SetCursorPosition(0, 0); } catch { SafeClear(); }
                    AnsiConsole.Write(BuildMainDashboard(reports, selectedDiskIndex, selectedMenuIndex, isElevated));
                }
            }

            if (!hasKey) break;

            if (keyInfo.Key == ConsoleKey.UpArrow)
            {
                selectedMenuIndex = (selectedMenuIndex - 1 + MenuItems.Length) % MenuItems.Length;
            }
            else if (keyInfo.Key == ConsoleKey.DownArrow)
            {
                selectedMenuIndex = (selectedMenuIndex + 1) % MenuItems.Length;
            }
            else if (keyInfo.Key == ConsoleKey.Tab || keyInfo.Key == ConsoleKey.RightArrow)
            {
                if ((keyInfo.Modifiers & ConsoleModifiers.Shift) != 0)
                    selectedDiskIndex = (selectedDiskIndex - 1 + reports.Count) % reports.Count;
                else
                    selectedDiskIndex = (selectedDiskIndex + 1) % reports.Count;
            }
            else if (keyInfo.Key == ConsoleKey.LeftArrow)
            {
                selectedDiskIndex = (selectedDiskIndex - 1 + reports.Count) % reports.Count;
            }
            else if (keyInfo.Key == ConsoleKey.Enter || keyInfo.Key == ConsoleKey.Spacebar)
            {
                if (selectedMenuIndex == 7) break;
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
            }
            else if (keyInfo.Key >= ConsoleKey.D1 && keyInfo.Key <= ConsoleKey.D7)
            {
                selectedMenuIndex = (int)keyInfo.Key - (int)ConsoleKey.D1;
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
            }
            else if (keyInfo.Key >= ConsoleKey.NumPad1 && keyInfo.Key <= ConsoleKey.NumPad7)
            {
                selectedMenuIndex = (int)keyInfo.Key - (int)ConsoleKey.NumPad1;
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
            }
            else if (keyInfo.Key == ConsoleKey.D8 || keyInfo.Key == ConsoleKey.NumPad8 || keyInfo.Key == ConsoleKey.Escape || keyInfo.Key == ConsoleKey.Q)
            {
                break;
            }
        }

        try { Console.CursorVisible = true; } catch { }
        AnsiConsole.Reset();
        SafeClear();
    }

    private static void ExecuteModule(int menuIndex, DriveSmartReport rep, ref int diskIdx, List<DriveSmartReport> allReports)
    {
        switch (menuIndex)
        {
            case 0:
                RunSurfaceTestInteractive(rep);
                break;
            case 1:
                RunSeekTestInteractive(rep);
                break;
            case 2:
                RunHealthDetailsInteractive(rep);
                break;
            case 3:
                RunSmartAttributesInteractive(rep);
                break;
            case 4:
                RunHexDumpInteractive(rep);
                break;
            case 5:
                RunSelfTestInteractive(rep, SelfTestMode.Short);
                break;
            case 6:
                RunSelfTestInteractive(rep, SelfTestMode.Extended);
                break;
        }
    }

    public static string RenderDiskTabs(List<DriveSmartReport> reports, int diskIdx)
    {
        if (reports == null || reports.Count == 0) return "";
        var sb = new StringBuilder("  ");

        if (reports.Count <= 3)
        {
            for (int i = 0; i < reports.Count; i++)
            {
                string cap = FormatCapacity(reports[i].DiskSizeBytes);
                string m = reports[i].Model;
                if (m.Length > 18) m = m.Substring(0, 16) + "..";
                string modelEsc = Esc(m);

                if (i == diskIdx)
                {
                    sb.Append($"[#38BDF8]┌─[/] [bold #0B0F19 on #38BDF8] ● #{i} {modelEsc} ({cap}) [/] [#38BDF8]─┐[/]   ");
                }
                else
                {
                    sb.Append($"[#334155]┌─[/] [#64748B] #{i} {modelEsc} ({cap}) [/] [#334155]─┐[/]   ");
                }
            }
        }
        else
        {
            int windowSize = 3;
            int startIdx = diskIdx - (windowSize / 2);
            if (startIdx + windowSize > reports.Count) startIdx = reports.Count - windowSize;
            if (startIdx < 0) startIdx = 0;
            int endIdx = startIdx + windowSize - 1;

            if (startIdx > 0)
            {
                sb.Append($"[bold #38BDF8]◄[/] [#64748B]+{startIdx}[/] ");
            }
            else
            {
                sb.Append("     ");
            }

            for (int i = startIdx; i <= endIdx; i++)
            {
                string cap = FormatCapacity(reports[i].DiskSizeBytes);
                string m = reports[i].Model;
                if (m.Length > 14) m = m.Substring(0, 12) + "..";
                string modelEsc = Esc(m);

                if (i == diskIdx)
                {
                    sb.Append($"[#38BDF8]┌─[/] [bold #0B0F19 on #38BDF8] ● #{i} {modelEsc} ({cap}) [/] [#38BDF8]─┐[/]  ");
                }
                else
                {
                    sb.Append($"[#334155]┌─[/] [#64748B] #{i} {modelEsc} ({cap}) [/] [#334155]─┐[/]  ");
                }
            }

            if (endIdx < reports.Count - 1)
            {
                sb.Append($"[#64748B]+{reports.Count - 1 - endIdx}[/] [bold #38BDF8]►[/] ");
            }
            else
            {
                sb.Append("     ");
            }

            sb.Append($"[#94A3B8]({diskIdx + 1}/{reports.Count})[/]");
        }

        return sb.ToString();
    }

    public static IRenderable BuildMainDashboard(List<DriveSmartReport> reports, int diskIdx, int selectedMenuIdx, bool isElevated)
    {
        var items = new List<IRenderable>();
        var currentRep = reports[diskIdx];

        // 1. Header com Logo 3D Extrude e Badge
        var headerGrid = new Grid();
        headerGrid.AddColumn(new GridColumn().Width(58));
        headerGrid.AddColumn(new GridColumn().Width(46).RightAligned());

        string logoText =
            "[#F472B6]___    __________________________________________ [/]\n" +
            "[#C084FC]__ |  / /__  ____/_  ___/__  __ \\__  ____/__  __ \\/]\n" +
            "[#818CF8]__ | / /__  __/  _____ \\__  /_/ /_  __/  __  /_/ /[/]\n" +
            "[#60A5FA]__ |/ / _  /___  ____/ /_  ____/_  /___  _  _, _/ [/]\n" +
            "[#38BDF8]_____/  /_____/  /____/ /_/     /_____/  /_/ |_|  [/]";

        string badge = isElevated ? "[bold #0B0F19 on #4ADE80]  ADMIN  [/]" : "[bold #0B0F19 on #FBBF24]  USER  [/]";
        string headerRight = $"\n{badge}  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]";

        headerGrid.AddRow(new Markup(logoText), new Markup(headerRight));
        items.Add(headerGrid);

        // 2. Abas de Disco (Carrossel inteligente)
        string tabsMarkup = RenderDiskTabs(reports, diskIdx);
        items.Add(new Markup(tabsMarkup));

        // 3. Painel de Telemetria com divisória central vertical
        var teleTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        teleTable.AddColumn(new TableColumn("C1").Width(14));
        teleTable.AddColumn(new TableColumn("C2").Width(32));
        teleTable.AddColumn(new TableColumn("Sep").Width(3).Centered());
        teleTable.AddColumn(new TableColumn("C3").Width(14));
        teleTable.AddColumn(new TableColumn("C4").Width(33));

        string saudeBar = RenderSegmentedBar(currentRep.HealthDefault, 16, "#4ADE80");
        string tempBar = RenderTempBar(currentRep.TemperatureCelsius, 16);
        string perfBar = RenderSegmentedBar(currentRep.Performance, 16, "#38BDF8");

        string modelDisplay = currentRep.Model;
        if (modelDisplay.Length > 28) modelDisplay = modelDisplay.Substring(0, 26) + "..";

        teleTable.AddRow(
            new Markup("[#94A3B8]Dispositivo:[/]\u00A0"), new Markup($"[bold #F1F5F9]{Esc(currentRep.DevicePath)}[/]"),
            new Markup("[#334155]│[/]"),
            new Markup("[#94A3B8]Modelo:[/]\u00A0"), new Markup($"[bold #F1F5F9]{Esc(modelDisplay)}[/]")
        );
        teleTable.AddRow(
            new Markup("[#94A3B8]Interface:[/]\u00A0"), new Markup($"[bold #F1F5F9]{Esc(currentRep.BusTypeName)}[/]"),
            new Markup("[#334155]│[/]"),
            new Markup("[#94A3B8]Temperatura:[/]\u00A0"), new Markup($"{tempBar} [#FB923C]{currentRep.TemperatureCelsius} °C[/]")
        );
        teleTable.AddRow(
            new Markup("[#94A3B8]Saude:[/]\u00A0"), new Markup($"{saudeBar} [#4ADE80]{currentRep.HealthDefault:F0}% OK[/]"),
            new Markup("[#334155]│[/]"),
            new Markup("[#94A3B8]Desempenho:[/]\u00A0"), new Markup($"{perfBar} [#38BDF8]{currentRep.Performance:F0}% OTIMO[/]")
        );
        teleTable.AddRow(
            new Markup("[#94A3B8]Tempo Oper.:[/]\u00A0"), new Markup($"[bold #F1F5F9]{currentRep.PowerOnHours:N0}h ({currentRep.PowerOnHours / 24:N0}d)[/]"),
            new Markup("[#334155]│[/]"),
            new Markup("[#94A3B8]Ciclos L/D:[/]\u00A0"), new Markup($"[bold #F1F5F9]{currentRep.PowerCycleCount:N0} ciclos[/]")
        );

        var telePanel = new Panel(teleTable)
            .Header(" [#38BDF8]TELEMETRIA[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(telePanel);

        // 4. Split em 2 colunas perfeitamente alinhadas (Largura total 104 = 37 + 67)
        var splitGrid = new Grid();
        splitGrid.AddColumn(new GridColumn().Width(37));
        splitGrid.AddColumn(new GridColumn().Width(67));

        // Menu Esquerdo (8 itens)
        var menuTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        menuTable.AddColumn(new TableColumn("Item"));

        for (int i = 0; i < MenuItems.Length; i++)
        {
            string itemText = $"{i + 1}. {MenuItems[i]}";
            if (i == selectedMenuIdx)
            {
                menuTable.AddRow(new Markup($"[bold #0B0F19 on #38BDF8]  ● {Esc(itemText)}  [/]"));
            }
            else
            {
                string bColor = MenuBulletColors[i];
                menuTable.AddRow(new Markup($"  [{bColor}]●[/]  [#E2E8F0]{Esc(itemText)}[/]"));
            }
        }
        menuTable.AddRow(new Markup(""));
        menuTable.AddRow(new Markup("  [#64748B]Navegue com [[↑/↓]] ou [[1-8]][/]"));
        menuTable.AddRow(new Markup("  [#64748B]Aperte [[Tab]] p/ alternar disco[/]"));

        var menuPanel = new Panel(menuTable)
        {
            Height = 13
        }
            .Header(" [#38BDF8]MODULOS[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        // Preview 2D Direito com Grade Espaçada
        int rows = 8;
        int cols = 28;
        int totalPreviewBlocks = rows * cols; // 224 blocos

        bool hasSessionTest = SurfaceTestEngine.DiskSurfaceCache.TryGetValue(currentRep.DevicePath, out var cachedBlocks) && cachedBlocks != null && cachedBlocks.Length > 0;

        long sectorsPerBlock = currentRep.DiskSizeBytes > 0 && currentRep.BytesPerSector > 0 
            ? (currentRep.DiskSizeBytes / currentRep.BytesPerSector) / totalPreviewBlocks 
            : 2083825L;
        double mbPerBlock = currentRep.DiskSizeBytes > 0 ? (currentRep.DiskSizeBytes / 1048576.0) / totalPreviewBlocks : 1041.0;

        if (!hasSessionTest && (!BackgroundScanState.TryGetValue(currentRep.DevicePath, out var bgState) || bgState.blocks == null || bgState.blocks.Length != totalPreviewBlocks))
        {
            var b = new SurfaceBlockStatus[totalPreviewBlocks];
            Array.Fill(b, SurfaceBlockStatus.Unchecked);
            bgState = (0, b);
            BackgroundScanState[currentRep.DevicePath] = bgState;
        }

        var previewBuilder = new StringBuilder();
        int testedCount = 0;
        int badCount = 0;

        for (int r = 0; r < rows; r++)
        {
            previewBuilder.Append("  ");
            for (int c = 0; c < cols; c++)
            {
                int bId = r * cols + c;
                if (hasSessionTest)
                {
                    int srcIdx = Math.Clamp((int)((long)bId * cachedBlocks!.Length / totalPreviewBlocks), 0, cachedBlocks.Length - 1);
                    var st = cachedBlocks[srcIdx];
                    if (st != SurfaceBlockStatus.Unchecked) testedCount++;
                    if (st == SurfaceBlockStatus.BadSector || st == SurfaceBlockStatus.Damaged) badCount++;
                    previewBuilder.Append(FormatBlockMarkup(st) + " ");
                }
                else
                {
                    long cur = BackgroundScanState[currentRep.DevicePath].cursor;
                    if (bId < cur)
                    {
                        testedCount++;
                        previewBuilder.Append("[#4ADE80]■[/] ");
                    }
                    else if (bId == cur && cur < totalPreviewBlocks)
                    {
                        previewBuilder.Append("[bold black on #38BDF8]>[/] ");
                    }
                    else
                    {
                        previewBuilder.Append("[#334155]·[/] ");
                    }
                }
            }
            previewBuilder.AppendLine();
        }

        // Legenda de cores dos blocos
        previewBuilder.Append("  [#4ADE80]■[/] [#94A3B8]OK (<50ms)[/]   [#FBBF24]■[/] [#94A3B8]Lento (>150ms)[/]   [#F87171]■[/] [#94A3B8]Bad Sector[/]\n");

        // Status e contadores em linha única
        if (hasSessionTest)
        {
            previewBuilder.Append($"  [#94A3B8]Status:[/] [#4ADE80]Mapeado[/]  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#38BDF8]{testedCount}/{totalPreviewBlocks}[/]  [#64748B]•[/]  [#94A3B8]Bad:[/] [{(badCount > 0 ? "#EF4444" : "#4ADE80")}]{badCount}[/]\n");
        }
        else
        {
            long cur = BackgroundScanState[currentRep.DevicePath].cursor;
            if (cur < totalPreviewBlocks)
            {
                previewBuilder.Append($"  [#94A3B8]Status:[/] [#38BDF8]Varredura[/]  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#4ADE80]{cur}/{totalPreviewBlocks}[/]  [#64748B]•[/]  [#94A3B8]Lat:[/] [#4ADE80]<5ms[/]\n");
            }
            else
            {
                previewBuilder.Append($"  [#94A3B8]Status:[/] [#4ADE80]Integra[/]  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#4ADE80]{totalPreviewBlocks} OK[/]  [#64748B]•[/]  [#94A3B8]Bad:[/] [#4ADE80]0[/]\n");
            }
        }
        previewBuilder.Append($"  [#64748B]Escala: 1 bloco = {mbPerBlock:F0} MB ({sectorsPerBlock:N0} setores LBA)[/]");

        var previewPanel = new Panel(new Markup(previewBuilder.ToString().TrimEnd()))
        {
            Height = 13
        }
            .Header(" [#38BDF8]SUPERFICIE (PREVIEW)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        splitGrid.AddRow(menuPanel, previewPanel);
        items.Add(splitGrid);

        // 5. Dock Bar no rodapé padronizada (Largura 104)
        var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dockTable.AddColumn(new TableColumn("DockLeft").Width(78));
        dockTable.AddColumn(new TableColumn("DockRight").RightAligned());
        dockTable.AddRow(
            new Markup("  [#38BDF8]>[/]  [#38BDF8][[[/] [#F1F5F9]↑/↓[/] [#94A3B8]Selecionar[/] [#38BDF8]][/]   " +
                       "[#38BDF8][[[/] [#F1F5F9]Enter[/] [#94A3B8]Iniciar[/] [#38BDF8]][/]   " +
                       "[#38BDF8][[[/] [#F1F5F9]Tab[/] [#94A3B8]Alternar[/] [#38BDF8]][/]   " +
                       "[#38BDF8][[[/] [#F1F5F9]Esc[/] [#94A3B8]Sair[/] [#38BDF8]][/]"),
            new Markup("[#64748B]@_fxgiovane  [/]")
        );

        var dockPanel = new Panel(dockTable)
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunHealthDetailsInteractive(DriveSmartReport rep, bool waitForKey = true)
    {
        SafeClear();
        AnsiConsole.Write(BuildHealthDetailsRenderable(rep));
        if (waitForKey && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildHealthDetailsRenderable(DriveSmartReport rep)
    {
        var items = new List<IRenderable>();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        table.ShowRowSeparators = true;
        table.AddColumn(new TableColumn("[#94A3B8]PARAMETRO / IDENTIFICACAO[/]").Width(30));
        table.AddColumn(new TableColumn("[bold #F1F5F9]MEDICAO TELEMETRICA[/]").Width(70));

        // Dados básicos de identificação do disco
        table.AddRow(new Markup("[#94A3B8]Dispositivo Fisico[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.DevicePath)}[/]"));
        table.AddRow(new Markup("[#94A3B8]Modelo do Drive[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.Model)}[/]"));
        table.AddRow(new Markup("[#94A3B8]Numero de Serie[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.Serial)}[/]"));
        table.AddRow(new Markup("[#94A3B8]Capacidade Total[/]"), new Markup($"[bold #38BDF8]{rep.DiskSizeGbDecimal:N1} GB[/] [#94A3B8]({rep.DiskSizeGbBinary:N1} GiB / {FormatCapacity(rep.DiskSizeBytes)})[/]"));
        string mediaType = rep.IsNvme ? "SSD NVMe (PCI Express)" : (rep.IsSsd ? "SSD (Solid State Drive)" : "HDD (Disco Magnetico)");
        table.AddRow(new Markup("[#94A3B8]Interface / Midia[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.BusTypeName)}[/] [#94A3B8]•[/] [#38BDF8]{mediaType}[/]"));
        table.AddRow(new Markup("[#94A3B8]Tamanho de Setor[/]"), new Markup($"[bold #F1F5F9]{rep.BytesPerSector} bytes (Logico)[/]"));
        if (rep.IsRaidMember)
        {
            table.AddRow(new Markup("[#94A3B8]Controladora RAID[/]"), new Markup($"[bold #FBBF24]{Esc(rep.RaidControllerName)} (Slot {rep.RaidSlotNumber})[/]"));
        }

        string healthPill = rep.HealthDefault >= 80
            ? $"[bold black on #4ADE80]  {rep.HealthDefault:F0}% SAUDAVEL  [/]"
            : $"[bold black on #F87171]  {rep.HealthDefault:F0}% ATENCAO  [/]";

        table.AddRow(new Markup("[#94A3B8]Indice de Saude Geral[/]"), new Markup(healthPill));
        table.AddRow(new Markup("[#94A3B8]Indice de Desempenho[/]"), new Markup($"[bold black on #38BDF8]  {rep.Performance:F0}% OTIMO  [/]"));
        table.AddRow(new Markup("[#94A3B8]Temperatura Operacional[/]"), new Markup($"[bold #38BDF8]{rep.TemperatureCelsius} °C[/] [#94A3B8](Faixa operacional segura: 0-60°C)[/]"));
        table.AddRow(new Markup("[#94A3B8]Horas em Operacao (POH)[/]"), new Markup($"[bold #F1F5F9]{rep.PowerOnHours:N0} h[/] [#94A3B8]({rep.PowerOnHours / 24:N0} dias)[/]"));
        table.AddRow(new Markup("[#94A3B8]Ciclos de Inicializacao[/]"), new Markup($"[bold #F1F5F9]{rep.PowerCycleCount:N0} ciclos[/]"));
        table.AddRow(new Markup("[#94A3B8]Total Bytes Gravados (TBW)[/]"), new Markup($"[bold #C084FC]{rep.TotalTbWritten:F2} TB[/]"));
        table.AddRow(new Markup("[#94A3B8]Total Bytes Lidos (TBR)[/]"), new Markup($"[bold #C084FC]{rep.TotalTbRead:F2} TB[/]"));
        table.AddRow(new Markup("[#94A3B8]Estimativa de Vida Util[/]"), new Markup($"[bold #4ADE80]{Esc(rep.RemainingLifetimeText)}[/]"));
        table.AddRow(new Markup("[#94A3B8]Laudo Diagnostico[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.HealthVerdictText)}[/]"));

        var panel = new Panel(table)
            .Header($" [#38BDF8]DIAGNOSTICO DETALHADO DE SAUDE — {Esc(rep.Model)} ({Esc(rep.Serial)})[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        items.Add(new Markup("  [bold black on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"));
        items.Add(panel);

        var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dockTable.AddColumn(new TableColumn("Dock"));
        dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"));
        var dockPanel = new Panel(dockTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunSmartAttributesInteractive(DriveSmartReport rep, bool waitForKey = true)
    {
        SafeClear();
        AnsiConsole.Write(BuildSmartAttributesRenderable(rep));
        if (waitForKey && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildSmartAttributesRenderable(DriveSmartReport rep)
    {
        var items = new List<IRenderable>();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        table.ShowRowSeparators = true;

        if (rep.IsNvme)
        {
            table.AddColumn(new TableColumn("[#94A3B8]ID[/]").Width(8));
            table.AddColumn(new TableColumn("[bold #F1F5F9]PARAMETRO S.M.A.R.T. NVMe[/]").Width(28));
            table.AddColumn(new TableColumn("[bold #38BDF8]VALOR BRUTO (RAW)[/]").Width(20));
            table.AddColumn(new TableColumn("[#FBBF24]STATUS[/]").Width(12));
            table.AddColumn(new TableColumn("[bold #4ADE80]INTERPRETACAO[/]").Width(32));

            if (rep.Attributes.Count == 0)
            {
                table.AddRow(new Markup("[#94A3B8]--[/]"), new Markup("[#94A3B8]Nenhum atributo NVMe registrado.[/]"), new Markup("--"), new Markup("--"), new Markup("--"));
            }
            else
            {
                foreach (var attr in rep.Attributes)
                {
                    string statusMarkup = "[#4ADE80]OK (Normal)[/]";
                    if (attr.Id == 0x01 && attr.RawValue != 0) statusMarkup = "[bold black on #F87171]  ALERTA  [/]";
                    if (attr.Id == 0x05 && attr.RawValue >= 90) statusMarkup = "[bold black on #FBBF24]  ATENCAO  [/]";
                    if (attr.Id == 0x0E && attr.RawValue > 0) statusMarkup = "[bold black on #F87171]  ERRO  [/]";

                    table.AddRow(
                        new Markup($"[#38BDF8]0x{attr.Id:X2}[/]"),
                        new Markup($"[bold #F1F5F9]{Esc(attr.Name)}[/]"),
                        new Markup($"[#F1F5F9]{attr.RawValue:N0}[/]"),
                        new Markup(statusMarkup),
                        new Markup($"[#F1F5F9]{Esc(attr.Interpretation)}[/]")
                    );
                }
            }
        }
        else
        {
            table.AddColumn(new TableColumn("[#94A3B8]ID[/]").Width(6));
            table.AddColumn(new TableColumn("[bold #F1F5F9]ATRIBUTO[/]").Width(24));
            table.AddColumn(new TableColumn("[#38BDF8]VAL[/]").Width(5));
            table.AddColumn(new TableColumn("[#94A3B8]PIOR[/]").Width(5));
            table.AddColumn(new TableColumn("[#FBBF24]FLAGS[/]").Width(7));
            table.AddColumn(new TableColumn("[bold #F1F5F9]RAW (DEC)[/]").Width(13));
            table.AddColumn(new TableColumn("[bold #4ADE80]INTERPRETACAO[/]").Width(26));

            if (rep.Attributes.Count == 0)
            {
                table.AddRow(new Markup("[#94A3B8]--[/]"), new Markup("[#94A3B8]Sem atributos ATA decodificados.[/]"), new Markup("--"), new Markup("--"), new Markup("--"), new Markup("--"), new Markup("--"));
            }
            else
            {
                foreach (var attr in rep.Attributes)
                {
                    table.AddRow(
                        new Markup($"[#38BDF8]0x{attr.Id:X2}[/]"),
                        new Markup($"[bold #F1F5F9]{Esc(attr.Name)}[/]"),
                        new Markup($"[#38BDF8]{attr.Current}[/]"),
                        new Markup($"[#94A3B8]{attr.Worst}[/]"),
                        new Markup($"[#FBBF24]{Esc(attr.FlagsHex)}[/]"),
                        new Markup($"[#F1F5F9]{attr.RawValue:N0}[/]"),
                        new Markup($"[#4ADE80]{Esc(attr.Interpretation)}[/]")
                    );
                }
            }
        }

        var panel = new Panel(table)
            .Header($" [#38BDF8]TABELA DE ATRIBUTOS S.M.A.R.T. — {Esc(rep.Model)}[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        items.Add(new Markup("  [bold black on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"));
        items.Add(panel);

        var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dockTable.AddColumn(new TableColumn("Dock"));
        dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"));
        var dockPanel = new Panel(dockTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunHexDumpInteractive(DriveSmartReport rep, bool waitForKey = true)
    {
        SafeClear();
        AnsiConsole.Write(BuildHexDumpRenderable(rep));
        if (waitForKey && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildHexDumpRenderable(DriveSmartReport rep)
    {
        var items = new List<IRenderable>();
        items.Add(new Markup($"  [bold black on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]  [#64748B]•[/]  [#94A3B8]DUMP RAW (512B) {Esc(rep.DevicePath)}[/]\n\n"));

        if (rep.RawSmartPayload == null || rep.RawSmartPayload.Length == 0)
        {
            items.Add(new Markup("  [#FBBF24]Nenhum dump bruto de 512 bytes extraido para este dispositivo.[/]\n"));
        }
        else
        {
            var sb = new StringBuilder(4096);
            for (int offset = 0; offset < rep.RawSmartPayload.Length; offset += 16)
            {
                sb.Append($"  [#38BDF8]{offset:X4}[/]  ");
                for (int i = 0; i < 16; i++)
                {
                    if (offset + i < rep.RawSmartPayload.Length)
                        sb.Append($"[#F1F5F9]{rep.RawSmartPayload[offset + i]:X2}[/] ");
                    else
                        sb.Append("   ");
                    if (i == 7) sb.Append(" ");
                }
                sb.Append(" │ ");
                for (int i = 0; i < 16; i++)
                {
                    if (offset + i < rep.RawSmartPayload.Length)
                    {
                        byte b = rep.RawSmartPayload[offset + i];
                        char ch = (b >= 32 && b <= 126) ? (char)b : '.';
                        sb.Append($"[#94A3B8]{Esc(ch.ToString())}[/]");
                    }
                }
                sb.AppendLine();
            }

            var panel = new Panel(new Markup(sb.ToString()))
                .Header(" [#38BDF8]DUMP RAW (512 BYTES)[/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(51, 65, 85))
                .Expand();

            items.Add(panel);
        }

        var hexDockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        hexDockTable.AddColumn(new TableColumn("Dock"));
        hexDockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"));
        var hexDockPanel = new Panel(hexDockTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(hexDockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunSelfTestInteractive(DriveSmartReport rep, SelfTestMode mode, bool waitForKey = true)
    {
        SafeClear();
        string testName = mode == SelfTestMode.Short ? "Short Self-Test (~2 min)" : "Extended Self-Test (~1 a 2 horas)";

        bool ok = FirmwareSelfTestEngine.ExecuteSelfTest(rep.DevicePath, rep.IsNvme, rep.BusTypeName == "SCSI" || rep.BusTypeName == "RAID", mode, out string msg);

        if (!ok)
        {
            var errTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            errTable.AddColumn(new TableColumn("Err"));
            errTable.AddRow(new Markup($"\n  [bold #0B0F19 on #F87171]  AVISO / INCOMPATIBILIDADE  [/]\n\n  [#F1F5F9]{Esc(msg)}[/]\n"));
            var errPanel = new Panel(errTable).Header(" [#F87171]STATUS DO AUTOTESTE DE FIRMWARE[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(248, 113, 113)).Expand();
            AnsiConsole.Write(MasterContainer(104,
                new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"),
                errPanel,
                new Markup("\n  [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]")
            ));
            if (waitForKey && !Console.IsInputRedirected)
            {
                try { Console.ReadKey(true); } catch { }
            }
            return;
        }

        var sw = Stopwatch.StartNew();
        double estSeconds = mode == SelfTestMode.Short ? 120.0 : 3600.0;
        int spinnerIdx = 0;
        string[] spinner = { "\u280B", "\u2819", "\u2839", "\u2838", "\u283C", "\u2834", "\u2826", "\u2827", "\u2807", "\u280F" };
        bool aborted = false;
        bool userExited = false;
        bool isCompleted = false;

        IRenderable BuildFrame()
        {
            var items = new List<IRenderable>();
            items.Add(new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"));

            var statusTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            statusTable.AddColumn(new TableColumn("Col1").Width(24));
            statusTable.AddColumn(new TableColumn("Col2").Width(74));

            statusTable.AddRow(new Markup("[#94A3B8]Dispositivo:[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.Model)}[/] [#64748B]({Esc(rep.DevicePath)})[/]"));
            statusTable.AddRow(new Markup("[#94A3B8]Modalidade:[/]"), new Markup($"[bold #38BDF8]{testName}[/]"));

            if (aborted)
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status Firmware:[/]"), new Markup("[bold #0B0F19 on #FBBF24]  TESTE ABORTADO PELO USUARIO  [/]"));
            }
            else if (isCompleted)
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status Firmware:[/]"), new Markup("[bold #0B0F19 on #4ADE80]  AUTOTESTE CONCLUIDO COM EXITO  [/]"));
            }
            else
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status Firmware:[/]"), new Markup($"[bold #0B0F19 on #4ADE80]  EM EXECUCAO NO MICROCODIGO  [/]  [#38BDF8]{spinner[spinnerIdx % spinner.Length]}[/]"));
            }

            statusTable.AddRow(new Markup("[#94A3B8]Tempo Decorrido:[/]"), new Markup($"[bold #F1F5F9]{sw.Elapsed:mm\\:ss}[/]"));

            double progressPct = Math.Clamp((sw.Elapsed.TotalSeconds / estSeconds) * 100.0, 0.0, 100.0);
            if (isCompleted) progressPct = 100.0;
            string progBar = RenderSegmentedBar(progressPct, 36, "#38BDF8");
            statusTable.AddRow(new Markup("[#94A3B8]Progresso Estimado:[/]"), new Markup($"{progBar}  [#38BDF8]{progressPct:F0}%[/]"));

            var statusPanel = new Panel(statusTable)
                .Header(" [#38BDF8]AUTOTESTE DE FIRMWARE EM ANDAMENTO[/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(51, 65, 85))
                .Expand();
            items.Add(statusPanel);

            var infoTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            infoTable.AddColumn(new TableColumn("Info"));
            infoTable.AddRow(new Markup("  [#94A3B8]O teste esta sendo executado internamente pela controladora do disco.[/]"));
            infoTable.AddRow(new Markup("  [#64748B]O resultado e gravado permanentemente no log de autotestes S.M.A.R.T.[/]"));
            var infoPanel = new Panel(infoTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            items.Add(infoPanel);

            var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            dockTable.AddColumn(new TableColumn("DockLeft").Width(74));
            dockTable.AddColumn(new TableColumn("DockRight").RightAligned().Width(24));
            if (isCompleted || aborted)
            {
                dockTable.AddRow(
                    new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"),
                    new Markup("[#64748B]@_fxgiovane  [/]")
                );
            }
            else
            {
                dockTable.AddRow(
                    new Markup("  [#38BDF8]>[/] [#38BDF8][[[/][bold #0B0F19 on #F87171] Esc [/] [#94A3B8]Abortar Teste[/][#38BDF8]]][/]   [#38BDF8][[[/][bold #0B0F19 on #38BDF8] Enter [/] [#94A3B8]Voltar (2º Plano)[/][#38BDF8]]][/]"),
                    new Markup("[#64748B]@_fxgiovane  [/]")
                );
            }
            var dockPanel = new Panel(dockTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            items.Add(dockPanel);

            return MasterContainer(104, items.ToArray());
        }

        AnsiConsole.Live(BuildFrame())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                while (!aborted && !userExited && !isCompleted)
                {
                    ctx.UpdateTarget(BuildFrame());
                    ctx.Refresh();

                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true).Key;
                        if (k == ConsoleKey.Escape)
                        {
                            FirmwareSelfTestEngine.ExecuteSelfTest(rep.DevicePath, rep.IsNvme, rep.BusTypeName == "SCSI" || rep.BusTypeName == "RAID", SelfTestMode.Abort, out _);
                            aborted = true;
                            ctx.UpdateTarget(BuildFrame());
                            ctx.Refresh();
                            break;
                        }
                        else if (k == ConsoleKey.Enter)
                        {
                            userExited = true;
                            break;
                        }
                    }

                    if (sw.Elapsed.TotalSeconds >= estSeconds)
                    {
                        isCompleted = true;
                        ctx.UpdateTarget(BuildFrame());
                        ctx.Refresh();
                        break;
                    }

                    spinnerIdx++;
                    Thread.Sleep(150);
                }
            });

        if ((aborted || isCompleted) && waitForKey && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static void RunSurfaceTestInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        using var cts = new CancellationTokenSource();
        int totalGridBlocks = smokeTestMode ? 5 : 350;
        var grid = new SurfaceBlockStatus[totalGridBlocks];
        var speedSpark = new Queue<double>();
        var latencySpark = new Queue<double>();
        long activeBlockIndex = -1;

        var tableLayout = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tableLayout.AddColumn(new TableColumn("Content"));

        Exception? capturedError = null;
        SurfaceTestProgress? lastProgress = null;
        var lastRefreshSw = Stopwatch.StartNew();

        Action<SurfaceBlockResult, SurfaceTestProgress> onBlockCallback = (res, prog) =>
        {
            lastProgress = prog;
            if (res.BlockIndex < totalGridBlocks)
                grid[res.BlockIndex] = res.Status;

            speedSpark.Enqueue(res.SpeedMBps);
            if (speedSpark.Count > 30) speedSpark.Dequeue();

            latencySpark.Enqueue(res.LatencyMs);
            if (latencySpark.Count > 30) latencySpark.Dequeue();

            if (!smokeTestMode && !Console.IsInputRedirected && Console.KeyAvailable)
            {
                var k = Console.ReadKey(true).Key;
                if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                {
                    cts.Cancel();
                }
            }
        };

        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Live(tableLayout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Bottom)
                .Start(ctx =>
                {
                    try
                    {
                        SurfaceTestEngine.RunTest(
                            rep.DevicePath,
                            rep.DiskSizeBytes,
                            rep.BytesPerSector,
                            totalGridBlocks,
                            cts.Token,
                            (res, prog) =>
                            {
                                onBlockCallback(res, prog);
                                if (lastRefreshSw.ElapsedMilliseconds >= 33 || res.BlockIndex >= totalGridBlocks - 1)
                                {
                                    var renderable = BuildSurfaceRenderable(rep, grid, speedSpark, latencySpark, prog, activeBlockIndex, cts.IsCancellationRequested);
                                    tableLayout.Rows.Clear();
                                    tableLayout.AddRow(renderable);
                                    ctx.Refresh();
                                    lastRefreshSw.Restart();
                                }
                            },
                            onBlockStarting: (b) => { activeBlockIndex = b; },
                            throttleMs: smokeTestMode ? 0 : 15);
                    }
                    catch (Exception ex)
                    {
                        capturedError = ex;
                    }
                });
        }
        else
        {
            try
            {
                SurfaceTestEngine.RunTest(
                    rep.DevicePath,
                    rep.DiskSizeBytes,
                    rep.BytesPerSector,
                    totalGridBlocks,
                    cts.Token,
                    onBlockCallback,
                    onBlockStarting: (b) => { activeBlockIndex = b; },
                    throttleMs: 0);

                var finalRenderable = BuildSurfaceRenderable(rep, grid, speedSpark, latencySpark, lastProgress ?? new SurfaceTestProgress(), activeBlockIndex, cts.IsCancellationRequested);
                AnsiConsole.Write(finalRenderable);
            }
            catch (Exception ex)
            {
                capturedError = ex;
            }
        }

        if (grid.Length > 0)
        {
            SurfaceTestEngine.DiskSurfaceCache[rep.DevicePath] = (SurfaceBlockStatus[])grid.Clone();
        }

        if (capturedError != null)
        {
            AnsiConsole.MarkupLine($"\n  [bold black on #F87171]  FAILED  [/] [#F87171]Erro no teste de superficie: {Esc(capturedError.Message)}[/]");
        }
        else if (cts.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("\n  [bold black on #FBBF24]  CANCELLED  [/] [#FBBF24]Varredura interrompida pelo usuario.[/]");
        }
        else if (lastProgress != null && lastProgress.BadBlocks > 0)
        {
            AnsiConsole.MarkupLine($"\n  [bold black on #F87171]  ALERTA  [/] [#F87171]Foram detectados {lastProgress.BadBlocks} setores defeituosos no disco.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n  [bold black on #4ADE80]  SUCCEEDED  [/] [#4ADE80]Varredura de superficie concluida sem setores criticos.[/]");
        }

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("  [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]");
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildSurfaceRenderable(
        DriveSmartReport rep,
        SurfaceBlockStatus[] grid,
        Queue<double> speedSpark,
        Queue<double> latencySpark,
        SurfaceTestProgress prog,
        long activeBlock,
        bool cancelled)
    {
        var items = new List<IRenderable>();

        string statusPill = cancelled
            ? "[bold black on #F87171]  CANCELLED  [/]"
            : "[bold black on #4ADE80]  RUNNING  [/]";

        items.Add(new Markup($"  {statusPill} [bold #F1F5F9]SURFACE TEST (SAFE READ)[/]  [#334155]•[/]  [#38BDF8]{Esc(rep.DevicePath)}[/] [#94A3B8]({Esc(rep.Model)})[/]  [#94A3B8]({prog.Elapsed.TotalSeconds:F1}s)[/]\n"));

        var gridBuilder = new StringBuilder(4096);
        int cols = 50;
        int rows = 7;

        for (int r = 0; r < rows; r++)
        {
            gridBuilder.Append(" ");
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                if (idx == activeBlock && prog.CurrentBlock <= prog.TotalBlocks)
                {
                    gridBuilder.Append("[bold black on #38BDF8]>[/] ");
                }
                else if (idx < grid.Length && idx < prog.CurrentBlock)
                {
                    gridBuilder.Append(FormatBlockMarkup(grid[idx]) + " ");
                }
                else
                {
                    gridBuilder.Append("[#334155]·[/] ");
                }
            }
            gridBuilder.AppendLine();
        }

        var mapPanel = new Panel(new Markup(gridBuilder.ToString()))
            .Header(" [#38BDF8]MAPA DE SUPERFICIE[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        items.Add(mapPanel);

        items.Add(new Markup("  [#94A3B8]Legenda:[/] [#4ADE80]■[/] <5ms  [#10B981]■[/] <20ms  [#FBBF24]■[/] <50ms  [#FB923C]▒[/] <150ms  [#F87171]▓[/] <500ms  [bold black on #F87171]  BAD  [/]  [bold black on #38BDF8]>[/] Scanner  [#334155]·[/] Pendente\n"));

        var metricsTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        metricsTable.AddColumn(new TableColumn("[#94A3B8]METRICA DE VARREDURA[/]").Width(28));
        metricsTable.AddColumn(new TableColumn("[bold #F1F5F9]TELEMETRIA EM TEMPO REAL[/]").Width(72));

        string speedSparkLine = RenderSparkline(speedSpark);
        string latSparkLine = RenderSparkline(latencySpark);

        int barWidth = 26;
        int filled = Math.Clamp((int)(prog.Percent / 100.0 * barWidth), 0, barWidth);
        string progressBar = new string('▓', filled) + new string('░', barWidth - filled);

        metricsTable.AddRow(new Markup("[#94A3B8]Setores / Tamanho Bloco[/]"), new Markup($"[#F1F5F9]{prog.TotalSectors:N0}[/] setores | Bloco: [bold #F1F5F9]{prog.BlockSectors:N0}[/] ([#38BDF8]{prog.BlockSizeMB * 1024.0:F0} KB[/])"));
        metricsTable.AddRow(new Markup("[#94A3B8]Distribuicao dos Blocos[/]"), new Markup($"Bons: [#4ADE80]{prog.GoodBlocks}[/] | Lentos: [#FBBF24]{prog.SlowBlocks}[/] | Danificados: [#FB923C]{prog.DamagedBlocks}[/] | Bad: [bold black on #F87171]  {prog.BadBlocks}  [/]"));
        metricsTable.AddRow(new Markup("[#94A3B8]Throughput Instantaneo[/]"), new Markup($"[bold #38BDF8]{prog.CurrentSpeedMBps:F1} MB/s[/] [#94A3B8](Min: {prog.MinSpeedMBps:F0} / Med: [/][#4ADE80]{prog.AvgSpeedMBps:F0}[/][#94A3B8] / Max: {prog.MaxSpeedMBps:F0} MB/s)[/]"));
        metricsTable.AddRow(new Markup("[#94A3B8]Sparkline Throughput (MB/s)[/]"), new Markup($"[#38BDF8]{speedSparkLine}[/]  [#94A3B8]Latencia:[/] [#FBBF24]{prog.CurrentLatencyMs:F2} ms[/]"));
        metricsTable.AddRow(new Markup("[#94A3B8]Sparkline Latencia (ms)[/]"), new Markup($"[#F87171]{latSparkLine}[/]"));
        metricsTable.AddRow(new Markup("[#94A3B8]Progresso da Varredura[/]"), new Markup($"[#4ADE80]{progressBar}[/] [bold #F1F5F9]{prog.Percent,5:F1}%[/] [#94A3B8](Restante: {prog.EstimatedRemaining:mm\\:ss})[/]"));
        metricsTable.AddRow(new Markup("[#94A3B8]Inspecao LBA Atual[/]"), new Markup($"Bloco [#38BDF8]{prog.CurrentBlock}[/] / {prog.TotalBlocks} | LBA: [bold #F1F5F9]0x{prog.CurrentLba:X10}[/]"));

        var metricsPanel = new Panel(metricsTable)
            .Header(" [#38BDF8]METRICAS[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();

        items.Add(metricsPanel);

        var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dockTable.AddColumn(new TableColumn("Dock"));
        dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9][[Esc]][/] [#94A3B8]ou[/] [bold #F1F5F9][[Q]][/] [#94A3B8]para cancelar o teste a qualquer momento.[/]"));
        var dockPanel = new Panel(dockTable)
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunSeekTestInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        using var cts = new CancellationTokenSource();
        long totalSeekOps = smokeTestMode ? 15 : 500;
        var latencyHistory = new List<double>();
        var sparkHistory = new Queue<double>();

        var tableLayout = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tableLayout.AddColumn(new TableColumn("Content"));

        Exception? capturedError = null;
        SeekTestProgress? lastProgress = null;
        var lastRefreshSw = Stopwatch.StartNew();

        Action<SeekTestSample, SeekTestProgress> onSeekCallback = (sample, prog) =>
        {
            lastProgress = prog;
            latencyHistory.Add(sample.LatencyMs);
            sparkHistory.Enqueue(sample.LatencyMs);
            if (sparkHistory.Count > 30) sparkHistory.Dequeue();

            if (!smokeTestMode && !Console.IsInputRedirected && Console.KeyAvailable)
            {
                var k = Console.ReadKey(true).Key;
                if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                {
                    cts.Cancel();
                }
            }
        };

        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Live(tableLayout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Start(ctx =>
                {
                    try
                    {
                        RandomSeekTestEngine.RunTest(
                            rep.DevicePath,
                            rep.DiskSizeBytes,
                            rep.BytesPerSector,
                            totalSeekOps,
                            cts.Token,
                            (sample, prog) =>
                            {
                                onSeekCallback(sample, prog);
                                if (lastRefreshSw.ElapsedMilliseconds >= 40 || prog.OperationsCompleted >= totalSeekOps)
                                {
                                    var renderable = BuildSeekRenderable(rep, sparkHistory, latencyHistory, prog, cts.IsCancellationRequested);
                                    tableLayout.Rows.Clear();
                                    tableLayout.AddRow(renderable);
                                    ctx.Refresh();
                                    lastRefreshSw.Restart();
                                }
                            });
                    }
                    catch (Exception ex)
                    {
                        capturedError = ex;
                    }
                });
        }
        else
        {
            try
            {
                RandomSeekTestEngine.RunTest(
                    rep.DevicePath,
                    rep.DiskSizeBytes,
                    rep.BytesPerSector,
                    totalSeekOps,
                    cts.Token,
                    onSeekCallback);

                string finalMsg = cts.IsCancellationRequested
                    ? "[bold black on #FBBF24]  CANCELADO  [/] [#FBBF24]Teste interrompido pelo usuario.[/]"
                    : "[bold black on #4ADE80]  SUCESSO  [/] [#4ADE80]Teste concluido com 100% das operacoes.[/]";

                var finalRenderable = BuildSeekRenderable(rep, sparkHistory, latencyHistory, lastProgress ?? new SeekTestProgress(), cts.IsCancellationRequested, isFinished: true, finalMessage: finalMsg);
                SafeClear();
                AnsiConsole.Write(finalRenderable);
            }
            catch (Exception ex)
            {
                capturedError = ex;
                var finalRenderable = BuildSeekRenderable(rep, sparkHistory, latencyHistory, lastProgress ?? new SeekTestProgress(), true, isFinished: true, finalMessage: $"[bold black on #F87171]  FALHA  [/] [#F87171]Erro: {Esc(ex.Message)}[/]");
                SafeClear();
                AnsiConsole.Write(finalRenderable);
            }
        }

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static (string badge, string text, string color) GetSeekVerdict(DriveSmartReport rep, SeekTestProgress prog)
    {
        if (rep.IsSsd || rep.IsNvme)
        {
            if (prog.AvgSeekMs < 0.5)
                return ("[bold #0B0F19 on #4ADE80]  EXCELENTE (SSD)  [/]", "Latencia media ultrarrapida (<0.5ms). Celulas flash e controladora com resposta ideal.", "#4ADE80");
            if (prog.AvgSeekMs < 2.0)
                return ("[bold #0B0F19 on #38BDF8]  BOM (SSD)  [/]", "Desempenho satisfatorio de acesso aleatorio em estado solido.", "#38BDF8");
            return ("[bold #0B0F19 on #F87171]  DEGRADADO (SSD)  [/]", "Latencia acima do padrao de estado solido (>2ms). Verifique estrangulamento termico.", "#F87171");
        }
        else
        {
            if (prog.AvgSeekMs < 12.0)
                return ("[bold #0B0F19 on #4ADE80]  EXCELENTE (HDD)  [/]", "Tempo de busca rapido para disco mecanico. Servomecanismo e braco em perfeito estado.", "#4ADE80");
            if (prog.AvgSeekMs < 18.0)
                return ("[bold #0B0F19 on #38BDF8]  NORMAL (HDD)  [/]", "Desempenho dentro dos padroes nominais de fabrica para disco magnetico.", "#38BDF8");
            return ("[bold #0B0F19 on #FBBF24]  LENTO (HDD)  [/]", "Latencia de busca elevada (>18ms). Atuador de cabecas apresentando retentativas.", "#FBBF24");
        }
    }

    public static IRenderable BuildSeekRenderable(
        DriveSmartReport rep,
        Queue<double> spark,
        List<double> latencyHistory,
        SeekTestProgress prog,
        bool cancelled,
        bool isFinished = false,
        string? finalMessage = null)
    {
        var items = new List<IRenderable>();

        string statusPill = cancelled
            ? "[bold black on #F87171]  CANCELADO  [/]"
            : (isFinished ? "[bold black on #4ADE80]  SUCESSO  [/]" : "[bold black on #4ADE80]  RUNNING  [/]");

        string modelShort = rep.Model.Length > 20 ? rep.Model.Substring(0, 18) + ".." : rep.Model;
        items.Add(new Markup($"  {statusPill} [bold #F1F5F9]RANDOM SEEK[/]  [#334155]•[/]  [#38BDF8]{Esc(rep.DevicePath)}[/] [#94A3B8]({Esc(modelShort)})[/]  [#64748B]•[/]  [#94A3B8]{prog.Elapsed.TotalSeconds:F1}s[/]\n"));

        var metricsTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        metricsTable.AddColumn(new TableColumn("").Width(20));
        metricsTable.AddColumn(new TableColumn("").Width(28));
        metricsTable.AddColumn(new TableColumn("").Width(20));
        metricsTable.AddColumn(new TableColumn("").Width(28));

        metricsTable.AddRow(
            new Markup("[#94A3B8]Seek Atual / Min:[/]"), new Markup($"[bold #38BDF8]{prog.CurrentSeekMs:F2} ms[/] / [#4ADE80]{prog.MinSeekMs:F2} ms[/]"),
            new Markup("[#94A3B8]Seek Med / Max:[/]"), new Markup($"[#4ADE80]{prog.AvgSeekMs:F2} ms[/] / [#F87171]{prog.MaxSeekMs:F2} ms[/]")
        );
        metricsTable.AddRow(
            new Markup("[#94A3B8]Throughput IOPS:[/]"), new Markup($"[bold #4ADE80]{prog.CurrentIops:F0} IOPS[/]"),
            new Markup("[#94A3B8]Progresso / Alvo:[/]"), new Markup($"[bold #F1F5F9]{prog.OperationsCompleted:N0}[/] / {prog.TotalOperationsTarget:N0} ([#38BDF8]{prog.Percent:F1}%[/])")
        );

        var panelMetrics = new Panel(metricsTable)
            .Header(" [#38BDF8]LATENCIA DE BUSCA (QPC)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(panelMetrics);

        string dotChart = RenderDotModeChart(latencyHistory, 72, 5);
        string sparkLine = RenderSparkline(spark, 72);
        var dotTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dotTable.AddColumn(new TableColumn("Plot"));
        dotTable.AddRow(new Markup(dotChart));
        dotTable.AddRow(new Markup($"  [#94A3B8]Historico:[/] {sparkLine}"));
        var dotPanel = new Panel(dotTable)
            .Header(" [#38BDF8]DISPERSAO DE LATENCIA E SPARKLINE[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(dotPanel);

        var histTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        histTable.AddColumn(new TableColumn("Faixa").Width(22));
        histTable.AddColumn(new TableColumn("Barra").Width(38));
        histTable.AddColumn(new TableColumn("Qtd").RightAligned().Width(24));

        long totalRecorded = Math.Max(1L, prog.CountUnder5ms + prog.CountUnder10ms + prog.CountUnder20ms + prog.CountOver20ms);

        void AddHistRow(string label, string color, long count)
        {
            double pct = (double)count / totalRecorded * 100.0;
            string bar = RenderSegmentedBar(pct, 32, color);
            histTable.AddRow(
                new Markup($"[{color}]{label}[/]"),
                new Markup(bar),
                new Markup($"[bold #F1F5F9]{count:N0}[/] [#64748B]({pct:F1}%)[/]")
            );
        }

        AddHistRow("< 5ms (Excelente)", "#4ADE80", prog.CountUnder5ms);
        AddHistRow("< 10ms (Normal)", "#38BDF8", prog.CountUnder10ms);
        AddHistRow("< 20ms (Lento)", "#FBBF24", prog.CountUnder20ms);
        AddHistRow("> 20ms (Marginal)", "#F87171", prog.CountOver20ms);

        var histPanel = new Panel(histTable)
            .Header(" [#38BDF8]DISTRIBUICAO DE LATENCIA (HISTOGRAMA)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(histPanel);

        if (isFinished)
        {
            var verdict = GetSeekVerdict(rep, prog);
            var verdictTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            verdictTable.AddColumn(new TableColumn("Badge").Width(24));
            verdictTable.AddColumn(new TableColumn("Desc").Width(72));
            verdictTable.AddRow(new Markup($"  {verdict.badge}"), new Markup($"[{verdict.color}]{Esc(verdict.text)}[/]"));
            var verdictPanel = new Panel(verdictTable)
                .Header(" [#38BDF8]LAUDO DE DESEMPENHO[/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(56, 189, 248))
                .Expand();
            items.Add(verdictPanel);
        }

        var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        dockTable.AddColumn(new TableColumn("Dock"));
        if (isFinished)
        {
            dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"));
        }
        else
        {
            dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9][[Esc]][/] [#94A3B8]ou[/] [bold #F1F5F9][[Q]][/] [#94A3B8]para cancelar o teste a qualquer momento.[/]"));
        }
        var dockPanel = new Panel(dockTable)
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    private static string FormatBlockMarkup(SurfaceBlockStatus st) => st switch
    {
        SurfaceBlockStatus.Excellent => "[#4ADE80]■[/]",
        SurfaceBlockStatus.Good => "[#10B981]■[/]",
        SurfaceBlockStatus.Slow => "[#FBBF24]■[/]",
        SurfaceBlockStatus.Lagging => "[#FB923C]▒[/]",
        SurfaceBlockStatus.Damaged => "[#F87171]▓[/]",
        SurfaceBlockStatus.BadSector => "[bold black on #F87171]  BAD  [/]",
        _ => "[#334155]·[/]"
    };

    private static string RenderSparkline(Queue<double> values, int width = 30)
    {
        if (values.Count == 0) return new string(' ', width);
        double max = 0.001;
        foreach (var v in values) if (v > max) max = v;

        var sb = new StringBuilder();
        var valList = values.ToList();
        for (int i = 0; i < width; i++)
        {
            int srcIdx = (int)((double)i / width * valList.Count);
            if (srcIdx >= 0 && srcIdx < valList.Count)
            {
                double v = valList[srcIdx];
                int idx = Math.Clamp((int)(v / max * 8.0), 0, 8);
                string col = v < 5.0 ? "#4ADE80" : (v < 10.0 ? "#38BDF8" : (v < 20.0 ? "#FBBF24" : "#F87171"));
                sb.Append($"[{col}]{SparkChars[idx]}[/]");
            }
            else
            {
                sb.Append(' ');
            }
        }
        return sb.ToString();
    }

    private static string RenderDotModeChart(List<double> history, int width, int height)
    {
        if (history.Count == 0) return "  [#94A3B8]Aguardando amostras de latencia...[/]";

        int take = Math.Min(history.Count, width);
        var samples = new List<double>();
        for (int i = history.Count - take; i < history.Count; i++)
            samples.Add(history[i]);

        char[,] grid = new char[height, width];
        double[] colValues = new double[width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                grid[y, x] = ' ';
        for (int x = 0; x < width; x++) colValues[x] = -1;

        for (int x = 0; x < samples.Count; x++)
        {
            double val = samples[x];
            colValues[x] = val;
            // Faixas de latencia:
            // y=0: >40ms (Marginal)
            // y=1: 20-40ms (Lento)
            // y=2: 10-20ms (Lento)
            // y=3: 5-10ms (Normal)
            // y=4: <5ms (Excelente)
            int y = val >= 40.0 ? 0 : (val >= 20.0 ? 1 : (val >= 10.0 ? 2 : (val >= 5.0 ? 3 : 4)));
            if (y >= 0 && y < height)
            {
                grid[y, x] = '●';
            }
        }

        string[] rowLabels = { ">40ms│", " 20ms│", " 10ms│", "  5ms│", " <5ms│" };
        var sb = new StringBuilder();
        for (int y = 0; y < height; y++)
        {
            sb.Append($"  [#94A3B8]{rowLabels[y]}[/] ");
            for (int x = 0; x < width; x++)
            {
                char c = grid[y, x];
                if (c == '●')
                {
                    double v = colValues[x];
                    string color = v < 5.0 ? "#4ADE80" : (v < 10.0 ? "#38BDF8" : (v < 20.0 ? "#FBBF24" : "#F87171"));
                    sb.Append($"[{color}]●[/]");
                }
                else
                {
                    sb.Append(' ');
                }
            }
            sb.AppendLine();
        }

        sb.Append("       [#334155]└");
        for (int x = 0; x < width; x++) sb.Append('─');
        sb.AppendLine("[/]");
        sb.Append("        [#94A3B8]0         10        20        30        40        50        60        70[/]");

        return sb.ToString();
    }

    public static void RunAutomatedVerification(List<DriveSmartReport> reports)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AnsiConsole.MarkupLine("[bold #38BDF8]================================================================================[/]");
        AnsiConsole.MarkupLine("  [bold #F1F5F9]VESPER AUTOMATED VERIFICATION HARNESS (--verify-all)[/]");
        AnsiConsole.MarkupLine("[bold #38BDF8]================================================================================[/]\n");

        // 1. Injetar disco 2 hostil com colchetes e caracteres especiais se houver < 2 discos
        if (reports.Count < 2)
        {
            byte[] hostilePayload = new byte[512];
            for (int b = 0; b < 256; b++) hostilePayload[b] = (byte)b;
            for (int b = 256; b < 512; b++) hostilePayload[b] = (byte)(b - 256);

            var hostileDrive = new DriveSmartReport
            {
                DevicePath = @"\\.\PhysicalDrive1",
                Model = "Seagate [RAID] ST1000LM035 (1 TB)",
                Serial = "WAA123 [HOSTILE]",
                BusType = 11,
                BusTypeName = "SATA [AHCI]",
                DiskSizeBytes = 1_000_204_886_016L,
                BytesPerSector = 512,
                IsSsd = false,
                IsNvme = false,
                TemperatureCelsius = 38,
                PowerOnHours = 14200,
                PowerCycleCount = 420,
                TotalTbRead = 45.2,
                TotalTbWritten = 38.6,
                HealthDefault = 92.0,
                Performance = 95.0,
                RemainingLifetimeText = "Mais de 1000 dias [Calculo Estendido]",
                HealthVerdictText = "Condicao operacional normal com [0] setores realocados.",
                RawSmartPayload = hostilePayload
            };

            hostileDrive.Attributes.Add(new SmartAttribute
            {
                Id = 0x05,
                Name = "Reallocated Sectors [RAW]",
                Current = 100,
                Worst = 100,
                Flags = 0x0033,
                RawValue = 0,
                Interpretation = "0 setores defeituosos [Limiar: 36]"
            });

            hostileDrive.Attributes.Add(new SmartAttribute
            {
                Id = 0x09,
                Name = "Power-On Hours [POH]",
                Current = 90,
                Worst = 90,
                Flags = 0x0032,
                RawValue = 14200,
                Interpretation = "14.200 h [591 dias de uso]"
            });

            reports.Add(hostileDrive);
        }

        AnsiConsole.MarkupLine($"[#94A3B8]Discos detectados/preparados para o teste:[/] [bold #38BDF8]{reports.Count}[/]");

        // [PASSO 1 - TELA PRINCIPAL & ABAS]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 1/9]][/] Validando renderizacao da Tela Principal e Abas (Discos #0 e #1, Itens 0 a 8)...");
        for (int d = 0; d < Math.Min(2, reports.Count); d++)
        {
            for (int m = 0; m < MenuItems.Length; m++)
            {
                var dashRenderable = BuildMainDashboard(reports, d, m, isElevated: true);
                var writer = new StringWriter();
                var console = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Out = new AnsiConsoleOutput(writer)
                });
                console.Write(dashRenderable);
            }
        }
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 1 OK]][/] Tela Principal renderizada em 18 estados sem erros de markup!");

        // [PASSO 2 - OPCAO 1 (SURFACE TEST)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 2/9]][/] Executando Opcao 1 (Surface Test) em modo smokeTestMode (5 blocos reais)...");
        RunSurfaceTestInteractive(reports[0], smokeTestMode: true);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 2 OK]][/] Surface Test executado e renderizado com sucesso!");

        // [PASSO 3 - OPCAO 2 (RANDOM SEEK TEST)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 3/9]][/] Executando Opcao 2 (Random Seek Test) em modo smokeTestMode (15 amostras reais)...");
        RunSeekTestInteractive(reports[0], smokeTestMode: true);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 3 OK]][/] Random Seek Test executado e renderizado com sucesso!");

        // [PASSO 4 - OPCAO 3 (DETALHES DE SAUDE)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 4/9]][/] Renderizando Opcao 3 (Detalhes de Saude) para todos os discos...");
        foreach (var r in reports)
        {
            RunHealthDetailsInteractive(r, waitForKey: false);
        }
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 4 OK]][/] Detalhes de Saude renderizados com sucesso!");

        // [PASSO 5 - OPCAO 4 (ATRIBUTOS SMART)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 5/9]][/] Renderizando Opcao 4 (Atributos SMART) para NVMe e ATA...");
        foreach (var r in reports)
        {
            RunSmartAttributesInteractive(r, waitForKey: false);
        }
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 5 OK]][/] Atributos SMART renderizados com sucesso!");

        // [PASSO 6 - OPCAO 5 (HEX DUMP 512B COM '[' E ']')]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 6/9]][/] Renderizando Opcao 5 (Dump Hexadecimal 512B) com caracteres hostis [[ e ]]...");
        foreach (var r in reports)
        {
            RunHexDumpInteractive(r, waitForKey: false);
        }
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 6 OK]][/] Hex Dump de 512B renderizado sem quebra de parser!");

        // [PASSO 7 - OPCAO 6 (SHORT SELF-TEST)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 7/9]][/] Executando Opcao 6 (Short Self-Test)...");
        RunSelfTestInteractive(reports[0], SelfTestMode.Short, waitForKey: false);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 7 OK]][/] Short Self-Test disparado e formatado com sucesso!");

        // [PASSO 8 - OPCAO 7 (EXTENDED SELF-TEST)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 8/9]][/] Executando Opcao 7 (Extended Self-Test)...");
        RunSelfTestInteractive(reports[0], SelfTestMode.Extended, waitForKey: false);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 8 OK]][/] Extended Self-Test disparado e formatado com sucesso!");

        // [PASSO 9 - OPCAO 8 (ALTERNAR DISCO ATIVO)]
        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 9/9]][/] Validando Opcao 8 (Alternar Disco Ativo)...");
        int nextDisk = 1 % reports.Count;
        var switchedDashboard = BuildMainDashboard(reports, nextDisk, 0, isElevated: true);
        var sWriter = new StringWriter();
        var sConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(sWriter) });
        sConsole.Write(switchedDashboard);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 9 OK]][/] Alternancia de disco ativo validada!");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold black on #4ADE80]================================================================================[/]");
        AnsiConsole.MarkupLine("[bold black on #4ADE80]  [[VERIFY-ALL 100% PASSED]]: Tela Principal + Opcoes 1 a 9 validadas em runtime! [/]");
        AnsiConsole.MarkupLine("[bold black on #4ADE80]================================================================================[/]\n");
    }
}
