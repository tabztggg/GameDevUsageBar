# Release verification

## Windows x64 preview 0.9.9

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

Local release verification passed 192 behavior checks, 22 WPF/Win32 checks
plus teardown, 10 footer account-switch checks, 8 provider-hover checks,
8 visible popup-position checks, the multiple-account UI suite and 7
native-account host checks. The Claude-account UI suite passed 13 checks.
The runtime suite passed 8 exit/failure scenarios and the diagnostic-export
checks. Overview, compact, strip and transparency suites passed in English
and Chinese. Provider-hover fixtures cover one, two, five and six accounts,
shared hover/click details, per-slot actions and independent error states.

The compact empty-panel regression now waits for the existing deferred WPF
refit before checking the original height limit. A render remains available
in local verification evidence; the production layout was not changed.

Protected-login transport fixtures now inject an owned, nonexecuted dependency
marker through a test-only internal constructor. They no longer depend on a
real user-level Bridge installation. A separate missing-Bridge check verifies
blocking before script launch or receipt creation. The public application
constructor and its required maintained Bridge path are unchanged.

The isolated English/Chinese installer lifecycle passed 62 checks, including
payload integrity, shortcut/startup choices, upgrade, running-app refusal,
uninstall and retention of synthetic user data. Production user data was not
used or modified. The GitHub Windows workflow repeats build, account, layout
and installer checks; its status is reported separately from local results.

`tools/Test-WindowsInstaller.ps1` refuses production package identities. Its
dedicated smoke installer verifies real per-user installation, executable
version, payload hashes, shortcuts, selected/unselected startup tasks, upgrade,
locked-app refusal, uninstall and retention of seeded profile data. It does
not launch the normal application against a user's live profile.

## Important limits

- Claude/Codex quota windows follow the source response. Plan names alone
  cannot establish allowance, coupon inventory or expiry. Missing values stay
  unknown; cached and failed queries are not displayed as a successful zero.
- Claude renewal applies only to enabled, explicitly bound local OAuth
  profiles, including isolated account directories. Saved credential captures
  and manual tokens do not get automatic login or renewal. An uncertain token
  rotation is journaled and is never automatically replayed.
- Adding an isolated Claude account requires a separately installed compatible
  protected Claude Code Bridge, its pinned CLI and Firefox setup. The release
  does not bundle that Bridge. Fresh-browser and auth contracts are covered
  by offline fixtures; an actual attended sign-in was not release-tested.
- Provider support and upstream response fields can change. Fixture coverage
  is not universal verification of every service, region or subscription.
- Gemini AI Studio uses the configured Google Cloud monitoring project;
  Gemini CLI is a separate source. Neither claims an invented API-key balance.
- The network display is Windows adapter upload/download in decimal MB/s;
  it is not per-process bandwidth or VPS traffic.
- This release has no native macOS/Linux/ARM64 installer, automatic updater,
  code-signing certificate or independent antivirus certification.

Use [the release notes](docs/releases/v0.9.9.md) for the final verification
counts and [installation instructions](docs/installation.md) for setup.
