using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SummaryPageViewModel() : PageViewModelBase("Summary")
{
    [ObservableProperty] private ObservableCollection<SummaryValueRow> runDataRows = [];
    [ObservableProperty] private ObservableCollection<SummaryComparisonRow> forkShockRows = [];
    [ObservableProperty] private ObservableCollection<SummaryComparisonRow> wheelRows = [];
}

public class SummaryValueRow(string label, string value)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
}

public class SummaryComparisonRow(string label, string leftValue, string rightValue)
{
    public string Label { get; } = label;
    public string LeftValue { get; } = leftValue;
    public string RightValue { get; } = rightValue;
}
