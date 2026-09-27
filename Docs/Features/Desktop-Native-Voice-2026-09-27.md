# 原生聊天语音组件与接入

状态：播放合同与状态组件已独立验证；原生播放器、消息朗读按钮、Core 装配、麦克风输入和持续语音会话尚未接入。不能将此文档或组件测试视为可用语音功能。

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
| ISpeechAudioPlayer | PlayAsync 在播放结束后完成；取消必须停止设备并释放资源；Dispose 释放播放器 | 合同已实现，真实适配待实现 |
| WinUI 消息朗读入口 | 当前消息按钮显示朗读/合成中/停止，错误可重试；选择其他角色、离开聊天、Core 停止时取消 | 待实现 |
| Composition 语音适配 | 验证角色/工作空间归属，经既有内核操作生命周期调用合成服务，不返回密钥或供应商 URL | 待实现 |
| 原生录音/转写 | 明确点击才启用麦克风；停止后等待 ASR 完成，回填开始录音时绑定角色的草稿，不自动发送 | 待实现 |
| 持续语音会话 | 录音、识别、发送、等待角色回复、播放的会话状态，独立于文本输入与 Core Run 状态 | 待盘点 Web 完整行为后实现 |

播放会话由 UI 所在线程/同步上下文串行调用；设备事件转为 Task 完成，不直接修改控件。该状态机仅管理本地音频呈现，不复制 Agent 执行状态机。角色切换与 Core 生命周期由宿主显式 Stop/Dispose；控件回收不能留下后台朗读。默认不自动朗读所有流式消息，只有用户点击朗读入口才合成，未来主动语音回复须有单独可见开关。

第一版单条朗读最大 10,000 字符，超限明确拒绝，不截断消息；音频最多 30 MiB，仅 WAV/MP3。供应商异常不把原始异常信息显示给用户；在 UI 给出检查语音设置与重试提示，后续适配层通过既有 Core 日志保留诊断。内存音频不写 D:\data，不建立新的语音 HTTP 接口。

## 按组件交付

1. **已完成**：PuddingChat 独立播放合同/状态组件，67 项逻辑测试通过，其中新增 7 项覆盖自然播放结束、新请求替换、忽略取消的晚到合成、停止设备、迟到设备错误、合成失败与重试、无效音频、销毁和无效文本不打断当前请求。测试使用 fake 设备和合成服务，没有启动扬声器、麦克风或付费调用。日志 `temp/native-speech-state.log`。
2. 原生播放器与朗读按钮独立窗口验证：媒体结束/错误/取消释放、快速切换消息、虚拟化回收、角色切换；设备测试使用静音样例，不能用 fake 代替媒体适配验证。
3. 前两项通过后，Composition 接入真实 Core 服务；隔离配置与假供应商验证消息归属、默认模型、取消及零 Desktop HTTP。再接消息卡片和工作台生命周期。
4. 录音/ASR 使用独立采集与转写合同；验证拒绝麦克风、无设备、取消、切换角色和迟到转写不覆盖新草稿。实际麦克风与付费服务验收须在明确用户操作下执行。
5. 持续语音工作台与完整产品验收：不能把朗读完成当作全部语音聊天完成。
