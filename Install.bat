@echo off
setlocal

rem A produced package carries the payload beside this file, and the bootstrap in it
rem installs the whole payload itself. That path involves no script host, so nothing here
rem depends on which PowerShell exists or on whether policy permits it to run a file.
set "PAYLOAD_BOOTSTRAP=%~dp0payload\bootstrap\rwl.exe"
if exist "%PAYLOAD_BOOTSTRAP%" goto packaged
goto source

:packaged
"%PAYLOAD_BOOTSTRAP%" install --launch %*
set "EXIT_CODE=%ERRORLEVEL%"
goto report

rem A source checkout has no payload yet and has to produce one first. That path already
rem requires the .NET SDK, so a developer PowerShell is a fair assumption there.
:source
set "INSTALL_SCRIPT=%~dp0src\RhinoWorktreeLauncher\Install-RhinoWorktreeLauncher.ps1"
if not exist "%INSTALL_SCRIPT%" goto nopayload

set "PS_HOST="
for %%I in (pwsh.exe) do if not "%%~$PATH:I"=="" set "PS_HOST=%%~$PATH:I"
if not defined PS_HOST for %%I in (powershell.exe) do if not "%%~$PATH:I"=="" set "PS_HOST=%%~$PATH:I"
if not defined PS_HOST if exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" set "PS_HOST=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not defined PS_HOST goto nohost

"%PS_HOST%" -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_SCRIPT%" -Launch %*
set "EXIT_CODE=%ERRORLEVEL%"
goto report

:nopayload
echo.
echo No Rhino Worktree Launcher payload was found beside this file, and no source
echo checkout was found either. Extract the release archive completely and run
echo Install.bat from the extracted folder.
echo.
pause
exit /b 9009

:nohost
echo.
echo Installing from a source checkout needs PowerShell, and neither Windows
echo PowerShell nor PowerShell 7 was found. Install from a release archive instead,
echo which needs no PowerShell at all.
echo.
pause
exit /b 9009

:report
if not "%EXIT_CODE%"=="0" (
    echo.
    echo Rhino Worktree Launcher installation failed with exit code %EXIT_CODE%.
    pause
)

exit /b %EXIT_CODE%
