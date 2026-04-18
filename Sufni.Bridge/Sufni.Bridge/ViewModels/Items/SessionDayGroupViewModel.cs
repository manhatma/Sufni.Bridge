using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sufni.Bridge.ViewModels.Items;

public partial class SessionDayGroupViewModel : ObservableObject
{
    public DateOnly? Date { get; }
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    [ObservableProperty] private bool isExpanded;

    public string Title => Date?.ToString("dd.MM.yyyy") ?? "No date";

    public string TimeRange
    {
        get
        {
            if (Sessions.Count == 0) return "";

            var timestamps = Sessions
                .Where(s => s.Timestamp.HasValue)
                .Select(s => s.Timestamp!.Value)
                .ToList();

            if (timestamps.Count == 0) return "";

            var first = timestamps.Min();
            var last = timestamps.Max();

            // Find session with latest timestamp to add its duration
            var lastSession = Sessions
                .Where(s => s.Timestamp.HasValue)
                .OrderByDescending(s => s.Timestamp!.Value)
                .First();
            var durationSeconds = lastSession.SessionModel.DurationSeconds ?? 0;
            var end = last.AddSeconds(durationSeconds);

            return $"{first:HH:mm} – {end:HH:mm}";
        }
    }

    public double ChevronAngle => IsExpanded ? 90 : 0;

    public SessionDayGroupViewModel(DateOnly? date, bool isExpanded = false)
    {
        Date = date;
        IsExpanded = isExpanded;
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        OnPropertyChanged(nameof(ChevronAngle));
    }

    public void RefreshTimeRange()
    {
        OnPropertyChanged(nameof(TimeRange));
    }
}
