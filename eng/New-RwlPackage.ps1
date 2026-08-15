[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repositoryRoot 'src'
$packageRoot = [IO.Path]::GetFullPath($OutputPath)
$payloadRoot = Join-Path $packageRoot 'payload'
$desktopRoot = Join-Path $payloadRoot 'desktop'
$cliRoot = Join-Path $payloadRoot 'cli'
$mcpRoot = Join-Path $payloadRoot 'mcp'
$bootstrapRoot = Join-Path $payloadRoot 'bootstrap'

if (Test-Path -LiteralPath $packageRoot)
{
    $resolvedPackageRoot = (Resolve-Path -LiteralPath $packageRoot).Path
    $resolvedRepositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path
    if ($resolvedPackageRoot -eq $resolvedRepositoryRoot -or
        $resolvedRepositoryRoot.StartsWith($resolvedPackageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Package output '$resolvedPackageRoot' cannot contain the repository root."
    }
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $desktopRoot, $cliRoot, $mcpRoot, $bootstrapRoot | Out-Null

$publishTargets = @(
    @{ Project = Join-Path $sourceRoot 'RhinoWorktreeLauncher\RhinoWorktreeLauncher.csproj'; Output = $desktopRoot },
    @{ Project = Join-Path $sourceRoot 'Rwl.Cli\Rwl.Cli.csproj'; Output = $cliRoot },
    @{ Project = Join-Path $sourceRoot 'Rwl.Mcp\Rwl.Mcp.csproj'; Output = $mcpRoot },
    @{ Project = Join-Path $sourceRoot 'Rwl.Bootstrap\Rwl.Bootstrap.csproj'; Output = $bootstrapRoot }
)
foreach ($target in $publishTargets)
{
    & dotnet publish $target.Project -c Release -r win-x64 --self-contained true -p:Version=$Version -o $target.Output
    if ($LASTEXITCODE -ne 0)
    {
        throw "Publish failed for '$($target.Project)' with exit code $LASTEXITCODE."
    }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Install.bat') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'RhinoWorktreeLauncher\Install-RhinoWorktreeLauncher.ps1') -Destination $packageRoot -Force

[ordered]@{
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding utf8

Write-Host "Created RWL package at '$packageRoot'." -ForegroundColor Green
