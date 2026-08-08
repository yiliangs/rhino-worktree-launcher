using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace RhinoWorktreeLauncher;

public partial class AddProjectDialog : Window
{
    private string? _driverPath;

    public AddProjectDialog(string projectPath)
    {
        InitializeComponent();
        ProjectPath = Path.GetFullPath(projectPath);
        ProjectPathText.Text = ProjectPath;
        Loaded += (_, _) => ApplyOwnerTheme();
    }

    public string ProjectPath { get; }
    public bool ReadRemote => RemoteReadToggle.IsChecked == true;
    public BuildMode BuildMode => CustomDriverChoice.IsChecked == true
        ? BuildMode.ImportedDriver
        : BuildMode.Typed;
    public string? ImportedDriverPath => CustomDriverChoice.IsChecked == true ? _driverPath : null;

    private void BuildChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        DriverPicker.Visibility = CustomDriverChoice.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void ChooseDriver_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Choose a PowerShell build driver",
            Filter = "PowerShell driver (*.ps1)|*.ps1|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _driverPath = Path.GetFullPath(dialog.FileName);
        DriverPathText.Text = _driverPath;
        DriverPathText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (CustomDriverChoice.IsChecked == true &&
            string.IsNullOrWhiteSpace(_driverPath))
        {
            ValidationText.Text = "Choose the driver RWL should import.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ApplyOwnerTheme()
    {
        if (Owner is null)
            return;
        foreach (string key in new[]
        {
            "WindowBrush",
            "PanelBrush",
            "ControlBrush",
            "ControlHoverBrush",
            "PanelBorderBrush",
            "DividerBrush",
            "TextStrongBrush",
            "TextBodyBrush",
            "TextSecondaryBrush",
            "TextMutedBrush",
            "PrimaryBrush",
            "PrimaryTextBrush",
            "AheadTextBrush"
        })
        {
            object? resource = Owner.TryFindResource(key);
            if (resource is not null)
                Resources[key == "AheadTextBrush" ? "AccentBrush" : key] = resource;
        }
    }
}
