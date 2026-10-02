---
title: 2026-10-02 memory/ 与 skills/ 迁入 .pudding/ 并移出版本控制
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 按用户 2026-10-02 裁定（第 5 条「memory、skills 等目录放到 .pudding 目录，同时将 .pudding 目录写入 git 忽略文件」）：
categories: [docs, changelog]
tags: [迁入, 目录]
related_docs: []
related_files: [Docs/10_conventions/goal-md-snapshot-policy.md, Tools/Dev/dev-up.py, Source/PuddingCore/Configuration/PuddingDataPaths.cs]
slug: changelog-2026-10-02-memory与skills迁入pudding目录
draft: false
---

# 2026-10-02 memory/ 与 skills/ 迁入 .pudding/ 并移出版本控制

## 改了什么

按用户 2026-10-02 裁定（第 5 条「memory、skills 等目录放到 `.pudding` 目录，同时将 `.pudding` 目录写入 git 忽略文件」）：

- `memory/` → `.pudding/memory/`（54 个文件；其中 14 个原本被 git 跟踪，38 个原本被 `/memory/` 规则忽略）
- `skills/` → `.pudding/skills/`（19 个文件；原本全部被 git 跟踪）
- `agents/`（Agent 工作目录残留）在本次操作前已不存在，无需迁移；`.gitignore` 规则保留防重建
- `git rm -r --cached memory skills`：33 个跟踪文件退出索引（历史仍在 Git 里，工作树文件已在 `.pudding/` 下）

`.gitignore`：
- 第 23 行 `.pudding` → `/.pudding/`（显式目录规则 + 注释说明这是 Agent/Harness 本地状态）
- 末尾「Agent runtime data」块：保留 `/agents/`、`/memory/`，新增 `/skills/`，并注明 2026-10-02 迁移事实（防止工具在仓库根原地重建同名目录）

文档同步：
- `Docs/10_conventions/goal-md-snapshot-policy.md`：落点表「项目记忆」改为 `.pudding/memory/projects/**`；第 108 行补 2026-10-02 迁移说明（权威运行时记忆仍是 `D:\data\workspaces\default\memory\`）。

## 验证

- 迁移后根目录 `memory` / `skills` / `agents` 均 `Test-Path=False`；`.pudding/` 下 `memory`（54 文件）、`skills`（19 文件）存在。
- `git status -- memory skills` 显示 33 项删除已入索引，无残留未跟踪文件。
- `Tools/Dev/dev-up.py --clear` 的清理白名单（`tmp`、`.tmp`、`temp/build`、`temp/test-out`、`.tmp-build`、`.tmp-test-out`、`.codex-out`、`data/logs`）**不含** `.pudding`，迁入的记忆不会被 `--clear` 误删（已实读 `Tools/Dev/dev-up.py:150-164`）。
- 产品代码不读取仓库根 `skills/`：技能目录解析在 DataRoot（`Source/PuddingCore/Configuration/PuddingDataPaths.cs` 注释明确为 `agents/{agentInstanceId}/skills`）与内置 C# 技能（`Source/PuddingCore/Skills/BuiltIn/`），故本次迁移不影响产品技能加载。
- 未运行构建/测试：本改动只移动非编译输入的资料文件与忽略规则。

## 未完成

- `.pudding/skills/*/manifest.json` 内的 `"path": "skills/<id>"` 字段仍是旧相对路径（这些文件已移出版本控制，属本地资料）；如后续 Agent 依赖该字段定位，需一并改指 `.pudding/skills/<id>`。
- 仓库根 `data/`（dev-up 日志目录，`Tools/Dev/dev-up.py:37`）与 `checkpoint.json`（`YoloSignalService`/`FileTools` 的仓库根标记）仍在根，属运行时数据，未纳入本次迁移。
