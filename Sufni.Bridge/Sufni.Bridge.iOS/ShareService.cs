using System;
using System.Threading.Tasks;
using CoreFoundation;
using Foundation;
using Sufni.Bridge.Services;
using UIKit;

namespace Sufni.Bridge.iOS;

public class ShareService : IShareService
{
    public Task ShareFileAsync(string filePath)
    {
        var tcs = new TaskCompletionSource<bool>();

        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try
            {
                var url = NSUrl.FromFilename(filePath);
                var activityController = new UIActivityViewController(
                    new NSObject[] { url }, null);

                var presenter = GetTopmostViewController();
                if (presenter == null)
                {
                    tcs.SetResult(false);
                    return;
                }

                // iPad: UIActivityViewController needs a source rect
                if (activityController.PopoverPresentationController != null)
                {
                    activityController.PopoverPresentationController.SourceView = presenter.View!;
                    var b = presenter.View!.Bounds;
                    activityController.PopoverPresentationController.SourceRect =
                        new CoreGraphics.CGRect(b.X + b.Width / 2, b.Y + b.Height / 2, 0, 0);
                }

                activityController.CompletionWithItemsHandler =
                    (activityType, completed, returnedItems, error) => tcs.SetResult(completed);

                presenter.PresentViewController(activityController, true, null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static UIViewController? GetTopmostViewController()
    {
        UIViewController? root = null;

        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene) continue;

            // prefer key window, fall back to first window
            var windows = windowScene.Windows;
            UIWindow? window = null;
            foreach (var w in windows) { if (w.IsKeyWindow) { window = w; break; } }
            window ??= windows.Length > 0 ? windows[0] : null;
            if (window?.RootViewController != null)
            {
                root = window.RootViewController;
                break;
            }
        }

        // Traverse to the topmost presented view controller
        var top = root;
        while (top?.PresentedViewController != null)
            top = top.PresentedViewController;
        return top;
    }
}
