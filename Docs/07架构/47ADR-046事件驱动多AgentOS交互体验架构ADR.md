# ADR-046：事件驱动多 Agent OS 交互体验架构

> 状态：**Proposed**  
> 日期：2026-05-28  
> 范围：Admin Chat、语音交互、摄像头视觉输入、Agent 虚拟形象、消息系统、事件系统、多 Agent 协作、全局 UI 语言、用户操作 UX  
> 关联：[37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR](37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR.md)、[42ADR-041Chat暗色主题语义Token收敛ADR](42ADR-041Chat暗色主题语义Token收敛ADR.md)、[46ADR-045双向消息系统与聊天室客户端ADR](46ADR-045双向消息系统与聊天室客户端ADR.md)、[49ADR-048Hermes型系统开发方向参考ADR](49ADR-048Hermes型系统开发方向参考ADR.md)

---

## 1. Context

Pudding 的目标不只是一个聊天页，而是一个基于事件驱动、多 Agent 架构的本地 AI OS。上层交互必须能表达底层系统能力：

- 用户通过文字、语音、摄像头、快捷动作、文件、Webhook、MQTT 等入口与系统交互。
- 用户可以授权浏览器麦克风和摄像头，让 Agent 获得“听觉”和“视觉”输入。
- Agent 不应只是聊天框背后的文本服务；它需要可感知、可反馈、可被用户信任的虚拟形象和状态表达。
- Agent 是聊天室参与者，也是后台任务执行者、事件消费者和消息发送者。
- 事件系统、消息系统、记忆系统、上下文合成、工具执行、会话层都需要被用户理解和观察，但不能把内部复杂度直接暴露为噪音。
- Admin Chat 是主要操作面，但不应成为唯一客户端；UI 只是观察窗口和人类输入端。

当前 UI 已有大量增量功能：Chat、Composer、语音按钮、DevPanel、Workspace Studio、子 Agent 卡片、记忆/缓存/Token 指示器、命令面板、主题 token。但这些能力仍像多个局部增强拼在一起，缺少统一的信息架构、多模态权限模型和交互状态模型。

---

## 2. Findings

### 2.1 Chat 仍偏单 Agent 对话模型

当前 Chat 顶部仍以“工作空间 + 单 Agent”作为主结构，左侧也是 session 列表。这不适合多 Agent OS：

- 用户需要理解“房间里有哪些参与者”，而不是只选择一个 Agent。
- `@agent/@all` 应该是聊天室路由语义，不是前端或 Controller 的补丁逻辑。
- Agent 主动消息、Agent-to-Agent、Connector 消息都需要进入同一 room timeline。

### 2.2 语音入口存在交互失真

`VoiceInputButton` 已存在，但之前的行为会在“即将开放”的提示下仍切换到录音/播放视觉状态。这会让用户误以为正在录音或播放，属于信任问题。

修复原则：

- 未接入语音管线前，入口必须明确禁用或标记为待接入。
- 不展示虚假的波形、录音、播放状态。
- 真正接入后，语音状态必须来自底层会话/事件，而不是前端局部假状态。

### 2.3 摄像头视觉输入尚未进入架构

如果 Agent 拥有视觉能力，摄像头不能只是前端组件直接把图片塞给模型。它需要完整链路：

- 浏览器权限申请。
- 摄像头会话生命周期。
- 本地预览、暂停、截图、关闭。
- 隐私提示和数据最小化。
- 帧采样和压缩策略。
- 视觉输入如何进入消息系统、事件系统或工具系统。
- 用户如何确认“Agent 正在看什么”。

缺少这层设计会导致安全和体验问题：用户不知道是否在录制，Agent 也无法把视觉输入与消息、任务、房间上下文关联。

### 2.4 Agent 虚拟形象不能只是装饰挂件

Live2D / sprite / pet 这类虚拟形象可以增强 Agent 的“在场感”，但它必须表达系统状态，而不是成为遮挡 UI 的装饰：

- idle：待命。
- listening：正在听。
- seeing：正在看。
- thinking：正在思考。
- speaking：正在回复或语音播报。
- tool：正在调用工具。
- error：需要用户处理。
- sleeping：Agent 休眠或不接收心跳。

虚拟形象必须绑定 Agent/RoomParticipant 状态和 delivery/execution projection，而不是自己维护一套动画状态。

### 2.5 设计语言仍有多套 token 和旧组件残留

已有 ADR-036 和 ADR-041 明确了 Pudding 设计语言与 Chat token，但 `styles.ts` 和部分组件仍混用：

- `--pudding-chat-*` 新语义 token。
- `--earth-brown`、`--soft-white`、`--warm-beige` 等旧 token。
- AntD token。
- 组件内联颜色。

这会导致主题切换、暗色模式、可访问性和跨页面一致性难以验证。

### 2.6 运行状态太分散

Composer 状态、DevPanel、子 Agent 卡片、Token 指示、缓存指示、工具步骤分别存在。用户看到很多“状态碎片”，但缺少一个可理解的系统层级：

```text
房间状态
  -> 消息投递状态
  -> Agent 执行状态
  -> 工具/记忆/上下文状态
  -> 结果和后续动作
```

### 2.7 UI 性能风险来自事件粒度和渲染粒度不一致

事件系统可能高频产生 delta、step、thinking、diagnostic、delivery 状态。UI 如果逐事件重渲染或反复解析 Markdown，会造成卡顿。当前已有 `useBufferedStreaming`，但多 Agent room timeline 需要进一步把“事件接收、状态归并、视图渲染”分层。

---

## 3. Decision

建立 Pudding Interaction OS 分层：

```text
Interaction Clients
  - Web Chat
  - Voice
  - Camera Vision
  - Avatar / Live2D / Sprite Companion
  - CLI
  - Mobile
  - External connector clients
        |
        v
Interaction Gateway
  - auth / workspace binding
  - input mode normalization
  - accessibility and permissions
  - rate limit / backpressure
        |
        v
Message Fabric
  - RoomParticipant
  - MessageEnvelope
  - MessageDelivery
  - MessageInbox
        |
        v
Event System (message-backed mechanism)
  - priority queue
  - wakeup
  - retry / dead letter
  - subscription
        |
        v
Execution Engine
  - Agent runtime
  - context synthesis
  - memory
  - tools
        |
        v
Observation Projections
  - room timeline
  - participant presence
  - delivery status
  - execution timeline
  - diagnostics
```

`Message Fabric` 和 `Event System` 在交互层不是两个可独立替换的功能块，而是一条 message-backed event pipeline 的领域层和机制层。`Message Fabric` 决定“谁给谁发了什么、谁可见、谁需要收件、投递状态是什么”；`Event System` 决定“这条投递如何被排队、唤醒、重试、死信、回放和诊断”。UI 可以把它们展示为一条消息链路中的不同阶段，但不能让组件直接绕过消息系统监听内部事件，也不能让交互入口绕过事件队列直接唤醒 Runtime。

因此，所有 text、voice、camera、file、command、connector 输入最终都应先归一为 `MessageEnvelope` 或 artifact-backed message，再由 `IMessageSystem` 产生 `MessageDelivery` 和 `message.deliver` 事件。UI projection 从 delivery、execution trace、voice/vision session projection 中读状态；原始事件只进入 Inspector 和诊断视图。

UI 不直接消费原始内部事件。UI 消费面向交互的投影：

- `RoomTimelineProjection`
- `ParticipantPresenceProjection`
- `MessageDeliveryProjection`
- `ExecutionTraceProjection`
- `VoiceSessionProjection`
- `VisionSessionProjection`
- `AvatarStateProjection`
- `SystemHealthProjection`

---

## 4. Product Information Architecture

### 4.1 Primary Workbench：聊天室

聊天室是主交互面，采用三层结构：

```text
Left Rail       Center Room Timeline              Right Inspector
房间/会话列表     消息、Agent 输出、系统状态          参与者、投递、工具、诊断
```

默认屏幕应聚焦于当前房间：

- 左侧：rooms / recent conversations / unread。
- 中间：room timeline，支持用户消息、Agent 消息、connector 消息、system note。
- 右侧：参与者列表、Agent 状态、delivery 状态、事件诊断，默认可折叠。
- 底部：统一 Composer，支持 text、voice、command、attachments、mode hints。

### 4.2 Composer：输入模式不是按钮堆叠

Composer 应表达“输入意图”，而不只是放很多图标：

| 输入模式 | UI 表达 | 底层入口 |
|----------|---------|----------|
| Text | 默认 textarea | `MessageEnvelope(contentType=text)` |
| Command | `/` 命令面板 | `MessageEnvelope(contentType=command)` |
| Mention | `@agent/@all` 补全 | `IMessageRouter` 权威解析 |
| Voice | 录音状态条 + 转写确认 | `VoiceSession -> MessageEnvelope` |
| Camera | 摄像头预览 + 截图/采样确认 | `VisionSession -> MessageEnvelope / ToolInput` |
| File | 附件槽 + 处理状态 | artifact / tool / memory event |
| Automation | 定时/心跳意图面板 | Cron / Heartbeat |

语音接入前，按钮只能显示“待接入”，不得伪造录音状态。

### 4.3 Voice Interaction

语音不是前端局部功能，应作为 Interaction Gateway 的一种输入模式：

```text
Microphone
  -> VoiceCaptureSession
  -> ASR / local transcription
  -> User confirmation or auto-send policy
  -> MessageEnvelope(contentType=text, metadata.inputMode=voice)
  -> IMessageSystem
```

Agent 语音回复：

```text
Agent send_message(contentType=text/audio)
  -> MessageDelivery(target=user)
  -> TTS job optional
  -> VoicePlaybackSession
  -> Web/Mobile/CLI client plays if allowed
```

语音交互必须支持：

- 明确录音权限请求。
- 明确录音中状态。
- 取消、暂停、重新录制。
- 转写预览和编辑。
- 快捷发送。
- 禁止在未授权时自动录音。
- 离线/无麦克风时降级到文字。

#### 4.3.1 Voice Input / ASR

语音输入是 Agent “听觉”的入口，但浏览器麦克风只负责采集，不负责业务决策。链路应为：

```text
Browser Microphone
  -> VoiceCaptureSession(permission / device / local meter)
  -> VoiceAudioFrame(format/sampleRate/duration/sequence)
  -> IVoiceRecognitionService
  -> IAsrProvider(DashScope / local / other)
  -> VoiceRecognitionStreamEvent(intermediate/final transcript)
  -> VoiceSessionProjection
  -> MessageEnvelope(contentType=text, metadata.inputMode=voice)
  -> IMessageSystem
```

Provider 侧建议抽象：

- `IVoiceRecognitionService`：面向业务，负责权限策略、隐私策略、turn 模式、热词策略、最终文本如何进入消息系统。
- `IAsrProvider`：面向厂商协议，封装 WebSocket、音频帧上传、VAD/manual commit、错误映射、连接关闭。
- `VoiceRecognitionRequest`：包含 `workspaceId`、`roomId`、`participantId`、`sessionId`、`model`、`language`、`audioFormat`、`sampleRate`、`turnMode`、`traceId`，不包含 API Key。
- `VoiceAudioFrame`：浏览器或客户端采集后的音频帧，包含 `sequence`、`format`、`sampleRate`、`durationMs`，不包含设备指纹等不必要信息。
- `VoiceRecognitionStreamEvent`：标准化 `speech_started`、`speech_stopped`、`transcript`、`completed`、`failed`，不向 UI 暴露厂商原始 JSON。
- `VoiceRecognitionResult`：最终可转成 `MessageEnvelope` 的文本结果，必须带 `metadata.inputMode=voice` 和 ASR provider/model/session 信息。

基于当前 DashScope 实时 ASR 文档，需要支持两类协议风格：

- Fun-ASR / Paraformer：WebSocket duplex，客户端发送 `run-task`，服务端 `task-started` 后上传二进制音频帧，完成后发送 `finish-task`，服务端返回 `task-finished`。连接可复用，但必须等 `task-finished` 后使用新的 `task_id` 开启下一次任务；任务失败后连接不可复用。
- Qwen-ASR Realtime：WebSocket realtime session，客户端发送 `session.update`，持续发送 `input_audio_buffer.append`，服务端返回转写事件；VAD 模式由服务端判断断句，Manual 模式由客户端发送 `input_audio_buffer.commit`。会话结束后需要 `session.finish`，当前不按 Fun-ASR 的方式复用连接。

Turn / VAD 策略：

- `server_vad`：默认适合对话、会议、智能助手；服务端根据静音阈值产生 final transcript。
- `manual`：适合“按住说话/松开发送”的聊天软件体验，客户端显式 commit。
- Qwen-ASR 的 `silence_duration_ms` 与 Fun-ASR / Paraformer 的 `max_sentence_silence` 是同类语义但字段不同，必须由 provider 映射，UI 不直接依赖厂商字段。
- 快速对话场景可以将静音阈值降到约 400ms，但应允许配置，避免打断长句。

识别增强能力：

- 热词：适合品牌名、人名、项目名、命令词；应由 workspace/agent 配置注入 provider，而不是让用户每次输入。
- 时间戳：Fun-ASR / Paraformer 可输出句级/字级时间戳，适合字幕、回放、高亮；Qwen-ASR realtime 当前不作为时间戳主路径。
- 情绪：Qwen-ASR 和部分 Paraformer 可输出情绪；情绪只能作为 UI/Agent 辅助信号，不能单独作为高风险自动化决策依据。
- 非人声过滤、自动语种检测、方言识别应在 `VoiceRecognitionRequest` 中表达为能力开关或 provider profile。

安全和体验约束：

- 麦克风权限必须由用户显式触发；默认不后台录音。
- UI 必须显示权限请求、就绪、录音、转写中、待确认、发送中、识别失败、已发送等真实 session 状态。
- `VoiceCaptureSession` 前端状态只保留权限、设备标签、本地计数、转写文本、provider/model/language/emotion 等投影字段；不得把 `VoiceAudioFrame` 原始字节留在 React state 或消息草稿中。
- Web 首阶段可以使用 `BrowserVoiceInputAdapter` 通过用户点击触发 `getUserMedia` 与浏览器 Speech Recognition，把 interim/final transcript 回填 Composer；该适配器必须可被后端 ASR/WebSocket provider 替换，且默认不自动发送最终文本。
- 音频帧应尽量短生命周期处理；默认不持久化原始音频，除非用户明确开启录音保存或审计。
- API Key 只允许存在于后端 KeyVault/环境变量。
- ASR 回调线程不能做重业务；应快速写入投影/队列，由消息系统或 worker 消费。
- 长连接需要心跳、重连、超时和 backpressure；网络抖动不能造成 UI 假死或重复发送消息。
- 最终 transcript 进入消息系统前必须可配置“自动发送/用户确认/编辑后发送”策略。

#### 4.3.2 Voice Output / TTS

语音输出是消息投递的一种表现形式，不应由 Chat UI 直接调用厂商 API。后端需要建立独立的 TTS Provider 边界：

```text
MessageDelivery(target=user, contentType=text/audio)
  -> VoiceSynthesisPolicy
  -> IVoiceSynthesisService
  -> ITtsProvider(DashScope / local / other)
  -> AudioArtifact(url / cached file / pcm stream)
  -> VoicePlaybackSessionProjection
  -> Web / Mobile / CLI playback
```

Provider 侧建议抽象：

- `IVoiceSynthesisService`：面向业务，决定是否合成、使用哪个 provider/profile、是否缓存。
- `ITtsProvider`：面向厂商协议，封装 HTTP/SSE、鉴权、重试、限流、错误映射。
- `VoiceSynthesisRequest`：包含 `messageId`、`deliveryId`、`text`、`languageType`、`voice`、`instructions`、`outputMode`、`traceId`。
- `VoiceSynthesisResult`：包含 `audioArtifactId`、`audioUrl`、`expiresAt`、`format`、`sampleRate`、`durationMs`、`provider`、`model`。
- `VoiceSynthesisChunk`：流式输出的 PCM/音频块，不直接暴露厂商原始响应结构给 UI。

基于当前 DashScope 文档，首个 provider 可以支持两种模式：

- 非实时 HTTP TTS：适合长文本、课程配音、有声书、内容生产；返回音频 URL，URL 有效期约 24 小时。Pudding 如果需要长期回放，应将音频复制到本地 artifact/cache，而不是长期依赖厂商临时 URL。
- 流式 TTS：适合聊天回复的低等待感播放；通过 SSE/分段响应返回 Base64 PCM 块，最终也可能给出完整音频 URL。前端只消费 `VoicePlaybackSessionProjection` 和音频流，不感知 `X-DashScope-SSE`、Base64 解码等厂商细节。
- 实时 WebSocket TTS：适合语音助手、智能客服、多 Agent 房间播报等低延迟场景；Provider 负责维护双向流、文本缓冲、音频增量、连接关闭和错误恢复。UI 不直接持有 WebSocket 连接。

实时 WebSocket 需要额外建模：

- `VoiceSynthesisTransports.WebSocket`：区别于 HTTP URL 和 SSE chunk。
- `VoiceSynthesisOutputModes.RealtimeDuplex`：表示文本流入和音频流出同时存在。
- `VoiceSynthesisSessionModes.ServerCommit`：服务端判断分段和提交时机，适合连续文本和低操作成本播报。
- `VoiceSynthesisSessionModes.Commit`：客户端显式提交文本缓冲，适合对话轮次、新闻播报、需要精确断句的 Agent 回复。
- `VoiceSynthesisStreamEvent`：把 `session.created`、`response.audio.delta`、`response.done`、`session.finished`、`task-started`、`task-finished` 等厂商事件归一为 `session_started`、`audio_delta`、`response_done`、`session_finished`、`failed`。
- `audio_delta` 向上层暴露解码后的 `byte[]/ArrayBuffer` 和 format/sampleRate，不把 Base64、厂商 JSON、Authorization、API Key 暴露给 UI。

连接复用和并发策略：

- WebSocket 可复用，但必须在服务端返回 `task-finished` 或 `session.finished` 后才可开启下一次任务。
- CosyVoice / Sambert 复用连接时，新任务必须使用新的 `task_id`。
- Qwen-TTS Realtime 结束后需要新 session，不能在未结束的 session 内强行开始下一轮。
- 任务失败后该连接不可复用；Provider 必须主动关闭或废弃连接/对象。
- 连接空闲超过 provider 限制会被断开，DashScope 当前文档给出的典型值约 60 秒；Projection 应将断开视为可恢复状态，而不是 UI 崩溃。
- 高并发场景必须有 provider 级对象池/连接池和 backpressure，池大小不得盲目拉满，应受账号 QPS、机器资源和事件队列压力约束。
- 启动阶段需要预热或渐进提升并发，避免大量 WebSocket 同时建连导致尖刺、阻塞和首包延迟异常。
- 音频回调线程不能执行阻塞业务逻辑；应快速写入内部 buffer，再由播放/缓存/投影 worker 消费。
- 首包延迟需要拆分观测：SDK 侧指标可能包含 TCP/TLS/WebSocket 建联时间；服务端实际首包延迟应单独记录。Pudding 的可观测字段至少包括 `firstAudioDelayMs`、`connectionReused`、`transport`、`sessionMode`、`providerRequestId`。

安全和体验约束：

- API Key 只允许存在于后端 KeyVault/环境变量，不进入前端 bundle、消息 payload 或 browser storage。
- 声音复刻、声音设计、情感化音色属于用户可识别声音能力，必须要求显式授权、配置记录和审计日志。
- TTS 文本可能包含隐私数据；合成策略需要支持禁用、脱敏或本地 provider 优先。
- `VoicePlaybackSession` 状态至少覆盖 `idle`、`queued`、`synthesizing`、`buffering`、`playing`、`paused`、`completed`、`failed`、`expired`。
- UI 只显示真实 session/projection 状态，不在没有音频 artifact 或流式块时伪造“正在说话”。
- Web 首阶段可以使用 `BrowserVoiceOutputAdapter` 调用浏览器 `speechSynthesis` 朗读 assistant 文本，作为低成本可用性入口；它必须是可替换 adapter，不能代替后端 `IVoiceSynthesisService` / `ITtsProvider` 的正式合成链路。
- Assistant 消息的朗读控件必须是低干扰消息动作：用户显式点击后播放，可停止；默认不自动朗读、不把 TTS 文本或音频状态写回消息正文。

### 4.3.5 Realtime Omni Interaction

Qwen-Omni-Realtime 类模型把 ASR、视觉帧理解、LLM 回复和 TTS 音频输出合并到一个实时会话。它不是简单的 ASR + TTS 串联：同一连接内会同时出现用户语音转录、输入图像帧、模型文本增量、模型音频增量、打断和 `response.done` usage。因此 Pudding 需要一个统一的实时多模态会话层：

```text
Browser Microphone + Camera
  -> OmniRealtimeInputFrame(audio / image / video_frame)
  -> IOmniRealtimeService
  -> IOmniRealtimeProvider(DashScope WebSocket / WebRTC / local)
  -> OmniRealtimeStreamEvent
       - input_transcript_delta / input_transcript_completed
       - response_text_delta
       - response_audio_transcript_delta
       - response_audio_delta
       - response_done
  -> voice / vision / avatar / chat projections
```

当前 Core 已落地以下基础契约：

- `OmniRealtimeSessionRequest`：描述一个实时多模态会话，包含 workspace/room/participant/session、provider/model、WebSocket 或 WebRTC transport、输出模态、音色、输入/输出 PCM 格式、VAD/manual 模式、输入转录模型、联网搜索开关和 trace；不包含 API Key 或 Authorization。
- `OmniRealtimeInputFrame`：统一表示音频、图片或视频抽帧输入；音频默认 16 kHz PCM，输出音频默认 24 kHz PCM；图片帧只作为瞬时 transport 输入，不能作为长期 React state 或聊天 metadata 保存。
- `OmniRealtimeStreamEvent`：统一表示 `conversation.item.input_audio_transcription.*`、`response.text.*`、`response.audio_transcript.*`、`response.audio.*`、`response.done` 等服务端事件，向 UI 暴露解码后的文本/audio bytes/usage，不暴露 provider raw JSON。
- `IOmniRealtimeService` / `IOmniRealtimeProvider`：把业务策略和厂商协议分开；Provider 负责 `session.update`、`input_audio_buffer.append`、`input_image_buffer.append`、`input_audio_buffer.commit`、`response.create/cancel` 和事件读取。
- `DashScopeOmniRealtimeEventMapper`：已覆盖 DashScope Qwen-Omni-Realtime 的关键 WebSocket 事件，将 `conversation.item.input_audio_transcription.delta` 映射为输入转录增量，将 `response.audio_transcript.delta` / `response.text.delta` 映射为助手文本增量，将 `response.audio.delta` 解码为音频字节，将 `response.done` 中的 usage 标准化。
- `ChatOmniRealtimeSessionRunner`：把实时多模态流投影回现有聊天 SSE 协议。输入转录增量写入 `voice_capture_status`，助手文本写入 `delta`，输出音频块写入 `voice_playback_status`，完成时写 `done`，失败时写 `voice_playback_status(status=failed)` 和 `error`；所有帧补齐同一个 `messageId`、chat `sessionId` 和 `omniSessionId`，因此 Chat timeline、VoicePlayback、Avatar runtime 可以消费同一条会话流。

接入策略：

- 浏览器低延迟实时语音优先 WebRTC，但 API Key 和 SDP signaling 必须由后端代理，不能让前端直接持有百炼 API Key。
- 服务端集成和可控审计优先 WebSocket；浏览器采集的音频/图像帧通过 Pudding 后端网关转发，便于权限、审计、限流、录制策略和事件投影。
- WebRTC 只支持服务端 VAD；Manual 按住说话模式应走 WebSocket 或由后端做显式 commit/response.create。
- 视频输入按抽帧策略进入，默认建议约 1 fps，并受模型视频上下文时长和 token 成本约束。
- 联网搜索与工具调用互斥；会话策略层必须在启用 `enable_search` 或 function calling 前做冲突检查。

### 4.4 Vision Interaction

摄像头是 Agent 的视觉输入，但必须是用户显式授权和可见控制的输入模式：

```text
Browser Camera
  -> CameraCaptureSession
  -> local preview
  -> user selects mode:
       - snapshot
       - periodic sampling
       - object / screen observation task
  -> VisionArtifactUpload API
  -> VisionFrame artifact (server generated artifactId)
  -> IVisualReasoningService
  -> IVisualReasoningProvider(DashScope / local / other)
  -> VisualReasoningStreamEvent(reasoning_delta / answer_delta)
  -> ChatVisualReasoningSessionRunner
     -> existing chat SSE(metadata / thinking / delta / done / error)
  -> MessageEnvelope(contentType=vision, metadata.inputMode=camera)
     or ToolInput for vision-capable model/tool
  -> IMessageSystem / Event System
```

Provider 侧建议抽象：

- `IVisualReasoningService`：面向业务，负责摄像头/截图 artifact 授权、模型选择、是否开启思考、推理预算、最终答案如何进入消息系统或工具链。
- `IVisualReasoningProvider`：面向厂商协议，封装 OpenAI-compatible SSE、DashScope SSE、`reasoning_content` 解析、usage 统计、错误映射。
- `VisualReasoningRequest`：包含 `workspaceId`、`roomId`、`participantId`、`sessionId`、`model`、`prompt`、`inputs`、`enableThinking`、`thinkingBudgetTokens`、`traceId`，不包含 API Key。
- `VisualInputArtifact`：只引用已授权的 `artifactId` / URL / MIME / 尺寸 / 捕获时间；不把摄像头原始帧字节常驻在业务请求对象中。
- `VisualReasoningStreamEvent`：标准化 `reasoning_delta` 与 `answer_delta`，把视觉推理的思考过程和最终回答分开投影；不向 UI 暴露厂商原始 JSON。
- `VisualReasoningResult`：包含最终 `answer`、可选 `reasoningSummary`、provider/model/requestId、token usage 和 `metadata.inputMode=vision`。
- 当前 Platform 已落地 `VisionArtifactStorageService` 和 `/api/workspaces/{workspaceId}/vision-artifacts`：浏览器摄像头上传图片字节，后端生成 `visionArtifactId`、保存 metadata，并在视觉模型调用前解析为受控 data URI。客户端传入的 `visionArtifactUri` 一律不可信，不参与 provider 请求。
- 当前 Core 已落地 `DefaultVisualReasoningService`：按显式 provider 或 model capability 选择 `IVisualReasoningProvider`，在厂商调用前校验 workspace/room/participant/session/prompt/input，并拒绝尚未解析为受控 URI 的 `VisualInputArtifact`。这使摄像头 artifact 授权、持久化、URI 签发保持在 Platform/Application 层，Core service 只接受可安全调用 provider 的引用。
- 当前 Platform 已落地 `ChatVisualReasoningSessionRunner`：把视觉推理流投影回现有聊天 SSE 协议，`reasoning_delta` 映射为 `thinking`，`answer_delta` 映射为 `delta`，完成时写 `done`，失败时写 `error`；所有帧补齐同一个 `messageId/sessionId`，因此前端可以复用既有聊天 timeline、思考区、loading 终止逻辑，而不需要为摄像头推理另建并行通道。
- 当前 Chat API 已把 `metadata.inputMode=camera` 分流到视觉推理 runner；普通文本仍走原 Runtime 文本通道。平台转发 runtime ingress metadata 时会保留前端的 `inputMode`、`voiceSessionId`、`cameraSessionId` 等输入模式字段，再由服务端覆盖 agent/source/fanout 字段，避免客户端伪造来源。

基于当前 DashScope 视觉推理文档，需要支持三类模型行为：

- 混合思考模型：如 Qwen3.6、Qwen3.5、Qwen3-VL、Kimi 系列，可通过 `enable_thinking` 控制是否输出思考过程；`thinking_budget` 可限制思考 token。
- 仅思考模型：如 QVQ 和 `*-thinking` 系列，总会先输出思考过程，不能关闭；策略层必须把它标为 `ThinkingMode=AlwaysOn`。
- QVQ：当前仅支持流式输出；Provider capability 必须标记 `RequiresStreaming=true`，避免 UI 或 worker 走非流式路径导致超时或协议错误。
- 当前 Core 已落地 `DashScopeVisualReasoningProvider` 的 OpenAI-compatible SSE 最小实现：请求体使用多模态 `messages[].content[]`，流式解析 `delta.reasoning_content` 与 `delta.content`，输出标准化 `VisualReasoningStreamEvent`，并在 `AnalyzeAsync` 中聚合 `answer`、`reasoningSummary` 与 usage；此实现仍要求上层先把 `VisualInputArtifact` 解析成受控 URI，不能直接把摄像头原始帧或 API Key 放入请求对象。

视觉交互必须支持：

- 权限前置说明。
- 明确的摄像头开启指示。
- 本地预览。
- 一键暂停和关闭。
- 快照前确认。
- 采样频率可见且可调。
- 默认不后台采集。
- 所有视觉帧带 `traceId/sessionId/messageId`，可追溯到用户操作。
- 复杂视觉任务应在 UI 中区分“正在推理/思考过程/最终答案”，并允许产品策略决定是否默认折叠思考过程。
- `CameraCaptureSession` 前端状态至少覆盖 `requesting_permission`、`ready`、`previewing`、`capturing`、`sampling`、`paused`、`awaiting_confirmation`、`sending`、`completed`、`cancelled`、`failed`。
- `CameraCaptureSession` 只保留权限、设备标签、预览尺寸、采样频率、本地计数、最新 `artifactId`、MIME、捕获时间和 prompt；不得把摄像头原始帧字节留在 React state 或消息草稿中。
- 相机来源消息进入 Message Fabric 时必须带 `metadata.inputMode=camera`、`cameraSessionId`、`visionArtifactId`，尺寸等数值元数据应以字符串进入前端消息 metadata，避免跨 JS/C# 精度和序列化差异。
- 当前前端 `AdminChatRequest` 已允许携带 `metadata`，`sendMessage(text, { metadata })` 会把 camera draft 的 `inputMode/cameraSessionId/visionArtifactId` 送到 Platform。Platform 已落地 `ChatVisualReasoningRequestFactory`，它只信任 `visionArtifactId`，通过 `IVisualArtifactReferenceResolver` 签发受控 URI；即使前端传入 `visionArtifactUri`，也会被忽略，避免把客户端提供的任意 URL 直接交给视觉模型。

视觉能力不等于持续监控。默认策略应是“用户显式触发的一次性快照”，之后再提供可控的周期采样。

### 4.5 Agent Avatar / Virtual Companion

Agent 虚拟形象是 Agent 状态和多模态交互的投影层。参考 `live2d-widget` 的价值点：网页内轻量展示 Live2D 角色、TypeScript 核心易集成、支持静态模型资源配置。但需要注意：

- 该项目本身不包含模型资源，模型需要单独配置。
- Cubism SDK 和模型资源存在独立许可边界。
- GPL 代码不能直接混入产品核心前端而不做法律和发布策略评估。

因此本项目采用抽象层，而不是直接绑定某个 widget 实现：

```text
IAgentAvatarRuntime
  - SpriteAvatarRuntime
  - Live2DAvatarRuntime
  - StaticAvatarRuntime
```

Avatar 状态来自 `AvatarStateProjection`：

```text
RoomParticipant status
MessageDelivery status
ExecutionTrace status
VoiceSession status
VisionSession status
        |
        v
AvatarStateProjection
        |
        v
Avatar Runtime animation / expression / pose
```

前端 `AgentAvatarRuntime` 是 projection consumer，不直接读取 provider 原始事件：

- 输入事件来自 voice/camera/vision/tool/message 的标准投影，例如 `voice_capture_status`、`voice_playback_status`、`camera_capture_status`、`visual_reasoning_status`、`tool_status`。
- 运行状态至少覆盖 `idle`、`listening`、`seeing`、`thinking`、`speaking`、`tool`、`error`、`sleeping`。
- 输出 `AvatarRenderState`，包含 `runtimeKind`、`status`、`expression`、`motion`、`visible`、sprite row/frame 或 Live2D/static URL，以及可访问的 `ariaLabel`。
- `error` 状态优先级最高；`prefers-reduced-motion` 或用户关闭动效时，sprite 仍显示状态但动画帧数降为 1。
- Avatar 可隐藏；隐藏不应丢失已配置的 `avatarId`、sprite sheet、Live2D model 或静态头像 URL。
- Avatar 不保存音频字节、摄像头帧字节、厂商 reasoning 原始 JSON；它只保存 session/delivery/artifact/tool id 等可追踪引用。
- `AgentAvatarRuntimeView` 是紧凑工作台组件：固定尺寸、显示 agent 名称和短状态文本、提供“隐藏虚拟形象”控制；不得遮挡输入区、消息时间线、错误提示或多模态控制。
- `ChatMain` 必须将当前选中 Agent 的虚拟形象作为主交互界面的稳定锚点渲染；状态来自 assistant response / voice / camera / vision / tool 投影，而不是从消息文本或厂商原始事件中推断。
- Sprite 第一版用 self-hosted sprite sheet、`spriteRow`、`spriteFrameCount` 渲染；Static 头像用普通 `img`；Live2D 只在 runtime 抽象允许时 lazy-load，不作为首屏强依赖。

第一阶段建议先做 sprite/pet 形象，因为：

- 可控、轻量、许可简单。
- 能表达 idle/listening/seeing/thinking/speaking/tool/error/sleeping。
- 不依赖外部 CDN 和 Live2D SDK。
- 后续可在同一 runtime interface 下替换为 Live2D。

Live2D 作为第二阶段实验能力：

- 所有 runtime 和模型放在独立 `AvatarRuntime` 包或 lazy-loaded chunk。
- 不使用第三方 CDN；模型和 runtime 自托管。
- 不让 avatar 覆盖核心输入、时间线、错误提示。
- 支持关闭、静音、降低动效。
- 遵守 `prefers-reduced-motion`。

### 4.6 Multi-Agent Room Semantics

多 Agent 不是多个单聊并排。房间内应有：

- 参与者 presence：available / busy / sleeping / disabled。
- 消息目标：to user / to agent / to room / to all。
- 可见性：public / private / system。
- 投递状态：queued / delivering / delivered / failed / acked。
- Agent 回合：thinking / tool / memory / answer。

UI 只展示对用户有意义的投影。内部事件细节默认折叠到 Inspector。

---

## 5. Design Language

采用“Quiet Local Intelligence”：

- 安静、本地、可信、克制。
- 暖纸面背景、深墨文本、低饱和紫色作为信号色。
- 主界面是工作台，不是营销页。
- 动效用于降低不确定性，不用于炫技。
- Agent 运行状态用紧凑状态行、弱色点、投递标记表达。

全局 UI token 应收敛为三层：

```text
Base tokens
  -> Admin tokens
  -> Chat / Room / Voice semantic tokens
```

禁止新组件继续直接散落旧色值和旧变量。Chat 长期只消费：

- `--pudding-chat-*`
- `--pudding-admin-*`
- AntD token 映射后的语义 token

---

## 6. Interaction State Model

每个用户可见操作应进入统一状态机：

```text
idle
  -> composing
  -> submitting
  -> queued
  -> delivering
  -> agent_thinking
  -> tool_executing
  -> streaming
  -> completed
  -> failed / cancelled
```

这个状态机来自消息 delivery 和 execution trace，而不是每个 React 组件自己推断。

短期可以保留前端推断，但要逐步迁移到后端投影。

---

## 7. Implementation Plan

### Phase 1：UX Architecture Foundation

- 新增本 ADR，统一交互系统目标。
- 修复假语音状态，避免未接入能力误导用户。
- 梳理 Chat token 残留，建立迁移清单。
- 明确 Chat 是 room client，不是单 Agent 控制器。

### Phase 2：Room Workbench

- 左侧从 session list 逐步演进为 room/conversation list。
- 顶部从单 Agent selector 演进为 room header + participant summary。
- 中央 timeline 支持 user / agent / connector / system note。
- 右侧 Inspector 展示 participants、delivery、runtime trace。

### Phase 3：Voice Pipeline

- `VoiceCaptureSession` 前端状态。
- ASR adapter 接口。
- 转写确认 UI。
- `metadata.inputMode=voice` 进入 Message Fabric。
- TTS/Agent audio playback 作为 delivery egress。

### Phase 4：Vision Pipeline

- `CameraCaptureSession` 前端状态。
- 摄像头权限、预览、暂停、关闭。
- 快照 artifact 和采样策略。
- `metadata.inputMode=camera` 进入 Message Fabric。
- 视觉模型或工具调用通过事件系统调度。
- 用户能在 timeline 中看到 Agent 使用了哪次视觉输入。

### Phase 5：Avatar Runtime

- 定义 `IAgentAvatarRuntime` 前端接口。
- 第一阶段实现 sprite runtime。
- 将 idle/listening/seeing/thinking/speaking/tool/error/sleeping 状态接入 projection。
- 第二阶段评估 Live2D runtime：许可、打包体积、模型来源、动效性能。
- Avatar 必须可关闭，且不得遮挡核心操作。

### Phase 6：Interaction Projection Layer

- 后端提供 room timeline projection。
- 后端提供 participant presence projection。
- 后端提供 delivery status projection。
- 后端或前端状态层提供 voice/vision/avatar projection。
- 前端不再直接从低层 SSE frame 拼产品状态。

### Phase 7：Cross-App UI Unification

- Chat、Workspace、Memory Library、Agent Settings、Diagnostics 共享 token 和 shell。
- 静态扫描禁止 Ant Pro 模板痕迹和旧 token 回流。
- Playwright 截图覆盖 light/dark、desktop/mobile。

---

## 8. Acceptance Criteria

1. Chat 空状态、输入区、消息区、状态区、管理页共享 Pudding 设计语言。
2. 未接入语音时不出现假录音、假播放、假波形。
3. 语音接入后所有语音消息仍进入 `IMessageSystem`，不绕过消息系统。
4. 摄像头接入必须显式授权、可预览、可关闭，默认不后台采样。
5. 视觉输入必须以 message/artifact/tool input 进入系统，并可追溯。
6. Agent 虚拟形象状态来自 projection，不维护独立业务状态。
7. 用户和 Agent 都以 room participant 身份出现。
8. 前端不决定 `@all` 最终目标集合。
9. UI 消费 room/message/execution/voice/vision/avatar 投影，不直接依赖内部事件细节。
10. 流式输出不导致历史消息重绘、布局跳动或滚动抢夺。
11. 所有 icon-only 控件有 `aria-label` 和 tooltip。
12. 暗色/亮色主题均通过 token 一致渲染。
13. 主要工作流在 375、768、1024、1440 宽度可用。

---

## 9. Immediate Fixes

- `VoiceInputButton` 在语音管线接入前改为禁用且可访问的“语音输入待接入”，不再模拟 recording / playing。
- 新增 `CameraCaptureSession`、`VoiceCaptureSession`、`AgentAvatarRuntime` 前，不允许在 UI 中展示假的“正在看/正在听/正在说”状态。
- 后续应删除或隔离 `ParticleSphere`、`GlobeSphere`、`AmbientParticles` 这类不符合 Chat 空状态 North Star 的旧装饰组件，除非用于明确的实验页。
- `styles.ts` 中旧 token 使用量较大，应作为专项收敛，不在功能改造中继续扩大。

---

## 10. External Reference Notes

`stevenjoezhang/live2d-widget` 适合作为“网页虚拟形象组件”的参考，但不是直接依赖结论。公开说明显示它可在网页中添加 Live2D widget、核心代码为 TypeScript、除 Live2D Cubism Core 外轻量；同时仓库不包含模型资源，模型和 Cubism SDK 需要单独处理并遵守许可。这些约束决定了 Pudding 应先抽象 Avatar Runtime，再决定 sprite 或 Live2D 的具体实现。
