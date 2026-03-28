using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.Statistics;
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
    [ObservableProperty] private SvgImage? reboundBalanceSvg;
    [ObservableProperty] private SvgImage? compressionBalanceSvg;
    [ObservableProperty] private SvgImage? frontLowSpeedSvg;
    [ObservableProperty] private SvgImage? rearLowSpeedSvg;

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
    private string? _reboundBalanceXml;
    private string? _compressionBalanceXml;
    private string? _frontLowSpeedXml;
    private string? _rearLowSpeedXml;

    public ObservableCollection<CompareTableRow> FrontWheelRows { get; } = [];
    public ObservableCollection<CompareTableRow> RearWheelRows { get; } = [];

    public CompareSessionsViewModel(List<SessionViewModel> sessions)
    {
        Sessions = sessions.OrderBy(s => s.Timestamp ?? DateTime.MinValue).ToList();
        SessionNames = Sessions.Select(s => s.Name ?? "Unknown").ToList();
    }

    private static SvgSource? SvgToSource(string? svgXml) =>
        svgXml is null ? null : SvgSource.LoadFromSvg(svgXml);

    private static SvgImage? SourceToImage(SvgSource? source) =>
        source is null ? null : new SvgImage { Source = source };

    private static string FormatPercent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:F1}");

    private static string FormatTravel(double value, double maxTravel) =>
        maxTravel <= 0
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{value / maxTravel * 100.0:0.0}");

    private static string FormatVelocity(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.0}");

    private static string FormatBottomouts(int value) => $"{value}";

    private sealed record SessionStats(
        DetailedTravelStatistics Travel,
        VelocityStatistics Velocity,
        double Comp95th,
        double Reb95th,
        double MaxTravel,
        VelocityBands? Bands);

    private static SessionStats? BuildSessionStats(TelemetryData data, SuspensionType type)
    {
        var suspension = type == SuspensionType.Front ? data.Front : data.Rear;
        if (!suspension.Present) return null;

        var maxTravel = type == SuspensionType.Front ? data.Linkage.MaxFrontTravel : data.Linkage.MaxRearTravel;
        var travel = data.CalculateDetailedTravelStatistics(type);
        var velocity = data.CalculateVelocityStatistics(type);
        var bands = data.CalculateVelocityBands(type, 200);

        var compVels = suspension.Strokes.Compressions
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)])
            .ToList();
        var rebVels = suspension.Strokes.Rebounds
            .SelectMany(s => suspension.Velocity[s.Start..(s.End + 1)].Select(Math.Abs))
            .ToList();

        return new SessionStats(
            travel, velocity,
            compVels.Count > 0 ? compVels.Percentile(95) : 0.0,
            rebVels.Count > 0 ? -rebVels.Percentile(95) : 0.0,
            maxTravel, bands);
    }

    private static List<CompareTableRow> BuildSummaryRows(List<SessionStats?> statsList, List<SessionViewModel> sessions, SuspensionType type)
    {
        var rows = new List<CompareTableRow>
        {
            new("Spring", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontSpringRate : s.SessionModel.RearSpringRate;
                return val ?? "-";
            }).ToList()),
            new("VolSpc", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontVolSpc : s.SessionModel.RearVolSpc;
                return val.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{val.Value:F2}") : "-";
            }).ToList()),
            new("HSC [clicks]", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontHighSpeedCompression : s.SessionModel.RearHighSpeedCompression;
                return val.HasValue ? val.Value.ToString() : "-";
            }).ToList()),
            new("LSC [clicks]", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontLowSpeedCompression : s.SessionModel.RearLowSpeedCompression;
                return val.HasValue ? val.Value.ToString() : "-";
            }).ToList()),
            new("LSR [clicks]", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontLowSpeedRebound : s.SessionModel.RearLowSpeedRebound;
                return val.HasValue ? val.Value.ToString() : "-";
            }).ToList()),
            new("HSR [clicks]", sessions.Select(s =>
            {
                var val = type == SuspensionType.Front ? s.SessionModel.FrontHighSpeedRebound : s.SessionModel.RearHighSpeedRebound;
                return val.HasValue ? val.Value.ToString() : "-";
            }).ToList()),
            new("Pos [AVG, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.Average, s.MaxTravel)).ToList()),
            new("Pos [95th, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.P95, s.MaxTravel)).ToList()),
            new("Pos [MAX, %]", statsList.Select(s => s is null ? "-" : FormatTravel(s.Travel.Max, s.MaxTravel)).ToList()),
            new("Bottom out [times]", statsList.Select(s => s is null ? "-" : FormatBottomouts(s.Travel.Bottomouts)).ToList()),
            new("Comp [AVG, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.AverageCompression)).ToList()),
            new("Reb [AVG, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.AverageRebound)).ToList()),
            new("Comp [95th, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Comp95th)).ToList()),
            new("Reb [95th, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Reb95th)).ToList()),
            new("Comp [MAX, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.MaxCompression)).ToList()),
            new("Reb [MAX, mm/s]", statsList.Select(s => s is null ? "-" : FormatVelocity(s.Velocity.MaxRebound)).ToList()),
            new("HSR [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.HighSpeedRebound)).ToList()),
            new("LSR [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.LowSpeedRebound)).ToList()),
            new("LSC [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.LowSpeedCompression)).ToList()),
            new("HSC [%]", statsList.Select(s => s?.Bands is null ? "-" : FormatPercent(s.Bands.HighSpeedCompression)).ToList()),
        };
        return rows;
    }

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

        // 5. Rebound Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalanceTypePlot(new Plot(), BalanceType.Rebound);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _reboundBalanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => ReboundBalanceSvg = SourceToImage(src));
        }));

        // 6. Compression Balance
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareBalanceTypePlot(new Plot(), BalanceType.Compression);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _compressionBalanceXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => CompressionBalanceSvg = SourceToImage(src));
        }));

        // 7. Front Low-Speed Velocity
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareLowSpeedVelocityPlot(new Plot(), SuspensionType.Front);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _frontLowSpeedXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => FrontLowSpeedSvg = SourceToImage(src));
        }));

        // 6. Rear Low-Speed Velocity
        tasks.Add(Task.Run(() =>
        {
            var p = new CompareLowSpeedVelocityPlot(new Plot(), SuspensionType.Rear);
            p.LoadMultipleSessions(sessionData);
            var svg = p.Plot.GetSvgXml(width, height);
            _rearLowSpeedXml = svg;
            var src = SvgToSource(svg);
            Dispatcher.UIThread.Post(() => RearLowSpeedSvg = SourceToImage(src));
        }));

        // 7. Summary Table
        tasks.Add(Task.Run(() =>
        {
            var frontStatsList = sessionData.Select(s => BuildSessionStats(s.data, SuspensionType.Front)).ToList();
            var rearStatsList = sessionData.Select(s => BuildSessionStats(s.data, SuspensionType.Rear)).ToList();

            var frontRows = BuildSummaryRows(frontStatsList, Sessions, SuspensionType.Front);
            var rearRows = BuildSummaryRows(rearStatsList, Sessions, SuspensionType.Rear);

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
         _frontRearTravelXml is not null || _balanceXml is not null ||
         _reboundBalanceXml is not null || _compressionBalanceXml is not null ||
         _frontLowSpeedXml is not null || _rearLowSpeedXml is not null);

    [RelayCommand(CanExecute = nameof(CanExportPdf))]
    private async Task ExportPdf()
    {
        IsGeneratingPdf = true;
        try
        {
            var svgs = new List<string?> { _frontTravelHistogramXml, _rearTravelHistogramXml, _frontRearTravelXml, _balanceXml, _reboundBalanceXml, _compressionBalanceXml, _frontLowSpeedXml, _rearLowSpeedXml };
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
