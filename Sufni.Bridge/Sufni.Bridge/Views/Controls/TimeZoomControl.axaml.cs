using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.Views.Controls;

public partial class TimeZoomControl : UserControl
{
    private TimeZoomViewModel? _vm;

    /// <summary>
    /// The session-overview strip to display. Set per page (travel on Spring, velocity on Damper,
    /// acceleration on Misc) from the shared view-model's domain-specific mini-map, while the window
    /// state (slider, overlay band) stays bound to the shared view-model.
    /// </summary>
    public static readonly StyledProperty<IImage?> MiniMapProperty =
        AvaloniaProperty.Register<TimeZoomControl, IImage?>(nameof(MiniMap));

    public IImage? MiniMap
    {
        get => GetValue(MiniMapProperty);
        set => SetValue(MiniMapProperty, value);
    }

    public TimeZoomControl()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as TimeZoomViewModel;
            UpdateOverlay();

            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimeZoomViewModel.StartFraction)
                           or nameof(TimeZoomViewModel.WidthFraction)
                           or nameof(TimeZoomViewModel.WindowSeconds))
        {
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        if (_vm is null || _vm.TotalDurationSeconds <= 0)
        {
            OverlayGrid.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Star);
            OverlayGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            OverlayGrid.ColumnDefinitions[2].Width = new GridLength(0, GridUnitType.Star);
            return;
        }

        var start = Math.Max(0, Math.Min(1, _vm.StartFraction));
        var width = Math.Max(0, Math.Min(1 - start, _vm.WidthFraction));
        var end = Math.Max(0, 1 - start - width);

        OverlayGrid.ColumnDefinitions[0].Width = new GridLength(start, GridUnitType.Star);
        OverlayGrid.ColumnDefinitions[1].Width = new GridLength(width, GridUnitType.Star);
        OverlayGrid.ColumnDefinitions[2].Width = new GridLength(end, GridUnitType.Star);
    }
}
