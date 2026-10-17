---
title: DeepSeek Search / Fetch 工具接入（独立组件 S1–S4 + 工具体系 S5）
author: hyfree
date: 2026-10-17
last_reviewed: 2026-10-17
status: archived
description: "为 PuddingAgent 接入 DeepSeek 官方 Search / Fetch 两个只读网络工具：先做零依赖叶子组件并独立测试（含真实 API Live 冒烟），再按组件化交付规程 S5 接入工具体系；风险权限为 Low（AutoAllowed，免运行时授权）。"
categories: [docs, changelog]
tags: [tools, deepseek, search, fetch, component, low-risk]
related_docs: [Docs/10_conventions/组件化交付规程.md, Docs/10_conventions/code-map-规范-v2.md, Source/PuddingDeepSeekWeb/code_map.md]
related_files: [Source/PuddingDeepSeekWeb/DeepSeekWebService.cs, Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekSearchTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekFetchTool.cs, Source/PuddingRuntime/Services/DeepSeekWeb/DeepSeekWebSettingsProvider.cs, Source/PuddingRuntime/Services/DeepSeekWeb/WebClientDeepSeekTransport.cs, Source/PuddingHost/default-data/agent-template-presets/general-assistant.json]
slug: changelog-2026-10-17-deepseek-search-fetch-tools
draft: false
---

# 2026-10-17 DeepSeek Search / Fetch 工具接入

## 目标 / 背景

用户要求：为 PuddingAgent 接入 DeepSeek 官方 **Search**（`POST https://api.deepseek.com/v1/tools/search`）与 **Fetch**（`POST https://api.deepseek.com/v1/tools/fetch`）两个工具；**先实现不依赖 PuddingAgent 的工具类/Service 并测试成功，再接入工具体系**；工具风险权限设为**低风险、无需授权**。参考文档：
[网页搜索与抓取指南](https://api-docs.deepseek.com/zh-cn/guides/search)、[Search API](https://api-docs.deepseek.com/zh-cn/api/search)、[Fetch API](https://api-docs.deepseek.com/zh-cn/api/fetch)。

按 [`组件化交付规程`](../../../10_conventions/组件化交付规程.md) 分两段交付：
（S1–S4）叶子组件独立构建/测试 →（S5）登记方案 + 宿主引用 + DI 装配 + 工具体系接入。文末"验证"节同时给出实现者自测与独立验收者（verifier，只读复跑）的口径。

## 改动

### S1–S4：新增叶子组件 `Source/PuddingDeepSeekWeb/`

| 文件 | 作用 |
|------|------|
| `PuddingDeepSeekWeb.csproj` | net10.0 叶子工程：**零** `ProjectReference` / **零** `PackageReference`；移除 `System.Net.Http` 隐式 using（组件不得直连 HTTP） |
| `DeepSeekWebTransport.cs` | 传输端口 `IDeepSeekWebTransport` + 请求/响应记录（组件不绑定 HTTP 栈） |
| `DeepSeekWebConnection.cs` | 连接参数（ApiKey/BaseUrl/TimeoutSeconds）+ 端点常量 + Base URL 规范化 |
| `DeepSeekSearchContracts.cs` | Search/Fetch 请求与结果记录（与官方字段一一对应） |
| `DeepSeekWebErrors.cs` | `DeepSeekWebErrorKind`（400/401/402/403/404/408/413/429/5xx/传输/坏响应）、结构化错误、`DeepSeekWebResult<T>` |
| `IDeepSeekWebService.cs` / `DeepSeekWebService.cs` | 对外接口与实现：参数校验 → 线上 snake_case JSON → 传输 → 解析/OpenAI 兼容错误映射 → 仅 408/429/5xx/Transport 有界重试（1s/2s… 指数退避，合法 `Retry-After` 1–30s 优先）；API Key 二次脱敏 |

新增独立测试工程 `Source/PuddingDeepSeekWebTests/`（只引用组件）：
`DeepSeekWebServiceTests.cs`（请求形状/校验矩阵/解析/错误映射/重试/取消）、`ComponentBoundaryTests.cs`（S3/S4 边界断言 + 探测器正向控制）、`DeepSeekWebLiveTests.cs`（真实 API Search→Fetch）、`TestAssemblyConfiguration.cs`。

### S5：接入工具体系（Runtime）

- `Source/PuddingRuntime/Services/DeepSeekWeb/DeepSeekWebSettingsProvider.cs`：凭据解析，**`search.providers.json` 的 `deepseek_search` 节优先 → 回退 `llm.providers.json` 的 `providerId=deepseek`**（`${ENV}` 展开；disabled 为终态不回退；错误信息带两处配置绝对路径且不回显 Key）。
- `Source/PuddingRuntime/Services/DeepSeekWeb/WebClientDeepSeekTransport.cs`：把组件传输端口原样适配到既有 `IWebClient`（复用 UA/超时/Flurl 通道；不分类、不重试、不吞异常）。
- `Source/PuddingRuntime/Services/DeepSeekWeb/DeepSeekWebServiceCollectionExtensions.cs`：`AddPuddingDeepSeekWeb()`，只装配配置解析器/传输/协议服务；两个工具由 `[Tool]` assembly scan 自动纳入注册表（不单独登记）。
- `Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekSearchTool.cs`（`deepseek_search`，SortOrder 56）与 `DeepSeekFetchTool.cs`（`deepseek_fetch`，SortOrder 57）：均为 `ToolCategory.Network`、`ToolPermissionLevel.Low`、`Safety = ReadOnly | ConcurrencySafe | RequiresNetwork` ⇒ 权限策略判定 **AutoAllowed、免运行时授权**；输出 ≤60,000 字符有界截断；Search 单条正文 ≤8,000 字符；错误按 Kind 给可操作提示并透出 HTTP 状态码。
- `Source/PuddingRuntime/PuddingRuntime.csproj`：`ProjectReference` 指向 `PuddingDeepSeekWeb`；`Tools/Platform/PuddingToolServiceCollectionExtensions.cs`：`AddPuddingToolRegistry` 内新增一行 `services.AddPuddingDeepSeekWeb();`（`IWebClient` 注册之后）。

### 测试与方案登记

- `Source/PuddingRuntimeTests/`：`Tools/DeepSeekSearchToolTests.cs`、`Tools/DeepSeekFetchToolTests.cs`、`Tools/DeepSeekWebTestHelpers.cs`、`Tools/DeepSeekWebLiveTests.cs`（真实联网 opt-in）、`Services/DeepSeekWebSettingsProviderTests.cs`、`Services/WebClientDeepSeekTransportTests.cs`。
- `Tests/PuddingHost.Tests/Tools/DeepSeekWebToolsCompositionTests.cs`：DesktopChild 产品组合根能解析两个工具、registry 可见、AutoAllowed。
- `PuddingAgentNetwork.slnx`：登记 `PuddingDeepSeekWeb` 与 `PuddingDeepSeekWebTests`。
- `Source/PuddingHost/default-data/agent-template-presets/general-assistant.json`、`research-assistant.json`：新增 `cap-deepseek-search` / `cap-deepseek-fetch` 能力选择，并在 `toolsDescription` 补齐工具用法（仅影响新装/新建 Agent；既有 Agent 模板需在能力目录里手工勾选）。
- 文档：`code_map.md`（根 §2/§3/§5/§7）、`Source/PuddingRuntime/code_map.md`、新增 `Source/PuddingDeepSeekWeb/code_map.md`。

## 验证

**组件独立（S1–S4）**

- `dotnet build Source\PuddingDeepSeekWeb\PuddingDeepSeekWeb.csproj --nologo` → **exit 0，0 警告 / 0 错误**。
- `dotnet test Source\PuddingDeepSeekWebTests\PuddingDeepSeekWebTests.csproj --nologo` → **48 通过 / 0 失败 / 0 跳过**（46 非 Live + 2 真实 API Live）。
- 真实 API Live（2/2 通过，凭据运行时读本机 `llm.providers.json` 的 `providerId=deepseek`，未打印 Key）：Search 返回 3 条（首条 `首次调用 API | DeepSeek API Docs`，authority=3）；对首条 URL Fetch 返回 2,866 字符 Markdown 正文。
- 边界取红：临时向组件加入直接 `HttpClient` 的文件 ⇒ 2 条边界用例变红；删除后复跑 48/48 复绿。

**接入工具体系（S5）**

- `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --nologo` → **exit 0，0 错误**（工程内既有警告不变）。
- `dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~DeepSeek"` → **40 通过 / 0 失败 / 1 跳过**（跳过项为未 opt-in 的 Live）。
- `dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --filter "FullyQualifiedName~DeepSeek"` → **1 通过 / 0 失败**（产品组合根解析工具 + registry 可见 + AutoAllowed）。
- 真实联网端到端（`PUDDING_DEEPSEEK_LIVE=1`，经工具体系 `ExecuteAsync → 组件 → Flurl IWebClient → api.deepseek.com`）→ **1 通过**，控制台证据：`source=llm.providers.json baseUrl=https://api.deepseek.com timeout=30s`、`search outputChars=10652`、`fetch host=api-docs.deepseek.com outputChars=10384`（独立验收者复跑结果一致）。
- 独立 API 连通性探针（Lead，curl，仅记录非敏感事实）：Search HTTP 200 / 3 条结果；Fetch HTTP 200 / 6,402 字符。
- 回归抽查（独立验收者按类名 filter 复跑）：`PuddingToolInfrastructureTests` 151、`RuntimeServiceExtensionsTests` 6、`ToolDiscoveryTests` 12、Host `PuddingApplicationHostCompositionTests` 2 —— **171 通过 / 0 失败**。
- `code_map_check.py --check`（根 + Runtime + 组件三份）：**error 0、指纹 6/6 校验通过**；全仓 `--check` 同样 exit 0（8 个声明指纹通过，2 个既有 code_map 指纹失配为 warn，非本次文件）。
- 全解决方案构建（`dotnet build PuddingAgentNetwork.slnx --no-restore`）：**DeepSeek 4 个工程均编译成功**；整体 exit 1 由 3 条**既有** `CS8604`（`Source/Pudding.Rpc.IpcProbe/ProbeExecutor.cs`，文件未改动、不引用本组件、该工程启用了告警即错误）导致，与本改动无关（该工程在 2026-10-12 的同类记录中已存在）。
- 文档门禁：`front_matter.py --check` exit 0（1502 份 md / 0 不合规）；`check_md_links.py` exit 1 的 24 个失效目标全部位于 5 个**未改动**的既有文档（含 `Docs/16_qa/Review.md`），本任务新增/修改文档无失效链接。
- 说明：组件 Live 用例在能定位本机凭据时随常规 `dotnet test` 自动执行（本次 2/2 真实调用并通过）；Runtime 侧 Live 需显式 `PUDDING_DEEPSEEK_LIVE=1`，避免常规验收意外产生网络费用。
- 未覆盖：真实 Core/Desktop 进程内 UI 对话 smoke（本任务未启动 Core；"工具真实可用"由上述经工具体系的真实 API Live 测试证明）；全量 RuntimeTests/Host.Tests（仅跑 DeepSeek 相关用例 + 指定回归类）；全解决方案构建中既有 `IpcProbe` 失败与既有 md 链接失效未修复（非本任务职责）。

## 影响与回滚

- **配置**：优先读取 `<DataRoot>/config/search.providers.json` 的 `deepseek_search` 节；未配置时自动回退 `<DataRoot>/config/llm.providers.json` 中 `providerId=deepseek` 的 baseUrl+apiKey。本机 `D:\data` 现状即回退路径可用；未写入任何密钥。
- **数据/数据库**：无迁移、无 schema 变更。
- **产品默认**：仅新增能力与预置模板能力项；不改变既有工具行为、审批策略或权限阈值。
- **回滚**：撤销本 commit 即可（组件为独立工程；Runtime 仅新增 ProjectReference、一行装配调用与两个工具类）。
- **费用**：Search/Fetch 按成功请求计费（官方价 5 元/1000 次）；Live 测试默认跳过，本任务真实调用次数为个位数。

## 关联

- 参考文档：[Search/Fetch 指南](https://api-docs.deepseek.com/zh-cn/guides/search) · [Search API](https://api-docs.deepseek.com/zh-cn/api/search) · [Fetch API](https://api-docs.deepseek.com/zh-cn/api/fetch)
- 规程：[组件化交付规程](../../../10_conventions/组件化交付规程.md) · [code_map 规范 v2](../../../10_conventions/code-map-规范-v2.md)
- 组件索引：[`Source/PuddingDeepSeekWeb/code_map.md`](../../../../Source/PuddingDeepSeekWeb/code_map.md)
- 共享任务：`task-1`（组件 S1–S4，owner component-dev）· `task-2`（S5 接入，owner integration-dev）· `task-3`（独立验收，owner verifier）
- 提交：与本日志同一 commit（DeepSeek Search/Fetch 工具接入）
