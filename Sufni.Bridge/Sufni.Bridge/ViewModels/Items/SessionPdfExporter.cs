using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HapticFeedback;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.ViewModels.Items;

internal static class SessionPdfExporter
{
    // Rendered height of the zone-band block appended below a low-speed velocity histogram
    // page, mirroring VelocityBandView's layout (title text + 44px band grid + spacing).
    private const float PdfBandBlockHeight = 62f;
    private const float PdfBandLeftInset = 50f;   // Mirrors VelocityBandView's Margin="50,0,20,0"
    private const float PdfBandRightInset = 20f;
    private const float PdfBandGridHeight = 44f;

    internal static async Task ExportAsync(SessionViewModel viewModel, bool essential)
    {
        App.Current?.Services?.GetService<IHapticFeedback>()?.Click();
        viewModel.IsGeneratingPdf = true;
        try
        {
            var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
            Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

            var cache = await databaseService.GetSessionCacheAsync(viewModel.Id);
            if (cache is null)
            {
                viewModel.ErrorMessages.Add("No cached plots found. Open the session first.");
                return;
            }

            // Combined sessions don't cache the phase-portrait plots (CreateCache skips them,
            // see the isCombined gate there) — but the full report must still include the
            // fork/damper position-vs-velocity pages, so render them fresh from telemetry on
            // demand here. The essential report never includes these pages, so skip this
            // (potentially expensive) regeneration entirely for that variant.
            string? frontPosVelSvg = null;
            string? rearPosVelSvg = null;
            if (!essential)
            {
                frontPosVelSvg = cache.FrontPositionVelocity;
                rearPosVelSvg = cache.RearPositionVelocity;
                if (frontPosVelSvg is null || rearPosVelSvg is null)
                {
                    var pdfTelemetryData = await databaseService.GetSessionPsstAsync(viewModel.Id);
                    if (pdfTelemetryData is not null)
                    {
                        var session = viewModel.SessionModel;
                        if (session.CropStartSample.HasValue && session.CropEndSample.HasValue)
                            pdfTelemetryData = pdfTelemetryData.CreateCroppedCopy(
                                session.CropStartSample.Value, session.CropEndSample.Value);

                        var (pvWidth, pvHeight) = ((int)SessionViewModel.LastKnownBounds.Width,
                            (int)(SessionViewModel.LastKnownBounds.Height / 2.0));

                        if (frontPosVelSvg is null && pdfTelemetryData.Front.Present)
                        {
                            var fpv = new PositionVelocityPlot(new Plot(), SuspensionType.Front);
                            fpv.LoadTelemetryData(pdfTelemetryData);
                            frontPosVelSvg = fpv.Plot.GetSvgXml(pvWidth, pvHeight);
                        }
                        if (rearPosVelSvg is null && pdfTelemetryData.Rear.Present)
                        {
                            var rpv = new PositionVelocityPlot(new Plot(), SuspensionType.Rear);
                            rpv.LoadTelemetryData(pdfTelemetryData);
                            rearPosVelSvg = rpv.Plot.GetSvgXml(pvWidth, pvHeight);
                        }
                    }
                }
            }

            // Collect all SVG entries in tab display order, each tagged with whether it's part
            // of the reduced "essential" (customer) report. Entries for the low-speed velocity
            // histograms also carry the zone-percentage band data so RenderSvgsToPdf can render
            // the VelocityBandView equivalent below the plot on the same page.
            var svgEntries = new List<(PdfSvgEntry? Entry, bool Essential)>
            {
                // Spring tab
                (PdfSvgEntry.For(cache.TravelComparisonHistogram), true),
                (PdfSvgEntry.For(cache.FrontRearTravelScatter), true),
                (PdfSvgEntry.For(cache.FrontTravelHistogram), true),
                (PdfSvgEntry.For(cache.RearTravelHistogram), true),

                // Damper tab
                (PdfSvgEntry.For(cache.VelocityDistributionComparison), true),
                (PdfSvgEntry.For(cache.FrontVelocityHistogram), true),
                (PdfSvgEntry.For(cache.FrontLowSpeedVelocityHistogram, "Front Zone %",
                    cache.FrontHsrPercentage, cache.FrontLsrPercentage, cache.FrontLscPercentage, cache.FrontHscPercentage), true),
                (PdfSvgEntry.For(cache.RearVelocityHistogram), true),
                (PdfSvgEntry.For(cache.RearLowSpeedVelocityHistogram, "Rear Zone %",
                    cache.RearHsrPercentage, cache.RearLsrPercentage, cache.RearLscPercentage, cache.RearHscPercentage), true),
                (PdfSvgEntry.For(cache.RearDamperVelocityHistogram), false),

                // Balance tab
                (PdfSvgEntry.For(cache.CombinedTravelFft), false),
                (PdfSvgEntry.For(cache.CombinedTravelFftHigh), false),
                (PdfSvgEntry.For(cache.CombinedVelocityFft), false),
                (PdfSvgEntry.For(cache.CombinedBalance), true),
                (PdfSvgEntry.For(cache.CompressionBalance), true),
                (PdfSvgEntry.For(cache.ReboundBalance), true),
                (PdfSvgEntry.For(cache.PitchBalance), false),
                (PdfSvgEntry.For(cache.PitchCoherence), false),
                (PdfSvgEntry.For(cache.GoutScatter), false),
                (PdfSvgEntry.For(cache.CumulativeTravel), false),
                (PdfSvgEntry.For(frontPosVelSvg), false),
                (PdfSvgEntry.For(rearPosVelSvg), false),

                // Misc tab (time-series: travel -> velocity -> acceleration, front/rear)
                (PdfSvgEntry.For(cache.FrontTravelTimeCropped), false),
                (PdfSvgEntry.For(cache.RearTravelTimeCropped), false),
                (PdfSvgEntry.For(cache.FrontVelocityTimeCropped), false),
                (PdfSvgEntry.For(cache.RearVelocityTimeCropped), false),
                (PdfSvgEntry.For(cache.FrontAccelerationTimeCropped), false),
                (PdfSvgEntry.For(cache.RearAccelerationTimeCropped), false),
            };

            var validSvgs = svgEntries
                .Where(s => s.Entry is not null && (!essential || s.Essential))
                .Select(s => s.Entry!)
                .ToList();
            if (validSvgs.Count == 0)
            {
                viewModel.ErrorMessages.Add("No plots to export.");
                return;
            }

            var discipline = await viewModel.GetSessionDisciplineAsync();
            var pdfPath = await Task.Run(() => RenderSvgsToPdf(
                viewModel, validSvgs, viewModel.SummaryPage, viewModel.NotesPage, discipline?.ToString()));

            viewModel.IsGeneratingPdf = false;
            var shareService = App.Current?.Services?.GetService<IShareService>();
            if (shareService is not null)
                await shareService.ShareFileAsync(pdfPath);
        }
        catch (Exception e)
        {
            viewModel.IsGeneratingPdf = false;
            viewModel.ErrorMessages.Add($"PDF export failed: {e.Message}");
        }
    }

    private static string RenderSvgsToPdf(
        SessionViewModel viewModel,
        List<PdfSvgEntry> svgEntries,
        SummaryPageViewModel summary,
        NotesPageViewModel notes,
        string? discipline)
    {
        var tempDir = System.IO.Path.GetTempPath();
        // Strip characters that are invalid in filenames or URLs (space, #, %, &, etc.)
        var sanitizedName = System.Text.RegularExpressions.Regex.Replace(
            viewModel.Name ?? "session", @"[^\w\-.]", "_");
        var pdfPath = System.IO.Path.Combine(tempDir, $"{sanitizedName}.pdf");
        var svgXml = svgEntries.Select(entry => entry.Svg).ToList();

        return SkiaPdfWriter.WritePdf(pdfPath, svgXml, (document, svgObjects) =>
        {
            DrawSummaryPage(document, summary, discipline, (float)SessionViewModel.LastKnownBounds.Width);

            for (var i = 0; i < svgObjects.Count; i++)
            {
                var picture = svgObjects[i].Picture;
                if (picture is null) continue;

                var band = svgEntries[i].Band;
                var bounds = picture.CullRect;
                var pageHeight = bounds.Height + (band is not null ? PdfBandBlockHeight : 0f);
                using var canvas = document.BeginPage(bounds.Width, pageHeight);
                canvas.DrawPicture(picture);
                if (band is not null)
                    DrawVelocityBand(canvas, band, bounds.Width, bounds.Height, pageHeight);
                document.EndPage();
            }

            DrawNotesPage(document, notes, (float)SessionViewModel.LastKnownBounds.Width);
        });
    }

    private static void DrawSummaryPage(
        SkiaSharp.SKDocument document,
        SummaryPageViewModel summary,
        string? discipline,
        float pageWidth)
    {
        const float margin = 30f;
        const float rowH = 26f;
        const float titleH = 28f;
        const float sectionGap = 18f;
        const float fontSize = 11f;
        const float titleFontSize = 10f;

        float contentWidth = pageWidth - margin * 2f;
        float col0 = 95f;
        float col12 = (contentWidth - col0) / 2f;

        var bgColor       = SkiaSharp.SKColor.Parse("#15191c");
        var cellBg        = SkiaSharp.SKColor.Parse("#20262b");
        var headerBg      = SkiaSharp.SKColor.Parse("#66c2a5");
        var headerFg      = SkiaSharp.SKColor.Parse("#15191c");
        var cellFg        = SkiaSharp.SKColor.Parse("#a0a0a0");
        var borderColor   = SkiaSharp.SKColor.Parse("#505050");

        // Calculate total page height
        float pageHeight = margin * 2f
            + rowH + sectionGap
            + titleH + summary.RunDataRows.Count * rowH
            + sectionGap
            + rowH + summary.WheelRows.Count * rowH
            + sectionGap
            + rowH + summary.ForkShockRows.Count * rowH;

        using var canvas = document.BeginPage(pageWidth, pageHeight);
        canvas.Clear(bgColor);

        using var fillPaint   = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 0.75f, Color = borderColor };
        using var textPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = fontSize };
        using var boldPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = titleFontSize,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold) };

        void DrawCell(float x, float y, float w, float h, SkiaSharp.SKColor bg, SkiaSharp.SKColor fg,
                      string text, bool rightAlign, bool bold)
        {
            var rect = new SkiaSharp.SKRect(x, y, x + w, y + h);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            var p = bold ? boldPaint : textPaint;
            p.Color = fg;
            float tw = p.MeasureText(text);
            float tx = rightAlign ? x + w - 6f - tw : x + 6f;
            float ty = y + h / 2f + p.TextSize * 0.35f;
            canvas.DrawText(text, tx, ty, p);
        }

        float setupLabelWidth = Math.Max(
            boldPaint.MeasureText("SETUP"),
            boldPaint.MeasureText("DISCIPLINE")) + 12f;
        float setupValueWidth = (contentWidth - setupLabelWidth * 2f) / 2f;
        float curY = margin;

        var selectedSetupName = summary.SelectedSetup?.Name;
        var setupName = string.IsNullOrWhiteSpace(selectedSetupName) ? "-" : selectedSetupName;
        var disciplineName = string.IsNullOrWhiteSpace(discipline) ? "-" : discipline;

        DrawCell(margin, curY, setupLabelWidth, rowH, headerBg, headerFg, "SETUP", false, true);
        DrawCell(margin + setupLabelWidth, curY, setupValueWidth, rowH,
            cellBg, cellFg, setupName, false, false);
        DrawCell(margin + setupLabelWidth + setupValueWidth, curY, setupLabelWidth, rowH,
            headerBg, headerFg, "DISCIPLINE", false, true);
        DrawCell(margin + setupLabelWidth * 2f + setupValueWidth, curY, setupValueWidth, rowH,
            cellBg, cellFg, disciplineName, false, false);
        curY += rowH + sectionGap;

        // RUN DATA
        DrawCell(margin, curY, contentWidth, titleH, headerBg, headerFg, "RUN DATA", false, true);
        curY += titleH;
        foreach (var row in summary.RunDataRows)
        {
            DrawCell(margin,          curY, col0,            rowH, cellBg, cellFg, row.Label, false, false);
            DrawCell(margin + col0,   curY, contentWidth - col0, rowH, cellBg, cellFg, row.Value, true,  false);
            curY += rowH;
        }

        curY += sectionGap;

        // WHEEL
        DrawCell(margin,              curY, col0,  rowH, headerBg, headerFg, "",             false, true);
        DrawCell(margin + col0,       curY, col12, rowH, headerBg, headerFg, "FRONT WHEEL",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, rowH, headerBg, headerFg, "REAR WHEEL", true,  true);
        curY += rowH;
        foreach (var row in summary.WheelRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, row.Label,      false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, row.LeftValue,  true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, row.RightValue, true,  false);
            curY += rowH;
        }

        curY += sectionGap;

        // FORK / SHOCK
        DrawCell(margin,                curY, col0,  rowH, headerBg, headerFg, "",      false, true);
        DrawCell(margin + col0,         curY, col12, rowH, headerBg, headerFg, "FORK",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, rowH, headerBg, headerFg, "SHOCK", true,  true);
        curY += rowH;
        foreach (var row in summary.ForkShockRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, row.Label,      false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, row.LeftValue,  true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, row.RightValue, true,  false);
            curY += rowH;
        }

        document.EndPage();
    }

    private static void DrawNotesPage(SkiaSharp.SKDocument document, NotesPageViewModel notes, float pageWidth)
    {
        const float margin = 30f;
        const float rowH = 26f;
        const float titleH = 28f;
        const float sectionGap = 18f;
        const float fontSize = 11f;
        const float titleFontSize = 10f;
        const float noteFontSize = 11f;

        float contentWidth = pageWidth - margin * 2f;
        float col0 = 95f;
        float col12 = (contentWidth - col0) / 2f;

        var bgColor     = SkiaSharp.SKColor.Parse("#15191c");
        var cellBg      = SkiaSharp.SKColor.Parse("#20262b");
        var headerBg    = SkiaSharp.SKColor.Parse("#66c2a5");
        var headerFg    = SkiaSharp.SKColor.Parse("#15191c");
        var cellFg      = SkiaSharp.SKColor.Parse("#a0a0a0");
        var borderColor = SkiaSharp.SKColor.Parse("#505050");

        var settingRows = new[]
        {
            ("Spring",  notes.ForkSettings.SpringRate,              notes.ShockSettings.SpringRate),
            ("VolSpc",  notes.ForkSettings.VolSpc?.ToString("F2"),  notes.ShockSettings.VolSpc?.ToString("F2")),
            ("HSC",     notes.ForkSettings.HighSpeedCompression?.ToString(), notes.ShockSettings.HighSpeedCompression?.ToString()),
            ("LSC",     notes.ForkSettings.LowSpeedCompression?.ToString(),  notes.ShockSettings.LowSpeedCompression?.ToString()),
            ("LSR",     notes.ForkSettings.LowSpeedRebound?.ToString(),      notes.ShockSettings.LowSpeedRebound?.ToString()),
            ("HSR",     notes.ForkSettings.HighSpeedRebound?.ToString(),     notes.ShockSettings.HighSpeedRebound?.ToString()),
            ("Tire pres.", notes.ForkSettings.TirePressure?.ToString("F1"),  notes.ShockSettings.TirePressure?.ToString("F1")),
        };

        bool hasDescription = !string.IsNullOrWhiteSpace(notes.Description);
        float noteHeight = 0f;
        string[] noteLines = [];
        using var notePaint = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = noteFontSize };

        if (hasDescription)
        {
            // Word-wrap the description to fit contentWidth with 8px padding on each side
            float wrapWidth = contentWidth - 16f;
            var words = notes.Description!.Replace("\r\n", "\n").Replace("\r", "\n").Split(' ');
            var lines = new List<string>();
            var currentLine = "";
            foreach (var word in words)
            {
                foreach (var segment in word.Split('\n'))
                {
                    var test = currentLine.Length == 0 ? segment : currentLine + " " + segment;
                    if (notePaint.MeasureText(test) > wrapWidth && currentLine.Length > 0)
                    {
                        lines.Add(currentLine);
                        currentLine = segment;
                    }
                    else
                    {
                        currentLine = test;
                    }
                    if (word.Contains('\n') && segment != words[^1].Split('\n')[^1])
                    {
                        lines.Add(currentLine);
                        currentLine = "";
                    }
                }
            }
            if (currentLine.Length > 0) lines.Add(currentLine);
            noteLines = lines.ToArray();
            noteHeight = titleH + noteLines.Length * (noteFontSize + 4f) + 16f;
        }

        float pageHeight = margin * 2f
            + titleH + settingRows.Length * rowH
            + (hasDescription ? sectionGap + noteHeight : 0f);

        using var canvas = document.BeginPage(pageWidth, pageHeight);
        canvas.Clear(bgColor);

        using var fillPaint   = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 0.75f, Color = borderColor };
        using var textPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = fontSize };
        using var boldPaint   = new SkiaSharp.SKPaint { IsAntialias = true, TextSize = titleFontSize,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold) };

        void DrawCell(float x, float y, float w, float h, SkiaSharp.SKColor bg, SkiaSharp.SKColor fg,
                      string text, bool rightAlign, bool bold)
        {
            var rect = new SkiaSharp.SKRect(x, y, x + w, y + h);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);
            var p = bold ? boldPaint : textPaint;
            p.Color = fg;
            float tw = p.MeasureText(text);
            float tx = rightAlign ? x + w - 6f - tw : x + 6f;
            float ty = y + h / 2f + p.TextSize * 0.35f;
            canvas.DrawText(text, tx, ty, p);
        }

        float curY = margin;

        // SETUP header
        DrawCell(margin,              curY, col0,  titleH, headerBg, headerFg, "",       false, true);
        DrawCell(margin + col0,       curY, col12, titleH, headerBg, headerFg, "FRONT",  true,  true);
        DrawCell(margin + col0 + col12, curY, col12, titleH, headerBg, headerFg, "REAR", true,  true);
        curY += titleH;

        foreach (var (label, frontVal, rearVal) in settingRows)
        {
            DrawCell(margin,                curY, col0,  rowH, cellBg, cellFg, label,          false, false);
            DrawCell(margin + col0,         curY, col12, rowH, cellBg, cellFg, frontVal ?? "-", true,  false);
            DrawCell(margin + col0 + col12, curY, col12, rowH, cellBg, cellFg, rearVal  ?? "-", true,  false);
            curY += rowH;
        }

        // Notes description
        if (hasDescription)
        {
            curY += sectionGap;
            DrawCell(margin, curY, contentWidth, titleH, headerBg, headerFg, "NOTES", false, true);
            curY += titleH;

            var noteRect = new SkiaSharp.SKRect(margin, curY, margin + contentWidth,
                curY + noteLines.Length * (noteFontSize + 4f) + 16f);
            fillPaint.Color = cellBg;
            canvas.DrawRect(noteRect, fillPaint);
            canvas.DrawRect(noteRect, strokePaint);

            notePaint.Color = cellFg;
            float lineY = curY + 8f + noteFontSize;
            foreach (var line in noteLines)
            {
                canvas.DrawText(line, margin + 8f, lineY, notePaint);
                lineY += noteFontSize + 4f;
            }
        }

        document.EndPage();
    }

    // One PDF page's worth of content: an SVG plot, plus (optionally) the zone-percentage
    // band data that mirrors VelocityBandView, rendered directly below the plot on export.
    private sealed record PdfSvgEntry(string Svg, VelocityBandData? Band)
    {
        public static PdfSvgEntry? For(string? svg) => svg is null ? null : new PdfSvgEntry(svg, null);

        public static PdfSvgEntry? For(string? svg, string bandTitle,
            double? hsr, double? lsr, double? lsc, double? hsc)
        {
            if (svg is null) return null;
            var band = VelocityBandData.Create(bandTitle, hsr, lsr, lsc, hsc);
            return new PdfSvgEntry(svg, band);
        }
    }

    // Mirrors VelocityBandView's HSR/LSR/LSC/HSC percentages. Only constructed when all four
    // percentages are present, matching the app control's IsVisible-on-non-null pattern.
    private sealed record VelocityBandData(string Title, double Hsr, double Lsr, double Lsc, double Hsc)
    {
        public static VelocityBandData? Create(string title, double? hsr, double? lsr, double? lsc, double? hsc)
        {
            if (hsr is null || lsr is null || lsc is null || hsc is null) return null;
            return new VelocityBandData(title, hsr.Value, lsr.Value, lsc.Value, hsc.Value);
        }
    }

    // Draws the zone-percentage band block below a low-speed velocity histogram, mirroring
    // VelocityBandView.axaml: bold title, then four columns (HSR/LSR/LSC/HSC) sized
    // proportionally to their percentage, each with a border and a "LABEL / value" text.
    private static void DrawVelocityBand(SkiaSharp.SKCanvas canvas, VelocityBandData band,
        float pageWidth, float top, float pageHeight)
    {
        var titleColor = SkiaSharp.SKColor.Parse("#d0d0d0");
        var borderColor = SkiaSharp.SKColor.Parse("#505558");
        var outerBg = SkiaSharp.SKColor.Parse("#303030");
        var innerBg = SkiaSharp.SKColor.Parse("#282828");
        var textColor = SkiaSharp.SKColor.Parse("#d0d0d0");

        using var titlePaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true, TextSize = 12f, Color = titleColor,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold),
        };
        using var labelPaint = new SkiaSharp.SKPaint
        {
            IsAntialias = true, TextSize = 11f, Color = textColor, TextAlign = SkiaSharp.SKTextAlign.Center,
            Typeface = SkiaSharp.SKTypeface.FromFamilyName(null, SkiaSharp.SKFontStyle.Bold),
        };
        using var fillPaint = new SkiaSharp.SKPaint { IsStroke = false };
        using var strokePaint = new SkiaSharp.SKPaint { IsStroke = true, StrokeWidth = 1f, Color = borderColor };

        // The SVG plot only covers its own CullRect; paint the appended band strip with the
        // plots' figure background (#15191c) so the page reads as one continuous dark panel.
        fillPaint.Color = SkiaSharp.SKColor.Parse("#15191c");
        canvas.DrawRect(new SkiaSharp.SKRect(0f, top, pageWidth, pageHeight), fillPaint);

        float left = PdfBandLeftInset;
        float right = pageWidth - PdfBandRightInset;
        float gridWidth = right - left;

        // Title, left-aligned above the grid (mirrors Margin="0,4,0,0" + Margin="0,0,0,2")
        float titleY = top + 4f + 12f;
        canvas.DrawText(band.Title, left, titleY, titlePaint);

        float gridTop = titleY + 2f;
        float gridBottom = Math.Min(gridTop + PdfBandGridHeight, pageHeight);
        float gridHeight = gridBottom - gridTop;

        var segments = new (string Label, double Value, SkiaSharp.SKColor Bg)[]
        {
            ("HSR", band.Hsr, outerBg),
            ("LSR", band.Lsr, innerBg),
            ("LSC", band.Lsc, innerBg),
            ("HSC", band.Hsc, outerBg),
        };

        double total = segments.Sum(s => s.Value);
        if (total <= 0) total = 1; // guard against div-by-zero; degenerates to equal widths

        float x = left;
        for (int i = 0; i < segments.Length; i++)
        {
            var (label, value, bg) = segments[i];
            float w = (float)(gridWidth * (value / total));
            // Absorb rounding error into the last column so borders line up with `right`.
            if (i == segments.Length - 1) w = right - x;

            var rect = new SkiaSharp.SKRect(x, gridTop, x + w, gridBottom);
            fillPaint.Color = bg;
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, strokePaint);

            float cx = x + w / 2f;
            float labelY = gridTop + gridHeight / 2f - 2f;
            float valueY = labelY + 13f;
            canvas.DrawText(label, cx, labelY, labelPaint);
            canvas.DrawText(value.ToString("0.0"), cx, valueY, labelPaint);

            x += w;
        }
    }
}
