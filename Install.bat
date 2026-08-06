@echo off
setlocal

pwsh -NoProfile -File "%~dp0src\RhinoWorktreeLauncher\Install-RhinoWorktreeLauncher.ps1" -Launch %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Rhino Worktree Launcher installation failed with exit code %EXIT_CODE%.
    pause
)

exit /b %EXIT_CODE%
