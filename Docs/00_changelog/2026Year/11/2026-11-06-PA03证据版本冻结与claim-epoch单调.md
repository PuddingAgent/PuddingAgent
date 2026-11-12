---
title: PA-03 证据版本冻结校验与 claim epoch 单调
author: hyfree
date: 2026-11-06
last_reviewed: 2026-11-06
status: archived
description: "历史 exit 0 仅在报告声明的 objective/acceptance/artifact 版本与 Job key 一致时才作证据；过期 Running 回收要求严格更大的 owner epoch；新增 3 条测试。"
categories: [docs, changelog]
tags: [review, evidence, version, epoch, pa03]
related_docs: [Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md]
related_files: [Source/PuddingPlatform/Services/Reviews/ReviewEvidenceReader.cs, Source/PuddingPlatform/Services/Reviews/ReviewJobStore.cs, Source/PuddingPlatformTests/Services/ReviewEvidenceReaderTests.cs, Source/PuddingPlatformTests/Services/ReviewJobStoreTests.cs]
slug: changelog-pa03-evidence-version-epoch-20261106
draft: false
---

# PA-03 证据版本冻结校验与 claim epoch 单调

落实裁定 §5.3（历史 exit 0 不得用于新产物）与 §5.4（claim epoch 单调校验）；未 commit、未部署、未调用真实模型。

## 1 证据版本冻结

- `ReviewEvidenceReader` 对每条 finished 报告校验其自声明版本：
  - `objectiveVersion` == `ReviewLogicalKey.ObjectiveVersion`
  - `acceptanceContractVersion` == `ReviewLogicalKey.AcceptanceContractVersion`
  - `artifactSetHash` == `ReviewLogicalKey.ArtifactSetHash`
- 未声明这些字段的报告维持原有可用性；**声明了但与 Job 冻结版本不一致 → 不作为证据**，写入 `evidence-version-mismatch:<criterion>:<field>` 到 MissingReasons，并保持 `EvidenceComplete=false`。
- 不可解析报告 → `evidence-version-mismatch:<criterion>:report-unreadable`，不渲染、不伪造正文。

## 2 claim epoch 单调

- `ReviewJobStore.ClaimAsync`：回收过期 Running 时要求 `ownerEpoch > 当前 OwnerEpoch`（相等也拒绝 `OwnerStale`），防止同一 epoch 重复占用；Pending 首次认领不受影响。
- 与既有 Worker 的毫秒时间戳 epoch 兼容（新 Worker 天然更大）。

## 3 测试与验证

- 新增 3 条：版本不一致报告不作证据、版本一致报告可用、过期回收要求严格更大 epoch。
- `ReviewEvidenceReaderTests`（8）+ `ReviewJobStoreTests`（9）+ `ReviewJobWorkerTests`（7）**24/24**。
- Platform S5 切片 **115/115**；全解决方案构建 exit 0；一键脚本 RunId 20261106-r13 exit 0（组件 274 + Platform 115 + 前端 47 + tsc）。
- 证据：`.work/pa03/evidence-version-epoch/`、`.work/verification/version-epoch-solution/`、`.work/pa08/components-20261106-r13/`。

## 4 未完成

- 过期 Running 若已有派发 attempt，仍缺“先核账（PA-01 ledger）再重试”的联动；claim epoch 单调已具备但派发关联未接。
- runtime binder、流式/子代理预算、Goal 自动续跑、08B/08C（NotRun）保持原状。
