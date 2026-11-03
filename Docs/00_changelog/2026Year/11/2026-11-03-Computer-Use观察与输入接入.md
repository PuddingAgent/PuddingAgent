---
title: 2026-11-03 Computer Use 观察与输入接入
author: Codex
date: 2026-11-03
last_reviewed: 2026-11-03
status: archived
description: 接入窗口观察与有回执的 Windows 输入，修复图内坐标映射，并记录真实 Chromium 验证和 Desktop/Core 部署。
categories: [docs, changelog]
tags: [computer-use, windows, dpi, deployment]
related_docs: [Docs/13_runbooks/Computer-Use.md, Docs/14_reports/2026-11-03-Computer-Use验收.md, Docs/12_features/Computer-Use搜索框输入诊断与实现方案-2026-11-03.md]
related_files: [Source/PuddingComputer.Windows/ComputerDesktopSession.cs, Source/PuddingComputer.Windows/WindowsDesktopInput.cs, Source/PuddingComputer.Windows/Win32InputPlatform.cs, Source/PuddingHost/Tools/ComputerObserveTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Computer/ComputerInputTool.cs, TestScripts/test-computer-use.ps1]
slug: changelog-2026-11-03-computer-use-integration
draft: false
---

# 2026-11-03 Computer Use 观察与输入接入

新增 WindowsDesktopInput/Win32InputPlatform、ComputerDesktopSession 和候选检测器接口，Host `computer_observe` 与 Runtime `computer_input` 共享观察会话。修正 `ComputerSnapshotAnnotator` 将全局坐标误当图内坐标的问题；DPI 改为同步线程作用域。提供原图/编号图、元素 ID/纠偏点、Unicode/剪贴板输入、焦点/落点/派发数量回执，参数说明使用工具系统实际识别的 ToolParam。

Windows 48、Automation 55、Host 装配 3 项通过；Host 与 Desktop/Core 发布通过。真实 WebView2 在 96/120 DPI 两屏曾完成 30/30 断言；后续 snake_case 复测有一次副屏光标落点不一致，安全拒绝点击，测试失败如实保留，未删改断言。文档链接检查仍有 29 个既有失效目标，与新文档无关。

最终 Guardian 请求 `a24679a592ed40c2b1f37bcee655face` 为 healthy；Desktop/Core PID 86788/120232，关键文件哈希匹配，两个工具在线 Available。未 commit/push，未安装驱动，未修改前端版本或生产配置。

未完成：抖音客户端真实编辑/搜索效果、OCR/小视觉模型、驱动输入与跨进程桌面控制租约。抖音的焦点、菜单粘贴、按需无障碍与驱动分支另列待实现方案。

详情：[验收报告](../../../14_reports/2026-11-03-Computer-Use验收.md)、[运行手册](../../../13_runbooks/Computer-Use.md)、[搜索框方案](../../../12_features/Computer-Use搜索框输入诊断与实现方案-2026-11-03.md)。
