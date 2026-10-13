---
title: 2026-10-13 Desktop 旧 Core 路径修正与实际启动验证
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: archived
description: "已保存的显式 Core 路径覆盖新版随附 Core；备份并清除此覆盖后，用户实际数据目录下的 Desktop、Core 和能力通道正常启动。"
categories: [docs, changelog]
tags: [desktop, deployment, core, validation]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-13-Core能力通道预检与日志编码修复.md, Docs/08_how_debuge/11-案例-桌面与Core启动.md]
related_files: [Source/PuddingDesktop.WpfArchive/Core/CoreExecutableResolver.cs, Source/PuddingDesktop.WpfArchive/Configuration/DesktopBootstrapPathProvider.cs]
slug: changelog-desktop-configured-core-path-startup-2026-10-13
draft: false
---

# 2026-10-13 Desktop 旧 Core 路径修正与实际启动验证

## 原因

上一项修复的隔离 smoke 使用随附 Core，用户实际 `%LOCALAPPDATA%\Pudding\desktop.json` 却显式配置 `Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.exe`。该路径优先级高于随附 `core/`，其能力通道程序集仍是 2026-10-12 旧构建。真实 Desktop 诊断再次显示旧的能力端点预检失败、日志乱码和 CircuitOpen。

## 改动与部署

未新增源码改动。通过已有认证控制面停止 Core；备份 Desktop 配置为同目录的 `desktop.json.bak-20261013-bundled-core`，仅将 `coreExecutablePath` 清为 null，保留 DataRoot 与其余设置。核对进程路径后结束已停止 Core 的旧 Shell，再启动已完成隔离验收的 `temp/build/winui3/bin/PuddingDesktop/debug_win-x64/PuddingDesktop.exe`。

实际 Core 路径现为该 Desktop 随附的 `core/PuddingAgent.exe`，由 Desktop 子进程启动。没有复制或读取 LLM Secret，没有删除数据锁文件，也没有启动 dev-up。

## 验证

实际 DataRoot 为 `D:\Data`。诊断显示 `desktopState=WorkbenchReady`、`coreState=Ready`、`lastError=null`，能力通道完成 Connecting → Handshaking → Ready。Core PID 38988 的父进程为新版 Shell PID 59636，观察时连续运行 76 秒，超过原 60 秒熔断窗口。

`/health`、`/health/ready`、`/admin/` 均返回 HTTP 200；工作台 HTML 可获取；重定向中文日志可读。本次只修正运行配置，因此不重复此前已通过的 48 项能力通道及 334 项 Desktop 测试。新增日志 Front Matter 与站内链接检查通过。

## 未完成与回滚

本次证明实际配置下的启动、就绪和工作台加载，不代表真实模型功能 smoke 或全部产品功能验收。新版 Desktop/Core 保持运行供用户使用。需要回滚路径时先停止 Core 和 Shell，再恢复配置备份；恢复旧路径会重新加载尚未修复的旧构建，不能作为解决启动故障的方法。

## 关联

代码修复提交为 `a179fd3`，隔离测试结论见 [上一项修改日志](2026-10-13-Core能力通道预检与日志编码修复.md)。
