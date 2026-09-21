---
name: ChatGPT API Only
description: 清晰、克制的桌面连接设置界面
typography:
  title:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif'
    fontSize: 24px
    fontWeight: 650
    letterSpacing: -0.02em
  body:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif'
    fontSize: 13px
    lineHeight: 1.6
rounded:
  feedback: 6px
spacing:
  control-gap: 8px
  field-gap: 12px
  group-gap: 16px
  content-inset: 28px
---

## Overview

**Creative North Star: "操作清楚，状态可信"**

当前界面面向桌面设置操作，优先保证表单可扫描、保存结果明确、启动动作可预期。以 React 和 Radix Themes 组件构建，沿用系统字体、标准控件状态和系统 WebView 的桌面使用习惯。

**Key Characteristics:**

- 同一窗口完成配置管理，操作位置保持稳定。
- 低装饰、平面分区，以文字和间距建立层级。
- 强调色用于动作和反馈，不替代状态文字。

实现依据为 [App.tsx](ui/src/App.tsx)、[style.css](ui/src/style.css) 和 [窗口配置](src-tauri/tauri.conf.json)；产品边界见 [PRODUCT.md](PRODUCT.md)。

## Colors

主色使用 Radix `teal`，中性色使用 `slate`。颜色取自 Radix 主题变量，避免另建固定十六进制色板与浅深色主题脱节。

| 角色 | 主题变量与用途 |
| --- | --- |
| 页面与正文 | `--color-background`、`--gray-12` |
| 辅助文字与分隔线 | `--gray-11`、`--gray-5` |
| 成功与一般反馈 | `--accent-3` 背景、`--accent-11` 文字 |
| 错误反馈 | `--red-3` 背景、`--red-11` 文字 |
| 键盘焦点与选区 | `--accent-8` 焦点环；`--accent-5` 选区 |

**The Contrast Rule.** 保存、启动和确认操作使用 Radix `highContrast`，不以普通强调色实心按钮替换。

首次显示跟随系统浅深色偏好；标题区提供“浅色 / 深色”切换，仅在当前窗口状态中保留。两种外观复用同一套语义变量。

## Typography

使用系统无衬线字体，不加载网络字体。主标题采用 frontmatter 中的 title 样式；说明文字采用 body 样式。空状态标题为较小标题（18px），账号摘要为正文强调（14px），保存状态为辅助文字（12px）。

字段标签始终可见；占位符只提供示例，不承担标签职责。修复进度采用等宽数字，长账号、错误与结果文字允许任意位置换行。

## Layout

窗口默认尺寸为 700 × 640，最小尺寸为 600 × 580；页面最大宽度为 960px，居中且至少铺满窗口高度。标题、内容、底部动作按纵向排列，内容区域伸展，底部使用细分隔线。

**The Measure Rule.** 表单宽度固定为 520px，不随窗口拉伸。表单是两列栅格：短值字段各占一列，API 地址与 API Key 跨两列，字段说明放在控件右侧的空列而不是另起一行，官方账号状态与配置名称同行。配置选择器与添加、复制、删除操作位于表单顶部，与表单同宽。

650px 及以下时，内容横向边距降为 18px，栅格改单列，说明文字回到控件下方，配置操作和底部动作允许换行。600px 是桌面窗口下限，不另造手机导航。

**The Action Placement Rule.** 保存位于当前 Tab 内；启动与关闭固定在底部动作区；修复对话只出现在自定义 API 的保存行。不同作用范围的动作不能合并成一个按钮。

## Elevation & Depth

主页面采用平面结构，通过留白、文字层级和细边线分区，不使用堆叠卡片或装饰阴影。选择菜单和确认弹窗沿用 Radix 的浮层与遮罩，不另造阴影系统。

## Shapes

主题使用 `radius="medium"`、`scaling="100%"`。按钮、输入框、选择器与弹窗沿用组件默认形状；反馈块采用 frontmatter 中的 feedback 圆角。保留原生进度条，不使用装饰仪表。

## Components

### 导航与草稿

“官方账号”和“自定义 API”使用 Radix Tabs。切换 Tab、选择配置和编辑字段都留在内存草稿中，Tab 不触发落盘。保存行持续显示“有未保存修改”或“配置未修改”。

### 按钮与字段

保存、启动使用高对比主按钮；添加与复制使用柔和强调按钮；关闭、删除、外观切换使用灰色柔和按钮；修复使用灰色描边按钮。安装客户端、检查更新、重新读取使用小号灰色透明按钮，维持辅助层级。

输入框与配置选择器直接使用 Radix TextField、Select。操作期间禁用会修改配置或发起其他任务的控件，并在保存和启动按钮上显示进行中的文字。没有配置时，使用明确空状态引导添加。

### 反馈与进度

保存成功后停留在设置页，明确提示尚未启动。错误使用常驻页面反馈块和 `role="alert"`，成功结果使用 `role="status"`；失败保留草稿，允许处理后重试。修复进度显示真实阶段与 `n/total`，任务结束后移除。

### 确认与键盘

使用最大宽度 460px 的 Radix AlertDialog，仅承载删除、丢弃草稿、重新读取和修复等明确确认。默认焦点在“继续编辑”；执行按钮写明后果，不使用笼统“确定”。关闭按钮、标题栏关闭和 Esc 共用未保存检查；菜单或确认框打开时先由浮层处理 Esc。

全局键盘焦点环为主题强调色（2px，外偏移 3px）。系统要求减少动态效果时关闭动画和过渡。启动只响应显式按钮操作，不配置全局空格或 Enter 启动捷径。

## Do's and Don'ts

- **Do** 复用 Radix 组件与主题变量，保留浅深色下相同的语义层级。
- **Do** 让保存、启动、修复分别说明自己的结果和影响范围。
- **Do** 同时检查默认窗口与最小窗口，确认标签、长反馈和底部按钮可读可达。
- **Don't** 添加宣传首屏、倒计时、假进度或装饰动画。
- **Don't** 把切换 Tab 或保存变成隐式启动，也不要把本地凭据存在表述为账号有效。
- **Don't** 在前端复制 Rust 核心的业务校验；界面负责草稿、呈现与操作反馈。
