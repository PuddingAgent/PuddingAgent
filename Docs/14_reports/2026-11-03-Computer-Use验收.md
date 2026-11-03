---
title: 2026-11-03 Computer Use 验收
author: Codex
date: 2026-11-03
last_reviewed: 2026-11-03
status: active
description: 物理坐标与输入组件、Host 装配和真实 WebView2 双屏输入的验收证据与限制。
categories: [docs, reports]
tags: [computer-use, chromium, dpi, validation]
related_docs: [Docs/13_runbooks/Computer-Use.md]
related_files: [Source/PuddingComputer.WindowsTests/DesktopInputTests.cs, Source/PuddingComputer.AutomationTests/ComputerSnapshotAnnotatorTests.cs, Source/PuddingComputer.AutomationTests/ComputerCoordinateMapperTests.cs, Tests/PuddingHost.Tests/Tools/ComputerUseCompositionTests.cs, TestScripts/test-computer-use.ps1, TestScripts/ComputerUseSmoke/Program.cs]
slug: reports-2026-11-03-computer-use
draft: false
---

# 2026-11-03 Computer Use 验收

源码基线 `5e7c407`，当前改动未提交、未推送。运行证据保存在 `.work/computer-use/20261103-5e7c407/`；交互式复现入口是 [test-computer-use.ps1](../../TestScripts/test-computer-use.ps1)，工具操作契约见 [运行手册](../13_runbooks/Computer-Use.md)。

## 发现与修改

- 原标注器把屏幕全局中心放入 `ImagePoint`，下游映射器又加 `SourceRect` 原点。原点为零时掩盖错误；现在按源区域与输出图尺寸逆变换后签发图内点，并测试标注器 → 映射器的真实组合。
- 库的模块初始化曾改变整个宿主 DPI。现在截图、枚举和输入使用同步线程 Per-Monitor V2 作用域；加载库无进程 DPI 副作用。
- `computer_input` 改用独立 Windows 输入组件；前台、原生焦点、光标读回、落点归属和输入数量进入回执。`computer_observe` 返回同帧原图/标注图与受限寿命候选 ID。
- 实测剪贴板回读可能遇到其他程序短时占锁；只重试取得剪贴板锁，不重发粘贴，内容不一致时拒绝派发。

## 构建与自动测试

| 验证 | 实际结果 | 证据目录/文件（相对本次运行目录） |
|---|---|---|
| Windows 组件测试 | 48 passed，0 failed，0 skipped | `test-results/windows-final/*.trx` |
| Automation 纯规则测试 | 55 passed，0 failed，0 skipped | `test-results/automation-final/*.trx` |
| Host 装配 | 3 passed，0 failed | `test-results/host-probe-final/*.trx` |
| Host 构建 | 0 errors；存在原有警告 | `host-build.log` |
| Desktop + 随包 Core 发布 | exit 0；包含 Windows/Automation/OpenCV 原生库 | `desktop-publish-final.log` |
| 文档链接 | 29 个既有失效目标、0 个失效锚点；新运行手册无失效引用 | `doc-links.log` |

本环境 VSTest 子进程监听父进程时出现 Win32 error 5，未把它计作测试成功。两个组件使用 MSTest 自带 Microsoft.Testing.Platform 可执行文件运行。Host 的新增装配测试及两条既有装配测试通过隔离的 MSTest 包装器调用原测试方法和断言；未修改既有测试以绕过断言。包装器使用独立临时 DataRoot，不读生产模型密钥、不启动生产数据库。

关键测试覆盖：x64/x86 INPUT 布局、Chromium 顶层原生焦点且无 Win32 caret、错误焦点/落点、光标夹取、移动后遮挡、部分派发与释放、不重复派发、派发后窗口丢失、时间/几何/DPI/进程身份过期、非有限值与舍入越界，以及 Host observe/input 共享同一个会话实例。

## 真实 Chromium 验收

使用独立 WinForms + WebView2 窗口，每个显示器执行 15 个断言，共 **30/30**。所有点击、按键和填值走生产 `ComputerInputTool` / `WindowsDesktopInput`；JavaScript 只读取测试页面的焦点、输入值和按钮计数，不参与操作。

| 显示器物理范围 | 目标窗口 DPI | 验收 |
|---|---|---|
| `(0,0,2560,1440)` | 96 | passed |
| `(2560,0,2560,1440)` | 120 | passed |

确认了缩小到最长边 512 的图像点仍能落到输入框、`document.activeElement.id` 为编辑框、Unicode `Pudding中文42` 与剪贴板 `粘贴Pudding` 读回完全一致、OpenCV 候选 ID 点击使按钮计数实际改变。错误调用者、重复使用观察 ID、窗口移动后的旧观察均被拒绝。副屏 DPI 实测 120，不能按“整机 DPI 96”处理。

最终脚本回执为 `CHROMIUM_SMOKE_PASS`。证据：`final-chromium-smoke/smoke.stdout.log`、`smoke.stderr.log` 与其 artifacts 下的 `smoke-output` 原图/标注图。负坐标与裁图缩放由纯规则测试验证；本机真实双屏没有负坐标显示器，不声称做了实屏负坐标验收。

## 抖音客户端的未验证部分

实际目标仍为 HWND 4331090、PID 45292。外部探针完整性 RID 为 4096（Low），会话 ID 1；读取目标进程令牌返回 error 5，目标完整性级别未知。目标处于最小化状态，`foregroundRoot` 虽然等于目标，但 `focus=0`；普通恢复未取得原生焦点，显式 AttachThreadInput 返回 error 5。没有继续盲发文字。

这些证据只说明此次外部验证受到窗口状态/权限边界限制。不能证明此前所有 SendInput 失效的根因，也不能证明抖音过滤注入，更不能得出必须安装驱动的结论。后续需要在合适的交互桌面和权限上下文里，由新加载的产品工具继续验证实际搜索框。

## 范围限制

OpenCV 输出仅为候选轮廓，未接 OCR/YOLO/VLM，也没有声称候选具备可点击语义。局部像素比较不能证明页面整体未变，输入后仍需新观察；未实现滚轮、拖拽、驱动注入、跨进程的桌面控制租约或任务级自动验证。主模型继续负责语义选择和效果判断。

## 补充验证与部署（2026-11-03）

线上 schema 复核发现原输入工具使用的 Description 标记未被 ToolDescriptorFactory 采纳，改为 ToolParam；Host 装配断言增加参数说明的实际可见性，`test-results/host-schema` 再次 3/3 通过。

将交互式夹具请求改为 schema 的 snake_case 后，复测主屏通过，副屏中文和粘贴通过，但最后的候选 ID 点击返回 `cursor_did_not_land`、实际按钮输入派发 0/2，脚本以失败结束。未修改断言，也未把它计作通过。该回执证明光标落点校验阻止了点击，尚未定位光标变化原因；不能将早先 30/30 表述为每轮都稳定通过。证据在 `schema-chromium-smoke/`。用户正在开展新的桌面诊断，本轮不继续争用鼠标重跑。

Guardian 首次部署请求 `f62f09d2f5d444c1839000c6970df53f` 被拒：部署前 Desktop/Core 已停止，当前版本不满足健康基线要求；没有 pending 事务、没有替换程序。通过 start 请求恢复健康后重新部署。

最终 schema 修订部署请求 `a24679a592ed40c2b1f37bcee655face` 达到 `healthy`，`busy=false`、`pending=false`、`error=null`。Desktop PID **86788**、Core PID **120232**，分别为 WorkbenchReady/Ready。前一版部署 PID 为 68652/72388。已核对安装目录七项关键程序集/原生库哈希与候选一致，工具目录返回两个新工具 Available，input 参数说明已进入线上 schema。Core `/health/ready` 为 200；前端 index 与部署前备份一致。证据：`deployment-evidence-final.json`。

以上是外部控制器部署与工具装配验收；没有在生产 Agent 中伪称完成抖音搜索框输入 smoke。用户随后提供的 Medium→Medium 测试报告与本报告 Low integrity 外部探针必须分开解释。
