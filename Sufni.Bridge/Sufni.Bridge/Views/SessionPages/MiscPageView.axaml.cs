using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sufni.Bridge.ViewModels.SessionPages;

namespace Sufni.Bridge.Views.SessionPages;

public partial class MiscPageView : UserControl
{
    private MiscPageViewModel? _vm;
    private IDisposable? _boundsSubscription;
    private Point _sliderPressPoint;
    private double _sliderPressValue;
    private bool _sliderPressed;
    private const double TapSlopPixels = 6.0;

    public MiscPageView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachVm();
        AttachBounds();
        MarkerSlider.AddHandler(PointerPressedEvent, OnMarkerSliderPressed, RoutingStrategies.Tunnel);
        MarkerSlider.AddHandler(PointerReleasedEvent, OnMarkerSliderReleased, RoutingStrategies.Tunnel);
        MarkerSlider.AddHandler(PointerCaptureLostEvent, OnMarkerSliderCaptureLost, RoutingStrategies.Tunnel);
        UpdateMarkerPosition();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        MarkerSlider.RemoveHandler(PointerPressedEvent, OnMarkerSliderPressed);
        MarkerSlider.RemoveHandler(PointerReleasedEvent, OnMarkerSliderReleased);
        MarkerSlider.RemoveHandler(PointerCaptureLostEvent, OnMarkerSliderCaptureLost);
        DetachVm();
        DetachBounds();
        base.OnUnloaded(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (!IsLoaded) return;
        DetachVm();
        AttachVm();
        UpdateMarkerPosition();
    }

    private void AttachVm()
    {
        _vm = DataContext as MiscPageViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void DetachVm()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void AttachBounds()
    {
        DetachBounds();
        _boundsSubscription = MapImage.GetObservable(BoundsProperty)
            .Subscribe(new ActionObserver<Rect>(_ => UpdateMarkerPosition()));
    }

    private void DetachBounds()
    {
        _boundsSubscription?.Dispose();
        _boundsSubscription = null;
    }

    // A slider normally jumps to the tapped position. Here a tap toggles the marker instead,
    // so the value is restored; dragging still moves it, and the nudge buttons cover fine
    // positioning.
    private void OnMarkerSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        _sliderPressed = true;
        _sliderPressPoint = e.GetPosition(MarkerSlider);
        _sliderPressValue = _vm?.MarkerSeconds ?? 0;
    }

    private void OnMarkerSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _sliderPressed = false;
    }

    private void OnMarkerSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_sliderPressed || _vm is null)
        {
            _sliderPressed = false;
            return;
        }

        var pos = e.GetPosition(MarkerSlider);
        var dx = pos.X - _sliderPressPoint.X;
        var dy = pos.Y - _sliderPressPoint.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < TapSlopPixels)
        {
            _vm.MarkerSeconds = _sliderPressValue;
            _vm.MarkerEnabled = !_vm.MarkerEnabled;
        }

        _sliderPressed = false;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MiscPageViewModel.MarkerX)
                           or nameof(MiscPageViewModel.MarkerY)
                           or nameof(MiscPageViewModel.MarkerDirX)
                           or nameof(MiscPageViewModel.MarkerDirY)
                           or nameof(MiscPageViewModel.MarkerOnTrack)
                           or nameof(MiscPageViewModel.MarkerShown)
                           or nameof(MiscPageViewModel.MarkerValueLabel)
                           or nameof(MiscPageViewModel.MapPixelWidth)
                           or nameof(MiscPageViewModel.MapPixelHeight)
                           or nameof(MiscPageViewModel.StatMaxBounds)
                           or nameof(MiscPageViewModel.StatMinBounds))
        {
            UpdateMarkerPosition();
        }
    }

    private void UpdateMarkerPosition()
    {
        PlaceOverlayControls();
        if (_vm is null) return;
        var b = MapImage.Bounds;
        if (b.Width <= 0 || b.Height <= 0 || _vm.MapPixelWidth <= 0 || _vm.MapPixelHeight <= 0)
            return;

        var scale = Math.Min(b.Width / _vm.MapPixelWidth, b.Height / _vm.MapPixelHeight);
        var offX = (b.Width - _vm.MapPixelWidth * scale) / 2.0;
        var offY = (b.Height - _vm.MapPixelHeight * scale) / 2.0;

        var px = b.X + offX + _vm.MarkerX * scale;
        var py = b.Y + offY + _vm.MarkerY * scale;

        // Normal to the travel direction. Same convention as the max marker in the renderer,
        // so the data marker and the statistic markers mean the same side.
        var dx = _vm.MarkerDirX;
        var dy = _vm.MarkerDirY;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        var nx = -dy;
        var ny = dx;

        const double DotRadius = 4.0;      // MarkerDot.Width / 2
        // Leader lengths tried in order, in points. In a crowded corner both sides of the short
        // leader can land on a statistic flag; stepping further out is the only way to clear it
        // without moving the dot off the edge it belongs to.
        double[] leaders = [26.0, 42.0, 58.0, 74.0];

        MarkerFlag.Measure(Size.Infinity);
        var fw = MarkerFlag.DesiredSize.Width;
        var fh = MarkerFlag.DesiredSize.Height;

        // The statistic markers' flags are wide rectangles beside their dots. Distance to
        // the dot alone therefore says nothing about whether the flags overlap.
        var maxRect = ToControlRect(_vm.StatMaxBounds, b, offX, offY, scale);
        var minRect = ToControlRect(_vm.StatMinBounds, b, offX, offY, scale);
        var chosenSide = 1.0;
        var chosenLeader = leaders[0];
        var chosenFlag = default(Rect);
        var bestOverlap = double.PositiveInfinity;
        var bestClearance = double.NegativeInfinity;
        foreach (var leader in leaders)
        for (var i = 0; i < 2; i++)
        {
            var s = i == 0 ? 1.0 : -1.0;
            var ex = px + nx * s * leader;
            var ey = py + ny * s * leader;
            var flagLeft = nx * s >= 0 ? ex + 3.0 : ex - 3.0 - fw;
            var flagTop = ey - fh / 2.0;
            flagLeft = Math.Clamp(flagLeft, b.X, Math.Max(b.X, b.X + b.Width - fw));
            flagTop  = Math.Clamp(flagTop,  b.Y, Math.Max(b.Y, b.Y + b.Height - fh));
            var candidate = new Rect(flagLeft, flagTop, fw, fh);

            var overlap = IntersectionArea(candidate, maxRect) + IntersectionArea(candidate, minRect);
            var cx = candidate.Center.X;
            var cy = candidate.Center.Y;
            var clearance = double.MaxValue;
            if (maxRect is { } mx)
            {
                var mdx = cx - mx.Center.X;
                var mdy = cy - mx.Center.Y;
                clearance = Math.Min(clearance, Math.Sqrt(mdx * mdx + mdy * mdy));
            }
            if (minRect is { } mn)
            {
                var mdx = cx - mn.Center.X;
                var mdy = cy - mn.Center.Y;
                clearance = Math.Min(clearance, Math.Sqrt(mdx * mdx + mdy * mdy));
            }

            if (overlap < bestOverlap || (overlap == bestOverlap && clearance > bestClearance))
            {
                bestOverlap = overlap;
                bestClearance = clearance;
                chosenSide = s;
                chosenLeader = leader;
                chosenFlag = candidate;
            }
        }

        // A shorter leader that already clears both flags is always preferred, so the search
        // above keeps the first zero-overlap candidate it meets and never trades it for a
        // longer one with the same score.

        nx *= chosenSide;
        ny *= chosenSide;

        var sx = px + nx * DotRadius * 1.4;
        var sy = py + ny * DotRadius * 1.4;
        var endX = px + nx * chosenLeader;
        var endY = py + ny * chosenLeader;

        MarkerLeader.StartPoint = new Point(sx, sy);
        MarkerLeader.EndPoint = new Point(endX, endY);

        Canvas.SetLeft(MarkerDot, px - MarkerDot.Width / 2.0);
        Canvas.SetTop(MarkerDot, py - MarkerDot.Height / 2.0);
        Canvas.SetLeft(MarkerFlag, chosenFlag.X);
        Canvas.SetTop(MarkerFlag, chosenFlag.Y);
    }

    private static Rect? ToControlRect(MapPixelRect? src, Rect b, double offX, double offY, double scale)
    {
        if (src is not { } v) return null;
        return new Rect(
            b.X + offX + v.X * scale,
            b.Y + offY + v.Y * scale,
            v.Width * scale,
            v.Height * scale);
    }

    private static double IntersectionArea(Rect a, Rect? other)
    {
        if (other is not { } b) return 0;
        var hit = a.Intersect(b);
        return hit.Width <= 0 || hit.Height <= 0 ? 0 : hit.Width * hit.Height;
    }

    // Distance the control bar and the aggregate pill keep to the drawn map on every side they
    // touch, in points.
    private const double OverlayInset = 12.0;

    // The image keeps the bitmap's aspect ratio, so it is narrower than the cell it sits in and
    // the overlay panels would otherwise measure their margin against the cell instead of the
    // picture. Adding the letterbox offset makes the gap identical on left, right and bottom.
    private void PlaceOverlayControls()
    {
        if (ControlBar.Parent is not Control parent) return;
        var b = MapImage.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        var right = Math.Max(0, parent.Bounds.Width - b.Right) + OverlayInset;
        var bottom = Math.Max(0, parent.Bounds.Height - b.Bottom) + OverlayInset;
        ControlBar.Margin = new Thickness(b.X + OverlayInset, 0, right, bottom);
        AggregatePill.Margin = new Thickness(0, b.Y + OverlayInset, right, 0);
    }

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
