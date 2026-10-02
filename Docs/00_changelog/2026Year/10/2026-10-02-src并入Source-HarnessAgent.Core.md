---
title: "2026-10-02 `src/` 并入 `Source/`（HarnessAgent.Core 归位）"
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 按用户 2026-10-02 裁定（第 4 条）：
categories: [docs, changelog]
tags: [harnessagent, core, 并入]
related_docs: []
related_files: [Source/HarnessAgent.Core/HarnessAgent.Core.csproj, Source/PuddingHost/PuddingHost.csproj, Tests/HarnessAgent.Cli/HarnessAgent.Cli.csproj, Tests/HarnessAgent.Core.Tests/HarnessAgent.Core.Tests.csproj, code_map.md, Source/PuddingHost/code_map.md, Docs/14_reports/Core-VS-Restore-Fix-2026-10-01.md, Docs/14_reports/模块清单-module_map-2026-09-18.md, Docs/10_conventions/组件化交付规程.md]
slug: changelog-2026-10-02-src并入source-harnessagent.core
draft: false
---

# 2026-10-02 `src/` 并入 `Source/`（HarnessAgent.Core 归位）

## 改了什么

按用户 2026-10-02 裁定（第 4 条）：

- `src/HarnessAgent/Core/` → `Source/HarnessAgent.Core/`（`git mv`，24 个跟踪文件全部为 R 重命名，无内容改动）；空的 `src/` 目录删除。
- 工程引用同步（3 个消费者 + 解决方案）：
  - `PuddingAgentNetwork.slnx`：`src/HarnessAgent/Core/HarnessAgent.Core.csproj` → `Source/HarnessAgent.Core/HarnessAgent.Core.csproj`
  - `Source/PuddingHost/PuddingHost.csproj`：`..\..\src\HarnessAgent\Core\...` → `..\HarnessAgent.Core\HarnessAgent.Core.csproj`
  - `Tests/HarnessAgent.Cli/HarnessAgent.Cli.csproj`、`Tests/HarnessAgent.Core.Tests/HarnessAgent.Core.Tests.csproj`：`..\..\src\HarnessAgent\Core\...` → `..\..\Source\HarnessAgent.Core\HarnessAgent.Core.csproj`
- 索引同步：`code_map.md` §2 组件表与 §2.5 缺口清单路径改写；`Source/PuddingHost/code_map.md` 中飞书 WS 底座路径 `../../src/HarnessAgent/Core/...` → `../HarnessAgent.Core/...`。

## 验证

- `dotnet build Source/HarnessAgent.Core/HarnessAgent.Core.csproj --nologo` → **0 错误**（3 个既有 CA1416 警告）。
- `dotnet build Tests/HarnessAgent.Core.Tests/HarnessAgent.Core.Tests.csproj --nologo` → **0 错误**（1 个既有 MSTEST0001 警告），证明新 ProjectReference 相对路径可解析。
- `dotnet build Source/PuddingHost/PuddingHost.csproj --nologo` → **0 错误**（140 个既有警告），证明唯一生产消费者的引用已生效。
- 未运行测试套件：本次为纯路径迁移，无行为改动；上述三个编译门禁已覆盖重命名面。

## 未完成

- `Source/PuddingHost/code_map.md` 中飞书 WS 底座的旧路径仍在文件中：该文件当时混有**他方未提交**的历史记录迁出改动，为遵守「只提交自己的改动」，本次撤回该文件的一行修改，待他方提交后用 `../../src/HarnessAgent/Core/…` → `../HarnessAgent.Core/…` 一行修复补上（不影响编译，仅索引路径失真）。
- `Tests/HarnessAgent.Cli/` 仍未登记进 `PuddingAgentNetwork.slnx`（既有缺口，本次不扩大范围）。
- 历史报告与检索基线（`Docs/14_reports/Core-VS-Restore-Fix-2026-10-01.md`、`Docs/14_reports/模块清单-module_map-2026-09-18.md`、`Source/PuddingRetrievalEval/eval/reports/*` 等）仍记录旧路径 `src/HarnessAgent/Core`；按历史快照原则不改写。

## 关联

- 用户裁定：2026-10-02（本会话「根目录整理」第 4 条）
- 组件交付规程：`Docs/10_conventions/组件化交付规程.md`
