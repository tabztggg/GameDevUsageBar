# Runtime logs and local diagnostics

[Home](../README.md) · [中文](diagnostics.zh-CN.md)

Starting with v0.9.2, GameDevUsageBar saves bounded runtime logs while the app runs. They help distinguish an explicit Exit, a Windows session ending, a recorded exception, and a session whose end could not be recorded. This is a local diagnostics feature; it does not add an uploader, updater, service, or restart watchdog.

## Where to find logs

```text
%LOCALAPPDATA%\GameDevBar\logs
```

The existing `GameDevBar` data-folder name is retained for compatibility. Logs include UTC and local timestamps and a session ID to separate runs. The current file is `runtime.jsonl`, followed by rotated segments `runtime.1.jsonl` through `runtime.4.jsonl`. The log budget is five files of at most 1 MiB each, approximately 5 MiB total, plus a small session-state marker. Rotation removes the oldest log segment; it does not modify account settings, balances, or saved logins.

If Windows prevents writes or the disk is unavailable, diagnostics report logging as unavailable. The app does not claim to have saved an event that could not be persisted. A logging failure cannot reliably diagnose a later exit by itself.

The overview warns when the current session cannot save logs. The diagnostic preview reports `FirstLoggingFailure`, `LastLoggingFailure`, and a bounded failure count; the ZIP uses camelCase field names. Each failure contains only a fixed operation stage, exception type, HResult, and UTC time. Messages and paths are excluded. This evidence stays in memory when disk logging fails; a diagnostic ZIP can preserve it and may also contain older session logs. It does not prove that the current session has a complete saved log.

## Events and their meaning

| Event | What it establishes |
| --- | --- |
| `app_start`, `app_ready` | A new process started, and initialization reached the ready state. |
| `handled_exception` | An operation failed and the app handled the error. This does not establish a process crash. |
| `startup_exception` | Initialization raised an exception. |
| `dispatcher_unhandled`, `appdomain_unhandled`, `winforms_unhandled` | An exception reached a UI or process-level exception handler. Consult the subsequent exit events and reason to determine whether it ended the process. |
| `task_unobserved` | A background task fault was reported by .NET. This alone does not prove the app exited. |
| `exit_requested`, `app_exit`, `session_end` | A recorded exit request or completion; its reason identifies the application's observed exit route. |
| `session_ending` | The app received a Windows session-ending notification. |
| `previous_session_incomplete` | On startup, the last session had no completed end record. Its cause is unknown. |
| `resource_sample` | A bounded sample of this process's memory and related resources. |

Known exit reasons include `tray_exit`, `overview_exit`, `tray_popup_exit`, `session_shutdown`, `session_logoff`, `startup_failure`, and `fatal_exception`. A closed overview or hidden widget does not exit the tray process. An exit whose route could not be recorded remains unknown rather than being assigned to a user, antivirus product, or cache.

`previous_session_incomplete` can result from a forced termination, power loss, a system failure, or an exit before the final write. It does not prove who ended the app or why. Abrupt termination cannot run the app's exception or exit handlers. A missing sample or end event is a limit of the evidence, not a diagnosis.

## Investigating memory or cache growth

While the app is running, it samples its resource use every 60 seconds. A sample includes process memory, managed-memory information, thread/handle counts, and bounded cache-size totals. It does not read cached response contents into a log. No separate background monitor continues after the app exits.

Compare consecutive samples in the same session. A consistently increasing memory footprint may support investigating a leak, but one high reading does not establish a leak or out-of-memory failure. File-cache size and process memory are different measurements; a small cache does not rule out an in-memory problem. A sampling error is recorded without fabricating a zero.

## Export a diagnostic bundle

1. Choose **Export diagnostics…** from the tray menu or **Export diagnostics** in the overview.
2. Review the current JSON summary. It includes the application version, runtime/logging state, safe provider status and timings, and local quota API status.
3. Choose **Save diagnostic ZIP…** and select a new local filename. Existing archives are preserved. The ZIP contains a filtered `diagnostics.json`, `runtime-summary.json`, bounded `recent-events.json`, and the known runtime log files. Saving is an explicit local action; nothing is uploaded.

A fixture or session without a runtime logger offers **Save diagnostic JSON…** instead. The preview states that logs are unavailable; it does not present a JSON-only export as a complete log archive.

Exports exclude account configuration, cached balances, credentials, native CLI auth files, and arbitrary files in the app-data directory. Exception records preserve the exception type and method stack without exception messages, source-file paths, HTTP headers, response bodies, tokens, or cookies. The summary still reveals which services are configured or failing and when requests happened. Review a bundle before choosing to share it yourself.

## Coverage limit

These records begin with v0.9.2. Installing this version cannot reconstruct an older exit that had no log. Even with logging enabled, an abrupt process or machine termination may only be recognizable at the next startup as an incomplete session with an unknown cause.
