using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

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

    public static int GetVisibleLength(string line)
    {
        int len = 0;
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '[' && (i + 1) < line.Length)
            {
                int closeIdx = line.IndexOf(']', i);
                if (closeIdx > i)
                {
                    string tag = line.Substring(i + 1, closeIdx - i - 1);
                    if (tag == "/" || tag.StartsWith("#") || tag.StartsWith("bg:") || tag.Contains("#"))
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
        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Fundo escuro industrial
        using (var bgBrush = new SolidBrush(HexToColor("0B0F19")))
        {
            g.FillRectangle(bgBrush, 0, 0, width, height);
        }

        // Barra de Título Windows Terminal
        using (var titleBarBrush = new SolidBrush(HexToColor("06090E")))
        {
            g.FillRectangle(titleBarBrush, 0, 0, width, 38);
        }

        using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        var titleSize = g.MeasureString(title, titleFont);
        float tabWidth = Math.Max(190, titleSize.Width + 65);

        // Aba Ativa
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

            // Controles de Janela
            g.DrawString("─", titleFont, grayBrush, width - 85, 12);
            g.DrawString("□", titleFont, grayBrush, width - 55, 12);
            g.DrawString("✕", titleFont, grayBrush, width - 25, 12);
        }

        using (var borderPen = new Pen(HexToColor("21262D"), 1))
        {
            g.DrawLine(borderPen, 0, 38, width, 38);
        }

        using var termFont = new Font("Consolas", 10.5f, FontStyle.Regular);
        float colWidth = 9.8f;
        float rowHeight = 20.0f;
        Color defaultFg = HexToColor("CBD5E1");
        float startX = 30.0f;
        float startY = 52.0f;

        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            float curY = startY + (row * rowHeight);

            int i = 0;
            int col = 0;
            Color curFg = defaultFg;
            Color? curBg = null;

            while (i < line.Length)
            {
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
                            i = closeIdx + 1;
                            continue;
                        }
                        else if (tag.Contains("#") && !tag.StartsWith("bg:"))
                        {
                            int hashIdx = tag.IndexOf('#');
                            string hex = tag.Substring(hashIdx, Math.Min(7, tag.Length - hashIdx));
                            curFg = HexToColor(hex);
                            curBg = null;
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

                int nextTag = -1;
                int searchIdx = i + 1;
                while (searchIdx < line.Length)
                {
                    int candidate = line.IndexOf('[', searchIdx);
                    if (candidate < 0) break;
                    int candidateClose = line.IndexOf(']', candidate);
                    if (candidateClose > candidate)
                    {
                        string candTag = line.Substring(candidate + 1, candidateClose - candidate - 1);
                        if (candTag == "/" || candTag.StartsWith("#") || candTag.StartsWith("bg:"))
                        {
                            nextTag = candidate;
                            break;
                        }
                    }
                    searchIdx = candidate + 1;
                }

                string textPart = nextTag >= 0 ? line.Substring(i, nextTag - i) : line.Substring(i);
                i = nextTag >= 0 ? nextTag : line.Length;

                if (textPart.Length > 0)
                {
                    if (curBg.HasValue)
                    {
                        float bgWidth = textPart.Length * colWidth;
                        using var bBrush = new SolidBrush(curBg.Value);
                        g.FillRectangle(bBrush, startX + (col * colWidth), curY + 1, bgWidth, rowHeight - 1);
                    }

                    using var fBrush = new SolidBrush(curFg);
                    for (int c = 0; c < textPart.Length; c++)
                    {
                        char ch = textPart[c];
                        float x = startX + ((col + c) * colWidth);
                        g.DrawString(ch.ToString(), termFont, fBrush, x, curY);
                    }

                    col += textPart.Length;
                }
            }
        }

        using (var windowPen = new Pen(HexToColor("30363D"), 1))
        {
            g.DrawRectangle(windowPen, 0, 0, width - 1, height - 1);
        }

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bmp.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"Screenshot salvo: {outputPath}");
    }

    private static string[] BuildDashboardLines()
    {
        var lines = new List<string>();

        // 1. Logo & Badge (Opção D: Cyber Slant with Bottom Cut 50 cols - Depth Gradient: Fundo Magenta -> Frente Ciano)
        lines.Add("  [#F472B6]___    __________________________________________ [/]   [bg:#4ADE80 fg:#0B0F19]  ADMIN  [/]  [#F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]");
        lines.Add("  [#C084FC]__ |  / /__  ____/_  ___/__  __ \\__  ____/__  __ \\/]");
        lines.Add("  [#818CF8]__ | / /__  __/  _____ \\__  /_/ /_  __/  __  /_/ /[/]");
        lines.Add("  [#60A5FA]__ |/ / _  /___  ____/ /_  ____/_  /___  _  _, _/ [/]");
        lines.Add("  [#38BDF8]_____/  /_____/  /____/ /_/     /_____/  /_/ |_|  [/]");
        lines.Add("  [#38BDF8]┌─[/] [bg:#38BDF8 fg:#0B0F19] ● #0 ATA SanDisk SSD PLUS (240 GB) [/] [#38BDF8]─┐[/]   [#334155]┌─[/] [#64748B] #1 Seagate Expansion (4,0 TB) [/] [#334155]─┐[/]");

        // 2. Telemetry Box (Width 102 inner, 104 total)
        int teleInner = 102;
        string teleTitle = "TELEMETRIA";
        int teleDashes = teleInner - 1 - teleTitle.Length;
        lines.Add("  [#334155]╭─[/][#38BDF8]" + teleTitle + "[/][#334155]" + new string('─', teleDashes) + "╮[/]");
        
        string line1 = "  [#94A3B8]Dispositivo:[/] [#F1F5F9]\\\\.\\PhysicalDrive0[/]       [#334155]│[/]   [#94A3B8]Modelo:[/]      [#F1F5F9]ATA SanDisk SSD PLUS[/]";
        lines.Add("  [#334155]│[/]" + PadRightVisible(line1, teleInner) + "[#334155]│[/]");

        string line2 = "  [#94A3B8]Interface:[/]   [#F1F5F9]SATA[/]                       [#334155]│[/]   [#94A3B8]Temperatura:[/] [#38BDF8][[[/][#FB923C]||||||||[/][#334155]::::::::[/][#38BDF8]][/] [#FB923C]31 °C[/]";
        lines.Add("  [#334155]│[/]" + PadRightVisible(line2, teleInner) + "[#334155]│[/]");

        string line3 = "  [#94A3B8]Saude:[/]       [#38BDF8][[[/][#4ADE80]||||||||||||||||[/][#38BDF8]][/] [#4ADE80]96% OK[/]  [#334155]│[/]   [#94A3B8]Desempenho:[/]  [#38BDF8][[[/][#38BDF8]||||||||||||||||[/][#38BDF8]][/] [#38BDF8]100% OTIMO[/]";
        lines.Add("  [#334155]│[/]" + PadRightVisible(line3, teleInner) + "[#334155]│[/]");

        string line4 = "  [#94A3B8]Tempo Oper.:[/]  [#F1F5F9]698h (29d)[/]                   [#334155]│[/]   [#94A3B8]Ciclos L/D:[/]  [#F1F5F9]94 ciclos[/]";
        lines.Add("  [#334155]│[/]" + PadRightVisible(line4, teleInner) + "[#334155]│[/]");

        lines.Add("  [#334155]╰" + new string('─', teleInner) + "╯[/]");
        lines.Add("");

        // 3. Middle Split (Left Menu: 36 inner, Right Block Map: 64 inner, Soma: 104)
        int leftInner = 36;
        int rightInner = 64;

        string menuTitle = "MODULOS";
        int menuDashes = leftInner - 1 - menuTitle.Length;
        string menuTop = "  [#334155]╭─[/][#38BDF8]" + menuTitle + "[/][#334155]" + new string('─', Math.Max(0, menuDashes)) + "╮[/]";

        string blockTitle = "SUPERFICIE (PREVIEW)";
        int blockDashes = rightInner - 1 - blockTitle.Length;
        string blockTop = "[#334155]╭─[/][#38BDF8]" + blockTitle + "[/][#334155]" + new string('─', Math.Max(0, blockDashes)) + "╮[/]";

        lines.Add(menuTop + blockTop);

        string[] menuItems = {
            "[bg:#38BDF8 fg:#0B0F19]  ● 1. Surface Test                 [/]",
            "    [#38BDF8]●[/]  [#E2E8F0]2. Random Seek Test[/]",
            "    [#C084FC]●[/]  [#E2E8F0]3. Detalhes de Saude[/]",
            "    [#FBBF24]●[/]  [#E2E8F0]4. Atributos S.M.A.R.T.[/]",
            "    [#94A3B8]●[/]  [#E2E8F0]5. Dump Hexadecimal[/]",
            "    [#38BDF8]●[/]  [#E2E8F0]6. Short Self-Test[/]",
            "    [#38BDF8]●[/]  [#E2E8F0]7. Extended Self-Test[/]",
            "    [#F87171]●[/]  [#E2E8F0]8. Sair do VESPER[/]",
            "",
            "    [#64748B]Navegue com [↑/↓] ou [1-8][/]",
            "    [#64748B]Aperte [Tab] p/ alternar disco[/]"
        };

        string[] blockRows = {
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "  [#4ADE80]■[/] [#94A3B8]OK (<50ms)[/]   [#FBBF24]■[/] [#94A3B8]Lento (>150ms)[/]   [#F87171]■[/] [#94A3B8]Bad Sector[/]",
            "  [#94A3B8]Status:[/] [#4ADE80]Integra[/]  [#64748B]•[/]  [#94A3B8]Blocos:[/] [#4ADE80]224 OK[/]  [#64748B]•[/]  [#94A3B8]Bad:[/] [#4ADE80]0[/]",
            "  [#64748B]Escala: 1 bloco = 1022 MB (2.093.056 setores LBA)[/]"
        };

        for (int r = 0; r < 11; r++)
        {
            string mRow = "  [#334155]│[/]" + PadRightVisible(menuItems[r], leftInner) + "[#334155]│[/]";
            string bRow = "[#334155]│[/]" + PadRightVisible(blockRows[r], rightInner) + "[#334155]│[/]";
            lines.Add(mRow + bRow);
        }

        string menuBottom = "  [#334155]╰" + new string('─', leftInner) + "╯[/]";
        string blockBottom = "[#334155]╰" + new string('─', rightInner) + "╯[/]";
        lines.Add(menuBottom + blockBottom);
        lines.Add("");

        // 4. Dock Box Padronizada (Width 102 inner, 104 total)
        lines.Add("  [#334155]╭" + new string('─', teleInner) + "╮[/]");
        string dockContent = "  [#38BDF8]>[/]  [#38BDF8][[[/] [#F1F5F9]↑/↓[/] [#94A3B8]Selecionar[/] [#38BDF8]][/]   [#38BDF8][[[/] [#F1F5F9]Enter[/] [#94A3B8]Iniciar[/] [#38BDF8]][/]   [#38BDF8][[[/] [#F1F5F9]Tab[/] [#94A3B8]Alternar[/] [#38BDF8]][/]   [#38BDF8][[[/] [#F1F5F9]Esc[/] [#94A3B8]Sair[/] [#38BDF8]][/]                                 [#64748B]@_fxgiovane[/]";
        lines.Add("  [#334155]│[/]" + PadRightVisible(dockContent, teleInner) + "[#334155]│[/]");
        lines.Add("  [#334155]╰" + new string('─', teleInner) + "╯[/]");

        return lines.ToArray();
    }

    public static void GenerateAllScreenshots(string targetDir, string? gDriveDir = null)
    {
        Console.WriteLine("\n========================================================");
        Console.WriteLine("  GERANDO SCREENSHOTS REAIS DE ALTA FIDELIDADE (TUI)");
        Console.WriteLine("========================================================\n");

        string[] dashLines = BuildDashboardLines();

        int W = 102;
        var surfList = new List<string>
        {
            "  [bg:#4ADE80 fg:#0B0F19]  RUNNING  [/]  [#F1F5F9]SURFACE TEST (SAFE READ)[/]  [#64748B]•[/]  [#38BDF8]\\\\.\\PhysicalDrive0 (SAMSUNG MZVL4256HBJD-00B)[/]  [#94A3B8](4.2s)[/]",
            "",
            "  [#334155]╭─[/][#38BDF8]MAPA DE SUPERFICIE[/][#334155]" + new string('─', W - 1 - 39) + "╮[/]"
        };

        string[] surfBlockRows = {
            "   [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "   [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "   [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "   [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/]",
            "   [#4ADE80]■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ ■ [/][bg:#38BDF8 fg:#0B0F19] > [/][#334155] · · · · · · · · · · · · · · · · · · · · · · · ·[/]",
            "   [#334155]· · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · [/]",
            "   [#334155]· · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · [/]"
        };
        foreach (var bRow in surfBlockRows)
        {
            surfList.Add("  [#334155]│[/]" + PadRightVisible(bRow, W) + "[#334155]│[/]");
        }
        surfList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        surfList.Add("  [#94A3B8]Legenda:[/] [#4ADE80]■[/] <5ms  [#10B981]■[/] <20ms  [#FBBF24]■[/] <50ms  [#FB923C]▒[/] <150ms  [#F87171]▓[/] <500ms  [bg:#F87171 fg:#0B0F19] BAD [/]  [bg:#38BDF8 fg:#0B0F19] > [/] Scanner  [#334155]·[/] Pendente");
        surfList.Add("");

        surfList.Add("  [#334155]╭─[/][#38BDF8]METRICAS[/][#334155]" + new string('─', W - 1 - 31) + "╮[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Setores / Tamanho Bloco :[/] [#F1F5F9]500.118.192 setores | Bloco: 128 (64 KB)[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Distribuicao dos Blocos :[/] [#4ADE80]Bons: 242[/] [#64748B]•[/] [#FBBF24]Lentos: 0[/] [#64748B]•[/] [#FB923C]Danificados: 0[/] [#64748B]•[/] [#4ADE80]Bad: 0[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Throughput Instantaneo  :[/] [#38BDF8]324.7 MB/s[/] [#94A3B8](Min: 184 / Med: 302 / Max: 412 MB/s)[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Sparkline Throughput     :[/] [#38BDF8] ▂▃▄▅▆▇██▇▆▅▄▃▂  ▂▃▄▅▆▇██▇▆▅▄▃▂[/]   [#94A3B8]Latencia:[/] [#38BDF8]0.19 ms[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Progresso da Varredura  :[/] [#38BDF8]▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓[/][#334155]░░░░░░░[/] [#4ADE80]69.1%[/] [#94A3B8](Restante: 00:04)[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Inspecao LBA Atual      :[/] [#F1F5F9]Bloco 243 / 350 | LBA: 0x0017D8F558[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        surfList.Add("");

        surfList.Add("  [#334155]╭" + new string('─', W) + "╮[/]");
        surfList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [#F1F5F9][Esc][/] [#94A3B8]ou[/] [#F1F5F9][Q][/] [#94A3B8]para cancelar o teste a qualquer momento.[/]", W) + "[#334155]│[/]");
        surfList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");

        string[] surfLines = surfList.ToArray();

        var seekList = new List<string>
        {
            "  [bg:#4ADE80 fg:#0B0F19]  RUNNING  [/]  [#F1F5F9]RANDOM SEEK[/]  [#334155]•[/]  [#38BDF8]\\\\.\\PhysicalDrive0 (SAMSUNG..)[/]  [#64748B]•[/]  [#94A3B8]2.8s[/]",
            "",
            "  [#334155]╭─[/][#38BDF8]LATENCIA DE BUSCA (QPC)[/][#334155]" + new string('─', W - 1 - 25) + "╮[/]"
        };
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Seek Atual / Min:[/] [#38BDF8]0.23 ms[/] / [#4ADE80]0.02 ms[/]           [#94A3B8]Seek Med / Max:[/] [#4ADE80]0.16 ms[/] / [#F87171]0.54 ms[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Throughput IOPS :[/] [#4ADE80]6435 IOPS[/]                       [#94A3B8]Progresso / Alvo:[/] [#F1F5F9]500[/] / 500 ([#38BDF8]100.0%[/])", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        seekList.Add("");

        seekList.Add("  [#334155]╭─[/][#38BDF8]DISPERSAO DE LATENCIA E SPARKLINE[/][#334155]" + new string('─', W - 1 - 34) + "╮[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,54│[/] [#38BDF8]●[/]                                                                        ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,43│[/]                                                                          ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,32│[/]                                                                          ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,21│[/]               [#38BDF8]●[/]                                                          ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,11│[/]   [#38BDF8]●●●●● ●  ●[/]                                                             ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("    [#94A3B8]0,00│[/]  [#38BDF8]●     ● ●● ●●[/]                                                           ", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("        [#334155]└────────────────────────────────────────────────────────────────────────[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("         [#94A3B8]0         10        20        30        40        50        60        70[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#94A3B8]Historico:[/] [#38BDF8]█ ▃▃▃▂▂▂▃▂▂▂ ▂▃▂▂▂▂ ▃▃▂___▂▂▂▃▃▃▄▄▅▅▆▆▇▇██[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        seekList.Add("");

        seekList.Add("  [#334155]╭─[/][#38BDF8]DISTRIBUICAO DE LATENCIA (HISTOGRAMA)[/][#334155]" + new string('─', W - 1 - 39) + "╮[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#4ADE80]< 5ms (Excelente)[/]   [#4ADE80]████████████████████████████████████████████████[/]  [#F1F5F9]498[/] [#64748B](99.6%)[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]< 10ms (Normal)[/]    [#38BDF8]██[/]                                                [#F1F5F9]2[/] [#64748B](0.4%)[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#FBBF24]< 20ms (Lento)[/]                                                     [#F1F5F9]0[/] [#64748B](0.0%)[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#F87171]> 20ms (Marginal)[/]                                                  [#F1F5F9]0[/] [#64748B](0.0%)[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        seekList.Add("");

        seekList.Add("  [#334155]╭─[/][#38BDF8]LAUDO DE DESEMPENHO[/][#334155]" + new string('─', W - 1 - 20) + "╮[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [bg:#4ADE80 fg:#0B0F19]  DESEMPENHO EXCELENTE  [/]   [#4ADE80]Latencia media ultrarrapida (0.16 ms). Resposta imediata do drive SSD/NVMe.[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        seekList.Add("");

        seekList.Add("  [#334155]╭" + new string('─', W) + "╮[/]");
        seekList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]>[/] [#94A3B8]Pressione[/] [#F1F5F9][Esc][/] [#94A3B8]ou[/] [#F1F5F9][Q][/] [#94A3B8]para cancelar o teste a qualquer momento.[/]", W) + "[#334155]│[/]");
        seekList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");

        string[] seekLines = seekList.ToArray();

        string healthTitle = "DIAGNOSTICO DETALHADO DE SAUDE — SAMSUNG MZVL4256HBJD-00B (0025_3881_F111_ED2)";
        int healthDashes = Math.Max(0, 104 - 5 - healthTitle.Length);
        string healthSep = "  [#334155]├" + new string('─', 32) + "┼" + new string('─', 67) + "┤[/]";
        var healthList = new List<string>
        {
            "  [bg:#4ADE80 fg:#0F172A]  ADMIN  [/]  [#F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]",
            "",
            "  [#334155]╭─[/][#38BDF8]" + healthTitle + "[/][#334155]" + new string('─', healthDashes) + "╮[/]"
        };
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]PARAMETRO / IDENTIFICACAO[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]MEDICAO TELEMETRICA[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Dispositivo Fisico[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]\\\\.\\PhysicalDrive0[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Modelo do Drive[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]SAMSUNG MZVL4256HBJD-00B[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Numero de Serie[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0025_3881_F111_ED2[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Capacidade Total[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#38BDF8]256,0 GB[/] [#94A3B8](238,4 GiB / 256 GB)[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Interface / Midia[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]NVMe (PCI Express)[/] [#94A3B8]•[/] [#38BDF8]SSD NVMe[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Tamanho de Setor[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]512 bytes (Logico)[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Indice de Saude Geral[/]", 30) + " [#334155]│[/] " + PadRightVisible("[bg:#4ADE80 fg:#0F172A]  81% SAUDAVEL  [/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Indice de Desempenho[/]", 30) + " [#334155]│[/] " + PadRightVisible("[bg:#38BDF8 fg:#0F172A]  100% OTIMO  [/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Temperatura Operacional[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#38BDF8]41 °C[/] [#94A3B8](Faixa operacional segura: 0-60°C)[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Horas em Operacao (POH)[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]3.420 h (142 dias)[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Ciclos de Inicializacao[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]2.250 ciclos[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Total Bytes Gravados (TBW)[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#C084FC]33.87 TB[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Total Bytes Lidos (TBR)[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#C084FC]58.36 TB[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Estimativa de Vida Util[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]mais que 1000 dias[/]", 65) + " [#334155]│[/]");
        healthList.Add(healthSep);
        healthList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]Laudo Diagnostico[/]", 30) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]A condicao do SSD NVMe e estavel. Desgaste registrado: 19%.[/]", 65) + " [#334155]│[/]");
        healthList.Add("  [#334155]╰" + new string('─', 32) + "┴" + new string('─', 67) + "╯[/]");
        healthList.Add("");
        healthList.Add("  [#334155]╭" + new string('─', 102) + "╮[/]");
        healthList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]", 102) + "[#334155]│[/]");
        healthList.Add("  [#334155]╰" + new string('─', 102) + "╯[/]");
        string[] healthLines = healthList.ToArray();

        string smartTitle = "TABELA DE ATRIBUTOS S.M.A.R.T. — SAMSUNG MZVL4256HBJD-00B";
        int smartDashes = Math.Max(0, 104 - 5 - smartTitle.Length);
        var smartList = new List<string>
        {
            "  [bg:#4ADE80 fg:#0F172A]  ADMIN  [/]  [#F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]",
            "",
            "  [#334155]╭─[/][#38BDF8]" + smartTitle + "[/][#334155]" + new string('─', smartDashes) + "╮[/]"
        };
        string smartSep = "  [#334155]├" + new string('─', 8) + "┼" + new string('─', 26) + "┼" + new string('─', 20) + "┼" + new string('─', 13) + "┼" + new string('─', 29) + "┤[/]";
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#94A3B8]ID[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]PARAMETRO S.M.A.R.T. NVMe[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#38BDF8]VALOR BRUTO (RAW)[/]", 18) + " [#334155]│[/] " + PadRightVisible("[#FBBF24]STATUS[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]INTERPRETACAO DETALHADA[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x01[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Critical Warning Flags[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000000[/] (0)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Normal (0x00)[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x02[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Composite Temperature[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000029[/] (41)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]41 °C (314 K)[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x03[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Available Spare[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000064[/] (100)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]100% (Limiar: 10%)[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x05[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Percentage Used (Wear)[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000013[/] (19)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]19% usado (81% restante)[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x06[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Data Units Read[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000006CB4245[/] (113M)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]58.36 TB lidos[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x07[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Data Units Written[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000003F17566[/] (66M)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]33.87 TB gravados[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x0C[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Power On Hours[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000D5C[/] (3420)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]3.420 horas (142 dias)[/]", 28) + " [#334155]│[/]");
        smartList.Add(smartSep);
        smartList.Add("  [#334155]│[/] " + PadRightVisible("[#38BDF8]0x0E[/]", 6) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]Media & Integrity Errors[/]", 24) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0x000000000000[/] (0)", 18) + " [#334155]│[/] " + PadRightVisible("[#4ADE80]OK (Normal)[/]", 11) + " [#334155]│[/] " + PadRightVisible("[#F1F5F9]0 (Sem erros de bloco)[/]", 28) + " [#334155]│[/]");
        smartList.Add("  [#334155]╰" + new string('─', 8) + "┴" + new string('─', 26) + "┴" + new string('─', 20) + "┴" + new string('─', 13) + "┴" + new string('─', 29) + "╯[/]");
        smartList.Add("");
        smartList.Add("  [#334155]╭" + new string('─', 102) + "╮[/]");
        smartList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]", 102) + "[#334155]│[/]");
        smartList.Add("  [#334155]╰" + new string('─', 102) + "╯[/]");
        string[] smartLines = smartList.ToArray();

        var hexList = new List<string>
        {
            "  [bg:#4ADE80 fg:#0F172A]  ADMIN  [/]  [#F1F5F9]VESPER 1.0[/]  [#64748B]•[/]  [#38BDF8]@_fxgiovane[/]  [#64748B]•[/]  [#94A3B8]DUMP RAW (512B) \\\\.\\PhysicalDrive0[/]",
            "",
            "  [#334155]╭─[/][#38BDF8]DUMP RAW (512 BYTES)[/][#334155]" + new string('─', W - 1 - 22) + "╮[/]"
        };
        string[] hexDumps = {
            "   [#38BDF8]0000[/]  [#F1F5F9]00 3A 01 64 0A 13 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8].:.d............[/]",
            "   [#38BDF8]0010[/]  [#F1F5F9]00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]................[/]",
            "   [#38BDF8]0020[/]  [#F1F5F9]45 42 CB 06 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]EB..............[/]",
            "   [#38BDF8]0030[/]  [#F1F5F9]66 75 F1 03 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]fu..............[/]",
            "   [#38BDF8]0040[/]  [#F1F5F9]CB 90 AF A4 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]................[/]",
            "   [#38BDF8]0050[/]  [#F1F5F9]24 0C DC 2E 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]$...............[/]",
            "   [#38BDF8]0060[/]  [#F1F5F9]0B 0E 00 00 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]................[/]",
            "   [#38BDF8]0070[/]  [#F1F5F9]CA 08 00 00 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]................[/]",
            "   [#38BDF8]0080[/]  [#F1F5F9]5C 0D 00 00 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8]\\...............[/]",
            "   [#38BDF8]0090[/]  [#F1F5F9]3B 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00[/]  [#334155]│[/] [#94A3B8];...............[/]",
            "   [#38BDF8]00C0[/]  [#F1F5F9]00 00 00 00 00 00 00 00  3A 01 50 01 00 00 00 00[/]  [#334155]│[/] [#94A3B8]........:.P.....[/]"
        };
        foreach (var hd in hexDumps)
        {
            hexList.Add("  [#334155]│[/]" + PadRightVisible(hd, W) + "[#334155]│[/]");
        }
        hexList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        hexList.Add("");
        hexList.Add("  [#334155]╭" + new string('─', W) + "╮[/]");
        hexList.Add("  [#334155]│[/]" + PadRightVisible("  [#38BDF8]>[/] [#94A3B8]Pressione qualquer tecla para retornar ao menu principal...[/]", W) + "[#334155]│[/]");
        hexList.Add("  [#334155]╰" + new string('─', W) + "╯[/]");
        string[] hexLines = hexList.ToArray();

        RenderTerminalWindow("VESPER - Dashboard", dashLines, Path.Combine(targetDir, "screenshot_01_tui_dashboard.png"), 1280, 720);
        RenderTerminalWindow("VESPER - Surface Test", surfLines, Path.Combine(targetDir, "screenshot_02_surface_test.png"), 1200, 600);
        RenderTerminalWindow("VESPER - Seek Test", seekLines, Path.Combine(targetDir, "screenshot_03_seek_test.png"), 1200, 680);
        RenderTerminalWindow("VESPER - Health Details", healthLines, Path.Combine(targetDir, "screenshot_04_health_details.png"), 1200, 480);
        RenderTerminalWindow("VESPER - SMART Table", smartLines, Path.Combine(targetDir, "screenshot_05_smart_table.png"), 1200, 480);
        RenderTerminalWindow("VESPER - Hex Dump", hexLines, Path.Combine(targetDir, "screenshot_06_hex_dump.png"), 1200, 480);

        if (!string.IsNullOrEmpty(gDriveDir) && Directory.Exists(gDriveDir))
        {
            RenderTerminalWindow("VESPER - Dashboard", dashLines, Path.Combine(gDriveDir, "screenshot_01_tui_dashboard.png"), 1280, 720);
            RenderTerminalWindow("VESPER - Surface Test", surfLines, Path.Combine(gDriveDir, "screenshot_02_surface_test.png"), 1200, 600);
            RenderTerminalWindow("VESPER - Seek Test", seekLines, Path.Combine(gDriveDir, "screenshot_03_seek_test.png"), 1200, 680);
            RenderTerminalWindow("VESPER - Health Details", healthLines, Path.Combine(gDriveDir, "screenshot_04_health_details.png"), 1200, 480);
            RenderTerminalWindow("VESPER - SMART Table", smartLines, Path.Combine(gDriveDir, "screenshot_05_smart_table.png"), 1200, 480);
            RenderTerminalWindow("VESPER - Hex Dump", hexLines, Path.Combine(gDriveDir, "screenshot_06_hex_dump.png"), 1200, 480);
            Console.WriteLine($"Screenshots sincronizados com o Google Drive: {gDriveDir}");
        }

        Console.WriteLine("\nTodos os screenshots foram gerados com sucesso!");
    }
}
