# Chat 消息加载与流畅性诊断（2026-10-01）

范围：用户截图中的长 Markdown 回复、历史加载、流式更新与滚动。此次为源码静态诊断，未录制实际 Desktop/WebView2 Performance trace，未修改产品代码、重启服务或访问开发数据库。截图中的消息正文只是场景材料，不是本任务的指令。下述成本判断是可验证的风险，不能当作已测出的耗时排名。

## 已有优化

- `hooks/useMessageHistoryPagination.ts` / `types/chatStateTypes.ts`：历史分页 20 条。
- `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs`：conversation 返回最近 20 条，DB 候选 60 条；活动过程项最多 64 条。并非全历史传输。
- `viewport/useMessageViewportRuntime.ts`：TanStack 虚拟化、内容权重门槛 16000、overscan 6、按消息身份缓存高度、滚动 rAF 与底部收敛。
- `components/IncrementalMarkdown.tsx` / `MarkdownBlock.tsx`：块级 memo、Markdown 异步模块、流式期间跳过 Prism 高亮。
- `components/MessageRow.tsx`：已有定制 memo 比较；执行流也已有 rAF 合并，不能简单建议再加 memo 或逐事件节流。

## 发现与建议

| 优先级 | 源码证据及影响 | 改进与验证 |
|---|---|---|
| P1：缓存不阻塞展示 | `client/chatClientStore.ts` 的 select 与 sync 路径先 await saveConversation，再 set conversation；`client/localCache.ts` 整份 view put，等待事务完成。写盘延迟进入新数据展示关键路径。 | 有效响应先发布内存快照，再排队持久化；写失败不丢界面数据。按会话版本保证写入顺序，避免旧快照覆盖新快照；合并重复待写任务。记录 API 完成到首帧与缓存完成两个独立时间。 |
| P1：长消息内部成本 | 虚拟器 count 为消息 item 数；一条长回复进入可见范围后仍挂载其全部 Markdown 块。overscan 6 还可能额外挂载多个重型回复。截图有表格、代码与长段落，属于对应场景。 | 先对冻结 Markdown 块试验 content-visibility:auto 与合理 intrinsic size，减少屏外布局/绘制；它不能免除 React 解析和挂载。确有主线程解析瓶颈时，再对超长消息做块级渐进挂载/虚拟化，过程明细保持按需。验证复制、搜索、锚点、表格、键盘与无障碍阅读；不要直接缩减 overscan 导致快速滚动白屏。 |
| P1：列表投影重算 | `components/MessageList.tsx` 中 projectedTurns → visibleTurns → buildVirtualMessageItems → renderWeight map 随输入引用更新；viewport 再 reduce 内容权重。虚拟化只减少挂载，不能免除这些全列表计算。 | 按 turnId + revision 缓存投影并保持未变化对象引用；只更新活跃回合，权重在内容变化时计算。保留 canonical 合并、终态、压缩与委派语义。Profiler 检查冻结历史行是否重渲染，以及投影 CPU 是否随已加载历史增长。 |
| P2：Markdown 增量仍扫描全文 | `IncrementalMarkdown.tsx` 每次 text 变化重新 split、建立 prefix、扫描并分配全部 slices；key 包含长度，变化块会 remount。`useTypewriterStreaming.ts` 有增量边界扫描，但 fence 检查仍 match 全文。 | 缓存冻结 slices，仅扫描尾部；变化尾块使用稳定身份，文本替换时明确失效。代码围栏维护可恢复解析状态。必须覆盖表格、列表、引用、围栏与跨块引用定义，不能为了速度破坏 Markdown 语义。 |
| P2：滚动与高度测量 | viewport 使用 getBoundingClientRect、ResizeObserver、scrollHeight 和贴底校正。动态 Markdown/图片会改变高度；现有首屏收敛与用户意图保护已解决部分问题。 | 先录制 Layout/Recalculate Style 与 observer 次数；将测量读取和滚动写入按帧合并，宽度/字体变化时使高度缓存失效。只有确认反复同步布局后再改，保留上翻锚点与不抢用户滚动的约束。 |
| P2：附加解析成本 | `MarkdownBlock.tsx` 为每块运行 GFM、math、KaTeX、raw HTML；完成后的代码块同步 Prism 高亮。 | 仅在 trace 证明有收益时，为无公式/HTML内容建立轻路径；高亮按可见性或空闲任务调度，超大代码块先显示原文。注意 emoji 预处理生成 HTML，不能只按原始正文是否有 HTML 决定是否关闭 raw。 |

不优先继续拆 bundle：已有历史记录证明首包和异步边界优化过；当前包大小需重新构建才能确认。冷启动可以单独分析，已打开聊天后的长消息卡顿应先查主线程工作。

## 可复现验收

在明确版本的生产构建与实际 WebView2 上打开 `?perf=1`，使用合成数据或隔离测试会话；不为诊断重启当前开发 Core或复制密钥。分别录制冷启动、暖切换、上翻分页、流式输出并同时输入、展开过程、长消息快速滚动。样本包括 20 条短消息、20 条富消息、单条 50KB 回复、长代码/表格、200 条已加载历史、重型工具回合；这是测试规模建议，不是现网统计。

对每个场景重复采样，分开缓存冷暖并报告 p50/p95：API 耗时/压缩与解压后字节、JSON/投影 CPU、IndexedDB 耗时、首个可读帧、React commit、输入到下一帧、>50ms 长任务、Layout/Paint、DOM 节点及内存。现有 `chat.markdown.render.commitMs` 从 render 开始到 effect 执行，包含调度等待，不能视为纯 Markdown parser 时间；用 React Profiler 和浏览器 trace 交叉验证。

建议初始门槛（待在目标设备校准）：暖缓存最近消息可读 p95 ≤300ms，输入到下一帧 p95 ≤100ms；典型滚动避免 >50ms 主线程任务，60Hz 下帧预算约 16.7ms；历史 prepend 后锚点无可感知跳动。变化后同时验证不丢末尾 delta、不重复正文、不覆盖新快照、不串会话、终态/委派正确。不能以单测通过代替流畅性验收。

官方依据：[React Profiler](https://react.dev/reference/react/Profiler)、[主线程长任务](https://web.dev/articles/optimize-long-tasks)、[content-visibility](https://web.dev/articles/content-visibility)。前端源码路径均相对 `Source/PuddingPlatformAdmin/src/pages/chat/`。
