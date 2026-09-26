using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Sufni.Bridge.Models;

namespace Sufni.Bridge.Services;

public sealed class MapTileService : IMapTileService, IDisposable
{
    public const string Attribution = "Esri, Maxar, Earthstar Geographics";

    private const int TileSize = 256;
    private const int MaxZoom = 19;
    private const int MaxTilesPerSide = 6;
    private const double OriginShift = 20037508.342789244;
    private const string TileUrl =
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";

    private readonly HttpClient httpClient;
    private readonly string? cacheRoot;
    private readonly Dictionary<(int z, int x, int y), byte[]> memoryCache = new();
    private readonly bool diskAvailable;

    public MapTileService()
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var canUseDisk = !OperatingSystem.IsBrowser();
        if (canUseDisk)
        {
            try
            {
                cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Sufni.Bridge",
                    "tiles");
                Directory.CreateDirectory(cacheRoot);
            }
            catch
            {
                cacheRoot = null;
                canUseDisk = false;
            }
        }

        diskAvailable = canUseDisk;
    }

    public async Task<MapMosaic?> GetMosaicAsync(MapBounds extent, CancellationToken cancellationToken)
    {
        if (extent.Width <= 0 || extent.Height <= 0)
            return null;

        var padded = extent;
        var z = ChooseZoom(padded);
        var n = 1 << z;
        var (x0, y0) = MercatorToTile(padded.MinX, padded.MaxY, z);
        var (x1, y1) = MercatorToTile(padded.MaxX, padded.MinY, z);
        x0 = Math.Clamp(x0, 0, n - 1);
        y0 = Math.Clamp(y0, 0, n - 1);
        x1 = Math.Clamp(x1, 0, n - 1);
        y1 = Math.Clamp(y1, 0, n - 1);
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);

        var cols = x1 - x0 + 1;
        var rows = y1 - y0 + 1;
        var mosaic = new SKBitmap(cols * TileSize, rows * TileSize);
        using var canvas = new SKCanvas(mosaic);
        canvas.Clear(new SKColor(0x20, 0x26, 0x2B));

        var any = false;
        try
        {
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = await LoadTileAsync(z, x, y, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) continue;
                    using var tile = SKBitmap.Decode(bytes);
                    if (tile is null) continue;
                    canvas.DrawBitmap(tile, (x - x0) * TileSize, (y - y0) * TileSize);
                    any = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            mosaic.Dispose();
            throw;
        }
        catch
        {
            mosaic.Dispose();
            return null;
        }

        if (!any)
        {
            mosaic.Dispose();
            return null;
        }

        var mosaicMinX = TileMinX(x0, z);
        var mosaicMaxX = TileMinX(x1 + 1, z);
        var mosaicMaxY = TileMaxY(y0, z);
        var mosaicMinY = TileMaxY(y1 + 1, z);

        var crop = CropToBounds(mosaic, mosaicMinX, mosaicMinY, mosaicMaxX, mosaicMaxY, padded);
        mosaic.Dispose();
        return new MapMosaic(crop, padded);
    }

    private static int ChooseZoom(MapBounds padded)
    {
        var chosen = 0;
        for (var z = 0; z <= MaxZoom; z++)
        {
            var (x0, y0) = MercatorToTile(padded.MinX, padded.MaxY, z);
            var (x1, y1) = MercatorToTile(padded.MaxX, padded.MinY, z);
            var nx = Math.Abs(x1 - x0) + 1;
            var ny = Math.Abs(y1 - y0) + 1;
            if (nx > MaxTilesPerSide || ny > MaxTilesPerSide)
                break;
            chosen = z;
        }

        return chosen;
    }

    private static (int x, int y) MercatorToTile(double x, double y, int z)
    {
        var n = 1 << z;
        var world = OriginShift * 2.0;
        var tx = (int)Math.Floor((x + OriginShift) / world * n);
        var ty = (int)Math.Floor((OriginShift - y) / world * n);
        return (tx, ty);
    }

    private static double TileMinX(int x, int z)
    {
        var n = (double)(1 << z);
        return x / n * (OriginShift * 2.0) - OriginShift;
    }

    private static double TileMaxY(int y, int z)
    {
        var n = (double)(1 << z);
        return OriginShift - y / n * (OriginShift * 2.0);
    }

    private static SKBitmap CropToBounds(
        SKBitmap mosaic,
        double mosaicMinX, double mosaicMinY, double mosaicMaxX, double mosaicMaxY,
        MapBounds bounds)
    {
        var mosaicW = mosaicMaxX - mosaicMinX;
        var mosaicH = mosaicMaxY - mosaicMinY;
        if (mosaicW <= 0 || mosaicH <= 0)
            return mosaic.Copy();

        var left = (bounds.MinX - mosaicMinX) / mosaicW * mosaic.Width;
        var right = (bounds.MaxX - mosaicMinX) / mosaicW * mosaic.Width;
        var top = (mosaicMaxY - bounds.MaxY) / mosaicH * mosaic.Height;
        var bottom = (mosaicMaxY - bounds.MinY) / mosaicH * mosaic.Height;

        var src = SKRect.Intersect(
            SKRect.Create(
                (float)left,
                (float)top,
                (float)Math.Max(1, right - left),
                (float)Math.Max(1, bottom - top)),
            SKRect.Create(0, 0, mosaic.Width, mosaic.Height));
        if (src.Width < 1 || src.Height < 1)
            return mosaic.Copy();

        var cropped = new SKBitmap((int)Math.Ceiling(src.Width), (int)Math.Ceiling(src.Height));
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(mosaic, src, SKRect.Create(0, 0, cropped.Width, cropped.Height));
        return cropped;
    }

    private async Task<byte[]?> LoadTileAsync(int z, int x, int y, CancellationToken cancellationToken)
    {
        var key = (z, x, y);
        lock (memoryCache)
        {
            if (memoryCache.TryGetValue(key, out var cached))
                return cached;
        }

        if (diskAvailable && cacheRoot is not null)
        {
            var path = TilePath(z, x, y);
            try
            {
                if (File.Exists(path))
                {
                    var fromDisk = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    Remember(key, fromDisk);
                    return fromDisk;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to the network.
            }
        }

        try
        {
            // Esri World Imagery uses z/y/x, not z/x/y.
            var url = string.Format(TileUrl, z, y, x);
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return null;
            Remember(key, bytes);
            if (diskAvailable && cacheRoot is not null)
            {
                try
                {
                    var path = TilePath(z, x, y);
                    var dir = Path.GetDirectoryName(path);
                    if (dir is not null)
                        Directory.CreateDirectory(dir);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Memory cache is enough.
                }
            }

            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void Remember((int z, int x, int y) key, byte[] bytes)
    {
        lock (memoryCache)
        {
            if (memoryCache.Count > 256)
                memoryCache.Clear();
            memoryCache[key] = bytes;
        }
    }

    private string TilePath(int z, int x, int y) =>
        Path.Combine(cacheRoot!, z.ToString(), x.ToString(), $"{y}.jpg");

    public void Dispose() => httpClient.Dispose();
}
