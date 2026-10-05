[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$ReceiptDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Package = [IO.Path]::GetFullPath($Package)
$metadataPath = "$Package.json"
if (-not (Test-Path -LiteralPath $Package -PathType Leaf) -or -not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'Installer and its build metadata are required.' }
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$testId = [Guid]::Empty
if ($metadata.schemaVersion -ne 1 -or $metadata.smokeTest -ne $true -or -not [Guid]::TryParseExact([string]$metadata.appId, 'D', [ref]$testId) -or $testId -eq [Guid]::Empty -or $testId -eq [Guid]'A62A6344-CFA1-42C0-933F-C648F614D433') { throw 'Refusing to test a production installer. Compile a dedicated -SmokeTest package first.' }
$expectedName = "GameDevUsageBar Installer Test $testId"
if ($metadata.shortcutName -ne $expectedName -or $metadata.mutex -ne "Local\GameDevUsageBar.InstallerTest.$testId" -or $metadata.file -ne [IO.Path]::GetFileName($Package) -or $metadata.sha256 -ne (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Smoke package identity or checksum does not match its build metadata.' }
$testUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($metadata.appId)_is1"
if (Test-Path -LiteralPath $testUninstallKey) { throw 'This test AppId is already installed. Reconcile that existing installation before testing again.' }
if (-not $ReceiptDirectory) { $ReceiptDirectory = Join-Path $projectRoot '.cache/installer-tests' }
$ReceiptDirectory = [IO.Path]::GetFullPath($ReceiptDirectory)
$testRoot = Join-Path $ReceiptDirectory ([Guid]::NewGuid().ToString('D'))
$installRoot = Join-Path $testRoot 'installed-app'
$fixtureRoot = Join-Path $testRoot 'user-profile-fixture'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$expectedName.lnk"
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) "$expectedName.lnk"
$startMenuGroup = Join-Path ([Environment]::GetFolderPath('Programs')) $expectedName
$startMenuShortcut = Join-Path $startMenuGroup "$expectedName.lnk"
$uninstallShortcut = Join-Path $startMenuGroup "Uninstall $expectedName.lnk"
foreach ($path in @($desktopShortcut, $startupShortcut, $startMenuGroup)) { if (Test-Path -LiteralPath $path) { throw "An isolated test shortcut already exists: $path" } }
New-Item -ItemType Directory -Path $testRoot, (Join-Path $fixtureRoot 'AppData/Local/GameDevBar/secrets'), (Join-Path $fixtureRoot '.codex'), (Join-Path $fixtureRoot '.claude') -Force | Out-Null
@{
    'AppData/Local/GameDevBar/settings.json' = '{"fixture":"settings-only"}'
    'AppData/Local/GameDevBar/secrets/placeholder.txt' = 'SYNTHETIC FIXTURE - NO REAL CREDENTIAL'
    '.codex/auth.json' = '{"fixture":"not-a-real-codex-login"}'
    '.claude/.credentials.json' = '{"fixture":"not-a-real-claude-login"}'
}.GetEnumerator() | ForEach-Object { Set-Content -LiteralPath (Join-Path $fixtureRoot $_.Key) -Value $_.Value -Encoding utf8 }
$fixtureHashes = @{}
Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File -Force | ForEach-Object { $fixtureHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$shell = New-Object -ComObject WScript.Shell
$checks = [Collections.Generic.List[object]]::new()
$sequence = 0
$heldMutex = $null
$pendingProcess = $null
$testFailure = $null
$uninstallFailure = $null
$app = Join-Path $installRoot 'GameDevUsageBar.exe'
$uninstaller = Join-Path $installRoot 'unins000.exe'

function Assert-Check([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw $Name }
}
function Get-ProductionGuard {
    $productionApp = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/GameDevUsageBar/GameDevUsageBar.exe'
    $productionStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'GameDevUsageBar.lnk'
    $productionRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A62A6344-CFA1-42C0-933F-C648F614D433}_is1'
    [ordered]@{
        executableHash = $(if (Test-Path -LiteralPath $productionApp) { (Get-FileHash -LiteralPath $productionApp -Algorithm SHA256).Hash } else { $null })
        startupHash = $(if (Test-Path -LiteralPath $productionStartup) { (Get-FileHash -LiteralPath $productionStartup -Algorithm SHA256).Hash } else { $null })
        installerRegistered = Test-Path -LiteralPath $productionRegistry
    } | ConvertTo-Json -Compress
}
function Assert-FixtureData {
    foreach ($path in $fixtureHashes.Keys) { Assert-Check ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $fixtureHashes[$path]) "Preserved fixture: $([IO.Path]::GetRelativePath($fixtureRoot, $path))" }
}
function Invoke-Setup([string]$Tasks, [string]$Language = 'english', [switch]$ExpectFailure) {
    $script:sequence++
    $log = Join-Path $testRoot "setup-$sequence.log"
    $arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', "/LANG=$Language", ('/DIR="' + $installRoot + '"'), ('/TASKS="' + $Tasks + '"'), ('/LOG="' + $log + '"'))
    $process = Start-Process -FilePath $Package -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $script:pendingProcess = $process; throw "UNKNOWN: installer PID $($process.Id) did not finish within 60 seconds. Do not replay; inspect it and its log first." }
    if ($ExpectFailure) { Assert-Check ($process.ExitCode -ne 0) 'A running-app mutex blocks installation without closing a process' }
    else { Assert-Check ($process.ExitCode -eq 0) "Installer finished successfully (language $Language, selected tasks '$Tasks'; exit $($process.ExitCode))" }
}
function Assert-Shortcut([string]$Path, [string]$Target, [string]$Name) {
    Assert-Check (Test-Path -LiteralPath $Path -PathType Leaf) "$Name exists"
    $link = $shell.CreateShortcut($Path)
    Assert-Check ($link.TargetPath -eq $Target) "$Name targets this isolated installation"
}
function Invoke-Uninstaller([switch]$ExpectFailure) {
    $script:sequence++
    $log = Join-Path $testRoot "uninstall-$sequence.log"
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $log + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $script:pendingProcess = $process; throw "UNKNOWN: uninstaller PID $($process.Id) did not finish within 60 seconds. Inspect its state before any retry." }
    if ($ExpectFailure) { Assert-Check ($process.ExitCode -ne 0) 'A running-app mutex blocks uninstall without closing a process' }
    else { Assert-Check ($process.ExitCode -eq 0) 'Uninstaller finished successfully' }
}
function Assert-Payload {
    Assert-Check (Test-Path -LiteralPath $app -PathType Leaf) 'Installed application executable exists'
    Assert-Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion.Split('+')[0] -eq $metadata.version) 'Installed executable version matches the release'
    $manifest = Join-Path $installRoot 'SHA256SUMS.txt'
    Assert-Check (Test-Path -LiteralPath $manifest -PathType Leaf) 'Installed package manifest exists'
    $count = 0
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Malformed installed checksum manifest.' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $path = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
        if (-not $path.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installed checksum path escapes the fixture installation.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw "Installed payload checksum mismatch: $relative" }
        $count++
    }
    Assert-Check ($count -eq $metadata.packageFileCount - 1) "All $count installed payload checksums match"
    Assert-Check (Test-Path -LiteralPath $testUninstallKey) 'Only the isolated AppId is registered for uninstall'
    $registration = Get-ItemProperty -LiteralPath $testUninstallKey
    Assert-Check ($registration.InstallLocation.TrimEnd('\') -eq $installRoot) 'Uninstall registry points to this isolated directory'
}
$productionBefore = Get-ProductionGuard
try {
    Invoke-Setup 'desktopicon,autostart'
    Assert-Payload
    Assert-Shortcut $desktopShortcut $app 'Desktop shortcut'
    Assert-Shortcut $startupShortcut $app 'Logon startup shortcut'
    Assert-Shortcut $startMenuShortcut $app 'Start Menu shortcut'
    Assert-Shortcut $uninstallShortcut $uninstaller 'Start Menu uninstall shortcut'
    Assert-FixtureData

    $created = $false
    $heldMutex = [Threading.Mutex]::new($true, [string]$metadata.mutex, [ref]$created)
    Assert-Check $created 'Running-app test mutex has a unique identity'
    $beforeBlockedInstall = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    try {
        Invoke-Setup 'desktopicon,autostart' -ExpectFailure
        Invoke-Uninstaller -ExpectFailure
    }
    finally { $heldMutex.ReleaseMutex(); $heldMutex.Dispose(); $heldMutex = $null }
    Assert-Check ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -eq $beforeBlockedInstall) 'Blocked installation does not overwrite the app'

    Invoke-Setup '' 'chinesesimp'
    Assert-Payload
    Assert-Check (-not (Test-Path -LiteralPath $desktopShortcut)) 'Upgrade can disable the desktop shortcut'
    Assert-Check (-not (Test-Path -LiteralPath $startupShortcut)) 'Upgrade can disable logon startup'
    Assert-FixtureData

    Invoke-Setup 'desktopicon,autostart' 'chinesesimp'
    Assert-Payload
    Assert-Shortcut $desktopShortcut $app 'Restored desktop shortcut'
    Assert-Shortcut $startupShortcut $app 'Restored logon startup shortcut'
    Assert-FixtureData
} catch { $testFailure = $_ }
finally {
    if ($heldMutex) { $heldMutex.ReleaseMutex(); $heldMutex.Dispose() }
    if ($pendingProcess) {
        $uninstallFailure = "UNKNOWN: PID $($pendingProcess.Id) may still be running. Cleanup was not replayed; reconcile its exit and the isolated installation manually."
    } elseif (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            $resolved = [IO.Path]::GetFullPath($uninstaller)
            if (-not $resolved.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Uninstaller is outside the isolated test root.' }
            Invoke-Uninstaller
            for ($attempt = 0; $attempt -lt 50 -and (Test-Path -LiteralPath $testUninstallKey); $attempt++) { Start-Sleep -Milliseconds 100 }
            Assert-Check (-not (Test-Path -LiteralPath $testUninstallKey)) 'Uninstall registry entry is removed'
            Assert-Check (-not (Test-Path -LiteralPath $app)) 'Installed application is removed'
            Assert-Check (-not (Test-Path -LiteralPath $desktopShortcut)) 'Desktop shortcut is removed'
            Assert-Check (-not (Test-Path -LiteralPath $startupShortcut)) 'Logon startup shortcut is removed'
            Assert-Check (-not (Test-Path -LiteralPath $startMenuShortcut)) 'Start Menu shortcut is removed'
            Assert-FixtureData
        } catch { $uninstallFailure = $_ }
    }
    $productionAfter = Get-ProductionGuard
    $checks.Add([ordered]@{ name = 'Production executable, startup shortcut and installer registration remain unchanged'; passed = $productionBefore -eq $productionAfter })
    if ($productionBefore -ne $productionAfter -and -not $testFailure) { $testFailure = 'Production guard changed during the isolated test.' }
    [ordered]@{
        app = 'GameDevUsageBar'; version = $metadata.version; testedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        isolatedAppId = $metadata.appId; installRoot = $installRoot; fixtureRoot = $fixtureRoot
        packageSha256 = $metadata.sha256; checks = $checks.ToArray()
        passed = -not $testFailure -and -not $uninstallFailure
        failure = $(if ($testFailure) { [string]$testFailure } else { $null })
        cleanupFailure = $(if ($uninstallFailure) { [string]$uninstallFailure } else { $null })
        runtimeLaunch = 'Not performed: app uses a shared per-user mutex and data home; separate native-host tests cover runtime.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
Write-Host "Installer verification receipt: $(Join-Path $testRoot 'results.json')"
if ($testFailure) { throw $testFailure }
if ($uninstallFailure) { throw $uninstallFailure }
Write-Host 'Verified actual isolated English/Chinese installation, payload checksums, shortcut options, running-app guard, reinstall, uninstall, and retained synthetic user data. Production installation was not modified.'
