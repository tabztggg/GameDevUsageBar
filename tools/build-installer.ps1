[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$OutputDirectory,
    [string]$InnoCompiler,
    [switch]$SmokeTest,
    [string]$TestId
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Source version is not a supported release version.' }
if (-not $PackageRoot) { $PackageRoot = Join-Path $projectRoot "artifacts/GameDevUsageBar-$version-win-x64" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts/releases/$version" }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory.TrimEnd('\', '/') -eq $PackageRoot.TrimEnd('\', '/') -or $OutputDirectory.StartsWith($PackageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must be outside the runtime package to avoid packaging its own output.' }
$app = Join-Path $PackageRoot 'GameDevUsageBar.exe'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw 'Publish the self-contained Windows x64 runtime before building an installer.' }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
if ($fileVersion.Split('+')[0] -ne $version) { throw "Package version $fileVersion does not match source version $version. Publish again first." }
$manifest = Join-Path $PackageRoot 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Package SHA256SUMS.txt is required.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Malformed package checksum manifest.' }
    $expectedHash = $Matches[1]
    $relative = $Matches[2]
    $file = [IO.Path]::GetFullPath((Join-Path $PackageRoot $relative))
    if (-not $file.StartsWith($PackageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package checksum path escapes its root.' }
    if (-not $seen.Add($file)) { throw 'Duplicate package checksum entry.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expectedHash) { throw "Package checksum mismatch: $relative" }
}
$payload = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object FullName -ne $manifest)
if ($payload.Count -ne $seen.Count) { throw 'Package contains files outside its checksum manifest.' }
foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a link or reparse point.' }
    if ($file.Name -match '^(auth\.json|oauth_creds\.json|\.credentials\.json|settings\.json|presentation\.json|credentials|secrets|native-auth|auth-renewal|auth-vault|\.codex|\.claude|\.env(?:\..*)?)$' -or $file.Extension -in '.pdb', '.pfx', '.pem', '.key', '.bak', '.tmp', '.bin') { throw "User data, debug symbols or private-key material must not be packaged: $($file.Name)" }
}
if (-not $InnoCompiler) {
    $installed = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        (Join-Path $projectRoot '.cache/build-tools/inno/ISCC.exe')
    )
    $InnoCompiler = $installed | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $InnoCompiler) { $InnoCompiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) { throw 'Inno Setup 6.3+ or 7 is required. Pass -InnoCompiler with its ISCC.exe path.' }
$InnoCompiler = [IO.Path]::GetFullPath($InnoCompiler)
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $InnoCompiler -Parent) 'Languages/ChineseSimplified.isl'))) { throw 'The Inno compiler must include Languages/ChineseSimplified.isl.' }
# Some compiler releases have 0.0.0.0 PE metadata; report their engine version
# when the CLI exposes it, otherwise leave it unknown rather than inventing one.
$compilerVersionText = (& $InnoCompiler --version 2>&1 | Out-String).Trim()
$compilerEngineVersion = if ($LASTEXITCODE -eq 0 -and $compilerVersionText -match '^\d+\.\d+\.\d+(?:\.\d+)?$') { $compilerVersionText } else { $null }
if ($SmokeTest) {
    if (-not $TestId) { $TestId = [Guid]::NewGuid().ToString('D') }
    $parsedId = [Guid]::Empty
    if (-not [Guid]::TryParseExact($TestId, 'D', [ref]$parsedId) -or $parsedId -eq [Guid]::Empty -or $parsedId -eq [Guid]'A62A6344-CFA1-42C0-933F-C648F614D433') { throw 'Smoke test identity must be a fresh nonproduction GUID in D format.' }
    $TestId = $parsedId.ToString('D')
} elseif ($TestId) { throw '-TestId can only be used with -SmokeTest.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$arguments = @('/Qp', "/DStage=$PackageRoot", "/DVersion=$version", "/DOutput=$OutputDirectory", "/DAppIcon=$(Join-Path $projectRoot 'src/GameDevUsageBar.App/Assets/GameDevUsageBar.ico')")
if ($SmokeTest) { $arguments += "/DTestId=$TestId" }
$arguments += Join-Path $projectRoot 'installer/GameDevUsageBar.iss'
& $InnoCompiler @arguments | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$packageName = "GameDevUsageBar-$version-win-x64-setup"
if ($SmokeTest) { $packageName += "-test-$TestId" }
$setup = Join-Path $OutputDirectory "$packageName.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Compiler finished without the expected setup executable.' }
$metadata = [ordered]@{
    schemaVersion = 1
    app = 'GameDevUsageBar'
    version = $version
    file = [IO.Path]::GetFileName($setup)
    sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    packageFileCount = $payload.Count + 1
    packageBytes = [long](($payload | Measure-Object -Property Length -Sum).Sum + (Get-Item -LiteralPath $manifest).Length)
    smokeTest = [bool]$SmokeTest
    appId = $(if ($SmokeTest) { $TestId } else { '{A62A6344-CFA1-42C0-933F-C648F614D433}' })
    shortcutName = $(if ($SmokeTest) { "GameDevUsageBar Installer Test $TestId" } else { 'GameDevUsageBar' })
    mutex = $(if ($SmokeTest) { "Local\GameDevUsageBar.InstallerTest.$TestId" } else { 'Local\GameDevBar' })
    compilerVersion = $compilerEngineVersion
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$setup.json" -Encoding utf8
Write-Host "Built $setup"
Write-Output $setup
