---
title: 2026-10-19 runtime_identity 增加部署新鲜度判定
author: hyfree
date: 2026-10-19
last_reviewed: 2026-10-19
status: archived
description: "把「重启过 ≠ 重编过」从人肉推断变成机器可读字段：runtime_identity 比对产物写入时间与 HEAD 提交时间，读不到就报 unknown 而不是默认一致。"
categories: [docs, changelog]
tags: [diagnostics, deployment, evidence-discipline]
related_docs: [Docs/14_reports/2026-10-19-浏览器工具能力缺口清单.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs, Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs]
slug: changelog-runtime-identity-deployment-freshness-20261019
draft: false
---

# 2026-10-19 runtime_identity 增加部署新鲜度判定

## 动机：同一类误判在一个会话里犯了三次

| 我当时的推论 | 事实 | 结论 |
|---|---|---|
「进程 11:57 重启了，所以修复生效了」 | 部署 `PuddingHost.dll` mtime 恒为 09:50，修复提交在 10:25 | **重启 ≠ 重编** |
「`git checkout` 后源码 mtime 是新的，说明改动在」 | checkout 会刷新 mtime | **mtime ≠ 提交先后** |
「DLL 里有 `DesktopLocatorKindWire` 字样，说明修复进了」 | 类型名可能只是 TypeRef（被引用程序集的元数据） | **字符串命中 ≠ 定义在此** |

三类推论都指向同一个缺失：**没有一个字段回答「这份部署是不是在 HEAD 之后产出的」**。

## 改法

`agent_diagnostics(action=runtime_identity)` 增加 `freshness` 块：

```json
"freshness": {
  "repositoryRoot": "D:\\...\\PuddingAgent",
  "headSha": "167d23f...",
  "headCommittedAtUtc": "2026-10-19T06:24:00+00:00",
  "headSubject": "...",
  "verdict": "deployed-before-head",
  "reason": null,
  "assemblies": [
    { "role": "runtime", "name": "PuddingRuntime", "lastWriteTimeUtc": "2026-10-19T06:05:01...", "verdict": "deployed-before-head" }
  ]
}
```

规则：
- 仓库根从 `AppContext.BaseDirectory` 向上找 `.git`（≤16 层），找不到 → `repository-not-found`；
- HEAD 用 `git -C <root> log -1 --format=%H%n%cI%n%s`，3 秒超时；
- **1 秒容差**：写入时间严格早于 HEAD 提交时间 1 秒以上才算 `deployed-before-head`（同一秒视为新鲜，因为文件系统精度与签出顺序不可靠）；
- 读不到写入时间、git 不可用、超时、输出不可解析 → `unknown` + `reason`，**绝不默认「一致」**。

## 验证

- `AgentDiagnosticsFreshnessTests` **5 / 5 通过**（早于 / 晚于 / 同秒容差 / 不可解析 / 仓库根发现与不臆造）；
- **变异取红**：把判定改成恒 `at-or-after-head`，`AssemblyWrittenBeforeHeadIsReportedAsDeployedBeforeHead` 失败
  （`Expected: deployed-before-head, Actual: at-or-after-head`）；复原后 5 / 5 绿；
- 编译：`PuddingRuntime` 0 错误。

## 已知边界

- 只对**已加载程序集**判定；单个项目级的「源码是否比产物新」仍需 `git log -1 -- <path>` 另行判定；
- 部署目录必须位于某个 git 仓库内（当前 `temp/build/...` 布局满足）；裸部署到仓库外会退化为 `repository-not-found`；
- `agent_diagnostics(tool_stats)` 仍然不可用（活动记录无 `tool_name` 元数据，500 条样本全部缺失），这是下一个待修的观测缺口；
- 本变更需重新编译部署 Core 才在运行时可见。
