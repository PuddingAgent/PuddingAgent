# 原生聊天目标完成度核对

范围：用户要求的角色优先 WinUI 3 聊天组件、布局，以及参照原 Web 的流式思考/工具活动。依据当前源码及独立窗口验证；不是完整 Desktop 迁移或真实模型产品验收。目标保持进行中。

| 要求 | 当前证据 | 判定与剩余门禁 |
|---|---|---|
| 原生组件、编译期依赖边界 | PuddingChat.WinUI.csproj 仅引用 PuddingChat，并通过 EnforceChatViewBoundary 拒绝其他项目引用 | 已实现；组件窗口与完整 Desktop 构建通过，隔离产品进程已验证聊天挂载。正式产品发布需另验 |
| 角色是一等公民 | RoleAvatarCard、ChatWorkspace、ChatSelection，按 RoleKey 保存草稿/选择/阅读位置 | 已接入，窗口 fixture 覆盖角色切换与晚到响应；真实角色日常使用待验 |
| 同进程调用、客户端免登录 | Composition/InProcessChatClient 直接 DI scope 调用应用服务，固定 LocalDesktopIdentity；没有聊天 HTTP 适配 | 源码已接入；NativeChatIntegrationTests 有 HTTP 探针，最近 Core ASR 接入的 3 项集成测试通过（temp/native-transcription-core.log） |
| 流式正文、真实思考与工具链 | ConversationActivity、TurnFlow、TurnContentView、ActivityContentView；canonical 顺序、调用 ID 配对、默认展开思考、懒加载工具内容 | 已实现并有组件测试；真实模型长会话及取消仍需验收 |
| 稳定阅读与长记录 | VirtualTranscript、FlowWindow、ReadingBookmark、历史直接调用端口 | 窗口覆盖消息虚拟化、渐进展开、阅读锚点；单 Turn 全展开、巨型文本与长期内存指标待验 |
| 消息/输入布局 | MessageCard、ChatComposer，用户消息靠右、900 DIP 阅读宽度、窄输入栏两行；ChatWorkspace 在窄布局或宿主折叠侧栏时提供原生角色 Flyout | 57 逻辑/139 窗口检查通过，覆盖 320 DIP 整页消息/输入宽度、角色入口、反复收放、草稿和侧栏偏好恢复。整窗主题、DPI、字体缩放、键盘及 IME 尚缺完整矩阵 |
| 原 Web 富文本能力 | MarkdownView 原生 GFM、CodeBlockView 通过 ColorCode 渲染原生语法高亮、ImageAttachmentView、MathFormulaView、MarkdownImageContext | 原生行内/块公式与生成图片解析已接入，60 逻辑/171 窗口检查通过；图片经当前工作空间 Core 资源端口读取，涵盖 image 围栏、Markdown 图片、流式复用与回收重载。外部 HTTP(S) 图片已接入点击展开的原生预览（79 逻辑/218 窗口检查）；公式使用预发行库，不承诺完整 KaTeX 等价或完整 DPI/无障碍验收。长代码不着色但保留全文，高对比度实时通知受宿主能力限制 |
| 图片与一般上下文附件 | 图片：选图/剪贴板/拖放→Core Artifact；文本：选择器或文件拖放/粘贴→不可变快照→角色草稿→既有 Core text 提交 | 文本/源代码预览、移除、发送、源文件删除后重试和混合文件传入已接通；57 逻辑/145 窗口、既有 3 Core 测试通过。原 Web 的一般附件本就未实现；PDF/Office 提取、历史独立附件卡、真实模型及系统选择器/资源管理器拖放人工验收仍缺失 |
| 审批卡与真实工具恢复 | ApprovalCard、PuddingApproval、SqliteApprovalStore 独立组件；Runtime 保留准入状态 | 未完成：请求生产者、持久暂停/恢复、Core 决定端口及产品待审批区域未接线。不能用独立卡片测试替代闭环 |
| 子代理与其他聊天富交互 | TurnFlow 保留精确 RunId；原生检查器经 Composition 读取 Core 归档，委派卡入口已接入；窗口 118 项与 Core 3/3 隔离验证 | 真实模型委派交互、超大归档性能待验；基础录音转写/消息朗读及语音来源元数据已原生接入，真实设备/供应商验收待补 |
| 用户实际产品效果 | 独立 WinUI harness；新构建的 PuddingDesktop.exe + Core DLL 隔离 smoke，PID 30028，真实控件 IsLoaded/Visible、角色/附件草稿及重启后重新装配通过 | 已运行测试产品进程并正常退出、释放租约；未替换用户数据目录上的 Desktop。真实模型聊天、完整视觉与 DPI/IME 矩阵仍需验收 |

## 当前切片：展开活动内容保持

ActivityContentView 为正文、输入、输出保留独立槽位，变化时仅更新对应 MarkdownView；状态更新不重新解析未变文本。思考、工具和委派沿用同一实现，折叠后仍释放内容。修复原先 RenderDisclosure 每次重建内部 StackPanel/MarkdownView、导致嵌套代码换行偏好和阅读状态丢失的问题。

验证：BCL 50/50、原生窗口 108 项，零构建警告/错误，日志 temp/native-activity-content-final.log。新增 6 项覆盖真实窗口中的容器/输入输出身份保持、嵌套代码偏好与文本、终态更新、移除失效输出、流式思考更新。没有修改 Core、运行数据或重启产品。

## 后续收口顺序

1. 优先完成仍缺失的用户能力：原生富文本剩余项、上下文入口与子代理检查；每项采用独立组件→组件验收→直接调用接线。
2. 按原生审批设计 A2/A3 完成 Core 真正暂停/恢复，再接审批卡；不通过 UI 重发整轮伪造续行。
3. 运行当前源码的完整 Core 组合测试和独立产品构建；随后执行明确新构建的产品视觉/交互矩阵。
4. 最后核对真实模型长会话、取消、流式性能与产品生命周期证据，再判断目标是否完成。缺失证据保持未完成，不用不断追加局部测试替代用户可见能力。

## 最新视觉发现（2026-09-27）

完整产品隔离生命周期 PID 40964 再次通过，构建零错误/144 警告。组件 60 逻辑/172 窗口检查通过，新增 VisualPreview 可重现宽/窄/深色外观；空附件/关闭错误提示占位已修正。**行内公式的组合视觉未通过：截图仍显示 LaTeX 原文，控件却报告 Rendered=true。** 因此公式目前只能认定有解析/位图/组件实现，尚不能认定完整聊天中的正确视觉显示；后续优先定位这一差异。材质、DPI/IME、真实模型和审批恢复等原有门禁不变。

## 行内公式视觉问题关闭（2026-09-27）

上节组合显示问题已修复：同主题渲染去重、固定内容树、布局结束后确认真实卸载再清理，避免 InlineUIContainer 的短暂卸载/重载不断重置图片。60 逻辑/179 窗口检查通过，含完整消息在宽/窄/深色下图片可见、原文折叠与跨帧稳定，且已检查截图。此证据关闭该特定显示问题；全量 TeX、DPI/辅助技术、材质、真实模型、语音与审批恢复等门禁仍未完成。

## 语音基础进度（2026-09-27）

SpeechPlaybackSession 与消息/音频合同完成独立 BCL 验证，逻辑测试总数 67；详情见 Desktop-Native-Voice-2026-09-27.md。真实播放器、朗读按钮、Core 装配、录音/ASR、持续语音会话均未接入，语音条目仍为未完成。

## 语音原生组件门禁（2026-09-27）

NativeSpeechAudioPlayer 与 SpeechPlaybackButton 已独立实现并通过静音真实媒体/原生窗口检查，67 逻辑/188 窗口检查通过；已修复 Stop 取消回调重入时状态发布顺序。下一步接产品消息卡片和 Core 合成端口；当前仍不能宣称产品朗读或完整语音会话可用。范围见原生语音方案。

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

## 远程 Markdown 图片原生预览（2026-09-27）

RemoteImageReference 只接受不含用户信息的 HTTP(S) 地址；IRemoteImageSource 与 RemoteImageData 是独立合同。WinUI RemoteImageSource 采用不携带 Cookie/默认凭据的独立 HttpClient，最多 5 次重定向且逐跳校验地址，15 秒总超时、8 MiB 下载上限；没有 Content-Length 时也逐块计数。只接受 PNG/JPEG/WebP/GIF/BMP，WIC 实际解码并限定 6400 万源像素、1280 最大预览边长。默认仅展示来源主机与展开入口，明确展开才发请求，不把远程 URL 当作 Core Artifact 或本地路径。

RemoteImageView 在收起/真实卸载时取消并释放图片，迟到数据不再展示；InlineUIContainer 的短暂布局卸载在队列中确认后再处理。MarkdownImageContext 接通消息正文及思考/工具等既有 Markdown 内容，流式追加保留相同图片控件与已解码内容。失败可收起后重试；网络请求是外部图片获取，不是 Desktop/Core HTTP。

79 逻辑/218 窗口检查通过，零构建警告/错误，日志 temp/native-remote-images.log。新增地址规则、真实 WinRT 图片解码、无自动请求、显式展开、声明长度/无长度超限、凭据重定向拒绝、取消及流式控件复用检查。HTTP 使用替身 Handler，不依赖外网；完整 Desktop 部署与真实网站网络表现不在本轮证据范围内。

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

## 输入快捷键与组词保护（2026-09-27）

ChatComposer 接入 TextBox.TextCompositionStarted/Ended，在组词期间不发出发送请求；Ctrl+Enter 只接受无 Shift/Alt 的组合，长按自动重复不再次发送。发送不可用时消费该快捷键，避免等待回执时意外插入换行；普通 Enter 保持原生换行行为。卸载/禁用编辑器清除组词标记。

83 项逻辑、224 项原生窗口检查通过，组件零警告/错误，日志 temp/native-composer-keyboard.log。新增五项检查在真实 WinUI 控件上调用内部键盘判定与组词状态入口，覆盖组合键、组词、重复、禁用发送及禁用编辑器；没有注入真实输入法候选词或系统键盘事件。因此这是发送保护的组件证据，尚不能关闭完整 IME/辅助技术验收门禁。

## 角色导航自动化语义（2026-09-27）

RoleAvatarCard 与实际 ListView 项的自动化名称现含角色、状态及未读数，描述作为 HelpText；状态刷新同步更新已实现容器，容器回收清除旧标签。搜索框和列表提供明确名称。冻结/停用角色仍显示未读数，不再被状态优先级隐藏；相同状态不重复触发变更。

83 逻辑/228 原生窗口检查通过，零组件警告/错误，日志 temp/native-role-accessibility.log。新增检查读取实际角色容器的 AutomationPeer 名称/描述与更新结果，并从 ListView 的自动化子节点验证 SelectionItem 模式。首次检查错误地从容器 peer 获取选择模式，已改为 WinUI 数据项 peer。此证据验证自动化树语义，不代表已完成 Narrator 实际播报、键盘全路径或高对比度验收。

## 大型工具结果有界排版（2026-09-27）

ActivityContentView 对超过 32,768 个 UTF-16 代码单元的工具正文/输入/输出使用 PagedTextView，较短内容仍用既有 MarkdownView。分页原文明确标注，每页最多 16,385 个代码单元（普通上限 16,384，额外一个用于保留 surrogate pair/CRLF）。上一页/下一页与复制全文均为原生控件；页号在流式追加时保持，输出缩短后可恢复 Markdown。思考与正常回复的渲染策略不变。

TextPageWindow 独立测试先通过，再验证分页窗口，最后接工具活动组件。当前 85 逻辑/236 原生窗口检查通过，零组件构建警告/错误；日志 temp/native-text-pages-state.log、temp/native-text-pages-ui.log、temp/native-text-pages-integration.log。检查涵盖分页无损拼接、emoji/CRLF 边界、追加/替换/空值、真实原生翻页、300 DIP 按钮布局、全文复制数据与工具流式接线。

此变更限制工具输出的 Markdown 解析与可视排版规模，未裁剪 canonical 内容、未改变 Core 提交/事件协议。完整字符串仍在内存中，复制也会复制全文；不能据此宣称整体内存有界或关闭单 Turn 全展开、长期内存及真实模型验收门禁。

## 完整 Desktop 隔离生命周期复验（2026-09-27）

通过：PID 14388，Core DLL 加载、原生聊天挂载、角色/文本附件草稿、后台线程到 UI 回调、Core 停止/重启后新的聊天客户端与已保存角色加载、数据目录设置保存、进程退出及目录租约释放。构建 0 错误/63 个既有警告；执行 TestScripts/test-pudding-desktop-kernel.ps1。日志与报告保存在 temp/native-chat-product-isolated.log、temp/native-chat-product-isolated-report.json。

来源必须同时记录：隔离工作树基线 ee7277afbd31fde898a080fea14bd72a42b07726，GM 子模块 ae0ca44294e9aa4853ab1002b076036346a651a3，另加入 WorkspaceService.cs 和 WorkspaceApiController.cs 两份既有合同重命名（WorkspaceDraft→WorkspaceCreateDraft、WorkspaceEdit→WorkspaceMetaUpdate），补丁存 temp/native-chat-validation-contract.patch。基线中的 Desktop 适配器已使用新名称，Core 重命名尚未提交，因此基线原样构建失败。本次通过不能被引用为该提交原样可构建的证据。最初共享目录构建另遇未提交 RoleSummary 重名，未在本任务改动该组工作。

测试产物 PuddingChat.WinUI.dll SHA256：98D4ECF68B1F9CBB3FFEDA6A26C874C22BC7DB047D33267B28C5EE181B9FFF50。该测试证明当前聊天组件能随完整 Shell/Core 装配和释放，不执行真实模型、麦克风、Narrator 或所有新增交互；新增交互的证据仍来自 85 逻辑/236 窗口检查。完整目标保持进行中。
