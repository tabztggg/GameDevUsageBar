# GameDevUsageBar 额度查询接口 v1

应用运行时，本机 Maze、Frame、Forge 总控统一从 `http://127.0.0.1:17864` 查询。应用退出后接口停止；沿用现有 Windows 登录自启动。

| 请求 | 用途 |
| --- | --- |
| `GET /v1/network` | 已有网速采样：下载、上传 MB/s，网卡及采样状态 |
| `GET /v1/health` | 检查应用身份、版本和接口状态；可选 `runtime` 显示日志状态和安全的错误元数据 |
| `GET /v1/usage` | 每个服务当前选中账号的额度及状态 |
| `GET /v1/usage/claude` | 单个服务当前选中账号；末尾可换成其他服务 ID |
| `GET /v1/accounts` | 全部服务的所有保存账号 |
| `GET /v1/usage/claude/accounts` | 单个服务的所有保存账号 |
| `GET /v1/usage/claude/accounts/{slot_id}` | 按带连字符的 GUID 查询单个账号槽位 |

服务 ID：`claude`、`codex`、`tripo`、`grsai`、`deepseek`、`gemini`（AI Studio API）、`gemini-cli`、`elevenlabs`、`openrouter`、`demo`。未来新增服务会随注册自动加入。

多账号接口返回 `accounts`，每项包括 `provider_id`、本机槽位 `slot_id`、自定义名称 `label`、是否选中显示 `selected` 和原有格式的 `usage`；指定槽位接口返回 `account`。名称仅在多账号接口公开，槽位不是服务商身份。`selected` 不证明 CLI 当前登录身份。各账号独立返回新鲜度、错误和指标可用性；查询不会切换账号或写入 auth 文件。

总控可以直接执行：

```powershell
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/usage
& "$env:LOCALAPPDATA\Programs\GameDevUsageBar\api\Get-GameDevQuota.ps1" -Summary
& "$env:LOCALAPPDATA\Programs\GameDevUsageBar\api\Get-GameDevQuota.ps1" -Provider claude -AsJson
```

脚本需要 PowerShell 7。`-Summary` 显示表格行；`-AsJson` 保留原始 JSON 精度。查询只读应用内存中的最新状态，不触发上游刷新、登录、设置修改或生成调用。

返回结构：版本字段 `schema_version=1`；全部服务为 `providers` 数组，单个服务为 `provider` 对象。每个服务含获取时间 `retrieved_at`、数据年龄 `age_seconds`、来源状态 `status`、新鲜度 `freshness`、错误 `error` 和指标 `metrics`。时间为 ISO 8601/UTC，可转换成当地时间。

额度判断必须检查目标指标的 **`usable=true`**，再读取 `value` 和 `unit`。`can_use_for_budget` 只表示该服务至少有一个有效指标，不能替代目标指标检查。`remaining_percent` 是剩余百分比。Claude 的 `five_hour` 是 **5 小时剩余额度**，`window_seconds=18000`；每周额度独立返回。重置或积分到期后，指标暂不可用，须等待新查询。`date_meaning=expiry` 表示积分到期时间。

缓存、失败、超过刷新间隔加 60 秒的数据、禁用来源、Demo、未知值都不能作为有效额度。真实 0 保留为 0，未知保留为 null，接口未启动也不能当作 0。HTTP 200 不代表每个服务登录成功，必须检查各服务状态。

三套总控共享同一账户余额，**不是每个项目各自拥有一份额度**，也不表示已预留资源或获得消费批准。原有 STOP、派单、租约、预算和费用授权规则保持有效；不得据此自动恢复项目制作或增加付费调用。

接口仅监听 `127.0.0.1:17864`，不对局域网或云端开放，不返回 API Key、Cookie、OAuth 令牌、账户身份或凭据路径。其他本机进程可以读取余额数字；不要通过隧道或代理外露。端口冲突时接口不可用，主程序继续运行，概览和诊断会报告问题。

完整字段、Python 示例和错误定义见同目录 `QUOTA-API.md`。

当前 Claude `local-oauth` 可另含 `auth_maintenance`：续期执行者 `owner=GameDevUsageBar`、是否启用 `enabled`、实际状态 `state`（scheduled／refreshing／ready／reauth_required／unknown）及真实下次尝试时间 `next_attempt_at` 或 null。不返回凭据或服务商身份。已安排／正在续期时保留等待；ready 只说明凭据就绪，仍须核验新鲜额度。续期结果未知不盲目重放；GET 查询不触发续期。


VPS 流量功能已移除，`/v1/usage/vps` 返回 404。Codex 保留接口实际报告的额度窗口秒数。重置券的 `unit=tickets`，按到期日期分组的条目 `date_meaning=expiry`，缺失日期保留 null。

`/v1/network` 只读取最近一次 Windows 网卡采样，不触发新采样或服务刷新。返回 `status`（live/warming_up/unavailable/disabled）、`adapter_id`、`adapter_name`、`download_mb_per_second`、`upload_mb_per_second`、`sampled_at`、`unit=MB/s`。未知网速为 null；总控应核对采样时间和 live 状态。本机网速与 VPS 流量额度是不同指标。此入口沿用相同的本机只读访问保护。

TypeSafe 已移除，`/v1/usage/typesafe` 返回 404。
