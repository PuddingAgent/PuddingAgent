# Task 21 - 潜意识/显意识双模型设计

状态：`partial`（v1 minimum implemented）  
优先级：`P0`  
最后更新：2026-02-20

## 1. 目标
在一个 Agent 角色模板中同时配置两个模型（服务商/模型），模拟人类的：
- 显意识（Conscious）：主任务执行、对外表达、工具调用决策。
- 潜意识（Subconscious）：记忆、回忆、压缩、监督、边界反思。

要求：两个心流都输出到 CLI 界面，但潜意识输出默认不显示。

## 2. 角色模板扩展
每个 Agent 模板从单模型升级为双模型，每个代理模板一个配置文件，放在agents目录里面：

```yaml
agent_template:
  role: builder
  conscious:
    provider: openai
    model: gpt-4o
  subconscious:
    provider: deepseek
    model: deepseek-chat
  subconscious_policy:
    visible: true
    verbosity: low
    budget_ratio: 0.15
```



我们还需要一个配置文件，用于配置全局的潜意识代理配置

潜意识LLM配置还允许配置缺省，缺省的状态下使用全局的潜意识代理LLM的配置，这样简化的配置。





### 选型原则
- 潜意识模型：速度快、价格低、稳定（优先吞吐）。
- 显意识模型：推理和生成质量优先（对外回答与关键决策）。

## 3. 潜意识职责定义
潜意识负责 4 类职责：

1. 记忆存储  
将会话关键事实写入：
- 全局记忆：`~/.pudding/memory/global.md`
- 项目记忆：`<project>/.pudding/memory/project.md`

2. 回忆召回  
- 短期：关键词检索（BM25/倒排索引）。
- 长期：向量索引召回（后续扩展）。

3. 摘要维护  
- 定期压缩历史记忆，去冗余、去陈旧。
- 维护“近期工作记忆 + 长期原则记忆”两层结构。

4. 监督反思  
- 检查是否越界（权限、架构约束、危险操作）。
- 对显意识决策做“内心审查”：是否偏离任务、是否引入风险。

## 4. 双心流执行模型
每轮用户输入处理流程：

1. 显意识接收用户输入，形成主计划。  
2. 潜意识并行读取上下文与记忆，生成：
- 回忆片段
- 风险提示
- 边界约束建议  
3. 显意识融合潜意识建议后执行。  
4. 潜意识在回合末生成“记忆写入候选”和“反思摘要”。

潜意识与显意识是异步的，不阻塞显意识的操作，潜意识的主要是输入是显意识的操作和心流，为了保证性能，这个过程并不需要立即触发，可以异步触发。
1. 在显意识的操作、心流、用户的输入都会记录到日志，日志一天为单位进行截断，默认保留全部的日志，这是原始的输入
2. 程序根据条件，在后台运行潜意识的LLM处理，输入日志，对日志内容进行理解、去掉重复、冗余的内容进行摘要和精选
3. 潜意识LLM将思考的结果写入到记忆文件作为显意识的记忆

### 记忆召回（同步和异步）：


潜意识LLM处理过程：
1. 显意识需要回忆（通过function call）或者回忆（或者同含义的关键词）触发
2. 潜意识LLM理解开始工作，输入最近1天的日志和5天记忆+关键词检索，通过潜意识LLM开始思考理解是否满足记忆召回的要求
3. 如果不满足，回到第2步骤，重新进行召回，但是这个过程需要设置一个条件防止无限循环，比如超时机制（不得超过120秒）+循环计数器（不超过5次）
4. 根据上一步的检索结果，将记忆召回的结果返回给显意识


记忆召回支持同步阻塞方式和异步方式：
1. 同步阻塞模式在记忆召回的时候，会阻塞显意识的LLM，直到潜意识返回结果。同步模式，直接返回结果。
2. 异步模式是后台运行潜意识LLM，显意识LLM会继续执行，当潜意识执行完成的时候，显意识LLM会继续执行（具体是同步模式还是异步模式，由显意识的LLM决定，默认是异步），当潜意识LLM工作完成的时候，通过HOOK，将潜意识LLM的思考结果，插入到对话流中。






## 5. CLI 展示规范
CLI 输出分两类心流：

- 显意识流：主输出流（默认展开）。
- 潜意识流：次级输出流（默认折叠，低饱和度标签），正常条件下默认不显示潜意识的LLM心流和结果，使用/debug指令开启pudding调试模式。



建议格式：
- `[C]` 显意识（Conscious）
- `[S]` 潜意识（Subconscious）

示例：
```text
[C] Planning: I will inspect AuthService and add async token generation.
[S] Recall: This project disallows direct DB calls in controllers.
[S] Guard: Proposed command touches path outside project scope, reject.
```

## 6. 成本与预算策略
- 为潜意识单独设预算比例：默认不超过总 token 的 15%-25%。
- 当预算紧张时，潜意识退化策略：
1. 关闭长摘要，仅做边界监督。
2. 降低输出频率（每 N 轮输出一次）。
3. 仅保留高风险提醒。

我们允许使用local代理比如ollama等作为处理的服务商，我们设计上潜意识使用非常廉价且快速的模型。




## 7. 数据结构建议
```csharp
public sealed record DualModelConfig(
    ModelRef Conscious,
    ModelRef Subconscious,
    double SubconsciousBudgetRatio);

public sealed record ModelRef(string ProviderId, string ModelId);
```

```csharp
public sealed record SubconsciousSignal(
    string Recall,
    string Risk,
    string BoundaryCheck,
    string MemoryWriteCandidate);
```

FIX:在设计上，我们复用现有的推理框架，以便更好的维护，如果现在的架构或者代理不够合理，我们需要重构和抽象这一点，我们需要评估。



## 8. 需要新增的核心组件
1. `SubconsciousEngine`：潜意识推理执行器。  
2. `MemoryStore`：全局/项目双层记忆存取。  
3. `MemoryIndexer`：关键词检索与向量召回接口。  
4. `ReflectionGuard`：反思监督与边界审查。  
5. `DualStreamRenderer`：CLI 双心流渲染器。  

## 9. 风险与约束
1. 双模型并发会增加复杂性，必须先做 budget 限流。  
2. 潜意识过度输出会干扰用户，默认低干扰。  
3. 潜意识建议不能直接越权执行，只能影响显意识决策。  

## 10. 分阶段落地
1. `v1`：双模型配置 + 潜意识文本检索记忆 + CLI 双标签输出。`已实现（最小版）`  
2. `v2`：向量召回 + 自动记忆压缩 + 预算自适应。  
3. `v3`：监督规则可配置（项目级 policy + hook 联动）。  
