# ChatGPT API Only

<img src="output/imagegen/app-logo.png" alt="ChatGPT API Only 应用图标" width="96" />

桌面版 ChatGPT 启动器，支持官方账号登录与自定义 API 两种连接方式。

## 主要功能

1. **不用手动编辑配置文件**
   通过“官方账号”“自定义 API”两个 Tab 选择连接方式；自定义模式可填写提供者名称、API 地址、API Key、模型和思考层级。
2. **解决启动时卡住一分钟的问题**
   让启动阶段无法访问的 OpenAI/ChatGPT 外壳地址立即失败，不再等待网络超时。典型启动等待由约 60 秒缩短到约 7 秒（60 秒 → 7 秒），自定义 API 请求不受影响。
3. **可选修复历史对话丢失问题**
   需要时手动点击“修复对话”，将本地历史对话重新关联到当前 provider；修复前自动备份，并显示真实的 `n/total` 进度。
4. **客户端安装和更新入口**
   未安装 ChatGPT 时提示前往商店安装；启动页和配置页均提供“打开应用商店”“检查更新”按钮。

## 下载

正式 Windows 构建位于 [GitHub Releases](https://github.com/shellus/ChatGPTApiOnly/releases)。可直接下载 `ChatGPTApiOnly.exe`，也可下载版本化 Windows ZIP。

源码仓库不跟踪构建产物。GitHub 自动生成的 “Source code” 压缩包只包含源码，不包含可运行 EXE。

## 界面预览

### 启动进度

![ChatGPT API Only 启动进度](docs/images/loading.gif)

### 官方账号

![ChatGPT API Only 官方账号设置](docs/images/official-mode.png)

### 自定义 API

![ChatGPT API Only 自定义 API 设置](docs/images/custom-mode.png)

配置截图使用隔离 fixture 和 `example` 占位值，不包含真实 API 配置或本机信息。

## 启动流程

- 首先检测 ChatGPT 是否已安装；未安装时询问是否前往商店，确定后打开安装页面并退出，取消则直接退出。安装完成后重新运行启动器。
- 配置有效时显示预计启动进度并启动桌面应用。
- 配置不完整或登录方式与路由不一致时打开连接设置；启动页也可通过按钮或空格键打开设置。
- 表单保存成功后继续启动；保存或历史对话修复失败时不启动。
- 不使用启动器级单实例锁。

配置写入用户 Codex 目录下的 `config.toml` 与 `auth.json`，两种模式的凭证和模型设置保存在该目录的 `launcher-profiles/modes.json`。这些文件包含私人配置，不进入版本控制。

## 官方账号与自定义 API

- Tab 切换只改变界面，点击“应用并启动”才写入配置并重新启动客户端。保存前会停止正在运行的客户端；无法停止或保存失败时不会继续启动。
- **官方账号**：使用内置 `openai` provider，直接连接官方服务，沿用官方 OAuth 凭证。点击下方“应用并启动”打开客户端，由客户端完成登录；切换账号时在客户端账号菜单退出后重新登录。启动器不自行实现 OAuth，也不验证账号权益。
- **自定义 API**：使用 `custom` provider 和 API Key，保留已有 API 配置及默认中等（`medium`）的思考层级。OAuth 令牌不会写入此模式的活动认证文件。
- 模式切换保留另一个模式的配置；切走官方模式时保存客户端最新刷新后的凭证。官方客户端退出登录后，不恢复旧凭证。首次切入官方模式不沿用自定义模型名，由官方客户端选择默认模型；之后保留官方模式自身的模型设置。
- 如果发现官方凭证但路由仍为 `custom`，默认展示官方 Tab，等待“应用并启动”后才改为官方路由。认证方式由 `auth.json` 的 `auth_mode` 与对应凭据决定，请求路由由 `model_provider` 决定。
- 启动器不写入 `forced_login_method`；存在显式登录限制时提示冲突，由用户核对配置。`cli_auth_credentials_store` 默认不写，已有 `file` 配置及注释原样保留；若显式使用 `keyring`、`auto`、`ephemeral` 等其他存储方式，提示冲突并阻止切换。
- 切入官方模式前，将整个 `model_providers` 表（含其他提供者、未知字段、注释和嵌套表）原文保存在 `launcher-profiles/modes.json` 的 `model_providers_toml` 字段，并从 `config.toml` 移除；切回自定义 API 时恢复该片段，再更新表单对应字段。官方模式下，自定义 API 表单从该文件读取；重复保存官方模式不会清空已保存的提供者。
- 不删除 `profile`、`chatgpt_base_url` 或 `openai_base_url`。选中了 `profile` 时阻止两种模式的切换；切到官方模式时，发现显式路由覆盖字段也会提示冲突。提供者须使用 TOML 表头，根级内联表或点分赋值会提示冲突。冲突检查在停止客户端及写入文件之前完成，保留现有配置供手动核对。
- “修复对话”仅位于自定义 API Tab，显式把历史数据关联到 `custom`；模式切换不会迁移历史对话，两个 provider 的历史可见范围可能不同。

登录方式和凭证存储配置以 [Codex 官方配置 schema](https://github.com/openai/codex/blob/main/codex-rs/core/config.schema.json) 为依据。

## 官方账号独立代理

官方账号 Tab 中的“HTTP 代理”接受 `http://主机:端口`，点击“应用并启动”后生效；留空不设置独立代理。代理软件须保持监听，系统代理和 TUN 可以关闭。自定义 API 模式不应用这项设置。

地址仅保存在 `launcher-profiles/modes.json` 的 `official_proxy_url`，不写入 Codex 的 `config.toml`、`auth.json` 或 Windows 系统代理。启动器为官方客户端设置：

- `--proxy-server`：覆盖 Electron 的 HTTP、HTTPS 和 WebSocket 网络路径。
- `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`：供内置 Codex 后端和支持这些变量的子进程使用。
- `NO_PROXY=localhost,127.0.0.1,::1`：本机通信直连。
- `NODE_USE_ENV_PROXY=1`：让支持该选项的 Node 辅助进程使用环境代理。

这些环境变量只写入新客户端进程的启动环境，不修改启动器、其他已运行应用或用户级环境变量。客户端发起的工具子进程也会继承；自行清理环境的工具或 WSL/远程主机不保证生效。浏览器中的外部登录页面、Microsoft Store 是独立应用，不由这些启动参数控制。

客户端已在后台运行时，重复启动由官方单实例机制处理，新参数不会改写旧进程环境；修改代理后应使用“应用并启动”重新启动客户端。代理地址不支持账号密码或 URL 路径。

Windows 原生客户端实测中，HTTP 参数与代理环境变量组合、SOCKS5 组合、PAC 与环境变量组合均完成了官方模型对话；仅 Electron 参数或仅环境变量未能完成整个启动流程。Node 辅助进程单独验证了 `NODE_USE_ENV_PROXY` 的必要性。启动器采用 HTTP 组合，避免 PAC 服务依赖及 Node SOCKS 支持差异。偶发重连不作为代理失败依据，应结合完整回复、代理连接记录和服务端错误判断。

实现依据：[Electron 代理启动参数](https://www.electronjs.org/docs/latest/api/command-line-switches#--proxy-serveraddressport)、[Node 环境代理](https://nodejs.org/api/cli.html#node_use_env_proxy1)。相关边界见 [Node helper 代理问题](https://github.com/openai/codex/issues/22623) 和 [WSL 环境传递问题](https://github.com/openai/codex/issues/37662)。

## 安装与更新客户端

- **打开应用商店**：打开 [ChatGPT 官方商店页面](https://apps.microsoft.com/detail/9PLM9XGG6VKS)，在商店中安装或更新客户端。
- **检查更新**：打开 Microsoft Store 的“下载和更新”页面，由商店执行更新检查并安装 ChatGPT 更新；启动器不自行查询最新版本或显示“已是最新版”。
- 系统未注册商店协议或启动商店失败时，自动尝试在默认浏览器打开上述官方商店网页。浏览器也无法打开时，提示可手动访问的网址。
- 更新完成后通过本启动器启动 ChatGPT，会自动选择本机已安装的最高版本。修复对话期间，商店和更新按钮暂时禁用。

产品 ID `9PLM9XGG6VKS` 来自 OpenAI 的 [Windows 部署说明](https://developers.openai.com/codex/enterprise/windows-deployment)。商店产品页和更新页使用微软公开支持的 [Microsoft Store URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app)。

## 工作原理

自定义 API 模式让 Electron 外壳访问可选的 OpenAI/ChatGPT 云端地址时立即失败，避免不可达网络请求等待超时；内置 Codex app-server 仍按用户配置访问自定义 API。官方模式不附加这些阻断规则，并从客户端子进程环境移除 `OPENAI_API_KEY` 和 `OPENAI_BASE_URL`，使官方登录请求走官方服务。

这些外壳域名规则也可能影响客户端内置更新器。独立的 Microsoft Store 不使用启动器传给客户端的网络规则，可通过“检查更新”入口更新客户端。

## Provider 字段

`model_provider` 是 Codex 用于筛选历史对话的 provider ID。自定义 API 模式固定使用 `custom`，官方模式使用内置 `openai`：

```toml
model_provider = "custom"

[model_providers.custom]
name = "显示名称"
```

表单中的“提供者名称”对应 `name`，不是 provider ID。“修复对话”按钮会显式地将历史对话元数据同步为 provider ID `custom`，不能把显示名称写入历史数据库。保存配置不会隐式修复对话。修复期间临时显示真实的 `n/total` 进度，完成或失败提示关闭后隐藏进度区。

进度按扫描对话、统计数据库、备份、修复分别计数，每个阶段重新计算 `n/total`。扫描与修复逐行处理 rollout，备份直接复制文件；后台只保留最新进度，界面每 100 毫秒读取一次，避免大量历史对话占满内存或积压界面消息。

同步范围与 Codex++ 的 Provider metadata sync 保持兼容：

- `sessions` 与 `archived_sessions` 中 rollout JSONL 的 `session_meta.payload.model_provider`；
- SQLite 的 `threads.model_provider`；
- 存在时同步 `local_thread_catalog.model_provider`；
- 修改前备份到 Codex 目录的 `backups_state/provider-sync`。

## 构建

项目以 Windows 自带的 .NET Framework C# 编译器构建，不依赖额外运行库：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:output\imagegen\app.ico `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.exe ChatGPTApiOnly.cs
```

Provider 同步的隔离测试入口只在定义 `PROVIDER_SYNC_TEST` 时编译：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /define:PROVIDER_SYNC_TEST /target:winexe `
  /win32icon:output\imagegen\app.ico `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.test.exe ChatGPTApiOnly.cs
```

测试必须通过 `CHATGPT_API_ONLY_CONFIG_DIR` 指向隔离 fixture，禁止对真实 Codex 目录运行测试入口。

自动回归测试会自行创建临时 fixture 并设置上述环境变量，覆盖成功更新、无变更幂等、数据库失败回滚、备份、保存配置不触发同步、大量历史对话内存占用，以及修复期间的界面响应和分阶段进度：

同一测试入口还覆盖客户端安装检测分支、确认和取消安装、商店和更新 URI、浏览器回退与双重失败提示；这些测试使用替代启动动作，不实际安装或更新客户端。

OAuth 回归使用虚构的 `example` 凭证，覆盖混合登录状态、双向切换、刷新后凭证保留、官方启动参数、Tab 不落盘、多文件保存失败恢复、退出登录不恢复旧凭证，以及切换不修改历史数据。

模式配置回归还覆盖完整提供者配置的保存、清理与恢复，带引号的表头、嵌套表、多行值、重复保存、已有文件存储设置保留，以及路由或凭据存储冲突和保存失败时三个配置文件均不被改写。

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /define:PROVIDER_SYNC_TEST /target:exe /main:ProviderSyncTests `
  /win32icon:output\imagegen\app.ico `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.test.exe ChatGPTApiOnly.cs tests\ProviderSyncTests.cs
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& .\ChatGPTApiOnly.test.exe
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
```

界面回归测试会短暂打开使用 `example` 配置的测试窗口，自动关闭本测试线程的结果提示框，并把应用窗口截图保存在输出的临时目录。

应用图标源文件为 `output/imagegen/app-logo.png`，ICO 包含 16、20、24、32、40、48、64、96、128、256 像素尺寸，编译时嵌入 EXE。修改源图后，在安装了 Pillow 的 Python 环境中运行 `python scripts/build_icon.py` 重建 `output/imagegen/app.ico`。图标文件无需随正式 EXE 单独分发。

## 上游与许可证

对话 Provider metadata 同步的数据范围、备份与事务策略参考了 [Codex++](https://github.com/BigPizzaV3/CodexPlusPlus) 的实现，并针对本项目的单文件 .NET Framework 启动器重新实现。

本项目采用 [GNU Affero General Public License v3.0](LICENSE)，SPDX 标识为 `AGPL-3.0-only`。
