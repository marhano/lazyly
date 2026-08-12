using System.Windows;

namespace PublishTool.Gui;

/// <summary>Shows the full message text for a single Event Logs grid row -- the grid itself only
/// shows a truncated single-line summary.</summary>
public partial class EventLogDetailDialog : Wpf.Ui.Controls.FluentWindow
{
    public EventLogDetailDialog(EventLogRowViewModel row)
    {
        InitializeComponent();

        TimeTextBlock.Text = row.TimeDisplay;
        LevelTextBlock.Text = row.Level;
        SourceTextBlock.Text = row.Source;
        EventIdTextBlock.Text = row.EventId.ToString();
        MachineTextBlock.Text = row.Record.MachineName;
        MessageTextBox.Text = JsonMessageBeautifier.Beautify(row.FullMessage);

        if (row.MethodName is not null)
        {
            MethodTextBlock.Text = row.MethodName;
        }
        else
        {
            MethodRow.Height = new GridLength(0);
            MethodLabelTextBlock.Visibility = Visibility.Collapsed;
            MethodTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(MessageTextBox.Text);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
