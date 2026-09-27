# 原生聊天语音组件与接入

状态：播放合同、WinRT 播放器与朗读按钮已独立验证，消息卡片/工作台与 Core 进程内适配已装配。麦克风输入、持续语音会话及真实供应商产品验收尚未完成。

## 现有实现与迁移边界

- Web `pages/chat/hooks/useTtsPlayer.ts`：消息文本→`synthesizeTts`→浏览器 Audio；包含合成、播放、停止和新请求替换。
- Web `IntentConsole.tsx`：录音结束后 ASR 回填草稿；旧实现通过 200 ms 定时器等最终转写。原生应等待明确完成回执，不移植这个时间假设。
- Core `VoiceController` 的 TTS 使用 `IVoiceSynthesisService`；ASR 使用 `VoiceProviderFileService` + `IVoiceProviderFactory.CreateAsrProvider`。`IVoiceRecognitionService` 另有流式合同，不能仅凭接口存在认定已装配或可用。
- Core `VoiceSynthesisService` 已处理默认服务商/模型选择、provider 适配、URL 音频物化。Desktop 复用应用服务，提供真实 WorkspaceId/MessageId，不照搬 Web Controller 固定 default 工作空间的参数。

原生调用链：消息按钮 → PuddingChat 播放会话 → IChatSpeechClient → Composition → Core IVoiceSynthesisService；音频返回后由原生 ISpeechAudioPlayer 播放。Desktop/Core 间没有 HTTP；Core 向已配置语音服务商发出的网络请求属于供应商调用。

## 组件与所有权

| 组件 | 所有权/行为 | 状态 |
|---|---|---|
| SpeechRequest / SpeechAudio / IChatSpeechClient | BCL 合同；请求绑定角色、工作空间和消息；音频只交付内存字节与 MIME | 已实现 |
| SpeechPlaybackSession | 一个聊天工作台一条播放通道；Idle→Synthesizing→Playing→Idle/Failed；新请求取消旧请求，代次阻止晚到结果污染 | 已独立验证 |
| ISpeechAudioPlayer / NativeSpeechAudioPlayer | WinRT MediaPlayer 内存音频播放，PlayAsync 在结束后完成；取消/错误/Dispose 释放播放器与流 | 独立真实媒体验证通过 |
| SpeechPlaybackButton | 当前消息按钮显示朗读/取消合成/停止/重试；卸载取消本条朗读但不销毁工作台共享通道 | 已接消息卡片；角色切换、离开聊天与工作台销毁停止播放 |
| Composition 语音适配 | 验证角色/工作空间归属，经既有内核操作生命周期调用合成服务，不返回密钥或供应商 URL | 已实现；按消息 ID 读取权威正文 |
| 原生录音/转写 | 明确点击才启用麦克风；停止后等待 ASR 完成，回填开始录音时绑定角色的草稿，不自动发送 | 待实现 |
| 持续语音会话 | 录音、识别、发送、等待角色回复、播放的会话状态，独立于文本输入与 Core Run 状态 | 待盘点 Web 完整行为后实现 |

播放会话由 UI 所在线程/同步上下文串行调用；设备事件转为 Task 完成，不直接修改控件。该状态机仅管理本地音频呈现，不复制 Agent 执行状态机。角色切换与 Core 生命周期由宿主显式 Stop/Dispose；控件回收不能留下后台朗读。默认不自动朗读所有流式消息，只有用户点击朗读入口才合成，未来主动语音回复须有单独可见开关。

第一版单条朗读最大 10,000 字符，超限明确拒绝，不截断消息；音频最多 30 MiB，仅 WAV/MP3。供应商异常不把原始异常信息显示给用户；在 UI 给出检查语音设置与重试提示，后续适配层通过既有 Core 日志保留诊断。内存音频不写 D:\data，不建立新的语音 HTTP 接口。

## 按组件交付

1. **已完成**：PuddingChat 独立播放合同/状态组件，67 项逻辑测试通过，其中新增 7 项覆盖自然播放结束、新请求替换、忽略取消的晚到合成、停止设备、迟到设备错误、合成失败与重试、无效音频、销毁和无效文本不打断当前请求。测试使用 fake 设备和合成服务，没有启动扬声器、麦克风或付费调用。日志 `temp/native-speech-state.log`。
2. **独立部分已完成**：NativeSpeechAudioPlayer 的静音 WAV 自然结束、取消、无效媒体错误、销毁；SpeechPlaybackButton 的取消合成、停止、失败重试、卸载取消与超长文本禁用。67 逻辑/188 窗口检查通过，日志 temp/native-speech-controls.log。角色切换与 Core 停止仍需产品装配后验证，不能以控件卸载测试代替。
3. **已装配**：Composition 直接调用 Core IVoiceSynthesisService；原生 MessageCard/ChatWorkspace 共用播放通道。隔离测试使用真实消息/会话仓储与替身合成服务，检验消息归属、默认配置委托和内核停止取消；不代表真实供应商验收。
4. 录音/ASR 使用独立采集与转写合同；验证拒绝麦克风、无设备、取消、切换角色和迟到转写不覆盖新草稿。实际麦克风与付费服务验收须在明确用户操作下执行。
5. 持续语音工作台与完整产品验收：不能把朗读完成当作全部语音聊天完成。

原生窗口验证发现取消可同步恢复调用方：SpeechPlaybackSession.Stop 先发布 Idle，再取消当前令牌，避免回收完成回执已到、状态却仍为 Synthesizing 的暂态；BCL 测试固定取消回调观察到的状态。播放器与流初始化失败也由外层 finally 清理操作归属，避免后续播放误用已释放的 CancellationTokenSource。

## 消息朗读进程内接入（2026-09-27）

已完成的角色回复（agent/assistant）显示原生朗读按钮。ChatWorkspace 拥有共享播放通道，切换角色、离开聊天和销毁工作台停止播放；虚拟列表回收按钮也取消所属播放。Composition 按消息 ID 读取已保存正文，验证工作空间、会话角色及本机用户归属，提取 canonical envelope 的人类正文而不朗读封装 JSON，再直接调用 IVoiceSynthesisService。供应商/模型由既有 Core 默认配置解析；没有新增 Desktop HTTP 路由。

原生验证 67 项逻辑测试、192 项窗口检查通过，零构建警告/错误，日志 temp/native-speech-wiring-ui.log。测试查找朗读入口时排除 ItemsRepeater 尚留在视觉树中的已回收禁用控件；真实 Core 的 agent 投影已纳入 fixture，避免仅使用 assistant 测试漏掉入口。Core 集成 3 项测试通过（构建仍有既有依赖/代码警告），证据见 temp/native-speech-wiring-core.log；没有使用 D:\data、麦克风或付费供应商。录音/ASR、持续语音和真实供应商的产品验收仍待完成。

## 录音/转写状态组件（2026-09-27）

已独立实现 `PuddingChat/VoiceInput.cs`：IVoiceCapture/IVoiceRecording 设备端口、IChatTranscriptionClient 进程内转写端口、VoiceInputSession 及 VoiceDraftAnchor/VoiceDraftResult。状态依次为 Opening → Recording → Finalizing → Transcribing → Completed，取消进入 Cancelling 并等设备释放，失败保留可重试提示。停止录音是完成信号，等待最终 WAV 后释放设备，再开始 ASR；不移植 Web IntentConsole 的 200 ms 延时。设备端口要求内存 WAV、最多 8 MiB/2 分钟，状态层到时请求收尾，原生采集实现还必须在设备边界限制内存与时长。

录音开始绑定角色、选择代次和原始草稿。返回的转写文本不直接修改草稿、不发送消息；TryAppendTo 仅在角色/代次/草稿都未变化时追加。拒绝追加时文本仍可供 UI 显式插入。取消转写可立即放弃等待，迟到供应商结果不再发布；设备打开或收尾即使忽略取消，也必须等获得/释放句柄后才能开始下一次录音，重复 DisposeAsync 同样等待释放。

独立逻辑测试总计 **77 项通过**，新增 10 项覆盖完整收尾、设备先释放后转写、打开期间取消及迟到句柄、录音销毁、迟到 ASR、草稿编辑/角色切换、权限失败与重试、无效音频/空结果、收尾取消与重复释放等待、预取消不打开麦克风。日志 `temp/native-voice-input-state.log`，构建无警告/错误。测试全用替身设备与转写服务；**尚未实现 WinRT 麦克风采集、原生录音按钮或 Core ASR 装配，不能认定产品语音输入已可用。** 下一步按独立组件顺序完成设备与输入控件，再接 Composition/Core。

## 原生录音控件（2026-09-27）

`PuddingChat.WinUI/VoiceInputControl.cs` 已独立实现：语音输入/结束录音、取消录音/转写、打开设备/收尾/转写/取消提示、失败重录、可选中文本预览与“加入草稿”。只有显式确认才请求插入，不自动发送；宿主回调拒绝已变化的角色/草稿时保留文本并提示复制粘贴，成功后禁用重复插入。Loaded/Unloaded 管理订阅，卸载与 Dispose 取消当前录音，工作台仍负责异步释放会话。

77 项逻辑测试和 **199 项真实 WinUI 窗口检查通过**，零构建警告/错误，日志 `temp/native-voice-input-control.log`。新增 7 项控件检查覆盖录音标签、设备释放后转写、预览不改草稿、冲突提示、显式插入一次且不发送、失败重录、卸载取消。设备及 ASR 使用替身，未打开真实麦克风；当前控件尚未装入 ChatComposer/ChatWorkspace。下一步为 WinRT 采集组件、Core ASR 直接调用适配，再完成工作台装配；独立控件通过不等于产品语音输入已可用。
