# 安装与卸载

[主页](../README.zh-CN.md) · [English](installation.md)

v0.9.9 安装包面向 **Windows 10/11 x64**，包含 .NET 运行时，启动应用无须另装运行时或开发工具。原生 CLI/浏览器功能需要对应软件；添加 Claude 账号要求另外安装受保护的 Claude Code Bridge。本版没有 macOS、Linux 或原生 ARM64 安装包。

## 选择下载

在[公开 GitHub Release](https://github.com/tabztggg/GameDevUsageBar/releases/tag/v0.9.9) 下载。公开发布文件无须登录 GitHub 即可下载，见 [GitHub 公开文件访问规则](https://docs.github.com/en/rest/releases/assets#get-a-release-asset)。

| 文件 | 用途 |
| --- | --- |
| `GameDevUsageBar-0.9.9-win-x64-setup.exe` | 当前用户安装，安装界面支持中英文 |
| `GameDevUsageBar-0.9.9-win-x64.zip` | 解压后运行 `GameDevUsageBar.exe` |
| `package-manifest.json` | 版本、源码提交、文件大小及 SHA256 |
| `SHA256SUMS.txt` | 发布文件的 SHA256 |

文件未签名。核对来源和 SHA256；不要关闭安全软件。若被拦截，请保留具体检测记录以便调查。哈希一致只证明完整性，不是杀毒认证。

```powershell
Get-FileHash -Algorithm SHA256 .\GameDevUsageBar-0.9.9-win-x64-setup.exe
Get-FileHash -Algorithm SHA256 .\GameDevUsageBar-0.9.9-win-x64.zip
```

与 Release 中 `SHA256SUMS.txt` 的对应文件比较。

## 安装和启动

用将要使用程序的 Windows 用户运行安装包。默认目录为 `%LOCALAPPDATA%\Programs\GameDevUsageBar`。

始终创建开始菜单入口。**登录 Windows 时启动**默认勾选，可取消；**桌面快捷方式**默认不勾选。升级保留此前选项。自启动使用当前用户 Startup 文件夹中的快捷方式，不安装系统服务。

从开始菜单或快捷方式打开。左击右下角托盘图标打开用量面板；右击提供总览、账号、刷新、显示设置、悬浮窗和退出。关闭总览会保留后台托盘；**退出**才结束程序。

选择**桌面悬浮窗**显示单行栏。拖动左侧握柄，图钉控制置顶，**…** 中可锁定和调节半透明。在显示设置中选择语言与重点服务。

便携版须完整解压到稳定目录，不要直接在 ZIP 中运行，也不要只复制 EXE。它与安装版共用当前用户数据目录和单实例标识，一次只运行一份。

从 Codex 或其他管理命令进程的工具部署时，应由资源管理器启动。直接用 `Start-Process` 启动的程序可能继承工具的 Windows 进程组，随工具退出而结束。源码中的 `tools/start-installed.ps1` 使用已有的资源管理器文件夹窗口或已注册的桌面入口，核对新进程的资源管理器父进程；入口不可用时明确失败，不退回命令进程启动。无需保持文件夹窗口打开；日常使用开始菜单或 Windows 登录自启动即可。属于某个进程组并不能确定管理它的是谁。见 [Microsoft 的资源管理器启动说明](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643)及 [Job Objects 文档](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)。

## 账号与认证

在服务**设置**中配置。API 服务使用对应 API Key；Claude/Codex 的订阅 OAuth 与推理 API Key 分开。Tripo、GRSAI 的全球/中国节点由你明确选择，不自动跨区。

本地 CLI 来源连接已有登录。Claude Cookie 导入由你发起，仅支持 Firefox，不自动换浏览器或认证来源。

在**管理账号 → Claude**里填写名称并点击**添加 Claude 账号**，建立独立的 `CLAUDE_CONFIG_DIR`，自动打开本机受保护的 Bridge 登录入口。每次授权会创建全新的 Firefox 配置和独立实例，不导入其他配置的 Cookie、历史或缓存。请在 Firefox 选择目标订阅账号，若提示则将完整授权码粘贴到终端。确认原生进程结束且凭据有变化后，程序只读取、绑定并刷新这个账号。**编辑账号**可改显示名，不移动目录；**登录此账号**可重试已确认失败的登录，未知结果禁止重复。**读取账号登录**不能解除先前未知操作。该入口要求同一台机器另外安装兼容的受保护 Claude Code Bridge；安装包不提供独立的登录客户端。登录不在安装、启动或定时刷新时执行。本版离线测试核验目录隔离和启动约定，不包含真实账号授权验收。

Codex/Claude 多账号操作：

1. 在 CLI 中自行登录目标账号。
2. 在**管理账号**中新建并命名槽位，选择**保存当前 CLI 登录**。
3. 对其他账号重复。
4. 悬停服务可对比所有已保存账号；点击后选择**显示此账号**，或使用托盘账号菜单，改变栏/总览的展示选择。
5. 若要修改真实 CLI 登录，先关闭 CLI 会话，再在管理账号中选择**切换 CLI 登录**，或在点击后的服务商弹窗中，选择对应账号的**切换 CLI 登录**。

点击后的服务面板为每个已保存账号提供操作。**显示此账号**仅切换展示，**当前显示的账号**标记也只有这个含义。受支持的 Codex/Claude 已保存登录可通过**切换 CLI 登录**核验 auth 替换后同步显示；API 账号提供显示选择、刷新和设置，不提供原生 CLI 切换。忙碌、只读或结果未确定时明确提示，不自动重试认证写入；已知账号设置只读时，在修改 auth 前拒绝操作。

登录副本使用 Windows 当前用户 DPAPI 加密。切换前保存加密恢复数据，仅替换 Codex `auth.json` 或 Claude 的 `claudeAiOauth` 字段；遵循 `CODEX_HOME` 和 `CLAUDE_CONFIG_DIR`，保留其他 Claude 字段，不改变已运行的桌面会话。删除应用账号不会删除或退出 CLI 登录。

程序运行时，通过原有的同一个续期管理器，对已启用、明确绑定的**Claude 本地 OAuth**账号在访问令牌到期前自动续期。遵守原生锁和每个账号的准确目录；手动登录到完成绑定期间暂停续期。账号快照和手填令牌不自动续期，到期后需重新登录并保存。续期凭据无效时需重新登录；结果未知会明确报告，不自动重放。重启可以本地恢复已确认保存的响应，不代表应再次发送结果未知的续期。

保存多账号/捕获登录后使用格式 4；独立 Claude 目录使用格式 5，需要 v0.9.6 或更新版本。旧程序会拒绝不支持的配置，不要降级强行读取。

## 网络与服务错误

查询和当前 Claude 续期采用 .NET 默认代理规则：Windows 上先读取代理环境变量，否则读取当前用户代理设置。没有固定私人路由器地址，不自动从代理失败改为直连重试。见[微软官方代理说明](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0)。

401 通常需修正凭据，403 表示服务端权限不足；重新安装 CLI 无法授予服务端权限。限流保留重试期限，缓存或失败值不会补成 0。

Gemini AI Studio 需要项目和 Monitoring 读取权限，其请求数不是剩余额度，可能延迟或缺失；Gemini CLI 使用独立的 Code Assist 来源。Claude 重置券仅在接口返回时显示。

## 升级

先从托盘选择**退出**，只关闭总览不够。运行中，安装器拒绝覆盖，不强制终止程序。

安装到相同目录，替换程序文件并保留 `%LOCALAPPDATA%\GameDevBar` 数据。安装包不包含账号登录或原生 CLI auth。便携版退出后使用新的解压目录，只保留指向目标版本的一份启动快捷方式。

没有自动下载/更新服务；下一版需明确安装已核验的 Release。

## 数据与卸载

程序文件在 `%LOCALAPPDATA%\Programs\GameDevUsageBar`；账号、加密凭据、缓存、恢复记录和显示设置在 `%LOCALAPPDATA%\GameDevBar`；自启动入口在当前用户 Startup 文件夹。

旧数据目录名是兼容设计。加密凭据绑定 Windows 用户，复制目录到其他用户并不能直接使用。

退出后，在 Windows **设置 → 应用**卸载，或运行安装目录的卸载器。移除程序和所属快捷方式/启动项；保留数据目录和原生 CLI 登录。便携版退出后可删除完整程序目录，并移除你创建的对应快捷方式。

删除保留数据是独立选择，需要时先备份，仅删除 `%LOCALAPPDATA%\GameDevBar`。卸载本程序不需要删除 `.codex`、`.claude` 或 `.gemini`。

## 本机接口检查

运行中可查询：

```powershell
Invoke-RestMethod -NoProxy -TimeoutSec 5 http://127.0.0.1:17864/v1/health
```

健康响应只证明本机 API 就绪，不证明所有服务已登录。17864 端口冲突时托盘/悬浮窗仍可使用，API 不自动换端口。接口仅绑定本机回环地址，不应通过隧道开放。

安装目录 `api/` 含中英文约定和 PowerShell 查询脚本，脚本要求 PowerShell 7。见[完整额度接口](../QUOTA-API.zh-CN.md)。
