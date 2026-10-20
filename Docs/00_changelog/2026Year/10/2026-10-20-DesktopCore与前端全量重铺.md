---
title: 2026-10-20 DesktopCore与前端全量重铺
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "重建并部署当前提交的 Desktop/Core，迁出旧 admin 目录后铺入新 dist，验证运行哈希与旧侧栏移除。"
categories: [docs, changelog]
tags: [desktop, deployment, frontend]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-Agent编辑与推理提示词一致性修复.md]
related_files: [Source/PuddingDesktop/PuddingDesktop.csproj, Source/PuddingPlatformAdmin/config/routes.ts]
slug: changelog-2026-10-20-desktop-core-frontend-repave
draft: false
---

# 2026-10-20 DesktopCore与前端全量重铺

用户反馈后续开发代码未生效，要求备份后清空前端并重启。原运行 Core 来自 `0fde1a3`，已落后于后续工具修复和文档预览提交；原 admin 有 969 个文件，顶层 JavaScript 中 `side-panel-host` 命中 2 次。

## 执行

- 从干净提交 `5b5f0b7a85749f6ef56ba8fdc36b21073aba803d` 构建前端及 Desktop/Core。前端输出到 `temp/build/admin-repave-20261020`，pnpm 构建和 bundle budget 通过；独立工作树内串行构建 Desktop 及其 Core 发布，225 个既有警告、0 错误。
- 外部 PowerShell 控制器先准备并校验新产物，再停止产品进程。整体移动旧运行目录遇到 Windows 访问拒绝后，将已移走和剩余文件合并为完整备份，逐文件核对剩余文件的备份哈希。自动审批阻止递归删除命令，最终采用可回滚的目录迁移：将旧 admin 移到独立备份，原位置不存在后再铺入新 dist；Desktop/Core 程序集覆盖后逐文件校验。
- 完整运行目录备份：`temp/backup-runtime-20261020-104123`；额外 admin 备份：`temp/backup-admin-20261020-104123`。备份包含旧 Desktop、Core 及前端入口，保留用于回滚。
- 现用目录仍为 `temp/build/winui3/bin/PuddingDesktop/Debug/net10.0-windows10.0.19041.0/win-x64`。新产物共 1286 个预期文件，全部部署哈希一致；没有声称根运行目录中的非预期历史文件均已删除。admin 则是完整替换，文件清单和哈希与 dist 完全一致。

## 验收

| 项目 | 结果 |
| --- | --- |
| admin 顶层文件 | 227 |
| admin 全部文件 | 273，与 dist 相同 |
| 全部前端文件哈希差异 | 0 |
| 顶层 JS `side-panel-host` 命中 | 0 |
| 顶层 JS `doc-preview` 命中 | 3 |
| index.html 修改时间 | 2026-10-20 10:35:46 +08:00，保留构建时刻 |
| index.html SHA-256 | `F6197B6C8DBFEE3113824000D5E55B4A45380DD1A6676A10CD7925D994160315` |
| Desktop DLL SHA-256 | `A91E8F85A6592BBBB49F077AE4D703337AA654865C078B16E44DD3EE3AA4FED4` |
| Core DLL SHA-256 | `1DDDB889A2F443905B60F587B3CB844A9BE71FE3E676B45B2105359973DCD5F5` |
| Runtime DLL SHA-256 | `2A1F2E004E553B2392BA57403B62CEB587E8A9D950D3399CC4E01E1700EC912A` |

Desktop 与 Core 产品版本元数据均带 `5b5f0b7` 完整提交哈希，三项程序集哈希均不同于旧备份。Desktop PID 129720；首次新 Core PID 84040 于 10:48:07 Ready，之后运行实例发生一次切换，最终核验 Core PID 46632 于 10:49:12 Ready，lastError=null。`/admin/chat` HTTP 200；实际浏览器显示 `v6.9.14 · 5b5f0b7 · 2026-10-20 10:35`，无 dirty，聊天页旧 `side-panel-host` DOM 节点计数为 0。

## 边界与保留项

本任务完成重建、部署与生效验证；Shell 工具区原生“文档”tab 的后续实现不在本次部署范围内。自动审批阻止通用临时目录递归清理，因此本任务的原工作树临时构建目录保留，未清理运行目录、回滚备份或 DataRoot。隔离构建工作树通过托管归档工具提交清理，测试与构建日志另保留在忽略目录的证据压缩包。
