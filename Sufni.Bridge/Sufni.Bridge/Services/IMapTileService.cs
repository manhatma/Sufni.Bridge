using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Services;

public interface IMapTileService
{
    Task<SKBitmap?> GetMosaicAsync(MapBounds bounds, CancellationToken cancellationToken);
}
