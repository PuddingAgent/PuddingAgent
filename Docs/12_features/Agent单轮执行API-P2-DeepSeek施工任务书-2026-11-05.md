---
title: Agent 单轮执行 API P2：DeepSeek 施工任务书
author: hyfree
date: 2026-11-05
last_reviewed: 2026-11-05
status: active
description: "仅授权执行 SDK 的离线示例与演示测试，明确文件白名单、入口签名、退出码、无自动重试和禁止改核心等施工边界。"
categories: [docs, features]
tags: [sdk, delegation, contracts]
related_docs: [Docs/12_features/Agent单轮执行API-P2-契约与接入说明-2026-11-05.md, Docs/12_features/Agent配置API-P0P1-DeepSeek施工任务书-2026-11-05.md]
related_files: [Source/Pudding.Api.Sdk/AgentWorkClient.cs, Source/Pudding.Api.Sdk/HttpAgentWorkApi.cs, TestScripts/test-agent-work-contract-freeze.ps1]
slug: features-agent-work-deepseek-order-20261105
draft: false
---

# Agent 单轮执行 API P2：DeepSeek 施工任务书

这里只派发 **DS-W01：单轮执行 SDK 消费示例**，不负责接入宿主、设计契约或实现插件权限。需收到明确 Task ID 才开工；没有 ID 就报告缺少任务，不自行选择任务。此任务不阻塞核心 API，可以交给另一位施工者，但不得与正在编辑同路径的施工者并发。

## 1 范围与禁止项

只允许创建以下文件；如果已存在立即停止，不覆盖、不“合并优化”：

```text
Samples/AgentWorkClient/AgentWorkClient.csproj
Samples/AgentWorkClient/Program.cs
Samples/AgentWorkClient/WorkDemo.cs
Samples/AgentWorkClient/code_map.md
Tests/AgentWorkClient.Tests/AgentWorkClient.Tests.csproj
Tests/AgentWorkClient.Tests/WorkDemoTests.cs
Tests/AgentWorkClient.Tests/code_map.md
```

不修改其他任何文件。特别禁止修改 public contracts、SDK、domain service、canonical adapter、Host、原测试、解决方案、全局包配置、配置 API DS-01/02/03 文件与任务书。不得运行 commit/push/reset/clean/stash、部署或重启。不得读 D:\Keys、D:\data 或真实 LLM 凭据；不得向真实 Agent 发消息。测试全部注入假 `IAgentWorkApi`。

开工/交付前都运行 `TestScripts/test-agent-configuration-contract-freeze.ps1` 和 `TestScripts/test-agent-work-contract-freeze.ps1`。任一失败立即停止，报告文件名；**禁止更新指纹使测试通过**。开始先保存 git status 到 `.work/ds-w01/<run>/before-status.txt`。

## 2 精确接口

示例命名空间 `Pudding.Samples.AgentWorkClient`，公开类 `WorkDemo`：

```csharp
public static Task<int> RunAsync(
    string[] args,
    IAgentWorkApi api,
    TextWriter output,
    TextWriter error,
    CancellationToken ct = default);
```

通过 `new Pudding.Api.Sdk.AgentWorkClient(api).For(new AgentRef(...))` 调用。只支持两个位置参数命令：

- `submit <workspaceId> <agentId> <operationId> <text>`，严格 5 个参数，文本作为一个参数。不读文件、不连接 stdin、不拼接后续参数。恰好调用一次 Submit，不自动查状态、不轮询、不重试。
- `get <workspaceId> <agentId> <operationId>`，严格 4 个参数，恰好调用一次 Get，不调用 Submit。

`api/output/error` 必须非 null。参数为空、数量不符、未知命令、空白字段：stderr 写固定用法，返回 2，API 调用数必须 0。其余合法性由公共 API 验证，示例不复制 ID 或文本长度规则。

成功时 stdout 写一行 camelCase JSON：submit 为原回执；get 为原快照。get 返回 null 时写 `{ "found": false }`，返回 3。空回复仍按 Status 输出，不改写终态。正常得到回执/快照返回 0（Failed/Cancelled 快照也是一次成功查询）。

捕获 `AgentWorkException`：stderr 只写 `code`、`effectOutcome`、操作 ID，不打印完整异常或服务端 message。`effectOutcome=unknown` 返回 4，其余返回 1。捕获调用方取消返回 130；其他异常不要捕获成成功。不得在取消时发取消命令，当前合同没有此能力。

`Program.cs` 不实现凭据读取或在线连接。固定向 stderr 写“这是可注入 IAgentWorkApi 的离线示例；请由上层提供已授权的 API。”并返回 2。这样可复用示例核心，却不会误触真实宿主。不要添加新的 HTTP 配置系统来“补齐”入口。

## 3 工程与步骤

1. Sample 使用 `Microsoft.NET.Sdk`、OutputType Exe，只 ProjectReference `../../Source/Pudding.Api.Sdk/Pudding.Api.Sdk.csproj`；TFM 继承根属性。无新 PackageReference。
2. Test 使用 `MSTest.Sdk/4.0.1`、OutputType Exe、UseVSTest false，只引用 sample；遵守现有测试工程写法，不升级 MSTest。
3. 先写签名和参数校验；再实现两个调用分支和上述错误映射；最后写测试，不抽出通用 CLI 框架。
4. 测试：两个合法命令分别精确一次调用、参数原样传递（含中文/首尾空格）、每种参数错误零调用、unknown 不重试且输出不含异常敏感文本、not-found、Failed/Cancelled 查询返回 0、取消返回 130 且没有额外调用。
5. 日志/产物只放 `.work/ds-w01/<run>/`；不改文档主索引/日志，由主代理集成时处理。

```powershell
dotnet build Tests/AgentWorkClient.Tests/AgentWorkClient.Tests.csproj --artifacts-path .work/ds-w01/run/artifacts -p:UseVSTest=false -p:CollectCoverage=false --nologo > .work/ds-w01/run/build.log 2>&1
# 仅在 build exit 0 后执行：
dotnet .work/ds-w01/run/artifacts/bin/AgentWorkClient.Tests/debug/AgentWorkClient.Tests.dll --no-ansi --no-progress --results-directory .work/ds-w01/run/results > .work/ds-w01/run/test.log 2>&1
```

## 4 停止条件和交付

发现需要改白名单以外文件、核心契约无法表达行为、构建/原测试失败、指纹不匹配或已存在同名文件时，立即停止该任务，说明证据和建议，不自行扩范围。不得通过放松断言、catch-all 或默认成功绕过。

交付仅标 `ready-for-review`，报告 Task ID、实际创建文件清单、构建/测试退出码及日志路径、两个冻结检查结果、未完成项。不要宣称整套 Agent/Goal/插件系统完成。
