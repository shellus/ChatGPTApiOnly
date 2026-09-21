# ChatGPT API Only

管理 Codex / ChatGPT 桌面客户端与 Codex CLI 的多个官方账号、自定义 API 和独立代理。采用 Rust 共享核心、Tauri 桌面界面和独立命令行入口。

[下载发布版本](https://github.com/shellus/ChatGPTApiOnly/releases/latest) · [使用与 CLI 命令](docs/features/usage.md) · [架构和行为边界](docs/dev-spec/application-behavior.md)

## 支持范围

| 平台 | 入口 | 分发依赖 |
| --- | --- | --- |
| Windows x64 | Tauri GUI、CLI | GUI 使用 WebView2；安装包可在缺失时安装运行时，便携 EXE 要求已安装 WebView2 |
| macOS | Tauri GUI、CLI | GUI 使用系统 WKWebView；桌面目标是 Electron 版 Codex.app |
| Linux x64 | 独立 CLI | musl 静态构建，不依赖 Tauri、WebView、Node.js、.NET 或系统 SQLite |

原 WinForms 实现由 Rust/Tauri 替换。现有配置结构继续使用，无旧 GUI 兼容层。macOS 原生 ChatGPT.app 不等同于 Electron Codex.app，不能套用相同的域名阻断参数。

## 桌面使用

1. 打开应用，选择“官方账号”或“自定义 API”，添加或选择配置。
2. 填写并点击当前页的“保存配置”。保存不会启动或停止客户端。
3. 点击底部“启动”。未保存修改或外部配置变化会阻止启动，并保留草稿。

官方账号通过官方客户端登录。新增账号保存后启动即可登录；本地存在凭据不代表账号有效。官方模式可设置独立 HTTP 代理，自定义模式只阻断 Electron 外壳的指定云端域名，不阻断内置 app-server 访问自定义 API。

关闭按钮、标题栏和 Esc 共用草稿确认。只有显式“修复对话”才改写历史 provider ID，修复前创建备份。

![自定义 API 设置](docs/images/custom-mode.png)

## 开发与验证

需要 Rust stable、Node.js 24；Windows 构建需要 MSVC C++ 构建工具和 Windows SDK，macOS 构建需要 Xcode Command Line Tools。

```sh
npm ci
npm run build
cargo test -p launcher-core -p chatgpt-api-only --locked
cargo clippy -p launcher-core -p chatgpt-api-only --all-targets -- -D warnings
cargo build -p chatgpt-api-only --release --locked
```

Windows / macOS 桌面开发和打包：

```sh
npm run tauri -- dev
npm run tauri -- build
```

仅构建桌面可执行文件：`cargo build -p chatgpt-api-only-desktop --release --locked`，此前须完成 `npm run build`。Windows 产物为 `target/release/ChatGPTApiOnly.exe`，CLI 为 `target/release/chatgpt-api-only`（Windows 带 `.exe`）。

```sh
npx playwright install chromium
npm test
```

Windows 可运行 `node scripts/desktop-smoke.mjs`，以临时 example 配置验证正式程序的实际 IPC、保存、冲突和关闭行为。Rust 测试显式传入临时 Store；GUI 测试使用隔离 fixture，不修改真实用户认证。

CI 在 Windows、Linux、macOS 运行核心和 CLI 测试，构建 Windows/macOS GUI，并上传平台产物。行为验证与运行边界见[架构文档](docs/dev-spec/application-behavior.md)。

## 许可证

[GNU Affero General Public License v3.0](LICENSE)，SPDX：`AGPL-3.0-only`。历史 provider 修复的初始数据范围与备份思路参考 [Codex++](https://github.com/BigPizzaV3/CodexPlusPlus)。
