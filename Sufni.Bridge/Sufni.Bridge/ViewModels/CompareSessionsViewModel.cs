using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels.Items;

namespace Sufni.Bridge.ViewModels;

public class CompareTableRow
{
    public string Label { get; }
    public List<string> Values { get; }

    public CompareTableRow(string label, List<string> values)
    {
        Label = label;
        Values = values;
    }
}

public partial class CompareSessionsViewModel : ViewModelBase
{
    private static readonly Color[] SessionColors =
    [
        Color.FromHex("#d53e4f"),  // Rot
        Color.FromHex("#3288bd"),  // Blau
        Color.FromHex("#66c2a5"), // Grün
    ];

    private static readonly LinePattern[] SessionPatterns =
    [
        LinePattern.Dashed,
        LinePattern.DenselyDashed,
        LinePattern.Dotted,
    ];

    public List<SessionViewModel> Sessions { get; }
    public List<string> SessionNames { get; }
    public int SessionCount => Sessions.Count;

    [ObservableProperty] private SvgImage? frontTravelHistogramSvg;
    [ObservableProperty] private SvgImage? rearTravelHistogramSvg;
    [ObservableProperty] private SvgImage? frontRearTravelSvg;
    [ObservableProperty] private SvgImage? balanceSvg;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportPdfCommand))]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportPdfCommand))]
    private bool isGeneratingPdf;

    // Raw SVG XML strings for PDF export
    private string? _frontTravelHistogramXml;
    private string? _rearTravelHistogramXml;
    private string? _frontRearTravelXml;
    private string? _balanceXml;

    public ObservableCollection<CompareTableRow> FrontWheelRows { get; } = [];
    public ObservableCollection<CompareTableRow> RearWheelRows { get; } = [];

    public CompareSessionsViewModel(List<SessionViewModel> sessions)
    {
        Sessions = sessions;
        SessionNames = sessions.Select(s => s.Name ?? "Unknown").ToList();
    }

    private static SvgSource? SvgToSource(string? svgXml) =>
        svgXml is null ? null : SvgSource.LoadFromSvg(svgXml);

    private static SvgImage? SourceToImage(SvgSource? source) =>
        source is null ? null : new SvgImage { Source = source };

    private static string FormatPercent(double value) => $"{value:F1}";

    public async Task GenerateComparePlots()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var bounds = SessionViewModel.LastKnownBounds;
        var (width, height) = ((int)bounds.Width, (int)(bounds.Height / 2.0));

        // Load TelemetryData for all sessions
        var sessionData = new List<(TelemetryData data, Color color, LinePattern pattern, string name)>();
        for (var i = 0; i < Sessions.Count; i++)
        {
            var telemetry = await databaseService.GetSessionPsstAsync(Sessions[i].Id);
            if (telemetry is null)
            {
                ErrorMessages.Add($"Session '{Sessions[i].Name}' has no processed data.");
                continue;
            }

            sessionData.Add((telemetry, SessionColors[i], SessionPatterns[i], Sessions[i].Name ?? $"Session {i + 1}"));
        }

        if (sessionData.Count < 2)
        {
            ErrorMessages.Add("At least 2 sessions with processed data are required.");
            IsLoading = false;
            return;
        }

        // Generate plots in parallel
        var tasks = new List<Task>();

        // 1. Front Travel Histogram
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareTravelHistogramPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontTravelHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontTravelHistogramSvg = SourceToImage(src));
        }));

        // 2. Rear Travel Histogram
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareTravelHistogramPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearTravelHistogramXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearTravelHistogramSvg = SourceToImage(src));
        }));

        // 3. Front vs Rear Travel
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareFrontRearTravelPlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontRearTravelXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontRearTravelSvg = SourceToImage(src));
        }));

        // 4. Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalancePlot(new Plot());
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _balanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => BalanceSvg = SourceToImage(src));
        }));

        // 5. Velocity Bands Table
        tasks.Add(Task.Run(() =>
        {
            var frontRows = new List<CompareTableRow>();
            var rearRows = new List<CompareTableRow>();

            var labels = new[] { "HSR [%]", "LSR [%]", "LSC [%]", "HSC [%]" };

            var frontBandsList = new List<VelocityBands?>();
            var rearBandsList = new List<VelocityBands?>();

            foreach (var (data, _, _, _) in sessionData)
            {
                frontBandsList.Add(data.Front.Present
                    ? data.CalculateVelocityBands(SuspensionType.Front, 200)
                    : null);
                rearBandsList.Add(data.Rear.Present
                    ? data.CalculateVelocityBands(SuspensionType.Rear, 200)
                    : null);
            }

            frontRows.Add(new CompareTableRow(labels[0],
                frontBandsList.Select(b => b is null ? "-" : FormatPercent(b.HighSpeedRebound)).ToList()));
            frontRows.Add(new CompareTableRow(labels[1],
                frontBandsList.Select(b => b is null ? "-" : FormatPercent(b.LowSpeedRebound)).ToList()));
            frontRows.Add(new CompareTableRow(labels[2],
                frontBandsList.Select(b => b is null ? "-" : FormatPercent(b.LowSpeedCompression)).ToList()));
            frontRows.Add(new CompareTableRow(labels[3],
                frontBandsList.Select(b => b is null ? "-" : FormatPercent(b.HighSpeedCompression)).ToList()));

            rearRows.Add(new CompareTableRow(labels[0],
                rearBandsList.Select(b => b is null ? "-" : FormatPercent(b.HighSpeedRebound)).ToList()));
            rearRows.Add(new CompareTableRow(labels[1],
                rearBandsList.Select(b => b is null ? "-" : FormatPercent(b.LowSpeedRebound)).ToList()));
            rearRows.Add(new CompareTableRow(labels[2],
                rearBandsList.Select(b => b is null ? "-" : FormatPercent(b.LowSpeedCompression)).ToList()));
            rearRows.Add(new CompareTableRow(labels[3],
                rearBandsList.Select(b => b is null ? "-" : FormatPercent(b.HighSpeedCompression)).ToList()));

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in frontRows) FrontWheelRows.Add(row);
                foreach (var row in rearRows) RearWheelRows.Add(row);
            });
        }));

        await Task.WhenAll(tasks);

        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
    }

    private bool CanExportPdf() => !IsLoading && !IsGeneratingPdf &&
        (_frontTravelHistogramXml is not null || _rearTravelHistogramXml is not null ||
         _frontRearTravelXml is not null || _balanceXml is not null);

    [RelayCommand(CanExecute = nameof(CanExportPdf))]
    private async Task ExportPdf()
    {
        IsGeneratingPdf = true;
        try
        {
            var svgs = new List<string?> { _frontTravelHistogramXml, _rearTravelHistogramXml, _frontRearTravelXml, _balanceXml };
            var validSvgs = svgs.Where(s => s is not null).Cast<string>().ToList();
            if (validSvgs.Count == 0)
            {
                ErrorMessages.Add("No plots to export.");
                return;
            }

            var sessionNames = string.Join("_vs_", SessionNames.Select(n =>
                System.Text.RegularExpressions.Regex.Replace(n, @"[^\w\-.]", "_")));
            var pdfPath = await Task.Run(() => RenderSvgsToPdf(validSvgs, sessionNames));

            IsGeneratingPdf = false;
            var shareService = App.Current?.Services?.GetService<IShareService>();
            if (shareService is not null)
                await shareService.ShareFileAsync(pdfPath);
        }
        catch (Exception e)
        {
            IsGeneratingPdf = false;
            ErrorMessages.Add($"PDF export failed: {e.Message}");
        }
    }

    private static string RenderSvgsToPdf(List<string> svgXmlList, string fileName)
    {
        var tempDir = System.IO.Path.GetTempPath();
        var pdfPath = System.IO.Path.Combine(tempDir, $"{fileName}.pdf");

        var svgObjects = svgXmlList
            .AsParallel()
            .AsOrdered()
            .Select(xml =>
            {
                var svg = new Svg.Skia.SKSvg();
                svg.FromSvg(xml);
                return svg;
            })
            .ToList();

        try
        {
            using var stream = new System.IO.FileStream(pdfPath, System.IO.FileMode.Create);
            using var document = SkiaSharp.SKDocument.CreatePdf(stream);

            foreach (var svg in svgObjects)
            {
                var picture = svg.Picture;
                if (picture is null) continue;

                var bounds = picture.CullRect;
                using var canvas = document.BeginPage(bounds.Width, bounds.Height);
                canvas.DrawPicture(picture);
                document.EndPage();
            }

            document.Close();
        }
        finally
        {
            foreach (var svg in svgObjects)
                svg.Dispose();
        }

        return pdfPath;
    }
}
