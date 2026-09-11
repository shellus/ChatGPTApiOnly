# AGENTS.md

## 项目边界

- 项目是单文件 .NET Framework WinForms 启动器，主源码为 `ChatGPTApiOnly.cs`。
- 保持启动器无需额外运行库、安装器或 PowerShell 脚本即可运行。
- 不添加启动器级单实例锁。重复启动行为应继续交给官方桌面应用处理。
- 保存配置与启动客户端分开；启动器不得因打开设置或保存配置而停止已运行的 ChatGPT，保存并启动时启动客户端后启动器自动退出。
- Electron 外壳的 OpenAI 云端域名阻断不能影响内置 Codex app-server 对自定义 API 的访问。

## 配置与 Provider

- `model_provider` 是历史对话使用的 provider ID；自定义 API 模式固定为 `custom`，官方账号模式使用内置 `openai`。
- 表单中的“提供者名称”只对应 `[model_providers.custom].name`，不得作为 provider ID 写入历史数据。
- 模式切换更新 `config.toml`、`auth.json` 与本机 `launcher-profiles/modes.json`；切换 Tab 不落盘，保存或切换模式不得隐式修复历史对话。
- 官方模式由官方客户端登录及刷新 OAuth 凭证，启动时不得携带域名阻断或自定义 API 环境变量。仅凭本地凭证存在不得声称账号有效。
- 官方独立代理偏好保存在模式文件，同时同步到 Codex `.env` 的 `CHATGPT API ONLY PROXY` 标记区块供 CLI 读取；桌面启动参数和子进程环境注入保持不变。不得修改 Windows 系统代理或用户环境变量；自定义模式或禁用代理时只移除管理区块，保留原有 `.env` 内容。回归覆盖地址校验、父进程环境不变、幂等、禁用、模式保留、异常标记预检及包含 `.env` 的四文件保存失败回滚。
- 切换前保留当前凭证及各模式模型设置，官方退出登录后不得恢复旧令牌。多文件保存失败必须尝试恢复原状态并阻止启动。
- 认证类型由 `auth.json` 决定，不写入 `forced_login_method`；存在显式限制时提示冲突。`cli_auth_credentials_store` 默认不写，已有 `file` 保留，其他显式存储方式提示冲突。
- 官方模式将完整 `model_providers` TOML 片段保存到 `launcher-profiles/modes.json` 的 `model_providers_toml`，从活动配置移除；自定义模式恢复片段后更新表单字段。保留未知字段、其他提供者及嵌套表，重复保存不得清空快照。
- 数据格式变更只做一次性迁移，不在程序中保留旧版本兼容分支或升级测试。
- 不静默删除 profile 或路由覆盖配置；发现与目标模式冲突时，在停止客户端及落盘前提示并阻止切换。
- 历史对话修复只能由“修复对话”按钮显式触发。进度必须来自真实工作量，并以临时 `n/total` 进度区展示。
- 修改 rollout 或 SQLite 前必须创建备份；SQLite 更新使用事务，失败时恢复已改写的 rollout 并阻止误报成功。

## 安全与测试

- 不得把真实 API Key、API 地址、账号、本机路径或 Codex 配置提交到仓库。
- README 截图或动画只能捕获应用窗口，并使用隔离 fixture 与明显的 `example` 占位值；不得包含桌面背景、终端、用户名或真实配置。
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
