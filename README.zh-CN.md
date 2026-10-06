# GameDevUsageBar

**把 AI 剩余额度、积分和余额，放进一条安静的 Windows 用量栏。**

Windows 10/11 · x64 · v0.9.1 预览版 · 中文 / English

[English](README.md) · [下载](https://github.com/tabztggg/GameDevUsageBar/releases/latest) · [安装说明](docs/installation.zh-CN.md) · [额度接口](QUOTA-API.zh-CN.md) · [Release 说明](docs/releases/v0.9.1.md)

![单行悬浮栏](docs/screenshots/floating-bar-zh-CN.png)

查看 Claude、Codex 的订阅额度，Tripo、GRSAI 的 API 积分，以及其他服务的余额和本机网速。可使用右下角托盘、桌面单行悬浮栏或完整总览。

*截图来自实际 WPF 界面的模拟数据渲染，不包含真实用户账号，也不代表已验证所有服务的真实访问权限。*

## 下载和启动

在 [Releases](https://github.com/tabztggg/GameDevUsageBar/releases/latest) 中选择：

| 文件 | 用途 |
| --- | --- |
| `GameDevUsageBar-0.9.1-win-x64-setup.exe` | 固定目录安装、开始菜单快捷方式，以及可选登录自启动 |
| `GameDevUsageBar-0.9.1-win-x64.zip` | 解压后运行的便携版 |
| `SHA256SUMS.txt` | 核对下载文件完整性 |

**运行时已包含，无须安装 .NET、Node.js、Git 或开发工具。** 便携版需保留完整目录。

启动后，在服务的**设置**中选择认证来源，配置、启用并保存。新来源默认禁用。升级、卸载和故障处理见[安装说明](docs/installation.zh-CN.md)。

## 悬浮栏和总览

![用量总览](docs/screenshots/overview-zh-CN.png)

- **单行悬浮栏：** 36 DIP 高度、小图标、剩余百分比、原生积分/货币单位和下载/上传 MB/s。
- **自定义两个重点模块：** 任意服务都可放入顶部大模块，其他服务以紧凑行显示，详情可展开。
- **明确重置周期：** 5 小时额度显示小时和分钟，较长周期显示天、小时、分钟；来源返回重置券时，同一行显示张数及过期日期。
- **悬停和点击信息一致：** 都显示额度、重置日期、倒计时和状态；点击面板还提供操作入口。
- **位置和透明度可调：** 拖动握柄、置顶、锁定位置，或调节底色透明度；文字和图标保持清晰。
- **中英文即时切换：** 不改动自定义账号名称。

百分比代表**剩余额度**。缺失信息不补成 0；实时、缓存、过旧、演示、鉴权、权限和限流状态分别显示。

## 支持的服务

| 服务 | 查询内容 | 认证方式 |
| --- | --- | --- |
| Claude | 接口返回的 5 小时、周、模型额度及额外用量 | 当前 CLI OAuth、保存的 CLI 登录、手填 OAuth，或 Firefox Cookie + 组织 UUID |
| Codex | 接口返回的配额周期、套餐、积分和重置券库存/到期 | 当前 CLI、保存的 CLI 登录，或手填订阅 OAuth |
| Tripo | 全球版/中国版 API 可用与冻结积分 | API Key |
| GRSAI | 全球/国内节点的账号剩余积分 | API Key |
| DeepSeek | 官方 API 余额，保留接口币种 | API Key |
| ElevenLabs | 字符额度、已用/剩余字符及重置时间 | API Key |
| OpenRouter | 已购积分减用量后的剩余美元余额 | 有积分查询权限的 API Key |
| Gemini CLI | Code Assist 返回的额度及重置时间 | 当前 CLI 登录或手填 OAuth |
| Gemini AI Studio | Google Cloud 项目近 24 小时完成的 Gemini API 请求数 | 项目 + 具有 Monitoring 读取权限的 Google OAuth |

查询适配已实现，但实际账号须具有相应接口权限。程序按返回字段显示，不根据 Pro/Plus 标签推断固定额度或周期。券库存仅在来源返回时显示；不能保证当前 Claude 接口能查到真实重置券。

Tripo API 积分不包含 Studio 积分。Gemini AI Studio 请求数**不是剩余额度或余额**，不能用 Gemini API Key 查询。OpenRouter 积分接口可能要求管理密钥。本版不包含 TypeSafe 或 VPS 流量查询。

## 多账号和快速切换

点击服务标题旁的箭头，或**托盘菜单 → 账号 → 服务**，选择显示账号。**管理账号**中可添加、编辑、刷新、选择或删除账号；各账号的凭据、节点、用量和刷新状态独立。

选择显示账号只改变栏和总览，不改变 CLI 登录。每个启用账号独立查询，增加账号会增加额度查询请求。

Codex 和 Claude 可在你自行登录各账号后，通过**保存当前 CLI 登录**保存 Windows 当前用户加密的认证副本。**切换 CLI 登录**是独立操作：先关闭 CLI 会话，程序保存加密恢复副本，仅替换支持的 auth 文件/字段并核验。遵循 `CODEX_HOME` 和 `CLAUDE_CONFIG_DIR`，保留 Claude 文件中的其他字段，不修改已有桌面会话、不启动 CLI、不自动登录。

0.9.1 在程序运行时提前续期**已明确连接的当前 Claude 本地 OAuth 登录**。遵守原生刷新锁，已确认响应可本地恢复；保存账号快照和手填令牌不自动续期。续期凭据撤销或到期后需重新登录，结果未知不自动重放。协议来自 Claude Code 实现，可能随上游变化。见[账号与认证](docs/installation.zh-CN.md#账号与认证)。

## 本机只读额度接口

其他本机工具可读取：

```powershell
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/health
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/usage
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/accounts
```

还支持单服务、单账号以及网速接口。仅允许 GET，不返回凭据、不触发刷新、账号切换、令牌续期或付费调用。程序必须运行；接口仅绑定 IPv4 本机回环地址。

调用方须核对所需指标的时效及 `usable`；多个工具读到的余额不是各自独占的额度。详见[中文接口约定](QUOTA-API.zh-CN.md)和安装目录内的 `api/Get-GameDevQuota.ps1`。

## 本地数据与安全

v0.9.2 源码新增有容量上限的启动、退出、错误及资源日志，保存在 `%LOCALAPPDATA%\GameDevBar\logs`。从总览或托盘菜单打开**导出诊断信息…**，预览后可将过滤后的 ZIP 保存到本机。中断的会话会在下次启动时标记，不推断缺乏证据的退出原因。见[诊断日志说明](docs/diagnostics.zh-CN.md)。

配置、缓存、加密凭据和显示设置位于 `%LOCALAPPDATA%\GameDevBar`，沿用旧目录名以保持兼容。安装或卸载不会退出 Codex、Claude 或 Gemini 登录。

额度查询不生成内容、不兑换重置券。当前 Claude 自动续期和明确执行的 CLI 切换，是上文限定的认证写入操作。请求保留 TLS 校验、响应限制、禁用重定向和账号/来源缓存隔离；显示及语言切换不查询服务。

文件未签名。SHA256 核对完整性，不是杀毒认证；不要关闭安全软件安装。见[安装与卸载](docs/installation.zh-CN.md)。

## 构建与扩展

源码要求 `global.json` 固定的 **.NET SDK 10.0.401**；使用依赖锁文件，警告按错误处理。

```powershell
.\tools\build.ps1 -Publish -DotnetPath '<dotnet.exe 的路径>'
# 可选：在解锁的 Windows 桌面上运行隔离 WPF/Win32 检查
.\tools\build.ps1 -UiChecks -DotnetPath '<dotnet.exe 的路径>'
```

Core 管理账号、指标和刷新；Providers 实现来源与解析；Infrastructure 管理受限请求、DPAPI、原生认证、网速和本地接口；App 提供 WPF 界面和托盘。新增服务须同步增加允许的请求/认证路径、解析测试与显示标签，不开放任意 URL 接收凭据。测试范围见 [ACCEPTANCE.md](ACCEPTANCE.md)，模拟通过不等于真实账号接口可用。

## 归属与许可

原创代码[由所有者保留权利](LICENSE.txt)，第三方许可单独保留，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

部分服务图标源自 [CodexBar](https://github.com/steipete/CodexBar/tree/main/Sources/CodexBar/Resources)，保留 MIT 声明；参考或派生解析代码保留 [NOTICE-CodexBar.txt](NOTICE-CodexBar.txt)。产品名称和商标归各自所有者。
