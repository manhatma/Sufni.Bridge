using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.ViewModels.Items;

namespace Sufni.Bridge.ViewModels.ItemLists;

public partial class SetupFilterItem : ObservableObject
{
    public Guid? SetupId { get; }
    public string Name { get; }
    [ObservableProperty] private bool isChecked;

    public SetupFilterItem(Guid? setupId, string name, bool isChecked = true)
    {
        SetupId = setupId;
        Name = name;
        IsChecked = isChecked;
    }
}

public partial class SessionListViewModel : ItemListViewModelBase
{
    [ObservableProperty] private bool isCompareMode;
    [ObservableProperty] private int compareSelectionCount;
    [ObservableProperty] private bool isFilterMenuOpen;
    [ObservableProperty] private bool isFilterActive;

    public ObservableCollection<SetupFilterItem> SetupFilters { get; } = [];

    // Track which setup IDs are currently allowed (null = no filter active / all)
    private HashSet<Guid?>? _allowedSetupIds;

    public override void ConnectSource()
    {
        Source.Connect()
            .Filter(vm => string.IsNullOrEmpty(SearchText) ||
                           (vm.Name is not null &&
                            vm.Name!.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) ||
                           (vm is SessionViewModel svm &&
                            svm.Description is not null &&
                            svm.Description!.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)))
            .Filter(svm => (DateFilterFrom is null || svm.Timestamp >= DateFilterFrom) &&
                           (DateFilterTo is null || svm.Timestamp <= DateFilterTo))
            .Filter(vm => _allowedSetupIds is null ||
                          (vm is SessionViewModel svm && _allowedSetupIds.Contains(svm.SessionModel.Setup)))
            .SortAndBind(out items, SortExpressionComparer<ItemViewModelBase>.Descending(svm => svm.Timestamp!))
            .DisposeMany()
            .Subscribe();
    }

    protected override async Task DeleteImplementation(ItemViewModelBase vm)
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");
        await databaseService.DeleteSessionAsync(vm.Id);
    }

    private async Task LoadSessionsAsync()
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            var sessionList = await databaseService.GetSessionsAsync();
            foreach (var session in sessionList)
            {
                Source.AddOrUpdate(new SessionViewModel(session, true));
            }
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load Sessions: {e.Message}");
        }
    }

    public override async Task LoadFromDatabase()
    {
        Source.Clear();
        await LoadSessionsAsync();
        await LoadSetupFiltersAsync();
    }

    private async Task LoadSetupFiltersAsync()
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        try
        {
            var setups = await databaseService.GetSetupsAsync();
            SetupFilters.Clear();
            SetupFilters.Add(new SetupFilterItem(null, "All", true) { });
            foreach (var setup in setups.OrderBy(s => s.Name))
            {
                var item = new SetupFilterItem(setup.Id, setup.Name);
                SetupFilters.Add(item);
            }
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not load Setups for filter: {e.Message}");
        }
    }

    [RelayCommand]
    private void ToggleFilterMenu()
    {
        IsFilterMenuOpen = !IsFilterMenuOpen;
    }

    [RelayCommand]
    private void ToggleSetupFilter(SetupFilterItem filterItem)
    {
        if (filterItem.Name == "All")
        {
            // "All" toggles all items
            var newState = !filterItem.IsChecked;
            foreach (var item in SetupFilters)
                item.IsChecked = newState;
        }
        else
        {
            filterItem.IsChecked = !filterItem.IsChecked;

            // Update "All" state: checked if all individual items are checked
            var allItem = SetupFilters.FirstOrDefault(f => f.Name == "All");
            if (allItem != null)
                allItem.IsChecked = SetupFilters.Where(f => f.Name != "All").All(f => f.IsChecked);
        }

        // Rebuild allowed set
        var allChecked = SetupFilters.Where(f => f.Name != "All").All(f => f.IsChecked);
        var noneChecked = SetupFilters.Where(f => f.Name != "All").All(f => !f.IsChecked);

        if (allChecked || noneChecked)
        {
            _allowedSetupIds = null; // No filter = show all
        }
        else
        {
            _allowedSetupIds = SetupFilters
                .Where(f => f.Name != "All" && f.IsChecked)
                .Select(f => (Guid?)f.SetupId)
                .ToHashSet();
        }

        IsFilterActive = _allowedSetupIds is not null;
        Source.Refresh();
    }

    [RelayCommand]
    private void ToggleCompareMode()
    {
        IsCompareMode = !IsCompareMode;
        if (!IsCompareMode)
        {
            // Clear all selections when exiting compare mode
            foreach (var item in Items)
            {
                item.IsSelectedForCompare = false;
            }
            CompareSelectionCount = 0;
        }
    }

    [RelayCommand]
    private void ToggleCompareSelection(ItemViewModelBase item)
    {
        if (item.IsSelectedForCompare)
        {
            item.IsSelectedForCompare = false;
            CompareSelectionCount--;
        }
        else if (CompareSelectionCount < 3)
        {
            item.IsSelectedForCompare = true;
            CompareSelectionCount++;
        }
    }

    [RelayCommand]
    private void OpenComparison()
    {
        var selected = Items
            .Where(i => i.IsSelectedForCompare)
            .OfType<SessionViewModel>()
            .ToList();

        if (selected.Count < 2) return;

        var mainViewModel = App.Current?.Services?.GetService<MainViewModel>();
        Debug.Assert(mainViewModel != null, nameof(mainViewModel) + " != null");

        var compareVm = new CompareSessionsViewModel(selected);
        mainViewModel.OpenView(compareVm);

        // Reset compare mode
        IsCompareMode = false;
        foreach (var item in Items)
        {
            item.IsSelectedForCompare = false;
        }
        CompareSelectionCount = 0;
    }
}
