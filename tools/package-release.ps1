[CmdletBinding()]
param([string]$DotnetPath,[string]$InnoCompiler,[switch]$SkipBuild)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version=(Select-Xml -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -XPath '//Version').Node.InnerText
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version.'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -Publish -DotnetPath $DotnetPath | ForEach-Object {Write-Host $_}}
$published=Join-Path $projectRoot "artifacts/GameDevUsageBar-$version-win-x64"
$release=Join-Path $projectRoot "artifacts/releases/$version"
New-Item -ItemType Directory -Path $release -Force | Out-Null
$setup=& (Join-Path $PSScriptRoot 'build-installer.ps1') -PackageRoot $published -OutputDirectory $release -InnoCompiler $InnoCompiler
$zip=Join-Path $release "GameDevUsageBar-$version-win-x64.zip"
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip}
[IO.Compression.ZipFile]::CreateFromDirectory($published,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try {
    $sourceFiles=@(Get-ChildItem -LiteralPath $published -Recurse -File)
    $archiveFiles=@($archive.Entries | Where-Object {$_.Name.Length -gt 0})
    if($archiveFiles.Count -ne $sourceFiles.Count){throw 'Portable archive file count differs from published runtime.'}
    if(-not $archive.GetEntry('GameDevUsageBar.exe') -or -not $archive.GetEntry('SHA256SUMS.txt')){throw 'Portable archive is incomplete.'}
} finally {$archive.Dispose()}
$assets=@([string]$setup,$zip)
$records=@($assets | ForEach-Object {
    [ordered]@{file=[IO.Path]::GetFileName($_);bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest=Join-Path $release 'package-manifest.json'
[ordered]@{schema_version=1;app='GameDevUsageBar';version=$version;platform='windows';architecture='x64';self_contained=$true;signed=$false;assets=$records} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8
@($assets)+@($manifest) | ForEach-Object {'{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($_)} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Release assets ready: $release"
Write-Output $release
