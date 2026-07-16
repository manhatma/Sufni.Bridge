using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sufni.Bridge.ViewModels.Items;

public partial class SessionDayGroupViewModel : ObservableObject
{
    public DateOnly? Date { get; }
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    [ObservableProperty] private bool isExpanded;

    // Optional user-defined label shown next to the date in the group header. Groups are
    // recreated on every list rebuild, so the value is re-applied from the list VM's lookup;
    // committing an edit persists through the PersistLabel hook set by the same VM.
    [ObservableProperty] private string? label;
    [ObservableProperty] private bool isEditingLabel;
    [ObservableProperty] private string? labelEditText;

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public bool CanEditLabel => Date.HasValue;
    public Func<SessionDayGroupViewModel, Task>? PersistLabel { get; set; }

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

    partial void OnLabelChanged(string? value) => OnPropertyChanged(nameof(HasLabel));

    // Copy the committed label into the edit buffer whenever editing starts (pencil toggled
    // on), so cancelling or a mid-edit list rebuild never half-applies the text.
    partial void OnIsEditingLabelChanged(bool value)
    {
        if (value) LabelEditText = Label;
    }

    [RelayCommand]
    private async Task ConfirmEditLabel()
    {
        var trimmed = LabelEditText?.Trim();
        Label = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        IsEditingLabel = false;
        if (PersistLabel is not null)
            await PersistLabel(this);
    }

    [RelayCommand]
    private void CancelEditLabel()
    {
        IsEditingLabel = false;
    }

    public void RefreshTimeRange()
    {
        OnPropertyChanged(nameof(TimeRange));
    }
}
