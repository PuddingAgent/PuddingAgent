---
title: DeepSeek chat-demo 源码对照与改进优先级
author: hyfree
date: 2026-11-15
last_reviewed: 2026-11-15
status: active
description: "对照 DeepSeek 应用 API、chat-demo 源码和 Pudding 现有链路，确定六项聊天能力的复用边界与改进顺序。"
categories: [docs, references]
tags: [deepseek, chatbot, search, documents, speech]
related_docs: [Docs/12_features/DeepSeek应用能力集成方案-2026-11-15.md, Docs/12_features/统一ASR-TTS能力与聊天语音体验优化方案-2026-11-15.md]
related_files: [Source/PuddingCore/Core/AnthropicMessagesLlmGateway.cs, Source/PuddingCore/Core/ResponsesLlmGateway.cs, Source/PuddingRuntime/Services/VoiceProviderFactory.cs, Source/PuddingPlatform/Services/SessionTitleService.cs]
slug: references-deepseek-chat-demo-comparison-2026-11-15
draft: false
---

# DeepSeek chat-demo 源码对照与改进优先级

## 1. 结论

**最值得继承的是有序内容块、搜索来源投影、附件任务状态和流式错误语义。最值得改进的是“用户能选能力、过程看得见、结果能追溯、下次还能接着问”的完整链路。** Pudding 已有 Agent 执行、工具、文件引用、语音和会话管理基础，应补齐这些链路；不需要移植一个新的聊天应用。

**整体优先级以用户明确选择的 ASR/TTS 为先**：先修语音故障、统一底层服务、完善输入与播放体验，再推进 DeepSeek 搜索、链接、文档和标题。语音不依赖 DeepSeek 原生聊天协议，应按 [统一语音方案](../12_features/统一ASR-TTS能力与聊天语音体验优化方案-2026-11-15.md) 独立交付。

本报告表中的 P0–P3 仅表示 DeepSeek 聊天分线内部的依赖顺序：P0 原生内容块与持久回放；P1 普通搜索、链接和标题；P2 深度搜索与文档；P3 可选 DeepSeek 语音适配。它们不排在统一语音体验之前。

配套的 [集成方案](../12_features/DeepSeek应用能力集成方案-2026-11-15.md) 给出接口、阶段、验收矩阵与部署要求。本报告是静态源码研究，不构成真实 API 或运行中产品验收。

## 2. 来源与研究边界

- [官方更新日志](https://api-docs.deepseek.com/zh-cn/updates/)：2026-11-13 发布应用 API。
- [构建 Chatbot](https://api-docs.deepseek.com/zh-cn/guides/build_a_chatbot/)：新能力面向 `deepseek-flash`，覆盖 Messages、Responses、Chat Completions 三种协议。
- [chat-demo 固定版本](https://github.com/deepseek-ai/chat-demo/tree/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c)：`5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c`，提交说明为 v0.1.1；研究时通过 GitHub 连接器读取指定版本源码，没有向仓库下载第三方代码。
- Pudding 基线：`78ae6fa`；任务开始时文件检索工具及相关测试存在他方未提交 WIP，本报告不以其行为作为结论依据。

下文区分官方协议事实、源码事实与集成建议。没有输出或复制运行时密钥，没有调用计费 API，也没有确认当前产品进程加载的是哪一份构建。

## 3. 六项能力：已有基础与真实缺口

| 目标 | 官方入口 | Pudding 已有基础 | 需要补齐 | 优先级 |
|---|---|---|---|---|
| 联网搜索 | `deepseek_web_search`，关闭思考 | 独立 `deepseek_search` 工具与叶子协议组件 | 原生工具声明、服务端步骤事件、引用、工具计费 | P1 |
| 深度思考搜索 | 同一工具，开启思考 | thinking 文本、Agent 多轮工具执行和过程展示 | 原生思考与 search/open/find 的有序交错、终态用量、取消 | P2 |
| 文件输入 | Files 上传解析后引用 `file_id` | 图片 Artifact、Files 大图上传、远端引用缓存 | 文档类型、两类解析 purpose、上传任务、过期恢复、冷水合 | P2 |
| 阅读链接 | `deepseek_read_url` | `deepseek_fetch` 可独立抓 Markdown | 原生 read_link、签名回放、第二轮及重启后继续问 | P1 |
| TTS / ASR | `/v1/tools/tts`、`/v1/tools/asr` | 语音配置、Provider 工厂、HTTP API、输入/播放 UI | 统一服务与体验为整体最高优先级；DeepSeek 只是后续可选 Provider | 语音主线优先；DeepSeek 适配 P3 |
| 会话标题 | `title.enabled`，随回答返回 | 默认序号标题、手动重命名 | 模型标题事件、首次请求策略、用户重命名保护 | P1 |

### 3.1 搜索已经有了，但不是新 API 的原生搜索

[`PuddingDeepSeekWeb`](../../Source/PuddingDeepSeekWeb/code_map.md) 已封装 Search/Fetch；Runtime 的 [`DeepSeekSearchTool`](../../Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekSearchTool.cs) 与 [`DeepSeekFetchTool`](../../Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeekFetchTool.cs) 是客户端工具，由 Pudding 调用和消费结果。

新 API 的搜索由 DeepSeek 服务端执行。它不应再次被转换成待执行的本地工具调用，否则会重复搜索、重复计费并扰乱工具结果配对。现有独立工具仍适用于其他模型、明确要求抓取正文的任务，以及需要本地编排的研究流程。

### 3.2 回放已有雏形，持久化仍是硬缺口

[`LlmContinuationState`](../../Source/PuddingCore/Models/LlmContinuationState.cs) 已有不透明 output items；[`AnthropicMessagesLlmGateway`](../../Source/PuddingCore/Core/AnthropicMessagesLlmGateway.cs:302) 能回传这些块，流解析也会先保存原始 block。然而其 delta 分支目前不处理 `citations_delta`，message delta 不取标题；[`StreamDelta`](../../Source/PuddingCore/Models/StreamDelta.cs) 没有搜索步骤、引用或标题字段。

更关键的是，当前 [`ChatMessageRow`](../../Source/PuddingCore/Platform/IPlatformRepositories.cs:24) 没有专门的 continuation 字段；[`ContextWindowManager`](../../Source/PuddingRuntime/Services/ContextWindowManager.cs:511) 的所查 DB 冷水合路径恢复文本和图片部件，未恢复 continuation。**热会话里暂时保留原始块，不等于重启后还能读取上轮网页。** 新能力必须做消息写入、镜像、冷水合、压缩和分支恢复的往返验收。

官方说明搜索历史和 read_link 的上下文语义不同：后者依靠签名恢复网页。不能把所有工具结果压成正文后仍声称保留网页上下文。相关契约见 [联网工具](https://api-docs.deepseek.com/zh-cn/guides/web_tools/)。

### 3.3 上传大图不等于支持文档输入

[`LlmContentPart`](../../Source/PuddingCore/Models/LlmContentPart.cs) 及其 v1 信封目前只识别 text/image；[`DeepSeekFilesApiClient`](../../Source/PuddingCore/Core/DeepSeekFilesApiClient.cs) 走 `purpose=user_data` 的图片上传路径。聊天输入 [`IntentConsole`](../../Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx:998) 的所查选择器是 `image/*`。

可复用 Artifact 身份、远端引用缓存和失效重建思路；不能把文档塞成 image，也不能沿用图片大小限制。新文档解析有独立的 purpose、时限和费用；见 [文件解析](https://api-docs.deepseek.com/zh-cn/guides/document_parsing/)。

### 3.4 语音和标题都是增量适配

[`VoiceProviderFactory`](../../Source/PuddingRuntime/Services/VoiceProviderFactory.cs) 的 TTS 当前只允许 DashScope，ASR 创建 DashScope 实现；[`VoiceController`](../../Source/PuddingPlatform/Controllers/Api/VoiceController.cs) 已有识别和合成端点。前端既有浏览器语音适配器，也有 DashScope 输入路径；“有语音按钮”不能作为已接入 DeepSeek 的证据。

[`SessionTitleService`](../../Source/PuddingPlatform/Services/SessionTitleService.cs) 实际生成的是“默认助手1”之类的序号标题，并未按聊天内容生成。保留它作默认标题即可，增加模型标题的窄更新入口。

## 4. chat-demo 最值得继承的实现

以下链接全部固定到同一提交，便于后续逐文件复核。

| 上游文件 | 值得继承 | Pudding 落点与改造要求 |
|---|---|---|
| [api/parseEvent.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/api/parseEvent.ts) | 按 block 类型解码，保留服务端附加字段；引用增量与签名分开处理 | Provider 协议组件解析；未知块保存在原始记录里，不能因 UI 不认识而破坏回放 |
| [chat/reply.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/chat/reply.ts) | 纯归并函数；block index 与显示 index 分离；正文、引用、思考分别累积 | 做协议无关的有序输出投影；历史回传规则必须留在 Provider 适配层 |
| [chat/view.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/chat/view.ts) | `tool_use_id` 配对；搜索、打开网页、查找、思考合成步骤；来源编号关联正文 | 扩展现有执行过程投影与消息气泡；不要再建第二套聊天 store |
| [api/sse.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/api/sse.ts)、[api/client.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/api/client.ts) | 跨分片 UTF-8、多行 data、保活、取消释放；HTTP/流内错误/断流分类 | 用作现有网关 SSE 的对照测试材料；EOF 与成功终态分开验证，不能直接复制前端网络层 |
| [attachments.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/attachments.ts) | 附件选中即排队；按附件身份去重；上传中/完成/失败/取消独立 | 上传任务和账号并发额度由 Core 管理；前端只显示状态，远端 file_id 不成为 UI 权威 |
| [speech/recorder.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/speech/recorder.ts)、[speech/readAloud.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/speech/readAloud.ts) | WAV 编码、麦克风清理；播放单实例、取消、PCM 半采样残留处理 | 复用算法和状态设计；录音优先用 AudioWorklet，避免照搬 ScriptProcessorNode；密钥留在 Core |
| [chat/branches.ts](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/src/services/chat/branches.ts) | 显示路径与送模型上下文使用同一分支；编辑和重生成保留旧节点 | 后续对照现有 checkpoint/分支语义，补测试；不随本次六项能力重写整个会话模型 |

一个需要特别区别的细节：demo 的 `reply.ts` 对已完成历史进行瘦身，仅保留部分 read_link 工具块；这是特定协议的投影优化，不能复制成全局 transcript 清理规则。Pudding 应保存完整 canonical 事实，出站时才按对应协议生成历史。

## 5. 产品改进的收益顺序

| 顺序 | 用户可见改进 | 判断依据 |
|---|---|---|
| 1 | ASR 可用、转写可编辑、Qwen TTS 播放与停止可靠 | 用户明确置顶；当前已有 HTTP 500 和端到端交互缺口 |
| 2 | 搜索开关、来源引用、展开步骤、读取失败原因 | 把现有“能调用搜索”变成“能核对答案依据”，并支持新原生 API |
| 3 | 文档卡片、解析状态、文字/图文模式、跨轮引用 | 从图片扩展到日常 PDF/Office 工作流，新增能力范围大 |
| 4 | 首轮自动标题，用户改名后不覆盖 | 基础服务已有，改动面较小，直接改善会话查找 |
| 5 | 消息编辑/重新生成的版本切换 | 有价值，但涉及持久事实、工具副作用与执行身份，另立任务 |
| 6 | 历史导入导出、更多主题与动效 | 可后续借鉴；当前有更直接的能力缺口 |

这里的产品收益顺序不同于施工依赖顺序：标题可与搜索并行开发，但必须经过同一事件与持久化边界。

## 6. 不适合继承的部分

- **浏览器直连和保存 API Key**：demo 是静态演示应用；Pudding 已有 Core 和配置服务，应由 Core 统一持有凭据、调用 API、执行配额与隔离。
- **IndexedDB/Zustand 作为对话真源**：Pudding 的 canonical Turn、消息持久化和事件投影不能迁到浏览器。
- **单一模型应用的假设**：DeepSeek 能力必须按实际模型与协议声明；默认模板当前仍选 `openai`，不能因 demo 使用 Messages 就暗中切换全体 Agent。
- **只按按钮停止来承诺停止计费**：上传解析或服务端调用可能已经执行；需要区别本地取消、远端完成未知和已确认失败。
- **整套 Markdown、主题和布局替换**：Pudding 已有对应组件，只引入搜索引用、附件、播放状态所需的增量。

上游为 [MIT 许可](https://github.com/deepseek-ai/chat-demo/blob/5d5dd754ebc1948e977ddb880a9eb5aa25f37b4c/LICENSE)。如后续复制实质性代码，需随代码保留版权与许可说明；本次只作研究和设计。

## 7. 待真实验证的问题

1. 新原生工具与 Pudding 本地 function tools 混用时，实际模型是否稳定遵守执行身份、工具结果配对和当前用户围栏。
2. 三协议在并发服务端步骤、引用、标题、拒答和累计用量上的线上形状是否与文档一致。
3. read_link 的签名经过消息持久化、重启、压缩、分支切换后能否继续生效。
4. 文档解析的延迟、真实费用、图文遗漏与大文件占用；语音首音延迟、取消与 WebView2 麦克风体验。

这些是后续实施验收项，不是本报告已经证明的行为。
