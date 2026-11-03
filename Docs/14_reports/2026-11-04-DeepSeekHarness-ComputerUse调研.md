---
title: DeepSeek Harness 的 computer use 实现调研
author: 鲸鲸
date: 2026-11-04
last_reviewed: 2026-11-04
status: accepted
description: 只读调研 DeepSeek harness：观察走平台无障碍树、输入走系统事件定向投递，Windows 侧尚未实现。
categories: [docs, reports]
tags: [computer-use, accessibility, chromium, research]
related_docs: [Docs/14_reports/2026-11-03-抖音客户端输入通道实测.md]
related_files: []
slug: reports-deepseek-harness-computer-use-2026-11-04
draft: false
---
# DeepSeek Harness（DSH）的 computer use 实现方式

> 调研对象：`F:\github\deepseek\deepseek-harness`（本地只读快照，HEAD 文件时间 2026-10-24）
> 调研时间：2026-11-04 00:15–00:45（+08:00）。严格只读：未修改仓库、未安装依赖、未运行构建。
> 所有路径相对仓库根。

## 结论摘要（≤10 行）

1. **有**，但不在 `packages/computer-use`。那个包只做「唯一命名注册」（`ctx.computerUse`），零桌面操作方法。
2. 真正实现：`packages/experimental/cua`（TS 对象层 / 协议 / 授权） + `packages/experimental/cua-macos`（Swift 原生 helper，macOS 14+）。
3. **观察 = macOS 无障碍树（AXUIElement）** + ScreenCaptureKit 截图。定位靠 AX 元素索引，**不是**「截图喂视觉模型再猜坐标」。
4. **输入 = CoreGraphics CGEvent**，通过 SkyLight 私有符号 + 窗口字段戳记做**后台定向投递（不抢焦点）**，回退到公开 `CGEvent.postToPid`。
5. **Chromium/Electron = AX 启用补丁**：`AXManualAccessibility=YES`，不支持则回退 `AXEnhancedUserInterface=YES`；启用是异步的，必须轮询真实控件。
6. 架构是「**Node/TS 插件 ↔ stdin/stdout 上的 JSONL ↔ Swift 可执行**」，协议规范由 JSON Schema 从 TS 一侧生成。
7. **Windows 未实现**：非 darwin 直接抛错，npm `os:["darwin"]`，文档写「Windows support is in development … C# helper」。仓库内无任何 SendInput / UIA / FlaUI 代码。
8. 浏览器自动化是**另一条独立路线**（Playwright MCP / Chrome DevTools MCP / Stagehand），与桌面 CUA 明确分离——CUA 的 `getCapabilities` 显式声明 `browser: false`。
9. 还有一条「直接嵌上游」的替代线：`@trycua/cua-driver` **0.28.0** npm SDK（同进程）或连它的 `cua-driver mcp`。
10. 语义上所有动作都返回 `dispatched:true, verified:false`——**送出不等于生效**，必须重新观察验证。

---

## 一、观察方式

### 1.1 主通道：macOS Accessibility（AX）树

- `getState` 返回 `{appHandle, pid, windowId, focusedWindowId, snapshotId, tree, elements, windows, truncated}`；每个元素含 `index / role / title / value / description / identifier / bounds / actions / enabled`
  （`packages/experimental/cua-macos/native/README.md:52`）。
- 元素边界是**屏幕点**，而窗口输入用**窗口本地点**（同上）。
- `snapshotId` 是快照凭据：模型只能用「该快照」里的 `elementIndex`；同一 app/window 重读会作废旧索引，其它窗口快照不受影响
  （`native/README.md:70`；`packages/experimental/cua/README.md:20`）。
- 遍历入口：`AXAccess.project(identity:windowID:maxElements:maxDepth:...)`（`packages/experimental/cua-macos/native/Sources/CuaNative/AXAccess.swift:339`），
  元素/深度双上限（`AXAccess.swift:401`、`AXWorker.swift:292`），超限置 `truncated` 而不是静默截断。
- 失败语义：遍历中元素失效 → 整次读取 `STALE_SNAPSHOT`，**不发布半截快照**（`native/README.md:70`）。

### 1.2 截图：ScreenCaptureKit（不是「截图当成视觉定位」）

- `SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: false)`（`.../CuaNative/WindowCapture.swift:90`）
  然后 `SCScreenshotManager.captureImage(contentFilter:configuration:)`（`WindowCapture.swift:122`）→ PNG bytes。
- 返回 `{mimeType:"image/png", data, pixelWidth, pixelHeight, bounds, coordinateSpace:"window-points", windowId}`
  （`native/README.md` 方法表 `screenshot` 行；`packages/experimental/cua/src/protocol.ts` 的 `NativeScreenshotResult`）。
- Retina 下**像素维度 ≠ 窗口点维度**；模型的绝对坐标默认按「最近一次返回的截图像素」解释（`packages/experimental/cua/README.md:20` 附近）。
- 模型侧：截图进 attachment pipeline，模型只收到「坐标元数据 + 一张图」，base64 明文不进历史（`cua/README.md` Model Experience 段）。

### 1.3 权限与可用性探测（不弹系统框）

- `getCapabilities` → `{platform:"macos", permissions:{accessibility, screenRecording}, sessionLocked, methods, coordinateSpace:"screenshot-pixels", browser:false}`。
- 实现：`AXIsProcessTrusted()` / `CGPreflightScreenCaptureAccess()`（`.../CuaNative/AXWorker.swift:210`、`Permissions.swift:16`）。

### 1.4 结论：不是「视觉模型看截图点点」

截图被明确限定为**坐标参考**与**人工/模型交叉确认**；元素级定位、命中测试、动作名都来自 AX。绝对坐标还需要有效截图映射，窗口尺寸变化会 `WINDOW_CHANGED` / `SCREENSHOT_REQUIRED`。

---

## 二、输入方式

### 2.1 事件构造：CoreGraphics CGEvent

- 键盘：`KeyboardInput` 用 `CGEventSource(stateID: .hidSystemState)` + `CGEvent(keyboardEventSource:virtualKey:keyDown:)`
  （`.../CuaNative/KeyboardInput.swift:124-140`）。
- 鼠标：`WindowPointerInput` 构造 mouse down/up/drag/wheel（`WindowPointerInput.swift:223-290`），`postPair` 控制按下-释放间隔（`WindowPointerInput.swift:213`）。

### 2.2 投递：SkyLight 私有符号做「定向到进程/窗口」

这是整套实现最关键、也最脆的一环（`.../CuaNative/PIDEventPoster.swift`）：

- `dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_LAZY|RTLD_LOCAL)`（`PIDEventPoster.swift:20-24`）
- `dlsym` 取 `SLEventPostToPid`、`CGEventSetWindowLocation`、`SLEventSetIntegerValueField`（`PIDEventPoster.swift:25-40`）
- `post(event, to: pid)`：设时间戳 + `eventTargetUnixProcessID`，有私有符号走 `SLEventPostToPid`，否则回退公开 `event.postToPid(pid)`（`PIDEventPoster.swift:46-55`）
- `stampWindow(...)`：写窗口本地坐标，并把 `pid` 写进 field 40、`windowID` 写进 field 51/91/92（`PIDEventPoster.swift:57-72`）
  → 事件直达目标窗口，**不需要把窗口提到前台**
- 无私有符号时窗口路由直接报错 `NATIVE_ERROR`：`"Window-local input routing is unavailable on this macOS version"`

### 2.3 键盘语义

- 组合键：显式 modifier 按下/释放序列，**逆序释放**，只释放本次按下的键（`KeyboardInput.swift:34-79`）
- 文本：按 Unicode scalar 逐字发 down/up，走 `keyboardSetUnicodeString`（`KeyboardInput.swift:99-100`）；
  `\n`→Return、`\t`→Tab 用物理键（`KeyboardInput.swift:82-97`），事件间隔可配（`textEventInterval`）
- 剪贴板路径：`ClipboardPaste` + 走菜单栏 AX 找到 Paste 命令（`AXAccess.swift:183 pasteCommand`）

### 2.4 语义纪律

- 所有动作返回 `{dispatched:true, verified:false}`，文档反复强调「读新状态验证」，失败**不回滚已送达输入**，Host 不得自动重试（`native/README.md:53,58`）。
- hover 有两条路：AX（`AXShowAlternateUI`/`AXShowDefaultUI`）或系统指针（需前台校验，否则 `FOREGROUND_REQUIRED`），结果里带 `hoverRoute / systemPointerMoved`（`native/README.md:96`）。

---

## 三、Chromium 类桌面应用（Electron / WebView2）的焦点与输入

### 3.1 AX 树启用（核心手法）

```swift
// AXAccess.swift:333-334
var error = AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
if error == .attributeUnsupported { error = AXUIElementSetAttributeValue(app, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue) }
```

- 结果按进程身份缓存（`enabledProcesses`，`AXAccess.swift:26,331,336`），每个进程只启用一次。
- 这正是 Chromium 系（含 Electron）暴露无障碍树所需的开关：不设 `AXManualAccessibility` 时，Chromium 默认**惰性构建** AX 树。

### 3.2 启用是异步的 → 必须轮询真实控件

- `native/tests/electron_e2e.py:79`：`# AX enablement is asynchronous in Chromium; the controls themselves signal readiness`
- `electron_e2e.py:87`：拿不到 fixture 控件就 `raise AssertionError('Chromium accessibility did not expose the fixture controls: ' + state['tree'])`
- `native/README.md:122`：`AX polling waits for actual controls after Chromium accessibility activation`

### 3.3 焦点策略

- 默认**后台投递，不 activate**：初始化与每次动作前都不切前台（`cua-macos/README.md` Use this package 段）。
- 需要时用 `show()` 请求「取消隐藏/取消最小化，但不置前、不切 Space」，返回 `foregroundChanged`（比对 WindowServer front pid 与最前窗口），null 表示证据不可用。
- 后台送不进去时的兜底是**显式要求用户/模型切前台**：`app.activate()` 或窗口 `raise`，然后再观察——文档明确「必要时前台优先级高于保持焦点」。

### 3.4 已知能做到什么、做不到什么

- 实测覆盖：`native/tests/electron-fixture.mjs` + `electron-fixture.html`（渲染进程注入点击/输入观察）+ `electron_e2e.py`（含中文 `第二次 Electron` 全串 Unicode 输入与 `Command+A` 全选校验，`electron_e2e.py:114-117`）。
- **未做 CDP**：全文检索无 CDP/DevTools 协议驱动桌面窗口的代码；浏览器 CDP 走的是另一组包（见 §五）。
- 已知不可靠样本（文档自述，`cua-macos/README.md` Known Limitations）：Unicode 文本在 Blender 未出现；后台 `Command+A` 在 AppKit 应用未全选 ⇒ **sent ≠ received**。

---

## 四、依赖清单与运行环境

### 4.1 运行时与目标框架

| 层 | 技术 | 证据 |
|---|---|---|
| Host/包层 | Node + TypeScript ESM，cordis 插件/DI 框架 | `packages/experimental/cua/package.json`、`packages/computer-use/computer-use/src/index.ts:11-14` |
| 协议 | stdin/stdout 上的**逐行 JSON**（JSONL）请求/应答，一条连接同一时刻一个在途请求 | `packages/experimental/cua/src/native-client.ts:1,145-175`；`native/README.md:36-40` |
| 原生 helper | **Swift 6**（swift-tools 6.0），`platforms: [.macOS(.v14)]`，产物 `dsh-cua-native` | `packages/experimental/cua-macos/native/Package.swift:1-13` |
| 系统框架 | AppKit、Accessibility(AX)、CoreGraphics、ScreenCaptureKit、ImageIO、UniformTypeIdentifiers | `.../CuaNative/*.swift` 头部；`native/README.md:12` |

### 4.2 npm 依赖（与 CUA 相关的全部）

| 包 | 版本 | 用途 | 证据 |
|---|---|---|---|
| `@trycua/cua-driver` | **0.28.0**（精确锁定） | 上游 Cua Driver 原生 SDK，同进程嵌入 | `packages/experimental/computer-use-cua-driver-native/package.json` dependencies |
| `zod` | `4.4.3` / `^4.4.3` | 请求/应答/能力校验 | `cua-macos/package.json`、`cua-driver-native/package.json` |
| `@deepseek-ai/cordis` | workspace | 插件与 DI 框架 | 同上 peerDependencies |

`@trycua/cua-driver` 通过 **npm optional dependencies** 分发各平台二进制（`computer-use-cua-driver-native/README.md` Host requirements 段）。

### 4.3 系统私有/SPI 依赖（重要风险面）

| 符号 / 接口 | 来源 | 用途 |
|---|---|---|
| `SLEventPostToPid` | SkyLight 私有框架 | 定向到 PID 投递事件 |
| `CGEventSetWindowLocation` | SkyLight | 事件窗口本地坐标 |
| `SLEventSetIntegerValueField` | SkyLight | 写 window/pid 字段（40/51/91/92） |
| `_AXUIElementGetWindow` | HIServices 私有 | AX 元素 → CGWindowID |
| `responsibility_get_pid_responsible_for_pid` | libSystem 私有 | TCC 责任进程归属（签名与 Chromium 同款） |

证据：`PIDEventPoster.swift:20-40`、`AXAccess.swift:26-34`、`native/README.md:82`。

### 4.4 上游来源与许可

- `packages/experimental/cua-macos/native/upstream.json`：`trycua/cua`，tag **`cua-driver-rs-v0.28.0`**，commit `1b50c02e2d34734f64d2d22f54eb76cc97b4a663`，MIT。
- **改编而非拷贝**：Rust `libs/cua-driver/rust/crates/platform-macos/src/{input/keyboard.rs,input/mouse.rs,input/skylight.rs,ax/tree.rs,windows.rs,apps/mod.rs}` → 对应 Swift 文件（`upstream.json` 的 `adaptedFiles`，`copiedFiles: []`）。
- `references`：`steipete/Peekaboo` commit `d581001…`，**仅用于核对 C 函数签名**，未拷贝实现。
- `packages/experimental/cua-macos/native/LICENSE.cua-driver` 保留 MIT 声明。

### 4.5 明确「没有」的依赖（对 Windows 方案重要）

`FlaUI`、`uiautomation`、`UIAutomationClient`、`IUIAutomation`、`SendInput`、`mouse_event`、`keybd_event`、`robotjs`、`nut.js`/`@nut-tree`、`pyautogui`、`Playwright`（在 CUA 内）、`CDP`（在 CUA 内）**全部零命中**。
`playwright` 只出现在 `browser-use-playwright-mcp` 这条独立产品线里。

### 4.6 跨平台与 Windows 隔离

| 机制 | 证据 |
|---|---|
| npm 包声明 `"os": ["darwin"]` | `packages/experimental/cua-macos/package.json:93-94` |
| 构建期硬门禁：非 darwin 或非 arm64/x64 **直接抛错** | `packages/experimental/cua-macos/src/native-build.ts:20` — `'Computer use currently requires macOS on Apple silicon or Intel. Windows support is in development'` |
| helper 路径按平台+架构分目录 | `native-build.ts:41` — `native/bin/darwin-${architecture}/dsh-cua-native` |
| 源码构建走 `xcrun swift build --package-path …` | `native-build.ts:79` |
| Bundle 在非 macOS 上**跳过 Loader entry**，只保留共享逻辑与权限页 | `packages/experimental/cua-bundle/README.md`（Use this package 段末） |
| 远程浏览器可连 Mac Host | 同上 |

> 也就是说：**Windows 侧不是「用另一套 API 实现同样功能」，而是根本没有实现**。设计上预留了一个「独立 provider 包 + C# helper」的位置（`packages/experimental/cua/README.md` Known Limitations 第一条）。

---

## 五、整体架构（为什么这么拆）

```
模型 ── tools.cua ──┬─ @deepseek-ai/dsh-cua（对象 API/校验/授权/截图准入）
                    │      └─ NativeClient（JSONL over stdin/stdout，单飞、字节上限、可取消）
                    └─ provider（三选一，互斥注册）
                          ├─ dsh-experimental-cua-macos        Swift helper（AX + CGEvent）
                          ├─ …computer-use-cua-driver-native   @trycua/cua-driver 同进程
                          └─ …computer-use-cua-driver-mcp      已安装 cua-driver，走 MCP
```

- 互斥注册：`ComputerUseRegistry.register(name)` 占唯一槽，重复注册（哪怕同名）直接抛错（`packages/computer-use/computer-use/src/index.ts:16-42`）。
- 授权在 Host 侧：`getApp` 返回 executable 摘要 + 签名要求 → Host 走 User Questions 拿人工同意 → 才允许 launch/observe/input；未签名应用只能拿会话级授权（`native/README.md:58-60`、`cua/README.md`）。
- 浏览器与桌面分开：`getCapabilities` 里 `browser: false`；浏览器能力在 `packages/browser-use/*` 与 `packages/experimental/browser-use-*`（Playwright MCP、Chrome DevTools MCP、Stagehand），走 MCP/CDP 而不是 AX。

---

## 六、证据表

| # | 结论 | 文件:行 |
|---|---|---|
| 1 | 共享层只做唯一命名注册，无桌面方法 | `packages/computer-use/computer-use/src/index.ts:16-42` |
| 2 | 真正的 CUA 实现与平台 provider 在 experimental | `packages/computer-use/README.md:12` |
| 3 | 观察 = AX 树，元素含 index/role/title/value/bounds/actions/enabled | `packages/experimental/cua-macos/native/README.md:52` |
| 4 | 动作只报 dispatched，须重读状态验证 | `packages/experimental/cua-macos/native/README.md:53` |
| 5 | 快照/索引失效与窗口作用域规则 | `packages/experimental/cua-macos/native/README.md:70` |
| 6 | AX 遍历入口与元素上限 | `.../CuaNative/AXAccess.swift:339`、`AXAccess.swift:401` |
| 7 | **Chromium AX 启用（AXManualAccessibility → AXEnhancedUserInterface 回退）** | `.../CuaNative/AXAccess.swift:333-334` |
| 8 | AX 启用按进程缓存，只做一次 | `.../CuaNative/AXAccess.swift:26,331,336` |
| 9 | 截图先取 SCShareableContent | `.../CuaNative/WindowCapture.swift:90` |
| 10 | 截图用 SCScreenshotManager | `.../CuaNative/WindowCapture.swift:122` |
| 11 | SkyLight dlopen | `.../CuaNative/PIDEventPoster.swift:20-24` |
| 12 | 取 `SLEventPostToPid` / `CGEventSetWindowLocation` / `SLEventSetIntegerValueField` | `.../CuaNative/PIDEventPoster.swift:25-40` |
| 13 | 定向 PID 投递（含公开回退） | `.../CuaNative/PIDEventPoster.swift:46-55` |
| 14 | 窗口字段戳记（pid→40，windowId→51/91/92）+ 本地坐标 | `.../CuaNative/PIDEventPoster.swift:57-72` |
| 15 | 键序与 modifier 逆序释放 | `.../CuaNative/KeyboardInput.swift:34-79` |
| 16 | 文本按 Unicode scalar，走 keyboardSetUnicodeString | `.../CuaNative/KeyboardInput.swift:82,99-100` |
| 17 | 鼠标事件构造与成对投递 | `.../CuaNative/WindowPointerInput.swift:213,223-290` |
| 18 | 剪贴板粘贴走菜单栏 AX | `.../CuaNative/AXAccess.swift:183` |
| 19 | Chromium AX 启用异步，需轮询控件就绪 | `packages/experimental/cua-macos/native/tests/electron_e2e.py:79,87`；`native/README.md:122` |
| 20 | hover 双路由 + FOREGROUND_REQUIRED | `packages/experimental/cua-macos/native/README.md:96` |
| 21 | Swift 6 / macOS 14+ / 无第三方 Swift 依赖 | `packages/experimental/cua-macos/native/Package.swift:1-13` |
| 22 | 原生 helper 技术栈一句话 | `packages/experimental/cua-macos/native/README.md:12` |
| 23 | 上游 = trycua/cua tag cua-driver-rs-v0.28.0，改编清单 | `packages/experimental/cua-macos/native/upstream.json` |
| 24 | npm 依赖 `@trycua/cua-driver` 0.28.0 | `packages/experimental/computer-use-cua-driver-native/package.json` |
| 25 | `"os": ["darwin"]` | `packages/experimental/cua-macos/package.json:93-94` |
| 26 | 非 darwin 构建期抛错（含 "Windows support is in development"） | `packages/experimental/cua-macos/src/native-build.ts:20` |
| 27 | helper 按平台架构分目录 / xcrun swift build | `.../src/native-build.ts:41,79` |
| 28 | Bundle 在非 macOS 跳过 Loader entry | `packages/experimental/cua-bundle/README.md`（Use this package 段末） |
| 29 | 能力声明 `browser: false` | `.../CuaNative/AXWorker.swift:210`；`packages/experimental/cua/src/protocol.ts`（`CapabilitiesResult`） |
| 30 | 请求方法集（getState/click/typeText/pressKey/scroll/drag/screenshot…） | `packages/experimental/cua/src/protocol.ts`（`Request` discriminated union） |
| 31 | JSONL 传输、单飞、字节上限、取消 | `packages/experimental/cua/src/native-client.ts:1,145-175`；`native/README.md:36-40` |
| 32 | 模型侧工具名为 `cua` | `packages/experimental/cua/src/index.ts:294` |
| 33 | 协议由 TS 侧生成到 Swift/JSON Schema | `packages/experimental/cua-macos/native/generate-protocol.ts`；`native/README.md`（Request protocol 段末） |

---

## 七、未证实项（明确标注，勿当结论）

1. **Windows 是否已有未合并实现**——本地只有 master 快照，看不到分支；文档口径统一为「in development」。**未证实**。
2. **Chromium 内部 AX 树暴露粒度**（哪些 DOM 节点变成 AX 节点、是否走 AXPlatformNode）——仓库内没有 Chromium 源码，只有启用补丁与轮询逻辑。**未证实**。
3. **`@trycua/cua-driver` 0.28.0 的内部实现**（它自己是不是也用 SkyLight/私有 SPI，Windows 上用什么）——`node_modules` 未安装，按要求不装依赖，无法读。**未证实**。
4. **后台投递在各类 AppKit 应用上的稳定边界**——文档只给了两个失败样本（Blender 文本、AppKit 后台 Command+A），没有量化成功率。**未证实**。
5. **`AXEnhancedUserInterface` 对 WebView2 是否等价有效**——这是 macOS 属性，WebView2 是 Windows 控件；此问题在 macOS 侧无对应物，只能等 Windows provider。**无法在本仓库证实**。

---

## 八、零命中证据（Windows 侧）

检索方式与结果（探测工具本身做过正对照：同一工具能找到 `AXManualAccessibility`、`AXUIElement`、`CGEvent` 等已知存在项）：

| 检索 | 范围 | 结果 |
|---|---|---|
| `SendInput\|mouse_event\|keybd_event\|FlaUI\|UIAutomationClient\|WebView2\|IUIAutomation` | `packages/`，默认枚举上限 2000 文件（**coverage: partial**） | **无命中** |
| 同上（`file_ext=swift;ts;py;json;md`，exclude node_modules/.git/lib/dist/snapshots/.build） | 仓库根（扫描 1762/2000 文件，**coverage: partial**） | **无命中** |
| `SendInput`（`pattern=*.swift`） | `packages/experimental/cua-macos/native`（完整扫描） | **无命中** |
| `*.cs` 文件全仓枚举 | 仓库根 | 仅 3 个，全在 `apps/desktop/scripts/windows-runner-launcher.cs`、`apps/desktop/scripts/windows-token-public-key.cs`、`apps/desktop/tests/fixtures/windows-token-public-key-fake.cs`，**与 CUA 无关** |
| 目录枚举 `packages/experimental/cua*` | — | 只有 `cua`、`cua-bundle`、`cua-macos`；**无 `cua-windows`** |

> 说明：前两条因文件枚举上限标记为 `partial`，严格讲是「已扫描范围内零命中」；但结合「`*.cs` 只有 3 个无关文件」「无 cua-windows 目录」「`native-build.ts:20` 硬抛错」「文档三处口径一致」四条独立证据，判定 **Windows 桌面操作在本快照中未实现**。

---

## 九、对 PuddingAgent 的可借鉴点（附注，非本次结论范围）

- **AX 树优先、截图次之**：元素级定位比视觉定位便宜且可验证；截图只做坐标与视觉兜底。
- **`dispatched ≠ verified` 的结果契约**：所有输入类工具返回值恒定含 `verified:false`，把「验证」责任显式留给下一轮观察，这是很干净的边界划分。
- **Chromium 需要 `AXManualAccessibility`**：任何自研 UIA/AX 方案若拿不到 Electron 应用的控件树，先查这一条。
- **后台投递靠私有 SPI**：收益是不抢用户焦点，代价是 macOS 升级即可能失效；文档把「依赖私有符号」写进 Known Limitations 是正确姿势。
- **协议用 JSON Schema 双向生成**（TS ↔ Swift），避免手写两套结构漂移。
