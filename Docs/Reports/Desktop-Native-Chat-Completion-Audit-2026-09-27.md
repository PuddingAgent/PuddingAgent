# 原生聊天目标完成度核对

范围：用户要求的角色优先 WinUI 3 聊天组件、布局，以及参照原 Web 的流式思考/工具活动。依据当前源码及独立窗口验证；不是完整 Desktop 迁移或真实模型产品验收。目标保持进行中。

| 要求 | 当前证据 | 判定与剩余门禁 |
|---|---|---|
| 原生组件、编译期依赖边界 | PuddingChat.WinUI.csproj 仅引用 PuddingChat，并通过 EnforceChatViewBoundary 拒绝其他项目引用 | 已实现；组件窗口构建通过。Core 装配与产品发布需另验 |
| 角色是一等公民 | RoleAvatarCard、ChatWorkspace、ChatSelection，按 RoleKey 保存草稿/选择/阅读位置 | 已接入，窗口 fixture 覆盖角色切换与晚到响应；真实角色日常使用待验 |
| 同进程调用、客户端免登录 | Composition/InProcessChatClient 直接 DI scope 调用应用服务，固定 LocalDesktopIdentity；没有聊天 HTTP 适配 | 源码已接入；既有 NativeChatIntegrationTests 有 HTTP 探针，本轮未重复运行 Core 集成 |
| 流式正文、真实思考与工具链 | ConversationActivity、TurnFlow、TurnContentView、ActivityContentView；canonical 顺序、调用 ID 配对、默认展开思考、懒加载工具内容 | 已实现并有组件测试；真实模型长会话及取消仍需验收 |
| 稳定阅读与长记录 | VirtualTranscript、FlowWindow、ReadingBookmark、历史直接调用端口 | 窗口覆盖消息虚拟化、渐进展开、阅读锚点；单 Turn 全展开、巨型文本与长期内存指标待验 |
| 消息/输入布局 | MessageCard、ChatComposer，用户消息靠右、900 DIP 阅读宽度、窄输入栏两行 | 窗口覆盖 320/360/900 DIP 输入区；整窗主题、DPI、字体缩放、键盘及 IME 尚缺完整矩阵 |
| 原 Web 富文本能力 | MarkdownView 原生 GFM、CodeBlockView、ImageAttachmentView | 代码复制/换行、列表表格链接及受控图片已具备；语法高亮、公式、Markdown 生成图片资源解析尚未实现 |
| 图片与一般上下文附件 | 原生选图/剪贴板/拖放→Core Artifact→typed input parts | 图片合同与解码已测；系统选择器实际操作、真实视觉调用待验，一般文件上下文入口未接入 |
| 审批卡与真实工具恢复 | ApprovalCard、PuddingApproval、SqliteApprovalStore 独立组件；Runtime 保留准入状态 | 未完成：请求生产者、持久暂停/恢复、Core 决定端口及产品待审批区域未接线。不能用独立卡片测试替代闭环 |
| 子代理与其他聊天富交互 | TurnFlow 展示委派生命周期/有界摘要；SubAgentInspector 已通过独立窗口验证 | 检查器 Core 归档适配与产品入口未接入，语音聊天尚未迁移；独立组件不等同生产功能 |
| 用户实际产品效果 | 独立 WinUI harness 与历史部署记录 | 本轮没有发布/替换运行中 Desktop；不能凭组件通过宣称当前产品已加载新代码 |

## 当前切片：展开活动内容保持

ActivityContentView 为正文、输入、输出保留独立槽位，变化时仅更新对应 MarkdownView；状态更新不重新解析未变文本。思考、工具和委派沿用同一实现，折叠后仍释放内容。修复原先 RenderDisclosure 每次重建内部 StackPanel/MarkdownView、导致嵌套代码换行偏好和阅读状态丢失的问题。

验证：BCL 50/50、原生窗口 108 项，零构建警告/错误，日志 temp/native-activity-content-final.log。新增 6 项覆盖真实窗口中的容器/输入输出身份保持、嵌套代码偏好与文本、终态更新、移除失效输出、流式思考更新。没有修改 Core、运行数据或重启产品。

## 后续收口顺序

1. 优先完成仍缺失的用户能力：原生富文本剩余项、上下文入口与子代理检查；每项采用独立组件→组件验收→直接调用接线。
2. 按原生审批设计 A2/A3 完成 Core 真正暂停/恢复，再接审批卡；不通过 UI 重发整轮伪造续行。
3. 运行当前源码的完整 Core 组合测试和独立产品构建；随后执行明确新构建的产品视觉/交互矩阵。
4. 最后核对真实模型长会话、取消、流式性能与产品生命周期证据，再判断目标是否完成。缺失证据保持未完成，不用不断追加局部测试替代用户可见能力。
