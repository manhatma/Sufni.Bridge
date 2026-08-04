using Avalonia.Svg.Skia;

namespace Sufni.Bridge.Extensions;

internal static class SvgHelpers
{
    // Call on background thread — SvgSource : Object, thread-safe
    internal static SvgSource? SvgToSource(string? svgXml) =>
        svgXml is null ? null : SvgSource.LoadFromSvg(svgXml);

    // Call on UI thread — SvgImage : AvaloniaObject, requires UI thread
    internal static SvgImage? SourceToImage(SvgSource? source) =>
        source is null ? null : new SvgImage { Source = source };
}
