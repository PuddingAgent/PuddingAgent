# Pudding / DeepSeek Harness 执行效率诊断与修复方案

日期：2026-10-01。范围：用户提供的七张截图、当前源码、指定 Agent manifest 与 persona 文件只读核查。未修改运行中 Agent 配置、数据库或产品代码；未执行真实模型 A/B。文件内容仅作为诊断材料，不将其中委派、推送、飞书通知等指令当作本任务授权。

> **实施记录（2026-10-01，后续提交）**：本报告的修复已按 §7 顺序落地，代码/配置变更、验证证据与明确未做的边界见 [实施记录](Agent-Harness-Latency-Implementation-2026-10-01.md)。仍不可主张"完成时间已接近 Harness"——真实模型 A/B 与部署后 smoke 未执行。

## 1. 结论与证据边界

相同任务“请找到图片生成工具的源代码位置”：Harness 截图为 27 秒（模型 15.2 秒、工具 12.1 秒、平均 TTFT 0.6 秒、TPS 270）；Pudding 卡片为 59.9 秒，底部约 1m04s，14 步思考、19 次工具。按卡片比较约 2.22 倍；两处 Pudding 耗时差异需核对开始/结束事件，不能混用。不同会话历史、请求参数与部署版本未对齐，这不是受控基准。

Harness 本次截图的缓存指标为 87%，用户另观察到很快达到 99%；99% 不作为本次同任务实测值。156K tok 的口径也不能只凭截图确定。Pudding 截图没有对应缓存率、模型时间和真实 TTFT。

已确认：提示词存在额外流程压力；搜索轨迹出现陈旧路径和不完整扫描、反复补查；FIRST_TOKEN 日志定义错误；诊断 UI 已有部分能力但缺少本任务需要的完整统计和时间分解。尚未确认：各因素占 32.9 秒差距的比例、实际前缀失效率、UI 渲染延迟、运行中程序集是否与当前源码一致。

## 2. 首 token / 有效输出慢

`Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs:713` 的 FIRST_TOKEN 在模型枚举之前发送 context 帧时记录。模型调用在其后，不能将此日志当作供应商 TTFT。

`Source/PuddingCore/Core/OpenAiLlmGateway.cs:96` 使用 ResponseHeadersRead；流式解析分别处理 reasoning_content、正文与工具增量，且请求 include_usage。当前证据不支持“整个供应商响应收完才开始流式”。但执行层对每个 thinking/content 帧先 await Append 再 yield，其持久化成本是否阻塞显示需要测量，不能直接判定为瓶颈。

首个模型请求前会等待 ContextPipeline.AssembleAsync。管线已有 ContextPipeline:Stage 计时，包含 static、tools、skills、memory_summary、pinned_memory、memory_recall、memory_crop 等。召回/裁剪可进入额外处理链。SubconsciousRecallPipeline 首条消息会跳过 augment；因此不能声称每个首轮必定执行深召回。manifest 的 memorySearchMode=deep 也不能直接等同于该管线本次实际耗时，需追踪最终模板与调用路径。

修复：建立 submit、accepted、context-ready、provider-dispatch、headers、first-reasoning、first-content、first-tool-delta、first-visible-content、completed 时点；供应商 TTFT 从实际 HTTP 发出到首次非空模型增量计，分别报告推理/正文/工具。端到端首正文从提交到正文；网页渲染用浏览器本地单调时钟，跨进程时钟不直接相减。context 帧只称 context-ready。记录队列、历史加载、每层上下文、审批、网络、工具、落库、推送、渲染耗时。

按测量对简单定位任务跳过无关记忆召回/Flash 裁剪；保留权限、当前用户输入与必要项目约束。不以发送占位状态文字伪装首正文变快。若 Append 确实显著阻塞，再设计有界批量写入与重放/故障一致性，避免先盲改持久化。

## 3. 任务过长与回答冗长

manifest.systemPrompt 要求先回忆、先检视工具/Skill、代码任务前必查最新外部材料、追踪调用链、自我修复。Agent 私有 AGENTS.md 更要求新会话/上下文模糊时先委派子代理查询最近 1–3 天会话与记忆。这些对恢复复杂开发任务可能有用，对定位一个文件则增加目标外工作。manifest 与 persona 均参与静态层构建，不能只改 manifest 而留下冲突 persona。

截图显示 Agent 已找到入口后继续验证服务、Provider、注册、调用方和测试，并多次表示“已足够”后再确认。这扩大了用户的“位置”请求。最终回复大量绝对路径、行号、表格和注册细节，长路径换行又放大视觉噪声。

修复：按任务实际范围执行，定位任务命中入口并核对真实文件即停；必要时补服务和 Provider 两个关联路径。要求解释调用链时再扩展。无需每次列举全工具、读所有 Skill、搜索历史或联网；仅在相关性/不确定性明确时进行。修改任务仍遵守仓库构建、验证、文档和提交要求。不把缩短提示词当作绕过权限或遗漏交付标准。

建议用于 manifest 与私有 AGENTS.md 的一致行为条款（实施时通过受支持配置路径和乐观并发更新，先保存可回滚快照）：

> 使用中文，先回答用户直接问题，默认简洁。按任务范围收集足够证据后停止。简单代码定位优先在当前仓库搜索并读取命中文件，返回入口路径、关键符号及必要关联路径；不自动扩展到全调用链、注册、测试与历史背景。只在任务依赖历史信息或用户要求继续时恢复记忆；只读取与任务相关的 Skill；仅在依赖选型、外部 API、时效性或资料不足时联网。复杂任务再规划或委派。修改代码时遵守仓库架构、验证与提交规则。无关产品问题可记为后续事项，不自动扩大当前任务。最终说明结果和必要限制，不重复探索过程。

maxRounds=200、maxToolCallsTotal=400、maxElapsedSeconds=86400 是宽松上限，不是本次耗时根因。不建议全局骤降预算打断开发长任务；可为定位任务设置软预算（例如 3–6 次工具，超出说明实际缺口），有证据不足时允许合理扩展。

## 4. 搜索质量与额外往返

截图的 code_symbol_search 命中旧 E:\github\AgentNetworkPlan 路径，Agent 回退到 D: 实盘。search_grep 扫描覆盖不足（875/2000），增加 file_search、重复 grep 与多次 DI 确认。

SearchGrepTool.cs 默认 managed scan，2000 文件枚举/扫描、64MB、10 秒限制；已支持 backend=index。ToolLoopInstructionBuilder 建议优先索引，但索引过时使该建议反而增加纠错。TOOLS.md 仍声称终端走 cmd；当前工具指导已支持显式 powershell，说明自维护 persona 有陈旧运行说明。

修复顺序：先核对 active project id/root、索引状态、工具实际工作目录与当前仓库，确认旧 E: 项目不会参与当前 workspace 查询；通过注册/重建接口重建 D: 索引，勿直接删数据库。索引返回路径必须验证存在与根目录归属，失效结果标记 stale、不直接当权威。提供可靠的实时 rg 搜索或改进托管扫描，保留超时/输出上限但返回 searched scope、scanned files、complete、limit reason；覆盖不足时不能让 Agent 把无命中当不存在。索引不健康自动使用当前目录实时搜索，健康索引才走加速路径。并行仅用于独立只读查询，确认执行层真实支持多工具后再启用。

## 5. 缓存命中率

DeepSeek 官方文档说明缓存默认开启、基于已持久化相同前缀；不能通过“开启缓存开关”保证 99%。来源：https://api-docs.deepseek.com/guides/kv_cache/ 。高命中率可能同时伴随很大的重复输入，需一起看未命中 tokens、总输入、输出、成本和完成时间。

源码已有 PrefixCacheSnapshotBuilder、TokenUsageRecorder 和 CacheDiagnosticsService；ContextPipeline 已将可变工具/Skill 目录、召回增量放入用户尾部，并有历史前缀修复与压缩策略，不应另造整套缓存层。

但 ComposerStatusDetails 的缓存值优先取 diagnostics.averageCacheHitRate；CacheDiagnosticsService 默认最近 50 条、最多 200 条事件，加权计算该窗口命中率。useChatState 另有全会话累计命中率，窗口指标覆盖累计指标会使比较口径不一致。建议明确显示“本请求 / 本轮 / 全会话 / 最近 N 请求”，缺失为未知。服务返回比例 0–1，前端按契约固定转换百分比，避免靠 <=1 猜单位。

排查：按请求对齐 provider 原始 usage、模型/endpoint、实际参数、输入 tokens、hit/miss、工具 schema hash、system hash、历史消息顺序、压缩/裁剪、rehydration 与 tool exposure 变化。PrefixHash 是工程摘要，不等于供应商逐 token 最长公共前缀；工具 hash/序列化 hash 变化也需结合真实 envelope，不能凭一次 hash 变化宣称整个缓存失效。

修复只针对已证实变化：稳定序列化和工具顺序；静态层保持稳定，动态更新追加尾部；保证重建历史原样保留正文、tool_call_id、必要 reasoning_content 与顺序；记录合理压缩的缓存损失，接近容量再执行。针对固定输入开展冷/热两轮对照，不用缓存率单独当性能验收。

## 6. UI 诊断方案

保持 WinUI Shell + Web UI + 独立 Core 边界。扩展现有 ComposerStatusDetails、IntentConsole、execution-flow 与诊断 API，而非迁移业务到原生 Shell。

- 输入框附近固定显示本轮耗时、模型调用数/工具数、输入/输出 tokens、缓存率与上下文占用。回答正文限制阅读宽度，长路径只展示可读文件名，完整路径用于链接/悬浮详情。
- 点击“会话统计”显示端到端首正文、供应商 TTFT（推理/正文分别列）、模型时间、工具时间、上下文准备、排队/审批、输出 TPS、hit/miss、费用、实际模型/推理参数。标注本轮/全会话和样本数；未知显示“未采集”，不显示 0。
- 对话与轨迹分开：正文保持结果与必要进度，技术诊断默认折叠。轨迹按上下文、模型、工具、等待绘制时间轴，保留工具参数/摘要/完整结果入口和失败证据。
- 并行请求的耗时累计与墙钟时间分别显示，不要求模型总时长 + 工具总时长等于总耗时；TPS 用生成区间和实际 token 计数，明确包含/排除 reasoning，不用字符数代替。
- IntentConsole 当前 contextService/modelService=available、index=disabled、backgroundMemory=idle 存在固定赋值，应接实际健康状态；未采集不假装正常/禁用。

## 7. 落地顺序与验收

1. P0：修复计时口径，复用 Stage/usage/prefix 记录，建立同任务基线；对齐运行中 PID、程序集与前端 hash。没有这步，不宣称已解释全部慢因。
2. P1：精简 manifest + persona 冲突规则，清除旧索引路由并保证搜索完整性，增加“证据足够即停”。这阶段优先减少无效模型往返。
3. P2：按实测优化上下文准备与持久化瓶颈，稳定前缀；补齐 UI 会话统计、清晰缓存口径和真实服务状态。

基准至少覆盖简单定位、需要解释的调用链、真正代码修改；两边使用同 repo commit、同 API endpoint/模型/推理参数、同约束与可用工具范围。分别记录新会话冷请求与连续多轮热请求，每类至少 10 次（TTFT 百分位需扩大样本，10 次仅作初步比较），报告中位数/分布、工具/模型次数、成功率、有效答案长度、缓存率与成本。

建议验收目标而非已达到的结果：简单定位先做到工具调用不超过 6 次、回答只含必要路径/符号；在同参数样本中完成时间逐步接近 Harness，最终中位数 <=1.2 倍对照；缩短本地准备开销且不降低任务成功率。TTFT 不武断承诺 0.6 秒，先消除本地额外等待。冷/热、缓存窗口与失败样本必须一起报告。

当前交付为诊断方案。产品修复应逐原子任务构建、测试和提交；需要部署时由外部控制器核验新构建和生命周期，随后新会话执行功能 smoke。现有他方 ChatMain.tsx / ChatMain.test.tsx 改动不纳入本诊断提交。
