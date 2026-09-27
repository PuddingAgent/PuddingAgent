# 原生聊天文本文件上下文

## 基线与设计

核对 `PuddingPlatformAdmin/src/pages/chat/components/ComposerActionMenu.tsx`：旧 Web 的一般附件按钮仍 disabled / 即将开放；图片是独立且已实现的能力。Core `ConversationContentValidator` 只接受 text/image。因此本项是新能力，不把一般附件标为已有 Web 接口迁移，也不新增 HTTP 或伪造 input_file 支持。

当前交付文本/源代码文件：用户选择文件 → BCL 读取不可变快照 → 当前角色草稿附件列表 → 预览/移除 → PendingSend 冻结快照 → Composition 合成用户 text 内容块 → 既有 ISubmitTurnHandler。图片继续使用 Artifact 管线。文件路径仅作为来源标签，发送与重试绝不重新读取路径。文件正文与用户草稿一起进入消息历史，未赋予更高指令权限；Markdown 围栏只是显示边界，不是防提示注入的安全边界。

## 原生交互与约束

- 输入框“文件”按钮使用系统多选器；文件卡片显示名称，点击可选择/阅读导入时的全文，提示包含来源路径与字节数，可移除。文件内容会随消息发给所选模型。
- UTF-8 严格解码、UTF-8 BOM、带 BOM 的 UTF-16 LE/BE；保留换行与空白。不猜测 GBK，不用替换字符掩盖解码错误。控制字符（二进制、非换行/制表符）及 UTF-32明确报错。
- 单文件 256 KiB、最多 8 文件、总计 512 KiB；读取最多上限加一字节，不先无限读入再检查。组合消息（正文、来源标签、围栏、快照）不得超过 Core 的 100,000 字符上限，不截断或拆分绕过限制。
- 批量导入全部成功才写入草稿。后台文件读取捕获角色，切换角色不会把晚到附件写入另一角色；窗口释放取消读取。发送前冻结附件数组，回执只移除这次已受理的附件，保留后来添加的内容与新草稿。
- 不创建附件数据库、不复制文件到 DataRoot、不在组件内执行模型调用。未发送快照当前仅驻留内存，关闭窗口不恢复。

## 边界与交付顺序

1. `PuddingChat/TextFileContext.cs`：BCL 读取/限制/不可变记录/消息合成；`ChatSelection`：角色草稿与回执清理；`PendingSend` 保持原始草稿及快照。
2. `PuddingChat.WinUI/ChatComposer` 与 `ChatWorkspace`：选择、导入、预览和移除。先完成独立组件逻辑和窗口验证。
3. `PuddingDesktop.Composition/InProcessChatClient.SendAsync`：取 `SubmittedText`，直接提交原有 Core text 内容块，Core 的角色、会话、准入、幂等处理保持不变。组件通过后才接入。

## 当前验证与未完成项

组件验证：57 项逻辑测试、132 项真实 WinUI 窗口检查通过，最终构建零警告/错误；记录 `temp/native-text-files-final.log`。覆盖编码、大小、取消、源文件变化/删除、围栏、批量限制、角色隔离、重试快照、回执保留新附件以及原生预览/移除/发送清理。初次构建遇到其他 XAML 编译进程短暂文件占用，自动重试恢复，未停止其他进程。

Core 集成测试 3/3 通过（`temp/native-text-files-core.log`，Core 构建有既存警告）：真实文件快照→删除源文件→直接提交/重试→读回消息与图片并存，校验字符上限与 Core 一致；既有零 HTTP 探针覆盖此路径。没有读取生产数据或调用真实付费模型。

文件拖放/粘贴入口现已接入（见下节）。仍未完成：PDF/Office/二进制文档提取、附件独立持久化与历史卡片、系统选择器及真实资源管理器拖放人工交互、真实模型文件理解和最终 Desktop 发布验收。当前不宣称一般格式附件或完整聊天迁移已完成。

## 混合文件拖放与粘贴

ChatComposer 将 StorageItems 的拖放与 Ctrl+V 交给 ChatWorkspace.FileTransfer；普通文字保留 TextBox 粘贴，位图与显式“粘贴图片”保留原图片路径。工作区在请求延迟 DataPackage provider 之前捕获角色并占用导入状态，先校验总数量、每类数量、文本解码与大小，再经原 Core 图片端口导入图片，全部成功后才写入原角色草稿；文件夹和无本地路径项明确拒绝，不递归读取。

批次原子性只针对草稿展示。若部分图片已进入 Core Artifact 存储而后续导入失败，草稿仍不添加这批附件，但本入口不会跨越 Core 所有权自行删除已写 Artifact；其生命周期仍归 Core。没有新增 HTTP 或持久化文件接口。
