using Avalonia.Controls;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.Views.SessionPages;

public partial class CropPageView : UserControl
{
    private CropPageViewModel? _vm;

    public CropPageView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as CropPageViewModel;
            UpdateOverlay();

            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CropPageViewModel.CropStartSample)
                           or nameof(CropPageViewModel.CropEndSample)
                           or nameof(CropPageViewModel.TotalSamples))
        {
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        if (_vm is null || _vm.TotalSamples <= 0)
        {
            OverlayGrid.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Star);
            OverlayGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            OverlayGrid.ColumnDefinitions[2].Width = new GridLength(0, GridUnitType.Star);
            return;
        }

        var total = _vm.TotalSamples;
        var start = System.Math.Max(0, System.Math.Min(_vm.CropStartSample, total));
        var end   = System.Math.Max(start, System.Math.Min(_vm.CropEndSample, total));

        OverlayGrid.ColumnDefinitions[0].Width = new GridLength(start,         GridUnitType.Star);
        OverlayGrid.ColumnDefinitions[1].Width = new GridLength(end - start,   GridUnitType.Star);
        OverlayGrid.ColumnDefinitions[2].Width = new GridLength(total - end,   GridUnitType.Star);
    }
}
