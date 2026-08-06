# Rhino Worktree Launcher application

Independent Windows application for selecting a manifest-configured Rhino plug-in project and launching one of its Git worktrees.

The app owns project selection, worktree discovery, local project registration, installation, and process dispatch. Each plug-in repository owns its committed `.rhino-worktree-launcher.json` plus the worktree entry point named there. The app must not reproduce registry mutation, plug-in verification, build logic, or project-specific dependency checks.

The primary checkout launches the manifest-selected Rhino version normally. Linked worktrees launch the manifest's relative entry point only when every readiness file is present. Missing contracts remain unavailable rather than falling back to a guessed launch.

`Assets/rhino-launcher.png` is the header mark and `Assets/rhino-launcher.ico` is the executable and taskbar icon. The fixed 720 × 1000 interface uses bundled IBM Plex Sans and Geist Mono fonts. Worktree rows show GitHub-style behind/ahead counts against `origin/HEAD` plus latest-commit activity; repositories without a symbolic default remote branch fall back to the primary checkout's `HEAD`.

For the self-contained single-file WPF publish, set the taskbar icon through `<ApplicationIcon>` and load the header PNG as a WPF `Resource`. Do not set `Window.Icon` to a linked external path; published startup fails with `XamlParseException`.
