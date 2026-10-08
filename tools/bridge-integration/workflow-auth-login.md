## Attended isolated account login

`python -E -s scripts/bridge.py auth-login --config-dir ABS [--result ABS]`
is the maintained entry for a user-clicked GameDevUsageBar Add Claude Account or
attended reauthentication action. Loading this entry does not authorize a login.
Do not call it during installation, startup, a timer, a fixture, or model dispatch.

The app first creates its own empty account directory at the Windows known-folder
LocalAppData `GameDevBar/claude-profiles/<slot-GUID-in-32-hex-N-format>` and writes the ownership marker
`gamedevusagebar-profile.json` with exactly `schema_version: 1`, `provider: "claude"`,
and matching hyphenated `slot_id` (D format). A display label stays in app configuration; it is not a CLI
argument or folder name. The entry refuses the default `.claude`, other paths,
missing/mismatched ownership, links, network paths, and an existing receipt file.
Existing owned profiles may be reauthenticated only after explicit attended user
action, with the collector's renewal/activation suspended for that slot. The CLI
may update authentication only inside that chosen profile. This does not switch
the default account, rotate an active goal to another account, or update billing
evidence.

The entry statically binds the reviewed Windows CLI 2.1.285 hash; it performs no
version or auth-status probe. It preserves the live pinned-proxy/WFP checks and
the maintained shared serial lock. The only native command is `--safe-mode auth
login --claudeai`. CLI 2.1.285 has no auth-login `--no-browser` flag: the pinned
static OAuth implementation prints a manual URL before its browser spawn, and
that spawn is refused by the existing one-process, kill-on-close Windows Job.
The entry captures the current auth URL in transient memory and requests the
maintained verified Firefox launcher once, from outside that Job. The auth-only
launcher exclusively creates a new owned browser container for every attempt at
`<selected-Claude-slot>/.bridge-firefox-auth/<attempt-GUID-N>/`. It synthesizes
`profile/user.js` from the same hash-pinned network defaults plus authentication
isolation preferences; it copies no cookies, history, cache, credentials,
sessions, extensions, or preferences from any existing browser profile. The
generic Firefox account/usage-page launcher and its shared profile are unchanged.
The exact `--no-remote --profile <fresh-profile> --new-window <auth-URL>` command
starts an independent session with no migration/reset flags. A strict environment
allowlist excludes inherited Firefox, XRE, selectable-profile, provider and runtime
overrides. Browser APPDATA, LOCALAPPDATA, HOME/USERPROFILE and temporary paths point
to new empty subdirectories in that attempt container. Immediately before launch,
the owned paths, marker, exact directory entries and canonical startup bytes are
checked again, and all pinned Firefox executable hashes and live WFP guards must
pass. No existing profile state is read for authentication.

Fresh authentication preferences disable Sync/accounts, password saving, OS
client-certificate loading and Windows/Entra SSO; they request private browsing,
disabled history and disabled disk cache. These startup checks do not establish
Firefox runtime behavior, secure erasure, page load, identity, or authentication
success. Fresh directories plus scrubbed startup environment and no migration
arguments are the isolation boundary; the legacy auto-migration preference is
defense in depth and is not relied on. A manual
`:open HTTPS_AUTH_URL` fallback is permitted only before any Firefox launch was
attempted. Only `/cai/oauth/authorize` (the pinned CLI's actual endpoint) or
`/oauth/authorize` HTTPS URLs at `claude.ai` or `claude.com` are
accepted. A browser-launch exception is UNKNOWN and is never retried. The user
chooses the intended subscription account and pastes the full `code#state` into
the visible terminal if prompted; input is hidden. `:cancel` closes the contained
native process. Bridge does not persist raw CLI output, authorization URLs, codes,
tokens or credentials in its own receipts or stream artifacts. The native CLI's
chosen-profile storage remains native behavior; no broader storage/privacy claim
is made from static or fixture evidence.

Receipt schema 1 includes `status: completed_unverified | blocked | failed | unknown`,
`process_closed`, `config_dir_sha256`, an `attempt_id` GUID, exact native PID when
created, exit code, observed root/tree closure and nonsecret Firefox attempt
state, fresh-browser attempt/directory, root PID when available, and retained-profile
metadata. `process_closed` refers to the contained native CLI only. Firefox is
opened separately and can remain open after the CLI exits or is cancelled;
`browser_process_closed` and `browser_tree_closed_verified` remain false because
this entry does not supervise or establish full Firefox tree closure. Every new
attempt receives a new directory; an existing attempt directory is never reused
or overwritten. Prepared, launched and UNKNOWN browser directories are retained;
there is no automatic deletion, browser termination, cleanup/reconcile or retry
endpoint. A launch exception preserves the UNKNOWN attempt and available
nonsecret metadata, and blocks another launch in the same attended run.
`credential_changed` compares the initial and final chosen-profile
credential file hashes internally only after process closure;
`credential_change_verified` says whether that comparison was observed. Neither
credential bytes nor credential hashes are included. A changed file does not
prove successful login or account identity. The path hash is SHA-256 over UTF-8 of the canonical absolute profile path
with `/` replaced by `\`, trailing `\` removed, and case folded, without a newline.
`completed_unverified` means native auth exit zero with root/tree closure, not
verified credentials, identity, plan, quota, account continuity or billing.
GameDevUsageBar separately reads/binds the profile through its existing protected
credential workflow. No model call is used to validate login.

An unresolved `.bridge-auth-login-state.json` prevents another attempt on that
slot. Do not delete/reset this state, replay login, or launch a status probe to
make it pass. Preserve it and diagnose the exact original process/receipt under
fresh authority. There is no automatic retry, goal account switch, logout,
provider fallback or renewal owner here.

Unit/mock tests cover the entry contract and failure handling. Local Windows
fixtures also verify read-only byte-lock contention with the existing serial
lock and the unchanged Job's pipe transport and root/tree cleanup using one
innocuous Python child. These fixtures make no CLI/browser/network/auth request.
The real attended
CLI/Firefox/paste-code interaction remains unverified until actually observed;
static implementation evidence and mock tests do not prove successful login.

Firefox startup references: [Mozilla command-line parameters](https://firefox-source-docs.mozilla.org/browser/CommandLineParameters.html),
[profile selection implementation](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/profile/nsToolkitProfileService.cpp),
and [startup/reset implementation](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/xre/nsAppRunner.cpp).
These explain why profile selection also requires a scrubbed startup environment;
the current upstream source does not substitute for this installation's executable
hash pin or observed runtime authentication.
