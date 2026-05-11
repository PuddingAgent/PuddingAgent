# 记忆能力联调测试结果（2026-05-08）

## 配置
- Provider: `mimo` → https://token-plan-cn.xiaomimimo.com/v1
- 模型: `mimo-v2.5-pro` (默认), `mimo-v2.5`
- Workspace `default` / Agent `ad4a6a5f-660a-47a4-8794-0721507093e0`("Pudding 助手"/"布丁")
- Agent 已直接绑定 provider=mimo, model=mimo-v2.5-pro
- Global Template `general-assistant` 同步绑定

## 测试 1：写入记忆（新会话）
**用户**: 我最喜欢吃香蕉，请记住。
**SSE 事件**: metadata → context → 13×thinking → 多个 delta → step → done → usage（共 45 帧）
**回复**: "好的，我记住了！你最喜欢吃香蕉！🍌 ..."
**Usage**: prompt=657 / completion=89 / total=746

## 测试 2：跨会话召回（新会话）
**用户**: 你还记得我喜欢吃什么水果吗？
**SSE 事件**: 完整流式（68 帧）
**回复（关键）**:
> 抱歉，我不记得你喜欢吃什么水果。😔
> 从我的记忆来看：
> - 用户档案：未配置
> - 置顶记忆：无
> - 近期对话记录：无
> - 召回记忆：无

**结果**: ❌ 未召回 "香蕉"

## 根因分析
1. **LLM 链路完全正常**：流式、thinking、tool 调用、usage 统计均工作。
2. **记忆未被写入**：System Prompt 的 MEMORY 层显示 "召回记忆：无"，说明：
   - `MemoryEngine.RecallWithIntentAsync` 未返回任何内容
   - 第 1 轮对话内容并未被写入记忆库
3. **PuddingMemoryEngine 仅暴露 Recall/Search 接口**，无显式 `RememberAsync`。记忆写入预期由后台"潜意识 LLM" pipeline 异步处理（参见 task38-subconscious-memory-engine.md），该子系统当前未在主对话路径上运行。
4. **Agent 模板未配置 `MemoryLlmEndpoint/ApiKey/ModelId`**，潜意识子系统即便启用也缺乏 LLM 资源来生成记忆候选。

## 已知阻塞
- 跨会话事实型记忆（"用户喜欢吃香蕉"）依赖后台子意识 LLM 提取与索引，目前未跑通。
- 同会话内的对话历史（hydrate from DB）应当可以工作，但本次测试用 `forceNewSession=true` 故意分到新 sessionId 触发跨会话场景。
