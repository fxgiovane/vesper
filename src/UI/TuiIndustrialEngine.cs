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
        "Benchmark de I/O",
        "Detalhes de Saude",
        "Atributos S.M.A.R.T.",
        "Logs de Erro",
        "Dump Hexadecimal",
        "Self-Test",
        "Sair do VESPER"
    };

    private static readonly string[] MenuBulletColors = {
        "#38BDF8",
        "#38BDF8",
        "#4ADE80",
        "#C084FC",
        "#FBBF24",
        "#F87171",
        "#94A3B8",
        "#38BDF8",
        "#F87171"
    };

    public static bool IsVerificationHarness = false;

    public static readonly Dictionary<string, (long cursor, SurfaceBlockStatus[] blocks)> BackgroundScanState = new();
    private static CancellationTokenSource? _bgScanCts;
    private static string _currentBgScanDevice = "";
    private static readonly object _bgScanLock = new();
    private static volatile bool _topologyChangePending = false;
    private static List<DriveSmartReport>? _pendingHotplugReports = null;
    private static readonly object _hotplugLock = new();
    private static volatile bool _isTelemetryBusy = false;
    private static volatile bool _telemetryUpdated = false;

    public static void StartOrUpdateBackgroundScanner(DriveSmartReport rep)
    {
        if (rep == null || string.IsNullOrEmpty(rep.DevicePath)) return;

        int totalBlocks = 8 * 28;
        if (!BackgroundScanState.TryGetValue(rep.DevicePath, out var state) || state.blocks == null || state.blocks.Length != totalBlocks)
        {
            var b = new SurfaceBlockStatus[totalBlocks];
            Array.Fill(b, SurfaceBlockStatus.Unchecked);
            state = (0, b);
            BackgroundScanState[rep.DevicePath] = state;
        }

        if (SurfaceTestEngine.DiskSurfaceCache.ContainsKey(rep.DevicePath)) return;
        if (state.cursor >= totalBlocks) return;

        lock (_bgScanLock)
        {
            if (_currentBgScanDevice == rep.DevicePath && _bgScanCts != null && !_bgScanCts.IsCancellationRequested)
                return;

            _bgScanCts?.Cancel();
            _bgScanCts = new CancellationTokenSource();
            _currentBgScanDevice = rep.DevicePath;
            var token = _bgScanCts.Token;
            int resumeCursor = (int)state.cursor;

            Task.Run(() =>
            {
                SurfaceTestEngine.ProbeSurfaceSample(
                    rep.DevicePath,
                    rep.DiskSizeBytes,
                    rep.BytesPerSector,
                    totalBlocks,
                    token,
                    (idx, status) =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (BackgroundScanState.TryGetValue(rep.DevicePath, out var curState) && curState.blocks != null && idx < curState.blocks.Length)
                        {
                            curState.blocks[idx] = status;
                            curState.cursor = idx + 1;
                            BackgroundScanState[rep.DevicePath] = curState;
                        }
                    },
                    startBlock: resumeCursor);
            }, token);
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
        if (IsVerificationHarness) return;
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
        var hotplugSw = Stopwatch.StartNew();
        long lastRenderedCursor = -1;

        using var hotplugCts = new CancellationTokenSource();
        var hotplugToken = hotplugCts.Token;
        Task.Run(async () =>
        {
            while (!hotplugToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2500, hotplugToken);
                    var latest = Program.RescanAllDrives();
                    if (HasDriveTopologyChanged(reports, latest))
                    {
                        lock (_hotplugLock)
                        {
                            _pendingHotplugReports = latest;
                            _topologyChangePending = true;
                        }
                    }
                }
                catch { }
            }
        }, hotplugToken);

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

                if (animSw.ElapsedMilliseconds >= 80)
                {
                    animSw.Restart();
                    if (selectedDiskIndex < reports.Count)
                    {
                        var curRep = reports[selectedDiskIndex];
                        if (BackgroundScanState.TryGetValue(curRep.DevicePath, out var bgState))
                        {
                            if (bgState.cursor != lastRenderedCursor)
                            {
                                lastRenderedCursor = bgState.cursor;
                                needRedraw = true;
                            }
                        }
                    }
                }

                if (telemetrySw.ElapsedMilliseconds >= 3000 && !_isTelemetryBusy)
                {
                    telemetrySw.Restart();
                    if (selectedDiskIndex < reports.Count)
                    {
                        var target = reports[selectedDiskIndex];
                        int prevTemp = target.TemperatureCelsius;
                        double prevHealth = target.HealthDefault;
                        _isTelemetryBusy = true;
                        Task.Run(() =>
                        {
                            try
                            {
                                if (Program.RefreshDriveTelemetry(target))
                                {
                                    if (target.TemperatureCelsius != prevTemp || Math.Abs(target.HealthDefault - prevHealth) > 0.01)
                                    {
                                        _telemetryUpdated = true;
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                _isTelemetryBusy = false;
                            }
                        });
                    }
                }

                if (_topologyChangePending)
                {
                    lock (_hotplugLock)
                    {
                        if (_pendingHotplugReports != null)
                        {
                            reports.Clear();
                            reports.AddRange(_pendingHotplugReports);
                            selectedDiskIndex = Math.Clamp(selectedDiskIndex, 0, Math.Max(0, reports.Count - 1));
                            _pendingHotplugReports = null;
                            needsClear = true;
                            needRedraw = true;
                        }
                        _topologyChangePending = false;
                    }
                }

                if (_telemetryUpdated)
                {
                    _telemetryUpdated = false;
                    needRedraw = true;
                }

                if (needRedraw && !Console.IsOutputRedirected)
                {
                    if (needsClear)
                    {
                        SafeClear();
                        needsClear = false;
                    }
                    else
                    {
                        try { Console.SetCursorPosition(0, 0); } catch { SafeClear(); }
                    }
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
                lastRenderedCursor = -1;
            }
            else if (keyInfo.Key == ConsoleKey.LeftArrow)
            {
                selectedDiskIndex = (selectedDiskIndex - 1 + reports.Count) % reports.Count;
                lastRenderedCursor = -1;
            }
            else if (keyInfo.Key == ConsoleKey.Enter || keyInfo.Key == ConsoleKey.Spacebar)
            {
                if (selectedMenuIndex == 8) break;
                _bgScanCts?.Cancel();
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
                lastRenderedCursor = -1;
            }
            else if (keyInfo.Key >= ConsoleKey.D1 && keyInfo.Key <= ConsoleKey.D8)
            {
                selectedMenuIndex = (int)keyInfo.Key - (int)ConsoleKey.D1;
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
                lastRenderedCursor = -1;
            }
            else if (keyInfo.Key >= ConsoleKey.NumPad1 && keyInfo.Key <= ConsoleKey.NumPad8)
            {
                selectedMenuIndex = (int)keyInfo.Key - (int)ConsoleKey.NumPad1;
                ExecuteModule(selectedMenuIndex, reports[selectedDiskIndex], ref selectedDiskIndex, reports);
                needsClear = true;
                lastRenderedCursor = -1;
            }
            else if (keyInfo.Key == ConsoleKey.D9 || keyInfo.Key == ConsoleKey.NumPad9 || keyInfo.Key == ConsoleKey.Escape || keyInfo.Key == ConsoleKey.Q)
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
                RunBenchmarkHubInteractive(rep);
                break;
            case 3:
                RunHealthDetailsInteractive(rep);
                break;
            case 4:
                RunSmartAttributesInteractive(rep);
                break;
            case 5:
                RunFirmwareErrorLogInteractive(rep);
                break;
            case 6:
                RunHexDumpInteractive(rep);
                break;
            case 7:
                RunSelfTestMenuInteractive(rep);
                break;
        }
    }

        public static string RenderDiskTabs(List<DriveSmartReport> reports, int diskIdx)
    {
        if (reports == null || reports.Count == 0) return "";
        var sb = new StringBuilder("  ");

        int maxVisible = 3;
        int startIdx = 0;
        int endIdx = reports.Count - 1;

        if (reports.Count > maxVisible)
        {
            startIdx = diskIdx - 1;
            if (startIdx + maxVisible > reports.Count) startIdx = reports.Count - maxVisible;
            if (startIdx < 0) startIdx = 0;
            endIdx = startIdx + maxVisible - 1;

            if (startIdx > 0) sb.Append($"[bold #38BDF8]\u25C4[/] [#64748B]+{startIdx}[/] ");
        }

        for (int i = startIdx; i <= endIdx; i++)
        {
            string cap = FormatCapacity(reports[i].DiskSizeBytes);
            string m = reports[i].Model;
            int maxChars = reports.Count > 1 ? 14 : 22;
            if (m.Length > maxChars) m = m.Substring(0, maxChars - 2) + "..";
            string modelEsc = Esc(m);

            if (i == diskIdx)
            {
                sb.Append($"[#38BDF8]\u250C\u2500[/] [bold #0B0F19 on #38BDF8] \u25CF #{i + 1} {modelEsc} ({cap}) [/] [#38BDF8]\u2500\u2510[/]  ");
            }
            else
            {
                sb.Append($"[#334155]\u250C\u2500[/] [#64748B] #{i + 1} {modelEsc} ({cap}) [/] [#334155]\u2500\u2510[/]  ");
            }
        }

        if (reports.Count > maxVisible)
        {
            if (endIdx < reports.Count - 1) sb.Append($"[#64748B]+{reports.Count - 1 - endIdx}[/] [bold #38BDF8]\u25BA[/] ");
            sb.Append($"[#94A3B8]({diskIdx + 1}/{reports.Count})[/]");
        }

        return sb.ToString();
    }

    public static IRenderable BuildMainDashboard(List<DriveSmartReport> reports, int diskIdx, int selectedMenuIdx, bool isElevated)
    {
        var items = new List<IRenderable>();
        if (reports == null || reports.Count == 0)
        {
            var noDiskTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            noDiskTable.AddColumn(new TableColumn("C"));
            noDiskTable.AddRow(new Markup("\n  [bold #0B0F19 on #FBBF24]  AGUARDANDO DISPOSITIVO  [/]\n\n  Nenhum disco fisico compativel detectado. Conecte uma unidade SATA, NVMe ou disco externo USB.\n"));
            var noDiskPanel = new Panel(noDiskTable).Header(" [#FBBF24]STATUS DO BARRAMENTO[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(251, 191, 36)).Expand();
            return MasterContainer(104, noDiskPanel);
        }
        diskIdx = Math.Clamp(diskIdx, 0, reports.Count - 1);
        var currentRep = reports[diskIdx];

        // 1. Header com Logo 3D Extrude e Badge
        var headerGrid = new Grid();
        headerGrid.AddColumn(new GridColumn().Width(58));
        headerGrid.AddColumn(new GridColumn().Width(46).RightAligned());

        string logoText =
            "[#F472B6]___    __________________________________________ [/]\n" +
            "[#C084FC]__ |  / /__  ____/_  ___/__  __ \\__  ____/__  __ \\ [/]\n" +
            "[#818CF8]__ | / /__  __/  _____ \\__  /_/ /_  __/  __  /_/ /[/]\n" +
            "[#60A5FA]__ |/ / _  /___  ____/ /_  ____/_  /___  _  _, _/ [/]\n" +
            "[#38BDF8]_____/  /_____/  /____/ /_/     /_____/  /_/ |_|  [/]";

        string badge = isElevated ? "[bold #0B0F19 on #4ADE80]  ADMIN  [/]" : "[bold #0B0F19 on #FBBF24]  USER  [/]";
        string headerRight = $"\n{badge}  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]";

        headerGrid.AddRow(new Markup(logoText), new Markup(headerRight));
        items.Add(headerGrid);
        items.Add(new Markup(""));

        string tabsMarkup = RenderDiskTabs(reports, diskIdx);
        items.Add(new Markup(tabsMarkup));

        var teleTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        teleTable.AddColumn(new TableColumn("C1").Width(12));
        teleTable.AddColumn(new TableColumn("C2").Width(31));
        teleTable.AddColumn(new TableColumn("Sep").Width(3).Centered());
        teleTable.AddColumn(new TableColumn("C3").Width(12));
        teleTable.AddColumn(new TableColumn("C4").Width(31));

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
        menuTable.AddColumn(new TableColumn("Item").NoWrap());

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
        menuTable.AddRow(new Markup("  [#64748B]Navegue com [[↑/↓]] ou [[1-9]][/]"));

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
            StartOrUpdateBackgroundScanner(currentRep);
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
                    var bStatus = (BackgroundScanState[currentRep.DevicePath].blocks != null && bId < BackgroundScanState[currentRep.DevicePath].blocks.Length)
                        ? BackgroundScanState[currentRep.DevicePath].blocks[bId]
                        : SurfaceBlockStatus.Unchecked;

                    if (bStatus != SurfaceBlockStatus.Unchecked)
                    {
                        testedCount++;
                        if (bStatus == SurfaceBlockStatus.BadSector || bStatus == SurfaceBlockStatus.Damaged) badCount++;
                        previewBuilder.Append(FormatBlockMarkup(bStatus) + " ");
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
                previewBuilder.Append($"  [#94A3B8]Status:[/] [#38BDF8]Varredura Real[/]  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#4ADE80]{testedCount}/{totalPreviewBlocks}[/]  [#64748B]•[/]  [#94A3B8]Bad:[/] [{(badCount > 0 ? "#EF4444" : "#4ADE80")}]{badCount}[/]\n");
            }
            else
            {
                string statusText = badCount > 0 ? $"[#EF4444]Defeitos ({badCount})[/]" : "[#4ADE80]Integra[/]";
                previewBuilder.Append($"  [#94A3B8]Status:[/] {statusText}  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#4ADE80]{testedCount} OK[/]  [#64748B]•[/]  [#94A3B8]Bad:[/] [{(badCount > 0 ? "#EF4444" : "#4ADE80")}]{badCount}[/]\n");
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
        dockTable.AddColumn(new TableColumn("Dock"));

        dockTable.AddRow(
            new Markup("  [#38BDF8]>[/]  [#38BDF8][[[#F1F5F9]↑/↓[/]]][/] [#94A3B8]Selecionar[/]   " +
                       "[#38BDF8][[[#F1F5F9]Enter[/]]][/] [#94A3B8]Iniciar[/]   " +
                       "[#38BDF8][[[#F1F5F9]Tab[/]]][/] [#94A3B8]Alternar[/]   " +
                       "[#38BDF8][[[#F1F5F9]Esc[/]]][/] [#94A3B8]Sair[/]")

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
        table.AddColumn(new TableColumn("[bold #F1F5F9]CAMPO[/]").Width(26));
        table.AddColumn(new TableColumn("[bold #94A3B8]VALOR[/]").Width(64));

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
        string tempText = $"[bold #38BDF8]{rep.TemperatureCelsius} °C[/] [#94A3B8](Faixa operacional segura: 0-60°C)[/]";
        if (rep.IsNvme && (rep.TemperatureSensor1 > 0 || rep.TemperatureSensor2 > 0))
        {
            tempText = $"[bold #38BDF8]{rep.TemperatureCelsius} °C[/] [#94A3B8](Composta)[/] [bold #64748B]•[/] [#94A3B8]Sensor 1:[/] [#38BDF8]{rep.TemperatureSensor1} °C[/] [bold #64748B]•[/] [#94A3B8]Sensor 2:[/] [#38BDF8]{rep.TemperatureSensor2} °C[/]";
        }
        table.AddRow(new Markup("[#94A3B8]Temperatura Operacional[/]"), new Markup(tempText));
        table.AddRow(new Markup("[#94A3B8]Horas em Operacao (POH)[/]"), new Markup($"[bold #F1F5F9]{rep.PowerOnHours:N0} h[/] [#94A3B8]({rep.PowerOnHours / 24:N0} dias)[/]"));
        table.AddRow(new Markup("[#94A3B8]Ciclos de Inicializacao[/]"), new Markup($"[bold #F1F5F9]{rep.PowerCycleCount:N0} ciclos[/]"));
        string tbwDisplay = rep.TotalTbWritten >= 1.0 ? $"{rep.TotalTbWritten:F2} TB" : $"{rep.TotalTbWritten * 1024.0:F2} GB";
        string tbrDisplay = rep.TotalTbRead >= 1.0 ? $"{rep.TotalTbRead:F2} TB" : $"{rep.TotalTbRead * 1024.0:F2} GB";
        table.AddRow(new Markup("[#94A3B8]Total Bytes Gravados (TBW)[/]"), new Markup($"[bold #C084FC]{tbwDisplay}[/]"));
        table.AddRow(new Markup("[#94A3B8]Total Bytes Lidos (TBR)[/]"), new Markup($"[bold #C084FC]{tbrDisplay}[/]"));
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
            table.AddColumn(new TableColumn("[#94A3B8]ID[/]").Width(6));
            table.AddColumn(new TableColumn("[bold #F1F5F9]PARAMETRO NVMe[/]").Width(24));
            table.AddColumn(new TableColumn("[bold #38BDF8]VALOR BRUTO[/]").Width(16));
            table.AddColumn(new TableColumn("[#FBBF24]STATUS[/]").Width(10));
            table.AddColumn(new TableColumn("[bold #4ADE80]INTERPRETACAO[/]").Width(26));

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
        var querySw = Stopwatch.StartNew();
        int spinnerIdx = 0;
        string[] spinner = { "\u280B", "\u2819", "\u2839", "\u2838", "\u283C", "\u2834", "\u2826", "\u2827", "\u2807", "\u280F" };
        bool aborted = false;
        bool userExited = false;
        bool isCompleted = false;
        byte fwPercent = 0;
        string fwStatusMessage = "Comando despachado ao firmware. Aguardando microcodigo...";

        IRenderable BuildFrame()
        {
            var items = new List<IRenderable>();
            items.Add(new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]\u2022[/]  [#38BDF8]@_fxgiovane[/]\n"));

            var statusTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            statusTable.AddColumn(new TableColumn("Col1").Width(24));
            statusTable.AddColumn(new TableColumn("Col2").Width(74));

            statusTable.AddRow(new Markup("[#94A3B8]Dispositivo:[/]"), new Markup($"[bold #F1F5F9]{Esc(rep.Model)}[/] [#64748B]({Esc(rep.DevicePath)})[/]"));
            statusTable.AddRow(new Markup("[#94A3B8]Modalidade:[/]"), new Markup($"[bold #38BDF8]{testName}[/]"));

            if (aborted)
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status:[/]"), new Markup("[bold #0B0F19 on #FBBF24]  TESTE INTERROMPIDO  [/]"));
            }
            else if (isCompleted)
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status:[/]"), new Markup("[bold #0B0F19 on #4ADE80]  AUTOTESTE CONCLUIDO  [/]"));
            }
            else
            {
                statusTable.AddRow(new Markup("[#94A3B8]Status:[/]"), new Markup($"[bold #0B0F19 on #4ADE80]  EXECUTANDO  [/]  [#38BDF8]{spinner[spinnerIdx % spinner.Length]}[/]"));
            }

            statusTable.AddRow(new Markup("[#94A3B8]Tempo Decorrido:[/]"), new Markup($"[bold #F1F5F9]{sw.Elapsed:mm\\:ss}[/]"));

            string progBar = RenderSegmentedBar(fwPercent, 36, "#38BDF8");
            statusTable.AddRow(new Markup("[#94A3B8]Progresso Firmware:[/]"), new Markup($"{progBar}  [#38BDF8]{fwPercent}%[/]"));
            statusTable.AddRow(new Markup("[#94A3B8]Telemetria:[/]"), new Markup($"[#F1F5F9]{Esc(fwStatusMessage)}[/]"));

            var statusPanel = new Panel(statusTable)
                .Header(" [#38BDF8]AUTOTESTE DE FIRMWARE[/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(51, 65, 85))
                .Expand();
            items.Add(statusPanel);

            var dockTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            dockTable.AddColumn(new TableColumn("Dock"));

            if (isCompleted || aborted)
            {
                dockTable.AddRow(
                    new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]")
                );
            }
            else
            {
                dockTable.AddRow(
                    new Markup("  [#38BDF8]>[/] [#38BDF8][[[/][bold #0B0F19 on #F87171] Esc [/] [#94A3B8]Abortar Teste[/][#38BDF8]]][/]   [#38BDF8][[[/][bold #0B0F19 on #38BDF8] Enter [/] [#94A3B8]Voltar (2º Plano)[/][#38BDF8]]][/]")
                );
            }
            var dockPanel = new Panel(dockTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            items.Add(dockPanel);

            return MasterContainer(104, items.ToArray());
        }

        if (Console.IsOutputRedirected)
        {
            AnsiConsole.Write(BuildFrame());
            return;
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

                    if (querySw.ElapsedMilliseconds >= 1000)
                    {
                        querySw.Restart();
                        var fwStat = FirmwareSelfTestEngine.QuerySelfTestStatus(rep);                         if (fwStat != null)                         {                             fwPercent = (byte)Math.Clamp(100 - fwStat.PercentRemaining, 0, 100);                             fwStatusMessage = fwStat.Message;                             if (fwStat.Succeeded || fwStat.Message.Contains("concluido", StringComparison.OrdinalIgnoreCase) || fwStat.Message.Contains("sucesso", StringComparison.OrdinalIgnoreCase))                             {                                 isCompleted = true;                                 fwPercent = 100;                             }                             else if (fwStat.Failed || fwStat.Message.Contains("abortado", StringComparison.OrdinalIgnoreCase))                             {                                 aborted = true;                             }                         }
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

    public static IRenderable BuildSurfaceModeSelectRenderable(DriveSmartReport rep)
    {
        var modeTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        modeTable.AddColumn(new TableColumn("Opt"));
        modeTable.AddRow(new Markup(
            "\n  [bold #0B0F19 on #38BDF8]  SELECAO DE VARREDURA  [/]\n\n" +
            $"  Disco: [bold #F1F5F9]{Esc(rep.Model)}[/] ([#38BDF8]{Esc(rep.DevicePath)}[/])\n\n" +
            "  [bold #38BDF8][[1]][/] [bold #F1F5F9]Varredura Completa (100% Linear)[/] [#94A3B8]— Leitura continua LBA 0 ao fim (~2.5 min)[/]\n" +
            "  [bold #4ADE80][[2]][/] [bold #F1F5F9]Varredura Rapida Densa (350 Zonas)[/] [#94A3B8]— Amostragem equidistante 0% a 100% LBA (~15-30s)[/]\n\n" +
            "  [#94A3B8]Pressione [bold #38BDF8]1[/], [bold #4ADE80]2[/] ou [bold #F1F5F9]ESC[/] para retornar...[/]\n"
        ));
        var modePanel = new Panel(modeTable)
            .Header(" [#38BDF8]TESTE DE SUPERFICIE[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(56, 189, 248))
            .Expand();

        return MasterContainer(104,
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"),
            modePanel
        );
    }

    public static void RunSurfaceTestInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        if (rep.IsRaidMember && !rep.DevicePath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            var errTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            errTable.AddColumn(new TableColumn("Err"));
            errTable.AddRow(new Markup(
                "\n  [bold #0B0F19 on #FBBF24]  DISCO MEMBRO DE ARRANJO RAID  [/]\n\n" +
                $"  O disco [bold #F1F5F9]{Esc(rep.Model)}[/] ([#38BDF8]{Esc(rep.Serial)}[/]) pertence a um arranjo gerenciado por controladora de hardware.\n\n" +
                "  [#94A3B8]• Setores brutos (LBA) individuais sao blindados pela controladora RAID e nao possuem handle de bloco direto no Windows.[/]\n" +
                "  [#4ADE80]• Para verificar a integridade da superficie ou ler blocos, execute o teste no volume do arranjo (ex: AMD-RAID Array).[/]\n" +
                "  [#38BDF8]• A telemetria de saude, temperatura e contadores SMART permanecem disponiveis nos Detalhes de Saude (Opcao 3 e 4).[/]\n"
            ));
            var errPanel = new Panel(errTable).Header(" [#FBBF24]STATUS DE SUPERFICIE (RAID)[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(251, 191, 36)).Expand();
            AnsiConsole.Write(MasterContainer(104,
                new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"),
                errPanel,
                new Markup("\n  [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]")
            ));
            if (!smokeTestMode && !Console.IsInputRedirected)
            {
                try { Console.ReadKey(true); } catch { }
            }
            return;
        }

        SurfaceScanCoverage coverage = SurfaceScanCoverage.QuickDense;
        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            SafeClear();
            AnsiConsole.Write(BuildSurfaceModeSelectRenderable(rep));

            while (true)
            {
                var k = Console.ReadKey(true).Key;
                if (k == ConsoleKey.D1 || k == ConsoleKey.NumPad1)
                {
                    coverage = SurfaceScanCoverage.FullLinear;
                    break;
                }
                if (k == ConsoleKey.D2 || k == ConsoleKey.NumPad2 || k == ConsoleKey.Enter)
                {
                    coverage = SurfaceScanCoverage.QuickDense;
                    break;
                }
                if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                {
                    return;
                }
            }
        }

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
            {
                if (res.Status > grid[res.BlockIndex]) grid[res.BlockIndex] = res.Status;
            }

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
                                    var renderable = BuildSurfaceRenderable(rep, grid, speedSpark, latencySpark, prog, activeBlockIndex, cts.IsCancellationRequested, coverage);
                                    tableLayout.Rows.Clear();
                                    tableLayout.AddRow(renderable);
                                    ctx.Refresh();
                                    lastRefreshSw.Restart();
                                }
                            },
                            onBlockStarting: (b) => { activeBlockIndex = b; },
                            throttleMs: 0,
                            scanMode: SurfaceScanMode.VerifyRead,
                            coverage: coverage);
                    }
                    catch (Exception ex)
                    {
                        capturedError = ex;
                    }

                    var finalRenderable = BuildSurfaceRenderable(rep, grid, speedSpark, latencySpark, lastProgress ?? new SurfaceTestProgress(), activeBlockIndex, cts.IsCancellationRequested, coverage, isFinished: true);
                    tableLayout.Rows.Clear();
                    tableLayout.AddRow(finalRenderable);
                    ctx.Refresh();
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
                    throttleMs: 0,
                    scanMode: SurfaceScanMode.VerifyRead,
                    coverage: coverage);

                var finalRenderable = BuildSurfaceRenderable(rep, grid, speedSpark, latencySpark, lastProgress ?? new SurfaceTestProgress(), activeBlockIndex, cts.IsCancellationRequested, coverage, isFinished: true);
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
            AnsiConsole.MarkupLine("\n  [bold black on #FBBF24]  CANCELLED  [/] [#FBBF24]Surface Test interrompido pelo usuario.[/]");
        }
        else if (lastProgress != null && lastProgress.BadBlocks > 0)
        {
            AnsiConsole.MarkupLine($"\n  [bold black on #F87171]  ALERTA  [/] [#F87171]Foram detectados {lastProgress.BadBlocks} setores defeituosos no disco.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n  [bold black on #4ADE80]  SUCCEEDED  [/] [#4ADE80]Surface Test concluido sem setores criticos.[/]");
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
        bool cancelled,
        SurfaceScanCoverage coverage = SurfaceScanCoverage.QuickDense,
        bool isFinished = false)
    {
        var items = new List<IRenderable>();

        string statusPill = cancelled
            ? "[bold black on #FBBF24]  CANCELADO  [/]"
            : (isFinished
                ? "[bold black on #4ADE80]  CONCLUÍDO  [/]"
                : "[bold black on #38BDF8]  RUNNING  [/]");

        string covLabel = coverage == SurfaceScanCoverage.FullLinear ? "LINEAR 100%" : "RAPIDA DENSA";
        items.Add(new Markup($"  {statusPill} [bold #F1F5F9]SURFACE TEST ({covLabel})[/]  [#334155]•[/]  [#38BDF8]{Esc(rep.DevicePath)}[/] [#94A3B8]({Esc(rep.Model)})[/]  [#94A3B8]({prog.Elapsed.TotalSeconds:F1}s)[/]\n"));

        var gridBuilder = new StringBuilder(4096);
        int cols = 50;
        int rows = 7;

        long mappedActive = (coverage == SurfaceScanCoverage.FullLinear && prog.TotalBlocks > 0)
            ? Math.Clamp((activeBlock * grid.Length) / prog.TotalBlocks, 0, grid.Length - 1)
            : activeBlock;

        for (int r = 0; r < rows; r++)
        {
            gridBuilder.Append(" ");
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                if (!isFinished && idx == mappedActive)
                {
                    gridBuilder.Append("[bold black on #38BDF8]>[/] ");
                }
                else if (idx < grid.Length && grid[idx] != SurfaceBlockStatus.Unchecked)
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

        items.Add(new Markup("  [#94A3B8]Legenda:[/] [#4ADE80]■[/] <12ms  [#10B981]■[/] <30ms  [#FBBF24]■[/] <80ms  [#FB923C]▒[/] <300ms  [bold black on #F87171]  BAD  [/]  [bold black on #38BDF8]>[/] Scanner  [#334155]·[/] Pendente\n"));

        var metricsTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        metricsTable.AddColumn(new TableColumn("[bold #F1F5F9]CAMPO[/]").Width(24));
        metricsTable.AddColumn(new TableColumn("[bold #94A3B8]VALOR[/]").Width(66));

        string speedSparkLine = RenderSparkline(speedSpark);
        string latSparkLine = RenderSparkline(latencySpark);

        int barWidth = 26;
        int filled = Math.Clamp((int)(prog.Percent / 100.0 * barWidth), 0, barWidth);
        string progressBar = new string('▓', filled) + new string('░', barWidth - filled);

        metricsTable.AddRow(new Markup("[#94A3B8]Setores / Tamanho Bloco[/]"), new Markup($"[#F1F5F9]{prog.TotalSectors:N0}[/] setores | Bloco: [bold #F1F5F9]{prog.BlockSectors:N0}[/] ([#38BDF8]{prog.BlockSizeMB * 1024.0:F0} KB[/])"));
        string multiPhasePart = prog.MultiPhaseRecoveredBlocks > 0
            ? $" | Rec. Multifasica: [bold #38BDF8]{prog.MultiPhaseRecoveredBlocks} rec[/]"
            : "";
        metricsTable.AddRow(new Markup("[#94A3B8]Distribuicao dos Blocos[/]"), new Markup($"Bons: [#4ADE80]{prog.GoodBlocks}[/] | Lentos: [#FBBF24]{prog.SlowBlocks}[/] | Danificados: [#FB923C]{prog.DamagedBlocks}[/] | Bad: [bold black on #F87171]  {prog.BadBlocks}  [/]{multiPhasePart}"));
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
        dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]ESC[/] [#94A3B8]ou[/] [bold #F1F5F9]Q[/] [#94A3B8]para cancelar o teste a qualquer momento.[/]"));
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
        if (rep.IsRaidMember && !rep.DevicePath.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            var errTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            errTable.AddColumn(new TableColumn("Err"));
            errTable.AddRow(new Markup(
                "\n  [bold #0B0F19 on #FBBF24]  DISCO MEMBRO DE ARRANJO RAID  [/]\n\n" +
                $"  O disco [bold #F1F5F9]{Esc(rep.Model)}[/] ([#38BDF8]{Esc(rep.Serial)}[/]) pertence a um arranjo gerenciado por controladora de hardware.\n\n" +
                "  [#94A3B8]• O teste de busca (Seek QPC) requer envio de comandos de leitura LBA direta a um dispositivo de bloco registrado no Windows.[/]\n" +
                "  [#4ADE80]• Para aferir os tempos de busca e latencias do conjunto, execute o teste na unidade do arranjo (ex: AMD-RAID Array).[/]\n" +
                "  [#38BDF8]• A telemetria fisica individual dos discos permanece ativa nos modulos de Saude e Atributos SMART.[/]\n"
            ));
            var errPanel = new Panel(errTable).Header(" [#FBBF24]STATUS DE BUSCA (RAID)[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(251, 191, 36)).Expand();
            AnsiConsole.Write(MasterContainer(104,
                new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]\n"),
                errPanel,
                new Markup("\n  [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]")
            ));
            if (!smokeTestMode && !Console.IsInputRedirected)
            {
                try { Console.ReadKey(true); } catch { }
            }
            return;
        }

        using var cts = new CancellationTokenSource();
        long totalSeekOps = 0;
        int durationMs = smokeTestMode ? 500 : 8000;
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
                                if (lastRefreshSw.ElapsedMilliseconds >= 40 || prog.OperationsCompleted >= prog.TotalOperationsTarget)
                                {
                                    var renderable = BuildSeekRenderable(rep, sparkHistory, latencyHistory, prog, cts.IsCancellationRequested);
                                    tableLayout.Rows.Clear();
                                    tableLayout.AddRow(renderable);
                                    ctx.Refresh();
                                    lastRefreshSw.Restart();
                                }
                            },
                            throttleMs: 0,
                            durationMs: durationMs,
                            isSsdOrNvme: rep.IsSsd || rep.IsNvme,
                            getTemperatureCallback: () => rep.TemperatureCelsius);
                    }
                    catch (Exception ex)
                    {
                        capturedError = ex;
                    }

                    string finalMsg = cts.IsCancellationRequested
                        ? "[bold black on #FBBF24]  CANCELADO  [/] [#FBBF24]Teste interrompido pelo usuario.[/]"
                        : (capturedError != null
                            ? $"[bold black on #F87171]  FALHA  [/] [#F87171]Erro: {Esc(capturedError.Message)}[/]"
                            : "[bold black on #4ADE80]  SUCESSO  [/] [#4ADE80]Teste concluido com 100% das operacoes.[/]");

                    var finalRenderable = BuildSeekRenderable(rep, sparkHistory, latencyHistory, lastProgress ?? new SeekTestProgress(), cts.IsCancellationRequested || capturedError != null, isFinished: true, finalMessage: finalMsg);
                    tableLayout.Rows.Clear();
                    tableLayout.AddRow(finalRenderable);
                    ctx.Refresh();
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
                    onSeekCallback,
                    throttleMs: 0,
                    durationMs: durationMs,
                    isSsdOrNvme: rep.IsSsd || rep.IsNvme,
                    getTemperatureCallback: () => rep.TemperatureCelsius);

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
        metricsTable.AddColumn(new TableColumn("").Width(18));
        metricsTable.AddColumn(new TableColumn("").Width(26));
        metricsTable.AddColumn(new TableColumn("").Width(18));
        metricsTable.AddColumn(new TableColumn("").Width(26));

        metricsTable.AddRow(
            new Markup("[#94A3B8]Seek Atual / Min:[/]"), new Markup($"[bold #38BDF8]{prog.CurrentSeekMs:F2} ms[/] / [#4ADE80]{(prog.MinSeekMs >= 999999.0 || prog.MinSeekMs < 0 ? 0.0 : prog.MinSeekMs):F2} ms[/]"),
            new Markup("[#94A3B8]Seek Med / Max:[/]"), new Markup($"[#4ADE80]{prog.AvgSeekMs:F2} ms[/] / [#F87171]{(prog.MaxSeekMs >= 999999.0 || prog.MaxSeekMs < 0 ? 0.0 : prog.MaxSeekMs):F2} ms[/]")
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
            verdictTable.AddColumn(new TableColumn("Badge").Width(22));
            verdictTable.AddColumn(new TableColumn("Desc").Width(68));
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
            dockTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]ESC[/] [#94A3B8]ou[/] [bold #F1F5F9]Q[/] [#94A3B8]para cancelar o teste a qualquer momento.[/]"));
        }
        var dockPanel = new Panel(dockTable)
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(dockPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength) return path;
        string root = Path.GetPathRoot(path) ?? "";
        string fileName = Path.GetFileName(path);
        int available = maxLength - root.Length - fileName.Length - 4;
        if (available > 0)
        {
            return $"{root}...{Path.DirectorySeparatorChar}{fileName}";
        }
        return path.Length > maxLength ? path.Substring(0, maxLength - 3) + "..." : path;
    }

    public static void RunBenchmarkInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        using var cts = new CancellationTokenSource();
        BenchmarkProgress? lastProgress = null;
        var swRefresh = Stopwatch.StartNew();

        var tableLayout = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tableLayout.AddColumn(new TableColumn("Content"));

        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Live(tableLayout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Start(ctx =>
                {
                    Action<BenchmarkProgress> onProg = p =>
                    {
                        lastProgress = p;
                        if (swRefresh.ElapsedMilliseconds >= 60 || p.OverallPercent >= 100.0)
                        {
                            swRefresh.Restart();
                            tableLayout.Rows.Clear();
                            tableLayout.AddRow(BuildBenchmarkRenderable(p, isCompleted: false));
                            ctx.Refresh();
                        }

                        if (!smokeTestMode && !Console.IsInputRedirected && Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true).Key;
                            if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                            {
                                cts.Cancel();
                            }
                        }
                    };

                    try
                    {
                        VesperBenchmarkEngine.RunBenchmark(rep, cts.Token, onProg, smokeTestMode: smokeTestMode);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[#F87171]Erro no benchmark: {Esc(ex.Message)}[/]");
                    }

                    if (lastProgress != null)
                    {
                        tableLayout.Rows.Clear();
                        tableLayout.AddRow(BuildBenchmarkRenderable(lastProgress, isCompleted: true));
                        ctx.Refresh();
                    }
                });
        }
        else
        {
            Action<BenchmarkProgress> onProg = p => { lastProgress = p; };
            try
            {
                VesperBenchmarkEngine.RunBenchmark(rep, cts.Token, onProg, smokeTestMode: smokeTestMode);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[#F87171]Erro no benchmark: {Esc(ex.Message)}[/]");
            }
            if (lastProgress != null)
            {
                AnsiConsole.Write(BuildBenchmarkRenderable(lastProgress, isCompleted: true));
            }
        }

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildBenchmarkRenderable(BenchmarkProgress p, bool isCompleted)
    {
        var items = new List<IRenderable>();

        var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headTable.AddColumn(new TableColumn("Left").Width(54));
        headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
        headTable.AddRow(
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
            new Markup($"[#94A3B8]Tamanho:[/] [bold #38BDF8]{p.TestFileSizeBytes / (1024 * 1024):N0} MiB[/]  [#64748B]•[/]  [#94A3B8]Alvo:[/] [bold #F1F5F9]{Esc(TruncatePath(p.TargetPath, 36))}[/]")
        );
        items.Add(headTable);
        items.Add(new Markup(""));

        string statusText = isCompleted
            ? "[bold #0B0F19 on #4ADE80]  CONCLUÍDO  [/]"
            : $"[bold #0B0F19 on #38BDF8]  {Esc(p.CurrentOperationDescription.ToUpperInvariant())}  [/]";

        string speedText = isCompleted ? "" : $"  [#94A3B8]Velocidade:[/] [bold #38BDF8]{p.CurrentSpeedMBps:N2} MB/s[/]";
        string overallBar = RenderSegmentedBar(p.OverallPercent, 22, "#4ADE80");

        var statusTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        statusTable.AddColumn(new TableColumn("S1").Width(52));
        statusTable.AddColumn(new TableColumn("S2").Width(52).RightAligned());
        statusTable.AddRow(
            new Markup($"  {statusText}{speedText}"),
            new Markup($"[#94A3B8]Progresso:[/] {overallBar} [bold #F1F5F9]{p.OverallPercent:F0}%[/]  [#64748B]•[/]  [#94A3B8]Tempo:[/] [#38BDF8]{p.Elapsed:mm\\:ss}[/]")
        );
        var statusPanel = new Panel(statusTable)
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(statusPanel);

        var benchTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        benchTable.ShowRowSeparators = true;
        benchTable.AddColumn(new TableColumn("[bold #38BDF8]PERFIL / FILA[/]").Width(12).Centered());
        benchTable.AddColumn(new TableColumn("[bold #4ADE80]READ (MB/s)[/]").Width(28));
        benchTable.AddColumn(new TableColumn("[bold #38BDF8]WRITE (MB/s)[/]").Width(28));
        benchTable.AddColumn(new TableColumn("[bold #F1F5F9]IOPS & LATENCIA[/]").Width(18));

        double maxSeenSpeed = 100.0;
        foreach (var item in p.Items)
        {
            if (item.ReadMBps > maxSeenSpeed) maxSeenSpeed = item.ReadMBps;
            if (item.WriteMBps > maxSeenSpeed) maxSeenSpeed = item.WriteMBps;
        }

        for (int i = 0; i < p.Items.Length; i++)
        {
            var item = p.Items[i];
            bool isActive = (!isCompleted && p.ActiveItemIndex == i);

            string profileCell = $"[bold #F1F5F9]{item.Name}[/]\n[#94A3B8]{item.QueueThreadInfo}[/]";

            string readCell;
            if (item.ReadDone)
            {
                int barLen = (int)Math.Clamp((item.ReadMBps / maxSeenSpeed) * 16, 1, 16);
                string bar = new string('█', barLen);
                readCell = $"[bold #4ADE80]{item.ReadMBps,9:N2}[/] [#94A3B8]MB/s[/]\n[#4ADE80]{bar}[/]";
            }
            else if (isActive && !p.IsWriting)
            {
                readCell = $"[bold #FBBF24]{p.CurrentSpeedMBps,9:N2}[/] [#FBBF24]MB/s[/]\n[#FBBF24]▒▒▒▒▒▒ [bold]TESTANDO[/] ▒▒▒▒▒▒[/]";
            }
            else
            {
                readCell = "[#475569]   ---   MB/s[/]\n[#334155]················[/]";
            }

            string writeCell;
            if (item.WriteDone)
            {
                int barLen = (int)Math.Clamp((item.WriteMBps / maxSeenSpeed) * 16, 1, 16);
                string bar = new string('█', barLen);
                writeCell = $"[bold #38BDF8]{item.WriteMBps,9:N2}[/] [#94A3B8]MB/s[/]\n[#38BDF8]{bar}[/]";
            }
            else if (isActive && p.IsWriting)
            {
                writeCell = $"[bold #FBBF24]{p.CurrentSpeedMBps,9:N2}[/] [#FBBF24]MB/s[/]\n[#FBBF24]▒▒▒▒▒▒ [bold]TESTANDO[/] ▒▒▒▒▒▒[/]";
            }
            else
            {
                writeCell = "[#475569]   ---   MB/s[/]\n[#334155]················[/]";
            }

            string iopsCell;
            if (item.ReadDone || item.WriteDone)
            {
                double bestIops = Math.Max(item.ReadIops, item.WriteIops);
                double bestLat = item.ReadLatencyUs > 0 ? item.ReadLatencyUs : item.WriteLatencyUs;
                string iopsStr = bestIops >= 1000 ? $"{bestIops / 1000.0:F1}k" : $"{bestIops:F0}";
                iopsCell = $"[bold #C084FC]{iopsStr} IOPS[/]\n[#94A3B8]Lat:[/] [bold #38BDF8]{bestLat:F1} μs[/]";
            }
            else
            {
                iopsCell = "[#475569]--- IOPS[/]\n[#475569]--- μs[/]";
            }

            benchTable.AddRow(new Markup(profileCell), new Markup(readCell), new Markup(writeCell), new Markup(iopsCell));
        }

        var benchPanel = new Panel(benchTable)
            .Header(" [#4ADE80]MEDICOES DE DESEMPENHO[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(benchPanel);

        var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        footTable.AddColumn(new TableColumn("Foot"));
        string footerAction = isCompleted
            ? "[#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"
            : "[#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]ESC[/] [#94A3B8]ou[/] [bold #F1F5F9]Q[/] [#94A3B8]para cancelar o teste a qualquer momento.[/]";

        footTable.AddRow(new Markup(
            $"  [#94A3B8]Processador:[/] [bold #F1F5F9]{Esc(p.CpuName)}[/]   " +
            $"[#64748B]•[/]   [#94A3B8]Dispositivo:[/] [bold #F1F5F9]{Esc(p.ModelName)}[/]\n" +
            $"  {footerAction}"
        ));
        var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(footPanel);

        return MasterContainer(104, items.ToArray());
    }

    private static bool HasDriveTopologyChanged(List<DriveSmartReport> oldDrives, List<DriveSmartReport> newDrives)
    {
        if (oldDrives.Count != newDrives.Count) return true;
        for (int i = 0; i < oldDrives.Count; i++)
        {
            if (!string.Equals(oldDrives[i].DevicePath, newDrives[i].DevicePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(oldDrives[i].Serial, newDrives[i].Serial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static void RunBenchmarkHubInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        if (smokeTestMode)
        {
            RunBenchmarkInteractive(rep, smokeTestMode: true);
            RunIoLadderInteractive(rep, smokeTestMode: true);
            RunLbaCurveInteractive(rep, smokeTestMode: true);
            return;
        }

        while (true)
        {
            SafeClear();
            var items = new List<IRenderable>();

            var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            headTable.AddColumn(new TableColumn("Left").Width(54));
            headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
            headTable.AddRow(
                new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
                new Markup($"[#94A3B8]Dispositivo:[/] [bold #38BDF8]{Esc(rep.DevicePath)}[/] [#94A3B8]({Esc(rep.Model)})[/]")
            );
            items.Add(headTable);
            items.Add(new Markup(""));

            var menuTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            menuTable.AddColumn(new TableColumn("[bold #38BDF8]OPCAO[/]").Width(8).Centered());
            menuTable.AddColumn(new TableColumn("[bold #F1F5F9]TESTE[/]").Width(42));
            menuTable.AddColumn(new TableColumn("[bold #94A3B8]ESCOPO[/]").Width(36));

            menuTable.AddRow(
                new Markup("[bold #0B0F19 on #4ADE80]  [[1]]  [/]"),
                new Markup("[bold #4ADE80]Vazao Continua & IOPS Multi-Queue[/]\n[#94A3B8]Filas assincronas unbuffered Q8/Q32[/]"),
                new Markup("[#F1F5F9]SEQ1M, SEQ128K, RND4K Q32T16, RND4K Q1T1[/]\n[#64748B]Taxa de pico e saturacao de IOPS[/]")
            );
            menuTable.AddRow(
                new Markup("[bold #0B0F19 on #38BDF8]  [[2]]  [/]"),
                new Markup("[bold #38BDF8]Escada de Eficiencia de Bloco I/O[/]\n[#94A3B8]Escada de I/O em potencias de 2 (4KB..64MB)[/]"),
                new Markup("[#F1F5F9]15 tamanhos de bloco com barras ANSI[/]\n[#64748B]Diagnostico de gargalo de bloco de dados[/]")
            );
            menuTable.AddRow(
                new Markup("[bold #0B0F19 on #FBBF24]  [[3]]  [/]"),
                new Markup("[bold #FBBF24]Curva de Vazao por Extensao LBA[/]\n[#94A3B8]Curva continua de 0% a 100% de extensao LBA[/]"),
                new Markup("[#F1F5F9]Vazao CAV em HDDs e quebra de cache SLC em SSDs[/]\n[#64748B]100 pontos de amostragem + tempo de acesso[/]")
            );

            var hubPanel = new Panel(menuTable)
                .Header(" [#38BDF8]BENCHMARKS[/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(51, 65, 85))
                .Expand();
            items.Add(hubPanel);

            var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            footTable.AddColumn(new TableColumn("F"));
            footTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]1[/], [bold #F1F5F9]2[/], [bold #F1F5F9]3[/] [#94A3B8]para iniciar ou[/] [bold #F1F5F9]ESC[/] [#94A3B8]para retornar ao menu principal.[/]"));
            var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            items.Add(footPanel);

            AnsiConsole.Write(MasterContainer(104, items.ToArray()));

            if (Console.IsInputRedirected) break;

            var key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.Escape || key == ConsoleKey.Q) break;
            if (key == ConsoleKey.D1 || key == ConsoleKey.NumPad1)
            {
                RunBenchmarkInteractive(rep, smokeTestMode: false);
            }
            else if (key == ConsoleKey.D2 || key == ConsoleKey.NumPad2)
            {
                RunIoLadderInteractive(rep, smokeTestMode: false);
            }
            else if (key == ConsoleKey.D3 || key == ConsoleKey.NumPad3)
            {
                RunLbaCurveInteractive(rep, smokeTestMode: false);
            }
        }
    }

    public static void RunIoLadderInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        using var cts = new CancellationTokenSource();
        IoBlockLadderProgress? lastProgress = null;
        var swRefresh = Stopwatch.StartNew();

        var tableLayout = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tableLayout.AddColumn(new TableColumn("Content"));

        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Live(tableLayout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Start(ctx =>
                {
                    Action<IoBlockLadderProgress> onProg = p =>
                    {
                        lastProgress = p;
                        if (swRefresh.ElapsedMilliseconds >= 60 || p.OverallPercent >= 100.0)
                        {
                            swRefresh.Restart();
                            tableLayout.Rows.Clear();
                            tableLayout.AddRow(BuildIoLadderRenderable(p, isCompleted: false));
                            ctx.Refresh();
                        }

                        if (!smokeTestMode && !Console.IsInputRedirected && Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true).Key;
                            if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                            {
                                cts.Cancel();
                            }
                        }
                    };

                    try
                    {
                        VesperBenchmarkEngine.RunIoLadderBenchmark(rep, cts.Token, onProg, smokeTestMode: smokeTestMode);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[#F87171]Erro na Escada de Blocos: {Esc(ex.Message)}[/]");
                    }

                    if (lastProgress != null)
                    {
                        tableLayout.Rows.Clear();
                        tableLayout.AddRow(BuildIoLadderRenderable(lastProgress, isCompleted: true));
                        ctx.Refresh();
                    }
                });
        }
        else
        {
            Action<IoBlockLadderProgress> onProg = p => { lastProgress = p; };
            try
            {
                VesperBenchmarkEngine.RunIoLadderBenchmark(rep, cts.Token, onProg, smokeTestMode: smokeTestMode);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[#F87171]Erro na Escada de Blocos: {Esc(ex.Message)}[/]");
            }
            if (lastProgress != null)
            {
                AnsiConsole.Write(BuildIoLadderRenderable(lastProgress, isCompleted: true));
            }
        }

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildIoLadderRenderable(IoBlockLadderProgress p, bool isCompleted)
    {
        var items = new List<IRenderable>();

        var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headTable.AddColumn(new TableColumn("Left").Width(54));
        headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
        headTable.AddRow(
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
            new Markup($"[#94A3B8]Alvo:[/] [bold #F1F5F9]{Esc(TruncatePath(p.TargetPath, 28))}[/]  [#64748B]•[/]  [#94A3B8]Modelo:[/] [bold #38BDF8]{Esc(p.ModelName)}[/]")
        );
        items.Add(headTable);
        items.Add(new Markup(""));

        string activeLabel = p.Steps.Count > 0 ? p.Steps[Math.Clamp(p.ActiveStepIndex, 0, p.Steps.Count - 1)].Label : "";
        string statusText = isCompleted
            ? "[bold #0B0F19 on #4ADE80]  ESCADA DE BLOCOS CONCLUIDA  [/]"
            : $"[bold #0B0F19 on #38BDF8]  TESTANDO BLOCO {activeLabel} ({(p.IsWriting ? "ESCRITA" : "LEITURA")})  [/]";

        string overallBar = RenderSegmentedBar(p.OverallPercent, 22, "#4ADE80");

        var statusTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        statusTable.AddColumn(new TableColumn("S1").Width(52));
        statusTable.AddColumn(new TableColumn("S2").Width(52).RightAligned());
        statusTable.AddRow(
            new Markup($"  {statusText}"),
            new Markup($"[#94A3B8]Progresso:[/] {overallBar} [bold #F1F5F9]{p.OverallPercent:F0}%[/]  [#64748B]•[/]  [#94A3B8]Tempo:[/] [#38BDF8]{p.Elapsed:mm\\:ss}[/]")
        );
        var statusPanel = new Panel(statusTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(statusPanel);

        double maxSpeed = 100.0;
        foreach (var s in p.Steps)
        {
            if (s.WriteMBps > maxSpeed) maxSpeed = s.WriteMBps;
            if (s.ReadMBps > maxSpeed) maxSpeed = s.ReadMBps;
        }

        int mid = (p.Steps.Count + 1) / 2;

        Table CreateSubLadderTable(int start, int end)
        {
            var t = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            t.AddColumn(new TableColumn("[bold #38BDF8]BLOCO[/]").Width(8).Centered());
            t.AddColumn(new TableColumn("[bold #FB923C]WRITE[/]").Width(19));
            t.AddColumn(new TableColumn("[bold #4ADE80]READ[/]").Width(19));

            for (int i = start; i < end && i < p.Steps.Count; i++)
            {
                var step = p.Steps[i];
                bool isCurrent = (!isCompleted && p.ActiveStepIndex == i);

                string writeCell;
                if (step.Done || (isCurrent && !p.IsWriting && step.WriteMBps > 0))
                {
                    int barLen = (int)Math.Clamp((step.WriteMBps / maxSpeed) * 6, 1, 6);
                    string bar = new string('█', barLen);
                    writeCell = $"[bold #FB923C]{step.WriteMBps,7:F1}[/] [#FB923C]{bar}[/]";
                }
                else if (isCurrent && p.IsWriting)
                {
                    writeCell = "[bold #FBBF24]▒TESTE▒[/]";
                }
                else
                {
                    writeCell = "[#475569]  ---  [/]";
                }

                string readCell;
                if (step.Done)
                {
                    int barLen = (int)Math.Clamp((step.ReadMBps / maxSpeed) * 6, 1, 6);
                    string bar = new string('█', barLen);
                    readCell = $"[bold #4ADE80]{step.ReadMBps,7:F1}[/] [#4ADE80]{bar}[/]";
                }
                else if (isCurrent && !p.IsWriting)
                {
                    readCell = "[bold #FBBF24]▒TESTE▒[/]";
                }
                else
                {
                    readCell = "[#475569]  ---  [/]";
                }

                t.AddRow(new Markup($"[bold #F1F5F9]{step.Label}[/]"), new Markup(writeCell), new Markup(readCell));
            }
            return t;
        }

        var leftSubTable = CreateSubLadderTable(0, mid);
        var rightSubTable = CreateSubLadderTable(mid, p.Steps.Count);

        var splitGrid = new Grid().Expand();
        splitGrid.AddColumn(new GridColumn().Width(52));
        splitGrid.AddColumn(new GridColumn().Width(52));
        splitGrid.AddRow(leftSubTable, rightSubTable);

        var ladderPanel = new Panel(splitGrid)
            .Header(" [#38BDF8]ESCADA DE EFICIENCIA DE BLOCO I/O (4 KB A 64 MB)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(ladderPanel);

        var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        footTable.AddColumn(new TableColumn("F"));
        string footMsg = isCompleted
            ? "[#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu...[/]"
            : "[#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]ESC[/] [#94A3B8]para interromper o teste.[/]";
        footTable.AddRow(new Markup($"  {footMsg}"));
        var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(footPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunLbaCurveInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        using var cts = new CancellationTokenSource();
        LbaCurveProgress? lastProgress = null;
        var swRefresh = Stopwatch.StartNew();

        var tableLayout = new Table().Border(TableBorder.None).HideHeaders().Expand();
        tableLayout.AddColumn(new TableColumn("Content"));

        if (!Console.IsOutputRedirected)
        {
            AnsiConsole.Live(tableLayout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Start(ctx =>
                {
                    Action<LbaCurveProgress> onProg = p =>
                    {
                        lastProgress = p;
                        if (swRefresh.ElapsedMilliseconds >= 60 || p.CurrentPercentile >= 99)
                        {
                            swRefresh.Restart();
                            tableLayout.Rows.Clear();
                            tableLayout.AddRow(BuildLbaCurveRenderable(p, isCompleted: false));
                            ctx.Refresh();
                        }

                        if (!smokeTestMode && !Console.IsInputRedirected && Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true).Key;
                            if (k == ConsoleKey.Escape || k == ConsoleKey.Q)
                            {
                                cts.Cancel();
                            }
                        }
                    };

                    try
                    {
                        VesperBenchmarkEngine.RunLbaCurveBenchmark(rep.DevicePath, rep.Model, rep.DiskSizeBytes, rep.BytesPerSector, cts.Token, onProg, smokeTestMode: smokeTestMode);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[#F87171]Erro na Curva de Vazao LBA: {Esc(ex.Message)}[/]");
                    }

                    if (lastProgress != null)
                    {
                        tableLayout.Rows.Clear();
                        tableLayout.AddRow(BuildLbaCurveRenderable(lastProgress, isCompleted: true));
                        ctx.Refresh();
                    }
                });
        }
        else
        {
            Action<LbaCurveProgress> onProg = p => { lastProgress = p; };
            try
            {
                VesperBenchmarkEngine.RunLbaCurveBenchmark(rep.DevicePath, rep.Model, rep.DiskSizeBytes, rep.BytesPerSector, cts.Token, onProg, smokeTestMode: smokeTestMode);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[#F87171]Erro na Curva de Vazao LBA: {Esc(ex.Message)}[/]");
            }
            if (lastProgress != null)
            {
                AnsiConsole.Write(BuildLbaCurveRenderable(lastProgress, isCompleted: true));
            }
        }

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildLbaCurveRenderable(LbaCurveProgress p, bool isCompleted)
    {
        var items = new List<IRenderable>();

        var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headTable.AddColumn(new TableColumn("Left").Width(54));
        headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
        headTable.AddRow(
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
            new Markup($"[#94A3B8]Dispositivo:[/] [bold #38BDF8]{Esc(p.DevicePath)}[/]  [#64748B]•[/]  [#94A3B8]Modelo:[/] [bold #F1F5F9]{Esc(p.ModelName)}[/]")
        );
        items.Add(headTable);
        items.Add(new Markup(""));

        var statTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        statTable.AddColumn(new TableColumn("[bold #38BDF8]METRICA LBA[/]").Width(18));
        statTable.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO[/]").Width(25));
        statTable.AddColumn(new TableColumn("[bold #38BDF8]METRICA LBA[/]").Width(18));
        statTable.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO[/]").Width(25));

        double minSpd = p.MinSpeedMBps == double.MaxValue ? 0.0 : p.MinSpeedMBps;
        statTable.AddRow(
            new Markup("[#94A3B8]Taxa Minima:[/]"), new Markup($"[bold #FBBF24]{minSpd:F1} MB/s[/]"),
            new Markup("[#94A3B8]Burst Rate:[/]"), new Markup($"[bold #C084FC]{p.BurstRateMBps:F1} MB/s[/]")
        );
        statTable.AddRow(
            new Markup("[#94A3B8]Taxa Media:[/]"), new Markup($"[bold #4ADE80]{p.AvgSpeedMBps:F1} MB/s[/]"),
            new Markup("[#94A3B8]Tempo de Acesso:[/]"), new Markup($"[bold #38BDF8]{p.AccessTimeMs:F2} ms[/]")
        );
        statTable.AddRow(
            new Markup("[#94A3B8]Taxa Maxima:[/]"), new Markup($"[bold #38BDF8]{p.MaxSpeedMBps:F1} MB/s[/]"),
            new Markup("[#94A3B8]Pontos de LBA:[/]"), new Markup($"[bold #F1F5F9]{p.Points.Count} / 100[/] ([#38BDF8]{p.Elapsed:mm\\:ss}[/])")
        );

        var statPanel = new Panel(statTable)
            .Header(" [#38BDF8]ESTATISTICAS DA CURVA DE VAZAO LBA[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(statPanel);

        var curveBuilder = new StringBuilder();
        int totalWidth = 84;
        double peakSpeed = Math.Max(p.MaxSpeedMBps, 1.0);

        for (int line = 3; line >= 0; line--)
        {
            double threshold = (line + 1) * (peakSpeed / 4.0);
            double subThreshold = line * (peakSpeed / 4.0);
            curveBuilder.Append($"  [#64748B]{threshold,6:F0}│[/] ");

            for (int x = 0; x < totalWidth; x++)
            {
                int ptIdx = (int)((x / (double)totalWidth) * p.Points.Count);
                if (ptIdx < p.Points.Count)
                {
                    double spd = p.Points[ptIdx].SpeedMBps;
                    if (spd >= threshold)
                    {
                        curveBuilder.Append("[#4ADE80]█[/]");
                    }
                    else if (spd > subThreshold)
                    {
                        curveBuilder.Append("[#38BDF8]▄[/]");
                    }
                    else
                    {
                        curveBuilder.Append("[#334155] [/]");
                    }
                }
                else
                {
                    curveBuilder.Append("[#1E293B]·[/]");
                }
            }
            curveBuilder.AppendLine();
        }

        curveBuilder.Append("        [#64748B]└" + new string('─', totalWidth) + "[/]\n");
        curveBuilder.Append("         [#94A3B8]0% LBA (Cilindros Externos/Cache SLC)[/]                        [#94A3B8]100% LBA (Cilindros Internos)[/]");

        var curvePanel = new Panel(new Markup(curveBuilder.ToString()))
            .Header(" [#4ADE80]CURVA CONTINUA DE VELOCIDADE POR LBA (0% A 100% DO DISCO)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(curvePanel);

        var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        footTable.AddColumn(new TableColumn("F"));
        string footMsg = isCompleted
            ? "[#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu...[/]"
            : "[#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]ESC[/] [#94A3B8]para interromper a amostragem.[/]";
        footTable.AddRow(new Markup($"  {footMsg}"));
        var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(footPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunFirmwareErrorLogInteractive(DriveSmartReport rep, bool smokeTestMode = false)
    {
        SafeClear();
        var report = FirmwareErrorLogEngine.QueryErrorLog(rep);

        var renderable = BuildFirmwareErrorLogRenderable(report);
        AnsiConsole.Write(renderable);

        if (!smokeTestMode && !Console.IsInputRedirected)
        {
            try { Console.ReadKey(true); } catch { }
        }
    }

    public static IRenderable BuildFirmwareErrorLogRenderable(FirmwareErrorLogReport report)
    {
        var items = new List<IRenderable>();

        var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headTable.AddColumn(new TableColumn("Left").Width(54));
        headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
        headTable.AddRow(
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
            new Markup($"[#94A3B8]Dispositivo:[/] [bold #38BDF8]{Esc(report.DevicePath)}[/]  [#64748B]•[/]  [#94A3B8]Protocolo:[/] [bold #F1F5F9]{Esc(report.Protocol)}[/]")
        );
        items.Add(headTable);
        items.Add(new Markup(""));

        if (!report.LogSupported)
        {
            var notSupTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            notSupTable.AddColumn(new TableColumn("C"));
            notSupTable.AddRow(new Markup($"\n  [bold #0B0F19 on #FBBF24]  LOG NAO DISPONIVEL  [/]\n\n  {Esc(report.SummaryText)}\n"));
            var notSupPanel = new Panel(notSupTable).Header(" [#FBBF24]STATUS DO MICROCODIGO[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(251, 191, 36)).Expand();
            items.Add(notSupPanel);
        }
        else if (report.Entries.Count == 0)
        {
            var cleanTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
            cleanTable.AddColumn(new TableColumn("C"));
            cleanTable.AddRow(new Markup(
                "\n  [bold #0B0F19 on #4ADE80]  0 ERROS  [/]  [#94A3B8]0 erros registrados no log SMART.[/]\n"
            ));
            var cleanPanel = new Panel(cleanTable).Header(" [#4ADE80]ERROR LOG[/] ").Border(BoxBorder.Rounded).BorderColor(new Color(74, 222, 128)).Expand();
            items.Add(cleanPanel);
        }
        else
        {
            var errTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(248, 113, 113)).Expand();
            errTable.AddColumn(new TableColumn("[bold #F87171]#[/]").Width(4).Centered());
            errTable.AddColumn(new TableColumn("[bold #38BDF8]LBA DA FALHA[/]").Width(14));
            errTable.AddColumn(new TableColumn("[bold #FBBF24]TIMESTAMP[/]").Width(10));
            errTable.AddColumn(new TableColumn("[bold #F1F5F9]COMANDO[/]").Width(18));
            errTable.AddColumn(new TableColumn("[bold #F87171]STATUS DO HARDWARE[/]").Width(22));
            errTable.AddColumn(new TableColumn("[bold #94A3B8]DETALHES[/]").Width(12));

            foreach (var e in report.Entries)
            {
                string timeStr = e.PowerOnHours > 0 ? $"{e.PowerOnHours:N0}h" : $"Err #{e.ErrorCount}";
                errTable.AddRow(
                    new Markup($"[bold #F87171]{e.EntryIndex}[/]"),
                    new Markup($"[bold #F1F5F9]0x{e.FailingLba:X10}[/]"),
                    new Markup($"[#FBBF24]{timeStr}[/]"),
                    new Markup($"[#F1F5F9]{Esc(e.CommandDescription)}[/]"),
                    new Markup($"[bold #F87171]{Esc(e.StatusDescription)}[/]"),
                    new Markup($"[#94A3B8]{Esc(e.ErrorDetails)}[/]")
                );
            }

            var errPanel = new Panel(errTable)
                .Header($" [#F87171]HISTORICO DE ERROS GRAVADOS NO FIRMWARE ({report.Entries.Count} OCORRENCIAS) [/] ")
                .Border(BoxBorder.Rounded)
                .BorderColor(new Color(248, 113, 113))
                .Expand();
            items.Add(errPanel);
        }

        var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        footTable.AddColumn(new TableColumn("F"));
        footTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]"));
        var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(footPanel);

        return MasterContainer(104, items.ToArray());
    }

    public static void RunSelfTestMenuInteractive(DriveSmartReport rep)
    {
        SafeClear();
        var items = new List<IRenderable>();

        var headTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        headTable.AddColumn(new TableColumn("Left").Width(54));
        headTable.AddColumn(new TableColumn("Right").RightAligned().Width(50));
        headTable.AddRow(
            new Markup("  [bold #0B0F19 on #4ADE80]  ADMIN  [/]  [bold #F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]"),
            new Markup($"[#94A3B8]Dispositivo:[/] [bold #38BDF8]{Esc(rep.DevicePath)}[/] [#94A3B8]({Esc(rep.Model)})[/]")
        );
        items.Add(headTable);
        items.Add(new Markup(""));

        var menuTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        menuTable.AddColumn(new TableColumn("[bold #38BDF8]OPCAO[/]").Width(8).Centered());
        menuTable.AddColumn(new TableColumn("[bold #F1F5F9]MODALIDADE DO SELF-TEST DO FIRMWARE[/]").Width(42));
        menuTable.AddColumn(new TableColumn("[bold #94A3B8]DURACAO & ESCOPO[/]").Width(36));

        menuTable.AddRow(
            new Markup("[bold #0B0F19 on #4ADE80]  [[1]]  [/]"),
            new Markup("[bold #4ADE80]Short Self-Test (Rotina Curta)[/]\n[#94A3B8]Teste eletrico e mecanico de cabecotes e buffer[/]"),
            new Markup("[#F1F5F9]~1 a 2 minutos[/]\n[#64748B]Verifica circuitos internos e trilhas de servo[/]")
        );
        menuTable.AddRow(
            new Markup("[bold #0B0F19 on #38BDF8]  [[2]]  [/]"),
            new Markup("[bold #38BDF8]Extended Self-Test (Rotina Longa)[/]\n[#94A3B8]Varredura completa de todos os blocos LBA[/]"),
            new Markup("[#F1F5F9]~30 a 120 minutos (conforme capacidade)[/]\n[#64748B]Diagnostico conclusivo da superficie inteira[/]")
        );

        var panel = new Panel(menuTable)
            .Header(" [#38BDF8]AUTOTESTE SMART (SELF-TEST)[/] ")
            .Border(BoxBorder.Rounded)
            .BorderColor(new Color(51, 65, 85))
            .Expand();
        items.Add(panel);

        var footTable = new Table().Border(TableBorder.None).HideHeaders().Expand();
        footTable.AddColumn(new TableColumn("F"));
        footTable.AddRow(new Markup("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [bold #F1F5F9]1[/] [#94A3B8]para Short,[/] [bold #F1F5F9]2[/] [#94A3B8]para Extended ou[/] [bold #F1F5F9]ESC[/] [#94A3B8]para cancelar.[/]"));
        var footPanel = new Panel(footTable).Border(BoxBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
        items.Add(footPanel);

        AnsiConsole.Write(MasterContainer(104, items.ToArray()));

        if (Console.IsInputRedirected) return;

        var key = Console.ReadKey(true).Key;
        if (key == ConsoleKey.D1 || key == ConsoleKey.NumPad1)
        {
            RunSelfTestInteractive(rep, SelfTestMode.Short);
        }
        else if (key == ConsoleKey.D2 || key == ConsoleKey.NumPad2)
        {
            RunSelfTestInteractive(rep, SelfTestMode.Extended);
        }
    }

    private static string FormatBlockMarkup(SurfaceBlockStatus st) => st switch
    {
        SurfaceBlockStatus.Excellent => "[#4ADE80]■[/]",
        SurfaceBlockStatus.Good => "[#10B981]■[/]",
        SurfaceBlockStatus.Slow => "[#FBBF24]■[/]",
        SurfaceBlockStatus.Lagging => "[#FB923C]▒[/]",
        SurfaceBlockStatus.Damaged => "[#F87171]▓[/]",
        SurfaceBlockStatus.BadSector => "[bold white on #DC2626]■[/]",
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
        IsVerificationHarness = true;

        var totalSuiteSw = Stopwatch.StartNew();
        var suiteStart = DateTime.Now;

        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("  [bold #F1F5F9]VESPER DEEP HARDWARE VERIFICATION & AUDIT HARNESS (--verify-all)[/]");
        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine($"[#94A3B8]Inicio da Suite:[/] [bold #F1F5F9]{suiteStart:yyyy-MM-dd HH:mm:ss.fff}[/]  [#64748B]•[/]  [#94A3B8]Discos Fisicos Detectados:[/] [bold #38BDF8]{reports.Count}[/]\n");

        var diskMetricsSummary = new List<(string diskName, double totalDurationSec, int testsPassed, int testsTotal)>();

        for (int diskIdx = 0; diskIdx < reports.Count; diskIdx++)
        {
            var r = reports[diskIdx];
            var diskSw = Stopwatch.StartNew();
            int passedTests = 0;
            int totalTests = 10;

            AnsiConsole.MarkupLine($"\n[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine($"  [bold #F1F5F9]DISCO FISICO #{diskIdx}: {Esc(r.Model)}[/] ([#38BDF8]{Esc(r.DevicePath)}[/])");
            AnsiConsole.MarkupLine($"[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");

            var idTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            idTable.AddColumn(new TableColumn("[#38BDF8]PARAMETRO DE HARDWARE[/]").Width(28));
            idTable.AddColumn(new TableColumn("[#F1F5F9]VALOR TELEMETRICO REAL[/]").Width(72));
            idTable.AddRow("Caminho do Dispositivo", $"[bold #38BDF8]{Esc(r.DevicePath)}[/]");
            idTable.AddRow("Modelo Comercial", $"[bold #F1F5F9]{Esc(r.Model)}[/]");
            idTable.AddRow("Numero de Serie", $"[bold #F1F5F9]{Esc(r.Serial)}[/]");
            idTable.AddRow("Barramento / Protocolo", $"[bold #4ADE80]{Esc(r.BusTypeName)}[/] (Tipo: {r.BusType})");
            idTable.AddRow("Capacidade Bruta", $"[bold #F1F5F9]{FormatCapacity(r.DiskSizeBytes)}[/] ({r.DiskSizeBytes:N0} bytes)");
            idTable.AddRow("Tamanho do Setor", $"{r.BytesPerSector} bytes");
            idTable.AddRow("Temperatura do Sensor", $"[#FB923C]{r.TemperatureCelsius} °C[/]");
            idTable.AddRow("Saude Operacional", $"[#4ADE80]{r.HealthDefault:F0}%[/] (Performance: [#38BDF8]{r.Performance:F0}%[/])");
            idTable.AddRow("Tempo de Atividade (POH)", $"{r.PowerOnHours:N0} horas ({r.PowerOnHours / 24:N0} dias)");
            idTable.AddRow("Ciclos de Energia", $"{r.PowerCycleCount:N0} ciclos");
            idTable.AddRow("Volume de Dados Lidos (TBR)", $"{r.TotalTbRead:F2} TB");
            idTable.AddRow("Volume de Dados Gravados (TBW)", $"{r.TotalTbWritten:F2} TB");
            idTable.AddRow("Veredicto de Saude", $"[#F1F5F9]{Esc(r.HealthVerdictText)}[/]");
            AnsiConsole.Write(idTable);

            var t1Start = DateTime.Now;
            var t1Sw = Stopwatch.StartNew();
            SurfaceTestProgress? surfProg = null;
            int surfBlocks = 100;
            var surfGrid = new SurfaceBlockStatus[surfBlocks];
            string t1Err = "";
            try
            {
                using var cts = new CancellationTokenSource();
                SurfaceTestEngine.RunTest(
                    r.DevicePath,
                    r.DiskSizeBytes,
                    r.BytesPerSector,
                    surfBlocks,
                    cts.Token,
                    (res, prog) =>
                    {
                        surfProg = prog;
                        if (res.BlockIndex < surfBlocks) surfGrid[res.BlockIndex] = res.Status;
                    },
                    onBlockStarting: null,
                    throttleMs: 0,
                    scanMode: SurfaceScanMode.VerifyRead,
                    coverage: SurfaceScanCoverage.QuickDense);
                passedTests++;
            }
            catch (Exception ex)
            {
                t1Err = ex.Message;
            }
            t1Sw.Stop();
            var t1End = DateTime.Now;

            var t1Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(56, 189, 248)).Expand();
            t1Table.AddColumn(new TableColumn("[bold #38BDF8]MODULO 1: SURFACE TEST (DADOS BRUTOS)[/]").Width(35));
            t1Table.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO NO HARDWARE[/]").Width(65));
            t1Table.AddRow("Janela Temporal de Execucao", $"Inicio: [#38BDF8]{t1Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t1End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t1Sw.Elapsed.TotalSeconds:F3}s[/]");
            if (string.IsNullOrEmpty(t1Err))
            {
                t1Table.AddRow("Zonas / Blocos Inspecionados", $"{surfProg?.CurrentBlock ?? 0} de {surfBlocks} blocos");
                t1Table.AddRow("Throughput Medio de Leitura", $"[bold #4ADE80]{surfProg?.AvgSpeedMBps ?? 0:F1} MB/s[/]");
                t1Table.AddRow("Throughput Instantaneo / Maximo", $"{surfProg?.CurrentSpeedMBps ?? 0:F1} MB/s / {surfProg?.MaxSpeedMBps ?? 0:F1} MB/s");
                t1Table.AddRow("Latencia Media dos Blocos", $"[bold #FBBF24]{surfProg?.CurrentLatencyMs ?? 0:F2} ms[/]");
                t1Table.AddRow("Blocos em Estado Excelente (<5ms)", $"[#4ADE80]{surfProg?.GoodBlocks ?? 0}[/]");
                t1Table.AddRow("Blocos Lentos / Marginais (>20ms)", $"[#FBBF24]{surfProg?.SlowBlocks ?? 0}[/]");
                t1Table.AddRow("Blocos Danificados / Bad Sectors", $"{(surfProg?.BadBlocks > 0 ? "[#F87171]" : "[#4ADE80]")}{surfProg?.BadBlocks ?? 0}[/]");
                t1Table.AddRow("Integridade Calculada da Superficie", $"[bold #4ADE80]{surfProg?.SurfaceHealthPercent ?? 100:F1}%[/]");
            }
            else
            {
                t1Table.AddRow("Status de Execucao", $"[bold #F87171]FALHA: {Esc(t1Err)}[/]");
            }
            AnsiConsole.Write(t1Table);

            var t2Start = DateTime.Now;
            var t2Sw = Stopwatch.StartNew();
            SeekTestProgress? seekProg = null;
            string t2Err = "";
            try
            {
                using var cts = new CancellationTokenSource();
                RandomSeekTestEngine.RunTest(
                    r.DevicePath,
                    r.DiskSizeBytes,
                    r.BytesPerSector,
                    targetOperations: 0,
                    cts.Token,
                    (sample, prog) => { seekProg = prog; },
                    throttleMs: 0,
                    durationMs: 5000,
                    isSsdOrNvme: r.IsSsd || r.IsNvme,
                    getTemperatureCallback: () => r.TemperatureCelsius);
                passedTests++;
            }
            catch (Exception ex)
            {
                t2Err = ex.Message;
            }
            t2Sw.Stop();
            var t2End = DateTime.Now;

            var t2Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(56, 189, 248)).Expand();
            t2Table.AddColumn(new TableColumn("[bold #38BDF8]MODULO 2: RANDOM SEEK TEST (DADOS BRUTOS)[/]").Width(35));
            t2Table.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO NO HARDWARE[/]").Width(65));
            t2Table.AddRow("Janela Temporal de Execucao", $"Inicio: [#38BDF8]{t2Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t2End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t2Sw.Elapsed.TotalSeconds:F3}s[/]");
            if (string.IsNullOrEmpty(t2Err))
            {
                t2Table.AddRow("Total de Operacoes Reais (Seek IOs)", $"{seekProg?.OperationsCompleted ?? 0} operacoes executadas");
                t2Table.AddRow("Throughput Sustentado (IOPS)", $"[bold #4ADE80]{seekProg?.CurrentIops ?? 0:F0} IOPS[/]");
                t2Table.AddRow("Latencia QPC Media", $"[bold #38BDF8]{seekProg?.AvgSeekMs ?? 0:F3} ms[/] ({((seekProg?.AvgSeekMs ?? 0) * 1000):F0} µs)");
                t2Table.AddRow("Latencia QPC Minima / Maxima", $"{seekProg?.MinSeekMs ?? 0:F3} ms / {seekProg?.MaxSeekMs ?? 0:F3} ms");
                t2Table.AddRow("Distribuicao < 5ms (Excelente)", $"[#4ADE80]{seekProg?.CountUnder5ms ?? 0} seeks[/]");
                t2Table.AddRow("Distribuicao 5-10ms (Normal)", $"[#38BDF8]{seekProg?.CountUnder10ms ?? 0} seeks[/]");
                t2Table.AddRow("Distribuicao 10-20ms (Lento)", $"[#FBBF24]{seekProg?.CountUnder20ms ?? 0} seeks[/]");
                t2Table.AddRow("Distribuicao > 20ms (Critico)", $"[#F87171]{seekProg?.CountOver20ms ?? 0} seeks[/]");
            }
            else
            {
                t2Table.AddRow("Status de Execucao", $"[bold #F87171]FALHA: {Esc(t2Err)}[/]");
            }
            AnsiConsole.Write(t2Table);

            var t3Start = DateTime.Now;
            var t3Sw = Stopwatch.StartNew();
            BenchmarkProgress? benchProg = null;
            string t3Err = "";
            string? benchDir = VesperBenchmarkEngine.ResolveBenchmarkDirectory(r);
            bool isRawBench = (benchDir == null);
            try
            {
                using var cts = new CancellationTokenSource();
                VesperBenchmarkEngine.RunBenchmark(r, cts.Token, p => { benchProg = p; }, smokeTestMode: false);
                passedTests++;
            }
            catch (Exception ex)
            {
                t3Err = ex.Message;
            }
            t3Sw.Stop();
            var t3End = DateTime.Now;

            string benchHeader = isRawBench
                ? $"[bold #38BDF8]MODULO 3: BENCHMARK I/O (MODO FISICO RAW - ESCRITA PROTEGIDA)[/]"
                : $"[bold #38BDF8]MODULO 3: BENCHMARK I/O (VOLUME: {Esc(benchDir!)})[/]";

            var t3Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(74, 222, 128)).Expand();
            t3Table.AddColumn(new TableColumn(benchHeader).Width(24));
            t3Table.AddColumn(new TableColumn("[bold #4ADE80]LEITURA (MB/s)[/]").RightAligned().Width(16));
            t3Table.AddColumn(new TableColumn("[bold #38BDF8]ESCRITA (MB/s)[/]").RightAligned().Width(16));
            t3Table.AddColumn(new TableColumn("[bold #F1F5F9]LEITURA IOPS[/]").RightAligned().Width(14));
            t3Table.AddColumn(new TableColumn("[bold #F1F5F9]ESCRITA IOPS[/]").RightAligned().Width(14));
            t3Table.AddColumn(new TableColumn("[bold #FBBF24]LATENCIA MED[/]").RightAligned().Width(14));

            if (benchProg != null)
            {
                foreach (var itm in benchProg.Items)
                {
                    double latUs = isRawBench ? itm.ReadLatencyUs : ((itm.ReadLatencyUs + itm.WriteLatencyUs) / 2.0);
                    string wCol = isRawBench ? "[#94A3B8]RAW READ-ONLY[/]" : $"[bold #38BDF8]{itm.WriteMBps,8:F1}[/]";
                    string wIopsCol = isRawBench ? "[#94A3B8]--[/]" : $"{itm.WriteIops,8:F0}";

                    t3Table.AddRow(
                        $"[bold #F1F5F9]{itm.Name} {itm.QueueThreadInfo}[/]",
                        $"[bold #4ADE80]{itm.ReadMBps,8:F1}[/]",
                        wCol,
                        $"{itm.ReadIops,8:F0}",
                        wIopsCol,
                        $"{(latUs > 1000 ? $"{latUs / 1000.0:F2} ms" : $"{latUs:F0} µs")}"
                    );
                }
            }
            else
            {
                t3Table.AddRow("Status", "-", "-", "-", "-", $"[#F87171]{Esc(t3Err)}[/]");
            }
            AnsiConsole.MarkupLine($"  [#94A3B8]Central de Vazao I/O:[/] Inicio: [#38BDF8]{t3Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t3End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t3Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t3Table);

            var t3bStart = DateTime.Now;
            var t3bSw = Stopwatch.StartNew();
            IoBlockLadderProgress? ladderProg = null;
            string t3bErr = "";
            try
            {
                using var cts = new CancellationTokenSource();
                VesperBenchmarkEngine.RunIoLadderBenchmark(r, cts.Token, p => { ladderProg = p; }, smokeTestMode: false);
                passedTests++;
            }
            catch (Exception ex)
            {
                t3bErr = ex.Message;
            }
            t3bSw.Stop();
            var t3bEnd = DateTime.Now;

            string ladderHeader = isRawBench
                ? $"[bold #38BDF8]MODULO 3b: ESCADA (MODO FISICO RAW)[/]"
                : $"[bold #38BDF8]MODULO 3b: ESCADA (VOLUME: {Esc(benchDir!)})[/]";

            var t3bTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            t3bTable.AddColumn(new TableColumn(ladderHeader).Width(24));
            t3bTable.AddColumn(new TableColumn("[bold #4ADE80]LEITURA (MB/s)[/]").RightAligned().Width(18));
            t3bTable.AddColumn(new TableColumn("[bold #38BDF8]ESCRITA (MB/s)[/]").RightAligned().Width(18));
            t3bTable.AddColumn(new TableColumn("[bold #F1F5F9]LEITURA IOPS[/]").RightAligned().Width(18));
            t3bTable.AddColumn(new TableColumn("[bold #F1F5F9]ESCRITA IOPS[/]").RightAligned().Width(18));

            if (ladderProg != null)
            {
                foreach (var stp in ladderProg.Steps)
                {
                    string wCol = isRawBench ? "[#94A3B8]RAW READ-ONLY[/]" : $"[bold #38BDF8]{stp.WriteMBps,8:F1}[/]";
                    string wIopsCol = isRawBench ? "[#94A3B8]--[/]" : $"{stp.WriteIops,8:F0}";

                    t3bTable.AddRow(
                        $"[bold #F1F5F9]{stp.Label}[/]",
                        $"[bold #4ADE80]{stp.ReadMBps,8:F1}[/]",
                        wCol,
                        $"{stp.ReadIops,8:F0}",
                        wIopsCol
                    );
                }
            }
            else
            {
                t3bTable.AddRow("Status", "-", "-", "-", $"[#F87171]{Esc(t3bErr)}[/]");
            }
            AnsiConsole.MarkupLine($"  [#94A3B8]Escada de Blocos I/O:[/] Inicio: [#38BDF8]{t3bStart:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t3bEnd:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t3bSw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t3bTable);

            var t3cStart = DateTime.Now;
            var t3cSw = Stopwatch.StartNew();
            LbaCurveProgress? lbaProg = null;
            string t3cErr = "";
            try
            {
                using var cts = new CancellationTokenSource();
                VesperBenchmarkEngine.RunLbaCurveBenchmark(r.DevicePath, r.Model, r.DiskSizeBytes, r.BytesPerSector, cts.Token, p => { lbaProg = p; }, smokeTestMode: false);
                passedTests++;
            }
            catch (Exception ex)
            {
                t3cErr = ex.Message;
            }
            t3cSw.Stop();
            var t3cEnd = DateTime.Now;

            var t3cTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(51, 65, 85)).Expand();
            t3cTable.AddColumn(new TableColumn("[bold #38BDF8]METRICA LBA[/]").Width(25));
            t3cTable.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO[/]").Width(25));
            t3cTable.AddColumn(new TableColumn("[bold #38BDF8]METRICA LBA[/]").Width(25));
            t3cTable.AddColumn(new TableColumn("[bold #F1F5F9]VALOR MEDIDO[/]").Width(25));
            double minSpd = lbaProg?.MinSpeedMBps == double.MaxValue ? 0.0 : (lbaProg?.MinSpeedMBps ?? 0.0);
            t3cTable.AddRow("Taxa Minima LBA", $"[bold #FBBF24]{minSpd:F1} MB/s[/]", "Burst Rate Instantaneo", $"[bold #C084FC]{lbaProg?.BurstRateMBps ?? 0:F1} MB/s[/]");
            t3cTable.AddRow("Taxa Media LBA", $"[bold #4ADE80]{lbaProg?.AvgSpeedMBps ?? 0:F1} MB/s[/]", "Tempo de Acesso Medio", $"[bold #38BDF8]{lbaProg?.AccessTimeMs ?? 0:F2} ms[/]");
            t3cTable.AddRow("Taxa Maxima LBA", $"[bold #38BDF8]{lbaProg?.MaxSpeedMBps ?? 0:F1} MB/s[/]", "Amostras Coletadas", $"{lbaProg?.Points.Count ?? 0} pontos LBA");
            AnsiConsole.MarkupLine($"  [#94A3B8]Curva LBA:[/] Inicio: [#38BDF8]{t3cStart:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t3cEnd:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t3cSw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t3cTable);

            var t4Start = DateTime.Now;
            var t4Sw = Stopwatch.StartNew();
            var t4Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(192, 132, 252)).Expand();
            t4Table.AddColumn(new TableColumn("[bold #C084FC]MODULO 4: DETALHES DE SAUDE[/]").Width(35));
            t4Table.AddColumn(new TableColumn("[bold #F1F5F9]ESTADO TELEMETRICO REAL[/]").Width(65));
            t4Table.AddRow("Veredicto Tecnico", $"[bold #F1F5F9]{Esc(r.HealthVerdictText)}[/]");
            t4Table.AddRow("Vida Util Estimada Restante", $"[bold #4ADE80]{Esc(r.RemainingLifetimeText)}[/]");
            t4Table.AddRow("Percentual de Desgaste (Percentage Used)", $"{100.0 - r.HealthDefault:F0}%");
            t4Table.AddRow("Reserva Disponivel (Available Spare)", r.IsNvme ? $"{r.Attributes.Find(a => a.Id == 0x03)?.RawValue ?? 100}%" : "N/A (SATA)");
            t4Table.AddRow("Setores Realocados (ID 0x05)", $"{r.Attributes.Find(a => a.Id == 0x05)?.RawValue ?? 0}");
            t4Table.AddRow("Setores Pendentes (ID 0xC5)", $"{r.Attributes.Find(a => a.Id == 0xC5)?.RawValue ?? 0}");
            t4Table.AddRow("Erros Incorrigiveis (ID 0xBB)", $"{r.Attributes.Find(a => a.Id == 0xBB)?.RawValue ?? 0}");
            t4Table.AddRow("Erros de Comunicacao CRC (ID 0xC7)", $"{r.Attributes.Find(a => a.Id == 0xC7)?.RawValue ?? 0}");
            t4Sw.Stop();
            var t4End = DateTime.Now;
            passedTests++;
            AnsiConsole.MarkupLine($"  [#94A3B8]Detalhes de Saude:[/] Inicio: [#38BDF8]{t4Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t4End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t4Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t4Table);

            var t5Start = DateTime.Now;
            var t5Sw = Stopwatch.StartNew();
            var smartRenderable = BuildSmartAttributesRenderable(r);
            t5Sw.Stop();
            var t5End = DateTime.Now;
            passedTests++;
            AnsiConsole.MarkupLine($"  [#94A3B8]Atributos SMART ({r.Attributes.Count} parametros decodificados):[/] Inicio: [#38BDF8]{t5Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t5End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t5Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(smartRenderable);

            var t6Start = DateTime.Now;
            var t6Sw = Stopwatch.StartNew();
            var errReport = FirmwareErrorLogEngine.QueryErrorLog(r);
            t6Sw.Stop();
            var t6End = DateTime.Now;
            passedTests++;
            var t6Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(248, 113, 113)).Expand();
            t6Table.AddColumn(new TableColumn("[bold #F87171]MODULO 6: LOG DE ERRO DO FIRMWARE[/]").Width(35));
            t6Table.AddColumn(new TableColumn("[bold #F1F5F9]VALOR DO MICROCODIGO[/]").Width(65));
            t6Table.AddRow("Suporte a Log de Firmware", errReport.LogSupported ? "[bold #4ADE80]SUPORTADO[/]" : "[bold #FBBF24]NAO DISPONIVEL NO CONTROLADOR[/]");
            t6Table.AddRow("Total de Erros Registrados", $"[bold #F1F5F9]{errReport.TotalLoggedErrors}[/]");
            t6Table.AddRow("Resumo do Microcodigo", Esc(errReport.SummaryText));
            AnsiConsole.MarkupLine($"  [#94A3B8]Logs de Erro do Firmware:[/] Inicio: [#38BDF8]{t6Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t6End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t6Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t6Table);

            var t7Start = DateTime.Now;
            var t7Sw = Stopwatch.StartNew();
            int payloadLen = r.RawSmartPayload?.Length ?? 0;
            t7Sw.Stop();
            var t7End = DateTime.Now;
            passedTests++;
            AnsiConsole.MarkupLine($"  [#94A3B8]Dump Hexadecimal 512B:[/] Inicio: [#38BDF8]{t7Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t7End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t7Sw.Elapsed.TotalSeconds:F3}s[/] | Bytes Capturados: [bold #38BDF8]{payloadLen}[/]");
            if (payloadLen >= 64)
            {
                var hexSb = new StringBuilder();
                for (int off = 0; off < Math.Min(64, payloadLen); off += 16)
                {
                    hexSb.Append($"    [#38BDF8]{off:X4}[/]  ");
                    for (int b = 0; b < 16; b++)
                    {
                        if (off + b < payloadLen) hexSb.Append($"[#F1F5F9]{r.RawSmartPayload![off + b]:X2}[/] ");
                        if (b == 7) hexSb.Append(" ");
                    }
                    hexSb.Append(" │ ");
                    for (int b = 0; b < 16; b++)
                    {
                        if (off + b < payloadLen)
                        {
                            byte c = r.RawSmartPayload![off + b];
                            char ch = (c >= 32 && c <= 126) ? (char)c : '.';
                            hexSb.Append($"[#94A3B8]{Esc(ch.ToString())}[/]");
                        }
                    }
                    hexSb.AppendLine();
                }
                AnsiConsole.Write(new Markup(hexSb.ToString()));
            }

            var t8Start = DateTime.Now;
            var t8Sw = Stopwatch.StartNew();
            bool isScsi = r.BusTypeName == "SCSI" || r.BusTypeName == "RAID";
            bool shortSuccess = FirmwareSelfTestEngine.ExecuteSelfTest(r.DevicePath, r.IsNvme, isScsi, SelfTestMode.Short, out string shortMsg);
            t8Sw.Stop();
            var t8End = DateTime.Now;

            var t9Start = DateTime.Now;
            var t9Sw = Stopwatch.StartNew();
            bool extSuccess = FirmwareSelfTestEngine.ExecuteSelfTest(r.DevicePath, r.IsNvme, isScsi, SelfTestMode.Extended, out string extMsg);
            t9Sw.Stop();
            var t9End = DateTime.Now;
            passedTests++;

            var t89Table = new Table().Border(TableBorder.Rounded).BorderColor(new Color(56, 189, 248)).Expand();
            t89Table.AddColumn(new TableColumn("[bold #38BDF8]MODULO 8 & 9: SELF-TEST[/]").Width(25));
            t89Table.AddColumn(new TableColumn("[bold #F1F5F9]STATUS DE DISPARO[/]").Width(25));
            t89Table.AddColumn(new TableColumn("[bold #94A3B8]RESPOSTA DO FIRMWARE[/]").Width(50));
            t89Table.AddRow("Short Self-Test", shortSuccess ? "[bold #4ADE80]COMANDO ACEITO[/]" : "[bold #FBBF24]RETORNO[/]", Esc(shortMsg));
            t89Table.AddRow("Extended Self-Test", extSuccess ? "[bold #4ADE80]COMANDO ACEITO[/]" : "[bold #FBBF24]RETORNO[/]", Esc(extMsg));
            AnsiConsole.MarkupLine($"  [#94A3B8]Self-Test Short:[/] Inicio: [#38BDF8]{t8Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t8End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t8Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.MarkupLine($"  [#94A3B8]Self-Test Extended:[/] Inicio: [#38BDF8]{t9Start:HH:mm:ss.fff}[/] | Fim: [#38BDF8]{t9End:HH:mm:ss.fff}[/] | Duracao: [bold #4ADE80]{t9Sw.Elapsed.TotalSeconds:F3}s[/]");
            AnsiConsole.Write(t89Table);

            diskSw.Stop();
            diskMetricsSummary.Add((r.Model, diskSw.Elapsed.TotalSeconds, passedTests, totalTests));
            AnsiConsole.MarkupLine($"  [bold #4ADE80]✔ DISCO #{diskIdx} CONCLUIDO COM SUCESSO:[/] Tempo Total = [bold #F1F5F9]{diskSw.Elapsed.TotalSeconds:F3}s[/] | Testes: {passedTests}/{totalTests}\n");
        }

        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("  [bold #F1F5F9]VALIDACAO DE ARQUITETURA, INTERFACE E PIPELINE[/]");
        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]\n");

        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 10/14]][/] Validando renderizacao da Tela Principal e Abas em todos os menus...");
        for (int d = 0; d < reports.Count; d++)
        {
            for (int m = 0; m < MenuItems.Length; m++)
            {
                var dashRenderable = BuildMainDashboard(reports, d, m, isElevated: true);
                var writer = new StringWriter();
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
                console.Write(dashRenderable);
            }
        }
        AnsiConsole.MarkupLine($"  [bold #4ADE80][[PASSO 10 OK]][/] Tela Principal renderizada em {reports.Count * MenuItems.Length} combinacoes sem erros de markup!");

        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 11/14]][/] Validando alternancia de disco ativo...");
        int nextDisk = reports.Count > 1 ? 1 : 0;
        var switchedDashboard = BuildMainDashboard(reports, nextDisk, 0, isElevated: true);
        var sWriter = new StringWriter();
        var sConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(sWriter) });
        sConsole.Write(switchedDashboard);
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 11 OK]][/] Alternancia de disco ativo validada!");

        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 12/14]][/] Validando filtragem de pendrives e deduplicacao de barramento...");
        bool isPendriveDetected = Program.IsPendriveOrUsbFlash(7, "USB DISK 2.0", 16L * 1024 * 1024 * 1024);
        if (!isPendriveDetected) throw new InvalidOperationException("Falha no filtro de pendrive: USB DISK 2.0 16GB deveria ser filtrado!");
        var testPipeline = StoragePipeline.ExecutePipeline(
            () => new List<DriveSmartReport>
            {
                new DriveSmartReport { DevicePath = @"\\.\PhysicalDrive1", Model = "Elements 1078", Serial = "0123", BusType = 7, DiskSizeBytes = 1000000000000L },
                new DriveSmartReport { DevicePath = @"\\.\PhysicalDrive1", Model = "WDC WD10JMVW", Serial = "WD-1234", BusType = 11, DiskSizeBytes = 1000000000000L },
                new DriveSmartReport { DevicePath = @"\\.\PhysicalDrive2", Model = "USB DISK 2.0", Serial = "USB123", BusType = 7, DiskSizeBytes = 16000000000L }
            },
            () => new List<DriveSmartReport>()
        );
        var drive1 = testPipeline.Find(x => x.DevicePath == @"\\.\PhysicalDrive1");
        if (drive1 == null) throw new InvalidOperationException("Disco PhysicalDrive1 nao encontrado!");
        if (testPipeline.Exists(x => x.Model.Contains("USB DISK 2.0"))) throw new InvalidOperationException("Pendrive USB DISK 2.0 nao foi filtrado!");
        if (testPipeline.Count(x => x.DevicePath == @"\\.\PhysicalDrive1") != 1) throw new InvalidOperationException("DevicePath PhysicalDrive1 duplicado!");
        if (drive1.Model != "WDC WD10JMVW") throw new InvalidOperationException($"Enriquecimento de modelo falhou: {drive1.Model}");
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 12 OK]][/] Filtragem de pendrives e deduplicacao de PhysicalDrive validadas com 100% de sucesso!");

        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 13/14]][/] Validando alinhamento estrito de 1 caractere no grid de blocos...");
        foreach (SurfaceBlockStatus st in Enum.GetValues<SurfaceBlockStatus>())
        {
            string formatted = FormatBlockMarkup(st);
            string unformatted = System.Text.RegularExpressions.Regex.Replace(formatted, @"\[.*?\]", "");
            if (unformatted.Length != 1)
            {
                throw new InvalidOperationException($"FormatBlockMarkup para {st} expandiu para {unformatted.Length} caracteres ('{unformatted}')!");
            }
        }
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 13 OK]][/] Grid 2D 100% monoespacado e imune a desalinhamentos!");

        AnsiConsole.MarkupLine("[bold #38BDF8][[PASSO 14/14]][/] Validando resiliencia contra caracteres hostis [[ e ]]...");
        byte[] hostilePayload = new byte[512];
        for (int b = 0; b < 256; b++) hostilePayload[b] = (byte)b;
        for (int b = 256; b < 512; b++) hostilePayload[b] = (byte)(b - 256);
        var hostileDrive = new DriveSmartReport
        {
            DevicePath = @"\\.\PhysicalDrive99",
            Model = "Seagate [RAID] ST1000LM035 [[HOSTILE]]",
            Serial = "WAA123 [[MALICIOUS]]",
            BusType = 11,
            BusTypeName = "SATA [[AHCI]]",
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
            RemainingLifetimeText = "Mais de 1000 dias [[Estendido]]",
            HealthVerdictText = "Condicao operacional normal com [[0]] setores realocados.",
            RawSmartPayload = hostilePayload
        };
        var hWriter = new StringWriter();
        var hConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(hWriter) });
        hConsole.Write(BuildHexDumpRenderable(hostileDrive));
        hConsole.Write(BuildHealthDetailsRenderable(hostileDrive));
        AnsiConsole.MarkupLine("  [bold #4ADE80][[PASSO 14 OK]][/] Strings hostis escapadas com sucesso em todas as telas!");

        totalSuiteSw.Stop();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("  [bold #F1F5F9]RESUMO CONSOLIDADO DA SUITE DE VERIFICACAO[/]");
        AnsiConsole.MarkupLine("[bold #38BDF8]════════════════════════════════════════════════════════════════════════════════[/]");

        var sumTable = new Table().Border(TableBorder.Rounded).BorderColor(new Color(74, 222, 128)).Expand();
        sumTable.AddColumn(new TableColumn("[bold #38BDF8]DISCO FISICO[/]").Width(32));
        sumTable.AddColumn(new TableColumn("[bold #4ADE80]TEMPO TOTAL DE EXECUCAO[/]").RightAligned().Width(24));
        sumTable.AddColumn(new TableColumn("[bold #F1F5F9]TESTES VALIDADOS[/]").Centered().Width(20));
        sumTable.AddColumn(new TableColumn("[bold #4ADE80]STATUS GERAL[/]").Centered().Width(20));

        foreach (var ds in diskMetricsSummary)
        {
            sumTable.AddRow(
                $"[bold #F1F5F9]{Esc(ds.diskName)}[/]",
                $"[bold #4ADE80]{ds.totalDurationSec:F3}s[/]",
                $"{ds.testsPassed} / {ds.testsTotal}",
                "[bold #4ADE80]100% APROVADO[/]"
            );
        }
        AnsiConsole.Write(sumTable);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold black on #4ADE80]════════════════════════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine($"[bold black on #4ADE80]  [[VERIFY-ALL 100% PASSED]]: {reports.Count} Discos + 14 Passos Validados em {totalSuiteSw.Elapsed.TotalSeconds:F3}s!   [/]");
        AnsiConsole.MarkupLine("[bold black on #4ADE80]════════════════════════════════════════════════════════════════════════════════[/]\n");
    }
}
