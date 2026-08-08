[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RequestPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-DriverMessage {
    param([hashtable]$Value)
    $Value | ConvertTo-Json -Depth 8 -Compress | Write-Output
}

function Write-Event {
    param([string]$Stage, [string]$Message)
    Write-DriverMessage ([ordered]@{
        protocolVersion = 1
        kind = 'event'
        stage = $Stage
        message = $Message
    })
}

function Write-Failure {
    param([string]$Code, [string]$Message)
    Write-DriverMessage ([ordered]@{
        protocolVersion = 1
        kind = 'result'
        success = $false
        errorCode = $Code
        errorMessage = $Message
    })
}

try
{
    $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
    if ($request.protocolVersion -ne 1 -or $request.command -ne 'prepareLaunch')
    {
        Write-Failure 'unsupported_request' 'Natalie supports RWL protocol v1 prepareLaunch requests.'
        exit 0
    }

    $repositoryRoot = [IO.Path]::GetFullPath($request.worktreePath)
    $receiptWriter = Join-Path $repositoryRoot 'Natalie\Testing\WorktreeLaunch\WorktreeLaunchBootstrap.cs'
    if (-not (Test-Path -LiteralPath $receiptWriter))
    {
        Write-Failure 'receipt_support_missing' 'This Natalie branch does not contain the RWL receipt writer and cannot provide loaded-binary verification.'
        exit 0
    }

    $webRoot = Join-Path $repositoryRoot 'web'
    $typescriptPath = Join-Path $webRoot 'node_modules\.bin\tsc.cmd'
    if (-not (Test-Path -LiteralPath $typescriptPath))
    {
        Write-Event 'dependencies' 'Installing Natalie web dependencies.'
        & npm --prefix $webRoot ci 2>&1 | ForEach-Object {
            Write-Event 'dependencies' $_.ToString()
        }
        if ($LASTEXITCODE -ne 0)
        {
            Write-Failure 'dependency_install_failed' "npm ci exited with code $LASTEXITCODE."
            exit 0
        }
    }

    Write-Event 'build' 'Building Natalie Debug x64 from the selected worktree.'
    $projectPath = Join-Path $repositoryRoot 'Natalie\Natalie.csproj'
    $solutionDirectory = $repositoryRoot.TrimEnd('\') + '\'
    & dotnet build $projectPath -c Debug '-p:Platform=x64' "-p:SolutionDir=$solutionDirectory" 2>&1 |
        ForEach-Object { Write-Event 'build' $_.ToString() }
    if ($LASTEXITCODE -ne 0)
    {
        Write-Failure 'build_failed' "Natalie build exited with code $LASTEXITCODE."
        exit 0
    }

    $packageDirectory = Join-Path $repositoryRoot 'Natalie\bin\Debug\net481'
    $pluginPath = Join-Path $packageDirectory 'Natalie.rhp'
    $dependencies = @('NatBase', 'NatSolve', 'InterfaceBar') | ForEach-Object {
        $path = Join-Path $packageDirectory "$_.dll"
        if (-not (Test-Path -LiteralPath $path))
        {
            throw "Required Natalie build artifact was not found at '$path'."
        }
        [ordered]@{ name = $_; path = $path }
    }
    if (-not (Test-Path -LiteralPath $pluginPath))
    {
        throw "Natalie.rhp was not found at '$pluginPath'."
    }

    Write-DriverMessage ([ordered]@{
        protocolVersion = 1
        kind = 'result'
        success = $true
        packageDirectory = $packageDirectory
        pluginPath = $pluginPath
        rhinoRuntime = 'netfx'
        criticalDependencies = $dependencies
        receipt = [ordered]@{
            launchIdEnvironmentVariable = 'RWL_LAUNCH_ID'
            receiptPathEnvironmentVariable = 'RWL_RECEIPT_PATH'
        }
        registration = [ordered]@{
            mode = 'windows-registry-lease'
            pluginId = 'c50b7fc9-ffee-4ac8-83e0-6290a321eae2'
            startupCommand = '_Natalie'
        }
    })
}
catch
{
    Write-Failure 'driver_failed' $_.Exception.Message
}
