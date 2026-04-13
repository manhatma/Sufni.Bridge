using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.ViewModels.Items;

namespace Sufni.Bridge.ViewModels.SessionPages;

public partial class SummaryPageViewModel() : PageViewModelBase("Summary")
{
    [ObservableProperty] private ObservableCollection<SummaryValueRow> runDataRows = [];
    [ObservableProperty] private ObservableCollection<SummaryComparisonRow> forkShockRows = [];
    [ObservableProperty] private ObservableCollection<SummaryComparisonRow> wheelRows = [];
    [ObservableProperty] private SetupViewModel? selectedSetup;
    [ObservableProperty] private IAsyncRelayCommand? changeSetupCommand;
    [ObservableProperty] private bool isEditingSetup;
    public ObservableCollection<SetupViewModel> AvailableSetups { get; } = [];

    [RelayCommand]
    private void ToggleEditSetup() => IsEditingSetup = !IsEditingSetup;
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
