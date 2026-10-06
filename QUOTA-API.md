# GameDevUsageBar local quota API v1

The running Windows application exposes a read-only HTTP API at **http://127.0.0.1:17864**. Maze, Frame and Forge controllers on this computer can call the same API. The application must be running; the API stops when it exits. Startup remains the existing per-user Windows startup shortcut. No separate service, administrator permission, firewall change, credentials or SDK installation is required for consumers.

## Endpoints

| Request | Result |
| --- | --- |
| `GET /v1/network` | Existing Windows adapter speed sample, upload/download decimal MB/s and sample status; no new sampling |
| `GET /v1/health` | App identity, app version, schema version, readiness and read-only flag; optional `runtime` logging status and safe failure metadata |
| `GET /v1/usage` | All registered providers, selected account per provider, including disabled sources |
| `GET /v1/usage/{provider_id}` | Selected account of one provider; unknown or retired IDs return 404 |
| `GET /v1/accounts` | Every saved account across providers |
| `GET /v1/usage/{provider_id}/accounts` | Every saved account of one provider |
| `GET /v1/usage/{provider_id}/accounts/{slot_id}` | One account by GUID slot (hyphenated D format); unknown slots return 404 |

Provider IDs currently include `claude`, `codex`, `tripo`, `grsai`, `deepseek`, `gemini`, `gemini-cli`, `elevenlabs`, `openrouter`, and `demo`. Future providers appear automatically when registered in the app. `grsai-account`, `openrouter-account`, `vps`, and `typesafe` are retired; `/v1/usage/vps` and `/v1/usage/typesafe` return 404.

Only GET is accepted. Other methods return 405. Query strings and request bodies are unsupported (400). Calls do not refresh providers, log in, change settings, spend credits or create model/generation requests. App polling continues on each source's existing interval/backoff policy. The API returns the same in-memory state used by the tray/widget.

## Query examples

```powershell
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/usage
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/usage/claude
& "$env:LOCALAPPDATA\Programs\GameDevUsageBar\api\Get-GameDevQuota.ps1" -Summary
& "$env:LOCALAPPDATA\Programs\GameDevUsageBar\api\Get-GameDevQuota.ps1" -Provider tripo -AsJson
```

The script requires PowerShell 7. `-AsJson` preserves the original JSON numeric precision. Without switches it returns the full object; `-Summary` produces rows containing value, unit, usability, freshness and timestamps.

```python
import json, urllib.request
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
with opener.open("http://127.0.0.1:17864/v1/usage/claude", timeout=5) as response:
    result = json.load(response)
metric = next((m for m in result["provider"]["metrics"] if m["id"] == "five_hour"), None)
remaining = metric["value"] if metric and metric["usable"] else None
```

## Account snapshots (0.9.0)

Existing usage routes follow the current display selection. All-account routes return `accounts`, each containing `provider_id`, `slot_id`, custom `label`, `selected`, and `usage` (the existing provider contract). The single-slot route returns `account`. Slot IDs identify local app records, not vendor identities. Custom labels are exposed only by these account routes. `selected` means selected for display, not proof of the CLI's current login. Each usage record has independent freshness/error and metric usability. Credentials, native identity hashes, credential references and bindings are omitted. GETs do not select accounts or write auth files.

## Contract and decision rules

Envelope fields are `schema_version` (currently 1), `app`, `app_version`, `generated_at` and `shared_account_state=true`. The all-provider response has `providers`; the single response has `provider`. Each provider includes:

- Stable `id`, display `name`, `channel`, `auth_source`, optional `region`, and `enabled`.
- Optional `auth_maintenance` for the current Claude `local-oauth` source: `owner="GameDevUsageBar"`, `enabled`, `state` (`scheduled`, `refreshing`, `ready`, `reauth_required`, `unknown`), and actual `next_attempt_at` or null. It contains no credential or vendor identity. Scheduled/in-progress renewal is an availability wait; `ready` is not proof of fresh quota. Unknown rotation outcomes are not automatically replayed. GET does not trigger renewal.
- `status` from the existing presentation state; `freshness` is `fresh`, `cached`, `stale`, `expired`, `invalid_time`, `no_data`, `disabled` or `demo`.
- `retrieved_at`, `age_seconds`, `refresh_interval_seconds`, `last_attempt_at`, `next_attempt_at`, and typed `error` (or null).
- `can_use_for_budget=true` means at least one fresh live metric is usable. **Consumers must also check the chosen metric's `usable` flag.** A usable weekly metric cannot replace an expired 5-hour metric.
- `metrics`: `id`, English `label`, `label_zh_cn`, `kind`, exact numeric `value`, `unit`, optional `total`, `remaining_percent`, `window_seconds`, `resets_at`, `reset_in_seconds`, `date_meaning`, `scope` and `usable`.

Times use UTC offsets/ISO 8601. `remaining_percent` is remaining quota, not used quota. Claude `five_hour` has `window_seconds=18000`; its weekly metrics have 604800. Codex windows retain `limit_window_seconds` reported by the API. Other windows are null unless their duration is known. Reset tickets use unit `tickets`; expiry groups have `date_meaning=expiry`. `date_meaning=expiry` denotes credit expiry rather than quota reset. After a metric's reset/expiry time passes, `usable=false` until fresh data arrives. A true zero remains numeric 0; unknown or unreported values remain null, never an invented 0.

Previous-session cache, failed refreshes, data older than the configured interval plus 60 seconds, future-dated snapshots, disabled sources, and Demo values are unusable. A local cache-save failure may retain usable fresh live values. `origin=demo` is always excluded from production budgets.

API values are shared account snapshots, **not separate project allocations or reservations**. Three controllers reading the same balance do not each own that amount. Reading a balance does not authorize paid calls, spending, project work, new tasks, or release a STOP. Existing account/source scope, user authorization, single-writer/lease rules and project approval gates remain authoritative. This API cannot promise quota or reserve it between reads and consumption.

## Local access and failure behavior

Only IPv4 loopback is bound. No LAN/cloud endpoint, CORS, proxy forwarding, remote access or port fallback is enabled. The Host header must match `127.0.0.1:<port>`. Requests with Origin/cross-site browser headers are rejected with 403. The API omits API keys, Cookies, OAuth tokens, native account IDs, native credential paths and cache bindings. Local processes can read numerical balances; do not expose this listener with tunnels or proxies.

If port 17864 is occupied, the API reports unavailable in app diagnostics and the overview; the tray/widget continue to function. Connection failure means API unavailable, not zero quota. Health readiness only proves local API readiness, not successful authentication for every provider. HTTP 200 may contain source failures or unknown metrics; always inspect the payload. HTTP 503 means snapshot unavailable. This interface has no mutation/refresh endpoint.

Current validation covers local serving, concurrent GETs, exact balances, quota windows, unknown/zero separation, stale/cache/Demo filtering, credential omission, browser-origin/Host rejection and port conflict. Live provider validity still depends on each configured source's state.

`GET /v1/network` returns `status` (`live`, `warming_up`, `unavailable`, `disabled`), adapter ID/name, `download_mb_per_second`, `upload_mb_per_second`, `sampled_at` and `unit=MB/s`. Unknown speeds are null. Network samples are local adapter throughput, not VPS bandwidth allowance. Consumers should require a recent sample and `status=live`. This endpoint uses the same loopback/Host/Origin/GET guard as quota endpoints.
