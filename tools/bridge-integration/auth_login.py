"""Attended subscription login for one GameDevUsageBar-owned isolated profile.

Imported only by the maintained bridge.py auth-login entry. No standalone runner,
credential extraction, auth-status probe, model request, logout, or retry path.
"""
from __future__ import annotations

import contextlib
import ctypes
import datetime as dt
import getpass
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import stat
import subprocess
import sys
import threading
import uuid
from urllib.parse import urlsplit

import browser
from network_guard import assert_network_guard, build_proxy_environment, check_settings, check_settings_object, check_managed_registry

PINNED_CLI_SHA256 = "121fc8151ed40bd9c144d68aa1cea23427803628ffab65e23da1cceda155697e"
OWNER_MARKER = "gamedevusagebar-profile.json"
LOGIN_STATE = ".bridge-auth-login-state.json"
URL_PATTERN = re.compile(r"https://(?:claude\.ai|claude\.com)/(?:cai/)?oauth/authorize\?[^\s\x00-\x20\x7f<>\"']+")


class LoginBlocked(Exception):
    """Only authored messages, never native output or exception values."""


def need(condition, message):
    if not condition:
        raise LoginBlocked(message)


def timestamp():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def path_hash(path):
    spelling = str(path).replace("/", "\\").rstrip("\\").casefold()
    return hashlib.sha256(spelling.encode("utf-8")).hexdigest()


def profile_root():
    """Use the OS known folder, never an inherited caller path override."""
    need(os.name == "nt", "Attended login is supported only on this Windows installation.")
    folder = ctypes.create_unicode_buffer(32768)
    result = ctypes.windll.shell32.SHGetFolderPathW(None, 28, None, 0, folder)
    need(result == 0 and folder.value, "Windows LocalAppData could not be resolved.")
    return Path(folder.value) / "GameDevBar" / "claude-profiles"


def no_aliases(path):
    path = Path(path)
    need(path.is_absolute(), "Login paths must be absolute local paths.")
    spelling = str(path)
    need(not spelling.startswith(("\\\\", "//")) and ":" not in spelling[2:]
         and ".." not in path.parts, "Network, device, alternate-stream or parent-relative paths are not allowed.")
    for item in (path, *path.parents):
        need(not item.is_symlink(), "A login path contains a link or reparse point.")
        if item.exists():
            attributes = getattr(item.stat(follow_symlinks=False), "st_file_attributes", 0)
            need(not attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
                 "A login path contains a link or reparse point.")


def read_record(path):
    no_aliases(path)
    need(path.is_file() and path.stat().st_size <= 16384, "The local login ownership or state record is missing or invalid.")
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, UnicodeError):
        raise LoginBlocked("The local login record could not be inspected; no values were displayed.") from None
    need(isinstance(value, dict), "Unsupported local login record shape.")
    return value


def validate_profile(value):
    path = Path(value)
    no_aliases(path)
    root = profile_root()
    no_aliases(root)
    need(path.parent.resolve() == root.resolve(), "Login requires a separate GameDevUsageBar-owned Claude profile.")
    try:
        slot = str(uuid.UUID(path.name))
    except (ValueError, AttributeError):
        raise LoginBlocked("The isolated profile directory must be its account slot GUID.") from None
    need(path.name.casefold() == uuid.UUID(slot).hex and path.is_dir(), "The isolated account profile directory must be its canonical 32-hex account slot.")
    marker = read_record(path / OWNER_MARKER)
    need(marker == {"schema_version": 1, "provider": "claude", "slot_id": slot},
         "The isolated account profile ownership does not match this slot.")
    # Inspect path metadata only, never auth/credential file contents. Refuse a
    # nested link before traversal, including an alias outside this profile.
    for directory, directories, files in os.walk(path, followlinks=False):
        for name in directories + files:
            item = Path(directory) / name
            no_aliases(item)
            need(not item.is_file() or item.stat().st_nlink == 1, "A login profile file has another hard-link owner.")
    return path.resolve(), slot


def validate_result(value, profile, attempt_id):
    path = Path(value) if value else profile / (".bridge-auth-result-" + attempt_id + ".json")
    no_aliases(path)
    need(path.parent.is_dir() and not path.exists(), "Login receipt requires an existing local directory and a fresh file.")
    need(path.suffix.casefold() == ".json", "The login receipt must be a separate JSON file.")
    return path


def child_environment(profile, network_config, firefox, inherited=None):
    inherited = os.environ if inherited is None else inherited
    # Deliberate allowlist: keys, provider selection, PYTHONPATH, runtime/TLS
    # injection, unknown BROWSER and bypass/proxy variables are never inherited.
    allowed = {"SYSTEMROOT", "WINDIR", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"}
    base = {name: value for name, value in inherited.items() if name.upper() in allowed}
    base.update({"USERPROFILE": str(profile), "HOME": str(profile),
                 "APPDATA": str(profile / ".bridge-appdata"),
                 "LOCALAPPDATA": str(profile / ".bridge-localappdata"),
                 "TEMP": str(profile / ".bridge-tmp"), "TMP": str(profile / ".bridge-tmp"),
                 "CLAUDE_CONFIG_DIR": str(profile), "BROWSER": str(firefox),
                 "CLAUDE_CODE_MAX_RETRIES": "0", "CLAUDE_CODE_RETRY_WATCHDOG": "0",
                 "CLAUDE_CODE_NONSTREAMING_TIMEOUT_RETRIES": "0", "MAX_STRUCTURED_OUTPUT_RETRIES": "1",
                 "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1", "CLAUDE_CODE_SKIP_PROMPT_HISTORY": "1",
                 "DISABLE_AUTOUPDATER": "1"})
    return build_proxy_environment(base, network_config)


def validate_auth_url(value):
    browser.validate_url(value)
    parsed = urlsplit(value)
    need(parsed.path in {"/cai/oauth/authorize", "/oauth/authorize"} and bool(parsed.query) and not parsed.fragment,
         "Only this login's Claude HTTPS authorization page can be opened.")
    return value


def captured_auth_url(text):
    # The pinned login prints its manual authorization URL before trying its
    # native browser spawn. OSC hyperlinks can surround that URL.
    for match in URL_PATTERN.finditer(text):
        if match.end() == len(text):
            continue  # Never open a partial URL split across pipe reads.
        preceding = text[max(0, match.start() - 128):match.start()]
        if "If the browser didn't open, visit:" not in preceding:
            continue
        candidate = match.group(0).split("\x1b", 1)[0]
        try:
            return validate_auth_url(candidate)
        except (LoginBlocked, browser.BrowserError, ValueError):
            continue
    return None


def browser_environment(inherited=None):
    inherited = os.environ if inherited is None else inherited
    allowed = {"SYSTEMROOT", "WINDIR", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"}
    return {name: value for name, value in inherited.items() if name.upper() in allowed}


def credential_digest(profile, bridge):
    """Internal change comparison only; no bytes or digest leave this helper."""
    path = profile / ".credentials.json"
    no_aliases(path)
    need(not path.exists() or path.is_file() and path.stat().st_nlink == 1,
         "The profile credential is not an independently owned regular file.")
    return bridge.file_hash(path) if path.is_file() else None


@contextlib.contextmanager
def read_only_serial_lock(path):
    """Contend on the existing maintained byte lock without changing its file."""
    import msvcrt
    from ctypes import wintypes as w
    no_aliases(path)
    need(path.is_file() and path.stat().st_size >= 1, "The maintained Bridge serial lock is unavailable.")
    class Overlapped(ctypes.Structure):
        _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                    ("Offset", w.DWORD), ("OffsetHigh", w.DWORD), ("hEvent", w.HANDLE)]
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LockFileEx.argtypes, kernel.LockFileEx.restype = [w.HANDLE, w.DWORD, w.DWORD, w.DWORD, w.DWORD, ctypes.POINTER(Overlapped)], w.BOOL
    kernel.UnlockFileEx.argtypes, kernel.UnlockFileEx.restype = [w.HANDLE, w.DWORD, w.DWORD, w.DWORD, ctypes.POINTER(Overlapped)], w.BOOL
    with path.open("rb") as stream:
        handle = msvcrt.get_osfhandle(stream.fileno())
        overlap = Overlapped()
        need(bool(kernel.LockFileEx(handle, 3, 0, 1, 0, ctypes.byref(overlap))),
             "Another Bridge operation is active or its serial lock is unavailable.")
        try:
            yield
        finally:
            kernel.UnlockFileEx(handle, 0, 1, 0, ctypes.byref(overlap))


def check_login_settings(profile, bridge, guard):
    paths = [profile / "settings.json", profile / "settings.local.json", profile / "remote-settings.json"]
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "ClaudeCode"
    paths.append(program_files / "managed-settings.json")
    if (program_files / "managed-settings.d").exists():
        paths.extend(sorted((program_files / "managed-settings.d").glob("*.json")))
    for ancestor in (profile, *profile.parents):
        paths.extend([ancestor / ".claude" / "settings.json", ancestor / ".claude" / "settings.local.json"])
    checked = check_settings(paths, guard)
    checked.extend(check_managed_registry())
    for path in paths:
        if path.exists():
            no_aliases(path)
            value = json.loads(path.read_text(encoding="utf-8-sig"))
            need(value.get("forceLoginMethod") in (None, "claudeai") and not value.get("forceLoginGatewayUrl"),
                 "Managed or profile settings require another login channel.")
    # Registry source values are checked again for channel restrictions, without
    # returning or recording any values.
    import winreg
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        try:
            with winreg.OpenKey(hive, r"SOFTWARE\Policies\ClaudeCode", 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
                value = json.loads(winreg.QueryValueEx(key, "Settings")[0])
                check_settings_object(value)
                need(value.get("forceLoginMethod") in (None, "claudeai") and not value.get("forceLoginGatewayUrl"),
                     "Managed settings require another login channel.")
        except FileNotFoundError:
            pass
    return checked


def _attended(process, stdout, stderr, stdin, browser_config, profile, attempt_id):
    """Raw child output stays in transient memory; hidden user input is not logged."""
    output = queue.Queue(maxsize=128)
    entries = queue.Queue(maxsize=8)
    def drain(stream):
        try:
            while block := stream.read(4096):
                output.put(block)
        finally:
            output.put(None)
    def input_worker():
        while True:
            try:
                line = getpass.getpass("Paste the login code, or type :cancel: ")
            except (EOFError, KeyboardInterrupt):
                line = ":cancel"
            entries.put(line)
            if line == ":cancel":
                return
    for stream in (stdout, stderr):
        threading.Thread(target=drain, args=(stream,), daemon=True).start()
    threading.Thread(target=input_worker, daemon=True).start()
    print("A fresh protected Firefox session will open for this login, without previous browser cookies or history. Choose the intended account there.", flush=True)
    tail = ""
    open_attempted = False
    receipt_info = {"browser_launch_attempted": False, "browser_launch_requested": False, "browser_launch_unknown": False}
    while True:
        while True:
            try:
                block = output.get_nowait()
            except queue.Empty:
                break
            if block is not None:
                tail = (tail + block.decode("utf-8", errors="replace"))[-32768:]
                url = captured_auth_url(tail)
                if url and not open_attempted:
                    # Mark before calling: a failed/uncertain launch is never
                    # repeated, including a user-entered :open command.
                    open_attempted = True
                    receipt_info["browser_launch_attempted"] = True
                    try:
                        result = browser.open_auth_browser(browser_config, url, profile, attempt_id, environment=browser_environment())
                        receipt_info.update({key: value for key, value in result.items() if key.startswith("browser_")})
                        receipt_info["browser_launch_requested"] = result.get("browser_launched") is True
                        print("Firefox launch requested. Complete sign-in and paste its code here if prompted.", flush=True)
                    except BaseException as error:
                        if isinstance(error, browser.AuthBrowserUnknown):
                            receipt_info.update(error.metadata)
                        receipt_info["browser_launch_unknown"] = True
                        print("The Firefox launch outcome is unknown. This login will not retry it; cancel if no protected page appeared.", flush=True)
                    tail = ""
        try:
            code = process.wait(0.1)
            return code, False, receipt_info
        except subprocess.TimeoutExpired:
            pass
        try:
            line = entries.get_nowait()
        except queue.Empty:
            continue
        if line == ":cancel":
            return None, True, receipt_info
        if line.startswith(":open ") and not open_attempted:
            # A manually supplied fallback is still an attended auth-only input,
            # validated by both this entry and the maintained Firefox launcher.
            try:
                url = validate_auth_url(line[6:])
            except (LoginBlocked, browser.BrowserError, ValueError):
                print("That input is not an allowed Claude authorization URL.", flush=True)
                continue
            open_attempted = True
            receipt_info["browser_launch_attempted"] = True
            try:
                result = browser.open_auth_browser(browser_config, url, profile, attempt_id, environment=browser_environment())
                receipt_info.update({key: value for key, value in result.items() if key.startswith("browser_")})
                receipt_info["browser_launch_requested"] = result.get("browser_launched") is True
            except BaseException as error:
                if isinstance(error, browser.AuthBrowserUnknown):
                    receipt_info.update(error.metadata)
                receipt_info["browser_launch_unknown"] = True
                print("The Firefox launch outcome is unknown; no retry will be made.", flush=True)
            continue
        need(not line.startswith(":"), "Unsupported attended login command; no input was sent to Claude.")
        need(len(line) <= 8192 and all(33 <= ord(character) <= 126 for character in line)
             and re.fullmatch(r"[^#]+#[^#]+", line) is not None,
             "Only the browser's full code and state can be pasted into this auth-only command.")
        stdin.write((line + "\n").encode("utf-8"))
        stdin.flush()
        line = ""


def dispatch(args, bridge):
    need(sys.stdin.isatty(), "This login requires a visible attended terminal; no unattended launch is allowed.")
    profile, slot = validate_profile(args.config_dir)
    attempt_id = str(uuid.uuid4())
    receipt_path = validate_result(args.result, profile, attempt_id)
    receipt = {"schema_version": 1, "status": "blocked", "process_closed": True,
               "config_dir_sha256": path_hash(profile), "attempt_id": attempt_id,
               "slot_id": slot, "started_at": timestamp(), "finished_at": None,
               "root_exit_observed": False, "job_tree_exit_observed": False,
               "cli_started": False, "account_status_verified": False,
               "credential_changed": False, "credential_change_verified": False,
               "automatic_retry": False, "exit_code": None}
    # Exclusive reservation forbids overwriting another attempt or a credential.
    with receipt_path.open("x", encoding="utf-8") as stream:
        json.dump(receipt, stream, sort_keys=True)
    process = None
    own_state = False
    launch_attempted = False
    initial_credential = None
    locks = contextlib.ExitStack()
    state_path = profile / LOGIN_STATE
    try:
        locks.enter_context(read_only_serial_lock(bridge.PRODUCTION_STATE / "bridge.lock"))
        if state_path.exists():
            previous = read_record(state_path)
            need(previous.get("schema_version") == 1 and previous.get("slot_id") == slot
                 and previous.get("config_dir_sha256") == receipt["config_dir_sha256"], "The prior login state belongs to another profile.")
            need(previous.get("status") in {"completed_unverified", "failed", "blocked"} and previous.get("process_closed") is True,
                 "The prior login outcome is unresolved; no authentication attempt was repeated.")
        policy = bridge.policy_load(bridge.DEFAULT_POLICY)
        need(policy.get("approved_cli_versions") == ["2.1.285"], "This auth entry has not been reviewed for another CLI version.")
        no_aliases(bridge.CLI)
        need(bridge.CLI.is_file() and bridge.file_hash(bridge.CLI) == PINNED_CLI_SHA256
             and PINNED_CLI_SHA256 in policy.get("approved_cli_sha256", []),
             "The pinned Claude executable is absent or changed; no CLI command was run.")
        guard = policy.get("network_guard")
        browser_config = policy.get("browser")
        browser_checks = browser.auth_status(browser_config)
        need(browser_checks.get("status") == "launch_checks_passed", "Protected Firefox checks did not pass.")
        need(Path(browser_checks["executable"]).resolve() == bridge.FIREFOX.resolve(), "Unapproved browser executable.")
        check_login_settings(profile, bridge, guard)
        initial_credential = credential_digest(profile, bridge)
        env = child_environment(profile, guard, bridge.FIREFOX)
        for directory in (".bridge-appdata", ".bridge-localappdata", ".bridge-tmp"):
            (profile / directory).mkdir(exist_ok=True)
        validate_profile(str(profile))
        guard_checks = assert_network_guard(guard, bridge.CLI)
        need(guard_checks.get("verified") is True and bridge.file_hash(bridge.CLI) == PINNED_CLI_SHA256,
             "Live Claude network protection or binary pin was not verified.")
        receipt.update(status="unknown", process_closed=False, cli_started=False, reason="native_start_pending")
        bridge.save(receipt_path, receipt)
        bridge.save(state_path, receipt)
        own_state = True
        # Pipes contain the native auth stream only in memory. Job containment
        # refuses its automatic browser child; Firefox is opened by Bridge.
        with contextlib.ExitStack() as stack:
            in_read, in_write = os.pipe()
            out_read, out_write = os.pipe()
            err_read, err_write = os.pipe()
            stdin_read = stack.enter_context(os.fdopen(in_read, "rb", buffering=0))
            stdin_write = stack.enter_context(os.fdopen(in_write, "wb", buffering=0))
            stdout_read = stack.enter_context(os.fdopen(out_read, "rb", buffering=0))
            stdout_write = stack.enter_context(os.fdopen(out_write, "wb", buffering=0))
            stderr_read = stack.enter_context(os.fdopen(err_read, "rb", buffering=0))
            stderr_write = stack.enter_context(os.fdopen(err_write, "wb", buffering=0))
            command = [str(bridge.CLI), "--safe-mode", "auth", "login", "--claudeai"]
            launch_attempted = True
            process = bridge.WindowsJob(command, env, profile, stdin_read, stdout_write, stderr_write, process_limit=1)
            receipt.update(cli_started=True, pid=process.pid, reason="attended_auth_pending")
            bridge.save(receipt_path, receipt)
            bridge.save(state_path, receipt)
            process.resume()
            code, cancelled, info = _attended(process, stdout_read, stderr_read, stdin_write, browser_config, profile, attempt_id)
            receipt.update(info, exit_code=code)
            receipt["root_exit_observed"] = process.terminate()
            receipt["job_tree_exit_observed"] = process.tree_exit_observed
            receipt["process_closed"] = receipt["root_exit_observed"] and receipt["job_tree_exit_observed"]
            if not receipt["process_closed"] or cancelled or info.get("browser_launch_unknown"):
                receipt.update(status="unknown", reason="attended_cancelled" if cancelled else "login_outcome_unresolved")
            else:
                receipt.update(status="completed_unverified" if code == 0 else "failed",
                               reason="native_auth_exit_zero" if code == 0 else "native_auth_failed")
    except (LoginBlocked, bridge.BridgeError) as error:
        receipt.update(status="unknown" if receipt["cli_started"] else "blocked", reason=str(error))
    except BaseException:
        receipt.update(status="unknown" if receipt["cli_started"] else "blocked", reason="local_auth_operation_interrupted_or_failed")
    finally:
        if process is not None:
            try:
                receipt["root_exit_observed"] = process.terminate()
                receipt["job_tree_exit_observed"] = process.tree_exit_observed
                receipt["process_closed"] = receipt["root_exit_observed"] and receipt["job_tree_exit_observed"]
            except BaseException:
                receipt.update(process_closed=False, status="unknown", reason="native_process_closure_unresolved")
            finally:
                process.close()
        elif launch_attempted:
            receipt.update(process_closed=False, status="unknown", reason="native_process_closure_unresolved")
        receipt["finished_at"] = timestamp()
        if receipt["cli_started"] and receipt["process_closed"]:
            try:
                terminal_credential = credential_digest(profile, bridge)
                receipt["credential_changed"] = terminal_credential is not None and terminal_credential != initial_credential
                receipt["credential_change_verified"] = True
            except BaseException:
                receipt.update(status="unknown", credential_change_verified=False, reason="credential_change_comparison_unresolved")
        try:
            bridge.save(receipt_path, receipt)
            if own_state:
                bridge.save(state_path, receipt)
        finally:
            locks.close()
    return receipt
