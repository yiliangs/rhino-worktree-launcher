# WPF adapter

This project owns only the native Windows presentation surface. `MainWindow` binds backend DTOs, invokes `LauncherBackend` commands, displays progress, and presents terminal diagnostics. It must not duplicate Git scanning, canonical solution validation, build execution, Rhino launch, or verifier logic from `RhinoWorktreeLauncher.Core`.

The fixed 720 x 1000 main interface follows `AppsUseLightTheme`, embeds the approved IBM Plex Sans and Geist Mono static fonts, and uses Ideal metrics, Fixed hinting, and ClearType. Keep the DWM-visible frame sizing, shared corner-radius tokens, dedicated right scrollbar rail, full-height non-scroll indicator, project identity row, Refresh progress control, and Add project ghost-button behavior.

`Config` is the project-specific surface for the canonical Rhino plug-in project, solution, Configuration, Platform, launch mode, and project grants. When multiple plug-in projects exist, keep Config available and require the user to select one there. Do not ask the user to select a built `.rhp`; MSBuild `TargetPath` derives that artifact from the saved project and solution configuration. `Settings` is the global surface for MCP setup. The footer contains Settings, Open folder, and the launch action. Do not restore the retired bottom-left selected-worktree indicator or a second MCP button.

Every project, solution, and configuration selector uses the shared `Themes/DropdownStyles.xaml` component: 38px value surface, 9px radius, explicit caret, visible focus ring, matched rounded popup, 32px menu rows, and the selected-worktree texture plus accent check. Keep value templates explicit so records never render through diagnostic `ToString()` output, and show dependent selectors disabled with directional placeholder text until options exist.

Launch is explicit through the button or Enter key. Double-click does not launch. The button label follows the selected project's Build & Launch or Direct Launch mode and waits for loaded-binary verification; it must never infer success from `Process.Start`.
