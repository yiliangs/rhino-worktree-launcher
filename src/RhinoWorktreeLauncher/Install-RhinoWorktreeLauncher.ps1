[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [switch]$InstallClaudeIntegration,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$desktopProject = Join-Path $PSScriptRoot 'RhinoWorktreeLauncher.csproj'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$cliProject = Join-Path $sourceRoot 'Rwl.Cli\Rwl.Cli.csproj'
$mcpProject = Join-Path $sourceRoot 'Rwl.Mcp\Rwl.Mcp.csproj'
$bootstrapProject = Join-Path $sourceRoot 'Rwl.Bootstrap\Rwl.Bootstrap.csproj'
$verifierProject = Join-Path $sourceRoot 'Rwl.RhinoVerifier\Rwl.RhinoVerifier.csproj'
$dataRoot = Join-Path $env:LOCALAPPDATA 'RhinoWorktreeLauncher'
$releaseId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$installRoot = Join-Path $dataRoot "releases\$releaseId"
$desktopRoot = Join-Path $installRoot 'desktop'
$cliRoot = Join-Path $installRoot 'cli'
$mcpRoot = Join-Path $installRoot 'mcp'
$bootstrapPublishRoot = Join-Path $installRoot 'bootstrap-publish'
$stableBootstrapRoot = Join-Path $dataRoot 'bootstrap'
$stableBootstrapPath = Join-Path $stableBootstrapRoot 'rwl.exe'

function Move-AtomicReplace([string]$Source, [string]$Destination)
{
    if (-not (Test-Path -LiteralPath $Destination))
    {
        [IO.File]::Move($Source, $Destination)
        return
    }

    $backup = "$Destination.rwl-replace-$PID.bak"
    try
    {
        [IO.File]::Replace($Source, $Destination, $backup, $true)
    }
    finally
    {
        if (Test-Path -LiteralPath $backup)
        {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

Write-Host 'Publishing Rhino Worktree Launcher...' -ForegroundColor Cyan
& dotnet build $verifierProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Rhino verifier build failed with exit code $LASTEXITCODE." }
& dotnet publish $desktopProject -c Release -r win-x64 --self-contained true -o $desktopRoot
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed with exit code $LASTEXITCODE." }
& dotnet publish $cliProject -c Release -r win-x64 --self-contained true -o $cliRoot
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE." }
& dotnet publish $mcpProject -c Release -r win-x64 --self-contained true -o $mcpRoot
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed with exit code $LASTEXITCODE." }
& dotnet publish $bootstrapProject -c Release -r win-x64 --self-contained true -o $bootstrapPublishRoot
if ($LASTEXITCODE -ne 0)
{
    throw "Bootstrap publish failed with exit code $LASTEXITCODE."
}
$verifierOutput = Join-Path $sourceRoot 'Rwl.RhinoVerifier\bin\Release\net48\Rwl.RhinoVerifier.rhp'
Copy-Item -LiteralPath $verifierOutput -Destination $desktopRoot -Force
Copy-Item -LiteralPath $verifierOutput -Destination $cliRoot -Force
Copy-Item -LiteralPath $verifierOutput -Destination $mcpRoot -Force

$desktopExecutable = Join-Path $desktopRoot 'RhinoWorktreeLauncher.exe'
$cliExecutable = Join-Path $cliRoot 'rwl-cli.exe'
$mcpExecutable = Join-Path $mcpRoot 'rwl-mcp.exe'
$releasePointer = [ordered]@{
    releaseId = $releaseId
    desktop = $desktopExecutable
    cli = $cliExecutable
    mcp = $mcpExecutable
}
New-Item -ItemType Directory -Force -Path $dataRoot, $stableBootstrapRoot | Out-Null
$bootstrapTemporary = Join-Path $stableBootstrapRoot "rwl.$PID.new.exe"
Copy-Item -LiteralPath (Join-Path $bootstrapPublishRoot 'rwl.exe') -Destination $bootstrapTemporary -Force
Move-AtomicReplace $bootstrapTemporary $stableBootstrapPath

$pointerPath = Join-Path $dataRoot 'current.json'
$pointerTemporary = Join-Path $dataRoot "current.$PID.tmp"
$releasePointer | ConvertTo-Json | Set-Content -Path $pointerTemporary -Encoding utf8
Move-AtomicReplace $pointerTemporary $pointerPath

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'Rhino Worktree Launcher.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $stableBootstrapPath
$shortcut.Arguments = 'desktop'
$shortcut.WorkingDirectory = $stableBootstrapRoot
$shortcut.IconLocation = "$stableBootstrapPath,0"
$shortcut.Description = 'Launch Rhino from configured Git worktrees'
$shortcut.Save()

if (-not [string]::IsNullOrWhiteSpace($ProjectRoot))
{
    $registration = Start-Process -FilePath $stableBootstrapPath `
        -ArgumentList @('project', 'register', ('"{0}"' -f [IO.Path]::GetFullPath($ProjectRoot))) `
        -Wait -PassThru
    if ($registration.ExitCode -ne 0)
    {
        throw "Project registration failed with exit code $($registration.ExitCode)."
    }
}

if ($InstallClaudeIntegration)
{
    $integration = Start-Process -FilePath $stableBootstrapPath `
        -ArgumentList @('integration', 'install', 'claude', '--bootstrap', ('"{0}"' -f $stableBootstrapPath)) `
        -Wait -PassThru
    if ($integration.ExitCode -ne 0)
    {
        throw "Claude integration installation failed with exit code $($integration.ExitCode)."
    }
}

Write-Host ''
Write-Host "Installed release: $installRoot" -ForegroundColor Green
Write-Host "Stable bootstrap: $stableBootstrapPath"
Write-Host "Shortcut: $shortcutPath"
if ($ProjectRoot)
{
    Write-Host "Registered project: $([IO.Path]::GetFullPath($ProjectRoot))"
}
Write-Host 'Pin the running app or Start Menu shortcut to the taskbar.' -ForegroundColor Yellow

if ($Launch)
{
    Start-Process -FilePath $stableBootstrapPath -ArgumentList 'desktop' | Out-Null
}
