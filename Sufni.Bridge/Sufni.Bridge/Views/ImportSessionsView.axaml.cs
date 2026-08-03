using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Sufni.Bridge.Views;

public partial class ImportSessionsView : UserControl
{
    public ImportSessionsView()
    {
        InitializeComponent();
    }

    private void ClearTextBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox textBox })
        {
            textBox.Text = string.Empty;
            Dispatcher.UIThread.Post(() => textBox.Focus());
        }
    }
}
