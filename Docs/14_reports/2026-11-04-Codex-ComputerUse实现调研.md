---
title: Codex 的 computer use 实现调研
author: 鲸鲸
date: 2026-11-04
last_reviewed: 2026-11-04
status: accepted
description: 拆解 Codex 桌面包：UIA 读树 + SendInput 注入 + WGC 截图，独立原生 helper 走 JSON-RPC，浏览器另走 CDP。
categories: [docs, reports]
tags: [computer-use, accessibility, chromium, research]
related_docs: [Docs/14_reports/2026-11-04-DeepSeekHarness-ComputerUse调研.md, Docs/14_reports/2026-11-03-抖音客户端输入通道实测.md]
related_files: []
slug: reports-codex-computer-use-2026-11-04
draft: false
---
# Codex（OpenAI）Computer Use 实现调研

- 调研时间：2026-11-04 00:20–01:05 (+08:00)
- 样本版本：`OpenAI.Codex_26.1027.3384.0_x64__2p2nqsd0c76g0`（WindowsApps MSIX 包）+ `codex` CLI（npm shim `C:\Users\hyfree\AppData\Roaming\npm\codex.ps1`）
- 方法：只读磁盘枚举 + 二进制字符串取证 + asar 目录表解析与定点切片读取；未安装、未构建、未启动 GUI 自动化
- 原始取证副本：`.work/codex-cu-survey/20261104-0020/raw/`（含 `sky-windows/`、`skills/oai_sky_lib/{windows,macos,linux}/SKILL.md`、`cua-docs/`、`asar-header.json`）

## 结论摘要

1. Codex 桌面端的 computer use 是**第一方原生栈**，不是"让视觉模型看截图自己点"：核心原语是 **UI Automation（UIA）树 + Windows.Graphics.Capture（WGC）截图 + SendInput 输入注入**。
2. 架构分三层：Electron 主进程（`app.asar` → `early-bootstrap-*.js`）→ JS SDK `@oai/sky`（`cua_node/bin/node_modules/@oai/sky`）→ **独立原生 helper 可执行**（`codex-computer-use.exe` 与 `codex-computer-use-swift.exe`）。
3. 桌面进程内模式走**命名管道 + 4 字节小端长度前缀的 JSON-RPC 2.0**（`SKY_CUA_NATIVE_PIPE=1`）；脱离宿主的模式则 **spawn 子进程**并用换行分隔 JSON 通信。
4. 动作解析是**优先元素级**（`element_index` → AX 元素的 click/set_value/perform_secondary_action），坐标点击是**降级路径**（仅在 AX 元素缺失时用窗口相对坐标 + `screenshotId`）。
5. 每个 app 都要过一次**授权弹窗**（`createElicitation` → `requestComputerUseApproval`，带 `riskLevel`、可 `persist: session/always`）；物理 Escape 是硬中断（写 `cache/computer-use/interrupts/<conv>/<turn>` 标记文件，后续调用直接 reject）。
6. 浏览器是完全不同的一条路：**CDP（Chrome DevTools Protocol）+ Playwright 定位器**，`Page.captureScreenshot` / `Input.dispatchMouseEvent` / `Accessibility.getFullAXTree`，不是桌面那套 UIA。
7. 截图"即使窗口被遮挡也能拿到"是论文级关键点：走 WGC（`GraphicsCaptureItem` + `Direct3D11CaptureFramePool`），不是 `BitBlt`/`PrintWindow`。
8. CLI 侧 `codex features list` 明确有 `computer_use  stable  true`。
9. 与 DeepSeek Harness 的根本差异：DSH 把 CU 做成**provider 抽象 + 外部 Cua Driver 二进制/SDK**（`cua-driver-native` / `cua-driver-mcp`），Codex 把 CU **内嵌进应用包**并自己实现 UIA/WGC/SendInput。
10. 未证实项见文末（主要是两个 helper 的选择逻辑、CLI 端是否也走同一条 pipe）。

---

## 一、包结构

包根：`C:\Program Files\WindowsApps\OpenAI.Codex_26.1027.3384.0_x64__2p2nqsd0c76g0`
- 全包：3665 个文件 / 2,477,853,542 字节（≈2.31 GiB）
- **ACL 可读**：本次对 WindowsApps 目录的读取**全部成功**，无一次拒绝访问（与预判相反，见 §七）

体量最大的可读件（`Get-ChildItem -Recurse` 实测）：

| 大小 | 路径（相对包根） | 性质 |
|---|---|---|
| 643.06 MB | `app\resources\app.asar` | Electron 全量前端 + 主进程 bundle |
| 351.88 MB | `app\resources\codex.exe` | Codex Core CLI（Rust 单体） |
| 318.12 MB | `app\chrome.dll` | Electron/Chromium |
| 303.28 MB | `app\resources\codex` | 无扩展名同族二进制 |
| 89.37 MB | `app\resources\cua_node\bin\node.exe` | 自带 Node 运行时（跑 node_repl/cua_repl） |
| 75.38 MB | `app\resources\codex-code-mode-host.exe` | 代码模式宿主 |
| 55.53 MB | `app\resources\codex-windows-sandbox-service.exe` | Windows 沙箱服务 |
| **53.92 MB** | `app\resources\cua_node\bin\node_modules\@oai\sky\bin\windows\swift\x64\codex-computer-use-swift.exe` | **CU 原生 helper（新版/Appshot）** |
| **1.51 MB** | `...\@oai\sky\bin\windows\codex-computer-use.exe` | **CU 原生 helper（默认）** |
| 18.15 MB | `...\@img\sharp-win32-x64\lib\libvips-42.dll` | 图像处理（sharp） |
| 3.89 MB | `...\browser-accessibility.wasm.br` | 浏览器 AX 树（wasm，三处副本） |
| 2.01 MB | `...\@oai\browser-desktop\scripts\browser-service.mjs` | 浏览器自动化服务 |
| 7.73 / 7.37 MB | `app\resources\plugin-signatures\openai-bundled\{chrome,browser}\plugin.tar.gz` | 浏览器插件签名包 |

`app\resources\` 一级目录：`accessibility / app.asar.unpacked / artifact-template-picker / cua_node / default_app / native / plugin-signatures / plugins / tectonic ...`

`cua_node\bin\node_modules\@oai\` 下只有 4 个包，全是 CU 相关：
`browser-desktop`、`cua`、`cua-repl`、`sky`。

`@oai/sky` 自带随包文档（这是最省事的架构说明书，已复制取证）：
`docs\sky-full-desktop-api.md`、`docs\sky-window-api.md`、`docs\sky-window2-api.md`、`docs\skills\oai_sky_lib\{windows,macos,linux}\SKILL.md`

`@oai/cua\docs\` 里的 REPL 架构文档：`tinysky-alt-core-{cua-repl,node-repl}-{windows,macos,linux,no-cua}.md`、`tinysky-alt-{audio,confirmations,other-browser-apis}.md`

---

## 二、computer use 实现证据

### 2.1 权威口径：官方随包 SKILL 直接写明系统 API

`app\resources\cua_node\bin\node_modules\@oai\sky\docs\skills\oai_sky_lib\windows\SKILL.md:14`
（取证副本 `.work/.../raw/skills/oai_sky_lib/windows/SKILL.md:14`）：

> Automates apps via SendInput and UI Automation, and takes screenshots of app windows via Windows.Graphics.Capture that works even if they are occluded.

同一文件 `:150`（Node REPL + Sky 用法）给出 `node_repl` + `@oai/sky` 的固定流程：
`list_apps()` → `get_window()` → `activate_window()` → `get_window_state({include_screenshot:true, include_text:true})` → `click/press_key/scroll/drag/set_value/perform_secondary_action` → 重新取 state。

同一文件 `:170-185` 的动作签名（决定性：**元素索引优先，坐标是备选**）：

```js
await sky.click({ window: targetWindow, element_index: 12 });
await sky.click({ window: targetWindow, screenshotId, x: 400, y: 300 });
await sky.set_value({ window: targetWindow, element_index: 12, value: "hello" });
```

`:203` 明确写 "Use window-relative screenshot coordinates **when accessibility elements are unavailable**"。

`SKILL.md:218` 还有一条 Windows 专属的产品化细节：
> If an input call reports that the point is over `StartMenuExperienceHost.exe` or another non-target window, call `sky.activate_window(...)`

→ 说明它在注入前会做**命中窗口校验**（点会不会落到别的窗口上），这是纯坐标方案容易踩的坑。

### 2.2 原生 helper 二进制取证（字符串直方图）

对 `...\@oai\sky\bin\windows\swift\x64\codex-computer-use-swift.exe`（56,534,320 字节）做 ASCII 扫描，命中计数：

| 符号 | 次数 | 含义 |
|---|---|---|
| `IUIAutomation` | 123 | UIA COM 接口 |
| `SendInput` | 7 | Win32 输入注入 |
| `Windows.Graphics.Capture` | 3 | WGC 截屏 |
| `GraphicsCaptureItem` | 12 | WGC 会话对象 |
| `Direct3D11CaptureFramePool` | 15 | WGC 帧池（D3D11 取帧） |
| `SetForegroundWindow` / `GetForegroundWindow` | 2 / 1 | 前台窗口管理 |
| `DwmGetWindowAttribute` | 2 | 取窗口真实边界（去阴影边框） |
| `CUIAutomation8` / `CUIAutomation` | 1 / 1 | UIA COM 类 ID（UIA2/UIA3） |
| `IUIAutomationElement` | 20 | 元素接口 |
| `IUIAutomationTreeWalker` | 4 | 树遍历（构建 AX tree） |
| `ElementFromHandle` | 3 | 由 HWND 取根元素 |
| `CreatePropertyCondition` | 2 | 属性条件查询 |
| `CGEvent` / `AXUIElement` | 0 / 0 | macOS 路径（本二进制不走） |
| `PrintWindow` / `BitBlt` | 0 / 0 | 旧式截屏（明确不用） |

同文件内的能力/协议字符串：
`windows_appshots_v1`、`windows_appshots_v2`、`windows_capture_border_access_v1`、`service_info`、`window_protocol_version`、`handle_appshot_hotkey_event`、`Codex Appshot process cache`、`SKY_ENABLE_AUDIO`、`computer_audio_v1`

对 `...\@oai\sky\bin\windows\codex-computer-use.exe`（1,544,448 字节，PA 前 1.51 MB）扫描，命中段原文（`[^\x20-\x7E]` 已替换为 `.`）：

方法名字表（同一二进制内连续出现，即是它实现的 RPC 方法全集）：
```
start_audio_recording stop_audio_recording get_window_state list_apps launch_app
list_windows get_window diagnostic_state activate_window perform_secondary_action
set_value click_element scroll_element click drag scroll type_text press_key
close end_turn
```

`SendInput` 命中段：
```
failed to install mouse hook ... src/input/interruption.rs ... failed to install mouse hook
... SetCursorPos initial overlay cursor position
SendInput sent ?. of ?. events; GetLastError=?.
```

`Windows.Graphics.Capture` 命中段：
```
cast drawing surface interop / begin overlay surface draw / end overlay surface draw
_cursor overlay rendering operation failed after likely device loss; recreating overlay window
Windows.Storage.Streams.DataReader / Windows.Graphics.Capture.GraphicsCaptureItem /
Windows.Graphics.Capture.Direct3D11CaptureFramePool / Windows.Imaging.SoftwareBitmap /
... windows-0.61.3/src/Windows/Win32/Networking/WinHttp/mod.rs
```

→ 三条硬结论：
1. 这个 helper 是 **Rust**（`src/input/interruption.rs`、`src/input/initial_cursor.rs`、`/usr/local/cargo/registry/.../windows-0.61.3`），用 **windows-rs 0.61.3** 绑 WinRT。
2. 它自己渲染一个**屏幕上可见的光标 overlay**（composition 图层），并装了**鼠标钩子**用于"用户打断"（interruption）判定。
3. 它把 WinHttp 也链进来了 → 说明同一进程内还有网络路径（音频/上传），与 `SKY_ENABLE_AUDIO`、`computer_audio_v1` 呼应。

`swift` 这个目录名是**产物代号不是语言**：同二进制里 `swift_` 前缀符号 1293 个但 `libswiftCore` / `_TtC`（Swift mangling 前缀）均为 0，`MSVCP140.dll`/`VCRUNTIME140.dll` 是 MSVC C++ 运行时 → 名称来源未证实，**不可**据此认定"Swift 写的"。

### 2.3 JS 层：@oai/sky 的传输实现

`...\@oai\sky\dist\project\cua\sky_js\src\targets\windows\internal\computer_use_client.js:1`（压缩单行，取证副本可读）：

- helper 路径解析（决定跑哪个二进制）：
  ```js
  function K(){return o({relative_path:["bin","windows","arm64"===process.arch
    ?"codex-computer-use-arm64.exe":"codex-computer-use.exe"], env_var_override:"OAI_SKY_WINDOWS_BIN"})}
  ```
- 双传输：
  - `if("1"===nodeRepl?.env?.SKY_CUA_NATIVE_PIPE)` → `new A(nodeRepl, timeoutMs)`（**宿主原生管道**，需要 `nodeRepl.nativePipe.createConnection` 与 `SKY_CUA_NATIVE_PIPE_DIRECTORY`，缺失则报 `Computer Use native pipe is unavailable`）
  - 否则 → `new WindowsHelperTransport({helperArgs,helperCommand,timeoutMs})`（**spawn 子进程**）
- JSON-RPC 2.0 + 长度前缀分帧（同一文件）：
  ```js
  const r=Buffer.from(JSON.stringify(e),"utf8");
  if(r.length>8388608) throw new Error(`Computer Use native pipe frame is too large: ${r.length}`);
  const i=Buffer.alloc(4+r.length); i.writeUInt32LE(r.length,0); r.copy(i,4); t.write(i);
  ```
  读侧上限 64 MB（`0x4000000`）。默认超时 10 s，`launch_app` 15 s。
- 服务端 → 客户端的反向请求：`"requestComputerUseApproval"`，由 `createElicitation` 弹窗应答（同文件 `W` 函数）。

`...\targets\windows\internal\helper_transport.js:1`（spawn 路径）：
- 子进程参数恒带 `--parent-pid <process.pid>`（父死子退）
- 请求预算头：`const P="x-oai-cua-request-budget-ms"`
- 物理打断文案常量（用户按 Escape 后喂给模型的强约束）：
  > Computer Use was stopped by the user with the physical Escape key. Stop your work, do not call further Computer Use tools in this turn...
- 打断落地为文件：`path.join("cache","computer-use","interrupts", conversationId, turnId)`（在 `CODEX_HOME` 下），存在即后续请求直接 reject
- 按 app 授权：弹窗文案 `` `Allow Codex to use ${t.displayName}?` ``，`riskLevel` 默认 `low`，`persist` 默认 `["session"]`（`allowPersistentApproval` 才给 `"always"`）；音频是特例（`"computer-audio"===t.app` 时禁用持久授权）

### 2.4 Electron 主进程侧（app.asar 内定点取证）

asar 头 6,132,976 字节；正则提表得 22,415 个条目。全文件流式扫描（8 MB 块 + 64 B 重叠）后按 (offset,size) 反查所属文件：

| 命中串 | 绝对偏移 | 所属文件 | 片段 |
|---|---|---|---|
| `SKY_CUA_NATIVE_PIPE_DIRECTORY` | 8,881,890 | `early-bootstrap-491-…js`（5,350,667 B） | 见下 |
| `windows_appshots_v2` / `windows-capture-native-bridge` | 13,013,559 | `early-bootstrap-491-…js` | 见下 |
| `codex-computer-use-swift` | 14,857,615 | `early-bootstrap-824-…js`（289,060 B） | 见下 |
| `cua_repl` | 19 处（含 8,805,762 / 15,089,504 / 173,793,059） | 多 bundle | — |

**(a) 命名管道宿主服务**（`early-bootstrap-491`，偏移约 8.88 MB 处的函数体）：
```js
... node_repl_host_services_socket_error / node_repl_host_services_peer_rejected ...
s.setMessageCallback(n=>{uU(n,e,r()?t:void 0,e=>{s.sendMessage(e)})}),
{pipePath:s.pipePath,dispose:()=>s.close()}
...
if("attachComputerAudio"===i.data.method){ ... n({id:i.data.id,jsonrpc:"2.0",result:{}}) }
let s=t.get(i.data.params.service);
```
→ Electron 主进程起了一个**带 peer 校验的命名管道 JSON-RPC 服务**，暴露 `service` 注册表 + `attachComputerAudio`。这正是 `SKY_CUA_NATIVE_PIPE` 的对应端。

**(b) 原生捕获桥 + 能力协商**（同文件，偏移约 13.01 MB）：
```js
eAZ = zod.object({ event: literal("appshotHotkey"), phase: enum(["ready","down","up"]) }),
eAX = zod.object({ capabilities: array(string).refine(e=>e.includes("windows_appshots_v2")),
                   protocolVersion: literal(1) }),
eA0 = ("windows-capture-native-bridge"),
eA1 = e=>({skipTurnTransition:!0, timeoutMs:e});
async function eA2(e){ const t=async(r=0)=>{ try{ eAX.parse(await e.request("service_info",{},eA1(500))); return } ... } }
```
→ 存在一个叫 **`windows-capture-native-bridge`** 的服务，必须自报 `capabilities` 含 `windows_appshots_v2` 且 `protocolVersion===1`，还推 `appshotHotkey` 事件（`ready/down/up`）。`service_info` 是握手方法。

**(c) 两个 helper 的执行路径**（`early-bootstrap-824`）：
```js
function C(e,t=process.arch){ let r=R(e,["bin","windows","arm64"===t?"codex-computer-use-arm64.exe":"codex-computer-use.exe"]);
  return w({component:"cua-helper",executablePath:r}),r }
function B(e){ let t=R(e,["bin","windows","swift","arm64"===process.arch?"arm64":"x64","codex-computer-use-swift.exe"]);
  return w({component:"cua-swift-helper",executablePath:t}),t }
function O(e){ return R(e,["dist","project","cua","sky_js","src","targets","windows","internal","helper_transport.js"]) }
```
→ 明确登记了两个 component：`cua-helper` 与 `cua-swift-helper`。**两者都在**，不是一个的旧备份（分别有独立 component 名与日志埋点）。

### 2.5 浏览器侧是完全独立的一条路（CDP）

`app\resources\plugins\openai-bundled\plugins\browser\scripts\browser-service.mjs`（1,612,413 字符 / 4607 行，压缩）关键串计数：`cdp` **385**、`playwright` **257**、`accessibility` **71**、`Page.captureScreenshot` 3、`Input.dispatchMouseEvent` 7、`Input.dispatchKeyEvent` 5、`DOM.getDocument` 1、`--remote-debugging-port` 0、`webSocketDebuggerUrl` 0（后两者为 0 说明端口/URL 不是硬编码在这里，由上层注入）。

原文片段（字符位置）：
- `char 811731`：
  ```js
  let r=await t.cdp.call(H(e.tab_id),"Page.captureScreenshot",
    {format:an,fromSurface:!0,optimizeForSpeed:!0,quality:80}, {preserveDebuggerOnTimeout:!0,timeoutMs:wee})
  ```
- `char 993748`：
  ```js
  await e.cua.dispatchMouseMove(t,o,0), await e.cdp.call(t,"Input.dispatchMouseEvent",
    {type:"mouseWheel",x:o.x,y:o.y,deltaX:i?u:0,deltaY:i?0:u})
  ```
- `char 1025712`：
  ```js
  let {nodes:h}=await this.cdp.callTarget(m.target,"Accessibility.getFullAXTree",{frameId:d.frameId})
  ```

`@oai/cua\docs\tinysky-alt-core-cua-repl-windows.md:99` 给出浏览器 provider 类型枚举：
```ts
type BrowserInfo = { id: string; name?: string; family?: string;
  type?: "iab" | "extension" | "cdp" | "mcpapps"; ... }
```
→ 浏览器有四类 provider（IAB / 浏览器扩展 / 裸 CDP / MCP apps），但**都不是**桌面那套 UIA+SendInput。

---

## 三、line 3 的判定（系统 API / 浏览器协议 / 视觉模型点坐标？）

| 目标 | 机制 | 证据 |
|---|---|---|
| 原生 Windows 应用 | **系统 API**：UIA 读树 + SendInput 注入 + WGC 截图 | `windows/SKILL.md:14`；swift helper 的 `IUIAutomation`×123 / `SendInput`×7 / `GraphicsCaptureItem`×12 |
| 浏览器标签页 | **浏览器协议**：CDP（+ Playwright 定位器） | `browser-service.mjs` 的 `Page.captureScreenshot` / `Input.dispatchMouseEvent` / `Accessibility.getFullAXTree` |
| 视觉模型 | **辅助角色**：截图回流给模型看布局与判读结果；坐标点击仅在 AX 元素缺失时使用，且必须带 `screenshotId` 防坐标失效 | `windows/SKILL.md:203`、`:170-185`；`@oai/cua` 文档 `ScreenshotInputOptions = { screenshotId?: string } // Windows only` |

**不是**"纯截图 + 视觉点坐标"方案：`element_index` 是首选通道，`perform_secondary_action` 走的是 UIA 的 action 名（文档明确 "It requires an action actually exposed for that element in the accessibility text. Do not guess action names."）。

**独立原生 helper 进程：有。** 两个独立可执行（`cua-helper` / `cua-swift-helper`），均是 out-of-process，通信为 JSON-RPC；桌面 App 内还额外有一条"不 spawn、走宿主命名管道"的 in-host 变体（`SKY_CUA_NATIVE_PIPE=1`）。

---

## 四、与 DeepSeek Harness 的差异

DeepSeek Harness（`F:\github\deepseek\deepseek-harness`）**有** computer use，但形态完全不同：**没有自己实现 UIA/WGC/SendInput**，是 provider 抽象 + 外部 Cua Driver 二进制。

证据：
- `packages/computer-use/computer-use`（纯 provider/契约层）；`packages/experimental/computer-use-cua-driver-native`、`...-cua-driver-mcp`（两个适配器）
- `package.json:237-239`：`@deepseek-ai/dsh-computer-use`、`@deepseek-ai/dsh-experimental-computer-use-cua-driver-mcp`、`@deepseek-ai/dsh-experimental-computer-use-cua-driver-native`
- `packages/experimental/computer-use-cua-driver-native/README.md:2`："Run Cua Driver computer-use tools from its **native npm SDK**, with durable screenshots and explicit host desktop permissions."
- 同文件 `:37`："It loads the exact **Cua Driver npm version** declared in package.json ... The registered provider name is `cua-driver-native`."
- 同文件 `:43`："The native dependency supplies **platform binaries through npm optional dependencies** ... The native runtime **shares the host process**, so native crashes can terminate that process. Use the installed MCP provider when the **separate Cua Driver application** should own permissions and execution."
- 元素定位语义用 `element_token`（`snapshots\session\computer-use-cua-driver-native\system-prompt.expected.md:32`）："Use element_token from that snapshot, or coordinates from its screenshot. A new snapshot of that window invalidates its earlier element tokens."

| 维度 | Codex 桌面 | DeepSeek Harness |
|---|---|---|
| 归属 | 第一方实现，随 MSIX 包分发 | 依赖第三方 Cua Driver（npm optional deps 提供的平台二进制） |
| 进程模型 | out-of-process helper（Rust）+ 可选宿主命名管道 | native SDK 与宿主**同进程**；或走独立 Cua Driver 应用的 MCP provider |
| 平台原语 | 自建 UIA + WGC + SendInput（+ 屏幕光标 overlay + 鼠标钩子打断） | 由 Cua Driver 决定（DSH 侧不实现） |
| 授权 | 每 app 一次 elicitation + `riskLevel` + 会话/永久持久化 | 依赖宿主应用的 OS 桌面权限（README 明言 "neither installs a permission-owning app nor changes OS grants"） |
| 浏览器 | 同一 `cua_repl` 内的 CDP/Playwright provider | 独立 `packages/browser-use`（另一个包，与 CU 解耦） |
| 打断 | 物理 Escape → 标记文件 → 硬拒后续调用 | 未在本次范围内取证（未证实） |

---

## 五、CLI feature flags（原始输出）

命令：`codex features list`（cwd=`D:\Data\workspaces\default`，exit=0，共 190 行）

与本问题相关的行（原文逐字，列宽固定 `name  status  enabled`）：

```
compaction_image_budget                  stable             true
computer_use                             stable             true
guardian_node_repl_transcript_images     under development  false
image_detail_original                    removed            false
image_generation                         stable             true
image_resize_notice                      under development  false
resize_all_images                        removed            true
unified_image_budget                     under development  false
view_image                               stable             true
```

- `computer_use` = **stable / true** → CLI 默认开启 CU。
- 无 `screenshot` / `vision` / `appshot` 独立 flag（190 行里不存在这三类名字）；CU 的能力协商在协议层（`service_info` → `capabilities` 含 `windows_appshots_v2`），不在 CLI feature flag 层。
- CLI shim：`C:\Users\hyfree\AppData\Roaming\npm\codex.ps1`；同一套 `cua_node` 资源目录同时被 `app\resources\codex.exe` 与 Electron 前端使用（`@oai/cua` 的 `tinysky-alt-core-node-repl-*.md` 同时提供 node_repl 与 cua_repl 两种变体文档）。

---

## 六、未证实项（不要当结论用）

1. **两个 helper 的运行期选择逻辑未证实**。已证实两者都被登记（`cua-helper` / `cua-swift-helper`，`early-bootstrap-824`），且 `windows-capture-native-bridge` 要求 `windows_appshots_v2` 能力；但"何时挑 swift、何时挑 rust"的判定代码未定位到单点（候选：默认 `OAI_SKY_WINDOWS_BIN` 覆盖 → `bin/windows/codex-computer-use.exe`；swift 仅在 `windows-capture-native-bridge` 握手成功时作为该服务实现）。**未证实**。
2. **CLI（`codex.exe`）是否也走同一条 `SKY_CUA_NATIVE_PIPE` 命名管道未证实**。已证实主进程实现了该管道服务、`computer_use` flag 为 true、`cua_node` 为共享资源；但未跟踪 CLI → helper 的实际 spawn 路径（需运行时抓进程树，本次约束下未做）。
3. **`swift` 命名来源未证实**（二进制内 Swift mangling 前缀为 0、MSVC 运行时在位）。
4. **音频（`SKY_ENABLE_AUDIO` / `computer_audio_v1` / `start_audio_recording`）的采集实现未展开**（只见到方法名与字符串，未读具体管道）。属于 CU 之外的并列能力。
5. **`@oai/sky` 的 `window2` API 与 `full desktop` API 的差异细节未逐条比对**（两份文档都在 `raw/`，可后续补）。
6. 未运行任何 CU 动作 → **运行时行为（真实 UIA 树形状、打断是否生效）均未实测**，本报告全部是静态取证。

## 七、读取失败 / 工具故障的原始报错

**包目录读取：无失败。** 预先担心的 WindowsApps ACL 拒绝**没有发生**：`Test-Path`、`Get-ChildItem -Recurse`（3665 文件）、`[IO.File]::ReadAllBytes`（56 MB 二进制）、asar 定点读取全部成功。（记录此点是因为预判与实测相反而非相反方向的错误结论。）

过程中出现的是**本机工具/脚本故障**，逐条留存（属"仪器坏了"，不是"包读不到"）：

1. cmd 方言误用（exit 255）：
   `[shell=cmd] 'Out-String' 不是内部或外部命令，也不是可运行的程序或批处理文件。`
   → 原因：在 `shell=cmd` 下用了 PowerShell cmdlet。
2. `node.exe` 执行 asar 解析脚本（exit 1）：
   `<anonymous_script>:1` 紧跟 asar 头部 JSON 原文，`original_chars=6134763`
   → 原因：内置 `node.exe` 对该调用形态把 header JSON 当脚本源码解析；改用 PowerShell 直读二进制修复。
3. PowerShell 5.1 无 `Latin1` 编码：
   `You cannot call a method on a null-valued expression.` @ `$s = [Text.Encoding]::Latin1.GetString(...)`
   → 改用 `[Text.Encoding]::GetEncoding(28591)`。
4. 函数实参被当表达式字符串：
   `InvalidArgument: Cannot convert value "8724623-150861" to type "System.Int32"`
   → 原因是把 `8724623-150861` 当位置参数传入；改为先算变量。
5. `Copy-Item -LiteralPath "<dir>\*"`：
   `Cannot find path '...\windows\*' because it does not exist.`（通配符不能用 `-LiteralPath`）→ 改 `-Path`。
6. `file_read` 相对路径未命中（`.work/.../helper_transport.js` 与绝对路径均报 not found）：因该文件实际位于 `...\internal\` 子目录，非读取故障。

---

## 附：一句话工程启示

Codex 的 CU 不是"模型看屏幕"，而是**给模型一副结构化的手眼**：AX 树提供"这是什么可点的东西（稳定 id + 命名动作）"，WGC 提供"现在长什么样"，SendInput 提供"怎么真的按下去"，而截图坐标只是三者失效时的兜底。这种分层（元素级 → 坐标级 → 视觉判读）值得作为本机同类实现的设计基线。
