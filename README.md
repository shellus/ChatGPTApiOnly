# ChatGPT API Only

桌面版 ChatGPT 启动器，面向使用自定义 API 的 Microsoft Store ChatGPT/Codex 桌面应用。

## 主要功能

1. **不用手动编辑配置文件**
   通过图形化表单填写提供者名称、API 地址、API Key、模型和推理级别，自动维护 `config.toml` 与 `auth.json`。
2. **解决启动时卡住一分钟的问题**
   让启动阶段无法访问的 OpenAI/ChatGPT 外壳地址立即失败，不再等待网络超时。典型启动等待由约 60 秒缩短到约 7 秒（60 秒 → 7 秒），自定义 API 请求不受影响。
3. **可选修复历史对话丢失问题**
   需要时手动点击“修复对话”，将本地历史对话重新关联到当前 provider；修复前自动备份，并显示真实的 `n/total` 进度。

## 下载

正式 Windows 构建位于 [GitHub Releases](https://github.com/shellus/ChatGPTApiOnly/releases)。可直接下载 `ChatGPTApiOnly.exe`，也可下载版本化 Windows ZIP。

源码仓库不跟踪构建产物。GitHub 自动生成的 “Source code” 压缩包只包含源码，不包含可运行 EXE。

## 界面预览

### 启动进度

![ChatGPT API Only 启动进度](docs/images/loading.gif)

### API 配置与对话修复

![ChatGPT API Only API 配置与对话修复](docs/images/config-form.gif)

配置动画使用 `example` 占位值，不包含真实 API 配置或本机信息。

## 启动流程

- 配置有效时显示预计启动进度并启动桌面应用。
- 配置无效时打开自定义 API 表单；启动页也可通过按钮或空格键打开表单。
- 表单保存成功后继续启动；保存或历史对话修复失败时不启动。
- 不使用启动器级单实例锁。

配置写入用户 Codex 目录下的 `config.toml` 与 `auth.json`。真实 API Key 不应写入源码、项目文档或版本控制。

## 工作原理

启动器让 Electron 外壳访问可选的 OpenAI/ChatGPT 云端地址时立即失败，避免不可达网络请求等待超时；内置 Codex app-server 仍按用户配置访问自定义 API。

## Provider 字段

`model_provider` 是 Codex 用于筛选历史对话的 provider ID。本项目固定使用 `custom`：

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
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.exe ChatGPTApiOnly.cs
```

Provider 同步的隔离测试入口只在定义 `PROVIDER_SYNC_TEST` 时编译：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /define:PROVIDER_SYNC_TEST /target:winexe `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.test.exe ChatGPTApiOnly.cs
```

测试必须通过 `CHATGPT_API_ONLY_CONFIG_DIR` 指向隔离 fixture，禁止对真实 Codex 目录运行测试入口。

自动回归测试会自行创建临时 fixture 并设置上述环境变量，覆盖成功更新、无变更幂等、数据库失败回滚、备份、保存配置不触发同步、大量历史对话内存占用，以及修复期间的界面响应和分阶段进度：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /define:PROVIDER_SYNC_TEST /target:exe /main:ProviderSyncTests `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:System.Web.Extensions.dll `
  /out:ChatGPTApiOnly.test.exe ChatGPTApiOnly.cs tests\ProviderSyncTests.cs
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& .\ChatGPTApiOnly.test.exe
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
```

界面回归测试会短暂打开使用 `example` 配置的测试窗口，自动关闭本测试线程的结果提示框，并把应用窗口截图保存在输出的临时目录。

## 上游与许可证

对话 Provider metadata 同步的数据范围、备份与事务策略参考了 [Codex++](https://github.com/BigPizzaV3/CodexPlusPlus) 的实现，并针对本项目的单文件 .NET Framework 启动器重新实现。

本项目采用 [GNU Affero General Public License v3.0](LICENSE)，SPDX 标识为 `AGPL-3.0-only`。
