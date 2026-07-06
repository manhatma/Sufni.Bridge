using System;
using Avalonia;
using Avalonia.Controls;

namespace Sufni.Bridge.Views.Controls;

/// <summary>
/// A single row inside the expanded sub-session list of a combined session.
/// Renders itself recursively so combined sessions nested inside other combined
/// sessions (up to the max combine depth) also get a chain-icon toggle and their
/// own indented sub-session list.
/// </summary>
public partial class SubSessionRow : UserControl
{
    // Base left padding used by the first (level 1) sub-session row, and the
    // additional step applied per extra nesting level below that.
    private const double BaseIndent = 50;
    private const double IndentStep = 30;

    /// <summary>
    /// Nesting level of this row (1 = direct child of the top-level combined session).
    /// Used to step the left indent further for each additional nesting level.
    /// </summary>
    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<SubSessionRow, int>(nameof(Level), 1);

    public int Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly StyledProperty<int> NextLevelProperty =
        AvaloniaProperty.Register<SubSessionRow, int>(nameof(NextLevel), 2);

    /// <summary>Level to assign to a nested SubSessionRow one level deeper than this one.</summary>
    public int NextLevel
    {
        get => GetValue(NextLevelProperty);
        private set => SetValue(NextLevelProperty, value);
    }

    public static readonly StyledProperty<Thickness> RowPaddingProperty =
        AvaloniaProperty.Register<SubSessionRow, Thickness>(nameof(RowPadding));

    public Thickness RowPadding
    {
        get => GetValue(RowPaddingProperty);
        private set => SetValue(RowPaddingProperty, value);
    }

    public static readonly StyledProperty<Thickness> SeparatorMarginProperty =
        AvaloniaProperty.Register<SubSessionRow, Thickness>(nameof(SeparatorMargin));

    public Thickness SeparatorMargin
    {
        get => GetValue(SeparatorMarginProperty);
        private set => SetValue(SeparatorMarginProperty, value);
    }

    public SubSessionRow()
    {
        InitializeComponent();
        UpdateIndent(Level);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty)
            UpdateIndent(change.GetNewValue<int>());
    }

    private void UpdateIndent(int level)
    {
        var indent = BaseIndent + Math.Max(0, level - 1) * IndentStep;
        RowPadding = new Thickness(indent, 0, 10, 0);
        SeparatorMargin = new Thickness(indent - 10, 0, 0, 0);
        NextLevel = level + 1;
    }
}
