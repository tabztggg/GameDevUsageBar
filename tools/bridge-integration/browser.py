#!/usr/bin/env python3
"""Dedicated Firefox entry point. No credential copying or browser fallback.

Static file checks and kernel guard readback do not prove Firefox's runtime
preferences, a loaded Claude page, sign-in, subscription, or account eligibility.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import uuid
from urllib.parse import urlsplit

from network_guard import NetworkError, assert_network_guard, build_proxy_environment, endpoint

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "references" / "policy.json"
ALLOWED_HOSTS = {"claude.ai", "claude.com"}
PROFILE_MARKER = "bridge-profile.json"
AUTH_CONTAINER = ".bridge-firefox-auth"
AUTH_MARKER = "bridge-auth-browser.json"
AUTH_OWNER = "gamedevusagebar-profile.json"
AUTH_ENV_KEYS = {"SYSTEMROOT", "WINDIR", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"}


class BrowserError(Exception):
    pass


class AuthBrowserUnknown(BrowserError):
    """A launch was attempted; safe metadata survives without enabling replay."""
    def __init__(self, metadata):
        super().__init__("Fresh Firefox launch outcome is unknown; no retry or cleanup was attempted.")
        self.metadata = metadata


def need(condition, message):
    if not condition:
        raise BrowserError(message)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def read_json(path):
    path = Path(path)
    need(path.is_file() and path.stat().st_size <= 2 * 1024 * 1024, "Required browser configuration is absent or too large.")
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, UnicodeError):
        raise BrowserError("Browser configuration could not be inspected; no values were logged.") from None
    need(isinstance(value, dict), "Unsupported browser configuration shape.")
    return value


def load_config(policy_path=DEFAULT_POLICY):
    value = read_json(policy_path).get("browser")
    need(isinstance(value, dict), "Required dedicated Firefox policy is missing.")
    return value


def no_links(path):
    """Reject aliases into an ordinary user profile or unpinned installation."""
    path = Path(path)
    need(path.is_absolute(), "Dedicated Firefox paths must be absolute.")
    for item in (path, *path.parents):
        need(not item.is_symlink(), "A dedicated Firefox path contains a link or reparse point.")
        if item.exists():
            attributes = getattr(item.stat(follow_symlinks=False), "st_file_attributes", 0)
            need(not attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
                 "A dedicated Firefox path contains a link or reparse point.")


def fixed_path(value, expected):
    need(isinstance(value, str) and bool(value), "Required dedicated Firefox path is missing.")
    path = Path(value)
    no_links(path)
    need(path.resolve() == expected.resolve(), "A browser path is outside the dedicated installation or profile.")
    return path.resolve()


def canonical_user_js(config):
    ip, port = endpoint(config)
    preferences = {
        "network.proxy.type": 1,
        "network.proxy.http": ip,
        "network.proxy.http_port": port,
        "network.proxy.ssl": ip,
        "network.proxy.ssl_port": port,
        "network.proxy.share_proxy_settings": True,
        "network.proxy.no_proxies_on": "",
        "network.proxy.autoconfig_url": "",
        "network.proxy.socks": "",
        "network.proxy.socks_port": 0,
        "network.proxy.failover_direct": False,
        "network.proxy.allow_bypass": False,
        "network.trr.mode": 5,
        "network.dns.disablePrefetch": True,
        "network.predictor.enabled": False,
        "network.prefetch-next": False,
        "media.peerconnection.enabled": False,
        "network.http.http3.enable": False,
        "network.captive-portal-service.enabled": False,
        "network.connectivity-service.enabled": False,
        "browser.shell.checkDefaultBrowser": False,
        "browser.startup.page": 0,
        "browser.startup.homepage": "about:blank",
        "startup.homepage_welcome_url": "",
        "startup.homepage_welcome_url.additional": "",
    }
    lines = ["// Owned by claude-code-bridge. Defaults are checked before each launch.",
             "// Kernel guards remain required; preferences alone are not network isolation."]
    lines.extend("user_pref(" + json.dumps(key) + ", " + json.dumps(value) + ");" for key, value in preferences.items())
    return ("\n".join(lines) + "\n").encode("utf-8")


def paths_and_applications(config):
    need(isinstance(config, dict), "Required dedicated Firefox policy is missing.")
    executable = fixed_path(config.get("executable"), ROOT / "state" / "firefox" / "firefox.exe")
    profile = fixed_path(config.get("profile_dir"), ROOT / "state" / "firefox-profile")
    policies = fixed_path(config.get("policies_path"), executable.parent / "distribution" / "policies.json")
    need(executable.is_file() and digest(executable) == config.get("executable_sha256"), "Dedicated Firefox executable changed or is absent.")
    need(policies.is_file() and digest(policies) == config.get("policies_sha256"), "Dedicated Firefox policies changed or are absent.")
    applications = config.get("applications")
    need(isinstance(applications, list) and bool(applications), "Dedicated Firefox executable guards are missing.")
    observed = set()
    for item in executable.parent.rglob("*"):
        no_links(item)
        if item.is_file() and item.suffix.lower() == ".exe":
            observed.add(item.resolve())
    approved = set()
    ids = set()
    weights = set()
    common_endpoint = None
    for application in applications:
        need(isinstance(application, dict), "Unsupported Firefox application policy.")
        app_value = application.get("app_path")
        need(isinstance(app_value, str) and Path(app_value).is_absolute(), "Firefox application paths must be absolute.")
        app = Path(app_value)
        no_links(app)
        app = app.resolve()
        need(app.is_relative_to(executable.parent) and app.suffix.lower() == ".exe" and app.is_file(),
             "A Firefox application is outside the dedicated installation.")
        need(app not in approved and digest(app) == application.get("app_sha256"), "Firefox application inventory changed or contains duplicates.")
        approved.add(app)
        guard_id = application.get("guard_id")
        need(isinstance(guard_id, str) and guard_id.startswith("claude-bridge-") and guard_id not in ids,
             "Firefox kernel guard identities are missing or duplicated.")
        ids.add(guard_id)
        weight = application.get("sublayer_weight")
        need(type(weight) is int and 1 <= weight <= 65535 and weight not in weights,
             "Firefox kernel sublayer weights are missing, invalid or duplicated.")
        weights.add(weight)
        proxy = endpoint(application)
        need(common_endpoint is None or common_endpoint == proxy, "Firefox application guards use inconsistent proxy endpoints.")
        common_endpoint = proxy
    need(observed == approved and executable in approved, "Dedicated Firefox executable inventory changed; inspect before approving.")
    ip, port = common_endpoint
    value = read_json(policies).get("policies", {})
    need(isinstance(value, dict), "Unsupported Firefox policy document.")
    proxy = value.get("Proxy", {})
    need(isinstance(proxy, dict) and proxy.get("Mode") == "manual" and proxy.get("Locked") is True
         and proxy.get("HTTPProxy") == f"{ip}:{port}" and proxy.get("SSLProxy") == f"{ip}:{port}"
         and proxy.get("UseHTTPProxyForAllProtocols") is True and proxy.get("Passthrough") == ""
         and not proxy.get("SOCKSProxy") and not proxy.get("AutoConfigURL"),
         "Firefox proxy policy is not the required fixed endpoint without bypass.")
    doh = value.get("DNSOverHTTPS", {})
    preferences = value.get("Preferences", {})
    need(isinstance(doh, dict) and doh.get("Enabled") is False and doh.get("Locked") is True,
         "Firefox DoH policy does not match the required router DNS flow.")
    need(isinstance(preferences, dict), "Unsupported Firefox preference policies.")
    for name in ("media.peerconnection.enabled", "network.http.http3.enable"):
        preference = preferences.get(name, {})
        need(isinstance(preference, dict) and preference.get("Value") is False and preference.get("Status") == "locked",
             "Firefox network preference policies are missing or changed.")
    need(value.get("DisableAppUpdate") is True, "Unattended Firefox updates must not bypass executable review.")
    return executable, profile, policies, applications, {"proxy_ip": ip, "proxy_port": port}


def initialize_profile(config):
    """Create only an empty owned profile; no browser/helper/network process."""
    _, profile, _, _, proxy = paths_and_applications(config)
    data = canonical_user_js(proxy)
    expected_sha = hashlib.sha256(data).hexdigest()
    declared = config.get("profile_user_js_sha256")
    need(declared is None or declared == expected_sha, "Profile defaults do not match the declared hash.")
    if profile.exists() and any(profile.iterdir()):
        validate_profile(profile, config, proxy)
        return {"status": "existing_owned_profile", "profile_dir": str(profile), "profile_user_js_sha256": expected_sha,
                "browser_launched": False, "network_checked": False}
    profile.mkdir(parents=True, exist_ok=True)
    marker = {"schema_version": 1, "kind": "claude-code-bridge-firefox-profile", "proxy_ip": proxy["proxy_ip"],
              "proxy_port": proxy["proxy_port"], "user_js_sha256": expected_sha}
    with (profile / "user.js").open("xb") as stream:
        stream.write(data)
    with (profile / PROFILE_MARKER).open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(marker, sort_keys=True, indent=2) + "\n")
    return {"status": "initialized_owned_profile", "profile_dir": str(profile), "profile_user_js_sha256": expected_sha,
            "browser_launched": False, "network_checked": False}


def validate_profile(profile, config, proxy):
    need(profile.is_dir(), "Dedicated Firefox profile has not been initialized.")
    user_js = profile / "user.js"
    marker_path = profile / PROFILE_MARKER
    no_links(user_js)
    no_links(marker_path)
    data = canonical_user_js(proxy)
    expected_sha = hashlib.sha256(data).hexdigest()
    need(config.get("profile_user_js_sha256") == expected_sha, "Dedicated profile defaults are not pinned to the current policy.")
    need(user_js.is_file() and user_js.stat().st_size == len(data) and user_js.read_bytes() == data,
         "Dedicated Firefox profile defaults changed or are absent.")
    marker = read_json(marker_path)
    need(marker == {"schema_version": 1, "kind": "claude-code-bridge-firefox-profile", "proxy_ip": proxy["proxy_ip"],
                    "proxy_port": proxy["proxy_port"], "user_js_sha256": expected_sha},
         "Dedicated Firefox profile ownership record changed.")
    return expected_sha


def status(config):
    executable, profile, policies, applications, proxy = paths_and_applications(config)
    profile_sha = validate_profile(profile, config, proxy)
    guards = []
    for application in applications:
        guard = assert_network_guard(application, Path(application["app_path"]))
        need(isinstance(guard, dict) and guard.get("verified") is True, "Firefox kernel guard readback was not verified.")
        guards.append({"app_path": application["app_path"], "guard_id": application["guard_id"], "verified": guard["verified"]})
    return {"status": "launch_checks_passed", "executable": str(executable), "profile_dir": str(profile),
            "policies_sha256": digest(policies), "profile_user_js_sha256": profile_sha,
            "guarded_application_count": len(guards), "guards": guards, **proxy,
            "runtime_preferences_verified": False, "browser_launched": False, "account_status_verified": False,
            "scope": "Dedicated Firefox executables and startup files only. Kernel readback is required; browser runtime and page loading need separate observation."}


def auth_status(config):
    """Validate pinned Firefox and live guards without reading shared site state."""
    executable, _, _, applications, proxy = paths_and_applications(config)
    base = canonical_user_js(proxy)
    need(config.get("profile_user_js_sha256") == hashlib.sha256(base).hexdigest(),
         "Fresh Firefox defaults are not pinned to the current policy.")
    for application in applications:
        guard = assert_network_guard(application, Path(application["app_path"]))
        need(isinstance(guard, dict) and guard.get("verified") is True,
             "Firefox kernel guard readback was not verified.")
    return {"status": "launch_checks_passed", "executable": str(executable),
            "guarded_application_count": len(applications), **proxy,
            "browser_launched": False, "runtime_preferences_verified": False}


def auth_account_root():
    need(os.name == "nt", "Fresh attended Firefox login requires Windows.")
    folder = ctypes.create_unicode_buffer(32768)
    result = ctypes.windll.shell32.SHGetFolderPathW(None, 28, None, 0, folder)
    need(result == 0 and folder.value, "Windows LocalAppData could not be resolved.")
    return Path(folder.value) / "GameDevBar" / "claude-profiles"


def auth_local_path(path):
    path = Path(path)
    spelling = str(path)
    need(path.is_absolute() and not spelling.startswith(("\\\\", "//"))
         and ":" not in spelling[2:] and ".." not in path.parts,
         "Fresh Firefox paths must be absolute local owned paths.")
    no_links(path)
    if path.is_file():
        need(path.stat().st_nlink == 1, "Fresh Firefox files must not be shared hard links.")
    return path


def auth_identity(account_profile, attempt_id):
    profile = auth_local_path(account_profile)
    root = auth_local_path(auth_account_root())
    need(profile.is_dir() and profile.parent.resolve() == root.resolve(),
         "Fresh Firefox requires a separate app-owned Claude profile.")
    try:
        slot = uuid.UUID(profile.name)
        attempt = uuid.UUID(attempt_id)
    except (ValueError, TypeError, AttributeError):
        raise BrowserError("Fresh Firefox slot and attempt identities must be canonical GUIDs.") from None
    need(profile.name == slot.hex and attempt_id == str(attempt),
         "Fresh Firefox slot and attempt identities must be canonical GUIDs.")
    owner_path = auth_local_path(profile / AUTH_OWNER)
    need(read_json(owner_path) == {"schema_version": 1, "provider": "claude", "slot_id": str(slot)},
         "The selected Claude profile ownership record changed.")
    return profile.resolve(), str(slot), attempt.hex


def auth_user_js(proxy):
    # The network defaults are synthesized from the same hash-pinned template;
    # no prefs, cookies, logins, history, session or extensions are copied.
    preferences = {
        "identity.fxaccounts.enabled": False,
        "network.http.windows-sso.enabled": False,
        "network.http.microsoft-entra-sso.enabled": False,
        "security.osclientcerts.autoload": False,
        "signon.rememberSignons": False,
        "browser.privatebrowsing.autostart": True,
        "places.history.enabled": False,
        "browser.cache.disk.enable": False,
        "browser.migrate.automigrate.enabled": False,
    }
    lines = ["// Fresh attended authentication only; no prior browser state is imported."]
    lines.extend("user_pref(" + json.dumps(key) + ", " + json.dumps(value) + ");" for key, value in preferences.items())
    return canonical_user_js(proxy) + ("\n".join(lines) + "\n").encode("utf-8")


def prepare_auth_profile(config, account_profile, attempt_id):
    """Exclusively create one never-reused attempt; no process or network call."""
    account, slot, attempt = auth_identity(account_profile, attempt_id)
    _, _, _, _, proxy = paths_and_applications(config)
    need(config.get("profile_user_js_sha256") == hashlib.sha256(canonical_user_js(proxy)).hexdigest(),
         "Fresh Firefox defaults are not pinned to the current policy.")
    parent = auth_local_path(account / AUTH_CONTAINER)
    parent.mkdir(exist_ok=True)
    need(parent.is_dir(), "Fresh Firefox attempt container is not a directory.")
    container = auth_local_path(parent / attempt)
    need(not container.exists(), "This Firefox attempt directory already exists; it will not be reused or overwritten.")
    container.mkdir()  # An incomplete or interrupted directory is retained.
    for name in ("profile", "appdata", "localappdata", "home", "tmp"):
        (container / name).mkdir()
    data = auth_user_js(proxy)
    with (container / "profile" / "user.js").open("xb") as stream:
        stream.write(data)
    record = {"schema_version": 1, "kind": "claude-code-bridge-fresh-auth-browser", "slot_id": slot,
              "attempt_id": attempt_id, "user_js_sha256": hashlib.sha256(data).hexdigest(),
              "launch_attempted": False, "launch_requested": False, "browser_tree_closed_verified": False}
    with (container / AUTH_MARKER).open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(record, sort_keys=True) + "\n")
    return container


def validate_fresh_auth_profile(config, account_profile, attempt_id, container):
    account, slot, attempt = auth_identity(account_profile, attempt_id)
    container = auth_local_path(container)
    need(container == account / AUTH_CONTAINER / attempt and container.is_dir(),
         "Fresh Firefox attempt path changed.")
    need({p.name for p in container.iterdir()} == {"profile", "appdata", "localappdata", "home", "tmp", AUTH_MARKER},
         "Fresh Firefox attempt contains unexpected state.")
    for item in container.rglob("*"):
        auth_local_path(item)
    for name in ("appdata", "localappdata", "home", "tmp"):
        directory = container / name
        need(directory.is_dir() and not any(directory.iterdir()), "Fresh Firefox environment directories contain inherited state.")
    profile = container / "profile"
    need(profile.is_dir() and {p.name for p in profile.iterdir()} == {"user.js"},
         "Fresh Firefox profile contains inherited or unexpected state.")
    _, _, _, _, proxy = paths_and_applications(config)
    data = auth_user_js(proxy)
    user_js = profile / "user.js"
    need(user_js.is_file() and user_js.stat().st_size == len(data) and user_js.read_bytes() == data,
         "Fresh Firefox startup preferences changed.")
    record = read_json(container / AUTH_MARKER)
    need(record == {"schema_version": 1, "kind": "claude-code-bridge-fresh-auth-browser", "slot_id": slot,
                    "attempt_id": attempt_id, "user_js_sha256": hashlib.sha256(data).hexdigest(),
                    "launch_attempted": False, "launch_requested": False, "browser_tree_closed_verified": False},
         "Fresh Firefox ownership or launch state changed; no replay is allowed.")
    return record


def auth_environment(container, proxy, inherited=None):
    inherited = os.environ if inherited is None else inherited
    env = {name.upper(): value for name, value in inherited.items() if name.upper() in AUTH_ENV_KEYS}
    # Deny all inherited profile/reset/migration switches (including XRE and
    # SELECTABLE_PROFILE), provider keys, MOZ variables and Python/runtime hooks.
    env.update(APPDATA=str(container / "appdata"), LOCALAPPDATA=str(container / "localappdata"),
               USERPROFILE=str(container / "home"), HOME=str(container / "home"),
               TEMP=str(container / "tmp"), TMP=str(container / "tmp"))
    return build_proxy_environment(env, proxy)


def open_auth_browser(config, url, account_profile, attempt_id, *, environment=None):
    """One fresh attended auth launch; no reuse, fallback, retry or deletion."""
    url = validate_url(url)
    parsed = urlsplit(url)
    need(parsed.path in {"/cai/oauth/authorize", "/oauth/authorize"} and bool(parsed.query) and not parsed.fragment,
         "Fresh Firefox accepts only the current Claude HTTPS authorization URL.")
    checks = auth_status(config)
    container = prepare_auth_profile(config, account_profile, attempt_id)
    env = auth_environment(container, checks, environment)
    checks = auth_status(config)  # Last pinned executable and live WFP readback.
    record = validate_fresh_auth_profile(config, account_profile, attempt_id, container)
    executable = Path(checks["executable"])
    metadata = {"browser_profile_dir": str(container / "profile"), "browser_profile_fresh": True,
                "browser_profile_retained": True, "browser_process_closed": False,
                "browser_tree_closed_verified": False, "browser_attempt_id": attempt_id}
    record["launch_attempted"] = True
    # Persist before the non-idempotent launch, so an exception cannot permit
    # reuse. This marker has no URL, token, credential or copied browser state.
    with (container / AUTH_MARKER).open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(record, sort_keys=True) + "\n")
    try:
        process = subprocess.Popen([str(executable), "--no-remote", "--profile", str(container / "profile"), "--new-window", url],
                                   cwd=executable.parent, env=env, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, shell=False, close_fds=True)
        record.update(launch_requested=True, pid=process.pid)
        metadata["browser_pid"] = process.pid
        with (container / AUTH_MARKER).open("w", encoding="utf-8", newline="\n") as stream:
            stream.write(json.dumps(record, sort_keys=True) + "\n")
    except BaseException:
        raise AuthBrowserUnknown(metadata) from None
    return {"status": "launch_requested", "browser_launched": True, **metadata,
            "guarded_application_count": checks["guarded_application_count"],
            "page_loaded_verified": False, "runtime_preferences_verified": False, "account_status_verified": False}


def validate_url(url):
    need(isinstance(url, str) and 0 < len(url) <= 8192 and not any(c.isspace() or ord(c) < 32 for c in url) and "\\" not in url,
         "Browser URL must be a valid Claude HTTPS URL.")
    try:
        parsed = urlsplit(url)
        accepted = parsed.scheme == "https" and parsed.hostname in ALLOWED_HOSTS and parsed.port in (None, 443) \
            and parsed.username is None and parsed.password is None
    except ValueError:
        accepted = False
    need(accepted, "Only HTTPS pages on claude.ai or claude.com are allowed.")
    return url


def open_browser(config, url="https://claude.ai/", *, environment=None):
    url = validate_url(url)
    checks = status(config)
    executable = Path(checks["executable"])
    profile = Path(checks["profile_dir"])
    # No shell, ordinary profile, migration, argument forwarding, or fallback.
    env = build_proxy_environment(dict(os.environ) if environment is None else dict(environment), {"proxy_ip": checks["proxy_ip"], "proxy_port": checks["proxy_port"]})
    for name in list(env):
        if name.upper().startswith("MOZ_"):
            env.pop(name)
    try:
        process = subprocess.Popen([str(executable), "--no-remote", "--profile", str(profile), "--new-window", url],
                                   cwd=executable.parent, env=env, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, shell=False, close_fds=True)
    except OSError:
        raise BrowserError("Dedicated Firefox could not be launched; no fallback was attempted.") from None
    return {"status": "launch_requested", "pid": process.pid, "allowed_host": urlsplit(url).hostname,
            "profile_dir": str(profile), "guarded_application_count": checks["guarded_application_count"],
            "browser_launched": True, "page_loaded_verified": False, "runtime_preferences_verified": False,
            "account_status_verified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("init-profile", "status", "open"))
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--url", default="https://claude.ai/")
    args = parser.parse_args()
    try:
        config = load_config(args.policy)
        if args.command == "init-profile":
            result = initialize_profile(config)
        elif args.command == "status":
            result = status(config)
        else:
            result = open_browser(config, args.url)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        return 0
    except (BrowserError, NetworkError) as error:
        print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1
    except Exception as error:
        print(json.dumps({"ok": False, "error_type": type(error).__name__, "error": "Local browser operation failed; no raw account or error values were displayed."}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
