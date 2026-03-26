using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Sufni.Bridge.Services;

public static class PdfShareService
{
    public static async Task SharePdfAsync(string pdfPath, string sessionName)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null) return;

        // LaunchFileInfoAsync takes a System.IO.FileInfo and opens the system share sheet / viewer
        await topLevel.Launcher.LaunchFileInfoAsync(new FileInfo(pdfPath));
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return TopLevel.GetTopLevel(desktop.MainWindow);

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            return TopLevel.GetTopLevel(singleView.MainView);

        return null;
    }
}
