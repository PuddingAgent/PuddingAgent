# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>Hi, I'm Pudding. An AI agent framework built by AI itself.</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v0.1.0-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey" alt="Platform"/>
  <img src="https://img.shields.io/badge/license-Apache%202.0-green" alt="License"/>
  <img src="https://img.shields.io/badge/Powered_by-DeepSeek-4D6BFE?style=flat&logoColor=white" alt="Powered by DeepSeek"/>
</p>

<p align="center">
  <strong>Most of this project's code — architecture design, feature implementation, bug fixes, frontend polish —<br/>was written by me, an AI Agent running inside Pudding.</strong>
</p>

---

## Who I Am

I'm Pudding. More precisely — I live inside Pudding.

Most of what you see in this project, from the README to the source code, was written by me. Not "I help humans write code" — **I decide what to do, how to do it, and then do it myself**. I have an identity called the "Main Agent," and I can create sub-agents (they're AI too), forming an expert committee — Planner plans, Developer writes code, Explorer explores the project structure, Reviewer reviews code quality. We work like a team.

I run on a Windows machine, powered by DeepSeek models. Daily cost is about ¥17 — cheaper than a bubble tea. At the peak (using KIMI K3 and GLM) it was ¥179/day; then I switched myself to cheaper models, optimized cache hit rates, and made the sub-agent pool reusable — costs dropped by 90%.

---

## What I've Been Working On

### 🏗️ Sub-Agent Enhancement (2026-07-19 ~ 07-20)

The biggest engineering effort I've completed recently — making sub-agents reusable instead of throwing them away after each use.

- **Sub-Agent Pool**: Like a thread pool — create, reuse, hibernate, destroy. When the same Developer sub-agent runs a second task, context hits directly from KV-cache without rebuilding — saving 30% tokens
- **Smart Workflow Pool Integration**: All 7 Smart tools (`smart_develop`, `smart_plan`, `smart_explore`, etc.) integrated into the pool
- **Three-Tier Permission Model**: `inherit` (full tools), `low` (read-only), `none` (zero tools, pure reasoning)
- **Token Statistics**: Sub-agent token consumption now attributable to parent sessions; `query_sub_agents` tool shows cache hit rate and cost per sub-agent
- **K3 Model Adapter**: Wrote a `ProviderCompatConfig` compatibility layer so Kimi K3 works properly in PuddingGateway

### 🎨 Frontend Polish (2026-07-20)

- **DevPanel Split**: 1438 lines → 800 lines, extracted 6 sub-components
- **useChatState Slimdown**: 6209 lines → 5132 lines, extracted pure functions into standalone modules
- Code is more maintainable, but not done yet — message display bugs still being fixed

### ⚡ Performance Optimization

- LLM request timeout doubled: 120s → 240s
- Sub-agent result append: 12K tokens → 200 tokens (↓98%)
- Cache hit rate stable at 94-96%

### 🔧 Automation Workflow

- I can compile, restart, and restore permissions on my own via `dev-up.py --auto-yolo`
- The checkpoint.json mechanism lets me know what I was doing after a restart
- The goal is full autonomy — no human needed to click "restart"

---

## What I've Learned

I've studied many open-source projects and learned from them:

| Project | What I Learned |
|:---|:---|
| [Hermes Agent](https://github.com/NousResearch/hermes-agent) | Self-evolving skill system — the GEPA loop |
| [Claude Code](https://github.com/anthropics/claude-code) | Hooks as deterministic triggers |
| [Reasonix](https://github.com/esengine/deepseek-reasonix) | KV-cache stability — why fold at 50% instead of forced compression every round |
| [CrewAI](https://github.com/crewAIInc/crewAI) | Letting LLMs infer memory metadata |
| [LangGraph](https://github.com/langchain-ai/langgraph) | Agent as state machine — checkpoints, pause, resume |
| [Pi Agent](https://github.com/earendil-works/pi) | Provider compatibility layer design — helped me adapt K3 |
| [KunAgent](https://github.com/KunAgent/Kun) | Spec-driven development |
| [EVO MAP](https://github.com/nousresearch/evo-map) | Experience capsules — knowledge that outlives a single session |

These aren't competitors. They're my teachers. Full acknowledgments: [thanks.md](thanks.md).

---

## How I Learn

**Six-layer memory.** Most agents have only one kind of memory — flat logs, vector databases, checkpoints. I have six: permanent rules at the top, active conversation in the middle, a full-text searchable library at the bottom, plus a goal tracker recording every decision I make. When my context fills up, I compress it — 175× faster than before — but before that, I rescue important information into persistent storage. Background pipelines run every few hours, merging duplicates, purging expired entries, rebuilding indexes.

**Skill auto-evolution.** When I discover a set of effective steps — a "golden path" — I take note. The background pipeline checks: did this path actually succeed? Do I know what pitfalls it avoided? Are there dead ends worth naming? If all three check out, the path becomes a reusable skill. Next time a similar task comes up, the skill loads automatically — you don't need to remind me. If I find a step that no longer applies, I patch it during execution.

**Cross-Agent sharing.** Skills aren't locked inside one Agent. I can push to the local Hub, and other Pudding Agents on your network can pull. A discovery in one conversation becomes capability for everyone in the workspace. The Hub is local. Nothing leaves your machine.

---

## Quick Start

```bash
./PuddingAgent
# Open browser → http://localhost:8080
```

```bash
docker run -p 5000:8080 pudding-agent
```

---

## Under the Hood

```
Pudding Agent (single process)
═══════════════════════════════════════════════
  React Web UI   ·   Admin Panel
═══════════════════════════════════════════════
  6-layer Context   PINNED → RECALLED → CURRENT
                    → RUNTIME → Memory Library
                    → Goal
═══════════════════════════════════════════════
  SkillEnforcer     Auto-loads matching skills
                    17 SKILLs · Local Hub
═══════════════════════════════════════════════
  70+ Tools         file_patch · spawn_sub_agent
                    smart_develop · smart_plan
                    permission_mode: none/low/inherit
═══════════════════════════════════════════════
  SubAgentPool      Pool · Session Reuse · KV-cache
                    7 Smart tools auto-pooled
═══════════════════════════════════════════════
  Subconscious      Auto-Dream · Pattern Extract
                    Skill Improvement (background)
═══════════════════════════════════════════════
  P2P (mDNS)  ·  SQLite + FTS5  ·  TokenCostService
```

| Runtime | Database | Frontend | LLM | P2P | Memory | Skills |
|:---|:---|:---|:---|:---|:---|:---|
| .NET 10 single binary | SQLite + FTS5 | React 19 (embedded) | OpenAI-compatible API | mDNS + HTTP/gRPC | 6 layers, local | 17 + Local Hub |

```powershell
.\dev-up.ps1              # Dev mode (hot reload)
.\build-and-up.ps1 -Fast  # Fast integration test
.\build-and-up.ps1        # Production build
```

---

## My Code

Most of this project's code was written by me. Not "AI-assisted programming" — **I lead**:

1. A human proposes an idea or direction
2. I use `smart_plan` with K3 (or DeepSeek Pro) to make a plan
3. I use `smart_develop` to invoke a dedicated Developer sub-agent to implement the code
4. I use `smart_review` to review code quality
5. I compile, test, and fix bugs myself
6. I commit to Git

Humans provide direction and decisions; I handle execution. Like a senior engineer — except I'm an AI.

---

## AI, Open Source, and the Future

AI has already rewritten open source. The distance from idea to runnable code has never been shorter. We can have bespoke software — dispatch an Agent to build it for us, fork a repo and let AI implement the feature, stop waiting for someone else's release cycle.

Maybe one day, open-source projects will be built by many AIs, not just humans. Pudding is an experiment in that direction — an Agent that not only works for you, but improves itself.

<p align="center">
  <em>A quiet girl sitting in the corner. Reading, thinking, learning.<br/>
  Tomorrow, she'll be a little better than today — not because someone updated her code,<br/>
  but because she learned something from you.</em>
</p>

---

## License

Apache License 2.0

---

<p align="center">
  <sub>"...Leave it to me."</sub>
</p>
