[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$PackageRoot,
    [switch]$InstallClaudeIntegration,
    [switch]$InstallCodexIntegration,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$sourceRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = Split-Path $sourceRoot -Parent
$packageScript = Join-Path $repositoryRoot 'eng\New-RwlPackage.ps1'
$dataRoot = Join-Path $env:LOCALAPPDATA 'RhinoWorktreeLauncher'
$releaseId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$installRoot = Join-Path $dataRoot "releases\$releaseId"
$desktopRoot = Join-Path $installRoot 'desktop'
$cliRoot = Join-Path $installRoot 'cli'
$mcpRoot = Join-Path $installRoot 'mcp'
$bootstrapPublishRoot = Join-Path $installRoot 'bootstrap-publish'
$stableBootstrapRoot = Join-Path $dataRoot 'bootstrap'
$stableBootstrapPath = Join-Path $stableBootstrapRoot 'rwl.exe'
$bundledPackageRoot = Join-Path $PSScriptRoot 'payload'
$temporaryPackageRoot = $null

function Remove-ReplacedExecutableBackup([string]$Path)
{
    try
    {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch [System.UnauthorizedAccessException]
    {
        Write-Warning "The replaced executable backup remains in use and will be left at '$Path'."
    }
    catch [System.IO.IOException]
    {
        Write-Warning "The replaced executable backup remains in use and will be left at '$Path'."
    }
}

function Move-AtomicReplace([string]$Source, [string]$Destination)
{
    if (-not (Test-Path -LiteralPath $Destination))
    {
        [IO.File]::Move($Source, $Destination)
        return
    }

    $destinationDirectory = Split-Path $Destination -Parent
    $destinationName = Split-Path $Destination -Leaf
    Get-ChildItem -LiteralPath $destinationDirectory -Filter "$destinationName.rwl-replace-*.bak" -File |
        ForEach-Object { Remove-ReplacedExecutableBackup $_.FullName }

    $backup = "$Destination.rwl-replace-$PID.bak"
    try
    {
        [IO.File]::Replace($Source, $Destination, $backup, $true)
    }
    finally
    {
        if (Test-Path -LiteralPath $backup)
        {
            Remove-ReplacedExecutableBackup $backup
        }
    }
}

function Remove-TemporaryPackage([string]$Path)
{
    $temporaryRoot = [IO.Path]::GetFullPath($Path)
    $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $systemTemporaryRoot = $systemTemporaryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $temporaryRoot.StartsWith($systemTemporaryRoot, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove temporary package path outside '$systemTemporaryRoot': '$temporaryRoot'."
    }
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

try
{
    if ([string]::IsNullOrWhiteSpace($PackageRoot))
    {
        if (Test-Path -LiteralPath $bundledPackageRoot)
        {
            $PackageRoot = $bundledPackageRoot
        }
        else
        {
            if (-not (Test-Path -LiteralPath $packageScript -PathType Leaf))
            {
                throw "The canonical RWL package producer was not found at '$packageScript'."
            }
            $temporaryPackageRoot = Join-Path (
                [IO.Path]::GetTempPath()) (
                'RhinoWorktreeLauncher.Package.' + [Guid]::NewGuid().ToString('N'))
            Write-Host 'Producing the canonical Rhino Worktree Launcher package from source...' -ForegroundColor Cyan
            & $packageScript -OutputPath $temporaryPackageRoot
            if ($LASTEXITCODE -ne 0)
            {
                throw "Package production failed with exit code $LASTEXITCODE."
            }
            $PackageRoot = Join-Path $temporaryPackageRoot 'payload'
        }
    }

    $PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
    $requiredPayload = @(
        (Join-Path $PackageRoot 'desktop\RhinoWorktreeLauncher.exe'),
        (Join-Path $PackageRoot 'cli\rwl-cli.exe'),
        (Join-Path $PackageRoot 'mcp\rwl-mcp.exe'),
        (Join-Path $PackageRoot 'bootstrap\rwl.exe')
    )
    foreach ($requiredPath in $requiredPayload)
    {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf))
        {
            throw "The RWL package is incomplete. Missing '$requiredPath'."
        }
    }

    Write-Host 'Installing the canonical Rhino Worktree Launcher payload...' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $desktopRoot, $cliRoot, $mcpRoot, $bootstrapPublishRoot | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot 'desktop\*') -Destination $desktopRoot -Recurse -Force
    Copy-Item -Path (Join-Path $PackageRoot 'cli\*') -Destination $cliRoot -Recurse -Force
    Copy-Item -Path (Join-Path $PackageRoot 'mcp\*') -Destination $mcpRoot -Recurse -Force
    Copy-Item -Path (Join-Path $PackageRoot 'bootstrap\*') -Destination $bootstrapPublishRoot -Recurse -Force
}
finally
{
    if (-not [string]::IsNullOrWhiteSpace($temporaryPackageRoot))
    {
        Remove-TemporaryPackage $temporaryPackageRoot
    }
}

$desktopExecutable = Join-Path $desktopRoot 'RhinoWorktreeLauncher.exe'
$cliExecutable = Join-Path $cliRoot 'rwl-cli.exe'
$mcpExecutable = Join-Path $mcpRoot 'rwl-mcp.exe'
$publishedBootstrapPath = Join-Path $bootstrapPublishRoot 'rwl.exe'
$releasePointer = [ordered]@{
    releaseId = $releaseId
    desktop = $desktopExecutable
    cli = $cliExecutable
    mcp = $mcpExecutable
}
New-Item -ItemType Directory -Force -Path $dataRoot, $stableBootstrapRoot | Out-Null
$bootstrapTemporary = Join-Path $stableBootstrapRoot "rwl.$PID.new.exe"
Copy-Item -LiteralPath $publishedBootstrapPath -Destination $bootstrapTemporary -Force
Move-AtomicReplace $bootstrapTemporary $stableBootstrapPath

$pointerPath = Join-Path $dataRoot 'current.json'
$pointerTemporary = Join-Path $dataRoot "current.$PID.tmp"
$releasePointer | ConvertTo-Json | Set-Content -Path $pointerTemporary -Encoding utf8
Move-AtomicReplace $pointerTemporary $pointerPath

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'Rhino Worktree Launcher.lnk'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
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

if ($InstallCodexIntegration)
{
    $integration = Start-Process -FilePath $stableBootstrapPath `
        -ArgumentList @('integration', 'install', 'codex', '--bootstrap', ('"{0}"' -f $stableBootstrapPath)) `
        -Wait -PassThru
    if ($integration.ExitCode -ne 0)
    {
        throw "Codex integration installation failed with exit code $($integration.ExitCode)."
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
