using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Sufni.Bridge.Services;
using Sufni.Bridge.ViewModels;
using Sufni.Bridge.Views;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Sufni.Bridge;

public partial class App : Application
{
    public new static App? Current => Application.Current as App;
    public IServiceProvider? Services { get; private set; }

    public App()
    {
        // The whole app is English: pin a fixed culture so numbers use a '.' decimal
        // separator (and a day-first English date format) regardless of device locale.
        // DefaultThreadCurrentCulture also covers the background threads that render plots.
        var culture = new CultureInfo("en-GB");
        // No thousands separators anywhere: ScottPlot's default tick formatter groups large
        // values via CurrentCulture, so a velocity axis tick like 1000 rendered as "1,000" and
        // could be misread as a decimal. Strip the group separators so ticks read "1000".
        culture.NumberFormat.NumberGroupSeparator = "";
        culture.NumberFormat.PercentGroupSeparator = "";
        culture.NumberFormat.CurrencyGroupSeparator = "";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

#if DEBUG
        RegisteredServices.Collection.AddSingleton<IHttpApiService, HttpApiServiceStub>();
#else
        RegisteredServices.Collection.AddSingleton<IHttpApiService, HttpApiService>();
#endif
        RegisteredServices.Collection.AddSingleton<ITelemetryDataStoreService, TelemetryDataStoreService>();
        RegisteredServices.Collection.AddSingleton<IDatabaseService, SqLiteDatabaseService>();
        RegisteredServices.Collection.AddSingleton<ISynchronizationService, SynchronizationService>();
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
    
    public override void OnFrameworkInitializationCompleted()
    {
        RegisteredServices.Collection.AddSingleton<IFilesService>(_ => new FilesService());
        RegisteredServices.Collection.AddSingleton<MainPagesViewModel>();
        RegisteredServices.Collection.AddSingleton<MainViewModel>();
        Services = RegisteredServices.Collection.BuildServiceProvider();
        
        var fileService = Services.GetService<IFilesService>();
        var mainViewModel = Services.GetService<MainViewModel>();
        Debug.Assert(fileService != null, nameof(fileService) + " != null");

        // One-time session_cache maintenance (re-pack pre-compression rows + VACUUM),
        // deferred so it never competes with the initial session-list load. Gated on
        // PRAGMA user_version inside, so after the first completed run this is a single
        // cheap PRAGMA probe per app start.
        var databaseService = Services.GetService<IDatabaseService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                await databaseService!.CompactSessionCacheAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"session_cache maintenance failed: {ex.Message}");
            }
        });

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow();
                fileService.SetTarget(TopLevel.GetTopLevel(desktop.MainWindow));
                desktop.MainWindow.DataContext = mainViewModel;
                break;
            case ISingleViewApplicationLifetime singleViewPlatform:
                singleViewPlatform.MainView = new MainView
                {
                    DataContext = mainViewModel
                };
                singleViewPlatform.MainView.Loaded += (_, _) =>
                {
                    var topLevel = TopLevel.GetTopLevel(singleViewPlatform.MainView);
                    topLevel!.InsetsManager!.DisplayEdgeToEdge = true;
                    mainViewModel!.SafeAreaPadding = topLevel.InsetsManager.SafeAreaPadding;
                    topLevel.InsetsManager.SafeAreaChanged += (_, e) =>
                    {
                        mainViewModel.SafeAreaPadding = e.SafeAreaPadding;
                    };
                    fileService.SetTarget(topLevel);
                };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}