[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectPath = Join-Path $PSScriptRoot 'RhinoWorktreeLauncher.csproj'
$dataRoot = Join-Path $env:LOCALAPPDATA 'RhinoWorktreeLauncher'
$releaseId = Get-Date -Format 'yyyyMMdd-HHmmss'
$installRoot = Join-Path $dataRoot "releases\$releaseId"

Write-Host 'Publishing Rhino Worktree Launcher...' -ForegroundColor Cyan
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -o $installRoot
if ($LASTEXITCODE -ne 0)
{
    throw "Launcher publish failed with exit code $LASTEXITCODE."
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'Rhino Worktree Launcher.lnk'
$executablePath = Join-Path $installRoot 'RhinoWorktreeLauncher.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $installRoot
$shortcut.IconLocation = "$executablePath,0"
$shortcut.Description = 'Launch Rhino from configured Git worktrees'
$shortcut.Save()

if (-not [string]::IsNullOrWhiteSpace($ProjectRoot))
{
    $registration = Start-Process -FilePath $executablePath `
        -ArgumentList @('--register-project', ('"{0}"' -f [IO.Path]::GetFullPath($ProjectRoot))) `
        -Wait -PassThru
    if ($registration.ExitCode -ne 0)
    {
        throw "Project registration failed with exit code $($registration.ExitCode)."
    }
}

Write-Host ''
Write-Host "Installed: $executablePath" -ForegroundColor Green
Write-Host "Shortcut: $shortcutPath"
if ($ProjectRoot)
{
    Write-Host "Registered project: $([IO.Path]::GetFullPath($ProjectRoot))"
}
Write-Host 'Pin the running app or Start Menu shortcut to the taskbar.' -ForegroundColor Yellow

if ($Launch)
{
    Start-Process -FilePath $executablePath | Out-Null
}
