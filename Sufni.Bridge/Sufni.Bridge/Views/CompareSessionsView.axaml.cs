using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Sufni.Bridge.ViewModels;

namespace Sufni.Bridge.Views;

public partial class CompareSessionsView : UserControl
{
    public CompareSessionsView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not CompareSessionsViewModel vm) return;

        BuildTable(FrontWheelHeader, FrontWheelTable, "FRONT WHEEL", vm);
        BuildTable(RearWheelHeader, RearWheelTable, "REAR WHEEL", vm);

        await Task.Run(() => vm.GenerateComparePlots());
    }

    private static void BuildTable(Grid header, ItemsControl table, string title, CompareSessionsViewModel vm)
    {
        var colCount = vm.SessionCount;

        // Build column definitions: Label + N session columns
        var colDefs = "130";
        for (var i = 0; i < colCount; i++)
            colDefs += ",*";

        header.ColumnDefinitions = ColumnDefinitions.Parse(colDefs);

        // Title row
        var titleBorder = CreateHeaderCell(title);
        Grid.SetColumn(titleBorder, 0);
        header.Children.Add(titleBorder);

        for (var i = 0; i < colCount; i++)
        {
            var cell = CreateHeaderCell(vm.SessionNames[i]);
            Grid.SetColumn(cell, i + 1);
            header.Children.Add(cell);
        }

        // Data rows template
        table.ItemTemplate = new FuncDataTemplate<CompareTableRow>((row, _) =>
        {
            if (row is null) return new TextBlock { Text = "" };

            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(colDefs) };

            var labelBorder = new Border
            {
                Classes = { "compare-cell" },
                Child = new TextBlock { Text = row.Label }
            };
            Grid.SetColumn(labelBorder, 0);
            grid.Children.Add(labelBorder);

            for (var i = 0; i < row.Values.Count && i < colCount; i++)
            {
                var valueBorder = new Border
                {
                    Classes = { "compare-cell" },
                    Child = new TextBlock
                    {
                        Text = row.Values[i],
                        HorizontalAlignment = HorizontalAlignment.Right
                    }
                };
                Grid.SetColumn(valueBorder, i + 1);
                grid.Children.Add(valueBorder);
            }

            return grid;
        });
    }

    private static Border CreateHeaderCell(string text)
    {
        return new Border
        {
            Classes = { "compare-header-cell" },
            Child = new TextBlock
            {
                Text = text,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#15191c")),
                FontSize = 11
            }
        };
    }
}
