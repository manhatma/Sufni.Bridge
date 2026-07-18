using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sufni.Bridge.Models;
using Sufni.Bridge.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.ViewModels.Items;

namespace Sufni.Bridge.ViewModels;

public partial class ImportSessionsViewModel : ViewModelBase
{
    #region Observable properties

    public ObservableCollection<ITelemetryDataStore>? TelemetryDataStores { get; set; }
    public ObservableCollection<ITelemetryFile> TelemetryFiles { get; } = [];
    private readonly SourceCache<ItemViewModelBase, Guid> sessions;

    [ObservableProperty] private ITelemetryDataStore? selectedDataStore;
    [ObservableProperty] private bool newDataStoresAvailable;
    [ObservableProperty] private bool importInProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportSessionsCommand))]
    private Guid? selectedSetup;

    #endregion Observable properties

    #region Property change handlers

    private async Task ApplyImportDefaults(List<ITelemetryFile> files)
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var importedIds = await databaseService.GetImportedSourceIdentifiersAsync();

        foreach (var file in files.Where(f => f.ShouldBeImported.HasValue && f.ShouldBeImported.Value))
        {
            if (importedIds.Contains(file.SourceIdentifier))
            {
                file.ShouldBeImported = false;
            }
        }
    }

    private async Task GetDataStoreFiles(ITelemetryDataStore dataStore)
    {
        ImportInProgress = true;

        TelemetryFiles.Clear();
        var files = await dataStore.GetFiles();
        await ApplyImportDefaults(files);
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var file in files)
            {
                TelemetryFiles.Add(file);
            }
        });

        ImportInProgress = false;
    }

    async partial void OnSelectedDataStoreChanged(ITelemetryDataStore? value)
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        if (value == null)
        {
            TelemetryFiles.Clear();
            SelectedSetup = null;
            return;
        }

        // Need to clear the DataStoresAvailable flag so that the notification does not show
        // up when the first datastore appears and auto-selected.
        ClearNewDataStoresAvailable();

        try
        {
            var boards = await databaseService.GetBoardsAsync();
            var selectedBoard = boards.FirstOrDefault(b => b?.Id.ToLower().Trim() == value.BoardId?.Trim(), null);
            SelectedSetup = selectedBoard?.SetupId;
        }
        catch (Exception e)
        {
            ErrorMessages.Add($"Error while changing data store: {e.Message}");
        }

        await GetDataStoreFiles(value);
    }

    #endregion Property change handlers

    #region Private members

    private readonly IDatabaseService? databaseService;

    #endregion Private members

    #region Constructors

    // This is only here for the designer
    public ImportSessionsViewModel() : this(new SourceCache<ItemViewModelBase, Guid>(m => m.Id)) { }

    public ImportSessionsViewModel(SourceCache<ItemViewModelBase, Guid> sessions)
    {
        databaseService = App.Current?.Services?.GetService<IDatabaseService>();
        var telemetryDataStoreService = App.Current?.Services?.GetService<ITelemetryDataStoreService>();

        this.sessions = sessions;

        Debug.Assert(databaseService != null, nameof(telemetryDataStoreService) + " != null");
        Debug.Assert(telemetryDataStoreService != null, nameof(telemetryDataStoreService) + " != null");

        TelemetryDataStores = telemetryDataStoreService.DataStores;
        TelemetryDataStores.CollectionChanged += (_, e) =>
        {
            var comparer = new TelemetryDataStoreComparer();
            var removed = (ITelemetryDataStore)e.OldItems?[0]!;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        NewDataStoresAvailable = true;
                        SelectedDataStore ??= TelemetryDataStores[0];
                    });
                    break;
                case NotifyCollectionChangedAction.Remove:
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (TelemetryDataStores.Count == 0 || !comparer.Equals(SelectedDataStore, removed)) return;
                        // XXX: The files from the correct datastore show up, but the ComboBox won't show the datastore
                        //      as selected. Probably has something to do with this fix, since it only handle adds:
                        //      https://github.com/AvaloniaUI/Avalonia/pull/4593/commits/8dfc65d17be00b7f7c96c294dabe7616916951b2
                        SelectedDataStore = TelemetryDataStores[^1];
                    });
                    break;
                case NotifyCollectionChangedAction.Replace:
                case NotifyCollectionChangedAction.Move:
                case NotifyCollectionChangedAction.Reset:
                    return;
            }
        };
        if (TelemetryDataStores.Count > 0)
        {
            SelectedDataStore = TelemetryDataStores[0];
        }
    }

    #endregion

    #region Public methods

    public async Task EvaluateSetupExists()
    {
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        var boards = await databaseService.GetBoardsAsync();
        var selectedBoard = boards.FirstOrDefault(b => b?.Id.ToLower().Trim() == SelectedDataStore?.BoardId?.Trim(), null);
        SelectedSetup = selectedBoard?.SetupId;
    }

    public async Task Refresh()
    {
        if (SelectedDataStore != null)
        {
            await GetDataStoreFiles(SelectedDataStore);
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task OpenDataStore()
    {
        var filesService = App.Current?.Services?.GetService<IFilesService>();
        Debug.Assert(filesService != null, nameof(filesService) + " != null");
        Debug.Assert(TelemetryDataStores != null, nameof(TelemetryDataStores) + " != null");

        var folder = await filesService.OpenDataStoreFolderAsync();
        if (folder is null) return;

        var massStorages = TelemetryDataStores.OfType<MassStorageTelemetryDataStore>()
            .Select(ds => ds.DriveInfo.RootDirectory.FullName)
            .ToArray();
        var folderLocalPath = folder.TryGetLocalPath();
        if (massStorages.Contains(folderLocalPath))
        {
            Notifications.Add("Folder is already opened in mass-storage mode!");
            return;
        }

        var dataStore = new StorageProviderTelemetryDataStore(folder);
        await dataStore.Initialization;

        TelemetryDataStores.Add(dataStore);
    }

    private async void ImportSessionsInternal()
    {
        Debug.Assert(SelectedSetup != null);
        Debug.Assert(SelectedDataStore != null);
        Debug.Assert(databaseService != null, nameof(databaseService) + " != null");

        ImportInProgress = true;

        var lastSession = await databaseService.GetMostRecentSessionAsync(SelectedSetup!.Value);
        var pendingChanges = await databaseService.GetPendingSetupChangesAsync(SelectedSetup!.Value);
        var pendingApplied = false;

        // Setup, linkage and calibrations are fixed for the whole run (SelectedSetup does not
        // change mid-import), so fetch and prepare them once instead of per file. A broken
        // configuration fails the import as a whole — every file would fail identically.
        Models.Telemetry.Linkage linkage;
        Models.Telemetry.Calibration? fcal, rcal;
        try
        {
            var swSetup = Stopwatch.StartNew();
            var setup = await databaseService.GetSetupAsync(SelectedSetup!.Value);
            linkage = await databaseService.GetLinkageAsync(setup!.LinkageId)
                      ?? throw new Exception("Linkage is missing");

            // Get front Calibration
            fcal = await databaseService.GetCalibrationAsync(setup.FrontCalibrationId ?? Guid.Empty);
            var fmethod = fcal is null ? null : await databaseService.GetCalibrationMethodAsync(fcal.MethodId);
            if (fcal is not null && fmethod == null)
            {
                throw new Exception("Front calibration method is missing.");
            }

            // Get rear Calibration
            rcal = await databaseService.GetCalibrationAsync(setup.RearCalibrationId ?? Guid.Empty);
            var rmethod = rcal is null ? null : await databaseService.GetCalibrationMethodAsync(rcal.MethodId);
            if (rcal is not null && rmethod == null)
            {
                throw new Exception("Rear calibration method is missing.");
            }

            fcal?.Prepare(fmethod!, linkage.MaxFrontStroke!.Value, linkage.MaxFrontTravel);
            rcal?.Prepare(rmethod!, linkage.MaxRearStroke!.Value, linkage.MaxRearTravel);
            PerfLog.Log("import/setup", swSetup.Elapsed.TotalMilliseconds);
        }
        catch (Exception e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessages.Add($"Could not import: {e.Message}");
            });
            ImportInProgress = false;
            return;
        }

        foreach (var telemetryFile in TelemetryFiles.Where(f => f.ShouldBeImported.HasValue && f.ShouldBeImported.Value))
        {
            try
            {
                using var perfTotal = PerfLog.Measure($"import/total {telemetryFile.Name}");

                var swProcess = Stopwatch.StartNew();
                var (telemetryData, psst) = await telemetryFile.GeneratePsstAsync(linkage, fcal, rcal);
                PerfLog.Log($"import/process {telemetryFile.Name}", swProcess.Elapsed.TotalMilliseconds);

                // Duration for the day-group time range display, straight from the in-memory
                // object (same expression the blob roundtrip used to compute).
                int? durationSeconds = null;
                var sampleCount = Math.Max(telemetryData.Front.Travel?.Length ?? 0, telemetryData.Rear.Travel?.Length ?? 0);
                if (telemetryData.SampleRate > 0)
                    durationSeconds = sampleCount / telemetryData.SampleRate;

                var session = new Session(
                    id: Guid.NewGuid(),
                    name: telemetryFile.Name,
                    description: telemetryFile.Description,
                    setup: SelectedSetup!.Value,
                    timestamp: (int)((DateTimeOffset)telemetryFile.StartTime).ToUnixTimeSeconds())
                {
                    ProcessedData = psst,
                    SourceIdentifier = telemetryFile.SourceIdentifier,
                    FrontSpringRate = pendingChanges?.FrontSpringRate ?? lastSession?.FrontSpringRate,
                    FrontVolSpc = pendingChanges?.FrontVolSpc ?? lastSession?.FrontVolSpc,
                    FrontHighSpeedCompression = pendingChanges?.FrontHighSpeedCompression ?? lastSession?.FrontHighSpeedCompression,
                    FrontLowSpeedCompression = pendingChanges?.FrontLowSpeedCompression ?? lastSession?.FrontLowSpeedCompression,
                    FrontLowSpeedRebound = pendingChanges?.FrontLowSpeedRebound ?? lastSession?.FrontLowSpeedRebound,
                    FrontHighSpeedRebound = pendingChanges?.FrontHighSpeedRebound ?? lastSession?.FrontHighSpeedRebound,
                    FrontTirePressure = pendingChanges?.FrontTirePressure ?? lastSession?.FrontTirePressure,
                    RearSpringRate = pendingChanges?.RearSpringRate ?? lastSession?.RearSpringRate,
                    RearVolSpc = pendingChanges?.RearVolSpc ?? lastSession?.RearVolSpc,
                    RearHighSpeedCompression = pendingChanges?.RearHighSpeedCompression ?? lastSession?.RearHighSpeedCompression,
                    RearLowSpeedCompression = pendingChanges?.RearLowSpeedCompression ?? lastSession?.RearLowSpeedCompression,
                    RearLowSpeedRebound = pendingChanges?.RearLowSpeedRebound ?? lastSession?.RearLowSpeedRebound,
                    RearHighSpeedRebound = pendingChanges?.RearHighSpeedRebound ?? lastSession?.RearHighSpeedRebound,
                    RearTirePressure = pendingChanges?.RearTirePressure ?? lastSession?.RearTirePressure,
                    DurationSeconds = durationSeconds,
                };

                var swDbWrite = Stopwatch.StartNew();
                await databaseService.PutSessionAsync(session);
                PerfLog.Log($"import/db-write {telemetryFile.Name}", swDbWrite.Elapsed.TotalMilliseconds);
                lastSession = session;
                if (pendingChanges != null)
                {
                    pendingApplied = true;
                    pendingChanges = null; // only apply to the first session in this batch
                }
                await telemetryFile.OnImported();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var svm = new SessionViewModel(session, true);
                    sessions.AddOrUpdate(svm);
                    Notifications.Insert(0, $"{svm.Name} was successfully imported.");
                    // Hand the in-memory TelemetryData to the cache precomputation so it does
                    // not have to re-read and re-deserialize the blob it was just built from.
                    _ = Task.Run(() => svm.PrecomputeCache(telemetryData));
                });
            }
            catch (Exception e)
            {
                await telemetryFile.OnImportFailed();
                Dispatcher.UIThread.Post(() =>
                {
                    ErrorMessages.Add($"Could not import {telemetryFile.Name}: {e.Message}");
                });
            }
        }

        if (pendingApplied)
        {
            await databaseService.DeletePendingSetupChangesAsync(SelectedSetup!.Value);
        }

        foreach (var telemetryFile in TelemetryFiles.Where(f => !f.ShouldBeImported.HasValue))
        {
            try
            {
                await telemetryFile.OnTrashed();
            }
            catch (Exception e)
            {
                // A failed trash must not abort the run — the finish signal below
                // still has to reach the DAQ so its server mode shuts down.
                Dispatcher.UIThread.Post(() =>
                {
                    ErrorMessages.Add($"Could not trash {telemetryFile.Name}: {e.Message}");
                });
            }
        }

        if (SelectedDataStore is NetworkTelemetryDataStore networkStore)
        {
            try
            {
                await networkStore.Finish();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Server finish for {networkStore.Name} failed: {e.Message}");
            }
            // Skip refresh — server is shutting down; the data store will disappear via mDNS.
            Dispatcher.UIThread.Post(() => TelemetryFiles.Clear());
            ImportInProgress = false;
            return;
        }

        var files = await SelectedDataStore.GetFiles();
        await ApplyImportDefaults(files);
        TelemetryFiles.Clear();
        foreach (var file in files)
        {
            TelemetryFiles.Add(file);
        }

        ImportInProgress = false;
    }

    [RelayCommand(CanExecute = nameof(CanImportSessions))]
    private void ImportSessions()
    {
        new Thread(ImportSessionsInternal).Start();
    }

    private bool CanImportSessions()
    {
        return SelectedSetup != null;
    }

    [RelayCommand]
    private void ClearNewDataStoresAvailable()
    {
        NewDataStoresAvailable = false;
    }

    [RelayCommand]
    private static void AddSetup()
    {
        var mainPagesViewModel = App.Current?.Services?.GetService<MainPagesViewModel>();
        Debug.Assert(mainPagesViewModel != null, nameof(mainPagesViewModel) + " != null");

        mainPagesViewModel.SetupsPage.AddCommand.Execute(null);
    }

    #endregion Commands
}
