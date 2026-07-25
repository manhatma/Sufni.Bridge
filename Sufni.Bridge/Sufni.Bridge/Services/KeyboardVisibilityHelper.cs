using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sufni.Bridge.ViewModels;

namespace Sufni.Bridge.Services;

public sealed class KeyboardVisibilityHelper
{
    private const double ScrollSlack = 24;
    private TextBox? focusedTextBox;
    private TextBox? layoutUpdatedTextBox;
    private bool scrollPosted;
    private bool viewModelAttached;

    public KeyboardVisibilityHelper()
    {
        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>(OnLostFocus);
    }

    public void Attach(MainViewModel viewModel)
    {
        if (viewModelAttached) return;
        viewModelAttached = true;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void InputPaneOpened()
    {
        RequestScroll();
    }

    private void OnGotFocus(TextBox _, GotFocusEventArgs e)
    {
        var textBox = FindTextBox(e.Source);
        if (textBox == null) return;

        if (!ReferenceEquals(focusedTextBox, textBox))
        {
            ClearLayoutUpdatedRetry();
            focusedTextBox = textBox;
        }

        RequestScroll();
    }

    private void OnLostFocus(TextBox _, RoutedEventArgs e)
    {
        var textBox = FindTextBox(e.Source);
        if (!ReferenceEquals(focusedTextBox, textBox)) return;

        focusedTextBox = null;
        scrollPosted = false;
        ClearLayoutUpdatedRetry();
    }

    private static TextBox? FindTextBox(object? source)
    {
        return source is Visual visual
            ? visual.FindAncestorOfType<TextBox>(includeSelf: true)
            : null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.KeyboardInset))
        {
            RequestScroll();
        }
    }

    private void RequestScroll()
    {
        if (focusedTextBox == null || scrollPosted) return;
        scrollPosted = true;

        Dispatcher.UIThread.Post(() =>
        {
            scrollPosted = false;
            var textBox = focusedTextBox;
            if (textBox == null) return;

            ScrollIntoView(textBox);
            SubscribeForLayoutUpdatedRetry(textBox);
        }, DispatcherPriority.Background);
    }

    private void SubscribeForLayoutUpdatedRetry(TextBox textBox)
    {
        if (layoutUpdatedTextBox != null) return;

        layoutUpdatedTextBox = textBox;
        textBox.LayoutUpdated += OnTextBoxLayoutUpdated;
    }

    private void OnTextBoxLayoutUpdated(object? sender, EventArgs e)
    {
        var textBox = sender as TextBox;
        ClearLayoutUpdatedRetry();

        if (textBox != null && ReferenceEquals(focusedTextBox, textBox))
        {
            ScrollIntoView(textBox);
        }
    }

    private void ClearLayoutUpdatedRetry()
    {
        if (layoutUpdatedTextBox == null) return;

        layoutUpdatedTextBox.LayoutUpdated -= OnTextBoxLayoutUpdated;
        layoutUpdatedTextBox = null;
    }

    private static void ScrollIntoView(TextBox textBox)
    {
        var scrollViewer = textBox.FindAncestorOfType<ScrollViewer>();
        if (scrollViewer == null)
        {
            textBox.BringIntoView();
            return;
        }

        var transform = textBox.TransformToVisual(scrollViewer);
        if (transform == null)
        {
            textBox.BringIntoView();
            return;
        }

        var bounds = new Rect(textBox.Bounds.Size).TransformToAABB(transform.Value);
        var targetOffset = scrollViewer.Offset.Y;

        if (bounds.Bottom > scrollViewer.Viewport.Height)
        {
            targetOffset += bounds.Bottom - scrollViewer.Viewport.Height + ScrollSlack;
        }
        else if (bounds.Top < 0)
        {
            targetOffset += bounds.Top - ScrollSlack;
        }

        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        targetOffset = Math.Clamp(targetOffset, 0, maximumOffset);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetOffset);
    }
}
