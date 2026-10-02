---
title: Desktop Shell 的 Debug 侧服务（切 Core 日志级别的可测地基）
author: hyfree
date: 2026-10-03
last_reviewed: 2026-10-03
status: archived
description: "用户要\"Desktop Shell 也留一个 Debug 按钮\"。按钮的 UI 依赖 MainWindow（他方正在改，见下）， 因此本轮先把它背后真正需要正确性的部分——与 Core 的 HTTP 往返——做成可测服务并验证。"
categories: [docs, changelog]
tags: [服务]
related_docs: []
related_files: [Source/PuddingDesktop.WpfArchive/Debug/DesktopLogLevelService.cs, Tests/PuddingDesktop.Tests/Debug/DesktopLogLevelServiceTests.cs, Source/PuddingDesktop/DesktopTrayIcon.cs, Source/PuddingDesktop/MainWindow.xaml.cs, Source/PuddingDesktop/PuddingDesktop.csproj, Source/PuddingDesktop.WpfArchive/Storage/CoreStorageManagementClient.cs]
slug: changelog-2026-10-03-shell侧debug服务
draft: false
---

# Desktop Shell 的 Debug 侧服务（切 Core 日志级别的可测地基）

## 做了什么

用户要"Desktop Shell 也留一个 Debug 按钮"。按钮的 UI 依赖 `MainWindow`（**他方正在改**，见下），
因此本轮先把它背后**真正需要正确性的部分**——与 Core 的 HTTP 往返——做成可测服务并验证。

| 文件 | 改动 |
|---|---|
| `Source/PuddingDesktop.WpfArchive/Debug/DesktopLogLevelService.cs`（新） | Shell 侧服务：`GetAsync` / `SetAsync` 调 Core 的 `/api/admin/diagnostics/log-level`，带 `ControlToken` 头（loopback 控制面，与既有存储管理客户端同一套做法）；`ToggleTarget(isVerbose)` 表达"Debug ⇄ Information 一键回退"的语义；非 2xx **抛异常**而不是返回假快照 |
| `Tests/PuddingDesktop.Tests/Debug/DesktopLogLevelServiceTests.cs`（新） | xunit 4 条：GET 带 ControlToken 且解析快照、PUT 的 JSON body 与路径、非 2xx 必须抛（不许"看起来成功"）、`ToggleTarget` 双向语义 |

**为什么放 `Debug/` 目录**：WinUI 工程用 `<Compile Include="../PuddingDesktop.WpfArchive/Debug/*.cs" …>`
链接该目录（`PuddingDesktop.csproj` 第 41 行），因此这个文件**自动属于 Shell 程序集**，
shell 侧代码可直接用（`internal`/同程序集），也不需要往 WPF 旧工程里加任何东西。

两个有意的设计选择：

1. **按调用传入 Core 地址与令牌**（而不是构造依赖）：Shell 里 Core 地址/令牌随 Core 生命周期变化，
   做成构造依赖会逼着本服务跟着 Core 重建，也容易持有过期令牌；
2. **失败抛异常、不吞**：按钮据此提示用户，避免"界面显示已开启、实际没生效"。

## 验证

- **Shell 构建 0 错误**（`dotnet build Source/PuddingDesktop/PuddingDesktop.csproj`，说明链接生效、文件确实进了 Shell）；
- **`PuddingDesktop.Tests` 构建 0 错误**；用 `dotnet vstest` 跑我的用例：**4/4 通过**；
- 过程中的两次自身失误（如实记录）：① 编译期发现 archive（WPF 工程）的隐式 using 不含 `System.Net.Http`，
   补了显式 using；② 我把测试文件错放到 `Source/PuddingDesktop.Tests/`（该目录不存在于任何工程），
   移到真正的 `Tests/PuddingDesktop.Tests/Debug/`；并且该工程用的是 **xunit**（不是 MSTest），已按 xunit 重写。

## 未完成（Shell 按钮的 UI 接线，两步，等他方 WIP 落定再做）

1. `Source/PuddingDesktop/DesktopTrayIcon.cs` 的托盘菜单加一项（现有菜单是 Win32 `AppendMenu`：
   `打开 Pudding` / `退出 Pudding`）——需要给它一个回调入参，**这会改到 `MainWindow.xaml.cs:109` 的构造点**；
2. `MainWindow.xaml.cs` 里用协调器已有的 Core 地址 + `GetDesktopControlTokenAsync` 构造
   `DesktopLogLevelService`，把回调接到 `ToggleTarget(current)`。

**为什么现在不动**：`Source/PuddingDesktop/MainWindow.xaml.cs`（与 `.xaml`）里有**另一方 AI 未提交的改动**
（运行中心内存口径）。按仓库纪律，动这两个文件必须只暂存自己的 hunk 并核对暂存内容不含他方代码；
在对方未落定前触碰它，风险大于收益。

## 未完成（整体）

- Shell 的 Debug 按钮 UI 接线（上面两步）；
- 日志统计与清理按钮（`logs` 各子目录占用/文件数/最早最晚 + 按周/月选择性清理）；
- 平台数据库按数据类占用（用户选 A：`dbstat`/页计数快速估算，**界面不标注「估算」**）。

## 关联

`dadbe52`（Core 侧接口）、`c4f750c`（前端 Debug 按钮）、
`Source/PuddingDesktop/PuddingDesktop.csproj`（链接 `Debug/*.cs` 的第 41 行）、
`Source/PuddingDesktop.WpfArchive/Storage/CoreStorageManagementClient.cs`（同一套 loopback + ControlToken 写法）。
