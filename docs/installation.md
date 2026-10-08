# Installation and removal

[Home](../README.md) · [中文](installation.zh-CN.md)

GameDevUsageBar v0.9.8 packages target **Windows 10/11 x64**. They include the .NET runtime; no separate runtime or developer tools are needed to launch the app. Native CLI/browser integrations require their corresponding software. The protected Claude Code Bridge is a separate prerequisite for Add Claude account. There is no native macOS, Linux, or ARM64 package.

## Choose a package

Download from the [public GitHub release](https://github.com/tabztggg/GameDevUsageBar/releases/tag/v0.9.8). Public release assets can be downloaded without signing into GitHub. [GitHub's public-asset access rules](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)

| File | Purpose |
| --- | --- |
| `GameDevUsageBar-0.9.8-win-x64-setup.exe` | Current-user installation with English/Chinese setup UI |
| `GameDevUsageBar-0.9.8-win-x64.zip` | Portable folder; extract and open `GameDevUsageBar.exe` |
| `package-manifest.json` | Version, source commit, asset sizes, and SHA256 hashes |
| `SHA256SUMS.txt` | Release-asset SHA256 hashes |

The downloads are unsigned. Verify their source and hashes; do not turn off security software. If a scanner blocks a file, retain its detection details for investigation. A matching hash verifies integrity, not antivirus clearance.

```powershell
Get-FileHash -Algorithm SHA256 .\GameDevUsageBar-0.9.8-win-x64-setup.exe
Get-FileHash -Algorithm SHA256 .\GameDevUsageBar-0.9.8-win-x64.zip
```

Compare the hashes with the matching filenames in the release's `SHA256SUMS.txt`.

## Install and start

Run the EXE installer as the Windows user who will use the app. The default path is:

```text
%LOCALAPPDATA%\Programs\GameDevUsageBar
```

Setup always creates a Start menu entry. **Start at Windows logon** is selected by default and may be unchecked; **Desktop shortcut** is optional and unchecked by default. Upgrades retain the prior task selections. Startup uses a shortcut in the current user's Startup folder, not a system service.

Open **GameDevUsageBar** from the Start menu or the selected shortcut. Left-click its notification-area icon for the compact usage panel; right-click for overview, accounts, refresh, display settings, widget, and Exit. Closing the overview leaves the tray running. **Exit** stops the app.

For deployment from Codex or another managed command runner, launch through Windows Explorer rather than directly with `Start-Process`. Child processes can inherit the runner's Windows Job and end when that runner is closed. The source helper `tools/start-installed.ps1` uses an existing Explorer folder view or its registered desktop view, checks the new process's Explorer parent, and refuses a command-process fallback. An open File Explorer window is optional; the Start menu and Windows logon shortcut are the normal desktop launch paths. Job membership alone does not identify the Job owner. See [Microsoft's Explorer launch guidance](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643) and [Job Objects documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

Choose **Desktop widget** for the single-row bar. Drag the left grip, use the pin button for topmost, and use **…** for lock and transparency. Choose your language and featured services in Display settings.

For the portable ZIP, extract all files into a stable folder. Do not run the executable directly inside the ZIP or copy only the EXE. The portable app uses the same current-user data directory and single-instance identity as the installed app; run one copy at a time.

## Accounts and authentication

Configure each service from its **Settings** action. API providers require the appropriate API key; Claude/Codex subscription OAuth is separate from inference API keys. Tripo and GRSAI offer explicit Global/China choices and never silently fall back to another region.

The local CLI option explicitly connects an existing login. Claude Cookie import is initiated by you and supports Firefox profiles; it does not fall back to another browser or auth source.

**Manage accounts → Claude → Add Claude account** takes a required display name and creates an isolated `CLAUDE_CONFIG_DIR`. It starts the maintained Bridge's attended subscription login with its protected proxy and Firefox checks. A fresh Firefox profile and separate instance are created for each authorization, without importing cookies, history or cache. Choose the intended subscription account in Firefox and paste its full code into the terminal if requested. After the contained native process closes successfully, the app reads and binds only this profile and refreshes its quota. Other logins are preserved. **Edit account** renames the display label without moving the profile; **Sign in to this account** retries a confirmed failed attempt. UNKNOWN attempts block repeated sign-in. **Read account login** cannot settle an earlier unknown attempt. This path requires a compatible protected Claude Code Bridge installed separately on the same machine; it is not a standalone sign-in client supplied by the installer. It never runs on a timer or during installation. Offline tests cover profile isolation and the launch contract; an actual attended authorization is not part of the release validation.

For multiple Codex/Claude logins:

1. Sign into the desired account yourself in its CLI.
2. Open **Manage accounts**, create/name a slot, and choose **Save current CLI login**.
3. Repeat for the other accounts.
4. Hover the service to compare all its saved accounts; click and choose **Show this account**, or use the tray account menu, to change the bar/overview selection.
5. To change the actual CLI auth file, close CLI sessions and explicitly choose **Switch CLI login** in Manage accounts, or **Switch CLI login** for the specific account in the clicked provider popup.

The clicked provider panel lists actions for each saved account. **Show this account** changes display selection only; the **Displayed account** badge has the same meaning. For a supported saved Codex/Claude login, **Switch CLI login** verifies the auth-file replacement before selecting it for display. API accounts expose display selection, Refresh, and Settings, without a native CLI-switch action. BUSY, read-only, and unresolved results are shown without automatically retrying an auth write. Known read-only account settings are rejected before native auth is changed.

Captured auth documents are protected with Windows current-user DPAPI. Switching creates encrypted recovery data and replaces only Codex `auth.json` or the Claude `claudeAiOauth` credential field. `CODEX_HOME` and `CLAUDE_CONFIG_DIR` are respected. Other Claude fields and existing desktop sessions remain intact. Deleting an app account does not delete or log out the native CLI.

Enabled, explicitly bound **Claude local-oauth** profiles renew automatically before access-token expiry while GameDevUsageBar runs, through the same existing renewal owner. Native locks and the exact credential directory protect each account; attended login pauses renewal through its new binding. Saved snapshots and manual tokens are not renewed automatically; sign in again and recapture them after expiry. An invalid refresh grant needs a new login. An unknown rotation result is reported and is not automatically replayed. Restarting the app can recover a confirmed saved response locally; it is not a reason to resend an uncertain refresh.

Multiple/captured accounts use schema 4; isolated Claude profiles use schema 5 and require v0.9.6 or later. Older builds reject unsupported configuration instead of silently reading another login directory. Do not downgrade to force a read.

## Network and service errors

Provider queries and current Claude renewal use .NET's default proxy selection. On Windows, proxy environment variables take precedence; otherwise the user's proxy settings are used. There is no fixed private-router address and no automatic proxy-to-direct retry. See [Microsoft's default-proxy documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0).

A 401 generally requires correcting the configured credential; 403 indicates missing server permission. Reinstalling a CLI cannot grant vendor permissions. Rate limits retain their retry deadline. Cached or failed data does not become a fabricated zero.

Gemini AI Studio requires a Google Cloud project and OAuth Monitoring read permission. Its count is not remaining quota and may be delayed or absent. Gemini CLI uses a separate Code Assist source. Optional Claude reset-ticket inventory is shown only if the endpoint provides it.

## Upgrade

Choose **Exit** from the tray before installing over the existing directory. Closing only the overview is insufficient. Setup refuses replacement while the app is running rather than forcibly terminating it.

Install the new package into the same directory. It replaces app files and retains `%LOCALAPPDATA%\GameDevBar` data. No account login or native CLI auth is part of the installer. For portable upgrades, exit first and use a fresh extracted folder; keep only one startup shortcut pointing to the version you intend to run.

There is no automatic download/update service. Install the next verified release explicitly.

## Local data and removal

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Programs\GameDevUsageBar` | Installed app and runtime files |
| `%LOCALAPPDATA%\GameDevBar` | Accounts, encrypted credentials, caches, recovery data, and presentation settings |
| User Startup folder | Optional `GameDevUsageBar.lnk` logon entry |

The old `GameDevBar` data name is intentional. Secrets and captured logins are bound to the Windows user; copying this directory to another user does not make those credentials portable.

Exit the app, then uninstall **GameDevUsageBar** from Windows Settings → Apps, or use its uninstaller. Owned app files, shortcuts, and startup entry are removed. The data directory and native CLI logins are preserved. A portable copy can be removed after exiting; also remove any shortcut you created for that copy.

Deleting the retained data is a separate choice. Back it up if needed and remove only `%LOCALAPPDATA%\GameDevBar`. Do not delete `.codex`, `.claude`, or `.gemini` to remove this app.

## Local API checks

With the app running:

```powershell
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/health
```

A healthy response confirms the local API, not all provider logins. Port conflict leaves the tray/widget available but makes the API unavailable; no alternative port is chosen automatically. The listener is loopback-only and should not be exposed through a tunnel.

Package `api/` contains the English/Chinese API contract and PowerShell client (PowerShell 7 required for that script). [Full quota contract](../QUOTA-API.md)
