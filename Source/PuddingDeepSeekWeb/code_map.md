# PuddingDeepSeekWeb 代码地图

> 源指纹: Source/PuddingDeepSeekWeb/**=b2bb74cfdc4e · 条目数: 7 · 最近整理: 2026-10-17
> 叶子组件（组件化交付规程 S1–S4）：DeepSeek Search / Fetch 协议语义。`ProjectReference = 0`、`PackageReference = 0`，不直接说 HTTP（传输是端口）。

## 定位

DeepSeek 官方 Search / Fetch API（`https://api.deepseek.com/v1/tools/search`、`/v1/tools/fetch`）的**协议客户端**：参数校验、线上 JSON 形状、响应映射、OpenAI 兼容错误分类与有界重试。宿主（`PuddingRuntime`）通过 `IDeepSeekWebTransport` 适配器复用既有 `IWebClient` 接入，成为 `deepseek_search` / `deepseek_fetch` 两个低风险工具。

## 关键文件

| 文件 | 用途 |
|------|------|
| `PuddingDeepSeekWeb.csproj` | 叶子工程：net10.0，零 Project/零 Package 引用；移除 `System.Net.Http` 隐式 using |
| `DeepSeekWebTransport.cs` | 传输端口 `IDeepSeekWebTransport` 与请求/响应记录 |
| `DeepSeekWebConnection.cs` | 连接参数、端点常量与 Base URL 规范化 |
| `DeepSeekSearchContracts.cs` | Search / Fetch 的请求与结果记录（与官方字段一一对应） |
| `DeepSeekWebErrors.cs` | 错误分类、结构化错误与统一返回 `DeepSeekWebResult<T>` |
| `IDeepSeekWebService.cs` | 组件对外接口：`SearchAsync` / `FetchAsync`（凭据逐次传入） |
| `DeepSeekWebService.cs` | 校验 → 线上 JSON → 传输 → 解析/错误映射 → 可重试类别有界重试 |

## 测试工程

`Source/PuddingDeepSeekWebTests/`（只引用本组件）：

- `DeepSeekWebServiceTests.cs` — 假传输驱动：请求形状/URL/鉴权头/线上 JSON/省略规则、校验矩阵（失败不产生出站请求）、成功解析（含未知字段容忍）、HTTP 错误映射、有界重试与 `Retry-After`、传输异常、取消传播、`DeepSeekWebEndpoints` 单元测试。
- `ComponentBoundaryTests.cs` — S3/S4 边界断言：测试进程加载闭包与组件引用表不含上层程序集、csproj 清单（零 ProjectReference/PackageReference、无 InternalsVisibleTo）、组件源码不出现 HTTP 栈 token；含探测器正向控制样本（URL/字段名/Bearer 属协议知识，不误伤）。
- `DeepSeekWebLiveTests.cs` — `[TestCategory("Live")]` 真实 API 冒烟：Search（count=3）→ 用首条 Url Fetch，断言非空结果/正文；凭据来自 `DEEPSEEK_API_KEY` 或 `llm.providers.json` 的 `providerId=deepseek`，拿不到时 Inconclusive。
- `TestAssemblyConfiguration.cs` — 显式 `[assembly: DoNotParallelize]`（Live 用例串行、省费用）。

## 不变量

- 组件不读配置文件、不持有长期凭据；API Key 不出现在错误消息/日志。
- 组件不引用任何 Pudding 上层程序集，也不直接使用 `HttpClient`（`System.Net.Http` 隐式 using 已移除）。
- Search 的 `need_content=false` 时 `content` 恒为空串；Fetch 返回 Markdown 正文。
- 线上请求 `need_content` 恒显式输出（false 也写），便于排障；可选字段为 null/空白时省略。
- 只有 408 / 429 / 5xx / 传输失败会重试，且重试次数有界（默认 2 次重试）；退避 1s、2s…，合法 `Retry-After`（1–30 整数秒）优先。
