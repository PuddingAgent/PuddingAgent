---
title: WinUI 3 骨架实施记录（2026-09-27）
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 原地重建 Source/PuddingDesktop，产品名仍为 PuddingDesktop.exe；原 WPF 移入 WpfArchive，仅保留迁移与回归基线。Foundation 先独立测试后登记解决方案。
categories: [docs, reports]
tags: [desktop, winui3, skeleton, reports]
related_docs: [Docs/14_reports/Desktop-Shell-Recovery-2026-09-29.md]
related_files: []
slug: reports-desktop-winui3-skeleton-2026-09-27
draft: false
---

# WinUI 3 骨架实施记录（2026-09-27）

> 历史记录：下列原生演示、脚本与进程内 Core 接入方向已被 2026-09-29 恢复裁定取代。原成果保存在 B；当前 master 的实现及验证见[恢复报告](Desktop-Shell-Recovery-2026-09-29.md)。

原地重建 `Source/PuddingDesktop`，产品名仍为 PuddingDesktop.exe；原 WPF 移入 WpfArchive，仅保留迁移与回归基线。Foundation 先独立测试后登记解决方案。

已实现角色导航、按角色隔离草稿、保留来源的五类文档标签、设置、离线运行中心、按预览配置目录隔离的单实例、双 WebView2 隔离验证。此阶段不加载 Core，也不使用生产数据。

默认浅色；根背景透明，使用真正 SystemBackdrop；设置支持 Mica、Mica Alt、Desktop Acrylic。高对比度使用系统纯色。Mica 是壁纸色调材质；实时磨砂使用 Acrylic，系统可能按透明效果/节能策略回退。依据：[系统背景](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)、[Mica](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)、[Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)。

验证：Foundation 14 项通过；旧 Desktop 247 项通过；全解决方案构建 0 错误（存在既有警告）；Release 构建/发布通过；真实 WinUI 自检通过角色草稿、来源、标签、主题、材质与配置、双 WebView2 页面及 Cookie 隔离。脚本：`TestScripts/test-pudding-winui-skeleton.ps1`、`start-pudding-winui-preview.ps1`。最后视觉检查被用户 Escape 中止，因此不宣称最终材质视觉验收、IME、多 DPI、托盘/崩溃恢复完成。

Core DLL 接入为下一切片；当前示例角色和文档不代表真实 Agent 执行。
