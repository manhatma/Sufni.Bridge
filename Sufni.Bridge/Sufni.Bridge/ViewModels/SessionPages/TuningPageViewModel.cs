using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sufni.Bridge.Models.Telemetry;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class TuningSuggestionRow : ObservableObject
{
    [ObservableProperty] private TuningSeverity severity;
    [ObservableProperty] private string component = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string reason = "";

    public IBrush SeverityBrush => Severity switch
    {
        TuningSeverity.Info        => new SolidColorBrush(Color.FromRgb(0x6C, 0xC4, 0x4A)), // green
        TuningSeverity.Recommended => new SolidColorBrush(Color.FromRgb(0xE0, 0xB8, 0x3A)), // yellow
        TuningSeverity.Critical    => new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x55)), // red
        _                          => new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
    };

    partial void OnSeverityChanged(TuningSeverity value) => OnPropertyChanged(nameof(SeverityBrush));
}

public partial class TuningPageViewModel() : PageViewModelBase("Tuning")
{
    public ObservableCollection<TuningSuggestionRow> Suggestions { get; } = new();

    public void Apply(IReadOnlyList<TuningSuggestion> suggestions)
    {
        Suggestions.Clear();
        foreach (var s in suggestions)
        {
            Suggestions.Add(new TuningSuggestionRow
            {
                Severity = s.Severity,
                Component = s.Component,
                Title = s.Title,
                Reason = s.Reason,
            });
        }
    }
}
