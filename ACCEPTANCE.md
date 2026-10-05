# Release verification

## Windows x64 preview 0.9.1

This is a Windows desktop preview. Checks distinguish source/build results,
synthetic WPF rendering, installer execution, and real provider integration.
Passing a fixture is not proof of a live account or physical desktop behavior.

Local verification uses the pinned .NET SDK from `global.json`, locked NuGet
dependencies, a Release build with warnings treated as errors, and a
self-contained Windows x64 publish. Behavior checks use synthetic credentials,
isolated Windows DPAPI storage, fixture OAuth transports, and loopback HTTP.
They do not redeem tickets, generate content or change a real CLI account.

The native WPF/Win32 suites cover English/Chinese menus, placement, topmost,
presentation persistence, compact panels, multiple accounts and native login
capture/switch safeguards. Additional fixture renders cover overview layouts,
small floating-bar icons, expanded details, dark menus and background opacity.
Screenshot values and account labels are synthetic.

`tools/Test-WindowsInstaller.ps1` refuses production package identities. Its
dedicated smoke installer verifies real per-user installation, executable
version, payload hashes, shortcuts, selected/unselected startup tasks, upgrade,
locked-app refusal, uninstall and retention of seeded profile data. It does
not launch the normal application against a user's live profile.

## Important limits

- Claude/Codex quota windows follow the source response. Plan names alone
  cannot establish allowance, coupon inventory or expiry. Missing values stay
  unknown; cached and failed queries are not displayed as a successful zero.
- Claude renewal is limited to the enabled current local OAuth source. Saved
  account slots do not get automatic login or renewal. An uncertain token
  rotation is journaled and is never automatically replayed.
- Provider support and upstream response fields can change. Fixture coverage
  is not universal verification of every service, region or subscription.
- Gemini AI Studio uses the configured Google Cloud monitoring project;
  Gemini CLI is a separate source. Neither claims an invented API-key balance.
- The network display is Windows adapter upload/download in decimal MB/s;
  it is not per-process bandwidth or VPS traffic.
- This release has no native macOS/Linux/ARM64 installer, automatic updater,
  code-signing certificate or independent antivirus certification.

Use [the release notes](docs/releases/v0.9.1.md) for the final verification
counts and [installation instructions](docs/installation.md) for setup.
