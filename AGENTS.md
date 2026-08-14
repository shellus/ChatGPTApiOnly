# AGENTS.md

## 项目边界

- 项目是单文件 .NET Framework WinForms 启动器，主源码为 `ChatGPTApiOnly.cs`。
- 保持启动器无需额外运行库、安装器或 PowerShell 脚本即可运行。
- 不添加启动器级单实例锁。重复启动行为应继续交给官方桌面应用处理。
- Electron 外壳的 OpenAI 云端域名阻断不能影响内置 Codex app-server 对自定义 API 的访问。

## 配置与 Provider

- `model_provider` 是历史对话使用的 provider ID，本项目固定为 `custom`。
- 表单中的“提供者名称”只对应 `[model_providers.custom].name`，不得作为 provider ID 写入历史数据。
- 保存配置只更新 `config.toml` 与 `auth.json`，不得隐式修复历史对话。
- 历史对话修复只能由“修复对话”按钮显式触发。进度必须来自真实工作量，并以临时 `n/total` 进度区展示。
- 修改 rollout 或 SQLite 前必须创建备份；SQLite 更新使用事务，失败时恢复已改写的 rollout 并阻止误报成功。

## 安全与测试

- 不得把真实 API Key、API 地址、账号、本机路径或 Codex 配置提交到仓库。
- Provider 同步测试必须设置 `CHATGPT_API_ONLY_CONFIG_DIR` 并指向隔离 fixture，禁止对真实 Codex 用户目录运行测试入口。
- 测试入口只能在定义 `PROVIDER_SYNC_TEST` 时编译，正式 EXE 不得包含该入口。
- 修改同步逻辑时至少验证：成功更新、无变更幂等、数据库失败回滚、备份存在，以及保存配置不会触发同步。

## 构建与发布产物

- 构建命令以 `README.md` 为准，正式产物为 `ChatGPTApiOnly.exe`。
- 源码行为改变后必须重新构建正式 EXE，并确认测试入口未进入正式二进制。
- 正式 EXE 与发布 ZIP 不进入 Git 跟踪，只能作为 GitHub Release 资产分发。
- 发布 ZIP 只包含正式 `ChatGPTApiOnly.exe`；发布前确认 ZIP 内 EXE 与本地正式 EXE 的 SHA-256 一致。
- Release tag 使用语义化版本；同一版本的 tag、标题、ZIP 文件名与发布说明必须一致。
- 提交前检查 `git status`、staged diff、敏感信息扫描结果和构建验证结果。
