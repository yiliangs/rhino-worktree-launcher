@echo off
setlocal

set "INSTALL_SCRIPT=%~dp0Install-RhinoWorktreeLauncher.ps1"
if not exist "%INSTALL_SCRIPT%" set "INSTALL_SCRIPT=%~dp0src\RhinoWorktreeLauncher\Install-RhinoWorktreeLauncher.ps1"

rem PowerShell 7 is a separate product and is absent from a stock Windows install, so it
rem cannot be the only host. Prefer it where it exists, then fall back to the Windows
rem PowerShell every machine ships. The install script is written to run under both.
set "PS_HOST="
for %%I in (pwsh.exe) do if not "%%~$PATH:I"=="" set "PS_HOST=%%~$PATH:I"
if not defined PS_HOST for %%I in (powershell.exe) do if not "%%~$PATH:I"=="" set "PS_HOST=%%~$PATH:I"
if not defined PS_HOST if exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" set "PS_HOST=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

if not defined PS_HOST (
    echo.
    echo No PowerShell host was found. Rhino Worktree Launcher needs either Windows
    echo PowerShell, which ships with Windows, or PowerShell 7.
    echo.
    pause
    exit /b 9009
)

rem Bypass is required rather than convenient. The Windows client default is Restricted,
rem and a release archive downloaded from GitHub carries a zone mark that blocks the
rem extracted script even under RemoteSigned. Both refuse to run this installer otherwise.
"%PS_HOST%" -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_SCRIPT%" -Launch %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Rhino Worktree Launcher installation failed with exit code %EXIT_CODE%.
    pause
)

exit /b %EXIT_CODE%
