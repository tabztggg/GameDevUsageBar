[CmdletBinding(SupportsShouldProcess)]
param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\GameDevUsageBar'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'This launcher requires a Windows desktop session.' }
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$appPath = Join-Path $installRoot 'GameDevUsageBar.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw 'The installed GameDevUsageBar executable was not found.' }
if (@(Get-Process -Name GameDevUsageBar -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'GameDevUsageBar is already running. Use its tray Exit action before changing how it is launched.'
}
if (-not $PSCmdlet.ShouldProcess($appPath, 'Launch through an existing Windows Explorer folder view')) { return }
if (-not ('GameDevUsageBar.ExplorerLaunchProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace GameDevUsageBar {
    public static class ExplorerLaunchProbe {
        [DllImport("user32.dll", SetLastError=true)]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        public static bool? InAnyJob(uint processId) {
            var handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) return null;
            try { bool result; return IsProcessInJob(handle, IntPtr.Zero, out result) ? (bool?)result : null; }
            finally { CloseHandle(handle); }
        }
    }
}
'@
}
$shell = New-Object -ComObject Shell.Application
$windows = $shell.Windows()
$window = $null
$explorerProcess = $null
$launchMethod = 'Explorer folder-view Application.ShellExecute'
foreach ($candidate in $windows) {
    if ([IO.Path]::GetFileName([string]$candidate.FullName) -ine 'explorer.exe') { continue }
    [uint32]$ownerId = 0
    [void][GameDevUsageBar.ExplorerLaunchProbe]::GetWindowThreadProcessId([IntPtr]$candidate.HWND, [ref]$ownerId)
    $owner = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ownerId)
    if ($null -ne $owner -and $owner.Name -ieq 'explorer.exe' -and $null -ne $candidate.Document.Application) {
        $window = $candidate
        $explorerProcess = $owner
        break
    }
}
if ($null -eq $window) {
    # The desktop is itself an Explorer-owned folder view and remains available
    # when every File Explorer window is closed. Obtain its automation object
    # from the registered desktop view, never a fresh ShellExecute object.
    # https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
    $desktopLocation = 0
    $desktopRoot = 0
    $desktopHwnd = 0
    $candidate = $windows.FindWindowSW([ref]$desktopLocation, [ref]$desktopRoot, 8, [ref]$desktopHwnd, 1)
    if ($null -ne $candidate -and [IO.Path]::GetFileName([string]$candidate.FullName) -ieq 'explorer.exe') {
        [uint32]$ownerId = 0
        [void][GameDevUsageBar.ExplorerLaunchProbe]::GetWindowThreadProcessId([IntPtr]$desktopHwnd, [ref]$ownerId)
        $owner = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ownerId)
        if ($null -ne $owner -and $owner.Name -ieq 'explorer.exe' -and $null -ne $candidate.Document.Application) {
            $window = $candidate
            $explorerProcess = $owner
            $launchMethod = 'Explorer desktop-view Application.ShellExecute'
        }
    }
}
if ($null -eq $window) {
    throw 'No verified Explorer folder or desktop view is available. Launch GameDevUsageBar from the Start menu. No command-process fallback was attempted.'
}
# Use the Application obtained from a view hosted by Explorer. A fresh
# Shell.Application.ShellExecute can instead launch from this command process.
$requestedUtc = [DateTimeOffset]::UtcNow
try {
    $window.Document.Application.ShellExecute($appPath, '', $installRoot, 'open', 0)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $appProcess = $null
    do {
        $appProcess = @(Get-CimInstance Win32_Process -Filter "Name='GameDevUsageBar.exe'" |
            Where-Object { $_.ExecutablePath -ieq $appPath -and $_.CreationDate.ToUniversalTime() -ge $requestedUtc.UtcDateTime.AddSeconds(-1) }) | Select-Object -First 1
        if ($null -eq $appProcess) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $appProcess -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $appProcess) { throw 'The new process could not be verified.' }
    if ($appProcess.ParentProcessId -ne $explorerProcess.ProcessId) { throw 'The expected Explorer parent could not be verified.' }
    [pscustomobject]@{
        ProcessId = $appProcess.ProcessId
        ExecutablePath = $appPath
        ParentProcessId = $explorerProcess.ProcessId
        ParentName = $explorerProcess.Name
        InAnyJob = [GameDevUsageBar.ExplorerLaunchProbe]::InAnyJob($appProcess.ProcessId)
        LaunchMethod = $launchMethod
    }
} catch {
    # A COM/RPC failure can occur after Explorer has already created the process.
    throw ('Launch result UNKNOWN after request at ' + $requestedUtc.ToString('o') + '. No fallback, termination, or replay was attempted. Check the tray and process list before trying again.')
}
# InAnyJob reports membership in any Windows Job, not the identity of its owner.
