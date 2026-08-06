# Rhino Worktree Launcher application

Independent Windows application for selecting a manifest-configured Rhino plug-in project and launching one of its Git worktrees.

The app owns project selection, worktree discovery, local project registration, installation, and process dispatch. `MainWindow` owns the native WPF interface and coordinates the existing scanner and launch services. Each plug-in repository owns its committed `.rhino-worktree-launcher.json` plus the worktree entry point named there. The app must not reproduce registry mutation, plug-in verification, build logic, or project-specific dependency checks.

The primary checkout launches the manifest-selected Rhino version normally. Linked worktrees launch the manifest's relative entry point only when every readiness file is present. Missing contracts remain unavailable rather than falling back to a guessed launch.

`Assets/rhino-launcher.png` is the header mark and `Assets/rhino-launcher.ico` is the executable and taskbar icon. The fixed 720 × 1000 interface follows the Windows `AppsUseLightTheme` setting live and uses bundled IBM Plex Sans and Geist Mono. Worktree rows show FRESH/STALE launch readiness (`CanLaunch`), tracked-line additions/deletions from `git diff --numstat HEAD`, relative activity, open/draft PR badges from authenticated `gh`, and a shared-scale divergence bar against `origin/HEAD` with primary-HEAD fallback. Refresh runs local scanning and Git fetch/PR lookup concurrently; GitHub or fetch failure must not block local discovery.

Published releases are self-contained and multifile, with the shared images and fonts embedded as WPF resources. Keep the taskbar icon in `<ApplicationIcon>` and do not set `Window.Icon` to a linked external path; published startup fails with `XamlParseException`. Visual verification must cover both Windows app themes plus idle, refresh, selected, hover, disabled, long-name, and PR badge states.
