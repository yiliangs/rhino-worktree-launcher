[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RequestPath)

$ErrorActionPreference = 'Stop'
$request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
if ($request.protocolVersion -ne 1 -or $request.command -ne 'prepareLaunch')
{
    [ordered]@{
        protocolVersion = 1
        kind = 'result'
        success = $false
        errorCode = 'unsupported_request'
        errorMessage = 'This driver supports protocol v1 prepareLaunch requests.'
    } | ConvertTo-Json -Compress
    exit 0
}

try
{
    [ordered]@{
        protocolVersion = 1
        kind = 'event'
        stage = 'build'
        message = 'Building the selected worktree.'
    } | ConvertTo-Json -Compress

    # Replace this block with the repository's deterministic build command and artifact paths.
    # Include rhinoRuntime = 'netfx' or 'netcore' in the successful result when required.
    throw 'Driver template not configured. Replace the build block in the app-local project Driver.ps1.'
}
catch
{
    [ordered]@{
        protocolVersion = 1
        kind = 'result'
        success = $false
        errorCode = 'build_failed'
        errorMessage = $_.Exception.Message
    } | ConvertTo-Json -Compress
    exit 0
}
