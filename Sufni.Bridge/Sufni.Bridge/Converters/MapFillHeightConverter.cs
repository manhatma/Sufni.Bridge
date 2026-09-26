using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Sufni.Bridge.Converters;

/// <summary>
/// Height for the Summary tab's top block (track map + RUN DATA). Returns the viewport height while
/// a map exists, so map and table together fill the initial screen, and 0 when there is no map —
/// without the null check a session without a GPX track would reserve a full screen for a hidden
/// image and push RUN DATA below the fold.
/// </summary>
public sealed class MapFillHeightConverter : IMultiValueConverter
{
    public static readonly MapFillHeightConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[1] is null || values[1] == AvaloniaProperty.UnsetValue)
            return 0.0;

        return values[0] is double height && double.IsFinite(height) && height > 0 ? height : 0.0;
    }
}
