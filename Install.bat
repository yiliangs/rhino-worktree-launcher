@echo off
setlocal

set "INSTALL_SCRIPT=%~dp0Install-RhinoWorktreeLauncher.ps1"
if not exist "%INSTALL_SCRIPT%" set "INSTALL_SCRIPT=%~dp0src\RhinoWorktreeLauncher\Install-RhinoWorktreeLauncher.ps1"

pwsh -NoProfile -File "%INSTALL_SCRIPT%" -Launch %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Rhino Worktree Launcher installation failed with exit code %EXIT_CODE%.
    pause
)

exit /b %EXIT_CODE%
