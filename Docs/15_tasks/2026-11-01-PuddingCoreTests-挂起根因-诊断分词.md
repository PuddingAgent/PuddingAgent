---
title: PuddingCoreTests 跑不完的根因：诊断分词把请求拖成分钟级
status: fixed
date: 2026-11-01
author: 鲸鲸
---

# 结论

`PuddingCoreTests` 长期跑不完（20 分钟无任何结果）**不是个别测试挂死，而是诊断代码在请求主链路上做了全量分词**。

## 证据链

1. `--blame-hang --blame-hang-timeout 120s` 点名：
   `VisionPreprocessingWireTests.BodyBudgetIncludesTextAndRecompressesBeforeSending`
2. 装 `dotnet-dump` 分析 hangdump，`clrstack -all` 拿到**精确调用栈**：

```
PuddingCode.Platform.ContextUsageSnapshotStore.CountTokens(String, String)
  ← PuddingCode.Observability.LlmRequestCapture.<WriteAsync>d__14.MoveNext()
  ← LlmRequestCapture.WriteAsync(HttpRequestMessage, String, CancellationToken)
  ← AnthropicMessagesLlmGateway.<ChatAsync>d__19.MoveNext()
  ← AnthropicMessagesLlmGateway.ChatAsync(...)
  ← VisionPreprocessingWireTests.BodyBudgetIncludesTextAndRecompressesBeforeSending
```

3. 尺寸二分（同一测试，逐步缩小载荷）：

| 用户消息大小 | 结果 |
|---|---|
| 24 MiB（原始） | >600 s 仍未返回 |
| 2 MiB | >90 s 仍未返回 |
| 256 KiB | 秒级完成（断言失败≠挂起） |
| 1 KiB | 秒级完成 |

⇒ 与图片载荷无关（`FillByteBudget=false` 也一样），**只与报文文本大小有关**。

## 根因

`LlmRequestCapture.WriteAsync` 为了让日志里同时出现「估算」与「实际」两个数，
对**整个请求体的文本**调用 `ContextUsageSnapshotStore.CountTokens`。
请求体是几十 MB 时，这一行诊断就把一次请求变成分钟级 CPU 占用。

`PUDDING_CAPTURE_REQUEST=1` 在本机环境中是设置状态，所以这条路径在本机全量触发。

## 修法

```csharp
// 诊断不得拖慢主链路；超过阈值记为 -1（未测量），而不是伪造估算值。
const int MaxTokenizedChars = 256 * 1024;
var bodyTokens = sanitized.Text.Length <= MaxTokenizedChars
    ? ContextUsageSnapshotStore.CountTokens(sanitized.Text, modelId)
    : -1;
```

选择 `-1` 而不是「抽样估算」：诊断字段被当成真值时最危险，宁可明确标「未测量」。

## 验证

| 项 | 结果 |
|---|---|
| 目标测试（24 MiB，capture 开启） | **3/3 通过**，秒级返回 |
| `PuddingCoreTests` 全量 | **1199 = 1196 通过 / 2 NotExecuted / 1 失败**，约 2 分钟跑完 |

唯一失败：`DeployDirectoryTransactional_DeploysChangedFiles_AndVerifiesHash` —— 与本改动无关，待单独排查。

## 副产品：这个工程终于可以进门禁了

在此之前，任何包含 `PuddingCoreTests` 的门禁都会挂住，于是这 1199 条测试事实上处于「不设防」状态。
现在它可以被常规门禁覆盖。

## 一条方法论

人工二分在这里是错的方向：先 `--blame-hang` 拿到测试名，再用 `dotnet-dump` 读调用栈，
两分钟出结论。**挂起问题的第一反应应该是拿栈，不是缩小负载。**
