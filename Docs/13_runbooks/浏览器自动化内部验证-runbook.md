---
title: 浏览器自动化内部验证 runbook（改完代码按这个顺序自证）
author: hyfree
date: 2026-10-09
last_reviewed: 2026-10-09
description: "把本会话反复使用的自证顺序写成 runbook：① 文档门禁（总数口径）→ ② 链内 7 套件（交付链，产出清单）→ ③ **链外抽查**（改了共享契约时必须跑：按引用图找真正加载该程序集的套件）→ ④ 外部验收排练（生成观察模板 + 拒绝未执行的结论）。每条都给出可复制的命令、它证明什么、以及**它证明不了什么**，便于交接与复用。"
status: active
categories: [docs, runbooks]
tags: [browser, verification, runbook, b7]
related_docs: [Docs/13_runbooks/浏览器自动化外部smoke-runbook.md, Docs/14_reports/2026-10-09-浏览器自动化交付总览-handover89.md, Docs/00_changelog/2026Year/10/2026-10-08-链外抽查脚本.md]
related_files: [TestScripts/check-round-gates.ps1, TestScripts/delivery-manifest.ps1, TestScripts/check-dependent-suites.ps1, TestScripts/external-acceptance.ps1, TestScripts/commit-round.ps1]
slug: runbooks-browser-internal-verification
draft: false
---

# 浏览器自动化内部验证 runbook

**用途**：改完浏览器自动化相关代码后，按这个顺序自证。每一步都写明**证明什么**与**证明不了什么**。

> 纪律：**门禁非零退出时不得提交**（本会话在 round-91/92 各违反过一次，之后用
> `TestScripts/commit-round.ps1` 把顺序固定下来：先门禁 → 可选测试 → 才 `git add`/`commit`）。

## 步骤 1：文档门禁（总数口径）

```powershell
& .\TestScripts\check-round-gates.ps1          # 只看总数，不做文件名过滤
& .\TestScripts\check-round-gates.ps1 -FixLinks # 顺带自动修正**我的交付物**里的相对链接深度
```

- **证明**：Front Matter / 全仓库链接 / Docs BOM 三项都在棘轮基线内（总数打印出来）。
- **证明不了**：链接**语义**是否正确（"能解析"≠"引用对"）；基线之外的他方违规（本会话基线含他方 32 条失效链接，已交接）。

## 步骤 2：链内 7 套件（交付链，产出清单）

```powershell
& .\TestScripts\delivery-manifest.ps1 -BuildId "<id>" `
    -PublishDirectory "Source/Pudding.DeliveryManifest.Cli/bin/Debug/net10.0" `
    -Report "Docs/14_reports/<日期>-浏览器自动化交付清单-<id>.md"
```

- **证明**：7 个套件全绿、产物/未验证项计数、阶段判定；**有失败项就拒绝产出清单**（fail-closed）。
- **证明不了**：真机行为（清单里 20 条 `knownUnverified` 即此项）；也没证明**链外**套件。
- 可选：`-RetryFailedSuites 1`（默认关闭；重试成功会把 TRX 改名带 `-retried`，清单里能看见）——
  **代价**是削弱"第一次必须全绿"，是否接受由验收方决定。

## 步骤 3：链外抽查（**改了共享契约/公共程序集时必须跑**）

```powershell
& .\TestScripts\check-dependent-suites.ps1                       # 只列出"谁真的加载 Pudding.Contracts"
& .\TestScripts\check-dependent-suites.ps1 -Run                  # 直接跑这些套件
& .\TestScripts\check-dependent-suites.ps1 -Assembly <程序集名> -Run
```

- **证明**：按 `<ProjectReference>` 传递闭包选出的套件（不受文本匹配误导）。
- **证明不了**：依赖方是否真的执行到被改的代码路径（需人工看用例）。
- 已知状态（2026-10-08）：可达 `Pudding.Contracts` 的测试项目 **12** 个，其中 **5** 个在交付链之外；
  其中 3 个全绿、`PuddingWebApiTests` 有 2–3 条**运行间会变**的 goal/compaction 失败（无浏览器/契约引用，未归因、未修）。

## 步骤 4：外部验收排练（确认"未执行 ≠ 通过"仍成立）

```powershell
& .\TestScripts\external-acceptance.ps1 -Template -Facts temp/test-out/facts.json
# ……由**进程外**一方在真机上逐项观察并填写 facts……
& .\TestScripts\external-acceptance.ps1 -SkipTemplate -Conclude -Facts temp/test-out/facts.json
```

- **证明**：脚手架可生成（13 项 S-01…S-13 + 生命周期四步）；任何 `not-run` 都会让结论**被拒**（exit 2）。
- **证明不了**：任何真机行为 —— 本步骤在内部环境下**只能是排练**。
- 注意：`-Template` / `-Conclude` / `-SkipTemplate` 是**开关**，给开关传路径会静默落到位置参数上；观察文件是 `-Facts`。

## 步骤 5：提交

```powershell
& .\TestScripts\commit-round.ps1 -Message "<message>" -Paths <精确路径...> -TestPath <可选 csproj...>
```

- 先跑步骤 1 的门禁（非零即**拒绝提交**），再跑可选测试，最后精确 `git add` + `commit`（无暂存内容时拒绝空提交）。
- **证明不了**：改动是否**正确**。它只保证"没有把已知门禁/测试破坏掉"。

## 本 runbook 覆盖不到的部分（诚实登记）

- **真机与生命周期四步**：只能由进程外控制器判定（见 `Agents.md` 两段式验收）；
- **并发/压力**：本会话只有一次 8 线程冒烟，无压力与死锁分析；
- **他方子系统的失败**（如 `PuddingWebApiTests` 的 goal/compaction）：只做"是否可归因于我"的判断，不代改。

## 关联

- 外部一侧：[外部 smoke 运行手册](浏览器自动化外部smoke-runbook.md)
- 交付总览：[handover-89](../14_reports/2026-10-09-浏览器自动化交付总览-handover89.md)
