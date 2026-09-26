using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Services;

public interface IMapTileService
{
    Task<MapMosaic?> GetMosaicAsync(MapBounds extent, CancellationToken cancellationToken);
}

public sealed record MapMosaic(SKBitmap Bitmap, MapBounds Bounds);
