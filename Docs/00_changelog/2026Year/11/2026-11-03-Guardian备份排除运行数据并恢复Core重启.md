---
title: 2026-11-03 Guardian 备份排除运行数据并恢复 Core 重启
author: Codex
date: 2026-11-03
last_reviewed: 2026-11-03
status: archived
description: 修复安装目录内运行输出使部署前快照校验失败的问题，外部更新 Guardian 并验证 Core 成功部署。
categories: [docs, changelog]
tags: [guardian, deployment, runtime-data, regression]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md]
related_files: [Source/PuddingDesktop.Guardian/GuardianStore.cs, Source/PuddingDesktop.GuardianTests/GuardianTests.cs, Source/PuddingDesktop.Guardian/code_map.md, Source/PuddingDesktop.GuardianTests/code_map.md, code_map.md]
slug: changelog-2026-11-03-guardian-runtime-data-restart
draft: false
---

# 2026-11-03 Guardian 备份排除运行数据并恢复 Core 重启

## 故障证据

请求 `31c2b739b6af4b568665bd5b20db5227` 已于 11:59:41 结束为 `failed / InvalidDataException`，并非仍在编译。候选包已成功发布，状态已有 `preparedDirectory`、无 `Pending`，新建备份目录为空且没有清单，旧 Core PID 56056 未停止。该执行位置将异常定位到 `Capture` 中复制前的原包哈希复核。

旧快照枚举包含 Core 根下仍在写入的 `data/logs`、`temp` 和 `.pudding/context-tool-results`。枚举后文件变化会使复核失败；隔离回归用例已确定性复现。旧 Guardian 未记录异常文件名，因此不能认定某一具体历史文件就是当次触发者。现场闲时的只读复核曾通过，不将其描述成现场复现。

## 改动

- `Source/PuddingDesktop.Guardian/GuardianStore.cs`：按 Core 根目录排除 `data/`、`temp/`、`.pudding/context-tool-results/`；Desktop 整包使用其随包 Core 的相对路径。枚举、校验、复制、删除共用边界，旧清单中的运行文件也不覆盖或删除。
- `Source/PuddingDesktop.GuardianTests/GuardianTests.cs`：新增 5 个测试实例，覆盖两种 target 的活动日志和旧清单，确认 `default-data/`、`wwwroot/data/`、`.pudding/skills/` 仍在产品快照中且程序集篡改仍失败。
- 两个子项目 `code_map.md`、根 `code_map.md` 和 [运行手册](../../../13_runbooks/Desktop-Guardian.md)：维护数据边界与外部 Guardian 更新流程。

## 验证

- Guardian `dotnet publish` 成功，产物隔离在 `.work/guardian-restart/20261103-b45a2bb/guardian-fixed/`。
- 修复前新增用例：1 通过、4 因运行文件哈希失败。修复后独立执行全部现有 Fact/Theory 方法及 InlineData：27 通过、0 失败，包含 HTTP 认证与回滚状态机。测试断言没有绕过或修改。
- 标准 VSTest 未能执行用例：testhost 访问父进程时出现 Win32Exception(5)。独立启动临时反射运行器后执行上述 27 个实例；此结果不等于标准 `dotnet test` 已通过。证据位于该任务 `.work` 下的 `runner-before-workspace.log`、`runner-detached.log` 和 `test-detached.err`。
- 更新前用修复程序集只读复核当前安装：1,610 个产品文件通过哈希校验，Desktop/Core 健康。
- 外部备份并更新 Guardian（PID 51796 → 31628），Desktop PID 8884 保留。复用原请求成功编译的 Core 候选包提交请求 `73b58c2280f046d28e931c5e060b5961`；新 Core PID 44004，父进程 8884。
- 12:19:33 Guardian 完成稳定观察：`healthy`、`busy=false`、`pending=false`、`error=null`；Desktop `WorkbenchReady`、Core `Ready`、`/health/ready` HTTP 200。
- 已部署 1,610 个候选文件全部哈希一致，原有会话日志仍保留。实际安装的 `PuddingRuntime.dll` 通过只读反射调用生成 `[turn-time 2026-11-03 12:19:56 +08:00]`，runtime tail 不含 `Date:`。这是安装程序集行为验证，不冒充真实 LLM 会话验收。
- 本次两个 Docs 文件的 Front Matter 检查通过；`git diff --check` 通过。全 Docs 链接检查扫描 1,886 个文件，发现 29 个既有失效目标、0 个失效锚点，本次修改文档无失效项，未扩大范围修复其他文档。

## 影响、回滚与未完成部分

本次只修复部署数据边界，未迁移运行输出目录、未修改数据库或用户配置。Guardian 原文件备份在 `.work/guardian-restart/20261103-b45a2bb/guardian-original/`；恢复 Guardian 必须由外部控制器停止对应进程后执行。Core 原包由 Guardian 保留为 fallback，可使用认证 rollback 命令。未运行全产品故障注入脚本；标准测试宿主权限问题、运行输出迁往 DataRoot 的治理另行处理。未 commit、未 push。
