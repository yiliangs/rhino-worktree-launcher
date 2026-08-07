# WPF adapter

This project owns only the native Windows presentation surface. `MainWindow` binds `ProjectSnapshot` and `WorktreeSnapshot` DTOs, invokes `LauncherBackend` commands, displays progress, and presents terminal diagnostics. It must not duplicate Git scanning, project contract validation, driver execution, Rhino launch, or receipt logic from `RhinoWorktreeLauncher.Core`.

The fixed 720 × 1000 interface follows `AppsUseLightTheme`, embeds the approved IBM Plex Sans and Geist Mono static fonts, and uses Ideal metrics, Fixed hinting, and ClearType. Keep the DWM-visible frame sizing, shared 4/8/14 corner-radius tokens, dedicated right scrollbar rail, full-height non-scroll indicator, project identity row, Refresh progress control, and Add project ghost-button behavior.

Launch is explicit through the button or Enter key. Double-click does not launch. The button waits for the backend's build, process start, and loaded-binary receipt result; it must never infer success from `Process.Start`.
