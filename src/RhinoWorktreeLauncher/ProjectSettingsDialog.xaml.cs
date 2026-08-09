using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RhinoWorktreeLauncher;

public partial class ProjectSettingsDialog : Window
{
    private readonly bool _existingDriverImported;
    private readonly string _projectPath;
    private string? _driverPath;

    public ProjectSettingsDialog(ProjectRegistration registration)
    {
        InitializeComponent();
        _projectPath = Path.GetFullPath(registration.PrimaryCheckout);
        _existingDriverImported = registration.BuildProfile.Mode == BuildMode.ImportedDriver;

        ProjectNameText.Text = registration.DisplayName;
        ProjectInitialText.Text = string.IsNullOrWhiteSpace(registration.DisplayName)
            ? "?"
            : registration.DisplayName.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture);
        ProjectPathText.ToolTip = _projectPath;
        RemoteReadToggle.IsChecked = registration.Access.ReadRemote;

        if (_existingDriverImported)
        {
            CustomDriverChoice.IsChecked = true;
            DriverPathText.Text = "Current imported driver (choose a file to replace)";
        }

        Loaded += (_, _) =>
        {
            ApplyOwnerTheme();
            UpdateProjectPathText();
        };
    }

    public bool ReadRemote => RemoteReadToggle.IsChecked == true;
    public BuildMode BuildMode => CustomDriverChoice.IsChecked == true
        ? BuildMode.ImportedDriver
        : BuildMode.Typed;
    public string? ImportedDriverPath => CustomDriverChoice.IsChecked == true ? _driverPath : null;
    public bool ClearCache => ClearCacheToggle.IsChecked == true;

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
        DriverPathText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CustomDriverChoice.IsChecked == true &&
            string.IsNullOrWhiteSpace(_driverPath) &&
            !_existingDriverImported)
        {
            ValidationText.Text = "Choose the driver RWL should import.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ProjectPathText_SizeChanged(
        object sender,
        SizeChangedEventArgs e) => UpdateProjectPathText();

    private void UpdateProjectPathText()
    {
        ProjectPathText.Text = TruncatePathFromStart(
            _projectPath,
            Math.Max(0, ProjectPathText.ActualWidth));
    }

    private string TruncatePathFromStart(string path, double availableWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || availableWidth <= 0)
            return path;

        Typeface typeface = new Typeface(
            (FontFamily)Application.Current.FindResource("MonoFont"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Measure(string value) => new FormattedText(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            11,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;

        if (Measure(path) <= availableWidth)
            return path;

        const string prefix = "…";
        int low = 0;
        int high = path.Length;
        while (low < high)
        {
            int length = (low + high + 1) / 2;
            string candidate = prefix + path.Substring(path.Length - length);
            if (Measure(candidate) <= availableWidth)
                low = length;
            else
                high = length - 1;
        }

        return prefix + path.Substring(path.Length - low);
    }

    private void ApplyOwnerTheme()
    {
        if (Owner is null)
            return;

        foreach (string key in new[]
        {
            "WindowBrush",
            "PanelBrush",
            "FooterBrush",
            "ControlBrush",
            "ControlHoverBrush",
            "RowHoverBrush",
            "RowActiveBrush",
            "TrackBrush",
            "TrackCenterBrush",
            "PanelBorderBrush",
            "DividerBrush",
            "ControlBorderBrush",
            "ControlHoverBorderBrush",
            "BadgeBrush",
            "BadgeBorderBrush",
            "TextStrongBrush",
            "TextBodyBrush",
            "TextSecondaryBrush",
            "TextMutedBrush",
            "TextBadgeBrush",
            "ControlTextBrush",
            "AccentBrush",
            "PatternBrush",
            "PrimaryBrush",
            "PrimaryHoverBrush",
            "PrimaryTextBrush",
            "ControlShadowEffect",
            "RowActiveShadowEffect",
            "PrimaryShadowEffect"
        })
        {
            object? resource = Owner.TryFindResource(key);
            if (resource is not null)
                Resources[key] = resource;
        }

        CopyOwnerResource("BehindTextBrush", "ValidationBrush");
        ApplyToggleTheme();
        Resources["LogoShadowEffect"] = CreateLogoShadow();
    }

    private void CopyOwnerResource(string ownerKey, string localKey)
    {
        object? resource = Owner?.TryFindResource(ownerKey);
        if (resource is not null)
            Resources[localKey] = resource;
    }

    private void ApplyToggleTheme()
    {
        bool isLight = IsLightTheme();
        Resources["ToggleOnBrush"] = CreateBrush(isLight ? "#6BA36F" : "#5F8A5C");
        Resources["ToggleOnBorderBrush"] = CreateBrush(isLight ? "#5D9161" : "#537A51");
        Resources["ToggleOffBrush"] = CreateBrush(isLight ? "#E2E5EA" : "#24272C");
        Resources["ToggleOffBorderBrush"] = CreateBrush(isLight ? "#D0D4DA" : "#31353B");
        Resources["ToggleKnobBrush"] = CreateBrush(isLight ? "#FFFFFF" : "#F0F2F5");
        Resources["ToggleKnobOffBrush"] = CreateBrush(isLight ? "#FFFFFF" : "#7D848D");
    }

    private bool IsLightTheme() =>
        ((SolidColorBrush)FindResource("WindowBrush")).Color.R > 128;

    private static SolidColorBrush CreateBrush(string value) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    private DropShadowEffect CreateLogoShadow()
    {
        bool isLight = IsLightTheme();
        return new DropShadowEffect
        {
            BlurRadius = isLight ? 10 : 12,
            Direction = 270,
            ShadowDepth = isLight ? 3 : 4,
            Opacity = isLight ? 0.3 : 0.5,
            Color = isLight ? Color.FromRgb(24, 30, 40) : Colors.Black
        };
    }
}
