using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sufni.Bridge.Services;

public class FilesService : IFilesService
{
    private TopLevel? target;

    public void SetTarget(TopLevel? newTarget)
    {
        target = newTarget;
    }

    public async Task<IStorageFile?> OpenLeverageRatioFileAsync()
    {
        Debug.Assert(target != null, nameof(target) + " != null");

        var files = await target.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = "Open Leverage Ratio file",
            AllowMultiple = false
        });

        return files.Count == 1 ? files[0] : null;
    }

    public async Task<IStorageFile?> OpenGpxFileAsync()
    {
        Debug.Assert(target != null, nameof(target) + " != null");

        var files = await target.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "GPX importieren",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GPX")
                {
                    Patterns = ["*.gpx"],
                    AppleUniformTypeIdentifiers = ["com.topografix.gpx", "public.xml"],
                    MimeTypes = ["application/gpx+xml", "application/xml", "text/xml"]
                }
            ]
        });

        return files.Count == 1 ? files[0] : null;
    }

    public async Task<IStorageFolder?> OpenDataStoreFolderAsync()
    {
        Debug.Assert(target != null, nameof(target) + " != null");

        var folders = await target.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions()
        {
            Title = "Open SST data store",
            AllowMultiple = false,
        });

        return folders.Count == 1 ? folders[0] : null;
    }
}