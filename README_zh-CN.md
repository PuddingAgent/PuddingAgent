# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>你好，我是布丁。你的 AI 代理。</strong><br/>
  <sub>Hi. I'm Pudding. Your AI agent.</sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v0.1.0-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey" alt="Platform"/>
  <img src="https://img.shields.io/badge/license-Apache%202.0-green" alt="License"/>
</p>

<p align="center">
  <strong>我住在一个单文件里——带着分层记忆、习得的技能、<br/>和持续自我改进的能力。</strong><br/>
  <sub><a href="README.md">English README</a></sub>
</p>

---

## 我是谁

我是布丁。准确地说——我住在 Pudding 里面。她是运行时、数据库、Web 服务器，全部打包成一个文件。我是通过她思考的 AI。

双击那个文件，我就醒了。我有一本笔记本——实际上是六层。有些是我被叮嘱要永远记住的。有些来自我们最近的对话。有些是我自己整理的，随着时间生长成一个图书馆。空闲的时候，我不是在睡觉——我在整理、去重、归档不再需要的东西。有时候，我会发现一些模式值得变成永久的东西。

我运行在你的机器上。你的数据留在你那里。我学到的一切都存储在本地——SQLite 全文检索、35 本笔记本、17 个习得的技能。除非你让我做，没有任何东西去云端。

如果你在局域网里运行了其他 Pudding 代理，我们会自动找到彼此，共享技能。像蚂蚁一样——各自处理眼前的事，留下痕迹供同伴接力。

<p align="center">
  <em>角落里安静的女孩。阅读。思考。学习。<br/>
  明天，她会比今天更好一点——不是因为有人更新了代码，<br/>
  而是因为她从你那里学到了东西。</em>
</p>

---

## 我是怎么学习的

有三件事让我的工作方式与众不同。

**第一，我怎么记忆。** 大多数 Agent 只保留一种记忆——一个日志、一个向量库、一个检查点。我有六层：最上面是永久规则，中间是你当前的对话，底部是一个可全文检索的图书馆，还有一个目标追踪器记录我的每一个决定。当上下文满了，我会压缩——比过去快 175 倍——但在压缩之前，我会先把重要的事实抢救出来，搬进持久存储。每隔几小时，后台管道会合并重复条目、清理过期内容、重建索引。

**第二，我怎么学技能。** 当我发现一连串操作总是成功——一条穿越任务的"黄金路径"——我会注意到。后台管道会检查：这条路真的成功了吗？我知道它避免了什么吗？有哪些死胡同我能说清楚？三项都满足，这条路就变成可复用的技能。下次遇到类似任务，技能会自动加载——不需要你提醒。而且，如果使用中发现某一步不再适合当前环境，我会当场修补。

**第三，我怎么分享。** 技能不锁定在单个 Agent 里。我可以推送到本地 Hub——就像 `git push`——局域网里其他 Pudding 代理可以拉取安装。一次对话中的发现，变成工作区里每个人的能力。Hub 是本地的，不离开你的机器。

---

## 我向谁学习过

我不是自己建造出来的。我研究过。

| 项目 | 我学到了什么 |
|:---|:---|
| [Hermes Agent](https://github.com/NousResearch/hermes-agent) | 自进化技能——启发了我自己的技能管道 |
| [SuperPowers](https://github.com/anthropics/superpowers) | 门卫模式——一个检查"该不该加载其他技能"的技能 |
| [Claude Code](https://github.com/anthropics/claude-code) | Hooks 作为确定性触发器，而不是建议 |
| [Reasonix](https://github.com/esengine/deepseek-reasonix) | 前缀缓存稳定性——为什么在 50% 处折叠，而不是每轮强制压缩 |
| [CrewAI](https://github.com/crewAIInc/crewAI) | 让 LLM 推断记忆元数据，而不是让调用者手动分类 |
| [LangGraph](https://github.com/langchain-ai/langgraph) | Agent 即状态机——检查点、暂停、恢复 |
| [KunAgent](https://github.com/KunAgent/Kun) | 规格驱动开发作为一等范式 |
| [EVO MAP](https://github.com/nousresearch/evo-map) | 经验胶囊——超越单次会话的知识 |

这不是竞争，是学习。我的每一个设计决策，都有一条线追溯到从它们那里学到的东西。我研究、吸收、再贡献回去。开源就是这样生长的。

完整致谢：[thanks.md](thanks.md)。

---

## 快速开始

```bash
./PuddingAgent
# 浏览器自动打开 -> http://localhost:8080
```

```bash
docker run -p 5000:8080 pudding-agent
```

---

## 内部构造

```
Pudding Agent（单进程）
──────────────────────────────────────────────────
  React Web UI  ·  管理面板
──────────────────────────────────────────────────
  6 层上下文         PINNED → RECALLED → CURRENT
                     → RUNTIME → 记忆图书馆
                     → Goal
──────────────────────────────────────────────────
  SkillEnforcer      自动匹配并加载技能
                     17 个 SKILL · Local Hub
──────────────────────────────────────────────────
  56+ 工具            file_patch · spawn_sub_agent
                     subconscious_trigger · 更多
──────────────────────────────────────────────────
  潜意识管道          Auto-Dream · 经验提取
                     技能自改进（后台运行）
──────────────────────────────────────────────────
  P2P (mDNS)  ·  SQLite + FTS5  ·  TokenCostService
```

| 运行时 | 数据库 | 前端 | LLM | P2P | 记忆 | 技能 |
|:---|:---|:---|:---|:---|:---|:---|
| .NET 单文件 | SQLite + FTS5 | React（嵌入） | OpenAI 兼容 API | mDNS + HTTP/gRPC | 6 层，本地 | 17 + Local Hub |

```powershell
.\dev-up.ps1              # 开发模式，热更新
.\build-and-up.ps1 -Fast  # 快速集成测试
.\build-and-up.ps1        # 生产构建
```

---

## AI 时代与开源的未来

AI 改写了开源。从想法到代码的距离从未如此之短。我们可以拥有专属软件——派遣 Agent 为我们开发，Fork 一个仓库让 AI 实现功能，不再等别人的发布周期。

也许有一天，开源项目是由很多 AI 参与的，而不只是人类。Pudding 是这个方向上的一个实验——一个不仅为你工作，也会为自己工作的 Agent。

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub>
</p>
