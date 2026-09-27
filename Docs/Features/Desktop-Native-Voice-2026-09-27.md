# 原生聊天语音组件与接入

状态：播放合同、WinRT 播放器与朗读按钮已独立验证，消息卡片/工作台与 Core 进程内适配已装配。基础录音/转写入口已装配；实际麦克风、真实供应商及持续语音会话验收尚未完成。

## 现有实现与迁移边界

- Web `pages/chat/hooks/useTtsPlayer.ts`：消息文本→`synthesizeTts`→浏览器 Audio；包含合成、播放、停止和新请求替换。
- Web `IntentConsole.tsx`：录音结束后 ASR 回填草稿；旧实现通过 200 ms 定时器等最终转写。原生应等待明确完成回执，不移植这个时间假设。
- Core `VoiceController` 的 TTS 使用 `IVoiceSynthesisService`；旧 ASR Controller 使用 `VoiceProviderFileService` + `IVoiceProviderFactory.CreateAsrProvider`。Desktop ASR 复用已存在的 `IAudioTranscriptionService`，统一默认模型选择与结果校验。`IVoiceRecognitionService` 另有流式合同，不能仅凭接口存在认定已装配或可用。
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
| 原生录音/转写 | 明确点击才启用麦克风；停止后等待 ASR 完成，确认后加入开始录音时绑定角色的草稿，不自动发送 | 已装配；真实麦克风与供应商验收待完成 |
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

## Core ASR 进程内适配（2026-09-27）

`InProcessChatClient.Transcription.cs` 实现 IChatTranscriptionClient：验证录音大小与角色是否存在/启用/冻结，再调用既有 IAudioTranscriptionService，格式为 WAV，供应商和模型保持未指定以使用 Core 配置。调用沿用内核操作跟踪、DI scope 和停止令牌；不创建 Controller/HTTP 路由，也不在 Desktop 复制默认模型选择。Core 到 ASR 供应商的网络调用与 Desktop/Core 之间的传输是两回事。

真实 Host 集成套件 **3 项通过**，日志 `temp/native-transcription-core.log`（构建仍有既有警告）。新增用例路径组合真实 WorkspaceAgentFileService、真实 AudioTranscriptionService、隔离 VoiceProviderFileService 与替身 Provider，覆盖无默认配置、默认模型解析、角色/工作区不存在、空音频、空识别结果、内核停止取消和零 Desktop 聊天 HTTP。没有访问 D:\data 或真实供应商。WinRT 麦克风采集及聊天工作台装配仍未完成。

后续采集实现依据 Microsoft [MediaCapture](https://learn.microsoft.com/uwp/api/windows.media.capture.mediacapture) 与 [StartRecordToStreamAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.mediacapture.startrecordtostreamasync)：初始化在 UI/STA 上执行，显式用户操作才请求麦克风，采用随机访问内存流并在退出/取消时释放。Firecrawl Developer keyless 查询不可用，本次使用微软官方文档核对；设备权限与解包桌面实际行为仍需原生适配及产品验证，不能以查阅文档代替验收。

## WinRT 音频采集组件（2026-09-27）

`PuddingChat.WinUI/NativeVoiceCapture.cs` 实现 IVoiceCapture/IVoiceRecording。显式 OpenAsync 才在 UI/STA 线程初始化音频专用 MediaCapture；16 kHz、单声道、16-bit PCM 写入 WAV 内存流。底层固定 8 MiB 的不可扩容 MemoryStream，通过 AsRandomAccessStream 交给 WinRT，编码器越界写会失败而不继续扩容。2 分钟定时器或设备失败触发停止；Finish 与 Dispose 共享一次停止任务，停止后释放 MediaCapture，Dispose 再释放流；设备错误即使与结束录音相邻发生也不能交付成功结果。

独立检查使用真实 WinRT 编码配置及流适配器：PCM 参数/无视频、WAV 字节写入不变、原生异步越界写拒绝、预取消不初始化设备。共 **77 逻辑/203 窗口检查通过**，零构建警告/错误，日志 `temp/native-voice-capture.log`。这不是实际麦克风录音验收，未调用 InitializeAsync 打开真实设备；设备权限、拔出、驱动行为和音频质量仍须用户显式操作验证。控件/设备/ASR 适配现已分别存在，下一步装配 ChatComposer/ChatWorkspace，并验证切换角色、离开聊天和宿主关闭的设备释放。

## 语音输入工作台装配（2026-09-27）

ChatComposer 增加“语音”入口，通过原生 Flyout 承载录音/转写控件；只有点击控件内“语音输入”才打开麦克风。ChatWorkspace 默认组合 NativeVoiceCapture、VoiceInputSession 与进程内 IChatTranscriptionClient，转写完成后必须显式“加入草稿”，角色/草稿冲突则保留可复制文本，不自动发送。开始录音停止当前朗读；角色/工作区切换、离开页面、控件卸载和销毁均取消录音。无可用角色时禁用入口。

ChatWorkspace 增加 IAsyncDisposable，立即取消后等待设备释放；MainWindow 统一收集旧聊天区释放任务，重建/退出前等待完成，并用挂载代次拒绝异步等待期间失效的重建。输入区在语音入口可见时提前至 560 DIP 分成两行。布局测试显式启用语音入口，覆盖 320/360/520/900 DIP；TextBox 的原生换行规范化在断言中按等价换行比较。

77 逻辑/209 原生窗口检查通过，零组件构建警告/错误，日志 `temp/native-voice-workspace.log`；新增完整工作台的角色绑定、确认追加、角色切换取消、草稿冲突、离开取消和等待设备释放断言。真实 WinUI 控件加替身录音/ASR，未启动真实麦克风或付费供应商；宽/窄/深色截图中已包含语音入口，窄屏截图已检查。完整产品生命周期证据另记，不能以这些组件检查替代实际录音质量或供应商验收。

完整 Desktop 产品隔离生命周期测试通过（PID 45336，报告 temp/test-out/kernel-winui-4fec4db0fdd44982b357ce4256a39f79/report.json）：原生聊天挂载、角色/文件草稿、UI 回调、Core 重启后新建聊天区、数据目录配置保存、退出及锁释放。构建零错误/147 个既有警告，日志 temp/native-voice-product.log；该构建来自共享工作树，不是单独提交的隔离构建。此测试未实际录音，不能代替设备/供应商验收。

## Web 语音会话范围核查（2026-09-27）

源码依据：`Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.tsx` 的 startCapture、sendVoiceMessage、speakLatestAnswer，以及 `hooks/dashScopeVoiceInput.ts`。

- Web 面板状态为 idle/requesting_permission/recording/transcribing/awaiting_confirmation/sending/failed；收音、发送和朗读均由按钮触发。最终转写进入待确认草稿，发送需要明确操作；源码未实现“回复后自动重开麦克风”的循环。
- Web 面板可编辑转写草稿，并有最新回复朗读快捷入口。原生目前将结果确认加入标准输入区后编辑/发送，朗读入口在各条消息上；这覆盖基础用户流程，但不等于所有快捷入口、来源元数据和状态观测均已对齐。
- Web 发送语音消息附 inputMode、voiceSessionId、asrProvider、asrModel、language；当前原生只发送最终文本，这一来源信息仍需真实合同接线，不能照抄旧代码硬编码的 browser/web-speech 供应商信息。
- 面板支持 onInterimTranscript 回调，但 DashScope 文件适配器明确没有流式中间结果，stop 后返回一次最终文本；不能将组件支持的回调直接认定为所有服务商已经提供流式 ASR。

此前文档使用“持续语音会话”泛指剩余语音范围，容易被误读为已有自动循环通话待迁移。本核查将既有 Web 等价能力与自动循环通话扩展区分开；自动循环并未因基础组件完成而实现，也不能据此宣布完整语音目标达成。后续优先补来源元数据与真实设备/供应商验证，最新回复入口和中间转写按实际合同补齐；自动循环通话需单独明确交互及执行边界。

## 语音来源随消息保存（2026-09-27）

转写合同返回 VoiceTranscript（正文、实际供应商与模型、可选语言）。VoiceInputOrigin 在确认加入草稿时绑定角色，发送前与正文一起捕获到 PendingSend；重试保留原始来源，旧发送回执不能清掉相同文字的新录音，清空草稿则移除来源。多次追加录音时记录最近一次被接受的录音来源，不声称逐段溯源。

Composition 直接将 inputMode=voice、voiceSessionId、实际 asrProvider/asrModel 交给 SubmitTurnCommand，使用 Core 既有消息元数据持久化流程。Core 当前 ASR 返回值没有语言检测结果，因此不填 language；未提供的字段均省略，不照抄 Web 的 browser/web-speech/zh-CN 常量。

验证：83 项逻辑测试、219 项原生窗口检查、3 项真实 Core 集成测试通过，含实际 ChatMessages.MetadataJson 回读断言。日志为 temp/native-voice-origin-state.log、temp/native-voice-origin-ui.log、temp/native-voice-origin-core-recheck.log。首次 Core 运行在此前的供应商配置测试发生 File.Replace IOException（temp/native-voice-origin-core.log）；同一构建无改动复验通过，文件占用根因未确认、未在本次修复，不能据此声称并发配置写入问题关闭。没有使用真实麦克风、真实供应商或 D:\data；完整目标仍未完成。
