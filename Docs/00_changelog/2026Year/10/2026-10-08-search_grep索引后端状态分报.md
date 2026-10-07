---
title: 2026-10-08 search_grep 索引后端区分「未建索引」与「引擎故障」
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "search_grep 的 backend=index 原先把「scope 尚未建索引」与「引擎故障」都压成 contract_error 的一句话，会把排障方向引向「参数写错了」，也会让「尚未搜索」被读成「代码里没有」。现按 HasIndex 分开：未建索引报 dependency_wait（不计失败账本、不触发熔断），引擎故障保持 contract_error。"
categories: [docs, changelog]
tags: [search_grep, fulltext-index, status-contract, dependency_wait]
related_docs: [Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs, Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs, Source/PuddingRuntime/code_map.md]
slug: changelog-search-grep-index-backend-status-2026-10-08
draft: false
---

# 2026-10-08 search_grep 索引后端区分「未建索引」与「引擎故障」

> 范围：`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs`、`Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs`、`Source/PuddingRuntime/code_map.md`

## 问题（改动前）

`backend=index` 在引擎返回 `Success=false` 时一律回一句
`Index backend unavailable for scope '<path>': <err>`，且 `status=contract_error`。
两种截然不同的情形被压成同一句话：

1. **scope 尚未建索引** —— 环境前置未就绪。报 `contract_error` 把排障方向带向「我的参数写错了」，而提示里同时写着 "The scope must be indexed first"，错误状态与自身提示互相矛盾。更糟的是，这种情况下**什么都没搜**，却和「搜索过、确实没有」共用同一副面孔。
2. **scope 已索引但引擎故障** —— 真正需要排查的运行时故障。

平台里 `dependency_wait` 早有明确语义：`ToolInvocationService` 不据此记 runtime fuse、`FailedToolCallTracker` 豁免失败账本。它正是「外部前置未就绪、不是调用方错误」的既有承载。

## 改动

`SearchGrepTool.IndexBackendSearchAsync`：`!engineResult.Success` 时先问 `_searchEngine.HasIndex(scopeDirectory)`。

| 情形 | telemetry outcome | 消息要点 | status |
|---|---|---|---|
| scope 未建索引 | `not_indexed` | `has no index yet - nothing was searched, so this is NOT evidence that the pattern is absent` + 建索引/回落扫描的下一步 | `dependency_wait` |
| 已索引但引擎失败 | `unavailable`（原值） | `The scope is indexed but the engine reported failure - this is not a parameter problem` | `contract_error`（原值） |

未改任何参数语义、未改成功路径、未改 `no_match` / `truncated` / `timeout` 语义；异常分支与超时分支未动。

## 验证（原始证据）

| 项 | 结果 |
|---|---|
| 聚焦 `FullyQualifiedName~SearchGrepToolTests` | 失败 0 / 通过 64 / 跳过 0 / 总计 64 |
| 全量 `PuddingRuntimeTests` | 失败 0 / 通过 1973 / 跳过 6 / 总计 1979（基线 1978，新增 1 例） |
| 变异取红（`!` 去掉 ⇒ `!HasIndex` 变 `HasIndex`） | **Failed: 2 / Passed: 62**，失败用例指名且**双向**：`Backend_Index_Fails_Closed_When_Scope_Is_Not_Indexed`（expected `dependency_wait` / actual `contract_error`）、`Backend_Index_Reports_Engine_Failure_Distinctly_From_Not_Indexed`（expected `contract_error` / actual `dependency_wait`） |
| 逐位复原 | 源码 SHA-256 `62E930C85CEC3A746578B243475A36F93065758E970ACE39F9317BE008E973BD`；复跑 64/64 绿；`git diff` 面与 patch 后一致（无变异残留） |

测试侧：`RecordingFullTextSearchEngine` 增加可选 `hasIndex`（默认 `true`，既有调用行为零变化）；「未建索引」用例改为断言 `dependency_wait` + 新消息（新消息含 "NOT evidence that the pattern is absent"）；新增「已索引但引擎故障」用例，断言 `contract_error` 且错误文本**不得**含 `has no index yet`。

证据文件：`temp/sgt-test-1.txt`（基线绿）、`temp/sgt-test-cycle.txt`（变异红）、`temp/sgt-mut-sub.txt`（失败用例指名）、`temp/sgt-restore-out.txt`（复原绿）、`temp/sgt-cycle.py`（变异/复原脚本，自带 anchor 命中数与 sha 打印）。

## 影响面与遗留

- 未建索引场景 `status` 由 `contract_error` 变为 `dependency_wait`：该场景从此不计入失败账本、不触发熔断——这正是本次修正的目的（环境前置未就绪 ≠ 调用失败）。
- 宿主未部署：本修复与既有 file_patch P0/P1 修复一样，目前只在源码与测试中生效，运行中的 Core 仍是旧 `PuddingRuntime.dll`。
