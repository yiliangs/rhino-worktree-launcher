using System.Windows;

namespace RhinoWorktreeLauncher;

/// <summary>
/// The keys a dialog follows its owner on, and the one place they are listed. The main
/// window reads <c>AppsUseLightTheme</c> and rewrites its own palette when the system theme
/// changes, so a dialog that stated its colours and stopped there would open in the dark
/// theme over a light main window. Copying the owner's live values into the dialog's own
/// resources shadows what <c>Themes/DialogStyles.xaml</c> declares, which is why that
/// dictionary holds the values a dialog falls back to rather than the ones a user sees.
///
/// Each dialog used to carry this list itself, and the two copies had already drifted: the
/// shorter one silently left its window on the dark palette for the keys it omitted.
/// </summary>
public static class OwnerTheme
{
    /// <summary>
    /// Every key a dialog takes from its owner verbatim. A key the owner does not define is
    /// skipped, so the dialog keeps the shared dictionary's value for it.
    /// </summary>
    private static readonly string[] OwnedKeys =
    [
        "WindowBrush",
        "PanelBrush",
        "FooterBrush",
        "ControlBrush",
        "ControlHoverBrush",
        "TrackBrush",
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
        "TextFaintBrush",
        "TextBadgeBrush",
        "ControlTextBrush",
        "AccentBrush",
        "RowHoverBrush",
        "RowActiveBrush",
        "PatternBrush",
        "PrimaryBrush",
        "PrimaryHoverBrush",
        "PrimaryTextBrush",
        "DropdownMenuBrush",
        "DropdownMenuBorderBrush",
        "DropdownOpenBorderBrush",
        "DropdownDisabledBrush",
        "DropdownDisabledBorderBrush",
        "DropdownFocusRingBrush",
        "DropdownSelectedBorderBrush",
        "DropdownAccentBrush",
        "ControlShadowEffect",
        "PrimaryShadowEffect",
        "DropdownControlShadowEffect",
        "DropdownMenuShadowEffect"
    ];

    /// <summary>
    /// The owner names the colour of a count that has fallen behind; a dialog spends the
    /// same colour on the message that says a choice is missing. One value, two readings,
    /// so the key is renamed on the way across rather than restated.
    /// </summary>
    private const string ValidationSourceKey = "BehindTextBrush";
    private const string ValidationKey = "ValidationBrush";

    /// <summary>
    /// Applies the owner's theme to <paramref name="dialog"/>. A dialog with no owner keeps
    /// the shared dictionary's values, which is the case in a test fixture and at design
    /// time and never in the running application.
    /// </summary>
    public static void Apply(Window dialog)
    {
        if (dialog.Owner is not null)
            Apply(dialog, dialog.Owner);
    }

    /// <summary>
    /// Applies <paramref name="owner"/>'s theme to <paramref name="dialog"/>. The owner is
    /// an argument rather than a read of <see cref="Window.Owner"/> so that the copy can be
    /// exercised without showing a window, which WPF requires before it accepts an owner.
    /// </summary>
    public static void Apply(Window dialog, FrameworkElement owner)
    {
        foreach (string key in OwnedKeys)
        {
            object? resource = owner.TryFindResource(key);
            if (resource is not null)
                dialog.Resources[key] = resource;
        }

        object? validation = owner.TryFindResource(ValidationSourceKey);
        if (validation is not null)
            dialog.Resources[ValidationKey] = validation;
    }
}
