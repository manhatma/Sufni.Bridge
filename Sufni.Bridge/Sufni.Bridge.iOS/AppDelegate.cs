using System;
using System.IO;
using Avalonia;
using Avalonia.iOS;
using Foundation;
using HapticFeedback;
using Microsoft.Extensions.DependencyInjection;
using SecureStorage;
using ServiceDiscovery;
using Sufni.Bridge.Services;
using UIKit;

namespace Sufni.Bridge.iOS
{
    // The UIApplicationDelegate for the application. This class is responsible for launching the
    // User Interface of the application, as well as listening (and optionally responding) to
    // application events from iOS.
    [Register("AppDelegate")]
    public partial class AppDelegate : AvaloniaAppDelegate<App>
    {
        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            RegisteredServices.Collection.AddSingleton<ISecureStorage, SecureStorage.SecureStorage>();
            RegisteredServices.Collection.AddSingleton<IServiceDiscovery, ServiceDiscovery.ServiceDiscovery>();
            RegisteredServices.Collection.AddSingleton<IHapticFeedback, HapticFeedback.HapticFeedback>();
            RegisteredServices.Collection.AddSingleton<IShareService, ShareService>();
            return base.CustomizeAppBuilder(builder)
                .WithInterFont()
                .With(new SkiaOptions { UseOpacitySaveLayer = true });
        }

        [Export("applicationWillEnterForeground:")]
        public void WillEnterForeground(UIApplication application)
        {
            var serviceDiscovery = App.Current?.Services?.GetService<IServiceDiscovery>();
            serviceDiscovery?.StartBrowse(ITelemetryDataStoreService.ServiceType);
        }

        [Export("application:openURL:options:")]
        public bool HandleOpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        {
            HandleIncomingUrl(url);
            return true;
        }

        private static void HandleIncomingUrl(NSUrl url)
        {
            var accessing = url.StartAccessingSecurityScopedResource();
            try
            {
                var path = url.Path;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return;

                var dest = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
                File.Copy(path, dest, overwrite: true);

                var inbox = App.Current?.Services?.GetService<IGpxInboxService>();
                inbox?.NotifyFileReceived(dest);
            }
            catch (Exception)
            {
                // Open-in is best-effort; a missing inbox service means the UI is not ready yet.
            }
            finally
            {
                if (accessing)
                    url.StopAccessingSecurityScopedResource();
            }
        }
    }
}