param([switch]$Publish,[switch]$UiChecks,[string]$DotnetPath)
$ErrorActionPreference='Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = (Select-Xml -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -XPath '//Version').Node.InnerText
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid source version.'}
$sdk = if($DotnetPath){[IO.Path]::GetFullPath($DotnetPath)}else{[IO.Path]::GetFullPath((Join-Path $projectRoot '..\..\toolchain\dotnet\dotnet.exe'))}
if(-not (Test-Path -LiteralPath $sdk)){ $sdk=(Get-Command dotnet -ErrorAction Stop).Source }
if((& $sdk --version) -ne '10.0.401'){throw 'This source requires the pinned .NET SDK 10.0.401.'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $projectRoot
try {
  & $sdk restore GameDevUsageBar.sln --locked-mode
  if($LASTEXITCODE -ne 0){throw 'Locked restore failed.'}
  & $sdk build GameDevUsageBar.sln -c Release --no-restore
  if($LASTEXITCODE -ne 0){throw 'Build failed.'}
  & $sdk run --project tests\GameDevUsageBar.Tests -c Release --no-build
  if($LASTEXITCODE -ne 0){throw 'Behavior checks failed.'}
  if($UiChecks){
    & $sdk run --project tests\GameDevUsageBar.AppTests -c Release --no-build -- --popup-positioning
    if($LASTEXITCODE -ne 0){throw 'Popup positioning integration checks failed.'}
    & $sdk run --project tests\GameDevUsageBar.AppTests -c Release --no-build
    if($LASTEXITCODE -ne 0){throw 'WPF/Win32 integration checks failed.'}
    & $sdk run --project tests\GameDevUsageBar.AppTests -c Release --no-build -- --multi-account
    if($LASTEXITCODE -ne 0){throw 'Multi-account and native-auth integration checks failed.'}
    & $sdk run --project tests\GameDevUsageBar.AppTests -c Release --no-build -- --runtime-lifecycle
    if($LASTEXITCODE -ne 0){throw 'Runtime lifecycle and diagnostics checks failed.'}
  }
  if($Publish){
    $destination = Join-Path $projectRoot "artifacts\GameDevUsageBar-$version-win-x64"
    $published = Join-Path $projectRoot "artifacts\.publish-$version-$([Guid]::NewGuid().ToString('N'))"
    & $sdk publish src\GameDevUsageBar.App -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $published
    if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
    # A previous publish may leave symbols containing machine-specific paths.
    Get-ChildItem -LiteralPath $published -Recurse -File -Filter '*.pdb' | ForEach-Object {Remove-Item -LiteralPath $_.FullName}
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'README.zh-CN.md'),(Join-Path $projectRoot 'ACCEPTANCE.md'),(Join-Path $projectRoot 'NOTICE-CodexBar.txt'),(Join-Path $projectRoot 'QUOTA-API.md'),(Join-Path $projectRoot 'QUOTA-API.zh-CN.md') -Destination $published -Force
    $apiFolder=Join-Path $published 'api';New-Item -ItemType Directory -Path $apiFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'QUOTA-API.md'),(Join-Path $projectRoot 'QUOTA-API.zh-CN.md'),(Join-Path $projectRoot 'tools/Get-GameDevQuota.ps1') -Destination $apiFolder -Force
    $launchFolder=Join-Path $published 'tools';New-Item -ItemType Directory -Path $launchFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/start-installed.ps1') -Destination $launchFolder -Force
    foreach($notice in @('LICENSE.txt','THIRD-PARTY-NOTICES.md')) {
      $noticePath=Join-Path $projectRoot $notice
      if(Test-Path -LiteralPath $noticePath){Copy-Item -LiteralPath $noticePath -Destination $published -Force}
    }
    $licenseRoot=Join-Path $projectRoot 'licenses'
    if(Test-Path -LiteralPath $licenseRoot){Copy-Item -LiteralPath $licenseRoot -Destination $published -Recurse -Force}
    $documentation=Join-Path $projectRoot 'docs'
    if(Test-Path -LiteralPath $documentation){Copy-Item -LiteralPath $documentation -Destination $published -Recurse -Force}
    $checksumFile = Join-Path $published 'SHA256SUMS.txt'
    Get-ChildItem -LiteralPath $published -Recurse -File | Where-Object FullName -ne $checksumFile | Sort-Object FullName | ForEach-Object {
      $relativeName = $_.FullName.Substring($published.Length+1).Replace('\','/')
      '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$relativeName
    } | Set-Content -LiteralPath $checksumFile -Encoding utf8
    # Both paths are fixed descendants of this project's artifacts directory.
    # Stage a fresh payload, then retain the preceding package for recovery.
    if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination (Join-Path $projectRoot "artifacts\.previous-$version-$([Guid]::NewGuid().ToString('N'))")}
    Move-Item -LiteralPath $published -Destination $destination
  }
} finally {Pop-Location}
