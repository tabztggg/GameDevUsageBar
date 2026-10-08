[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProfileDirectory,
    [Parameter(Mandatory = $true)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bridgeScript = Join-Path $env:USERPROFILE '.codex\skills\claude-code-bridge\scripts\bridge.py'
if (-not (Test-Path -LiteralPath $bridgeScript -PathType Leaf)) {
    throw 'The protected Claude Code Bridge login entry is not installed.'
}
$pythonCommand = Get-Command python.exe -ErrorAction Stop
Write-Host 'Sign in to this account in Firefox. Other account profiles stay unchanged.'
# No shell expression is assembled from an account label or path. The maintained
# bridge validates the profile, client, network guard and Firefox before login.
& $pythonCommand.Source -E -s $bridgeScript auth-login --config-dir $ProfileDirectory --result $ResultPath
$loginExit = $LASTEXITCODE
if ($loginExit -ne 0) {
    Write-Host 'The sign-in did not finish. Return to GameDevUsageBar to check its status.'
    [void](Read-Host 'Press Enter to close this sign-in window')
}
exit $loginExit
