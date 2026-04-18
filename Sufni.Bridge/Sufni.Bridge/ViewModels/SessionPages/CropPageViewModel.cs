using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScottPlot;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Plots;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class CropPageViewModel() : PageViewModelBase("Crop")
{
    [ObservableProperty] private SvgImage? travelTimeHistory;
    [ObservableProperty] private SvgImage? cropPreview;
    [ObservableProperty] private int totalSamples;
    [ObservableProperty] private int cropStartSample;
    [ObservableProperty] private int cropEndSample;
    [ObservableProperty] private int sampleRate = 1;

    internal TelemetryData? FullData { get; set; }
    internal Rect ViewBounds { get; set; }

    // Snapshot of values at the time the crop page was last initialized — used to detect user changes.
    internal int OriginalStartSample { get; set; }
    internal int OriginalEndSample { get; set; }

    public string CropStartTime => FormatTime(CropStartSample, SampleRate);
    public string CropEndTime   => FormatTime(CropEndSample,   SampleRate);
    public string CropDuration  => FormatTime(CropEndSample - CropStartSample, SampleRate);

    public bool IsCropped    => CropStartSample != 0 || CropEndSample != TotalSamples;
    public bool IsModified   => CropStartSample != OriginalStartSample || CropEndSample != OriginalEndSample;

    private CancellationTokenSource? _previewCts;

    partial void OnCropStartSampleChanged(int value)
    {
        OnPropertyChanged(nameof(CropStartTime));
        OnPropertyChanged(nameof(CropDuration));
        OnPropertyChanged(nameof(IsCropped));
        OnPropertyChanged(nameof(IsModified));
        SchedulePreviewUpdate();
    }

    partial void OnCropEndSampleChanged(int value)
    {
        OnPropertyChanged(nameof(CropEndTime));
        OnPropertyChanged(nameof(CropDuration));
        OnPropertyChanged(nameof(IsCropped));
        OnPropertyChanged(nameof(IsModified));
        SchedulePreviewUpdate();
    }

    partial void OnTotalSamplesChanged(int value)
    {
        OnPropertyChanged(nameof(IsCropped));
    }

    public ICommand? ApplyCropCommand { get; set; }
    public ICommand? ResetCropCommand { get; set; }

    private int NudgeStep => Math.Max(1, SampleRate / 2);

    [RelayCommand] private void NudgeStartBack()    => CropStartSample = Math.Max(0, CropStartSample - NudgeStep);
    [RelayCommand] private void NudgeStartForward() => CropStartSample = Math.Min(CropEndSample, CropStartSample + NudgeStep);
    [RelayCommand] private void NudgeEndBack()      => CropEndSample   = Math.Max(CropStartSample, CropEndSample - NudgeStep);
    [RelayCommand] private void NudgeEndForward()    => CropEndSample   = Math.Min(TotalSamples, CropEndSample + NudgeStep);

    private void SchedulePreviewUpdate()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                if (token.IsCancellationRequested) return;
                await GeneratePreviewAsync(token);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task GeneratePreviewAsync(CancellationToken token)
    {
        if (FullData is null) return;
        var start = Math.Max(0, Math.Min(CropStartSample, TotalSamples));
        var end   = Math.Max(start + 1, Math.Min(CropEndSample, TotalSamples));
        if (end - start < 2) return;

        var cropped = FullData.CreateCroppedCopy(start, end);

        var bounds = ViewBounds;
        var w = bounds.Width > 0 ? bounds.Width : 393;
        var h = w / 2.0;

        var plot = new Plot();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#15191C");
        plot.DataBackground.Color  = ScottPlot.Color.FromHex("#15191C");
        var chartPlot = new TravelTimeHistoryPlot(plot);
        chartPlot.LoadTelemetryData(cropped);
        plot.Axes.Title.Label.Text = "Travel over time (cropped)";
        var svgXml = plot.GetSvgXml((int)w, (int)h);

        if (token.IsCancellationRequested) return;

        var source = SvgSource.LoadFromSvg(svgXml);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested)
                CropPreview = new SvgImage { Source = source };
        });
    }

    private static string FormatTime(int samples, int rate)
    {
        if (rate <= 0) return "0:00.0";
        var totalSeconds = samples / (double)rate;
        var minutes = (int)(totalSeconds / 60);
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00.0}";
    }
}
