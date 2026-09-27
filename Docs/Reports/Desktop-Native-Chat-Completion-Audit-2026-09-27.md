# 原生聊天目标完成度核对

范围：用户要求的角色优先 WinUI 3 聊天组件、布局，以及参照原 Web 的流式思考/工具活动。依据当前源码及独立窗口验证；不是完整 Desktop 迁移或真实模型产品验收。目标保持进行中。

| 要求 | 当前证据 | 判定与剩余门禁 |
|---|---|---|
| 原生组件、编译期依赖边界 | PuddingChat.WinUI.csproj 仅引用 PuddingChat，并通过 EnforceChatViewBoundary 拒绝其他项目引用 | 已实现；组件窗口与完整 Desktop 构建通过，隔离产品进程已验证聊天挂载。正式产品发布需另验 |
| 角色是一等公民 | RoleAvatarCard、ChatWorkspace、ChatSelection，按 RoleKey 保存草稿/选择/阅读位置 | 已接入，窗口 fixture 覆盖角色切换与晚到响应；真实角色日常使用待验 |
| 同进程调用、客户端免登录 | Composition/InProcessChatClient 直接 DI scope 调用应用服务，固定 LocalDesktopIdentity；没有聊天 HTTP 适配 | 源码已接入；既有 NativeChatIntegrationTests 有 HTTP 探针，本轮未重复运行 Core 集成 |
| 流式正文、真实思考与工具链 | ConversationActivity、TurnFlow、TurnContentView、ActivityContentView；canonical 顺序、调用 ID 配对、默认展开思考、懒加载工具内容 | 已实现并有组件测试；真实模型长会话及取消仍需验收 |
| 稳定阅读与长记录 | VirtualTranscript、FlowWindow、ReadingBookmark、历史直接调用端口 | 窗口覆盖消息虚拟化、渐进展开、阅读锚点；单 Turn 全展开、巨型文本与长期内存指标待验 |
| 消息/输入布局 | MessageCard、ChatComposer，用户消息靠右、900 DIP 阅读宽度、窄输入栏两行；ChatWorkspace 在窄布局或宿主折叠侧栏时提供原生角色 Flyout | 57 逻辑/139 窗口检查通过，覆盖 320 DIP 整页消息/输入宽度、角色入口、反复收放、草稿和侧栏偏好恢复。整窗主题、DPI、字体缩放、键盘及 IME 尚缺完整矩阵 |
| 原 Web 富文本能力 | MarkdownView 原生 GFM、CodeBlockView 通过 ColorCode 渲染原生语法高亮、ImageAttachmentView、MathFormulaView、MarkdownImageContext | 原生行内/块公式与生成图片解析已接入，60 逻辑/171 窗口检查通过；图片经当前工作空间 Core 资源端口读取，涵盖 image 围栏、Markdown 图片、流式复用与回收重载。外部远程图片仍显示文本；公式使用预发行库，不承诺完整 KaTeX 等价或完整 DPI/无障碍验收。长代码不着色但保留全文，高对比度实时通知受宿主能力限制 |
| 图片与一般上下文附件 | 图片：选图/剪贴板/拖放→Core Artifact；文本：选择器或文件拖放/粘贴→不可变快照→角色草稿→既有 Core text 提交 | 文本/源代码预览、移除、发送、源文件删除后重试和混合文件传入已接通；57 逻辑/145 窗口、既有 3 Core 测试通过。原 Web 的一般附件本就未实现；PDF/Office 提取、历史独立附件卡、真实模型及系统选择器/资源管理器拖放人工验收仍缺失 |
| 审批卡与真实工具恢复 | ApprovalCard、PuddingApproval、SqliteApprovalStore 独立组件；Runtime 保留准入状态 | 未完成：请求生产者、持久暂停/恢复、Core 决定端口及产品待审批区域未接线。不能用独立卡片测试替代闭环 |
| 子代理与其他聊天富交互 | TurnFlow 保留精确 RunId；原生检查器经 Composition 读取 Core 归档，委派卡入口已接入；窗口 118 项与 Core 3/3 隔离验证 | 真实模型委派交互、超大归档性能待验；语音聊天尚未迁移 |
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
