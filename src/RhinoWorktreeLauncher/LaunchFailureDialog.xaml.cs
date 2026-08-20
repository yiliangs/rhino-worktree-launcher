using System.Windows;

namespace RhinoWorktreeLauncher;

/// <summary>
/// What the surface shows when a launch fails. The diagnostic's message leads, because it is
/// written to be read at a glance; the failing tool's own output waits behind Details,
/// because it is written for whoever needs the file and the line. The launch log path is
/// there either way, since the transcript this dialog bounds is already unabridged on disk.
/// </summary>
public partial class LaunchFailureDialog : Window
{
    public LaunchFailureDialog(Diagnostic diagnostic, string? logPath)
    {
        InitializeComponent();

        CodeText.Text = diagnostic.Code;
        FailureMessageText.Text = diagnostic.Message;
        DetailText.Text = diagnostic.Detail ?? string.Empty;
        // A failure with nothing more to show offers nothing to open.
        DetailsToggle.Visibility = string.IsNullOrWhiteSpace(diagnostic.Detail)
            ? Visibility.Collapsed
            : Visibility.Visible;
        LogPathText.Text = logPath ?? string.Empty;
    }

    private void Details_Toggled(object sender, RoutedEventArgs e)
    {
        DetailPanel.Visibility = DetailsToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
