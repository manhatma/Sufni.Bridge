using System;
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

public partial class SessionListViewModel : ItemListViewModelBase
{
    [ObservableProperty] private bool isCompareMode;
    [ObservableProperty] private int compareSelectionCount;

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
