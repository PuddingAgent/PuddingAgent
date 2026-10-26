---
title: Desktop Guardian 进程外验收
author: hyfree
date: 2026-10-26
last_reviewed: 2026-10-26
status: active
description: Guardian 独立门禁、WinUI 和 Host 组合测试，以及隔离产品构建、健康回滚和退出回收的实测记录。
categories: [docs, reports]
tags: [desktop, guardian, verification]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md, Docs/00_changelog/2026Year/10/2026-10-26-Desktop守护组件独立门禁.md]
related_files: [Source/PuddingDesktop.GuardianTests/GuardianTests.cs, Tests/PuddingDesktop.GuardianIntegrationTests/DesktopGuardianEndpointTests.cs, TestScripts/test-pudding-guardian.ps1]
slug: reports-desktop-guardian-2026-10-26
draft: false
---

# Desktop Guardian 进程外验收

## 自动化门禁

| 门禁 | 实测结果 |
|---|---|
| 独立 Guardian 构建 | 通过；0 警告、0 错误，无产品 ProjectReference |
| Guardian 独立 xUnit | 20/20：健康窗口、事务回滚/新增文件移除、中断恢复、坏备份拒绝、构建失败不停止、build-only、主动退出、请求去重/互斥、HTTP 认证/Origin/请求大小、PID 身份、三次恢复上限、快照保留和配置路径隔离 |
| Runtime `BootstrapRebootToolTests` | 24/24：原请求格式与新的 Guardian 端口/目标/请求 ID/无请求体令牌 |
| WinUI Guardian HTTP 集成 | 1/1：引用真实 WinUI 产物，验证 UI ping、令牌、协调退出和旧入口互斥，不实例化测试窗口 |
| Host 组合测试 | 4/4：`PuddingApplicationHostCompositionTests`、`BiyingFinanceToolsCompositionTests`、`DeepSeekWebToolsCompositionTests` |
| 完整 Desktop 发布 | 通过；产物含 Shell、Core、observer、guardian。存在已有编译警告 |

合计 49 项自动化测试通过。Guardian 独立覆盖率行 56.89%、分支 42.03%、方法 81.19%；进程运行器/构建器的真实副作用由下面的进程外 smoke 覆盖，未把 smoke 虚算进覆盖率。

## 进程外生命周期

所有操作从仓库外部 PowerShell 控制器发起，不使用 computer-use。每轮独立 DesktopHome、DataRoot、三个端口和随机控制令牌；不读取生产 LLM 凭据，不访问生产数据库。发布包位于 `temp/build/guardian-validation`。

第一轮完整 smoke：隔离目录 ID `5c8d508d056340b486692312973ac200`，`success=true`，8 项检查通过：

1. Guardian 自动启动，认证控制面及 Desktop/Core 稳定健康通过。
2. 通过 HTTP `action=build,target=core` 实际编译 Core，原 Desktop/Core PID 不变。
3. 通过 HTTP 部署编译产物，Core PID 替换、Desktop PID 保持。
4. 将候选 `PuddingAgent.dll` 改为无效 IL，健康验收失败后恢复旧文件 SHA-256。
5. 通过 HTTP `action=build,target=desktop` 实际编译 Shell 和随包 Core。
6. 将候选 `PuddingDesktop.exe` 改为无效可执行文件，恢复整个旧包；Guardian PID 始终为 24508。
7. 强制结束测试 Core，健康监控在进程外恢复它。
8. 显式停止 Desktop，所有记录的 Shell/Core PID 退出，DataRoot 文件租约可重新独占打开，等待后未自动复活。

第一轮 Shell PID：19484、8264、45920、5512；Core PID：47692、44052、35100、44096、24612。异常候选产生的早退 PID 也由监督器回收，以上为成功获得健康快照的进程。

第二轮使用最终 Guardian（含工作台完成初始化检查），执行 `-SkipBuilds`：隔离目录 ID `591aa4c9c63e4b8fbbadb0d182d54b72`，`success=true`，4 项检查通过：启动/健康、Core 意外退出恢复、Desktop 意外退出恢复、主动停止与租约释放。Guardian PID 50548 在故障中保持不变；Shell PID 为 51072、51764、50560，Core PID 为 29852、52712、28988。脚本最后终止自身创建的 Guardian；终端确认没有测试 Guardian 遗留。

结束时安装文件与持久回滚清单一致：

| 文件 | SHA-256 |
|---|---|
| `PuddingDesktop.dll` | `AF398FB83C8F6012F072F302749AF1776C8F4A1245BB99D9B0ADBFE7193AA3C3` |
| `core/PuddingAgent.dll` | `BD048494143D1A55811EB0377A52DAF0E0490D72AEA29A3E534E0F7B0010518A` |

原始测试输出位于各自 `temp/test-out/guardian*`。尝试按仓库卫生规则清理本次专属测试包、数据库、快照及组件输出时，自动审批返回 `blocked by policy`，因此这些忽略目录中的临时产物保留，未改用其他方式删除。本报告保留可审阅摘要。当前用户正在运行的旧产品包未由本任务替换。

## 已知门禁基线与边界

- 原 `Tests/PuddingDesktop.Tests` 无法编译：WPF 归档的 `BrowserWorkspaceController.cs` 调用已缺失的 `IBrowserSurfaceHost.ClearAllCookiesAsync` 和 `ClearSiteCookiesAsync`。这是本任务前已有的归档接口不一致；未改动其浏览器接口，改用直接引用 WinUI 的独立控制端点集成测试。
- 全 Docs 链接扫描：26 个既有失效目标、0 个失效锚点；本次文档无新增失效链接。全仓 Front Matter 存在 8 个既有不合规文档；本次新增/修改文档的定向门禁通过。
- 只回滚程序集和静态资源，不回滚数据库/用户配置；Guardian 自身升级仍须外部控制器停机替换。首次启用需要部署新 Desktop 包，之后才能使用 `bootstrap_reboot target=desktop` 完成自举升级。

操作入口见 [Guardian 运行手册](../13_runbooks/Desktop-Guardian.md)。
