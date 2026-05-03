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
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.Models;
using Sufni.Bridge.Models.Telemetry;
using Sufni.Bridge.Services;
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
    [ObservableProperty] private bool isCombineMode;
    [ObservableProperty] private int combineSelectionCount;
    [ObservableProperty] private bool combineSetupMismatch;
    [ObservableProperty] private bool combineDepthExceeded;
    [ObservableProperty] private bool isCombining;
    [ObservableProperty] private bool isDeleteMode;
    [ObservableProperty] private int deleteSelectionCount;
    public string DeleteConfirmLabel => HasSelectedCombinedSessions ? "Uncombine" : "Delete";

    public ObservableCollection<SetupFilterItem> SetupFilters { get; } = [];

    // Track which setup IDs are currently allowed (null = no filter active / all)
    private HashSet<Guid?>? _allowedSetupIds;

    // Remember previously selected session IDs across mode toggles
    private readonly HashSet<Guid> _lastCompareSelection = [];
    private readonly HashSet<Guid> _lastCombineSelection = [];

    // Track combined session IDs for marking
    private HashSet<Guid> _combinedIds = [];
    private readonly Dictionary<DateOnly, bool> _expandState = new();
    private bool _noDateExpanded;
    private bool HasSelectedCombinedSessions
    {
        get
        {
            var selected = Items
                .Where(i => i.IsSelectedForCompare)
                .OfType<SessionViewModel>()
                .ToList();
            return selected.Count > 0 && selected.All(s => s.IsCombinedSession);
        }
    }

    public ObservableCollection<SessionDayGroupViewModel> DayGroups { get; } = [];

    private void RebuildDayGroups()
    {
        // Save expand states
        foreach (var g in DayGroups)
        {
            if (g.Date.HasValue)
                _expandState[g.Date.Value] = g.IsExpanded;
            else
                _noDateExpanded = g.IsExpanded;
        }

        DayGroups.Clear();

        var groups = items
            .OfType<SessionViewModel>()
            .GroupBy(s => s.Timestamp.HasValue ? DateOnly.FromDateTime(s.Timestamp.Value) : (DateOnly?)null)
            .OrderByDescending(g => g.Key);

        bool isFirst = true;
        foreach (var g in groups)
        {
            bool expanded;
            if (g.Key.HasValue)
                expanded = _expandState.TryGetValue(g.Key.Value, out var saved) ? saved : isFirst;
            else
                expanded = _noDateExpanded;

            var dayGroup = new SessionDayGroupViewModel(g.Key, expanded);
            foreach (var s in g.OrderByDescending(s => s.Timestamp))
                dayGroup.Sessions.Add(s);
            DayGroups.Add(dayGroup);
            isFirst = false;
        }
    }

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
            .Subscribe(_ => RebuildDayGroups());
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
            _combinedIds = await databaseService.GetAllCombinedIdsAsync();

            // Build all ViewModels
            var sessionMap = new Dictionary<Guid, SessionViewModel>();
            foreach (var session in sessionList)
            {
                var svm = new SessionViewModel(session, true);
                if (_combinedIds.Contains(session.Id))
                    svm.IsCombinedSession = true;
                sessionMap[session.Id] = svm;
            }

            // Populate SubSessions for combined sessions and collect all source IDs.
            // Sub-sessions are listed newest-first to match the global session list order.
            var allSourceIds = new HashSet<Guid>();
            foreach (var combinedId in _combinedIds)
            {
                if (!sessionMap.TryGetValue(combinedId, out var combinedVm)) continue;
                var sourceIds = await databaseService.GetCombinedSourcesAsync(combinedId);
                var sourceVms = new List<SessionViewModel>();
                foreach (var sourceId in sourceIds)
                {
                    allSourceIds.Add(sourceId);
                    if (sessionMap.TryGetValue(sourceId, out var sourceVm))
                        sourceVms.Add(sourceVm);
                }
                foreach (var sourceVm in sourceVms.OrderByDescending(s => s.Timestamp ?? DateTime.MinValue))
                    combinedVm.SubSessions.Add(sourceVm);
            }

            // Add only non-source sessions to the main list
            foreach (var (id, svm) in sessionMap)
            {
                if (!allSourceIds.Contains(id))
                    Source.AddOrUpdate(svm);
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

        // Backfill duration for sessions imported before duration tracking was added
        _ = Task.Run(async () =>
        {
            try { await databaseService!.BackfillDurationAsync(); }
            catch { /* non-critical */ }
        });
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

    private void ClearSelectionMode()
    {
        foreach (var item in Items)
            item.IsSelectedForCompare = false;
        CompareSelectionCount = 0;
        CombineSelectionCount = 0;
        CombineSetupMismatch = false;
        CombineDepthExceeded = false;
        DeleteSelectionCount = 0;
        OnPropertyChanged(nameof(DeleteConfirmLabel));
    }

    [RelayCommand]
    private void ToggleCompareMode()
    {
        IsCompareMode = !IsCompareMode;

        if (IsCompareMode)
        {
            IsCombineMode = false;
            IsDeleteMode = false;
            ClearSelectionMode();

            // Restore previous selection
            var count = 0;
            foreach (var item in Items)
            {
                if (_lastCompareSelection.Contains(item.Id))
                {
                    item.IsSelectedForCompare = true;
                    count++;
                }
            }
            CompareSelectionCount = count;
        }
        else
        {
            _lastCompareSelection.Clear();
            foreach (var item in Items)
            {
                if (item.IsSelectedForCompare)
                    _lastCompareSelection.Add(item.Id);
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

        // Remember selection for next time
        _lastCompareSelection.Clear();
        foreach (var item in selected)
            _lastCompareSelection.Add(item.Id);

        var mainViewModel = App.Current?.Services?.GetService<MainViewModel>();
        Debug.Assert(mainViewModel != null, nameof(mainViewModel) + " != null");

        var compareVm = new CompareSessionsViewModel(selected);
        mainViewModel.OpenView(compareVm);

        // Reset compare mode UI
        IsCompareMode = false;
        foreach (var item in Items)
        {
            item.IsSelectedForCompare = false;
        }
        CompareSelectionCount = 0;
    }

    [RelayCommand]
    private void ToggleCombineMode()
    {
        IsCombineMode = !IsCombineMode;

        if (IsCombineMode)
        {
            IsCompareMode = false;
            IsDeleteMode = false;
            ClearSelectionMode();

            var count = 0;
            foreach (var item in Items)
            {
                if (_lastCombineSelection.Contains(item.Id))
                {
                    item.IsSelectedForCompare = true;
                    count++;
                }
            }
            CombineSelectionCount = count;
            CheckCombineCompatibility();
        }
        else
        {
            _lastCombineSelection.Clear();
            foreach (var item in Items)
            {
                if (item.IsSelectedForCompare)
                    _lastCombineSelection.Add(item.Id);
                item.IsSelectedForCompare = false;
            }
            CombineSelectionCount = 0;
            CombineSetupMismatch = false;
            CombineDepthExceeded = false;
        }
    }

    [RelayCommand]
    private void ToggleCombineSelection(ItemViewModelBase item)
    {
        if (item.IsSelectedForCompare)
        {
            item.IsSelectedForCompare = false;
            CombineSelectionCount--;
        }
        else
        {
            item.IsSelectedForCompare = true;
            CombineSelectionCount++;
        }
        CheckCombineCompatibility();
    }

    private void CheckCombineCompatibility()
    {
        var selected = Items
            .Where(i => i.IsSelectedForCompare)
            .OfType<SessionViewModel>()
            .ToList();

        if (selected.Count < 2)
        {
            CombineSetupMismatch = false;
            CombineDepthExceeded = false;
            return;
        }

        var firstSetup = selected[0].SessionModel.Setup;
        CombineSetupMismatch = selected.Any(s => s.SessionModel.Setup != firstSetup);

        // Max nesting depth is 3: result depth = max(sub-depths) + 1 ≤ 3
        var maxDepth = selected.Max(s => s.NestingDepth);
        CombineDepthExceeded = maxDepth + 1 > 3;
    }

    [RelayCommand]
    private async Task CombineSessions()
    {
        var databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var selected = Items
            .Where(i => i.IsSelectedForCompare)
            .OfType<SessionViewModel>()
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (selected.Count < 2) return;

        IsCombining = true;
        try
        {
            // Load telemetry data for all selected sessions
            var telemetryDataList = new List<TelemetryData>();
            foreach (var svm in selected)
            {
                var td = await databaseService.GetSessionPsstAsync(svm.Id);
                if (td is null)
                {
                    ErrorMessages.Add($"Could not load telemetry for '{svm.Name}'");
                    return;
                }
                // Apply crop if set — combine uses the cropped slice, not the full session
                if (svm.SessionModel.CropStartSample.HasValue && svm.SessionModel.CropEndSample.HasValue)
                    td = td.CreateCroppedCopy(svm.SessionModel.CropStartSample.Value, svm.SessionModel.CropEndSample.Value);
                telemetryDataList.Add(td);
            }

            // Build combined name — session names + shared date in parentheses if all on same day
            // Strip any existing date suffix "(dd.MM.yyyy)" so re-combining doesn't duplicate it
            var sessionNames = selected.Select(s =>
            {
                var trimmed = s.Name?.TrimStart('0') ?? "";
                trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s*\(\d{2}\.\d{2}\.\d{4}\)\s*$", "").TrimEnd();
                return trimmed.Length == 0 ? "0" : trimmed;
            }).ToList();
            var namesPart = string.Join(" + ", sessionNames);

            var dates = selected
                .Where(s => s.Timestamp.HasValue)
                .Select(s => s.Timestamp!.Value.Date)
                .Distinct()
                .ToList();
            var datePart = dates.Count == 1 ? $" ({dates[0]:dd.MM.yyyy})" : "";

            var combinedName = namesPart + datePart;
            if (combinedName.Length > 80)
                combinedName = combinedName[..77] + "...";
            // No text prefix — chain icon is shown in the list

            // Combine telemetry
            var combined = TelemetryData.CombineSessions(telemetryDataList, combinedName);
            var serialized = MessagePackSerializer.Serialize(combined);

            // Compute combined duration
            var combinedSampleCount = Math.Max(combined.Front.Travel?.Length ?? 0, combined.Rear.Travel?.Length ?? 0);
            var combinedDuration = combined.SampleRate > 0 ? combinedSampleCount / combined.SampleRate : 0;

            // Create session record
            var firstSession = selected[0].SessionModel;
            var firstTimestamp = selected
                .Where(s => s.SessionModel.Timestamp.HasValue)
                .Min(s => s.SessionModel.Timestamp)
                ?? firstSession.Timestamp
                ?? (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            var newSession = new Session(
                id: Guid.NewGuid(),
                name: combinedName,
                description: $"Combined from {selected.Count} sessions",
                setup: firstSession.Setup,
                timestamp: firstTimestamp)
            {
                ProcessedData = serialized,
                FrontSpringRate = firstSession.FrontSpringRate,
                RearSpringRate = firstSession.RearSpringRate,
                FrontVolSpc = firstSession.FrontVolSpc,
                RearVolSpc = firstSession.RearVolSpc,
                FrontHighSpeedCompression = firstSession.FrontHighSpeedCompression,
                FrontLowSpeedCompression = firstSession.FrontLowSpeedCompression,
                FrontLowSpeedRebound = firstSession.FrontLowSpeedRebound,
                FrontHighSpeedRebound = firstSession.FrontHighSpeedRebound,
                RearHighSpeedCompression = firstSession.RearHighSpeedCompression,
                RearLowSpeedCompression = firstSession.RearLowSpeedCompression,
                RearLowSpeedRebound = firstSession.RearLowSpeedRebound,
                RearHighSpeedRebound = firstSession.RearHighSpeedRebound,
                DurationSeconds = combinedDuration
            };

            // Persist
            await databaseService.PutSessionAsync(newSession);
            await databaseService.PutCombinedSourcesAsync(newSession.Id, selected.Select(s => s.Id).ToList());

            // Add to UI — move source sessions from main list into SubSessions.
            // List sub-sessions newest-first so the order matches the global session list.
            var newVm = new SessionViewModel(newSession, true) { IsCombinedSession = true, IsExpanded = true };
            foreach (var svm in selected.OrderByDescending(s => s.Timestamp ?? DateTime.MinValue))
            {
                newVm.SubSessions.Add(svm);
                Source.Remove(svm);
            }
            Source.AddOrUpdate(newVm);
            _combinedIds.Add(newSession.Id);

            // Reset combine mode
            IsCombineMode = false;
            CombineSelectionCount = 0;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Could not combine sessions: {e.Message}");
        }
        finally
        {
            IsCombining = false;
        }
    }

    [RelayCommand]
    private void ToggleDeleteMode()
    {
        IsDeleteMode = !IsDeleteMode;

        if (IsDeleteMode)
        {
            IsCompareMode = false;
            IsCombineMode = false;
            ClearSelectionMode();
        }
        else
        {
            foreach (var item in Items)
                item.IsSelectedForCompare = false;
            DeleteSelectionCount = 0;
        }
        OnPropertyChanged(nameof(DeleteConfirmLabel));
    }

    [RelayCommand]
    private void ToggleDeleteSelection(ItemViewModelBase item)
    {
        if (item.IsSelectedForCompare)
        {
            item.IsSelectedForCompare = false;
            DeleteSelectionCount--;
        }
        else
        {
            item.IsSelectedForCompare = true;
            DeleteSelectionCount++;
        }
        OnPropertyChanged(nameof(DeleteConfirmLabel));
    }

    [RelayCommand]
    private async Task DeleteSelectedSessions()
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var selected = Items
            .Where(i => i.IsSelectedForCompare)
            .ToList();

        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            if (item is SessionViewModel { IsCombinedSession: true } combinedVm)
            {
                // Dissolve combined session: delete grouping, restore sub-sessions to main list
                await databaseService.DeleteCombinedSourcesAsync(combinedVm.Id);
                await databaseService.DeleteSessionAsync(combinedVm.Id);
                _combinedIds.Remove(combinedVm.Id);

                foreach (var sub in combinedVm.SubSessions)
                    Source.AddOrUpdate(sub);

                Source.Remove(combinedVm);
            }
            else
            {
                Source.Remove(item);
                await databaseService.DeleteSessionAsync(item.Id);
            }
        }

        // Reset delete mode
        IsDeleteMode = false;
        DeleteSelectionCount = 0;
        OnPropertyChanged(nameof(DeleteConfirmLabel));
    }
}
